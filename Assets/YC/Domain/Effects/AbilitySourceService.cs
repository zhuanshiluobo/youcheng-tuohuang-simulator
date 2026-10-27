using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>按冻结声明和真实实例解析资格；不保存另一份库存或按钮状态。</summary>
    public static class AbilitySourceService
    {
        public const string SubscriptionLinkVersion = "character_subscription.link.v1";
        public const string SubscriptionLinkPrefix = "lifecycle.character_subscription:";
        public static string SubscriptionAbility(AbilitySourceDeclaration declaration)
        {
            var links = declaration.AllowedOperations.Where(p => p.StartsWith(SubscriptionLinkPrefix, StringComparison.Ordinal)).ToList();
            if (links.Count == 0) return declaration.AbilityId;
            if (links.Count != 1) return "";
            string target = links[0].Substring(SubscriptionLinkPrefix.Length);
            return target.Length > 0 && !target.Contains(':') && target != "*" ? target : "";
        }
        public static IReadOnlyList<AbilitySourceRef> QuerySources(GameState state, IEnumerable<AbilitySourceDeclaration> declarations,
            int playerId = -1, string kind = "", string eventType = "", string queryKind = "", bool includeCharacterActivation = false)
        {
            var result = new List<AbilitySourceRef>();
            if (state?.ItemZones == null || declarations == null) return result;
            foreach (var declaration in declarations.OrderBy(d => d.AbilityId, StringComparer.Ordinal))
            {
                foreach (var item in state.ItemZones.Items.Where(i => i.DefinitionId == declaration.DefinitionId).OrderBy(i => i.ItemId, StringComparer.Ordinal))
                {
                    var zone = state.ItemZones.Zones.Find(z => z.ZoneId == item.ZoneId);
                    if (zone == null || !declaration.EffectiveZones.Contains(zone.TemplateId)) continue;
                    if (item.TypeId == "entrepreneur.ability_card" || item.TypeId == "entrepreneur.round_card")
                    {
                        if (zone.TemplateId != "core.zone.entrepreneur_active" || item.TypeId == "entrepreneur.round_card" &&
                            !AbilitySourceSelection.HasSelectedFaceReceipt(state, item, Text(item.LocalState, "activeFaceId"), item.ControllerSubject.PlayerId)) continue;
                    }
                    string face = Text(item.LocalState, "activeFaceId");
                    if (declaration.ActiveFaceId.Length > 0 && face != declaration.ActiveFaceId) continue;
                    foreach (var player in state.Players.Where(p => playerId < 0 || p.PlayerId == playerId).OrderBy(p => p.PlayerId))
                    {
                        string authorization = ResolveAuthorization(state, declaration, item, zone, player.PlayerId,includeCharacterActivation);
                        if (authorization.Length == 0) continue;
                        var source = new AbilitySourceRef { ItemId = item.ItemId, DefinitionId = item.DefinitionId, AbilityId = declaration.AbilityId,
                            ActiveFaceId = declaration.ActiveFaceId.Length > 0 ? face : "", DeclarationVersion = declaration.DeclarationVersion,
                            AuthorizedPlayerId = player.PlayerId, AuthorizationRef = authorization, EffectiveZoneId = zone.ZoneId, RuleOwnerItemId = item.ItemId };
                        source.SourceKey = StableIdFactory.Create("ability-source", source.ItemId, source.AbilityId, source.ActiveFaceId,
                            source.DeclarationVersion, player.PlayerId.ToString(CultureInfo.InvariantCulture));
                        var grants = Value(item.LocalState, "grantingSourceRefs");
                        if (grants?.Kind == NormalizedValueKind.Array) source.GrantingSourceRefs = grants.Items.Select(v => v.StringValue).OrderBy(v => v, StringComparer.Ordinal).ToList();
                        result.Add(source);
                    }
                }
            }
            foreach (var source in result) source.GrantingSourceRefs = source.GrantingSourceRefs.Where(key => result.Any(s => s.SourceKey == key && s.AuthorizedPlayerId == source.AuthorizedPlayerId)).ToList();
            var byAbility = declarations.ToDictionary(d => d.AbilityId, StringComparer.Ordinal);
            return result.Where(source => (kind.Length == 0 || byAbility[source.AbilityId].Kind == kind) &&
                (eventType.Length == 0 || byAbility[source.AbilityId].ObservedEvents.Contains(eventType)) && (queryKind.Length == 0 || byAbility[source.AbilityId].QueryKinds.Contains(queryKind))).ToList();
        }

        private static string ResolveAuthorization(GameState state, AbilitySourceDeclaration declaration, ItemInstanceState item, ZoneInstanceState zone, int player,bool includeCharacterActivation)
        {
            bool controlled = item.ControllerSubject?.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId == player;
            switch (declaration.AuthorizationPolicy)
            {
                case "controller": return controlled ? "controller:" + player : "";
                case "holder": return controlled && zone.OwnerPlayerId == player ? "holder:" + player + ":" + zone.ZoneId : "";
                case "owner": return item.OwnerSubject?.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == player ? "owner:" + player : "";
                case "character_covered":
                    var cover = state.CharacterCoverSlots.Find(c => c.CardInstanceId == item.ItemId && c.PlayerId == player && c.SlotId == item.SlotId);
                    return controlled && cover != null ? "cover:" + cover.SourceEffectId + ":" + cover.SlotId : "";
                case "character_subscription":
                    string subscription = SubscriptionAbility(declaration);
                    if (!controlled || subscription.Length == 0) return "";
                    if (CharacterLifecycleService.IsSubscriptionActive(state, item.ItemId, subscription)) return "subscription:" + item.ItemId + ":" + player;
                    // 正文首次装载发生在持续牌落入persistent之前。仅原能力的真实Use/Activation祖先有此资格；
                    // 链接到该订阅的纯付款来源仍须等订阅正式生效，普通payload不能声明这份证明。
                    if (includeCharacterActivation && declaration.Kind == "event" && subscription == declaration.AbilityId && zone.TemplateId == "core.zone.character_covered")
                    {
                        var use = state.CharacterUses.SingleOrDefault(u => u.CardInstanceId == item.ItemId && u.ExecutingPlayerId == player && !u.Finished);
                        var activation = use == null ? null : state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.ParentEffectId == use.OwnerEffectId &&
                            n.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && n.PlayerId == player &&
                            Text(n.NormalizedArguments,"useId") == use.UseId && Text(n.NormalizedArguments,"abilityId") == subscription &&
                            use.Modes.Any(m => m.AbilityId == subscription && m.Mode == Text(n.NormalizedArguments,"mode")) &&
                            !use.CompletedModes.Contains(Text(n.NormalizedArguments,"mode")) && !use.SkippedModes.Contains(Text(n.NormalizedArguments,"mode")) &&
                            n.Status != EffectNodeStatus.Completed && n.Status != EffectNodeStatus.Failed && n.Status != EffectNodeStatus.Faulted);
                        if (activation != null) return "activation:" + activation.EffectId + ":" + player;
                    }
                    return "";
                case "active_selection":
                    string receipt = Text(item.LocalState, "activationAuthorizationRef");
                    // 004将正式选择收据写入同一真实实例；草稿、未选面和无来源节点都无资格。
                    return controlled && zone.OwnerPlayerId == player && receipt.Length > 0 && AbilitySourceSelection.HasSelectedFaceReceipt(state, item, Text(item.LocalState, "activeFaceId"), player)
                        ? "selection:" + receipt : "";
                case "public_declaration":
                    var declarations = state.FindPlayer(player)?.DeclaredCityStyles;
                    return declarations != null && declarations.Any(d => d.CityStyleId == item.DefinitionId && d.MarkerArea.Length > 0 &&
                        state.ItemZones.Items.Any(m => m.ItemId == d.InfluenceMarkerId && m.OwnerSubject.Kind == ItemSubjectKind.Player && m.OwnerSubject.PlayerId == player &&
                            state.ItemZones.Zones.Any(z => z.ZoneId == m.ZoneId && z.ParentKind == ZoneParentKind.Item && z.ParentId == item.ItemId)))
                        ? "declaration:" + item.ItemId + ":" + player : "";
                default: return "";
            }
        }

        public static bool IsCurrent(GameState state, IEnumerable<AbilitySourceDeclaration> declarations, AbilitySourceRef source) => source != null &&
            QuerySources(state, declarations, source.AuthorizedPlayerId).Any(s => SameSource(s,source));
        public static bool IsCurrentForCarrierLoad(GameState state,IEnumerable<AbilitySourceDeclaration> declarations,AbilitySourceRef source) => source != null &&
            QuerySources(state,declarations,source.AuthorizedPlayerId,includeCharacterActivation:true).Any(s=>SameSource(s,source));
        private static bool SameSource(AbilitySourceRef a,AbilitySourceRef b) => a.SourceKey==b.SourceKey && a.AuthorizationRef==b.AuthorizationRef &&
            a.EffectiveZoneId==b.EffectiveZoneId && a.ActiveFaceId==b.ActiveFaceId && a.GrantingSourceRefs.SequenceEqual(b.GrantingSourceRefs);

        public static string WindowId(GameState state, int player) => state.EffectRuntime.MainNodes.Find(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId &&
            n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == player)?.ExecutionEffectId ??
            StableIdFactory.Create("action-window", state.GameId, state.Round.ToString(CultureInfo.InvariantCulture), state.ActionRound.ToString(CultureInfo.InvariantCulture), player.ToString(CultureInfo.InvariantCulture));

        public static string OccupancyKey(GameState state, AbilitySourceRef source, AbilityActionDeclaration action) => StableIdFactory.Create("action-occupancy",
            source.ItemId, source.AuthorizationRef.StartsWith("declaration:", StringComparison.Ordinal) || source.AuthorizationRef.StartsWith(AbilityLegacyStyleBridge.AuthorizationPrefix, StringComparison.Ordinal)
                ? source.AuthorizedPlayerId.ToString(CultureInfo.InvariantCulture) : "instance", action.UsageTrackId.Length > 0 ? action.UsageTrackId : action.ActionId,
            action.LifecyclePolicy == "once" ? "game" : action.LifecyclePolicy == "round" ? "round:" + state.Round : WindowId(state, source.AuthorizedPlayerId));

        public static string WindowReason(GameState state, AbilitySourceRef source, AbilityActionDeclaration action, string ownInvocation = "")
        {
            int player = source.AuthorizedPlayerId;
            var p = state.FindPlayer(player);
            if (state.EffectRuntime.Status != EffectRuntimeStatus.Active) return "action_fault_paused";
            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2) return "wrong_phase";
            if (state.CurrentPlayerId != player) return "not_current_player";
            if (state.HasPendingChoiceForActiveOperation()) return "pending_choice_required";
            var window = state.EffectRuntime.MainNodes.Find(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId && n.PlayerId == player && n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow);
            if (state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open" && !(state.RuleConfiguration.PlayerStageSchedulingMode == PlayerStageSchedulingMode.Concurrent && EffectTreeExecutor.TryFindDeferredResponse(state, r.OwnerEffectId, out var response) && (response.ProtocolVersion == 0 || response.IsBypass)) && !(window != null && r.OwnerEffectId == window.ExecutionEffectId &&
                r.InteractionTypeId.StartsWith(RoundExecutionService.MainlineCompletionInteractionTypeId + ":", StringComparison.Ordinal)))) return "action_window_busy";
            string occupancy = OccupancyKey(state, source, action);
            if (state.EffectRuntime.AbilityActionInvocations.Any(i => i.InvocationId != ownInvocation && i.OccupancyKey == occupancy &&
                (InvocationActive(state, i) || i.ActivationCommitted && ConsumesLifecycle(action)))) return "action_group_occupied";
            if (action.EntryKind == "special")
            {
                var budget = new MainActionBudgetService().ValidateCanSpendSpecial(state, player);
                if (!budget.IsValid) return budget.Reason;
            }
            if (action.LockPolicy == "character_action_round" && (p.UsedCharacterThisTurn || state.CharacterUses.Any(u => u.ExecutingPlayerId == player && !u.Finished && !u.SelectionCancelled))) return "character_action_round_conflict";
            if (source.AuthorizationRef.StartsWith("cover:", StringComparison.Ordinal))
            {
                var availability = CharacterLifecycleService.GetAvailability(state, player, source.ItemId);
                if (!availability.CanUse) return availability.DisabledReasonCode;
            }
            return "";
        }
        public static bool ConsumesLifecycle(AbilityActionDeclaration action) => action.LifecyclePolicy == "once" || action.LifecyclePolicy == "round" || action.LifecyclePolicy == "action_round";
        public static bool InvocationActive(GameState state, AbilityActionInvocation invocation)
        {
            if (invocation.Phase != "completed") return true;
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == invocation.OwnerEffectId);
            return owner == null || owner.Status != EffectNodeStatus.Completed && owner.Status != EffectNodeStatus.Failed;
        }
    }
}
