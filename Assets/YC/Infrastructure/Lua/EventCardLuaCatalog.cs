using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    /// <summary>
    /// 事件牌内容的版本化 Lua handler 目录。
    /// 目录数据只用于生成可审计的 Lua 源码；实际运行统一经过沙箱、Effect 白名单和编译器，
    /// 不把 C# 委托或 Lua 函数引用写入 GameState。
    /// </summary>
    public static class EventCardLuaCatalog
    {
        public const string DefinitionVersion = "1.0.0";

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var dispatcher = new LuaEffectEventDispatcher(registry);
            PlayerEntranceLuaCatalog.Register(registry);
            RegisterCityMoveObserver(dispatcher, registry);
            foreach (string cardId in new[] { EventColor.Green, EventColor.Red, EventColor.Yellow }.SelectMany(EventCardDatabase.GetCardIds))
            {
                EventCardDefinition card = EventCardDatabase.Get(cardId);
                if (card == null) continue;

                string subscriptionId = "lua:event-card:" + card.CardId;
                if (registry.HasEventHandler(subscriptionId)) continue;
                dispatcher.Register(new LuaHandlerDefinition(
                    subscriptionId,
                    EventCardEffectEventTypeIds.Resolved,
                    card.CardId,
                    CreateDefinition(card),
                    EffectHandlerRole.Primary,
                    0,
                    string.Empty,
                    card.CardId));
            }
        }

        private static void RegisterCityMoveObserver(
            LuaEffectEventDispatcher dispatcher,
            EffectRegistry registry)
        {
            const string subscriptionId = "lua:city-move:completed:event-card";
            if (registry.HasEventHandler(subscriptionId)) return;
            const string source = @"
return function(ctx)
    local payload = ctx.payload
    local point = Global.Map.GetResourcePoint(payload.targetLocationId)
    if point == nil or point.hasResourcePointIndicator then
        return {}
    end
    return { Effect.RevealEventCard({
        player = ctx.playerId,
        eventDeck = 'main',
        placeResourcePointIndicator = true,
        resourcePoint = payload.targetLocationId,
        eventColor = 'green'
    }) }
end";
            dispatcher.Register(new LuaHandlerDefinition(
                subscriptionId,
                CityMoveEventTypeIds.CityMoveCompleted,
                "*",
                new LuaScriptDefinition(
                    "content.city-move.completed-event-card",
                    "city_move.completed_event_card",
                    subscriptionId,
                    DefinitionVersion,
                    source,
                    LuaContentHasher.ComputeSha256(source)),
                EffectHandlerRole.Observer,
                0,
                "city-move",
                "city_move.completed_event_card"));
        }

        /// <summary>
        /// 为指定内容创建稳定脚本定义。测试和工具可用它检查版本、哈希和源码，
        /// 运行时注册仍通过 <see cref="Register"/> 进入统一 dispatcher。
        /// </summary>
        public static LuaScriptDefinition CreateDefinition(string cardId)
        {
            EventCardDefinition card = EventCardDatabase.Get(cardId);
            if (card == null) throw new ArgumentException("事件牌不存在：" + cardId, nameof(cardId));
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
                AppendResourcePayments(builder, costs, "event_card.cost:" + card.CardId);

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
