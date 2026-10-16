using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    /// <summary>通用生命周期声明、影响力锁及既有收尾调度；技能正文由正式Lua提供。</summary>
    public static class CharacterContentEffectExecutor
    {
        public const string Prefix = "effect.character.content.";
        public const string CleanupActionId = "character.execute_cleanup";
        public const string CleanupOfferTypeId = Prefix + "cleanup_offer";
        public const string CleanupInteractionTypeId = "character.cleanup.settlement";
        public static readonly Dictionary<string, string> Constructors = new Dictionary<string, string> {
            { "RetainCharacter", "retain" }, { "CreateCityInfluenceLock", "city_lock" }
        };
        public static bool IsPublic(string type) => type != null && type.StartsWith(Prefix, StringComparison.Ordinal) && Constructors.Values.Contains(type.Substring(Prefix.Length));
        public static string[] Fields(string type) => type == Prefix + "city_lock" ? new[] { "apiVersion", "player", "definitionId" } : new[] { "apiVersion", "player" };
        public static string ValidateArguments(string type, NormalizedValue args) => IsPublic(type) && args?.Kind == NormalizedValueKind.Object && Number(args,"apiVersion") == 1 &&
            CharacterLifecycleService.ParsePlayer(Text(args,"player")) > 0 && args.Properties.All(p => Fields(type).Concat(new[] { "kind", "effectTypeId" }).Contains(p.Name)) &&
            (type != Prefix + "city_lock" || Text(args,"definitionId").Length > 0) ? "" : "character_content_arguments_invalid";
        public static bool ValidateState(GameState state)
        {
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => IsPublic(n.EffectTypeId)))
                if (ValidateArguments(node.EffectTypeId,node.NormalizedArguments).Length > 0 || CharacterLifecycleService.GetResumableFlowStage(node).Length > 0) return false;
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == CleanupOfferTypeId))
                if (ValidateCleanupOfferArguments(node.NormalizedArguments).Length > 0 || !new[] { "", "offered" }.Contains(node.FlowStage) || node.ChildEffectIds.Count != 0 || node.NestedEffects.Count == 0 || node.NestedGroupSizes.Count != 1 || node.NestedGroupSizes[0] != node.NestedEffects.Count) return false;
            return true;
        }
        public static void Register(EffectRegistry registry)
        {
            ContentTransactionEffectExecutor.Register(registry);
            foreach (string suffix in Constructors.Values)
            {
                string type = Prefix + suffix;
                if (!registry.TryGet(type,out _)) registry.Register(new EffectRegistration(type, c => {
                    if (ValidateArguments(c.Node.EffectTypeId,c.Node.NormalizedArguments).Length > 0 || Text(c.Node.NormalizedArguments,"player") != c.Node.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed("character_content_arguments_invalid");
                    return c.Node.EffectTypeId == Prefix + "city_lock" ? InfluenceLockRules.Create(c,Text(c.Node.NormalizedArguments,"definitionId")) : EffectStepResult.Completed();
                },EffectExecutorKind.Atomic,"v1") { Validator = spec => ValidateArguments(type,spec.NormalizedArguments) });
            }
            if (!registry.TryGet(Prefix + "immediate_special",out _)) registry.Register(new EffectRegistration(Prefix + "immediate_special",ImmediateSpecial,EffectExecutorKind.IntrinsicFlow,"v1"));
            if (!registry.TryGet(CleanupOfferTypeId,out _)) registry.Register(new EffectRegistration(CleanupOfferTypeId,c => EffectStepResult.Completed().WithFlowStage("offered"),EffectExecutorKind.IntrinsicFlow,"v1") { Validator = spec => ValidateCleanupOfferArguments(spec.NormalizedArguments) });
            InfluenceLockRules.Register(registry);
        }
        private static EffectStepResult Empty() => EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("no_candidates")));
        private static EffectStepResult Wait(EffectExecutionContext c, string stage, IEnumerable<string> ids, string type = "lua.choice", bool allowDecline = false)
        {
            var list = ids.ToList(); if (list.Count == 0) return Empty();
            var request = new EffectInteractionSpec { InteractionTypeId = type, AnsweringPlayerId = c.Node.PlayerId, Visibility = "owner", PromptKey = c.Node.EffectTypeId + "." + stage, MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id", AllowDecline = allowDecline };
            request.CandidateIds.AddRange(list); return EffectStepResult.Continue(stage).AddInteraction(PrepareInteraction(c, request));
        }
        private static string ValidateCleanupOfferArguments(NormalizedValue args) =>
            args?.Kind == NormalizedValueKind.Object && Number(args,"apiVersion") == 1 &&
            new[] { "cardInstanceId", "abilityId", "windowEffectId" }.All(key => Value(args,key)?.Kind == NormalizedValueKind.String && Text(args,key).Length > 0) &&
            args.Properties.All(p => new[] { "apiVersion", "cardInstanceId", "abilityId", "windowEffectId" }.Contains(p.Name)) ? "" : "cleanup_offer_arguments_invalid";
        public static IList<EffectSpec> WrapCleanupOffer(EffectEventHandlerContext context, string abilityId, IList<EffectSpec> body)
        {
            if (body == null || body.Count == 0) return new List<EffectSpec>();
            var card = Value(context.Event.HostOnlyPayload, "cardInstanceRef");
            string cardId = card?.Kind == NormalizedValueKind.StableReference ? card.ReferenceId : card?.StringValue;
            if (string.IsNullOrEmpty(cardId) || context.Event.EventType != "PlayerCleanupStarted" ||
                !context.State.EffectRuntime.MainNodes.Any(n => n.ExecutionEffectId == context.Event.SourceEffectId && n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == context.Event.PlayerId))
                throw new InvalidOperationException("cleanup_offer_window_missing");
            var spec = EffectSpec.Create(CleanupOfferTypeId, Object("apiVersion", NormalizedValue.CreateInteger(1),
                "cardInstanceId", NormalizedValue.CreateString(cardId), "abilityId", NormalizedValue.CreateString(abilityId),
                "windowEffectId", NormalizedValue.CreateString(context.Event.SourceEffectId)), context.Event.PlayerId);
            spec.NestedEffectGroups.Add(body.ToList());
            return new List<EffectSpec> { spec };
        }
        private static IEnumerable<EffectNodeRuntimeState> CleanupOffers(GameState state, string windowId) =>
            state.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Completed && n.FlowStage == "offered" &&
                n.EffectTypeId == CleanupOfferTypeId && Text(n.NormalizedArguments,"windowEffectId") == windowId);
        private static string CleanupCard(GameState state,EffectNodeRuntimeState offer) => Text(offer.NormalizedArguments,"cardInstanceId");
        private static string CleanupAction(EffectNodeRuntimeState offer) => CleanupActionId;
        private static CharacterModeContract CleanupMode(GameState state, EffectNodeRuntimeState offer, string card) =>
            state.CharacterUses.Where(u => u.CardInstanceId == card).SelectMany(u => u.Modes).LastOrDefault(m =>
                m.SettlementActionId == CleanupAction(offer) && (offer.EffectTypeId != CleanupOfferTypeId || m.AbilityId == Text(offer.NormalizedArguments, "abilityId")));
        public static bool HasPendingCleanupOffers(GameState state, EffectNodeRuntimeState window) =>
            CleanupOffers(state, window.EffectId).Any(offer => !CharacterSettlementOptionQuery.IsInvoked(window,
                CharacterSettlementOptionQuery.OptionId(window.EffectId, CleanupCard(state, offer), CleanupAction(offer))));
        public static IReadOnlyList<CharacterSettlementOption> GetCleanupOptions(GameState state, int player)
        {
            var result = new List<CharacterSettlementOption>();
            var main = state.EffectRuntime.MainNodes.Find(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId && n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == player && !n.CompletionRequested);
            var window = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == main?.ExecutionEffectId);
            if (window == null) return result;
            var request = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.OwnerEffectId == window.EffectId && r.Status == "open" && r.InteractionTypeId == CleanupInteractionTypeId);
            foreach (var offer in CleanupOffers(state, window.EffectId))
            {
                string card = CleanupCard(state, offer), id = CharacterSettlementOptionQuery.OptionId(window.EffectId, card, CleanupAction(offer));
                bool invoked = CharacterSettlementOptionQuery.IsInvoked(window, id);
                if (request == null && !invoked) continue;
                var mode = CleanupMode(state, offer, card);
                int cost = 0;
                string reason = invoked ? "settlement_already_invoked" : request == null ? "character_window_closed" :
                    mode == null || !CharacterLifecycleService.IsSubscriptionActive(state, card, mode.AbilityId) ? "character_subscription_inactive" :
                    CleanupCostReason(state, player, offer.NestedEffects);
                result.Add(new CharacterSettlementOption { OptionId = id, InteractionId = request?.GetStableInteractionId() ?? "", OwnerEffectId = window.EffectId,
                    SourceItemId = card, CandidateId = offer.EffectId, AnsweringPlayerId = player, StateRevision = state.EffectRuntime.StateRevision,
                    CandidateSetVersion = request?.CandidateSetVersion ?? 0, TitleKey = mode?.TitleKey ?? "", DescriptionKey = mode?.DescriptionKey ?? "",
                    RequiredGoldVoucherAmount = cost, IsEnabled = reason.Length == 0, VisibleToViewer = true, DisabledReasonCode = reason });
            }
            return result;
        }
        public static IList<EffectSpec> GetCleanupBody(GameState state, CharacterSettlementOption option)
        {
            var offer = CleanupOffers(state, option.OwnerEffectId).Single(n => n.EffectId == option.CandidateId && n.PlayerId == option.AnsweringPlayerId);
            return offer.NestedEffects.Select(EffectRegistry.FromRuntimeSpec).ToList();
        }
        private static string CleanupCostReason(GameState state, int player, IEnumerable<EffectSpecRuntimeState> effects)
        {
            foreach (var effect in effects)
            {
                if (effect.EffectTypeId == EffectTypeIds.Condition && effect.NestedGroupSizes.Count > 0)
                {
                    string reason = CleanupCostReason(state, player, effect.NestedEffects.Take(effect.NestedGroupSizes[0]));
                    if (reason.Length > 0) return reason;
                }
                else if (effect.EffectTypeId == ResourceEffectTypeIds.Pay && Enum.TryParse(Text(effect.NormalizedArguments, "resourceType"), true, out ResourceType resource))
                {
                    var cost = new ResourceSet(); cost.Set(resource, Number(effect.NormalizedArguments, "amount"));
                    if (!ResourceEffectExecutor.CanPayWithStoredMarkers(state, player, cost)) return "insufficient_resource";
                }
                else if (effect.EffectTypeId == ContentPrimitiveEffectExecutor.PayMarkers)
                {
                    var zone = state.ItemZones.Zones.Find(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == Text(effect.NormalizedArguments, "cardInstanceId") && z.LocalZoneId == Text(effect.NormalizedArguments, "localZoneId"));
                    if (zone == null || state.ItemZones.Items.Count(i => i.ZoneId == zone.ZoneId && i.PhysicalKind == ItemPhysicalKind.Cube) < Number(effect.NormalizedArguments, "count")) return "insufficient_items";
                }
            }
            return "";
        }
        private static EffectStepResult ImmediateSpecial(EffectExecutionContext c)
        {
            if(c.Node.FlowStage=="executing")return EffectStepResult.Completed();
            var player=c.State.FindPlayer(c.Node.PlayerId);
            var choices=player.DeclaredCityStyles.Where(m=>m.MarkerArea.Length>0 && m.UnlockedSpecialActionId.Length>0 && m.RemainingSpecialActionUses>0 &&
                !player.UsedSpecialActionIdsThisRound.Contains(m.UnlockedSpecialActionId) &&
                !(YC.Domain.SpecialActions.SpecialActionDatabase.Get(c.State,m.UnlockedSpecialActionId)?.LocksCharacterCard==true && (player.UsedCharacterThisTurn || c.State.CharacterUses.Any(u=>u.ExecutingPlayerId==player.PlayerId && !u.Finished)))).ToList();
            if(c.Node.FlowStage.Length==0)return Wait(c,"choose_special",choices.Select(m=>m.InfluenceMarkerId));
            if(c.Node.FlowStage!="choose_special")throw new InvalidOperationException("immediate_special_stage_invalid");
            var selected=choices.Find(m=>m.InfluenceMarkerId==Answer(c)); if(selected==null)return Wait(c,"choose_special",choices.Select(m=>m.InfluenceMarkerId));
            var spec=CityStyleSpecialActionEffectSpecFactory.Activate(c.Node.PlayerId,selected.UnlockedSpecialActionId,selected.InfluenceMarkerId);
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name="activationSource",Value=NormalizedValue.CreateString("immediate")});spec.NormalizedArguments.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            return EffectStepResult.Continue("executing").AddChild(spec);
        }
    }
}
