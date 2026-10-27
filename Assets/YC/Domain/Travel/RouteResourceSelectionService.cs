using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Commands;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Travel
{
    public sealed class RouteResourceSelectionService : IRouteResourceSelectionService
    {
        private readonly IMapQueryService configuredMap;
        private readonly EffectRegistry queryRegistry;
        public RouteResourceSelectionService(IMapQueryService mapQuery = null) { configuredMap = mapQuery; }
        public RouteResourceSelectionService(IMapQueryService mapQuery, EffectRegistry registry) { configuredMap = mapQuery; queryRegistry = registry; }
        internal IMapQueryService Map(GameState state) { return configuredMap ?? new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)); }

        /// <summary>只创建本地预览值；不安装权威会话、请求或资金占用。</summary>
        public ValidationResult TryCreateExplorationPreview(GameState state, int playerId,
            out RouteSelectionSession session, out RouteSelectionDraft draft, out RouteSelectionProjection projection)
            => TryCreateExplorationPreview(state, null, playerId, out session, out draft, out projection);

        public ValidationResult TryCreateExplorationPreview(GameState state, GameStateView publicView, int playerId,
            out RouteSelectionSession session, out RouteSelectionDraft draft, out RouteSelectionProjection projection)
        {
            session = null; draft = null; projection = null;
            if (state == null) return Invalid("route_selection_state_missing");
            var player = state.FindPlayer(playerId);
            if (player == null) return Invalid("route_selection_player_missing");
            var phase = CommandPhasePolicy.ValidatePhase(state, GameCommandKind.ExploreLocation);
            if (!phase.IsValid) return phase;
            if (state.CurrentPlayerId != playerId) return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "当前不是该玩家的行动回合。");
            var budget = new MainActionBudgetService().ValidateCanSpend(state, playerId);
            if (!budget.IsValid) return budget;
            IMapQueryService map;
            try { map = Map(state); }
            catch (ArgumentException) { return Invalid("route_selection_map_mismatch"); }
            if (map.Map.MapId != state.MapId) return Invalid("route_selection_map_mismatch");
            var seed = new RouteSelectionSession { PlayerId = playerId, Purpose = RouteSelectionPurpose.Exploration, ActionContextId = "main_action" };
            bool publicPreview = publicView != null;
            if (publicPreview && (publicView.GameId != state.GameId || publicView.MapId != state.MapId || publicView.ContentPackHash != state.ContentPackHash ||
                publicView.CurrentPlayerId != state.CurrentPlayerId || publicView.Phase != state.Phase || publicView.Round != state.Round ||
                publicView.ViewerRole != GameStateViewerRole.Player || publicView.ViewerPlayerId != playerId || publicView.Decks == null ||
                publicView.StateRevision < 0 || state.EffectRuntime != null && publicView.StateRevision != state.EffectRuntime.StateRevision))
                return Invalid("route_selection_public_view_required");
            if (!publicPreview && state.EffectRuntime == null) return Invalid("route_selection_public_view_required");
            // 带公开投影的调用明确是公开预览；UI可见副本也可能保留轻量运行时。
            var eligible = publicPreview ? PublicExplorationLocations(state, publicView, playerId, map) :
                RouteResourceSelectionEffectExecutor.GetSelectableLocations(state, seed, map);
            if (eligible.Count == 0) return Invalid("route_selection_target_unavailable");
            int observedRevision = publicPreview ? publicView.StateRevision : state.EffectRuntime.StateRevision;
            string previewId = StableIdFactory.Create("exploration_preview", state.GameId, playerId.ToString(CultureInfo.InvariantCulture),
                observedRevision.ToString(CultureInfo.InvariantCulture), player.CityLocationId);
            session = new RouteSelectionSession { SessionId = previewId, OwnerEffectId = "", Purpose = RouteSelectionPurpose.Exploration,
                PlayerId = playerId, MapId = state.MapId, StartLocationId = player.CityLocationId, ActionContextId = "main_action",
                OpenedAtStateRevision = observedRevision, StartGoldVoucherAmount = player.Resources.GoldVoucher,
                BudgetBindingId = "", SelectableLocationUpperBound = Copy(eligible), MinimumLocations = 1, MaximumLocations = 1,
                AllowCancelBeforeCommit = true, Stage = RouteSelectionStage.Selecting };
            draft = new RouteSelectionDraft(previewId, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RouteRecipientChoice>());
            projection = Query(state, session, draft, session.SelectableLocationUpperBound);
            projection.ObservedStateRevision = observedRevision;
            return projection.Validation;
        }

        private static IReadOnlyList<string> PublicExplorationLocations(GameState state, GameStateView publicView, int playerId, IMapQueryService map)
        {
            // 公共预览只判断公开资格；隐藏顶牌的有效选项仍在正式受理时由原探索服务复验。
            var player = state.FindPlayer(playerId);
            if (string.IsNullOrEmpty(player.CityLocationId) || player.InfluenceSupply <= 0) return Array.Empty<string>();
            var tokens = new HashSet<string>(state.Map.ResourceTokens.ToArray().Select(value => value.LocationId), StringComparer.Ordinal);
            var occupied = new HashSet<string>(state.Map.Influences.ToArray().Select(value => value.SlotId), StringComparer.Ordinal);
            var cities = new HashSet<string>(state.Players.Where(value => value.PlayerId != playerId).Select(value => value.CityLocationId), StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var location in map.Map.Locations)
            {
                int remaining = location.EventColor == EventColor.Green ? publicView.Decks.EventDeckGreenCount :
                    location.EventColor == EventColor.Red ? publicView.Decks.EventDeckRedCount : publicView.Decks.EventDeckYellowCount;
                if (remaining <= 0 || tokens.Contains(location.LocationId) || cities.Contains(location.LocationId) ||
                    RedZoneAccessRule.IsClosed(state, map.Map, location)) continue;
                for (int i = 0; i < location.InfluenceSlotCount; i++)
                    if (!occupied.Contains(InfluenceService.GetLocationSlotId(location.LocationId, i))) { result.Add(location.LocationId); break; }
            }
            return result.AsReadOnly();
        }

        public RouteSelectionProjection Query(GameState state, RouteSelectionSession session, RouteSelectionDraft draft, IReadOnlyList<string> selectableLocationIds)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (selectableLocationIds == null) throw new ArgumentNullException(nameof(selectableLocationIds));
            var result = new RouteSelectionProjection { SessionId = session.SessionId, LocalDraftVersion = draft.LocalDraftVersion,
                ObservedStateRevision = state.EffectRuntime?.StateRevision ?? session.OpenedAtStateRevision, Validation = ValidationResult.Success };
            var player = state.FindPlayer(session.PlayerId);
            if (player == null || session.Stage != RouteSelectionStage.Selecting || session.MapId != state.MapId || player.CityLocationId != session.StartLocationId || draft.SessionId != session.SessionId)
                return Failed(result, "route_selection_session_invalid");
            var upper = new HashSet<string>(session.SelectableLocationUpperBound, StringComparer.Ordinal);
            var input = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in selectableLocationIds) if (id == null || !upper.Contains(id) || !input.Add(id)) return Failed(result, "route_selection_input_outside_upper_bound");
            var map = Map(state);
            var facts = new RouteTollService(map).ReadRouteFacts(state, session.PlayerId);
            CollectionPartition partition = null;
            var routeEligible = selectableLocationIds;
            var routeSelected = draft.SelectedLocationIds;
            if (session.SchemaVersion == CollectionPlanService.ProtocolVersion)
            {
                var valid = CollectionPlanService.ValidateCurrent(state, map, session, selectableLocationIds, session.CollectionFreeLocations);
                if (!valid.IsValid) return Failed(result, valid.Reason);
                try { partition = CollectionPlanService.Partition(draft.SelectedLocationIds, session.CollectionFreeLocations); }
                catch (ArgumentException) { return Failed(result, "collection_plan_selection_invalid"); }
                routeEligible = Array.AsReadOnly(selectableLocationIds.Where(id => !session.CollectionFreeLocations.FreeLocationIds.Contains(id)).ToArray());
                routeSelected = partition.RouteRequiredLocationIds;
                result.CollectionFreeLocations = session.CollectionFreeLocations;
            }
            var graph = QueryGraph(map, facts, session.StartLocationId, draft.ConfirmedRouteIds, routeEligible, routeSelected, draft.ConfirmEmptySelection);
            if (partition != null && draft.SelectedLocationIds.Count > 0 && routeSelected.Count == 0 && graph.IsValid)
            {
                // 图的零需求不是玩家的空采集：S仍完整保留，且不改变ConfirmEmptySelection。
                graph.HasFinalPlan = true;
                graph.PrunedRouteIds = graph.OriginalConfirmedRouteIds;
                graph.PrunedFreeRouteIds = graph.AutomaticallySelectedFreeRouteIds;
                graph.EffectiveRouteIds = Array.Empty<string>(); graph.ChargeRouteIds = Array.Empty<string>();
                graph.FinalGoldVoucherAmount = 0;
            }
            result.ReachableLocationIds = graph.ReachableLocationIds;
            result.AutomaticallySelectedFreeRouteIds = graph.AutomaticallySelectedFreeRouteIds;
            result.ProvisionalGoldVoucherAmount = graph.ProvisionalGoldVoucherAmount;
            int available = Math.Max(0, Math.Min(session.StartGoldVoucherAmount, player.Resources.GoldVoucher));
            result.Budget = new RouteSelectionBudget { BudgetBindingId = session.BudgetBindingId, StartGoldVoucherAmount = session.StartGoldVoucherAmount, AvailableSelectionGoldVoucherAmount = available };
            var confirmed = new HashSet<string>(draft.ConfirmedRouteIds, StringComparer.Ordinal);
            var frontier = new HashSet<string>(graph.FrontierRouteIds, StringComparer.Ordinal);
            var free = new HashSet<string>(graph.AutomaticallySelectedFreeRouteIds, StringComparer.Ordinal);
            var kept = new HashSet<string>(graph.EffectiveRouteIds, StringComparer.Ordinal);
            var pruned = new HashSet<string>(graph.PrunedRouteIds, StringComparer.Ordinal);
            var options = new List<RouteSelectionRouteOption>();
            foreach (var fact in facts)
            {
                fact.IsFrontier = frontier.Contains(fact.RouteId); fact.IsOriginallyConfirmed = confirmed.Contains(fact.RouteId);
                fact.IsAutomaticallySelectedFreeRoute = free.Contains(fact.RouteId); fact.IsRetainedInPreview = kept.Contains(fact.RouteId);
                fact.IsPrunedInPreview = pruned.Contains(fact.RouteId); fact.CanAffordIfCharged = fact.TollGoldVoucherAmount <= available;
                options.Add(fact);
            }
            result.RouteOptions = options.AsReadOnly();
            var reachable = new HashSet<string>(graph.ReachableLocationIds, StringComparer.Ordinal);
            var selected = new HashSet<string>(draft.SelectedLocationIds, StringComparer.Ordinal);
            var witness = graph.HasFinalPlan ? Witness(map, facts, graph.EffectiveRouteIds, session.StartLocationId) : Array.AsReadOnly(new RouteConnectivityLink[0]);
            var previewReachable = new HashSet<string>(witness.Select(link => link.LocationId), StringComparer.Ordinal);
            if (graph.HasFinalPlan) previewReachable.Add(session.StartLocationId);
            var locationOptions = new List<RouteSelectionLocationOption>();
            foreach (string id in selectableLocationIds) locationOptions.Add(new RouteSelectionLocationOption { LocationId = id, IsSelected = selected.Contains(id), IsReachableInOriginalDraft = reachable.Contains(id), IsReachableInPreview = previewReachable.Contains(id), IsFreeCollection = partition != null && session.CollectionFreeLocations.FreeLocationIds.Contains(id) });
            result.LocationOptions = locationOptions.AsReadOnly();
            if (!graph.IsValid) return Failed(result, graph.Diagnostic);
            if (!graph.HasFinalPlan) return result;
            ValidationResult validation = draft.SelectedLocationIds.Count < session.MinimumLocations || draft.SelectedLocationIds.Count > session.MaximumLocations ? Invalid("route_selection_target_count") : ValidationResult.Success;
            var recipientMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var choice in draft.RecipientChoices)
            {
                if (choice == null || !confirmed.Contains(choice.RouteId) || recipientMap.ContainsKey(choice.RouteId)) return Failed(result, "route_selection_recipient_structure");
                recipientMap.Add(choice.RouteId, choice.ReceiverSubjectKey);
            }
            var factsById = facts.ToDictionary(f => f.RouteId, StringComparer.Ordinal);
            foreach (var choice in draft.RecipientChoices)
                if (factsById[choice.RouteId].FreeReason != RouteFreeReason.None || !factsById[choice.RouteId].EligibleRecipients.Any(r => r.SubjectKey == choice.ReceiverSubjectKey)) return Failed(result, "route_selection_recipient_invalid");
            var lines = new List<RouteTollPlanLine>();
            foreach (string routeId in graph.ChargeRouteIds)
            {
                var fact = factsById[routeId];
                string subject;
                if (!recipientMap.TryGetValue(routeId, out subject))
                {
                    if (fact.EligibleRecipients.Count == 1) subject = fact.EligibleRecipients[0].SubjectKey;
                    else { validation = Invalid("route_selection_recipient_required"); continue; }
                }
                lines.Add(new RouteTollPlanLine { RouteId = routeId, ReceiverSubjectKey = subject, GoldVoucherAmount = fact.TollGoldVoucherAmount });
            }
            if (available < graph.FinalGoldVoucherAmount) validation = ValidationResult.Failure(CommandErrorCode.InsufficientResource, "开始金券或当前可用金券不足。");
            var preview = new RouteSelectionPreview { ScopeKey = session.ActiveRequestBinding?.ScopeKey ?? "", SelectedLocationIds = Copy(draft.SelectedLocationIds), Collection = partition,
                OriginalConfirmedRouteIds = graph.OriginalConfirmedRouteIds, PrunedRouteIds = graph.PrunedRouteIds, EffectiveRouteIds = graph.EffectiveRouteIds,
                ChargeRouteIds = graph.ChargeRouteIds, PrunedFreeRouteIds = graph.PrunedFreeRouteIds, ConnectivityWitness = witness,
                PaymentPlan = new RouteTollPlan { Lines = lines.AsReadOnly(), TotalGoldVoucherAmount = graph.FinalGoldVoucherAmount }, FinalizationValidation = validation,
                RemainingStartGoldVoucherAmount = Math.Max(0, session.StartGoldVoucherAmount - graph.FinalGoldVoucherAmount) };
            preview.PlanDigest = PlanDigest(session, preview);
            result.Preview = preview;
            return result;
        }

        public ValidationResult TryValidatePlan(GameState state, RouteSelectionSession session, RouteSelectionRequestBinding requestBinding,
            RouteSelectionDraft draft, IReadOnlyList<string> selectableLocationIds, out RouteSelectionPreview preview)
            => TryValidatePlanCore(state, session, requestBinding, draft, selectableLocationIds, false, out preview);

        internal ValidationResult TryValidateNormalHarvestPlan(GameState state, RouteSelectionSession session, RouteSelectionRequestBinding requestBinding,
            RouteSelectionDraft draft, IReadOnlyList<string> selectableLocationIds, out RouteSelectionPreview preview)
        {
            preview = null;
            if (state == null || session == null || !InteractionRequestValidity.IsNormalHarvestOwner(state, state.EffectRuntime?.EffectNodes.Find(n => n.EffectId == session.OwnerEffectId)))
                return Invalid("normal_harvest_request_owner_invalid");
            return TryValidatePlanCore(state, session, requestBinding, draft, selectableLocationIds, true, out preview);
        }

        private ValidationResult TryValidatePlanCore(GameState state, RouteSelectionSession session, RouteSelectionRequestBinding requestBinding,
            RouteSelectionDraft draft, IReadOnlyList<string> selectableLocationIds, bool concurrentPreflight, out RouteSelectionPreview preview)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (requestBinding == null) throw new ArgumentNullException(nameof(requestBinding));
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (selectableLocationIds == null) throw new ArgumentNullException(nameof(selectableLocationIds));
            preview = null;
            if (session.ActiveRequestBinding == null || requestBinding.SessionId != session.SessionId || requestBinding.ScopeKey != session.ActiveRequestBinding.ScopeKey
                || !(concurrentPreflight ? MatchesConcurrentRequestFacts(state, session, requestBinding, selectableLocationIds) :
                    MatchesRequestFacts(state, session, requestBinding, selectableLocationIds))) return Invalid("route_selection_facts_changed");
            var projection = Query(state, session, draft, selectableLocationIds);
            if (!projection.Validation.IsValid) return projection.Validation;
            if (projection.Preview == null) return Invalid("route_selection_target_required");
            if (!projection.Preview.FinalizationValidation.IsValid) return projection.Preview.FinalizationValidation;
            preview = projection.Preview;
            return ValidationResult.Success;
        }

        public ValidationResult TryFinalize(GameState state, RouteSelectionSession session, RouteSelectionRequestBinding requestBinding,
            RouteSelectionDraft draft, IReadOnlyList<string> selectableLocationIds, out RouteSelectionCommitment commitment)
        {
            commitment = null;
            var validation = TryValidatePlan(state, session, requestBinding, draft, selectableLocationIds, out var preview);
            if (!validation.IsValid) return validation;
            if (state.EffectRuntime == null) return Invalid("route_selection_host_runtime_required");
            if (session.SchemaVersion == CollectionPlanService.ProtocolVersion)
            {
                try
                {
                    var current = new CollectionFreeLocationQuery(queryRegistry, Map(state)).Query(state,
                        CollectionPlanService.Context(state, session, selectableLocationIds));
                    if (current.ResultDigest != preview.Collection.FreeLocations.ResultDigest) return Invalid("collection_free_facts_changed");
                }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
                { return Invalid(error.Message); }
            }
            commitment = new RouteSelectionCommitment { CommitmentId = StableIdFactory.Create("route_selection_commitment", session.SessionId, preview.PlanDigest),
                SessionId = session.SessionId, OwnerEffectId = session.OwnerEffectId, PlayerId = session.PlayerId, AcceptedAtStateRevision = state.EffectRuntime.StateRevision,
                BudgetBindingId = session.BudgetBindingId, AcceptedPreview = preview };
            return ValidationResult.Success;
        }

        internal bool MatchesRequestFacts(GameState state, RouteSelectionSession session, RouteSelectionRequestBinding binding, IReadOnlyList<string> selectable)
        {
            if (binding.FrozenFactsDigest == FactsDigest(state, session, selectable)) return true;
            // 原公开计划入口仅在可信新答案留下接受事实后允许观察差异；旧无标答案仍按旧facts精确校验。
            var owner = state.EffectRuntime?.EffectNodes.Find(n => n.EffectId == session.OwnerEffectId);
            var proof = RouteSelectionSessionAuthority.Value(owner?.ContinuationData, InteractionRequestValidity.AcceptanceRecord);
            if (RouteSelectionSessionAuthority.Integer(proof, InteractionRequestValidity.AcceptedVersionParameter) != InteractionRequestValidity.ContractVersion ||
                RouteSelectionSessionAuthority.Text(proof, InteractionRequestValidity.AcceptedDigestParameter).Length != 64 ||
                RouteSelectionSessionAuthority.Text(proof, InteractionRequestValidity.AcceptedCommandParameter).Length == 0 ||
                owner?.ContinuationOperationContext?.SourceCommandId != RouteSelectionSessionAuthority.Text(proof, InteractionRequestValidity.AcceptedCommandParameter) ||
                owner.ContinuationOperationContext.ActingPlayerId != session.PlayerId) return false;
            var accepted = state.EffectRuntime.InteractionRequests.Find(r => r.OwnerEffectId == session.OwnerEffectId && r.Status == "answered" &&
                r.InteractionTypeId == "route_resource.selection" && r.CandidateSetId == binding.CandidateSetId && r.CandidateSetVersion == binding.CandidateSetVersion &&
                r.GetStableInteractionId() == RouteSelectionSessionAuthority.Text(proof, "interactionId"));
            return accepted != null && MatchesConcurrentRequestFacts(state, session, binding, selectable);
        }

        internal bool MatchesConcurrentRequestFacts(GameState state, RouteSelectionSession session, RouteSelectionRequestBinding binding, IReadOnlyList<string> selectable)
        {
            if (binding.FrozenFactsDigest == FactsDigest(state, session, selectable)) return true;
            var owner = state.EffectRuntime?.EffectNodes.Find(n => n.EffectId == session.OwnerEffectId);
            if (!InteractionRequestValidity.IsNormalHarvestOwner(state, owner)) return false;
            // 旧scope/facts字节保持。只复验并发投影CurrentPlayer的可能旧值，其余原facts逐字段参与同一摘要。
            // 不改当前State、不重签scope，也不把真实路线、资源token、影响力或费用变动归为观察变化。
            return state.Players.Select(p => p.PlayerId).Concat(new[] { -1 }).Distinct()
                .Any(player => binding.FrozenFactsDigest == FactsDigest(state, session, selectable, player));
        }

        internal string FactsDigest(GameState state, RouteSelectionSession session, IReadOnlyList<string> selectable)
            => FactsDigest(state, session, selectable, state.CurrentPlayerId);

        private string FactsDigest(GameState state, RouteSelectionSession session, IReadOnlyList<string> selectable, int observationPlayer)
        {
            var map = Map(state); var facts = new RouteTollService(map).ReadRouteFacts(state, session.PlayerId);
            var fields = new List<string> { "route_selection.v1", session.OwnerEffectId, session.Purpose.ToString(), session.PlayerId.ToString(CultureInfo.InvariantCulture), state.MapId,
                state.FindPlayer(session.PlayerId)?.CityLocationId ?? "", session.ActionContextId, session.MinimumLocations.ToString(CultureInfo.InvariantCulture), session.MaximumLocations.ToString(CultureInfo.InvariantCulture), session.BudgetBindingId };
            AddList(fields, "upper", session.SelectableLocationUpperBound); AddList(fields, "eligible", selectable);
            fields.Add(state.Phase.ToString()); fields.Add(observationPlayer.ToString(CultureInfo.InvariantCulture)); fields.Add(state.Round.ToString(CultureInfo.InvariantCulture));
            fields.Add("routeFacts"); fields.Add(facts.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var fact in facts)
            {
                fields.Add(fact.RouteId); AddList(fields, "coverage", fact.CoveredLocationIds); fields.Add(fact.FreeReason.ToString()); fields.Add(fact.TollGoldVoucherAmount.ToString(CultureInfo.InvariantCulture));
                AddList(fields, "recipients", fact.EligibleRecipients.Select(r => r.SubjectKey).ToArray());
            }
            var tokens = state.Map.ResourceTokens.ToArray(); fields.Add("tokens"); fields.Add(tokens.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var token in tokens) { fields.Add(token.LocationId); fields.Add(token.ResourceType.ToString()); fields.Add(token.Amount.ToString(CultureInfo.InvariantCulture)); }
            var influences = state.Map.Influences.ToArray(); fields.Add("influences"); fields.Add(influences.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var influence in influences)
            {
                fields.Add(influence.InfluenceId); fields.Add(influence.SlotId); fields.Add(influence.LocationId); fields.Add(influence.RouteId); fields.Add(influence.PlayerId.ToString(CultureInfo.InvariantCulture));
                fields.Add(influence.OwnerSubject?.SubjectType ?? ""); fields.Add(influence.OwnerSubject?.InstanceId ?? "");
            }
            fields.Add(state.FindPlayer(session.PlayerId)?.HasCollectedResourcesThisRound.ToString() ?? "");
            if (session.SchemaVersion == CollectionPlanService.ProtocolVersion)
            {
                fields.Add("collection.plan.v2");
                fields.Add(CollectionFreeLocationQuery.FactsDigest(state, CollectionPlanService.Context(state, session, selectable), map,
                    session.CollectionFreeLocations?.QuerySources.Select(s => s.SourceRef).ToArray()));
                fields.Add(session.CollectionFreeLocations?.ResultDigest ?? "");
            }
            return Hash(fields);
        }

        internal static RouteGraphResult QueryGraph(IMapQueryService map, IReadOnlyList<RouteSelectionRouteOption> facts, string start, IReadOnlyList<string> confirmed, IReadOnlyList<string> eligible, IReadOnlyList<string> selected, bool empty)
        {
            return RouteSelectionGraphQuery.Query(map.Map.Locations.Select(l => l.LocationId).ToArray(), facts.Select(f => new RouteGraphRoute(f.RouteId, f.CoveredLocationIds, f.FreeReason != RouteFreeReason.None, f.TollGoldVoucherAmount)).ToArray(), start, confirmed, eligible, selected, empty);
        }

        internal static string PlanDigest(RouteSelectionSession session, RouteSelectionPreview preview)
        {
            var fields = new List<string> { "route_selection.v1", preview.ScopeKey, session.SessionId, session.MapId, session.StartLocationId, session.BudgetBindingId, session.StartGoldVoucherAmount.ToString(CultureInfo.InvariantCulture) };
            AddList(fields, "targets", DigestIds(session, preview.SelectedLocationIds)); AddList(fields, "original", DigestIds(session, preview.OriginalConfirmedRouteIds)); AddList(fields, "pruned", DigestIds(session, preview.PrunedRouteIds));
            AddList(fields, "effective", DigestIds(session, preview.EffectiveRouteIds)); AddList(fields, "charge", DigestIds(session, preview.ChargeRouteIds)); AddList(fields, "prunedFree", DigestIds(session, preview.PrunedFreeRouteIds));
            fields.Add("paymentLines"); fields.Add(preview.PaymentPlan.Lines.Count.ToString(CultureInfo.InvariantCulture));
            IEnumerable<RouteTollPlanLine> paymentLines = preview.PaymentPlan.Lines;
            if (session.SchemaVersion == CollectionPlanService.ProtocolVersion) paymentLines = paymentLines.OrderBy(l => l.RouteId, StringComparer.Ordinal);
            foreach (var line in paymentLines) { fields.Add(line.RouteId); fields.Add(line.ReceiverSubjectKey); fields.Add(line.GoldVoucherAmount.ToString(CultureInfo.InvariantCulture)); }
            fields.Add(preview.PaymentPlan.TotalGoldVoucherAmount.ToString(CultureInfo.InvariantCulture));
            if (session.SchemaVersion == CollectionPlanService.ProtocolVersion)
            {
                fields.Add("collection.plan.v2"); fields.Add(preview.Collection?.FreeLocations.ResultDigest ?? "");
                if (preview.Collection != null)
                {
                    AddList(fields, "selectedFree", DigestIds(session, preview.Collection.SelectedFreeLocationIds));
                    AddList(fields, "routeRequired", DigestIds(session, preview.Collection.RouteRequiredLocationIds));
                }
            }
            return Hash(fields);
        }

        private static IReadOnlyList<string> DigestIds(RouteSelectionSession session, IReadOnlyList<string> ids)
            => session.SchemaVersion == CollectionPlanService.ProtocolVersion
                ? Array.AsReadOnly(ids.OrderBy(x => x, StringComparer.Ordinal).ToArray()) : ids;

        private static void AddList(List<string> fields, string name, IReadOnlyList<string> values)
        {
            fields.Add(name); fields.Add(values.Count.ToString(CultureInfo.InvariantCulture)); fields.AddRange(values);
        }

        internal static string Hash(IEnumerable<string> fields)
        {
            var text = new StringBuilder();
            foreach (string field in fields) { string value = field ?? ""; text.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        internal static IReadOnlyList<string> Copy(IReadOnlyList<string> values) { return Array.AsReadOnly(values.ToArray()); }
        internal static ValidationResult Invalid(string reason) { return ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason); }
        private static RouteSelectionProjection Failed(RouteSelectionProjection result, string reason) { result.Validation = Invalid(reason); return result; }

        private static IReadOnlyList<RouteConnectivityLink> Witness(IMapQueryService map, IReadOnlyList<RouteSelectionRouteOption> facts, IReadOnlyList<string> effective, string start)
        {
            var retained = new HashSet<string>(effective, StringComparer.Ordinal);
            var adjacency = new Dictionary<string, List<RouteSelectionRouteOption>>(StringComparer.Ordinal);
            foreach (var location in map.Map.Locations) adjacency.Add(location.LocationId, new List<RouteSelectionRouteOption>());
            foreach (var route in facts) if (retained.Contains(route.RouteId)) foreach (string location in route.CoveredLocationIds) adjacency[location].Add(route);
            var visited = new HashSet<string>(StringComparer.Ordinal) { start }; var usedRoutes = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<string>(); queue.Enqueue(start);
            var result = new List<RouteConnectivityLink>();
            while (queue.Count > 0) { string current = queue.Dequeue(); foreach (var route in adjacency[current]) if (usedRoutes.Add(route.RouteId)) foreach (string location in route.CoveredLocationIds) if (visited.Add(location)) { result.Add(new RouteConnectivityLink { LocationId = location, ParentLocationId = current, ViaRouteId = route.RouteId }); queue.Enqueue(location); } }
            return result.AsReadOnly();
        }

        internal static IReadOnlyList<RouteConnectivityLink> BuildConnectivityWitness(IMapQueryService map, IReadOnlyList<string> effective, string start)
        {
            // 恢复只读取定义拓扑；绝不把事件后影响力、公路或钱包作为原费用的来源。
            var facts = new List<RouteSelectionRouteOption>();
            foreach (var route in map.Map.Routes)
            {
                var locations = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                if (route.CoveredLocationIds != null) foreach (string id in route.CoveredLocationIds) if (!string.IsNullOrEmpty(id) && seen.Add(id)) locations.Add(id);
                foreach (string id in new[] { route.FromLocationId, route.ToLocationId }) if (!string.IsNullOrEmpty(id) && seen.Add(id)) locations.Add(id);
                facts.Add(new RouteSelectionRouteOption { RouteId = route.RouteId, CoveredLocationIds = locations.AsReadOnly() });
            }
            return Witness(map, facts.AsReadOnly(), effective, start);
        }
    }
}
