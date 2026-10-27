using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class AbilityActionExecutor
    {
        public const string DirectAdmissionVersion="direct_admission.v1.0.1";
        public const string CostReceiptPolicyVersion = "cost_operation_receipts.v2";
        public const string TypeId = "effect.ability.action.invoke", DirectTypeId = "effect.ability.action.authorized", Version = "1.1.0";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, Version));
            if (!registry.TryGet(DirectTypeId, out _)) registry.Register(new EffectRegistration(DirectTypeId, Execute, EffectExecutorKind.IntrinsicFlow, Version));
        }
        public static string Digest(NormalizedValue value)
        { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToDeterministicString())).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
        public static NormalizedValue CostValue(ResourceSet value) => Object("Originium", NormalizedValue.CreateInteger(value.Originium), "OriginiumShard", NormalizedValue.CreateInteger(value.OriginiumShard),
            "Iron", NormalizedValue.CreateInteger(value.Iron), "PureOriginium", NormalizedValue.CreateInteger(value.PureOriginium), "GoldVoucher", NormalizedValue.CreateInteger(value.GoldVoucher));
        public static EffectSpec Create(AbilityActionOption option, string commandId, NormalizedValue arguments = null)
        {
            var spec = EffectSpec.Create(TypeId, Object("optionId", NormalizedValue.CreateString(option.OptionId), "commandId", NormalizedValue.CreateString(commandId),
                "requestId", NormalizedValue.CreateString(option.RequestRef.InteractionId), "factsDigest", NormalizedValue.CreateString(option.RequestRef.FactsDigest),
                "contentHash", NormalizedValue.CreateString(option.VersionRef.ContentPackHash), "arguments", arguments?.Clone() ?? Object()), option.SourceRef.AuthorizedPlayerId);
            spec.DefinitionVersion = Version; spec.StableKey = "command:" + commandId; return spec;
        }
        public static EffectSpec CreateAuthorized(int player, string itemId, string actionId, string callPolicyRef) => EffectSpec.Create(DirectTypeId,
            Object("itemId", NormalizedValue.CreateString(itemId), "actionId", NormalizedValue.CreateString(actionId), "callPolicyRef", NormalizedValue.CreateString(callPolicyRef)), player);

        public static string PreviewCost(GameState state, int player, IList<EffectSpec> costs) =>
            PreviewCost(state, player, costs, null, null);
        public static string PreviewCost(GameState state, EffectRegistry registry, AbilitySourceRef source, IList<EffectSpec> costs) =>
            source == null || registry == null ? "entry_cost_source_required" : PreviewCost(state, source.AuthorizedPlayerId, costs,
                spec => registry.PreviewItemTransferEntryCost(state, source, spec), (spec, cost) => ResourcePaymentCostPreview.CanPay(state, registry,
                    new EffectNodeRuntimeState { EffectId = StableIdFactory.Create("action-cost-preview", source.SourceKey, spec.EffectTypeId),
                        PlayerId = source.AuthorizedPlayerId, EffectTypeId = spec.EffectTypeId, NormalizedArguments = spec.NormalizedArguments.Clone() }, cost));
        private static string PreviewCost(GameState state, int player, IList<EffectSpec> costs, Func<EffectSpec, string> transferPreview, Func<EffectSpec, ResourceSet, bool> resourcePreview)
        {
            string shape = AbilityActionEntryCondition.Unwrap(costs, out var payment);
            if (shape.Length > 0) return shape;
            costs = payment;
            // 多根费用可能共享替代库存；交给005/007的同权威纯报价器，不能逐根够付就声称整组可付。
            if (costs == null || costs.Count == 0) return "";
            if (costs.Count > 1) return "entry_cost_joint_preview_unavailable";
            var total = new ResourceSet();
            foreach (var spec in costs)
            {
                if (spec.PlayerId != player && spec.PlayerId >= 0) return "entry_cost_player_invalid";
                if (AbilityActionRecovery.IsRetiredMarkerPayment(spec.EffectTypeId)) return AbilityActionRecovery.RetiredMarkerPaymentDiagnostic;
                if (spec.EffectTypeId == ResourceEffectTypeIds.Pay)
                {
                    var args = spec.NormalizedArguments; var cost = Value(args, "cost");
                    if (cost != null) foreach (var entry in cost.Properties)
                    { if (!Enum.TryParse(entry.Name, out ResourceType type) || entry.Value.Kind != NormalizedValueKind.Integer || entry.Value.IntegerValue < 0 || entry.Value.IntegerValue > int.MaxValue) return "entry_cost_invalid"; total.Set(type, checked(total.Get(type) + (int)entry.Value.IntegerValue)); }
                    else
                    {
                        string resource = Text(args, "resourceType");
                        if (!Enum.TryParse(resource, true, out ResourceType type)) return "entry_cost_preview_unsupported";
                        int amount = Number(args, "amount", -1); if (amount < 0) return "entry_cost_invalid";
                        total.Set(type, checked(total.Get(type) + amount));
                    }
                }
                else if (spec.EffectTypeId == ResourcePaymentChoiceEffectExecutor.TypeId)
                {
                    var choices = Value(spec.NormalizedArguments, "options");
                    bool available = choices?.Kind == NormalizedValueKind.Array && choices.Items.Any(c =>
                    {
                        var cost = new ResourceSet(); foreach (var e in c.Properties) cost.Set((ResourceType)Enum.Parse(typeof(ResourceType), e.Name), checked((int)e.Value.IntegerValue));
                        return resourcePreview != null ? resourcePreview(spec, cost) : ResourceEffectExecutor.CanPayWithStoredMarkers(state, player, cost);
                    });
                    if (!available) return "insufficient_resources";
                }
                else if (spec.EffectTypeId == ItemTransferEffectExecutor.TypeId)
                    return transferPreview == null ? "entry_cost_source_required" : transferPreview(spec);
                else return "entry_cost_preview_unsupported";
            }
            return (resourcePreview != null ? resourcePreview(costs[0], total) : ResourceEffectExecutor.CanPayWithStoredMarkers(state, player, total)) ? "" : "insufficient_resources";
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node = context.Node; var registry = context.Registry; var state = context.State;
            var invocation = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.OwnerEffectId == node.EffectId);
            if (invocation == null)
            {
                bool direct = node.EffectTypeId == DirectTypeId;
                string command = direct ? "effect:" + node.EffectId : Text(node.NormalizedArguments, "commandId");
                if (string.IsNullOrWhiteSpace(command)) return EffectStepResult.Failed("action_command_id_missing");
                string digest = Digest(node.NormalizedArguments);
                var duplicate = state.EffectRuntime.AbilityActionInvocations.Find(i => i.CommandId == command);
                if (duplicate != null) return EffectStepResult.Failed(duplicate.CommandDigest == digest ? "action_command_already_accepted" : "action_command_payload_conflict");
                if(direct && !AbilityLegacyStyleBridge.IsBridge(node))
                {
                    if(!registry.TryCallPolicy(Text(node.NormalizedArguments,"callPolicyRef"),out var callerPolicy)) return EffectStepResult.Failed("action_direct_policy_denied");
                    string callerError=registry.ValidateActionCaller(state,node.ParentEffectId,callerPolicy,node.PlayerId);
                    if(callerError.Length>0) return EffectStepResult.Failed(callerError);
                }
                var option = AbilityLegacyStyleBridge.IsBridge(node) ? AbilityLegacyStyleBridge.QueryOption(context) : direct ? registry.QueryAuthorizedActions(state,node.ParentEffectId,Text(node.NormalizedArguments,"callPolicyRef")).SingleOrDefault(o => o.SourceRef.ItemId == Text(node.NormalizedArguments, "itemId") && o.ActionId == Text(node.NormalizedArguments, "actionId")) :
                    registry.QueryActions(state, node.PlayerId).SingleOrDefault(o => o.OptionId == Text(node.NormalizedArguments, "optionId"));
                if (option == null) return EffectStepResult.Failed("action_source_invalid");
                AbilityActionCallPolicy policy = null;
                if (direct)
                {
                    if (AbilityLegacyStyleBridge.IsBridge(node))
                    { if (!option.Enabled) return EffectStepResult.Failed(option.DisabledReason); policy = AbilityLegacyStyleBridge.Policy; }
                    else { string error = ValidateDirect(context, option, out policy); if (error.Length > 0) return EffectStepResult.Failed(error); }
                }
                else if (!option.Enabled || Text(node.NormalizedArguments, "requestId") != option.RequestRef.InteractionId ||
                    Text(node.NormalizedArguments, "factsDigest") != option.RequestRef.FactsDigest || Text(node.NormalizedArguments, "contentHash") != option.VersionRef.ContentPackHash)
                    return EffectStepResult.Failed(option.Enabled ? "action_request_expired" : option.DisabledReason);
                var action = registry.ActionDeclaration(option.ActionId);
                string argumentError = registry.NormalizeActionArguments(action.ActionId, Value(node.NormalizedArguments, "arguments") ?? Object(), out _);
                if (argumentError.Length > 0) return EffectStepResult.Failed(argumentError);
                string occupied = AbilitySourceService.OccupancyKey(state, option.SourceRef, action);
                if (state.EffectRuntime.AbilityActionInvocations.Any(i => i.OccupancyKey == occupied && (AbilitySourceService.InvocationActive(state, i) || i.ActivationCommitted && AbilitySourceService.ConsumesLifecycle(action)))) return EffectStepResult.Failed("action_group_occupied");
                invocation = new AbilityActionInvocation { InvocationId = StableIdFactory.Create("action-invocation", state.GameId, command), OwnerEffectId = node.EffectId,
                    ParentEffectId = node.ParentEffectId, CommandId = command, CommandDigest = digest, OptionId = option.OptionId, ActionId = option.ActionId,
                    OccupancyKey = occupied, SourceRef = GameStateCloneService.DeepClone(option.SourceRef), VersionRef = GameStateCloneService.DeepClone(option.VersionRef),
                    TrackBindingId = option.TrackBindingId, UsageProviderVersion = registry.UsageProvider(action.UsageTrackId)?.DefinitionVersion ?? "",
                    RequestRef = GameStateCloneService.DeepClone(option.RequestRef), CallPolicyRef = policy?.PolicyRef ?? "" };
                invocation.ReceiptIds.Add(StableIdFactory.Create("action-receipt", invocation.InvocationId, "accepted"));
                state.EffectRuntime.AbilityActionInvocations.Add(invocation);
                state.EffectRuntime.AbilityActionProtocolVersion = 1;
                return EffectStepResult.Continue("accepted");
            }
            var declaration = registry.ActionDeclaration(invocation.ActionId);
            registry.TryCallPolicy(invocation.CallPolicyRef, out var callPolicy);
            if (invocation.CallPolicyRef == AbilityLegacyStyleBridge.PolicyRef) callPolicy = AbilityLegacyStyleBridge.Policy;
            string normalizedError = registry.NormalizeActionArguments(declaration.ActionId, Value(node.NormalizedArguments, "arguments") ?? Object(), out var normalizedArguments);
            if (normalizedError.Length > 0) return Fault(invocation, normalizedError);
            var call = new AbilityActionCallContext { State = state, OwnerNode = node, SourceRef = invocation.SourceRef, Action = declaration, Arguments = normalizedArguments };
            if (invocation.Phase == "accepted")
            {
                if (!SourceCurrent(state, registry, invocation)) return Complete(invocation, "source_invalid_before_payment", false);
                string entryError = ValidateInvocationEntry(context, invocation, declaration, callPolicy);
                if (entryError.Length > 0) return Complete(invocation, entryError, false);
                var costs = declaration.EntryCostRef.Length > 0 && callPolicy?.OmitEntryCost != true ? registry.ConstructAbilityBody(declaration.EntryCostRef, call) : new List<EffectSpec>();
                string error = registry.ValidateActionEntryCost(state, invocation.SourceRef, declaration, costs); if (error.Length > 0) return Complete(invocation, error, false);
                if (AbilityActionEntryCondition.IsGate(costs))
                {
                    invocation.ProtocolVersion = AbilityActionEntryCondition.InvocationProtocolVersion;
                    invocation.EntryConditionVersion = AbilityActionEntryCondition.Version;
                    invocation.EntryConditionHandlerHash = registry.HandlerHash(declaration.EntryCostRef);
                    invocation.EntryConditionHandlerVersion = registry.GetAbilityHandlerVersion(declaration.EntryCostRef);
                    invocation.EntryConditionProgramDigest = AbilityActionEntryCondition.ProgramDigest(costs[0]);
                }
                invocation.Phase = "entry_payment";
                var step = EffectStepResult.Continue("entry_payment");
                foreach (var cost in costs) step.AddChild(cost);
                return step;
            }
            if (invocation.Phase == "entry_payment")
            {
                invocation.PaymentEffectIds = context.ChildNodes.Select(n => n.EffectId).ToList();
                var subtree = Descendants(state, node.EffectId);
                invocation.PaymentCommitted = HasCommittedEntryPayment(state, invocation);
                if (subtree.Any(n => n.Status == EffectNodeStatus.Faulted)) throw new InvalidOperationException("action_payment_child_faulted");
                if (EntryPaymentNodes(state, invocation).Any(n => n.Status == EffectNodeStatus.Completed && TokenTransferReceiptRequired(n) && !HasCommittedTokenTransfer(state, n)))
                    return Fault(invocation, "action_cost_transfer_receipt_missing");
                if (subtree.Any(n => n.Status == EffectNodeStatus.Failed))
                {
                    if (invocation.PaymentCommitted) return Fault(invocation, "action_payment_incomplete_after_commit");
                    return Complete(invocation, "entry_payment_declined", false);
                }
                if (!string.IsNullOrEmpty(invocation.EntryConditionVersion))
                {
                    string gateError = AbilityActionEntryCondition.ValidateReceipt(state, registry, invocation);
                    if (gateError.Length > 0) return Fault(invocation, gateError);
                    var gate = context.ChildNodes.Single();
                    if (gate.Status != EffectNodeStatus.Completed || Text(gate.NormalizedResult, "outcome") != "condition_met")
                        return invocation.PaymentCommitted ? Fault(invocation, "entry_condition_failed_after_payment") : Complete(invocation, "entry_condition_not_met", false);
                }
                if (!SourceCurrent(state, registry, invocation))
                    return invocation.PaymentCommitted ? Fault(invocation, "action_source_invalid_after_payment") : Complete(invocation, "source_invalid_before_activation", false);
                string activationError = ValidateInvocationEntry(context, invocation, declaration, callPolicy);
                if (activationError.Length > 0) return invocation.PaymentCommitted ? Fault(invocation, activationError) : Complete(invocation, activationError, false);
                if (declaration.UsageTrackId.Length > 0)
                {
                    var provider = registry.UsageProvider(declaration.UsageTrackId);
                    string currentBinding = "";
                    string reason = provider?.Validate(state, invocation.SourceRef, declaration, out currentBinding) ?? "usage_track_unavailable";
                    if (reason.Length == 0 && (currentBinding != invocation.TrackBindingId || provider.DefinitionVersion != invocation.UsageProviderVersion)) reason = "usage_binding_changed";
                    if (reason.Length > 0) return invocation.PaymentCommitted ? Fault(invocation, reason) : Complete(invocation, reason, false);
                    provider.Activate(context, invocation, declaration);
                }
                if (declaration.LockPolicy == "character_action_round") new MainActionBudgetService().LockCharacterCard(state, node.PlayerId);
                invocation.ActivationCommitted = true; invocation.Phase = "activated";
                var bodies = registry.ConstructAbilityBody(declaration.BodyHandlerRef, call);
                if (declaration.EntryAfterEffectsRef.Length > 0 && callPolicy?.OmitEntryAfterEffects != true) bodies = bodies.Concat(registry.ConstructAbilityBody(declaration.EntryAfterEffectsRef, call)).ToList();
                invocation.ReceiptIds.Add(StableIdFactory.Create("action-receipt", invocation.InvocationId, "activated"));
                var step = EffectStepResult.Continue("activated"); foreach (var body in bodies) step.AddChild(body); return step;
            }
            if (invocation.Phase == "activated")
            {
                invocation.BodyEffectIds = context.ChildNodes.Select(n => n.EffectId).Except(invocation.PaymentEffectIds).ToList();
                invocation.Phase = "body"; return EffectStepResult.Continue("body");
            }
            if (invocation.Phase == "body")
            {
                var children = Descendants(state, node.EffectId);
                if (children.Any(n => n.Status == EffectNodeStatus.Faulted)) return Fault(invocation, "action_body_faulted");
                var strictFailure = children.FirstOrDefault(n => n.Status == EffectNodeStatus.Failed && n.FailureReason != "player_declined" &&
                    n.EffectTypeId == ItemTransferEffectExecutor.TypeId && !ItemTransferEffectExecutor.IsExpectedIndependentFailure(n) && !ConsumedByCondition(state, n));
                if (strictFailure != null) return Fault(invocation, "action_required_transfer_failed:" + strictFailure.EffectId + ":" + strictFailure.FailureReason);
                // 正文自己的条件不成立、空集合或玩家放弃不撤销已激活的行动。
                if (declaration.EntryKind == "special" && callPolicy?.OmitMainAction != true && !invocation.MainActionCommitted)
                {
                    string budgetError = registry.ValidateActionMainBudget(state, node.PlayerId, declaration, callPolicy);
                    if (budgetError.Length > 0) return Fault(invocation, budgetError);
                    var player = state.FindPlayer(node.PlayerId); int before = player.CompletedMainActionsThisTurn, remaining = player.RemainingMainActionsThisTurn;
                    new MainActionBudgetService().SpendCompletedMainAction(state, node.PlayerId, true);
                    if (player.CompletedMainActionsThisTurn != checked(before + 1) || player.RemainingMainActionsThisTurn != remaining - 1) return Fault(invocation, "action_main_budget_commit_missing");
                    invocation.MainActionCommitted = true;
                }
                return Complete(invocation, "completed", true);
            }
            if (invocation.Phase == "completed") return EffectStepResult.Completed(NormalizedValue.CreateString(invocation.InvocationId));
            return Fault(invocation, "action_phase_invalid");
        }

        private static string ValidateDirect(EffectExecutionContext context, AbilityActionOption option, out AbilityActionCallPolicy policy)
        {
            policy = null; var node = context.Node; var registry = context.Registry;
            if (!registry.TryCallPolicy(Text(node.NormalizedArguments, "callPolicyRef"), out policy) || policy.TargetActionId != option.ActionId ||
                registry.ActionDeclaration(option.ActionId).DirectCallPolicy != policy.PolicyRef) return "action_direct_policy_denied";
            var authorizedPolicy = policy;
            string callerError = registry.ValidateActionCaller(context.State, node.ParentEffectId, policy, node.PlayerId);
            if (callerError.Length > 0) return callerError;
            // 只允许编译自同一冻结正文的子式；任意外部根、客户端参数、伪装SourceId均不能授权。
            if (!context.State.EffectRuntime.DispatchReceipts.Any(r => r.AttachedEffectIds.Contains(node.EffectId) && r.AbilityId == authorizedPolicy.CallerAbilityId) &&
                (node.ContinuationAbilityId != policy.CallerAbilityId || node.ContinuationContentHash != policy.CallerHandlerHash)) return "action_direct_provenance_missing";
            var action = registry.ActionDeclaration(option.ActionId);
            return registry.ValidateAuthorizedActionEntry(context.State, option.SourceRef, action, policy);
        }
        private static string ValidateInvocationEntry(EffectExecutionContext context, AbilityActionInvocation invocation, AbilityActionDeclaration action, AbilityActionCallPolicy policy)
        {
            if (policy == null || policy.PolicyRef == AbilityLegacyStyleBridge.PolicyRef)
                return context.Registry.ValidateActionMainBudget(context.State, context.Node.PlayerId, action, policy);
            string callerError = context.Registry.ValidateActionCaller(context.State, invocation.ParentEffectId, policy, context.Node.PlayerId);
            return callerError.Length > 0 ? callerError : context.Registry.ValidateAuthorizedActionEntry(context.State, invocation.SourceRef, action, policy, invocation.InvocationId, false);
        }
        private static EffectStepResult Complete(AbilityActionInvocation invocation, string outcome, bool success)
        {
            invocation.Phase = "completed"; invocation.Outcome = outcome;
            invocation.ReceiptIds.Add(StableIdFactory.Create("action-receipt", invocation.InvocationId, "completed"));
            return success ? EffectStepResult.Completed(NormalizedValue.CreateString(invocation.InvocationId)) : EffectStepResult.Failed(outcome);
        }
        private static bool SourceCurrent(GameState state, EffectRegistry registry, AbilityActionInvocation invocation) => invocation.CallPolicyRef == AbilityLegacyStyleBridge.PolicyRef
            ? AbilityLegacyStyleBridge.SourceCurrent(state, invocation) : AbilitySourceService.IsCurrent(state, registry.AbilityDeclarations, invocation.SourceRef);
        // 通用转移的已提交事实来自原执行节点/真实位置变化和原批次收据，不根据载体或物品名称推断费用。
        public static bool IsTokenTransferOperation(EffectNodeRuntimeState node)
        {
            return node?.EffectTypeId == ItemTransferEffectExecutor.TypeId && (node.TransferBatchRequest != null ?
                node.TransferBatchRequest.Transfers?.Count > 0 && node.TransferBatchRequest.Transfers.All(t => t != null && ItemTransferService.IsTokenProfile(t.OperationProfileId)) :
                ItemTransferService.IsTokenProfile(Text(node.NormalizedArguments, "operationProfileId")));
        }
        public static bool HasCommittedTokenTransfer(GameState state, EffectNodeRuntimeState node)
        {
            if (state?.ItemZones?.Items == null || !IsTokenTransferOperation(node) ||
                node.Status != EffectNodeStatus.Completed && node.PendingOutcome != EffectPendingOutcome.Completed ||
                Value(node.NormalizedResult, "changed")?.Kind != NormalizedValueKind.Boolean || !Flag(node.NormalizedResult, "changed")) return false;
            var ids = Value(node.NormalizedResult, "movedItemIds"); int count = Number(node.NormalizedResult, "movedCount");
            if (count < 1 || ids?.Kind != NormalizedValueKind.Array || ids.Items == null || ids.Items.Count != count ||
                ids.Items.Any(i => i == null || i.Kind != NormalizedValueKind.String || string.IsNullOrEmpty(i.StringValue)) ||
                ids.Items.Select(i => i.StringValue).Distinct(StringComparer.Ordinal).Count() != count || node.PositionChanges == null ||
                ids.Items.Any(id => node.PositionChanges.Count(p => p?.ItemId == id.StringValue) != 1) ||
                node.PositionChanges.Where(p => p != null && ids.Items.Any(id => id.StringValue == p.ItemId)).Any(p => p.From == null || p.To == null ||
                    string.IsNullOrEmpty(p.From.ZoneId) || string.IsNullOrEmpty(p.To.ZoneId) ||
                    p.From.ZoneId == p.To.ZoneId && p.From.SlotId == p.To.SlotId) ||
                ids.Items.Any(id => !state.ItemZones.Items.Any(item => item.ItemId == id.StringValue &&
                    ItemTransferService.IsTokenKind(item.PhysicalKind)))) return false;
            if (node.TransferBatchRequest != null || Value(node.ContinuationData, "transferBatchReceipt") != null)
                return node.TransferBatchRequest?.ProtocolVersion == ItemTransferService.TokenTransferProtocolVersion &&
                    ItemTransferService.TryValidateTokenState(state, node, out _) &&
                    node.TransferBatchRequest.Transfers.Sum(t => (long)t.RequiredCount) == count;
            // 既有单笔api1没有批次对象；仍核原请求的实例、足额数量及真实来源/去向。
            var requested = Value(node.NormalizedArguments, "itemRefs");
            string from = Text(Value(node.NormalizedArguments, "expectedSource"), "zoneId"), to = Text(Value(node.NormalizedArguments, "destination"), "zoneId");
            return Number(node.NormalizedArguments, "apiVersion") == 1 && Number(node.NormalizedArguments, "requiredCount", -1) == count &&
                requested?.Kind == NormalizedValueKind.Array && requested.Items?.Count == count &&
                requested.Items.Select(i => Text(i, "itemId")).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(ids.Items.Select(i => i.StringValue).OrderBy(id => id, StringComparer.Ordinal)) &&
                node.PositionChanges.Where(p => p != null && ids.Items.Any(id => id.StringValue == p.ItemId)).All(p => p.From.ZoneId == from && p.To.ZoneId == to);
        }
        public static bool TokenTransferReceiptRequired(EffectNodeRuntimeState node) => IsTokenTransferOperation(node) &&
            (Number(node.NormalizedArguments, "requiredCount") > 0 || Number(node.NormalizedResult, "movedCount") > 0 ||
                node.TransferBatchRequest?.Transfers.Any(t => t.RequiredCount > 0) == true);
        private static bool CommittedOperation(GameState state, EffectNodeRuntimeState node) => ResourcePaymentSourceService.IsResourcePaymentNode(node) && node.Payment?.IsCommitted == true ||
            node?.EffectTypeId == ResourceEffectTypeIds.Pay && node.Payment == null && node.Status == EffectNodeStatus.Completed || HasCommittedTokenTransfer(state, node);
        public static IEnumerable<EffectNodeRuntimeState> EntryPaymentNodes(GameState state, AbilityActionInvocation invocation)
        {
            var roots = AbilityActionEntryCondition.PaymentRoots(state, invocation);
            if (roots.Count == 0) return Enumerable.Empty<EffectNodeRuntimeState>();
            return Descendants(state, invocation.OwnerEffectId).Where(n => roots.Any(id => InfluenceDepartureProtocol.IsWithin(state, n.EffectId, id)));
        }
        public static bool HasCommittedEntryPayment(GameState state, AbilityActionInvocation invocation) => EntryPaymentNodes(state, invocation).Any(n => CommittedOperation(state, n));
        public static bool IsCommittedCost(GameState state, EffectNodeRuntimeState node)
        {
            if (ResourcePaymentSourceService.IsResourcePaymentNode(node) && node.Payment?.IsCommitted == true || node?.EffectTypeId == ResourceEffectTypeIds.Pay && node.Payment == null && node.Status == EffectNodeStatus.Completed) return true;
            if (!HasCommittedTokenTransfer(state, node)) return false;
            string childId = node.EffectId; var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(childId))
            {
                if (state.EffectRuntime.AbilityActionInvocations.Any(i => i.PaymentEffectIds?.Contains(childId) == true &&
                    InfluenceDepartureProtocol.IsWithin(state, childId, i.OwnerEffectId))) return true;
                var child = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == childId);
                var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == child?.ParentEffectId);
                if (parent == null) return false;
                if (parent.EffectTypeId == EffectTypeIds.Condition && parent.NestedGroupSizes?.Count >= 2 && parent.NestedGroupSizes[0] > 0)
                {
                    var programChildren = parent.ChildEffectIds.Where(id => state.EffectRuntime.Blockers.Any(b =>
                        b.OwnerEffectId == parent.EffectId && b.TargetEffectId == id && b.BlockerKind == "child"));
                    if (programChildren.Take(parent.NestedGroupSizes[0]).Contains(childId)) return true;
                }
                childId = parent.EffectId;
            }
            return false;
        }
        private static bool ConsumedByCondition(GameState state, EffectNodeRuntimeState failed)
        {
            string parent = failed.ParentEffectId;
            while (!string.IsNullOrEmpty(parent))
            {
                var node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == parent); if (node == null) return false;
                if (node.EffectTypeId == EffectTypeIds.Condition && Text(node.NormalizedResult, "outcome") == "condition_not_met") return true;
                parent = node.ParentEffectId;
            }
            return false;
        }
        private static EffectStepResult Fault(AbilityActionInvocation invocation, string reason)
        {
            invocation.FirstFaultPhase = invocation.Phase; invocation.Phase = "faulted"; invocation.FailureReason = reason;
            throw new KernelException(EffectFaultCodes.InvalidEffectSpec, reason);
        }
        internal static List<EffectNodeRuntimeState> Descendants(GameState state, string owner)
        {
            var result = new List<EffectNodeRuntimeState>(); var ids = new HashSet<string> { owner };
            bool added; do { added = false; foreach (var node in state.EffectRuntime.EffectNodes) if (ids.Contains(node.ParentEffectId) && ids.Add(node.EffectId)) { result.Add(node); added = true; } } while (added);
            return result;
        }
        internal static void ObserveBodyStep(GameState state, EffectNodeRuntimeState node)
        {
            foreach (var invocation in state.EffectRuntime.AbilityActionInvocations.Where(i => i.Phase == "activated"))
                if (Descendants(state, invocation.OwnerEffectId).Any(n => n.EffectId == node.EffectId) && !invocation.PaymentEffectIds.Contains(node.EffectId))
                { invocation.Phase = "body"; invocation.BodyEffectIds = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == invocation.OwnerEffectId).ChildEffectIds.Except(invocation.PaymentEffectIds).ToList(); }
        }
    }
    public partial class EffectTreeExecutor
    {
        public bool TryInvokeAbilityAction(int player, string optionId, string requestId, string factsDigest, string contentHash, string commandId,
            NormalizedValue arguments, out string invocationOwnerId, out string diagnostic)
        {
            invocationOwnerId = ""; diagnostic = "";
            var existing = Runtime.AbilityActionInvocations.Find(i => i.CommandId == commandId);
            if (existing != null)
            {
                if (registry.NormalizeActionArguments(existing.ActionId, arguments ?? Object(), out var canonicalReplay).Length > 0)
                { diagnostic = "action_command_payload_conflict"; return false; }
                var oldNode = Runtime.EffectNodes.Find(n => n.EffectId == existing.OwnerEffectId);
                var args = Object("optionId", NormalizedValue.CreateString(optionId), "commandId", NormalizedValue.CreateString(commandId), "requestId", NormalizedValue.CreateString(requestId),
                    "factsDigest", NormalizedValue.CreateString(factsDigest), "contentHash", NormalizedValue.CreateString(contentHash), "arguments", canonicalReplay);
                if (oldNode?.PlayerId != player || existing.CommandDigest != AbilityActionExecutor.Digest(args)) { diagnostic = "action_command_payload_conflict"; return false; }
                invocationOwnerId = oldNode.EffectId; return true;
            }
            var option = registry.QueryActions(state, player).SingleOrDefault(o => o.OptionId == optionId);
            if (string.IsNullOrWhiteSpace(commandId) || option?.Enabled != true || option.RequestRef.InteractionId != requestId || option.RequestRef.FactsDigest != factsDigest || option.VersionRef.ContentPackHash != contentHash)
            { diagnostic = option?.DisabledReason.Length > 0 ? option.DisabledReason : "action_request_expired"; return false; }
            diagnostic = registry.NormalizeActionArguments(option.ActionId, arguments ?? Object(), out var canonicalArguments);
            if (diagnostic.Length > 0) return false;
            return TryCreatePlayerActionEffect(player, AbilityActionExecutor.Create(option, commandId, canonicalArguments), "", commandId, out invocationOwnerId) || FailInvoke(out diagnostic);
        }
        private bool FailInvoke(out string diagnostic) { diagnostic = LastDiagnostic; return false; }
    }
}
