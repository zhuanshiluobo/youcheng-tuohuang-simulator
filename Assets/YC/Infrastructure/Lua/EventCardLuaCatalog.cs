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
            var cards = new[] { EventColor.Green, EventColor.Red, EventColor.Yellow }.SelectMany(EventCardDatabase.GetCardIds).Select(EventCardDatabase.Get).ToList();
            RegisterCards(registry, cards, cards.Any(card => card?.EventChoiceSchemaVersion == 1) ? ExternalContentPack.Current : null);
        }
        public static void Register(EffectRegistry registry, ExternalContentPack pack) => RegisterCards(registry, pack.CreateEvents(), pack);
        private static void RegisterCards(EffectRegistry registry, IEnumerable<EventCardDefinition> cards, ExternalContentPack pack = null)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            var definitions = cards.ToList();
            var dispatcher = new LuaEffectEventDispatcher(registry, compiler: definitions.Any(card => card?.EventChoiceSchemaVersion == 1)
                ? new LuaEffectSpecCompiler(presentationPack: pack ?? throw new InvalidOperationException("事件选择缺少冻结内容包。")) : null);
            PlayerEntranceLuaCatalog.Register(registry);
            RegisterCityMoveObserver(dispatcher, registry);
            foreach (var card in definitions)
            {
                if (card == null) continue;

                if (card.EventChoiceSchemaVersion != 1 || pack == null)
                    throw new InvalidOperationException("事件定义版本不受支持：" + card.CardId);
                var content = pack.ActiveDefinitions.Single(d => d.ContentType == "event" && (d.DefinitionId == card.CardId || d.RuntimeId == card.CardId));
                foreach (var choice in card.Choices)
                    RegisterChoice(dispatcher, registry, pack, content, card.CardId, choice.ChoiceId, choice.Script, choice.Version, choice.CompletionHandlerId);
                if (card.Choices.Count == 0)
                    RegisterChoice(dispatcher, registry, pack, content, card.CardId, "", card.ImmediateResolve.Script, card.ImmediateResolve.Version, "");
            }
        }

        internal static string CompletionAbilityId(ExternalContentPack pack, ExternalContentDefinition content, string handlerId) =>
            StableIdFactory.Create("event-completion-ability", pack.GetDefinitionSourcePackId(content.DefinitionId), content.DefinitionId, handlerId);

        internal static LuaScriptDefinition CreateCompletionDefinition(ExternalContentPack pack, ExternalContentDefinition content, string handlerId)
        {
            var handler = content.AbilityHandlers.Single(value => value.HandlerId == handlerId);
            string source = pack.GetScript(handler.Script);
            return new LuaScriptDefinition(content.DefinitionId, CompletionAbilityId(pack, content, handlerId), handler.HandlerId,
                handler.Version, source, LuaContentHasher.ComputeSha256(source), pack.GetScriptEntryId(handler.Script));
        }

        private static void RegisterChoice(LuaEffectEventDispatcher dispatcher, EffectRegistry registry, ExternalContentPack pack,
            ExternalContentDefinition content, string cardId, string choiceId, string path, string version, string completionHandlerId)
        {
            string owner = pack.GetDefinitionSourcePackId(content.DefinitionId);
            string subscriptionId = StableIdFactory.Create("event-choice-handler", owner, content.DefinitionId, choiceId);
            if (registry.HasEventHandler(subscriptionId)) return;
            string route = choiceId.Length == 0 ? StableIdFactory.Create("event-choice-immediate", cardId)
                : EventCardOptionIdFactory.CreateChoice(cardId, choiceId);
            LuaScriptDefinition completion = string.IsNullOrEmpty(completionHandlerId) ? null : CreateCompletionDefinition(pack, content, completionHandlerId);
            string abilityId = completion?.AbilityId ?? StableIdFactory.Create("event-choice-ability", owner, content.DefinitionId, choiceId);
            if (completion != null)
            {
                string completionSubscription = StableIdFactory.Create("event-choice-continuation", owner, content.DefinitionId, completionHandlerId);
                if (!registry.HasEventHandler(completionSubscription))
                    dispatcher.RegisterContinuation(new LuaHandlerDefinition(completionSubscription, "EffectCompleted", "", completion,
                        EffectHandlerRole.Continuation, contentInstanceId: cardId, abilityId: abilityId));
            }
            string source = pack.GetScript(path);
            var script = new LuaScriptDefinition(content.DefinitionId, abilityId, subscriptionId, version, source,
                LuaContentHasher.ComputeSha256(source), pack.GetScriptEntryId(path));
            var definition = new LuaHandlerDefinition(subscriptionId, EventCardEffectEventTypeIds.Resolved, route, script,
                EffectHandlerRole.Primary, contentInstanceId: cardId, abilityId: abilityId,
                completionHandlerId: completionHandlerId, continuationTarget: completion);
            RegisterChoiceHandler(registry, dispatcher, definition, cardId);
        }

        // 对纯地图选点先取得宿主合法候选；原选点完成后先放基础影响力，再进入 Lua 完成续接。
        // 不增加 Lua 构造器或参数，普通奖励仍按基础影响力、Lua 效果的顺序排队。
        private static void RegisterChoiceHandler(EffectRegistry registry, LuaEffectEventDispatcher dispatcher,
            LuaHandlerDefinition definition, string cardId)
        {
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(definition.SubscriptionId, definition.EventType,
                definition.RouteKey, definition.Script.HandlerId, context =>
                {
                    var effects = dispatcher.Invoke(definition, context).ToList();
                    string primary = ItemTransferEffectExecutor.Text(context.Event.Payload, "primaryInfluenceSlotId");
                    EffectSpec Primary() => InfluenceEffectSpecFactory.PlaceInfluence(context.Event.PlayerId, primary,
                        "event_card_primary:" + cardId, causeKind: InfluenceCauseKinds.OtherIntrinsicFlow);
                    if (effects.Count == 1 && TryReadPaidPlacement(effects[0], out var paidPlacement))
                    {
                        if (registry.InfluenceService == null) throw new InvalidOperationException("event_choice_influence_service_missing");
                        var legal = registry.InfluenceService.GetLegalPlacementSlotIds(context.State, context.Event.PlayerId)
                            .Where(id => id != primary);
                        var scope = ItemTransferEffectExecutor.Value(paidPlacement.NormalizedArguments, "candidateScope");
                        if (scope != null)
                        {
                            var allowed = new HashSet<string>((scope.Items ?? new List<NormalizedValue>()).Select(value =>
                                value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value.StringValue), StringComparer.Ordinal);
                            legal = legal.Where(allowed.Contains);
                        }
                        if (!legal.Any()) return new[] { EmptyInfluenceSelection(context.Event.PlayerId) };
                    }
                    if (effects.Count == 1 && IsInfluencePreselection(effects[0]))
                    {
                        var choice = effects[0];
                        if (registry.InfluenceService == null) throw new InvalidOperationException("event_choice_influence_service_missing");
                        var available = new HashSet<string>(registry.InfluenceService.GetLegalPlacementSlotIds(context.State, context.Event.PlayerId), StringComparer.Ordinal);
                        var options = ItemTransferEffectExecutor.Value(choice.NormalizedArguments, "options");
                        var eligible = options.Items.Select(value => value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value.StringValue)
                            .Where(id => !string.IsNullOrEmpty(id) && id != primary && available.Contains(id))
                            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
                        options.Items.Clear();
                        foreach (string id in eligible) options.Items.Add(NormalizedValue.CreateString(id));
                        if (eligible.Count == 0) choice.CompletionHandlerId = string.Empty;
                        else foreach (string unused in eligible)
                            choice.NestedEffectGroups.Add(string.IsNullOrEmpty(primary) ? new List<EffectSpec>() : new List<EffectSpec> { Primary() });
                        return effects;
                    }
                    if (!string.IsNullOrEmpty(primary)) effects.Insert(0, Primary());
                    return effects;
                }, EffectHandlerRole.Primary, definition.Priority, definition.ContentInstanceId,
                definition.AbilityId, definition.Script.DefinitionVersion, definition.Script.ContentHash, definition.Script.ContentId));
        }

        private static bool TryReadPaidPlacement(EffectSpec spec, out EffectSpec placement)
        {
            placement = null;
            if (spec?.EffectTypeId != EffectTypeIds.Condition || spec.NestedEffectGroups == null || spec.NestedEffectGroups.Count != 2 ||
                spec.NestedEffectGroups[0].Count != 1 || spec.NestedEffectGroups[0][0].EffectTypeId != ResourceEffectTypeIds.Pay ||
                spec.NestedEffectGroups[1].Count != 1) return false;
            var candidate = spec.NestedEffectGroups[1][0];
            if (candidate.EffectTypeId != InfluenceEffectTypeIds.PlaceInfluence ||
                ItemTransferEffectExecutor.Value(candidate.NormalizedArguments, "targetSlotId") != null ||
                ItemTransferEffectExecutor.Value(candidate.NormalizedArguments, "targetLocationId") != null) return false;
            var owner = ItemTransferEffectExecutor.Value(candidate.NormalizedArguments, "ownerSubject");
            if (ItemTransferEffectExecutor.Text(owner, "subjectType") != "player" ||
                ItemTransferEffectExecutor.Number(owner, "playerId", -1) != candidate.PlayerId) return false;
            placement = candidate;
            return true;
        }

        private static EffectSpec EmptyInfluenceSelection(int playerId) => EffectSpec.Create(LuaDomainEffectTypeIds.Choice,
            ItemTransferEffectExecutor.Object("player", NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture)),
                "interactionType", NormalizedValue.CreateString("effect.influence.place.target"),
                "options", ItemTransferEffectExecutor.Strings(Array.Empty<string>()),
                "minSelections", NormalizedValue.CreateInteger(1), "maxSelections", NormalizedValue.CreateInteger(1)), playerId);

        private static bool IsInfluencePreselection(EffectSpec spec)
        {
            var options = ItemTransferEffectExecutor.Value(spec?.NormalizedArguments, "options");
            return spec != null && spec.EffectTypeId == LuaDomainEffectTypeIds.Choice &&
                ItemTransferEffectExecutor.Text(spec.NormalizedArguments, "interactionType") == "effect.influence.place.target" &&
                ItemTransferEffectExecutor.Number(spec.NormalizedArguments, "minSelections", 1) == 1 &&
                ItemTransferEffectExecutor.Number(spec.NormalizedArguments, "maxSelections", 1) == 1 &&
                options?.Kind == NormalizedValueKind.Array && !string.IsNullOrEmpty(spec.CompletionHandlerId) &&
                spec.NestedEffectGroups.Count == 0 && spec.Children.Count == 0;
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
    local point = Global.Map.GetLocation(payload.targetLocationId)
    if point == nil or point.hasResourcePointIndicator then
        return {}
    end
    return { Effect.RevealEventCard({
        player = ctx.playerId,
        eventDeck = 'main',
        placeResourcePointIndicator = true,
        resourcePoint = payload.targetLocationId,
        eventColor = point.eventColor
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
    }
}
