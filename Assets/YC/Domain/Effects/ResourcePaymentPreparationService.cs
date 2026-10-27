using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Interactions;
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
        public string AuthorizationKind = "main_action", SelectionOwnerEffectId = "", SelectionInteractionId = "", SelectionCandidateSetId = "";
        public int SelectionCandidateSetVersion;
        public NormalizedValue ActionArguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
        public bool IsPromoted, IsCancelled;
        public string BusinessBeforeEventId = "";
        public bool BusinessBeforeReady;
        public string SourceLocationId = "", PromotedActionEffectId = "";
        public string CancellationCommandId = "", CancellationDigest = "";
        public int CancellationPlayerId = -1, CancellationRequestRevision = -1;
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
            if (payment.PaymentProtocolVersion == 2)
            {
                var value = payment.PaymentCost.Clone(); var extra = new ResourceSet();
                foreach (var d in payment.SourceDrafts.Where(d => d.Status == "valid"))
                {
                    foreach (var t in PaymentResources.Types) value.Set(t, checked(value.Get(t) - d.Allocation.Get(t)));
                    extra = PaymentResources.Add(extra, PaymentResources.Scale(d.Rule.UnitResourceCost, PaymentResources.Total(d.Allocation)));
                }
                return PaymentResources.Add(value, extra);
            }
            return payment.PaymentCost.Clone();
        }

        public static bool TryValidateConfirmationAnswer(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner,
            InteractionRequest request, NormalizedValue answer, out string reason)
        {
            reason = "";
            var p = owner?.Payment;
            if (p == null) return true;
            var value = answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1 ? answer.Items[0] : answer;
            string id = value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.Kind == NormalizedValueKind.Boolean ?
                (value.BooleanValue ? "continue" : "decline") : value?.StringValue;
            bool confirm = request.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId && id == ResourceEffectExecutor.PaymentConfirmationCandidateId ||
                request.InteractionTypeId == "action.decline_effect" && id == "continue" ||
                request.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId && id == Text(owner.ContinuationData, "selectedPaymentOptionId");
            if (!confirm) return true; // 配置及合法放弃仍可进行，不要求已足额。
            if (!p.BeforePaymentReady || p.IsCommitted || p.IsCommitRequested || p.Preparation?.IsCancelled == true)
            { reason = "payment_confirmation_host_missing"; return false; }
            if (p.PaymentProtocolVersion == 2) return ResourcePaymentPlanService.TryBuild(state, registry, owner, out _, out reason);
            if (CanConfirm(state, owner, registry)) return true;
            reason = "payment_confirmation_unavailable"; return false;
        }
        // 仅为有真实确认记录的不足额再验恢复待答；不吞其他来源/身份/数据故障。
        public static bool TryRetryInsufficientConfirmation(EffectExecutionContext c, out EffectStepResult step)
        {
            step = null;
            var p = c.Node.Payment;
            if (p == null || p.IsCommitted || p.Preparation?.IsCancelled == true) return false;
            var previous = c.State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.OwnerEffectId == c.Node.EffectId && r.Status == "answered" &&
                (r.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId || r.InteractionTypeId == "action.decline_effect" || r.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId));
            if (previous == null) return false;
            p.IsCommitRequested = false; p.Phase = "confirmation";
            var fresh = new EffectInteractionSpec { InteractionTypeId = previous.InteractionTypeId, AnswerSchema = previous.AnswerSchema,
                AnsweringPlayerId = previous.AnsweringPlayerId, Visibility = previous.Visibility, PromptKey = previous.PromptKey,
                PromptParameters = previous.PromptParameters.Clone(), Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(previous.Presentation),
                CandidateSetId = previous.CandidateSetId, CandidateSetVersion = previous.CandidateSetVersion, CandidateResolutionId = previous.CandidateResolutionId,
                MinSelections = previous.MinSelections, MaxSelections = previous.MaxSelections, AllowDecline = previous.AllowDecline };
            fresh.CandidateIds.AddRange(previous.CandidateIds);
            step = EffectStepResult.Continue(c.Node.FlowStage).AddInteraction(fresh);
            return true;
        }
        public static bool CanConfirm(GameState state, EffectNodeRuntimeState owner, EffectRegistry registry = null)
        {
            var payment = owner?.Payment;
            if (payment == null || !payment.BeforePaymentReady || payment.IsCommitted || payment.IsCommitRequested ||
                payment.Preparation?.IsCancelled == true || state.FindPlayer(payment.PlayerId) == null) return false;
            if (payment.PaymentProtocolVersion == 2)
                return registry != null && ResourcePaymentPlanService.TryBuild(state, registry, owner, out _, out _);
            if (payment.Preparation != null && (!TryQuotePreparation(state, owner, out var currentCost, out _, registry) || !SameCost(currentCost, payment.PaymentCost))) return false;
            if (payment.SelectedSubstitution != null)
            {
                return false; // 旧替代草稿无api3逐枚证明，不能据当前库存续付。
            }
            return state.FindPlayer(payment.PlayerId).Resources.CanPay(Remaining(payment));
        }

        public static NormalizedValue Facts(GameState state, EffectNodeRuntimeState owner, EffectRegistry registry = null)
        {
            var payment = owner.Payment;
            var facts = Object("ownerEffectId", NormalizedValue.CreateString(owner.EffectId),
                "paymentId", NormalizedValue.CreateString(payment.PaymentId), "paymentGeneration", NormalizedValue.CreateInteger(payment.PaymentGeneration),
                "sourceKind", NormalizedValue.CreateString(payment.SourceKind),
                "authorizationKind", NormalizedValue.CreateString(payment.Preparation?.AuthorizationKind ?? ""),
                "preparationOwnerEffectId", NormalizedValue.CreateString(payment.Preparation?.PreparationOwnerEffectId ?? ""),
                "originalCost", Cost(payment.PaymentCost),
                "substitutionCost", Cost(payment.SelectedSubstitution?.Allocation ?? new ResourceSet()),
                "remainingCost", Cost(Remaining(payment)), "canPay", NormalizedValue.CreateBoolean(CanConfirm(state, owner, registry)),
                "canSubstitute", NormalizedValue.CreateBoolean(false),
                "isCommitRequested", NormalizedValue.CreateBoolean(payment.IsCommitRequested), "isCommitted", NormalizedValue.CreateBoolean(payment.IsCommitted));
            if (payment.PaymentProtocolVersion == 2 && registry != null)
            {
                facts.Properties.Add(new NormalizedValueEntry { Name = "paymentSourceQuery", Value = ResourcePaymentDraftHost.SourceQuery(state, registry, owner) });
                facts.Properties.Find(e => e.Name == "canSubstitute").Value = NormalizedValue.CreateBoolean(payment.AllowsSubstitution && payment.BeforePaymentReady && !payment.IsCommitted &&
                    (ResourcePaymentSourceService.Query(state, registry, owner, payment.PaymentCost).Count > 0 || payment.SourceDrafts.Count > 0));
            }
            return facts;
        }

        // null表示前置响应已完整结束，调用者才可进入其原确认/扣款路径。
        public static EffectStepResult AwaitBeforePayment(EffectExecutionContext context, ResourceSet cost)
        {
            var node = context.Node;
            ResourcePaymentRuntimeState payment;
            try { payment = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(node, cost); }
            catch (InvalidOperationException error) { return EffectStepResult.Failed(error.Message); }
            if (payment.PaymentProtocolVersion != 2) payment.PaymentProtocolVersion = 1;
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
                    out var revised, out var reason,context.Registry,node.EffectId) || !SameCost(revised, cost)) return EffectStepResult.Failed(reason.Length > 0 ? reason : "payment_cost_changed");
            }
            if (payment.BeforePaymentEventId.Length == 0)
            {
                payment.BeforePaymentEventId = StableIdFactory.Create("event", node.EffectId, BeforeResourcePayment,
                    payment.PaymentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (payment.PaymentProtocolVersion == 2)
                {
                    payment.BeforePaymentReceipts.Add(new PaymentBeforeReceipt { EventId = payment.BeforePaymentEventId, Generation = payment.PaymentGeneration, GrossCost = cost.Clone() });
                    payment.Phase = "before_payment";
                }
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
            if (payment.PaymentProtocolVersion == 2)
            {
                payment.BeforePaymentReceipts.Single(r => r.EventId == payment.BeforePaymentEventId).Ready = true;
                payment.Phase = "confirmation";
            }
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
            if (!IsMainActionAuthorization(preparation) || owner.PlayerId != playerId || preparation.IsCancelled || preparation.IsPromoted ||
                owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Faulted) return false;
            var window = state.EffectRuntime.OperationWindows.Find(value => value.OperationWindowId == preparation.OperationWindowId &&
                value.WindowGeneration == preparation.WindowGeneration && value.Lifecycle == OperationWindowLifecycle.Open &&
                value.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow && value.AuthorizedPlayerIds.SequenceEqual(new[] { playerId }));
            return window != null && state.EffectRuntime.MainNodes.Any(main => main.NodeId == state.EffectRuntime.ActiveMainNodeId &&
                main.NodeId == window.SourceMainNodeId && main.PlayerId == playerId && main.ExecutionEffectId == owner.ParentEffectId);
        }

        internal static bool IsMainActionAuthorization(PreparedActionPaymentContext preparation) => preparation != null &&
            preparation.AuthorizationKind == "main_action" && preparation.SelectionOwnerEffectId == "" &&
            preparation.SelectionInteractionId == "" && preparation.SelectionCandidateSetId == "" && preparation.SelectionCandidateSetVersion == 0;

        internal static bool ValidateEffectBuildCandidate(GameState state, EffectNodeRuntimeState selection, InteractionRequest input,
            string targetId, string sourceZoneId, string mode, bool requireCurrentSource, EffectRegistry registry = null)
        {
            if (selection == null || input == null || input.OwnerEffectId != selection.EffectId || input.AnsweringPlayerId != selection.PlayerId ||
                input.InteractionTypeId != FacilitySelectionEffectExecutor.NormalBuildInteractionType ||
                input.CandidateSetVersion < 1 || !input.CandidateIds.Contains(targetId)) return false;
            var record = state.EffectRuntime.CandidateResolutions.Find(c => c.CandidateSetId == input.CandidateSetId && c.Version == input.CandidateSetVersion);
            string source = Text(selection.NormalizedArguments, "sourceZone", "supply");
            return record?.SourceEffectId == selection.EffectId && record.ExecutingPlayerId == selection.PlayerId &&
                record.CandidateKind == FacilityBuildCandidateQuery.CandidateKind && record.FinalCandidateIds.SequenceEqual(input.CandidateIds) &&
                Text(input.PromptParameters, "sourceZone") == source &&
                (source != "deck" || Text(input.PromptParameters, "expectedSourceZoneId") == sourceZoneId) &&
                FacilitySelectionEffectExecutor.ValidateEffectBuildSource(state, selection, targetId, sourceZoneId, mode, requireCurrentSource, registry);
        }

        internal static bool ValidateEffectBuildAssociation(GameState state, EffectNodeRuntimeState owner, out EffectNodeRuntimeState selection, bool requireCurrentSource = true, EffectRegistry registry = null)
        {
            selection = null;
            var p = owner?.Payment?.Preparation;
            if (p == null || p.AuthorizationKind != "effect_build" || p.ActionKind != "build" ||
                p.PreparationOwnerEffectId != owner.EffectId || owner.EffectTypeId != FacilityBuildEffectExecutor.PaymentTypeId ||
                string.IsNullOrEmpty(p.SelectionOwnerEffectId) || string.IsNullOrEmpty(p.SelectionInteractionId) ||
                string.IsNullOrEmpty(p.SelectionCandidateSetId) || p.SelectionCandidateSetVersion < 1 ||
                p.OperationWindowId != "" || p.WindowGeneration != 0 || p.ActionArguments?.Kind != NormalizedValueKind.Object || !p.ActionArguments.IsValid() ||
                p.ActionArguments.Properties.Count != 2 || p.ActionArguments.Properties.Any(e =>
                    e.Name != "paymentMode" && e.Name != "expectedSourceZoneId" || e.Value?.Kind != NormalizedValueKind.String)) return false;
            selection = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == p.SelectionOwnerEffectId);
            var input = state.EffectRuntime.InteractionRequests.Find(q => q.GetStableInteractionId() == p.SelectionInteractionId);
            var record = state.EffectRuntime.CandidateResolutions.Find(c => c.CandidateSetId == p.SelectionCandidateSetId && c.Version == p.SelectionCandidateSetVersion);
            string source = Text(p.ActionArguments, "expectedSourceZoneId");
            bool valid = FacilitySelectionEffectExecutor.IsNormalBuildSelection(selection) &&
                selection.PlayerId == owner.PlayerId && owner.ParentEffectId == selection.EffectId && p.TargetId != null &&
                input?.OwnerEffectId == selection.EffectId && input.Status == "invalidated" && input.InteractionTypeId == FacilitySelectionEffectExecutor.NormalBuildInteractionType &&
                input.AnsweringPlayerId == owner.PlayerId && input.CandidateSetId == p.SelectionCandidateSetId && input.CandidateSetVersion == p.SelectionCandidateSetVersion &&
                record?.SourceEffectId == selection.EffectId && record.ExecutingPlayerId == owner.PlayerId && record.FinalCandidateIds.SequenceEqual(input.CandidateIds) &&
                record.FinalCandidateIds.Contains(p.TargetId) &&
                ValidateEffectBuildCandidate(state, selection, input, p.TargetId, source, Text(p.ActionArguments, "paymentMode"), requireCurrentSource && !p.IsPromoted && !p.IsCancelled, registry) &&
                state.ItemZones.Items.Any(i => i.ItemId == p.TargetId && i.DefinitionId == Text(owner.NormalizedArguments, "facilityId")) &&
                Text(owner.NormalizedArguments, "paymentMode") == Text(p.ActionArguments, "paymentMode") &&
                (Text(p.ActionArguments, "paymentMode") == "resources" || Text(p.ActionArguments, "paymentMode") == "gold");
            if (!valid) return false;
            if (p.IsCancelled) return !p.IsPromoted && string.IsNullOrEmpty(p.PromotedActionEffectId) &&
                !owner.Payment.IsCommitted && !owner.Payment.IsCommitRequested && owner.Status == EffectNodeStatus.Failed &&
                owner.FailureReason == "payment_preparation_cancelled" &&
                !state.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == owner.EffectId && q.Status == "open");
            if (!p.IsPromoted) return true;
            var build = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == p.PromotedActionEffectId);
            return build?.EffectTypeId == FacilityEntryEffectTypeIds.Build && build.ParentEffectId == selection.EffectId && build.PlayerId == owner.PlayerId &&
                Text(build.NormalizedArguments, "preparedPaymentOwnerEffectId") == owner.EffectId && Text(build.NormalizedArguments, "facilityId") == p.TargetId &&
                Text(build.NormalizedArguments, "expectedSourceZoneId") == source && Text(build.NormalizedArguments, "paymentMode") == Text(p.ActionArguments, "paymentMode") &&
                Text(build.NormalizedArguments,"sourceKind")==Text(selection.NormalizedArguments,"sourceZone","supply") &&
                Text(build.NormalizedArguments,"sourceKey")==Text(selection.NormalizedArguments,"sourceKey") &&
                Value(build.NormalizedArguments, "consumeMainAction")?.Kind == NormalizedValueKind.Boolean && Flag(build.NormalizedArguments, "consumeMainAction")==Flag(selection.NormalizedArguments,"consumeMainAction") &&
                Value(build.NormalizedArguments, "reserveForFree")?.Kind == NormalizedValueKind.Boolean && !Flag(build.NormalizedArguments, "reserveForFree");
        }

        internal static bool ValidateEffectBuildIdentity(GameState state, EffectNodeRuntimeState owner, out EffectNodeRuntimeState selection) =>
            ValidateEffectBuildAssociation(state, owner, out selection, false);

        public static bool CanCancelEffectBuildPreparation(GameState state, EffectNodeRuntimeState owner, int playerId) =>
            IsCurrentEffectBuildPreparation(state, owner, playerId, false) && !owner.Payment.Preparation.IsPromoted && !owner.Payment.IsCommitted && !owner.Payment.IsCommitRequested && !HasPendingResponse(state, owner);

        public static bool IsCurrentEffectBuildPreparation(GameState state, EffectNodeRuntimeState owner, int playerId, bool requireCurrentSource = true, EffectRegistry registry = null)
        {
            var p = owner?.Payment?.Preparation;
            return state?.EffectRuntime?.Status == EffectRuntimeStatus.Active && owner?.PlayerId == playerId && p != null && !p.IsCancelled &&
                owner.Status != EffectNodeStatus.Failed && owner.Status != EffectNodeStatus.Faulted &&
                ValidateEffectBuildAssociation(state, owner, out var selection, requireCurrentSource, registry) && selection.PendingOutcome == EffectPendingOutcome.None &&
                selection.Status != EffectNodeStatus.Completed && selection.Status != EffectNodeStatus.Failed && selection.Status != EffectNodeStatus.Faulted &&
                Text(selection.ContinuationData, "effectBuildPreparationId") == owner.EffectId &&
                (!p.IsPromoted && owner.Status != EffectNodeStatus.Completed && selection.FlowStage == "deck_preparing" ||
                    p.IsPromoted && selection.FlowStage == "built" && state.EffectRuntime.EffectNodes.Any(n => n.EffectId == p.PromotedActionEffectId &&
                        n.ParentEffectId == selection.EffectId && Text(n.NormalizedArguments, "preparedPaymentOwnerEffectId") == owner.EffectId));
        }

        internal static bool TryQuotePreparation(GameState state, EffectNodeRuntimeState owner, out ResourceSet cost, out string reason, EffectRegistry registry = null)
        {
            cost = null; reason = "payment_preparation_context_invalid";
            var p = owner?.Payment?.Preparation;
            if (IsMainActionAuthorization(p)) return TryQuote(state, owner.PlayerId, p.ActionKind, p.TargetId, p.ActionArguments, out cost, out reason, registry, owner.EffectId);
            if (!ValidateEffectBuildAssociation(state, owner, out var selection, true, registry)) return false;
            return TryQuoteEffectBuildTarget(state, owner.PlayerId, p.TargetId, p.ActionArguments, selection, out cost, out reason, registry);
        }

        internal static bool TryQuoteEffectBuildTarget(GameState state, int playerId, string target, NormalizedValue args,
            EffectNodeRuntimeState selection, out ResourceSet cost, out string reason, EffectRegistry registry = null)
        {
            cost = null; reason = "payment_preparation_arguments_invalid";
            if (args?.Kind != NormalizedValueKind.Object || !args.IsValid() || args.Properties.Count != 2 ||
                args.Properties.Any(e => e.Name != "paymentMode" && e.Name != "expectedSourceZoneId" || e.Value?.Kind != NormalizedValueKind.String)) return false;
            var mode = Text(args, "paymentMode"); var item = state.ItemZones.Items.Find(i => i.ItemId == target);
            var player = state.FindPlayer(playerId);
            if (player == null || mode != "resources" && mode != "gold") return false;
            if (selection?.PlayerId != playerId || !FacilitySelectionEffectExecutor.ValidateEffectBuildSource(state, selection, target,
                Text(args, "expectedSourceZoneId"), mode, true, registry))
            { reason = "source_zone_changed"; return false; }
            var definition = FacilityCardDatabase.Get(state, item.DefinitionId);
            if (definition == null || !ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId, "core.facility_card")) return false;
            var queries = registry?.FacilityBuildQueries ?? new FacilityBuildQueryService();
            var built=queries.ResolveBuiltFacts(state,playerId,selection.EffectId);
            if (FacilityCardDatabase.PlayerHasBuiltUniqueFacilityForInstances(state, player, definition,built.ItemIds) || queries.ResolveSlots(state, playerId, target, selection.EffectId).Count == 0)
            { reason = "invalid_build_slot"; return false; }
            cost = mode == "gold" ? new ResourceSet { GoldVoucher = definition.GoldVoucherCost } : new FacilityBuildCostService().GetEffectiveResourceCostForInstances(
                state, player, definition, built.ItemIds);
            reason = ""; return true;
        }

        public static bool TryQuote(GameState state, int playerId, string actionKind, string targetId, NormalizedValue arguments,
            out ResourceSet cost, out string reason, EffectRegistry registry = null, string sourceEffectId = "")
        {
            cost = null; reason = "payment_preparation_arguments_invalid";
            if (arguments?.Kind != NormalizedValueKind.Object || !arguments.TryValidate(out _) || state.FindPlayer(playerId) == null) return false;
            if (actionKind == "city_move")
            {
                if (arguments.Properties.Count != 0) return false;
                var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
                if (!string.IsNullOrEmpty(targetId))
                {
                    var validation = new CityMovementService(map, new InfluenceService(map), new TravelCostService(map)) { DestinationRegistry = registry }
                        .CanMoveCityEffect(state, playerId, targetId, true, sourceEffectId, "specified");
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
                !BuildFacilityService.CreateForEffectTree().ValidatePaidPlacement(state, playerId, item.ItemId, slot).IsValid)
            { reason = "invalid_build_slot"; return false; }
            cost = mode == BuildFacilityService.PaymentModeGold ? new ResourceSet { GoldVoucher = definition.GoldVoucherCost } :
                new FacilityBuildCostService().GetEffectiveResourceCost(state, player, definition);
            reason = ""; return true;
        }
    }

    public partial class EffectTreeExecutor
    {
        public bool TryPrepareEffectBuildPayment(int playerId, string selectionOwnerEffectId, string selectionInteractionId,
            string selectionCandidateSetId, int selectionCandidateSetVersion, string targetItemId, NormalizedValue actionArguments,
            int expectedRevision, string commandId, out string reason)
        {
            reason = "effect_build_context_invalid";
            if (expectedRevision != Runtime.StateRevision) { reason = "state_revision_mismatch"; return false; }
            if (Runtime.Status != EffectRuntimeStatus.Active || string.IsNullOrEmpty(commandId)) return false;
            var parent = GetNode(selectionOwnerEffectId);
            var request = Runtime.InteractionRequests.Find(q => q.GetStableInteractionId() == selectionInteractionId && q.Status == "open");
            if (parent?.EffectTypeId != FacilitySelectionEffectExecutor.TypeId || parent.PlayerId != playerId || parent.FlowStage != "deck_select" ||
                parent.PendingOutcome != EffectPendingOutcome.None || !FacilitySelectionEffectExecutor.IsNormalBuildSelection(parent) ||
                request?.OwnerEffectId != parent.EffectId || request.InteractionTypeId != FacilitySelectionEffectExecutor.NormalBuildInteractionType ||
                request.AnsweringPlayerId != playerId || request.StateRevision != expectedRevision || request.CandidateSetId != selectionCandidateSetId ||
                request.CandidateSetVersion != selectionCandidateSetVersion || !request.CandidateIds.Contains(targetItemId) ||
                Runtime.EffectNodes.Any(n => n.ParentEffectId == parent.EffectId && ResourcePaymentPreparationService.IsCurrentEffectBuildPreparation(state, n, playerId, true, registry))) return false;
            if (!ResourcePaymentPreparationService.ValidateEffectBuildCandidate(state, parent, request, targetItemId,
                Text(actionArguments, "expectedSourceZoneId"), Text(actionArguments, "paymentMode"), true, registry)) return false;
            if (!ResourcePaymentPreparationService.TryQuoteEffectBuildTarget(state, playerId, targetItemId, actionArguments, parent, out var cost, out reason, registry)) return false;
            var work = GameStateCloneService.DeepClone(state); parent = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == selectionOwnerEffectId);
            var input = FindInteraction(work.EffectRuntime, selectionInteractionId); input.Status = "invalidated";
            ResolveInteractionBlockerOnWorking(work.EffectRuntime, input.GetStableInteractionId());
            var item = work.ItemZones.Items.Find(i => i.ItemId == targetItemId);
            var spec = EffectSpec.Create(FacilityBuildEffectExecutor.PaymentTypeId, Object("facilityId", NormalizedValue.CreateString(item.DefinitionId),
                "paymentMode", NormalizedValue.CreateString(Text(actionArguments, "paymentMode"))), playerId);
            var entries = new List<RuleJournalEntry>();
            var ids = AppendChildEffects(work, parent, new[] { spec }, "preparation", commandId, entries);
            var owner = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == ids[0]);
            var payment = ResourcePaymentSubstitutionEffectExecutor.InitializePayment(owner, cost); payment.SourceKind = "build";
            ResourcePaymentSubstitutionEffectExecutor.EnableV2(payment, new ResourcePaymentDemandState { FixedCost = cost.Clone() }, new ResourceSet());
            payment.Preparation = new PreparedActionPaymentContext { AuthorizationKind = "effect_build", PreparationOwnerEffectId = owner.EffectId,
                ActionKind = "build", TargetId = targetItemId, ActionArguments = actionArguments.Clone(), SelectionOwnerEffectId = parent.EffectId,
                SelectionInteractionId = selectionInteractionId, SelectionCandidateSetId = selectionCandidateSetId, SelectionCandidateSetVersion = selectionCandidateSetVersion };
            parent.FlowStage = "deck_preparing"; Set(parent, "effectBuildPreparationId", NormalizedValue.CreateString(owner.EffectId));
            CommitPreparedWork(work, commandId, entries, -1, -1, ids);
            var report = RunUntilQuiescent(); if (report.Faulted) { reason = report.FaultCode; return false; }
            reason = ""; return true;
        }

        public bool TryCancelPreparedEffectBuildPayment(int playerId, string preparationOwnerEffectId, int expectedRevision, string commandId, out string reason)
        {
            reason = "effect_build_context_invalid";
            var source = GetNode(preparationOwnerEffectId);
            var cancelled = source?.Payment?.Preparation;
            if (cancelled?.IsCancelled == true && !string.IsNullOrEmpty(commandId) && cancelled.CancellationCommandId == commandId)
            {
                bool same = source.PlayerId == playerId && !source.Payment.IsCommitted && !source.Payment.IsCommitRequested && ResourcePaymentPreparationService.ValidateEffectBuildIdentity(state, source, out _) &&
                    cancelled.CancellationPlayerId == playerId && cancelled.CancellationRequestRevision == expectedRevision &&
                    cancelled.CancellationDigest == StableIdFactory.Create("payment-cancel", preparationOwnerEffectId, playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
                reason = same ? "" : "command_id_payload_mismatch"; return same;
            }
            if (expectedRevision != Runtime.StateRevision) { reason = "state_revision_mismatch"; return false; }
            if (string.IsNullOrEmpty(commandId) || !ResourcePaymentPreparationService.IsCurrentEffectBuildPreparation(state, source, playerId, false) ||
                source.PendingOutcome != EffectPendingOutcome.None || source.Payment.Preparation.IsPromoted || source.Payment.IsCommitted || source.Payment.IsCommitRequested) return false;
            if (ResourcePaymentPreparationService.HasPendingResponse(state, source)) { reason = "settlement_required_pending"; return false; }
            var work = GameStateCloneService.DeepClone(state); var owner = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == source.EffectId);
            var parent = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == owner.ParentEffectId);
            var entries = new List<RuleJournalEntry>(); CancelEffectBuildPreparationOnWorking(work, owner, parent, false, entries);
            owner.Payment.Preparation.CancellationCommandId = commandId; owner.Payment.Preparation.CancellationPlayerId = playerId;
            owner.Payment.Preparation.CancellationRequestRevision = expectedRevision;
            owner.Payment.Preparation.CancellationDigest = StableIdFactory.Create("payment-cancel", preparationOwnerEffectId, playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            CommitPreparedWork(work, commandId, entries, -1, -1, new[] { owner.EffectId, parent.EffectId });
            var report = RunUntilQuiescent(); if (report.Faulted) { reason = report.FaultCode; return false; }
            reason = ""; return true;
        }

        private void CancelEffectBuildPreparationOnWorking(GameState work, EffectNodeRuntimeState owner, EffectNodeRuntimeState parent, bool decline, List<RuleJournalEntry> entries)
        {
            ResourcePaymentSubstitutionEffectExecutor.CancelDraft(owner); owner.Payment.Preparation.IsCancelled = true;
            ClosePreparedPaymentInput(work, owner); parent.FlowStage = decline ? "deck_declined" : "deck_select";
            parent.ContinuationData.Properties.RemoveAll(e => e.Name == "effectBuildPreparationId");
            owner.PendingOutcome = EffectPendingOutcome.Failed; owner.FailureReason = "payment_preparation_cancelled";
            TryCompleteNodeOnWorking(work, owner, entries);
        }

        private bool TrySubmitEffectBuildPaymentAnswerOnWorking(InteractionRequest request, int playerId, NormalizedValue answer, out string reason)
        {
            reason = "effect_build_context_invalid"; var source = GetNode(request.OwnerEffectId);
            if (!ResourcePaymentPreparationService.IsCurrentEffectBuildPreparation(state, source, playerId, false) ||
                source.Payment.Preparation.IsPromoted || source.Payment.IsCommitRequested || source.Payment.IsCommitted ||
                request.InteractionTypeId != ResourceEffectExecutor.PaymentConfirmationInteractionTypeId ||
                !source.Payment.BeforePaymentReady || ResourcePaymentPreparationService.HasPendingResponse(state, source)) return false;
            var value = answer.Kind == NormalizedValueKind.Array && answer.Items.Count == 1 ? answer.Items[0] : answer;
            string selected = value.Kind == NormalizedValueKind.Boolean && !value.BooleanValue ? "decline" :
                value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value.StringValue;
            var parentSource = GetNode(source.ParentEffectId);
            if (selected != "pay" && selected != "decline" || selected == "decline" && !Flag(parentSource.NormalizedArguments, "allowDecline")) return false;
            if (selected == "pay" && (!ResourcePaymentPreparationService.IsCurrentEffectBuildPreparation(state, source, playerId, true, registry) || !ResourcePaymentPreparationService.CanConfirm(state, source, registry))) { reason = "payment_cannot_confirm"; return false; }
            var work = GameStateCloneService.DeepClone(state); var owner = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == source.EffectId);
            var parent = work.EffectRuntime.EffectNodes.Find(n => n.EffectId == owner.ParentEffectId); var input = FindInteraction(work.EffectRuntime, request.GetStableInteractionId());
            owner.ContinuationOperationContext = work.EffectRuntime.CurrentOperationContext?.Clone() ?? owner.ContinuationOperationContext;
            input.Status = "answered"; input.NormalizedAnswer = answer.Clone(); input.AnsweredAtRevision = work.EffectRuntime.StateRevision + 1;
            ResolveInteractionBlockerOnWorking(work.EffectRuntime, input.GetStableInteractionId());
            var entries = new List<RuleJournalEntry> { new RuleJournalEntry { Kind = RuleJournalEntryKind.Interaction, EntityId = input.GetStableInteractionId(), Detail = "interaction_answered", Payload = answer.Clone() } };
            int eventStart = work.EffectRuntime.RuleEvents.Count, receiptStart = work.EffectRuntime.DispatchReceipts.Count;
            DispatchEventsOnWorking(work, new[] { new EffectEventRequest { EventType = "InteractionAnswered", SourceEffectId = owner.EffectId,
                OwnerNodeId = owner.EffectId, PlayerId = playerId, Visibility = input.Visibility ?? "owner", ResponseKind = RuleEventResponseKind.Effects,
                SemanticKey = input.GetStableInteractionId(),
                Payload = Object("answer", answer.Clone(), "answeringPlayerId", NormalizedValue.CreateInteger(playerId), "requestId", NormalizedValue.CreateString(input.GetStableInteractionId())) } }, owner, entries);
            if (selected == "decline") CancelEffectBuildPreparationOnWorking(work, owner, parent, true, entries);
            else
            {
                var p = owner.Payment.Preparation;
                var spec = FacilityBuildEffectSpecFactory.Build(playerId, p.TargetId, -1, Text(p.ActionArguments, "paymentMode"), false,
                    parent.SourceId, Flag(parent.NormalizedArguments,"consumeMainAction"), Text(p.ActionArguments, "expectedSourceZoneId"),
                    Text(parent.NormalizedArguments,"sourceZone","supply"), Text(parent.NormalizedArguments,"sourceKey"));
                spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "preparedPaymentOwnerEffectId", Value = NormalizedValue.CreateString(owner.EffectId) });
                spec.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                var ids = AppendChildEffects(work, parent, new[] { spec }, "child", input.GetStableInteractionId(), entries);
                p.IsPromoted = true; p.PromotedActionEffectId = ids[0]; owner.Payment.IsCommitRequested = true;
                ClosePreparedPaymentInput(work, owner); owner.Status = HasUnresolvedBlocker(work.EffectRuntime, owner) ? EffectNodeStatus.Blocked : EffectNodeStatus.Ready;
                parent.FlowStage = "built"; Set(parent, "selectedItemIds", Strings(new[] { p.TargetId })); Set(parent, "selectedSourceZoneId", NormalizedValue.CreateString(Text(p.ActionArguments, "expectedSourceZoneId")));
            }
            CommitPreparedWork(work, "interaction.answer:" + input.GetStableInteractionId(), entries, eventStart, receiptStart, new[] { owner.EffectId, parent.EffectId });
            reason = ""; return true;
        }

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
                if (!ResourcePaymentPreparationService.TryQuote(state, playerId, actionKind, targetId, actionArguments, out var updatedCost, out diagnostic, registry) ||
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
            if (!ResourcePaymentPreparationService.TryQuote(state, playerId, actionKind, targetId ?? "", actionArguments, out var cost, out diagnostic, registry)) return false;
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
            ResourcePaymentSubstitutionEffectExecutor.EnableV2(payment, new ResourcePaymentDemandState { FixedCost = cost.Clone() }, new ResourceSet());
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
                source.Status != EffectNodeStatus.Blocked || !ResourcePaymentPreparationService.CanConfirm(state, source, registry) ||
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
            if (!ResourcePaymentPreparationService.TryQuote(state, playerId, kind, target, finalArguments, out var current, out diagnostic, registry) ||
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
