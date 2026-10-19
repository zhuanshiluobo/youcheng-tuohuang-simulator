using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Travel
{
    public sealed class RouteSelectionSessionAuthority : IRouteSelectionSessionAuthority, IRouteSelectionClientViewReader
    {
        private readonly IMapQueryService configuredMap;
        public RouteSelectionSessionAuthority(IMapQueryService mapQuery = null) { configuredMap = mapQuery; }
        private RouteResourceSelectionService Service { get { return new RouteResourceSelectionService(configuredMap); } }

        public ValidationResult TryOpen(GameState state, EffectNodeRuntimeState ownerNode, RouteSelectionOptions options, out RouteSelectionSession session)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (ownerNode == null) throw new ArgumentNullException(nameof(ownerNode));
            if (options == null) throw new ArgumentNullException(nameof(options));
            session = null;
            if (!IsActualNode(state, ownerNode) || ownerNode.PlayerId != options.PlayerId
                || (int)options.Purpose < 0 || (int)options.Purpose > 2 || options.SelectableLocationUpperBound == null || options.MinimumLocations < 0 || options.MaximumLocations < options.MinimumLocations) return Invalid("route_selection_owner_invalid");
            var existing = Value(ownerNode.ContinuationData, "routeSelection");
            if (existing != null) return TryRestore(state, ownerNode, out session, out _);
            var player = state.FindPlayer(options.PlayerId);
            if (player == null || player.CityLocationId != options.StartLocationId || player.Resources.GoldVoucher < 0) return Invalid("route_selection_start_invalid");
            int startGold = player.Resources.GoldVoucher;
            if (options.Purpose == RouteSelectionPurpose.NormalHarvest)
            {
                if (player.ResourceCollectionStartGoldVoucher < 0) return Invalid("route_selection_collection_budget_missing");
                startGold = player.ResourceCollectionStartGoldVoucher;
            }
            else if (options.Purpose == RouteSelectionPurpose.ExtraHarvest)
            {
                var parent = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == options.ExistingStartBudgetBindingId);
                if (parent == null || ownerNode.ParentEffectId != parent.EffectId) return Invalid("route_selection_extra_budget_owner_invalid");
                var amounts = Value(parent.ContinuationData, "collectionStartGoldVoucherAmounts");
                var amount = amounts?.Items?.Find(v => Integer(v, "playerId", -1) == options.PlayerId);
                if (amount == null) return Invalid("route_selection_extra_budget_missing");
                startGold = Integer(amount, "amount", -1);
            }
            if (startGold < 0) return Invalid("route_selection_start_budget_invalid");
            var map = Service.Map(state);
            var upper = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in options.SelectableLocationUpperBound)
                if (id == null || !map.Map.Locations.Any(l => l.LocationId == id) || !upper.Add(id)) return Invalid("route_selection_upper_bound_invalid");
            session = new RouteSelectionSession { SessionId = StableIdFactory.Create("route_selection_session", ownerNode.EffectId, "1"), OwnerEffectId = ownerNode.EffectId,
                Purpose = options.Purpose, PlayerId = player.PlayerId, MapId = state.MapId, StartLocationId = options.StartLocationId, ActionContextId = options.ActionContextId ?? "",
                OpenedAtStateRevision = state.EffectRuntime.StateRevision, StartGoldVoucherAmount = startGold,
                BudgetBindingId = StableIdFactory.Create("route_selection_budget", ownerNode.EffectId, options.ExistingStartBudgetBindingId ?? ""),
                SelectableLocationUpperBound = RouteResourceSelectionService.Copy(options.SelectableLocationUpperBound), MinimumLocations = options.MinimumLocations, MaximumLocations = options.MaximumLocations,
                AllowCancelBeforeCommit = options.AllowCancelBeforeCommit, Stage = RouteSelectionStage.Selecting };
            try
            {
                var saved = PutRecord(ownerNode, SessionRecord(session));
                if (!saved.IsValid) session = null;
                return saved;
            }
            catch (ArgumentException) { session = null; return Invalid("route_selection_record_too_large"); }
        }

        public ValidationResult TryRestore(GameState state, EffectNodeRuntimeState ownerNode, out RouteSelectionSession session, out RouteSelectionRequestBinding requestBinding)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (ownerNode == null) throw new ArgumentNullException(nameof(ownerNode));
            session = null; requestBinding = null;
            if (!IsActualNode(state, ownerNode)) return Invalid("route_selection_owner_invalid");
            var record = Value(ownerNode.ContinuationData, "routeSelection");
            if (!TryReadSession(record, out session) || session.SessionId != StableIdFactory.Create("route_selection_session", ownerNode.EffectId, "1") || session.OwnerEffectId != ownerNode.EffectId
                || session.PlayerId != ownerNode.PlayerId || state.FindPlayer(session.PlayerId) == null || session.MapId != state.MapId) { session = null; return Invalid("route_selection_restore_invalid"); }
            var locations = new HashSet<string>(Service.Map(state).Map.Locations.Select(l => l.LocationId), StringComparer.Ordinal);
            if (!locations.Contains(session.StartLocationId) || session.SelectableLocationUpperBound.Any(id => !locations.Contains(id))) { session = null; return Invalid("route_selection_restore_location_invalid"); }
            requestBinding = session.ActiveRequestBinding;
            return ValidationResult.Success;
        }

        /// <summary>正常并发采集的只读准入复验；不Finalize/Seal、不改scope或答案。</summary>
        public ValidationResult TryValidateNormalHarvestRequest(GameState state, EffectNodeRuntimeState ownerNode,
            InteractionRequest request, IReadOnlyList<string> selectedCandidateIds)
        {
            if (!InteractionRequestValidity.IsNormalHarvestOwner(state, ownerNode) || request?.Status != "open" ||
                request.InteractionTypeId != "route_resource.selection" ||
                !state.EffectRuntime.InteractionRequests.Any(r => ReferenceEquals(r, request)) ||
                request.OwnerEffectId != ownerNode.EffectId || request.AnsweringPlayerId != ownerNode.PlayerId)
                return Invalid("normal_harvest_request_owner_invalid");
            var restored = TryRestore(state, ownerNode, out var session, out var binding);
            if (!restored.IsValid) return restored;
            var player = state.FindPlayer(session.PlayerId);
            if (session.Purpose != RouteSelectionPurpose.NormalHarvest || session.Stage != RouteSelectionStage.Selecting || binding == null ||
                player.CityLocationId != session.StartLocationId || player.HasCollectedResourcesThisRound ||
                player.ResourceCollectionStartGoldVoucher != session.StartGoldVoucherAmount ||
                binding.CandidateSetId != request.CandidateSetId || binding.CandidateSetVersion != request.CandidateSetVersion ||
                binding.ScopeKey != Text(request.PromptParameters, "scopeKey") || binding.FrozenFactsDigest != Text(request.PromptParameters, "factsDigest"))
                return Invalid("normal_harvest_request_binding_changed");
            var map = Service.Map(state);
            var eligible = YC.Domain.Effects.RouteResourceSelectionEffectExecutor.GetSelectableLocations(state, session, map)
                .Where(id => session.SelectableLocationUpperBound.Contains(id)).ToArray();
            if (!Service.MatchesConcurrentRequestFacts(state, session, binding, eligible)) return Invalid("route_selection_facts_changed");
            if (selectedCandidateIds == null) return ValidationResult.Success;
            var parsed = new RouteSelectionAnswerCodec().TryParse(request, selectedCandidateIds, out var answer);
            if (!parsed.IsValid) return parsed;
            if (answer.IsCancellation) return Invalid("normal_harvest_cannot_cancel");
            return Service.TryValidateNormalHarvestPlan(state, session, binding, answer.Draft, eligible, out _);
        }

        public ValidationResult TryPrepareRequest(GameState state, EffectNodeRuntimeState ownerNode, RouteSelectionSession session, IReadOnlyList<string> selectableLocationIds, out RouteSelectionRequestBinding requestBinding, out IReadOnlyList<string> candidateIds)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (ownerNode == null) throw new ArgumentNullException(nameof(ownerNode));
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (selectableLocationIds == null) throw new ArgumentNullException(nameof(selectableLocationIds));
            requestBinding = null; candidateIds = null;
            if (!IsActualNode(state, ownerNode) || session.OwnerEffectId != ownerNode.EffectId || session.Stage != RouteSelectionStage.Selecting) return Invalid("route_selection_owner_invalid");
            if (selectableLocationIds.Distinct(StringComparer.Ordinal).Count() != selectableLocationIds.Count || selectableLocationIds.Any(id => id == null || !session.SelectableLocationUpperBound.Contains(id))) return Invalid("route_selection_input_outside_upper_bound");
            if ((session.ActiveRequestBinding?.CandidateSetVersion ?? 0) >= NormalizedValue.MaxInteger) return Invalid("route_selection_version_exhausted");
            int version = (session.ActiveRequestBinding?.CandidateSetVersion ?? 0) + 1;
            string facts = Service.FactsDigest(state, session, selectableLocationIds);
            string scope = RouteResourceSelectionService.Hash(new[] { session.SessionId, version.ToString(CultureInfo.InvariantCulture), facts });
            requestBinding = new RouteSelectionRequestBinding { SessionId = session.SessionId, CandidateSetId = StableIdFactory.Create("route_selection_candidates", session.SessionId),
                CandidateSetVersion = version, ScopeKey = scope, FrozenFactsDigest = facts, RequestStateRevision = state.EffectRuntime.StateRevision + 1 };
            var ids = new List<string> { RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit) };
            if (session.AllowCancelBeforeCommit) ids.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Cancel));
            foreach (string location in selectableLocationIds) ids.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Location, location));
            foreach (var route in new RouteTollService(Service.Map(state)).ReadRouteFacts(state, session.PlayerId))
                foreach (var receiver in route.EligibleRecipients) ids.Add(RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.RouteAndRecipient, route.RouteId, receiver.SubjectKey));
            if (ids.Any(id => System.Text.Encoding.UTF8.GetByteCount(id) > NormalizedValue.MaxStringBytes)) { requestBinding = null; return Invalid("route_selection_candidate_too_large"); }
            var previousBinding = session.ActiveRequestBinding;
            session.ActiveRequestBinding = requestBinding;
            ValidationResult saved;
            try { saved = PutRecord(ownerNode, SessionRecord(session)); }
            catch (ArgumentException) { saved = Invalid("route_selection_record_too_large"); }
            if (!saved.IsValid) { requestBinding = null; session.ActiveRequestBinding = previousBinding; return saved; }
            candidateIds = ids.AsReadOnly();
            return ValidationResult.Success;
        }

        // 新factory只携带选择引用；托管执行器从这里取得原封存值。
        public ValidationResult TryResolveFinalized(GameState state, string selectionOwnerEffectId, out RouteSelectionSession session, out RouteSelectionCommitment commitment)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (selectionOwnerEffectId == null) throw new ArgumentNullException(nameof(selectionOwnerEffectId));
            session = null; commitment = null;
            var owner = state.EffectRuntime?.EffectNodes.Find(n => n.EffectId == selectionOwnerEffectId);
            if (owner == null) return Invalid("route_selection_owner_missing");
            var restored = TryRestore(state, owner, out session, out _);
            if (!restored.IsValid) return restored;
            if (session.Stage != RouteSelectionStage.Committed && session.Stage != RouteSelectionStage.Settled) return Invalid("route_selection_not_finalized");
            var record = Value(owner.ContinuationData, "routeSelection");
            try
            {
                var lines = new List<RouteTollPlanLine>();
                foreach (string line in Text(record, "paymentLines").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split('~');
                    int amount;
                    if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out amount) || amount != RouteTollService.RouteCostGoldVoucher) return Invalid("route_selection_saved_plan_invalid");
                    string routeId, subject;
                    if (!RouteSelectionAnswerCodec.TryDecodeField(parts[0], out routeId) || !RouteSelectionAnswerCodec.TryDecodeField(parts[1], out subject)) return Invalid("route_selection_saved_encoding_invalid");
                    int receiver;
                    if (subject != "bank" && (!subject.StartsWith("player:", StringComparison.Ordinal) || !int.TryParse(subject.Substring(7), out receiver) || state.FindPlayer(receiver) == null)) return Invalid("route_selection_saved_receiver_invalid");
                    lines.Add(new RouteTollPlanLine { RouteId = routeId, ReceiverSubjectKey = subject, GoldVoucherAmount = amount });
                }
                var original = Decode(Text(record, "originalRoutes")); var effective = Decode(Text(record, "effectiveRoutes")); var charge = Decode(Text(record, "chargeRoutes"));
                var pruned = original.Where(id => !effective.Contains(id)).ToArray();
                if (original.Distinct(StringComparer.Ordinal).Count() != original.Count || effective.Distinct(StringComparer.Ordinal).Count() != effective.Count || charge.Distinct(StringComparer.Ordinal).Count() != charge.Count
                    || charge.Any(id => !original.Contains(id) || !effective.Contains(id)) || !lines.Select(l => l.RouteId).SequenceEqual(charge) || lines.Sum(l => l.GoldVoucherAmount) != Integer(record, "totalCost", -1)
                    || lines.Sum(l => l.GoldVoucherAmount) > session.StartGoldVoucherAmount) return Invalid("route_selection_saved_plan_invalid");
                var map = Service.Map(state);
                var knownRoutes = new HashSet<string>(map.Map.Routes.Select(r => r.RouteId), StringComparer.Ordinal);
                if (original.Any(id => !knownRoutes.Contains(id)) || effective.Any(id => !knownRoutes.Contains(id))) return Invalid("route_selection_saved_route_unknown");
                var preview = new RouteSelectionPreview { ScopeKey = session.ActiveRequestBinding.ScopeKey, SelectedLocationIds = Decode(Text(record, "selectedLocations")), OriginalConfirmedRouteIds = original,
                    PrunedRouteIds = Array.AsReadOnly(pruned), EffectiveRouteIds = effective, ChargeRouteIds = charge, PrunedFreeRouteIds = Decode(Text(record, "prunedFreeRoutes")), ConnectivityWitness = RouteResourceSelectionService.BuildConnectivityWitness(map, effective, session.StartLocationId),
                    PaymentPlan = new RouteTollPlan { Lines = lines.AsReadOnly(), TotalGoldVoucherAmount = Integer(record, "totalCost", -1) }, FinalizationValidation = ValidationResult.Success,
                    RemainingStartGoldVoucherAmount = session.StartGoldVoucherAmount - Integer(record, "totalCost", -1), PlanDigest = Text(record, "planDigest") };
                var reachable = new HashSet<string>(preview.ConnectivityWitness.Select(link => link.LocationId), StringComparer.Ordinal) { session.StartLocationId };
                var upperBound = session.SelectableLocationUpperBound;
                if (preview.SelectedLocationIds.Count < session.MinimumLocations || preview.SelectedLocationIds.Count > session.MaximumLocations
                    || preview.SelectedLocationIds.Distinct(StringComparer.Ordinal).Count() != preview.SelectedLocationIds.Count
                    || preview.PrunedFreeRouteIds.Distinct(StringComparer.Ordinal).Count() != preview.PrunedFreeRouteIds.Count
                    || preview.PrunedFreeRouteIds.Any(id => !knownRoutes.Contains(id) || effective.Contains(id) || charge.Contains(id))
                    || RouteResourceSelectionService.PlanDigest(session, preview) != preview.PlanDigest || preview.SelectedLocationIds.Any(id => !upperBound.Contains(id) || !reachable.Contains(id))) return Invalid("route_selection_saved_digest_invalid");
                commitment = new RouteSelectionCommitment { CommitmentId = Text(record, "commitmentId"), SessionId = session.SessionId, OwnerEffectId = session.OwnerEffectId, PlayerId = session.PlayerId,
                    BudgetBindingId = session.BudgetBindingId, AcceptedAtStateRevision = Integer(record, "acceptedRevision", -1), AcceptedPreview = preview };
                if (commitment.CommitmentId != StableIdFactory.Create("route_selection_commitment", session.SessionId, preview.PlanDigest)) return Invalid("route_selection_saved_commitment_invalid");
                return ValidationResult.Success;
            }
            catch (UriFormatException) { return Invalid("route_selection_saved_encoding_invalid"); }
            catch (ArgumentException) { return Invalid("route_selection_saved_encoding_invalid"); }
            catch (OverflowException) { return Invalid("route_selection_saved_integer_invalid"); }
        }

        public ValidationResult TryClaimFinalized(GameState state, EffectNodeRuntimeState consumerNode, string selectionOwnerEffectId, string expectedCommitmentId, out RouteSelectionSession session, out RouteSelectionCommitment commitment)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (consumerNode == null) throw new ArgumentNullException(nameof(consumerNode));
            if (selectionOwnerEffectId == null) throw new ArgumentNullException(nameof(selectionOwnerEffectId));
            if (expectedCommitmentId == null) throw new ArgumentNullException(nameof(expectedCommitmentId));
            var result = TryResolveFinalized(state, selectionOwnerEffectId, out session, out commitment);
            if (!result.IsValid) return result;
            if (!IsActualNode(state, consumerNode) || consumerNode.PlayerId != session.PlayerId || commitment.CommitmentId != expectedCommitmentId
                || session.Purpose != RouteSelectionPurpose.Exploration || consumerNode.EffectTypeId != YC.Domain.Effects.ExplorationEffectTypeIds.Explore) return Invalid("route_selection_consumer_invalid");
            var ancestor = consumerNode;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (ancestor != null && ancestor.EffectId != selectionOwnerEffectId && seen.Add(ancestor.EffectId)) ancestor = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == ancestor.ParentEffectId);
            if (ancestor == null || ancestor.EffectId != selectionOwnerEffectId || consumerNode.EffectId == selectionOwnerEffectId) return Invalid("route_selection_consumer_not_descendant");
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == selectionOwnerEffectId);
            var record = Value(owner.ContinuationData, "routeSelection").Clone();
            string claimant = Text(record, "consumerEffectId");
            if (claimant.Length > 0 && claimant != consumerNode.EffectId) return Invalid("route_selection_already_claimed");
            Set(record, "consumerEffectId", NormalizedValue.CreateString(consumerNode.EffectId));
            return PutRecord(owner, record);
        }

        internal ValidationResult Seal(GameState state, EffectNodeRuntimeState owner, RouteSelectionSession session, RouteSelectionCommitment commitment)
        {
            var preview = commitment.AcceptedPreview;
            if (!IsActualNode(state, owner) || session.Stage != RouteSelectionStage.Selecting || commitment.OwnerEffectId != owner.EffectId || commitment.SessionId != session.SessionId) return Invalid("route_selection_seal_invalid");
            var record = SessionRecord(session);
            Set(record, "stage", NormalizedValue.CreateInteger((int)RouteSelectionStage.Committed));
            try
            {
                foreach (var pair in new[] { Tuple.Create("commitmentId", commitment.CommitmentId), Tuple.Create("planDigest", preview.PlanDigest), Tuple.Create("selectedLocations", Encode(preview.SelectedLocationIds)),
                    Tuple.Create("originalRoutes", Encode(preview.OriginalConfirmedRouteIds)), Tuple.Create("effectiveRoutes", Encode(preview.EffectiveRouteIds)), Tuple.Create("chargeRoutes", Encode(preview.ChargeRouteIds)), Tuple.Create("prunedFreeRoutes", Encode(preview.PrunedFreeRouteIds)),
                    Tuple.Create("paymentLines", string.Join(";", preview.PaymentPlan.Lines.Select(l => RouteSelectionAnswerCodec.EncodeField(l.RouteId) + "~" + RouteSelectionAnswerCodec.EncodeField(l.ReceiverSubjectKey) + "~" + l.GoldVoucherAmount.ToString(CultureInfo.InvariantCulture)))) })
                    Set(record, pair.Item1, NormalizedValue.CreateString(pair.Item2));
                Set(record, "totalCost", NormalizedValue.CreateInteger(preview.PaymentPlan.TotalGoldVoucherAmount)); Set(record, "acceptedRevision", NormalizedValue.CreateInteger(commitment.AcceptedAtStateRevision));
                var saved = PutRecord(owner, record);
                if (saved.IsValid) session.Stage = RouteSelectionStage.Committed;
                return saved;
            }
            catch (ArgumentException) { return Invalid("route_selection_record_too_large"); }
        }

        internal ValidationResult Cancel(GameState state, EffectNodeRuntimeState owner, RouteSelectionSession session)
        {
            if (!IsActualNode(state, owner) || session.Stage != RouteSelectionStage.Selecting || !session.AllowCancelBeforeCommit) return Invalid("route_selection_cancel_invalid");
            var record = SessionRecord(session);
            Set(record, "stage", NormalizedValue.CreateInteger((int)RouteSelectionStage.Cancelled));
            var saved = PutRecord(owner, record);
            if (saved.IsValid) session.Stage = RouteSelectionStage.Cancelled;
            return saved;
        }

        public ValidationResult TryRead(GameStateView playerView, InteractionView interaction, out RouteSelectionSession session, out RouteSelectionRequestBinding requestBinding)
        {
            if (playerView == null) throw new ArgumentNullException(nameof(playerView));
            if (interaction == null) throw new ArgumentNullException(nameof(interaction));
            session = null; requestBinding = null;
            if (playerView.ViewerRole != GameStateViewerRole.Player || playerView.ViewerPlayerId != interaction.AnsweringPlayerId || interaction.Kind != "route_resource.selection" || interaction.Status != "open"
                || !playerView.Interactions.Contains(interaction) || !TryReadSession(interaction.PromptParameters, out session) || session.PlayerId != playerView.ViewerPlayerId || session.OwnerEffectId != interaction.OwnerEffectId) { session = null; return Invalid("route_selection_projection_invalid"); }
            requestBinding = session.ActiveRequestBinding;
            if (requestBinding == null || requestBinding.CandidateSetId != interaction.CandidateSetId || requestBinding.CandidateSetVersion != interaction.CandidateSetVersion) return Invalid("route_selection_projection_binding_invalid");
            requestBinding.RequestStateRevision = interaction.StateRevision;
            return ValidationResult.Success;
        }

        internal static NormalizedValue SessionRecord(RouteSelectionSession session)
        {
            var record = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
            foreach (var pair in new[] { Tuple.Create("sessionId", session.SessionId), Tuple.Create("ownerEffectId", session.OwnerEffectId), Tuple.Create("mapId", session.MapId), Tuple.Create("startLocationId", session.StartLocationId),
                Tuple.Create("actionContextId", session.ActionContextId), Tuple.Create("budgetBindingId", session.BudgetBindingId), Tuple.Create("upperBound", Encode(session.SelectableLocationUpperBound)),
                Tuple.Create("scopeKey", session.ActiveRequestBinding?.ScopeKey ?? ""), Tuple.Create("factsDigest", session.ActiveRequestBinding?.FrozenFactsDigest ?? ""), Tuple.Create("candidateSetId", session.ActiveRequestBinding?.CandidateSetId ?? "") }) Set(record, pair.Item1, NormalizedValue.CreateString(pair.Item2));
            foreach (var pair in new[] { Tuple.Create("schemaVersion", 1), Tuple.Create("playerId", session.PlayerId), Tuple.Create("purpose", (int)session.Purpose), Tuple.Create("stage", (int)session.Stage),
                Tuple.Create("openedRevision", session.OpenedAtStateRevision), Tuple.Create("startGold", session.StartGoldVoucherAmount), Tuple.Create("minimumLocations", session.MinimumLocations), Tuple.Create("maximumLocations", session.MaximumLocations),
                Tuple.Create("candidateSetVersion", session.ActiveRequestBinding?.CandidateSetVersion ?? 0), Tuple.Create("requestRevision", session.ActiveRequestBinding?.RequestStateRevision ?? 0) }) Set(record, pair.Item1, NormalizedValue.CreateInteger(pair.Item2));
            Set(record, "allowCancel", NormalizedValue.CreateBoolean(session.AllowCancelBeforeCommit));
            return record;
        }

        private static bool TryReadSession(NormalizedValue record, out RouteSelectionSession session)
        {
            session = null;
            if (record?.Kind != NormalizedValueKind.Object || !record.IsValid() || Integer(record, "schemaVersion", -1) != 1 || Integer(record, "startGold", -1) < 0 || Integer(record, "playerId", -1) <= 0 || Value(record, "upperBound") == null || Value(record, "allowCancel")?.Kind != NormalizedValueKind.Boolean) return false;
            var stringFields = new[] { "sessionId", "ownerEffectId", "mapId", "startLocationId", "actionContextId", "budgetBindingId", "upperBound", "scopeKey", "factsDigest", "candidateSetId" };
            var integerFields = new[] { "schemaVersion", "playerId", "purpose", "stage", "openedRevision", "startGold", "minimumLocations", "maximumLocations", "candidateSetVersion", "requestRevision" };
            var savedFields = new[] { "commitmentId", "planDigest", "selectedLocations", "originalRoutes", "effectiveRoutes", "chargeRoutes", "prunedFreeRoutes", "paymentLines", "totalCost", "acceptedRevision", "consumerEffectId", "selectionDiagnostic" };
            if (stringFields.Any(key => Value(record, key)?.Kind != NormalizedValueKind.String) || integerFields.Any(key => Value(record, key)?.Kind != NormalizedValueKind.Integer)
                || record.Properties.Any(p => p.Name != "allowCancel" && !stringFields.Contains(p.Name) && !integerFields.Contains(p.Name) && !savedFields.Contains(p.Name))) return false;
            int purpose = Integer(record, "purpose", -1), stage = Integer(record, "stage", -1);
            if (purpose < 0 || purpose > 2 || stage < 0 || stage > 3) return false;
            session = new RouteSelectionSession { SessionId = Text(record, "sessionId"), OwnerEffectId = Text(record, "ownerEffectId"), PlayerId = Integer(record, "playerId", -1), MapId = Text(record, "mapId"), StartLocationId = Text(record, "startLocationId"),
                Purpose = (RouteSelectionPurpose)purpose, Stage = (RouteSelectionStage)stage, ActionContextId = Text(record, "actionContextId"), BudgetBindingId = Text(record, "budgetBindingId"), StartGoldVoucherAmount = Integer(record, "startGold", -1),
                OpenedAtStateRevision = Integer(record, "openedRevision", -1), SelectableLocationUpperBound = Decode(Text(record, "upperBound")), MinimumLocations = Integer(record, "minimumLocations", -1), MaximumLocations = Integer(record, "maximumLocations", -1), AllowCancelBeforeCommit = Value(record, "allowCancel").BooleanValue };
            if (session.SessionId.Length == 0 || session.OwnerEffectId.Length == 0 || session.BudgetBindingId.Length == 0 || session.MinimumLocations < 0 || session.MaximumLocations < session.MinimumLocations || session.OpenedAtStateRevision < 0) return false;
            if (session.SelectableLocationUpperBound.Any(string.IsNullOrEmpty) || session.SelectableLocationUpperBound.Distinct(StringComparer.Ordinal).Count() != session.SelectableLocationUpperBound.Count) return false;
            if (Text(record, "scopeKey").Length > 0) session.ActiveRequestBinding = new RouteSelectionRequestBinding { SessionId = session.SessionId, ScopeKey = Text(record, "scopeKey"), FrozenFactsDigest = Text(record, "factsDigest"), CandidateSetId = Text(record, "candidateSetId"),
                CandidateSetVersion = Integer(record, "candidateSetVersion", -1), RequestStateRevision = Integer(record, "requestRevision", -1) };
            if (session.ActiveRequestBinding != null && (session.ActiveRequestBinding.ScopeKey.Length != 64 || session.ActiveRequestBinding.CandidateSetId.Length == 0
                || session.ActiveRequestBinding.CandidateSetVersion < 1 || session.ActiveRequestBinding.RequestStateRevision < 0 || session.ActiveRequestBinding.FrozenFactsDigest.Length != 64)) return false;
            if ((session.Stage == RouteSelectionStage.Committed || session.Stage == RouteSelectionStage.Settled) && (session.ActiveRequestBinding == null
                || savedFields.Take(8).Any(key => Value(record, key)?.Kind != NormalizedValueKind.String) || Value(record, "totalCost")?.Kind != NormalizedValueKind.Integer || Integer(record, "acceptedRevision", -1) < 0)) return false;
            return true;
        }

        private static bool IsActualNode(GameState state, EffectNodeRuntimeState node) { return state.EffectRuntime != null && state.EffectRuntime.EffectNodes.Contains(node); }
        private static ValidationResult PutRecord(EffectNodeRuntimeState node, NormalizedValue record)
        {
            var continuation = node.ContinuationData.Clone(); Set(continuation, "routeSelection", record);
            if (!continuation.IsValid()) return Invalid("route_selection_record_too_large");
            node.ContinuationData = continuation; return ValidationResult.Success;
        }
        internal static NormalizedValue Value(NormalizedValue value, string key) { return value?.Properties?.Find(p => p.Name == key)?.Value; }
        internal static string Text(NormalizedValue value, string key) { var field = Value(value, key); return field?.Kind == NormalizedValueKind.String ? field.StringValue : ""; }
        internal static int Integer(NormalizedValue value, string key, int fallback = 0) { var field = Value(value, key); return field?.Kind == NormalizedValueKind.Integer && field.IntegerValue >= int.MinValue && field.IntegerValue <= int.MaxValue ? (int)field.IntegerValue : fallback; }
        internal static void Set(NormalizedValue value, string key, NormalizedValue field) { var entry = value.Properties.Find(p => p.Name == key); if (entry != null) entry.Value = field; else value.Properties.Add(new NormalizedValueEntry { Name = key, Value = field }); value.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name)); }
        internal static string Encode(IEnumerable<string> ids) { return string.Join(",", ids.Select(Uri.EscapeDataString)); }
        internal static IReadOnlyList<string> Decode(string encoded) { return Array.AsReadOnly(encoded.Length == 0 ? new string[0] : encoded.Split(',').Select(Uri.UnescapeDataString).ToArray()); }
        private static ValidationResult Invalid(string reason) { return RouteResourceSelectionService.Invalid(reason); }
    }
}
