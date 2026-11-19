using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    /// <summary>只用于比较迁移前事件规则，不参与正式内容注册或执行。</summary>
    internal static class LegacyEventLuaFixture
    {
        private const string DefinitionVersion = "1.0.0";
        public static LuaScriptDefinition CreateDefinition(string cardId)
        {
            string fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "Assets/YC/Tests/EditMode/Fixtures/event_migration_legacy.json");
            var fixture = JObject.Parse(File.ReadAllText(fixturePath));
            var raw = (string)fixture["definitions"]["event/" + cardId + ".json"];
            if (string.IsNullOrEmpty(raw)) throw new ArgumentException("旧事件夹具不存在：" + cardId, nameof(cardId));
            var card = JObject.Parse(raw)["data"].ToObject<EventCardDefinition>();
            return CreateDefinition(card);
        }

        public static LuaScriptDefinition CreateDefinition(EventCardDefinition card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            string source = CreateScriptSource(card);
            string handlerId = "lua.event_card." + card.CardId;
            return new LuaScriptDefinition(
                card.CardId,
                card.CardId,
                handlerId,
                DefinitionVersion,
                source,
                LuaContentHasher.ComputeSha256(source));
        }

        public static string CreateScriptSource(EventCardDefinition card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            var builder = new StringBuilder();
            builder.AppendLine("return function(ctx)");
            builder.AppendLine("    local payload = ctx.payload");
            builder.AppendLine("    local effects = {}");
            builder.AppendLine("    if payload.primaryInfluenceSlotId ~= nil and payload.primaryInfluenceSlotId ~= '' then");
            builder.AppendLine(
                "        table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, " +
                "influenceSource = " + LuaString("event_card_primary:" + card.CardId) +
                ", ownerSubject = ctx.playerId, targetSlotId = payload.primaryInfluenceSlotId, causeKind = " +
                LuaString(InfluenceCauseKinds.OtherIntrinsicFlow) + " }))");
            builder.AppendLine("    end");

            for (int optionIndex = 0; optionIndex < card.ChoiceDescriptions.Count; optionIndex++)
            {
                builder.AppendLine(
                    "    if tonumber(payload.optionIndex) == " +
                    optionIndex.ToString(CultureInfo.InvariantCulture) + " then");
                AppendResourceGains(
                    builder,
                    card.ChoiceRewards[optionIndex],
                    "event_card.reward:" + card.CardId);

                IReadOnlyList<EventEffect> pendingEffects = card.ChoicePendingEffects[optionIndex];
                ResourceSet costs = CollectCosts(pendingEffects);
                bool rulePayment = EventEffectUtility.HasInfluencePayment(pendingEffects);
                if (rulePayment) builder.AppendLine("        local outerEffects = effects; effects = {}");
                else AppendResourcePayments(builder, costs, "event_card.cost:" + card.CardId);

                int slotCursor = 1;
                for (int effectIndex = 0; effectIndex < pendingEffects.Count; effectIndex++)
                {
                    EventEffect effect = pendingEffects[effectIndex];
                    if (effect == null || effect.Amount <= 0) continue;

                    switch (effect.Kind)
                    {
                        case EventEffectKind.GainScore:
                            AppendScoreGain(builder, effect.Amount, "event_card.score:" + card.CardId);
                            break;
                        case EventEffectKind.GrantResource:
                            AppendScopedResourceGain(builder, effect, card.CardId);
                            break;
                        case EventEffectKind.PlaceInfluence:
                            for (int amountIndex = 0; amountIndex < effect.Amount; amountIndex++)
                            {
                                AppendInfluencePlacement(
                                    builder,
                                    slotCursor++,
                                    "event_card_pending:" + card.CardId);
                            }
                            break;
                    }
                }

                if (rulePayment)
                {
                    string costFields = string.Join(", ", costs.Enumerate().Select(pair => pair.Key.ToString() + " = " + pair.Value.ToString(CultureInfo.InvariantCulture)));
                    builder.AppendLine("        local rightEffects = effects; effects = outerEffects");
                    builder.AppendLine("        table.insert(effects, Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ player = ctx.playerId, cost = { " + costFields + " } }) }, rightEffects = rightEffects }), { interaction = { prompt = Text.Ref('core:condition.event.event_yellow_04.payment') } }))");
                }
                builder.AppendLine("    end");
            }

            builder.AppendLine("    return effects");
            builder.AppendLine("end");
            return builder.ToString();
        }

        private static void AppendResourceGains(StringBuilder builder, ResourceSet reward, string reasonId)
        {
            if (reward == null) return;
            foreach (KeyValuePair<ResourceType, int> pair in reward.Enumerate())
            {
                if (pair.Value <= 0) continue;
                builder.AppendLine(
                    "        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = " +
                    LuaString(pair.Key.ToString()) + ", amount = " +
                    pair.Value.ToString(CultureInfo.InvariantCulture) + ", reasonId = " + LuaString(reasonId) + " }))");
            }
        }

        private static void AppendResourcePayments(StringBuilder builder, ResourceSet costs, string reasonId)
        {
            if (costs == null) return;
            foreach (KeyValuePair<ResourceType, int> pair in costs.Enumerate())
            {
                if (pair.Value <= 0) continue;
                builder.AppendLine(
                    "        table.insert(effects, Effect.PayResource({ recipient = ctx.playerId, resourceType = " +
                    LuaString(pair.Key.ToString()) + ", amount = " +
                    pair.Value.ToString(CultureInfo.InvariantCulture) + ", reasonId = " + LuaString(reasonId) + " }))");
            }
        }

        private static void AppendScoreGain(StringBuilder builder, int amount, string reasonId)
        {
            builder.AppendLine(
                "        table.insert(effects, Effect.GainScore({ recipient = ctx.playerId, amount = " +
                amount.ToString(CultureInfo.InvariantCulture) + ", reasonId = " + LuaString(reasonId) + " }))");
        }

        private static void AppendScopedResourceGain(StringBuilder builder, EventEffect effect, string cardId)
        {
            if (effect.TargetScope != EventEffectTargetScope.Opponents &&
                effect.TargetScope != EventEffectTargetScope.Self)
            {
                return;
            }

            string reasonId = effect.TargetScope == EventEffectTargetScope.Opponents
                ? "event_card.opponent_reward:" + cardId
                : "event_card.pending_reward:" + cardId;
            if (effect.TargetScope == EventEffectTargetScope.Self)
            {
                builder.AppendLine(
                    "        table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = " + LuaString(effect.ResourceType.ToString()) +
                    ", amount = " + effect.Amount.ToString(CultureInfo.InvariantCulture) +
                    ", reasonId = " + LuaString(reasonId) + " }))");
                return;
            }

            builder.AppendLine("        for _, opponent in ipairs(GameData.GetPlayers()) do");
            builder.AppendLine("            if opponent.playerId ~= ctx.playerId then");
            builder.AppendLine(
                "                table.insert(effects, Effect.GainResource({ recipient = opponent, resourceType = " + LuaString(effect.ResourceType.ToString()) +
                ", amount = " + effect.Amount.ToString(CultureInfo.InvariantCulture) +
                ", reasonId = " + LuaString(reasonId) + " }))");
            builder.AppendLine("            end");
            builder.AppendLine("        end");
        }

        private static void AppendInfluencePlacement(StringBuilder builder, int slotIndex, string sourceId)
        {
            builder.AppendLine(
                "        table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, " +
                "influenceSource = " + LuaString(sourceId) +
                ", ownerSubject = ctx.playerId, targetSlotId = payload.selectedInfluenceSlotIds[" +
                slotIndex.ToString(CultureInfo.InvariantCulture) + "], causeKind = " +
                LuaString(InfluenceCauseKinds.OtherIntrinsicFlow) + " }))");
        }

        private static ResourceSet CollectCosts(IReadOnlyList<EventEffect> effects)
        {
            var costs = new ResourceSet();
            if (effects == null) return costs;
            for (int i = 0; i < effects.Count; i++)
            {
                EventEffect effect = effects[i];
                if (effect == null || effect.Kind != EventEffectKind.PlaceInfluence || effect.CostAmount <= 0)
                {
                    continue;
                }

                costs.Set(
                    effect.CostResourceType,
                    costs.Get(effect.CostResourceType) + effect.CostAmount);
            }

            return costs;
        }



        private static string LuaString(string value)
        {
            return "'" + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "'";
        }
    }
}
