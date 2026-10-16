using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Items;
using YC.Domain.Cards;
using YC.Domain.Influence;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable]
    public sealed class PreparedActionPaymentContext
    {
        public string PreparationOwnerEffectId = "", ActionKind = "", TargetId = "";
        public string OperationWindowId = "";
        public int WindowGeneration;
        public NormalizedValue ActionArguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public bool IsPromoted, IsCancelled;
        public string BusinessBeforeEventId = "";
        public bool BusinessBeforeReady;
        public string SourceLocationId = "", PromotedActionEffectId = "";
    }

    public static class ResourcePaymentPreparationService
    {
        public const string BeforeResourcePayment = "BeforeResourcePayment";
        public const string BeforePaymentStage = "before_resource_payment";

        public static NormalizedValue Cost(ResourceSet cost) => Object(
            "originium", NormalizedValue.CreateInteger(cost.Originium),
            "originiumShard", NormalizedValue.CreateInteger(cost.OriginiumShard),
            "iron", NormalizedValue.CreateInteger(cost.Iron),
            "pureOriginium", NormalizedValue.CreateInteger(cost.PureOriginium),
            "goldVoucher", NormalizedValue.CreateInteger(cost.GoldVoucher));

        public static ResourceSet Remaining(ResourcePaymentRuntimeState payment)
        {
            var remaining = payment.PaymentCost.Clone();
            if (payment.SelectedSubstitution != null)
                foreach (ResourceType type in new[] { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron })
                    remaining.Set(type, remaining.Get(type) - payment.SelectedSubstitution.Allocation.Get(type));
            return remaining;
        }

        public static bool CanConfirm(GameState state, EffectNodeRuntimeState owner)
        {
            var payment = owner?.Payment;
            if (payment == null || !payment.BeforePaymentReady || payment.IsCommitted || payment.IsCommitRequested ||
                payment.Preparation?.IsCancelled == true || state.FindPlayer(payment.PlayerId) == null) return false;
            if (payment.Preparation != null && (!TryQuote(state, payment.PlayerId, payment.Preparation.ActionKind, payment.Preparation.TargetId,
                payment.Preparation.ActionArguments, out var currentCost, out _) || !SameCost(currentCost, payment.PaymentCost))) return false;
            if (payment.SelectedSubstitution != null)
            {
                var draft = payment.SelectedSubstitution;
                var zone = ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, payment.PlayerId, draft.SourceZoneId);
                int count = draft.Allocation.Originium + draft.Allocation.OriginiumShard + draft.Allocation.Iron;
                if (zone == null || zone.ParentId != draft.SourceItemId || count < 0 || count > payment.SubstitutionBudget ||
                    ResourcePaymentSubstitutionEffectExecutor.BasicTypes.Any(type => draft.Allocation.Get(type) < 0 || draft.Allocation.Get(type) > payment.PaymentCost.Get(type)) ||
                    draft.Allocation.GoldVoucher != 0 || draft.Allocation.PureOriginium != 0 ||
                    count > ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, zone, payment.PlayerId).Count)
                    return false;
            }
            return state.FindPlayer(payment.PlayerId).Resources.CanPay(Remaining(payment));
        }

        public static NormalizedValue Facts(GameState state, EffectNodeRuntimeState owner)
        {
            var payment = owner.Payment;
            return Object("ownerEffectId", NormalizedValue.CreateString(owner.EffectId),
                "paymentId", NormalizedValue.CreateString(payment.PaymentId), "paymentGeneration", NormalizedValue.CreateInteger(payment.PaymentGeneration),
                "sourceKind", NormalizedValue.CreateString(payment.SourceKind),
                "preparationOwnerEffectId", NormalizedValue.CreateString(payment.Preparation?.PreparationOwnerEffectId ?? ""),
                "originalCost", Cost(payment.PaymentCost),
                "substitutionCost", Cost(payment.SelectedSubstitution?.Allocation ?? new ResourceSet()),
                "remainingCost", Cost(Remaining(payment)), "canPay", NormalizedValue.CreateBoolean(CanConfirm(state, owner)),
                "canSubstitute", NormalizedValue.CreateBoolean(payment.AllowsSubstitution && payment.BeforePaymentReady && state.ItemZones != null && state.ItemZones.Zones.Any(zone =>
                    ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, payment.PlayerId, zone.ZoneId) != null &&
                    ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, zone, payment.PlayerId).Count > 0) &&
                    (long)payment.PaymentCost.Originium + payment.PaymentCost.OriginiumShard + payment.PaymentCost.Iron > 0),
                "isCommitRequested", NormalizedValue.CreateBoolean(payment.IsCommitRequested), "isCommitted", NormalizedValue.CreateBoolean(payment.IsCommitted));
        }

        // null表示前置响应已完整结束，调用者才可进入其原确认/扣款路径。
        public static EffectStepResult AwaitBeforePayment(EffectExecutionContext context, ResourceSet cost)
        {
            var node = context.Node;
            ResourcePaymentRuntimeState payment;
            try { payment = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(node, cost); }
            catch (InvalidOperationException error) { return EffectStepResult.Failed(error.Message); }
            payment.PaymentProtocolVersion = 1;
            if (payment.BeforePaymentReady || payment.IsCommitted) return null;
            var preparation = payment.Preparation;
            if (preparation?.ActionKind == "city_move" && !preparation.BusinessBeforeReady)
            {
                if (preparation.TargetId.Length == 0) throw new InvalidOperationException("payment_target_missing");
                if (preparation.BusinessBeforeEventId.Length == 0)
                {
                    var request = CityMoveEffectExecutor.CreatePreparedBeforeEvent(context.State, node, preparation.TargetId);
                    preparation.BusinessBeforeEventId = request.EventId;
                    return EffectStepResult.Continue("prepared_before_city_move").AddEvent(request);
                }
                if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed)) return EffectStepResult.Failed("before_city_move_response_failed");
                preparation.BusinessBeforeReady = true;
                if (!TryQuote(context.State, payment.PlayerId, preparation.ActionKind, preparation.TargetId, preparation.ActionArguments,
                    out var revised, out var reason) || !SameCost(revised, cost)) return EffectStepResult.Failed(reason.Length > 0 ? reason : "payment_cost_changed");
            }
            if (payment.BeforePaymentEventId.Length == 0)
            {
                payment.BeforePaymentEventId = StableIdFactory.Create("event", node.EffectId, BeforeResourcePayment,
                    payment.PaymentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return EffectStepResult.Continue(BeforePaymentStage).AddEvent(new EffectEventRequest {
                    EventId = payment.BeforePaymentEventId, EventType = BeforeResourcePayment, PlayerId = node.PlayerId,
                    SourceEffectId = node.EffectId, OwnerNodeId = node.EffectId, Visibility = "owner", SemanticKey = "payment.before",
                    Payload = Object("paymentId", NormalizedValue.CreateString(payment.PaymentId), "ownerEffectId", NormalizedValue.CreateString(node.EffectId),
                        "payer", NormalizedValue.CreateStableReference("player", node.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        "cost", Cost(cost), "paymentGeneration", NormalizedValue.CreateInteger(payment.PaymentGeneration),
                        "sourceKind", NormalizedValue.CreateString(payment.SourceKind), "sourceEffectId", NormalizedValue.CreateString(node.EffectId)) });
            }
            if (context.ChildNodes.Any(child => child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed))
                return EffectStepResult.NoProgress("等待支付前必要响应完整结束。");
            if (context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed)) return EffectStepResult.Failed("before_resource_payment_response_failed");
            payment.BeforePaymentReady = true;
            return null;
        }

        public static bool SameCost(ResourceSet first, ResourceSet second) => first != null && second != null &&
            Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().All(type => first.Get(type) == second.Get(type));

        public static bool HasPendingResponse(GameState state, EffectNodeRuntimeState owner) =>
            state.EffectRuntime.EffectNodes.Any(node => node.EffectId != owner.EffectId &&
                SettlementEntryService.IsWithin(state.EffectRuntime, node.EffectId, owner.EffectId) &&
                node.Status != EffectNodeStatus.Completed && node.Status != EffectNodeStatus.Failed) ||
            !string.IsNullOrEmpty(owner.SettlementWindow?.ActiveTargetEffectId);

        internal static EffectNodeRuntimeState FindDeclinableRulePayment(GameState state, EffectNodeRuntimeState source, int playerId)
        {
            var paymentOwner = source?.Payment?.SourceKind == "condition" ? source : state.EffectRuntime.EffectNodes.Find(node =>
                node.Payment?.RuleConditionOwnerEffectId == source?.EffectId && node.Payment.SourceKind == "condition");
            var payment = paymentOwner?.Payment;
            var condition = payment == null ? null : state.EffectRuntime.EffectNodes.Find(node => node.EffectId == payment.RuleConditionOwnerEffectId);
            if (paymentOwner?.PlayerId != playerId || payment == null || !payment.BeforePaymentReady || payment.IsCommitted || payment.IsCommitRequested ||
                paymentOwner.FlowStage != ResourceEffectExecutor.PaymentConfirmationStage || HasPendingResponse(state, paymentOwner) ||
                condition?.EffectTypeId != EffectTypeIds.Condition || !Flag(condition.NormalizedArguments, "ruleCondition") ||
                condition.FlowStage != "left:0" || condition.NestedGroupSizes[0] != 1 || condition.NestedEffects[0].EffectTypeId != ResourceEffectTypeIds.Pay ||
                condition.ChildEffectIds.Count != 1 || condition.ChildEffectIds[0] != paymentOwner.EffectId || condition.PlayerId != playerId ||
                condition.PendingOutcome != EffectPendingOutcome.None || paymentOwner.PendingOutcome != EffectPendingOutcome.None ||
                !state.EffectRuntime.InteractionRequests.Any(request => request.OwnerEffectId == paymentOwner.EffectId && request.Status == "open" &&
                    request.InteractionTypeId == "action.decline_effect" && request.AllowDecline)) return null;
            return paymentOwner;
        }

        public static bool IsCurrentPreparation(GameState state, EffectNodeRuntimeState owner, int playerId)
        {
            var preparation = owner?.Payment?.Preparation;
            if (preparation == null || owner.PlayerId != playerId || preparation.IsCancelled || preparation.IsPromoted ||
                owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Faulted) return false;
            var window = state.EffectRuntime.OperationWindows.Find(value => value.OperationWindowId == preparation.OperationWindowId &&
                value.WindowGeneration == preparation.WindowGeneration && value.Lifecycle == OperationWindowLifecycle.Open &&
                value.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow && value.AuthorizedPlayerIds.SequenceEqual(new[] { playerId }));
            return window != null && state.EffectRuntime.MainNodes.Any(main => main.NodeId == state.EffectRuntime.ActiveMainNodeId &&
                main.NodeId == window.SourceMainNodeId && main.PlayerId == playerId && main.ExecutionEffectId == owner.ParentEffectId);
        }

        public static bool TryQuote(GameState state, int playerId, string actionKind, string targetId, NormalizedValue arguments,
            out ResourceSet cost, out string reason)
        {
            cost = null; reason = "payment_preparation_arguments_invalid";
            if (arguments?.Kind != NormalizedValueKind.Object || !arguments.TryValidate(out _) || state.FindPlayer(playerId) == null) return false;
            if (actionKind == "city_move")
            {
                if (arguments.Properties.Count != 0) return false;
                var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
                if (!string.IsNullOrEmpty(targetId))
                {
                    var validation = new CityMovementService(map, new InfluenceService(map), new TravelCostService(map))
                        .CanMoveCityEffect(state, playerId, targetId, true);
                    if (!validation.IsValid) { reason = validation.Reason; return false; }
                }
                cost = new TravelCostService(map).GetCityMoveBaseCost(); reason = ""; return true;
            }
            if (actionKind != "build" || !ItemZoneLegacyProjection.IsActive(state) ||
                arguments.Properties.Any(entry => !new[] { "paymentMode", "expectedSourceZoneId", "cityBoardSlotIndex" }.Contains(entry.Name))) return false;
            var mode = Text(arguments, "paymentMode"); var sourceId = Text(arguments, "expectedSourceZoneId");
            if (mode != BuildFacilityService.PaymentModeResources && mode != BuildFacilityService.PaymentModeGold) return false;
            var item = state.ItemZones.Items.Find(value => value.ItemId == targetId);
            if (item == null || sourceId.Length == 0 || item.ZoneId != sourceId) { reason = "source_zone_changed"; return false; }
            var zone = state.ItemZones.Zones.Find(value => value.ZoneId == sourceId);
            if (zone == null || zone.TemplateId != "core.zone.facility_supply_slot" &&
                !FacilityBuildCandidateQuery.IsActiveStoredFacility(state, zone, playerId)) { reason = "invalid_build_source"; return false; }
            var definition = FacilityCardDatabase.Get(state, item.DefinitionId);
            var player = state.FindPlayer(playerId);
            int slot = Number(arguments, "cityBoardSlotIndex", -1);
            if (definition == null || FacilityCardDatabase.PlayerHasBuiltUniqueFacility(state, player, definition) ||
                BuildFacilityService.FindFirstEmptyCityBoardSlot(state, playerId) < 0 || slot >= 0 &&
                !BuildFacilityService.CreateForEffectTree().ValidatePaidPlacement(state, playerId, item.DefinitionId, slot).IsValid)
            { reason = "invalid_build_slot"; return false; }
            cost = mode == BuildFacilityService.PaymentModeGold ? new ResourceSet { GoldVoucher = definition.GoldVoucherCost } :
                new FacilityBuildCostService().GetEffectiveResourceCost(state, player, definition);
            reason = ""; return true;
        }
    }

    public partial class EffectTreeExecutor
    {
        public bool TryPrepareActionPayment(int playerId, string actionKind, string targetId, NormalizedValue actionArguments,
            int expectedRevision, string commandId, out string preparationOwnerEffectId, out string paymentOwnerEffectId,
            out string interactionId, out string diagnostic)
        {
            preparationOwnerEffectId = paymentOwnerEffectId = interactionId = "";
            diagnostic = "payment_preparation_context_invalid";
            if (expectedRevision != Runtime.StateRevision) { diagnostic = "state_revision_mismatch"; return false; }
            if (string.IsNullOrEmpty(commandId) || Runtime.Status != EffectRuntimeStatus.Active ||
                state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2 ||
                !new MainActionBudgetService().ValidateCanSpend(state, playerId).IsValid) return false;
            var window = Runtime.MainNodes.Find(main => main.NodeId == Runtime.ActiveMainNodeId &&
                main.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && main.PlayerId == playerId);
            var operation = Runtime.OperationWindows.Find(value => value.SourceMainNodeId == window?.NodeId &&
                value.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow && value.Lifecycle == OperationWindowLifecycle.Open &&
                value.AvailablePlayerIds.Contains(playerId));
            if (window == null || operation == null) { diagnostic = "settlement_permission_denied"; return false; }
            var previous = Runtime.EffectNodes.FirstOrDefault(node => ResourcePaymentPreparationService.IsCurrentPreparation(state, node, playerId));
            if (previous != null)
            {
                if (actionKind != "city_move" || previous.Payment.Preparation.ActionKind != actionKind ||
                    previous.Payment.Preparation.TargetId.Length != 0 || string.IsNullOrEmpty(targetId) ||
                    previous.Payment.Preparation.BusinessBeforeEventId.Length != 0)
                { diagnostic = "payment_preparation_cancel_required"; return false; }
                if (!ResourcePaymentPreparationService.TryQuote(state, playerId, actionKind, targetId, actionArguments, out var updatedCost, out diagnostic) ||
                    !ResourcePaymentPreparationService.SameCost(updatedCost, previous.Payment.PaymentCost)) return false;
                var updated = GameStateCloneService.DeepClone(state);
                updated.EffectRuntime.EffectNodes.Find(node => node.EffectId == previous.EffectId).Payment.Preparation.TargetId = targetId;
                CommitPreparedWork(updated, commandId, new List<RuleJournalEntry>(), -1, -1, new[] { previous.EffectId });
                preparationOwnerEffectId = paymentOwnerEffectId = previous.EffectId;
                var updatedReport = RunUntilQuiescent();
                if (updatedReport.Faulted) { diagnostic = updatedReport.FaultCode; return false; }
                interactionId = Runtime.InteractionRequests.LastOrDefault(request => request.OwnerEffectId == previous.EffectId && request.Status == "open")?.GetStableInteractionId() ?? "";
                diagnostic = ""; return true;
            }
            if (state.HasPendingChoiceForCommand(Runtime.CurrentOperationContext)) { diagnostic = "settlement_required_pending"; return false; }
            if (!ResourcePaymentPreparationService.TryQuote(state, playerId, actionKind, targetId ?? "", actionArguments, out var cost, out diagnostic)) return false;
            var work = GameStateCloneService.DeepClone(state);
            var parent = work.EffectRuntime.EffectNodes.Find(node => node.EffectId == window.ExecutionEffectId);
            var spec = actionKind == "build" ? EffectSpec.Create(FacilityBuildEffectExecutor.PaymentTypeId,
                Object("facilityId", NormalizedValue.CreateString(work.ItemZones.Items.Find(item => item.ItemId == targetId).DefinitionId),
                    "paymentMode", NormalizedValue.CreateString(Text(actionArguments, "paymentMode"))), playerId) :
                ResourceEffectSpecFactory.Pay(playerId, cost, "city_move.base_cost");
            var entries = new List<RuleJournalEntry>();
            var ids = AppendChildEffects(work, parent, new[] { spec }, "preparation", commandId, entries);
            var owner = work.EffectRuntime.EffectNodes.Find(node => node.EffectId == ids[0]);
            var payment = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(owner, cost);
            payment.SourceKind = actionKind;
            payment.Preparation = new PreparedActionPaymentContext { PreparationOwnerEffectId = owner.EffectId,
                ActionKind = actionKind, TargetId = targetId ?? "", ActionArguments = actionArguments.Clone(),
                OperationWindowId = operation.OperationWindowId, WindowGeneration = operation.WindowGeneration,
                SourceLocationId = work.FindPlayer(playerId).CityLocationId };
            CommitPreparedWork(work, commandId, entries, -1, -1, ids);
            preparationOwnerEffectId = paymentOwnerEffectId = owner.EffectId;
            var report = RunUntilQuiescent();
            if (report.Faulted) { diagnostic = report.FaultCode; return false; }
            interactionId = Runtime.InteractionRequests.LastOrDefault(request => request.OwnerEffectId == owner.EffectId && request.Status == "open")?.GetStableInteractionId() ?? "";
            diagnostic = ""; return true;
        }

        public bool TryCancelPreparedActionPayment(int playerId, string preparationOwnerEffectId, int expectedRevision,
            string commandId, out string diagnostic)
        {
            diagnostic = "payment_preparation_context_invalid";
            if (expectedRevision != Runtime.StateRevision) { diagnostic = "state_revision_mismatch"; return false; }
            var source = GetNode(preparationOwnerEffectId);
            if (string.IsNullOrEmpty(commandId) || !ResourcePaymentPreparationService.IsCurrentPreparation(state, source, playerId) ||
                source.Payment.IsCommitted || source.Payment.IsCommitRequested) return false;
            if (ResourcePaymentPreparationService.HasPendingResponse(state, source)) { diagnostic = "settlement_required_pending"; return false; }
            var work = GameStateCloneService.DeepClone(state);
            var owner = work.EffectRuntime.EffectNodes.Find(node => node.EffectId == source.EffectId);
            ResourcePaymentSubstitutionEffectExecutor.CancelDraft(owner);
            owner.Payment.Preparation.IsCancelled = true;
            ClosePreparedPaymentInput(work, owner);
            var entries = new List<RuleJournalEntry>();
            if (owner.Status != EffectNodeStatus.Failed)
            {
                owner.PendingOutcome = EffectPendingOutcome.Failed; owner.FailureReason = "payment_preparation_cancelled";
                TryCompleteNodeOnWorking(work, owner, entries);
            }
            CommitPreparedWork(work, commandId, entries, -1, -1, new[] { owner.EffectId });
            diagnostic = ""; return true;
        }

        public bool TryCreatePreparedPlayerActionEffect(int playerId, string preparationOwnerEffectId, EffectSpec actionSpec,
            string roundId, string commandId, out string actionEffectId, out string diagnostic)
        {
            actionEffectId = ""; diagnostic = "payment_preparation_context_invalid";
            var source = GetNode(preparationOwnerEffectId);
            if (string.IsNullOrEmpty(commandId) || !ResourcePaymentPreparationService.IsCurrentPreparation(state, source, playerId) ||
                source.Status != EffectNodeStatus.Blocked || !ResourcePaymentPreparationService.CanConfirm(state, source) ||
                ResourcePaymentPreparationService.HasPendingResponse(state, source) || !new MainActionBudgetService().ValidateCanSpend(state, playerId).IsValid) return false;
            var preparation = source.Payment.Preparation;
            string kind = actionSpec?.EffectTypeId == FacilityEntryEffectTypeIds.Build ? "build" :
                actionSpec?.EffectTypeId == CityMoveEffectTypeIds.Move ? "city_move" : "";
            string target = kind == "build" ? Text(actionSpec.NormalizedArguments, "facilityId") :
                Value(actionSpec?.NormalizedArguments, "targetLocation")?.ReferenceId ?? "";
            if (kind != preparation.ActionKind || target != preparation.TargetId || actionSpec.PlayerId != playerId ||
                kind == "build" && (Text(actionSpec.NormalizedArguments, "paymentMode") != Text(preparation.ActionArguments, "paymentMode") ||
                    Text(actionSpec.NormalizedArguments, "expectedSourceZoneId") != Text(preparation.ActionArguments, "expectedSourceZoneId")) ||
                kind == "city_move" && (preparation.SourceLocationId != state.FindPlayer(playerId).CityLocationId || !preparation.BusinessBeforeReady))
            { diagnostic = "payment_preparation_source_changed"; return false; }
            var finalArguments = preparation.ActionArguments.Clone();
            if (kind == "build")
            {
                finalArguments.Properties.RemoveAll(entry => entry.Name == "cityBoardSlotIndex");
                finalArguments.Properties.Add(new NormalizedValueEntry { Name = "cityBoardSlotIndex", Value = NormalizedValue.CreateInteger(Number(actionSpec.NormalizedArguments, "cityBoardSlotIndex", -1)) });
                finalArguments.Properties.Sort((first, second) => StringComparer.Ordinal.Compare(first.Name, second.Name));
            }
            if (!ResourcePaymentPreparationService.TryQuote(state, playerId, kind, target, finalArguments, out var current, out diagnostic) ||
                !ResourcePaymentPreparationService.SameCost(current, source.Payment.PaymentCost)) { if (diagnostic.Length == 0) diagnostic = "payment_cost_changed"; return false; }
            var work = GameStateCloneService.DeepClone(state);
            var owner = work.EffectRuntime.EffectNodes.Find(node => node.EffectId == source.EffectId);
            var parent = work.EffectRuntime.EffectNodes.Find(node => node.EffectId == owner.ParentEffectId);
            var entries = new List<RuleJournalEntry>();
            var savedSpec = EffectRegistry.FromRuntimeSpec(actionSpec.ToRuntimeState());
            savedSpec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "preparedPaymentOwnerEffectId", Value = NormalizedValue.CreateString(owner.EffectId) });
            savedSpec.NormalizedArguments.Properties.Sort((first, second) => StringComparer.Ordinal.Compare(first.Name, second.Name));
            var ids = AppendChildEffects(work, parent, new[] { savedSpec }, "child", commandId, entries);
            actionEffectId = ids[0]; owner.Payment.Preparation.PromotedActionEffectId = actionEffectId;
            owner.Payment.Preparation.IsPromoted = true; owner.Payment.IsCommitRequested = true;
            ClosePreparedPaymentInput(work, owner); owner.Status = EffectNodeStatus.Ready;
            CommitPreparedWork(work, commandId, entries, -1, -1, new[] { owner.EffectId, actionEffectId });
            diagnostic = ""; return true;
        }

        private static void ClosePreparedPaymentInput(GameState work, EffectNodeRuntimeState owner)
        {
            foreach (var request in work.EffectRuntime.InteractionRequests.Where(value => value.OwnerEffectId == owner.EffectId &&
                (value.Status == "open" || value.Status == "suspended")))
            {
                request.Status = "cancelled";
                foreach (var blocker in work.EffectRuntime.Blockers.Where(value => value.InteractionRequestId == request.RequestId)) blocker.IsResolved = true;
            }
            if (owner.SettlementWindow != null)
            {
                owner.SettlementWindow.IsClosed = true;
                if (work.EffectRuntime.ActiveSettlementWindowId == owner.SettlementWindow.Context.Id) work.EffectRuntime.ActiveSettlementWindowId = "";
            }
        }
    }
}
