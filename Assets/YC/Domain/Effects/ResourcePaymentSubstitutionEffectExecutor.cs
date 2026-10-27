using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public delegate bool BoundedResourceAllocationDecoder(string encoded, IReadOnlyList<string> allowedIds, int minimumTotal,
        int maximumTotal, IReadOnlyDictionary<string, int> rowMaximums, out Dictionary<string, int> amounts, out string diagnostic);

    [Serializable]
    public sealed class PaymentSubstitutionSelection
    {
        public string SourceZoneId = "", SourceItemId = "";
        public ResourceSet Allocation = new ResourceSet();
    }

    [Serializable]
    public sealed class ResourcePaymentRuntimeState
    {
        public string PaymentId = "", OwnerEffectId = "";
        public int PlayerId = -1, SubstitutionBudget;
        public ResourceSet PaymentCost = new ResourceSet();
        public PaymentSubstitutionSelection SelectedSubstitution;
        public bool IsCommitted, IsCommitRequested;
        public string BeforePaymentEventId = "", SourceKind = "pay", RuleConditionOwnerEffectId = "";
        public bool BeforePaymentReady;
        public bool AllowsSubstitution = true;
        public int PaymentGeneration = 1;
        public int PaymentProtocolVersion;
        public PreparedActionPaymentContext Preparation;
        public ResourcePaymentDemandState Demand;
        public ResourceSet SelectedAllocation = new ResourceSet(), GrossCost = new ResourceSet(), FinalCost = new ResourceSet();
        public string Phase = "demand_frozen";
        public List<PaymentSourceDraft> SourceDrafts = new List<PaymentSourceDraft>();
        public List<PaymentSourceEdit> SourceEdits = new List<PaymentSourceEdit>();
        public List<PaymentSourceReceipt> SourceReceipts = new List<PaymentSourceReceipt>();
        public List<PaymentBeforeReceipt> BeforePaymentReceipts = new List<PaymentBeforeReceipt>();
        public PaymentFinalReceipt FinalReceipt;
    }

    /// <summary>同笔替代草稿；分配只保存计划，原宿主最终确认后才移回标记并扣余款。</summary>
    public static class ResourcePaymentSubstitutionEffectExecutor
    {
        public const string TypeId = "effect.resource.payment_substitution";
        public const string InteractionTypeId = "effect.resource.payment_substitution_allocation";
        public const string AnswerSchema = "bounded_resource_allocation";
        public static readonly IReadOnlyList<ResourceType> BasicTypes = Array.AsReadOnly(new[] { ResourceType.Originium, ResourceType.OriginiumShard, ResourceType.Iron });

        public static void Register(EffectRegistry registry) => Register(registry, ResourceAllocationAnswer.TryParseBounded);

        public static void Register(EffectRegistry registry, BoundedResourceAllocationDecoder decoder)
        {
            if (registry == null || decoder == null) throw new ArgumentNullException();
            if (!registry.TryGet(TypeId, out _)) registry.Register(new EffectRegistration(TypeId, c => Execute(c, decoder),
                EffectExecutorKind.IntrinsicFlow, "1.0.0") { Validator = ValidateSpec });
        }

        public static EffectSpec Create(int playerId, string paymentOwnerEffectId, string sourceZoneId, int substitutionBudget)
        {
            var spec = EffectSpec.Create(TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1),
                "player", NormalizedValue.CreateString(playerId.ToString(CultureInfo.InvariantCulture)),
                "paymentOwnerEffectId", NormalizedValue.CreateString(paymentOwnerEffectId),
                "sourceZoneId", NormalizedValue.CreateString(sourceZoneId),
                "substitutionBudget", NormalizedValue.CreateInteger(substitutionBudget)), playerId);
            spec.DefinitionVersion = "1.0.0"; return spec;
        }

        public static string ValidateSpec(EffectSpec spec) => ValidateArguments(spec?.NormalizedArguments);

        public static string ValidateArguments(NormalizedValue args)
        {
            if (Number(args, "apiVersion") == 2) return ResourcePaymentInsertionService.ValidateArguments(args);
            return args?.Kind != NormalizedValueKind.Object || Number(args, "apiVersion") != 1 ||
                CharacterLifecycleService.ParsePlayer(Text(args, "player")) < 1 ||
                string.IsNullOrEmpty(Text(args, "paymentOwnerEffectId")) || string.IsNullOrEmpty(Text(args, "sourceZoneId")) ||
                Number(args, "substitutionBudget") < 1 || Number(args, "substitutionBudget") > 2 ||
                args.Properties.Any(e => !new[] { "apiVersion", "player", "paymentOwnerEffectId", "sourceZoneId", "substitutionBudget", "kind", "effectTypeId" }.Contains(e.Name))
                ? "payment_substitution_arguments_invalid" : "";
        }

        public static ResourcePaymentRuntimeState ReadPayment(EffectNodeRuntimeState node)
        {
            return node?.Payment;
        }

        public static ResourcePaymentRuntimeState InitializePayment(EffectNodeRuntimeState node, ResourceSet cost, bool preferLegacy = false)
        {
            if (node == null || cost == null) throw new ArgumentNullException();
            if (Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Any(t => cost.Get(t) < 0))
                throw new ArgumentException("payment_cost_invalid");
            var previous = ReadPayment(node);
            if (previous != null)
            {
                if (previous.PaymentId != node.EffectId || previous.PlayerId != node.PlayerId || !SameCost(previous.PaymentCost, cost))
                    throw new InvalidOperationException("payment_cost_changed");
                return previous;
            }
            // 旧在途choose_substitution保留原合同，不能静默覆盖其已保存选择。
            if (node.FlowStage == "choose_substitution") throw new InvalidOperationException("legacy_payment_requires_original_executor");
            node.Payment = new ResourcePaymentRuntimeState { PaymentId = node.EffectId, OwnerEffectId = node.EffectId,
                PlayerId = node.PlayerId, PaymentCost = GameStateCloneService.DeepClone(cost), SubstitutionBudget = 2,
                PaymentProtocolVersion = 1,
                AllowsSubstitution = Value(node.NormalizedArguments, "allowSubstitution") == null || Flag(node.NormalizedArguments, "allowSubstitution") };
            if ((node.DefinitionVersion == ResourceEffectExecutor.PaymentDefinitionVersion ||
                node.EffectTypeId == ResourcePaymentChoiceEffectExecutor.TypeId && node.DefinitionVersion == ResourcePaymentChoiceEffectExecutor.DefinitionVersion ||
                node.EffectTypeId == FacilityBuildEffectExecutor.PaymentTypeId && Number(node.NormalizedArguments,"paymentProtocolVersion") == 2) && !preferLegacy)
                EnableV2(node.Payment, new ResourcePaymentDemandState { FixedCost = cost.Clone() }, new ResourceSet());
            return ReadPayment(node);
        }

        public static bool HasLegacySource(GameState state, int player) => state?.ItemZones?.Zones.Any(z => FindEligibleZone(state, player, z.ZoneId) != null) == true;

        public static void EnableV2(ResourcePaymentRuntimeState payment, ResourcePaymentDemandState demand, ResourceSet allocation)
        {
            if (payment == null || payment.IsCommitted || payment.IsCommitRequested || payment.SelectedSubstitution != null ||
                !ResourcePaymentAllocationService.TryValidate(demand, allocation, out var gross, out var reason) || !SameCost(payment.PaymentCost, gross))
                throw new InvalidOperationException("payment_v2_initialization_invalid");
            payment.PaymentProtocolVersion = 2; payment.Demand = GameStateCloneService.DeepClone(demand); payment.SelectedAllocation = allocation.Clone();
            payment.GrossCost = gross; payment.Phase = demand.Mode == "total" ? "allocation" : "demand_frozen";
        }

        // 原选费页尚未消费的费用草稿可改选；执行阶段继续沿严格InitializePayment。
        public static bool TryUpdateUninvokedPaymentCost(GameState state, EffectNodeRuntimeState owner, ResourceSet cost, out string reason, EffectRegistry registry = null)
        {
            reason = "payment_draft_already_consumed";
            var payment = ReadPayment(owner);
            if (payment?.PaymentProtocolVersion == 2)
            {
                if (registry == null) { reason = "payment_v2_requires_versioned_requote"; return false; }
                return ResourcePaymentDraftService.TryChangeFixedQuote(state, registry, owner, cost, payment.PaymentGeneration, out reason);
            }
            if (state?.EffectRuntime == null || cost == null || payment == null ||
                owner.EffectTypeId != ResourcePaymentChoiceEffectExecutor.TypeId || owner.FlowStage != "awaiting_payment" ||
                owner.PendingOutcome != EffectPendingOutcome.None || owner.Status == EffectNodeStatus.Completed ||
                owner.Status == EffectNodeStatus.Failed || owner.Status == EffectNodeStatus.Faulted ||
                payment.PaymentId != owner.EffectId || payment.OwnerEffectId != owner.EffectId || payment.PlayerId != owner.PlayerId ||
                payment.IsCommitted || payment.IsCommitRequested || payment.SelectedSubstitution != null ||
                Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Any(t => cost.Get(t) < 0) ||
                !state.EffectRuntime.InteractionRequests.Any(r => r.OwnerEffectId == owner.EffectId && r.AnsweringPlayerId == owner.PlayerId &&
                    r.Status == "open" && r.InteractionTypeId == ResourcePaymentChoiceEffectExecutor.InteractionTypeId)) return false;
            var window = owner.SettlementWindow;
            if (window != null && (window.IsClosed || window.Context?.Kind != SettlementEntryService.ResourcePayment ||
                window.Context.OwnerEffectId != owner.EffectId || window.Context.PaymentId != payment.PaymentId ||
                !string.IsNullOrEmpty(window.ActiveTargetEffectId) || !string.IsNullOrEmpty(window.SuspendedInteractionId) ||
                window.Entries.Any(e => e.AcceptedUseCount != 0 || !string.IsNullOrEmpty(e.TargetEffectId) || e.SettlementState != "available"))) return false;
            if (state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == TypeId &&
                SettlementEntryService.IsWithin(state.EffectRuntime, n.EffectId, owner.EffectId))) return false;
            payment.PaymentCost = GameStateCloneService.DeepClone(cost);
            payment.PaymentGeneration = checked(payment.PaymentGeneration + 1);
            payment.BeforePaymentEventId = ""; payment.BeforePaymentReady = false;
            if (window != null) window.Context.PaymentCost = GameStateCloneService.DeepClone(cost);
            reason = string.Empty; return true;
        }

        // 只有已经存在的付款交互可以承载手动插入。普通自动 Pay 不借此增造确认页。
        public static bool TryOpenPaymentWindow(GameState state, EffectNodeRuntimeState owner, ResourceSet cost,
            string interactionId, out SettlementWindowRuntimeState window, out string reason)
        {
            window = null; reason = "payment_confirmation_host_missing";
            var request = state?.EffectRuntime?.InteractionRequests.Find(r => r.GetStableInteractionId() == interactionId &&
                r.OwnerEffectId == owner?.EffectId && r.AnsweringPlayerId == owner.PlayerId && r.Status == "open");
            if (request == null || cost == null) return false;
            // 原组合选择仅保留旧在途协议；新的UI宿主须先选择并冻结本笔完整费用。
            if (owner.FlowStage == "choose_substitution") { reason = "legacy_payment_requires_original_executor"; return false; }
            if (owner.SettlementWindow != null && (owner.SettlementWindow.IsClosed ||
                owner.SettlementWindow.Context?.Kind != SettlementEntryService.ResourcePayment))
            { reason = "payment_settlement_window_invalid"; return false; }
            try { InitializePayment(owner, cost); }
            catch (InvalidOperationException error) { reason = error.Message; return false; }
            if (owner.Payment.IsCommitted) { reason = "payment_already_committed"; return false; }
            if (owner.SettlementWindow == null)
                owner.SettlementWindow = SettlementEntryService.CreateWindow(state, owner,
                    SettlementEntryService.ResourcePayment, owner.PlayerId, "", "");
            window = owner.SettlementWindow;
            state.EffectRuntime.ActiveSettlementWindowId = window.Context.Id;
            reason = ""; return true;
        }

        public static bool TrySetDraft(GameState state, EffectNodeRuntimeState owner, string sourceZoneId,
            IReadOnlyDictionary<string, int> amounts, int declaredBudget, out string reason)
        {
            reason = "payment_legacy_substitution_requires_migration";
            return false; // 旧草稿没有真实来源规则/逐枚收据，不能重新解释后自动挑库存。
        }

        public static void CancelDraft(EffectNodeRuntimeState owner)
        {
            if (ReadPayment(owner)?.IsCommitted == true) throw new InvalidOperationException("payment_already_committed");
            var payment = ReadPayment(owner);
            if (payment == null) throw new InvalidOperationException("payment_context_missing");
            payment.SelectedSubstitution = null; payment.IsCommitRequested = false;
            if (payment.PaymentProtocolVersion == 2)
            {
                foreach (var draft in payment.SourceDrafts) { draft.Status = "withdrawn"; draft.Allocation = new ResourceSet(); draft.SelectedItemIds.Clear(); }
                foreach (var edit in payment.SourceEdits.Where(e => e.Status == "open")) edit.Status = "cancelled";
                payment.Phase = "cancelled";
            }
        }

        public static bool TryValidatePaymentRecords(GameState state, out string reason)
        {
            reason = "payment_record_invalid";
            if (state?.EffectRuntime == null) return false;
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => n.Payment != null))
            {
                var payment = node.Payment;
                if (payment.PaymentProtocolVersion == 2)
                {
                    if (!ResourcePaymentRecovery.TryValidateNode(state, null, node, out reason)) return false;
                    continue;
                }
                if (payment.BeforePaymentEventId == null || payment.SourceKind == null || payment.RuleConditionOwnerEffectId == null) return false;
                if (payment.PaymentProtocolVersion < 0 || payment.PaymentProtocolVersion > 1 || payment.PaymentGeneration < 1 ||
                    !new[] { "pay", "condition", "build", "city_move" }.Contains(payment.SourceKind) || payment.BeforePaymentReady && string.IsNullOrEmpty(payment.BeforePaymentEventId)) return false;
                if (!string.IsNullOrEmpty(payment.BeforePaymentEventId))
                {
                    var ruleEvent = state.EffectRuntime.RuleEvents.Find(value => value.EventId == payment.BeforePaymentEventId);
                    if (ruleEvent == null || ruleEvent.EventType != ResourcePaymentPreparationService.BeforeResourcePayment || ruleEvent.SourceEffectId != node.EffectId ||
                        Text(ruleEvent.Payload, "paymentId") != payment.PaymentId || Text(ruleEvent.Payload, "ownerEffectId") != node.EffectId || Text(ruleEvent.Payload, "sourceEffectId") != node.EffectId ||
                        Number(ruleEvent.Payload, "paymentGeneration") != payment.PaymentGeneration || Text(ruleEvent.Payload, "sourceKind") != payment.SourceKind ||
                        ResourcePaymentPreparationService.Cost(payment.PaymentCost).ToDeterministicString() != Value(ruleEvent.Payload, "cost")?.ToDeterministicString()) return false;
                }
                var preparation = payment.Preparation;
                if (preparation != null)
                {
                    var operation = state.EffectRuntime.OperationWindows.Find(value => value.OperationWindowId == preparation.OperationWindowId &&
                        value.WindowGeneration == preparation.WindowGeneration && value.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                        value.SourceEffectId == node.ParentEffectId && value.AuthorizedPlayerIds.SequenceEqual(new[] { payment.PlayerId }));
                    bool validAuthorization = ResourcePaymentPreparationService.IsMainActionAuthorization(preparation) ? operation != null :
                        preparation.AuthorizationKind == "effect_build" && ResourcePaymentPreparationService.ValidateEffectBuildIdentity(state, node, out _);
                    if (preparation.TargetId == null || preparation.BusinessBeforeEventId == null || preparation.SourceLocationId == null || preparation.PromotedActionEffectId == null ||
                        preparation.PreparationOwnerEffectId != node.EffectId || !validAuthorization || preparation.ActionKind != payment.SourceKind ||
                        preparation.ActionKind != "build" && preparation.ActionKind != "city_move" || preparation.IsCancelled && (preparation.IsPromoted || payment.IsCommitted || payment.IsCommitRequested || payment.SelectedSubstitution != null) ||
                        preparation.ActionArguments?.Kind != NormalizedValueKind.Object || !preparation.ActionArguments.TryValidate(out _) ||
                        preparation.IsPromoted != !string.IsNullOrEmpty(preparation.PromotedActionEffectId)) return false;
                    if (preparation.IsPromoted && !state.EffectRuntime.EffectNodes.Any(action => action.EffectId == preparation.PromotedActionEffectId &&
                        action.PlayerId == payment.PlayerId && action.ParentEffectId == node.ParentEffectId && Text(action.NormalizedArguments, "preparedPaymentOwnerEffectId") == node.EffectId)) return false;
                    if (preparation.ActionKind == "city_move" && (preparation.BusinessBeforeReady && preparation.BusinessBeforeEventId.Length == 0 ||
                        preparation.BusinessBeforeEventId.Length > 0 && !state.EffectRuntime.RuleEvents.Any(value => value.EventId == preparation.BusinessBeforeEventId &&
                            value.SourceEffectId == node.EffectId && value.EventType == CityMoveEventTypeIds.BeforeCityMove && Text(value.Payload, "targetLocationId") == preparation.TargetId &&
                            Text(value.Payload, "sourceLocationId") == preparation.SourceLocationId))) return false;
                }
                if (payment.PaymentId != node.EffectId || payment.OwnerEffectId != node.EffectId || payment.PlayerId != node.PlayerId ||
                    state.FindPlayer(payment.PlayerId) == null || payment.SubstitutionBudget < 1 || payment.SubstitutionBudget > 2 ||
                    payment.PaymentCost == null || Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Any(t => payment.PaymentCost.Get(t) < 0) ||
                    payment.IsCommitted && (payment.IsCommitRequested || node.FlowStage != "payment_finished" ||
                        node.Status != EffectNodeStatus.Completed && node.PendingOutcome != EffectPendingOutcome.Completed)) return false;
                if (payment.SelectedSubstitution != null)
                { reason = "payment_legacy_substitution_requires_migration"; return false; }
            }
            reason = ""; return true;
        }

        public static EffectStepResult CommitPayment(EffectExecutionContext context, ResourceSet requested, NormalizedValue result)
        {
            ResourcePaymentRuntimeState payment;
            try { payment = InitializePayment(context.Node, requested); }
            catch (InvalidOperationException error) { return EffectStepResult.Failed(error.Message); }
            if (payment.IsCommitted) return EffectStepResult.Completed(context.Node.NormalizedResult.Clone());
            if (payment.PaymentProtocolVersion == 2) return ResourcePaymentPlanService.Commit(context, result);
            var player = context.State.FindPlayer(payment.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            var remaining = GameStateCloneService.DeepClone(payment.PaymentCost);
            if (payment.SelectedSubstitution != null) return EffectStepResult.Failed("payment_legacy_substitution_requires_migration");
            if (!player.Resources.CanPay(remaining)) return EffectStepResult.Failed("insufficient_resource");
            // 创建暂存区沿现有工厂复制提交状态，重新取得唯一工作对象。
            payment = context.Node.Payment; player = context.State.FindPlayer(payment.PlayerId);
            if (!player.Resources.TryPay(remaining)) throw new InvalidOperationException("payment_balance_changed_during_commit");
            payment.IsCommitted = true; payment.IsCommitRequested = false;
            return EffectStepResult.Completed(result).WithFlowStage("payment_finished");
        }

        private static EffectStepResult Execute(EffectExecutionContext context, BoundedResourceAllocationDecoder decoder)
        {
            if (Number(context.Node.NormalizedArguments, "apiVersion") == 2) return ResourcePaymentInsertionService.Execute(context, decoder);
            string invalid = ValidateArguments(context.Node.NormalizedArguments);
            if (invalid.Length > 0) return EffectStepResult.Failed(invalid);
            var args = context.Node.NormalizedArguments;
            var owner = context.State.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == Text(args, "paymentOwnerEffectId") && n.PlayerId == context.Node.PlayerId);
            if (Text(args, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture) || owner?.Payment?.PaymentProtocolVersion != 2 ||
                owner.Payment.IsCommitted || owner.Payment.IsCommitRequested || !owner.Payment.BeforePaymentReady || !SettlementEntryService.IsWithin(context.State.EffectRuntime, context.Node.EffectId, owner.EffectId))
                return EffectStepResult.Failed("payment_substitution_context_invalid");
            if (context.Node.FlowStage == "finished") return EffectStepResult.Completed();
            var zone = context.State.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == Text(args, "sourceZoneId"));
            var rules = ResourcePaymentSourceService.Query(context.State, context.Registry, owner, owner.Payment.PaymentCost).Where(r => r.ItemCost != null &&
                r.ItemCost.CardInstanceId == zone?.ParentId && r.ItemCost.LocalZoneId == zone.LocalZoneId).ToList();
            if (rules.Count != 1) return EffectStepResult.Failed("payment_substitution_source_changed");
            var rule = rules[0]; var p = owner.Payment;
            var previous = p.SourceDrafts.SingleOrDefault(d => ResourcePaymentSourceService.Key(d.Rule) == ResourcePaymentSourceService.Key(rule));
            var others = p.SourceDrafts.Where(d => d != previous && d.Status == "valid").ToList();
            var items = ResourcePaymentSourceService.TransferableItems(context.State, context.Registry, owner, rule).Where(i => !others.Any(d => d.SelectedItemIds.Contains(i.ItemId))).ToList();
            string inventory = StableIdFactory.Create("resource-source-inventory", string.Join("|", items.Select(i => i.ItemId + ":" + ResourcePaymentSourceService.ItemSelectionGroup(context.State, i))));
            int maximum = (int)Math.Min(Number(args, "substitutionBudget"), Math.Min(rule.MaxUnits, Math.Max(0L, rule.QuotaLimit -
                others.Where(d => d.Rule.QuotaGroupId == rule.QuotaGroupId && d.Rule.QuotaScope == rule.QuotaScope).Sum(d => (long)PaymentResources.Total(d.Allocation)))));
            maximum = Math.Min(maximum, items.Count / rule.ItemCost.CountPerUnit);
            var rows = BasicTypes.Where(rule.AllowedResourceTypes.Contains).ToDictionary(t => t.ToString(), t =>
                (int)Math.Min(maximum, Math.Max(0L, (long)p.PaymentCost.Get(t) - others.Sum(d => (long)d.Allocation.Get(t)))), StringComparer.Ordinal);
            if (context.Node.FlowStage == "allocating")
            {
                if (Text(context.Node.ContinuationData, "paymentRuleFingerprint") != ResourcePaymentSourceService.Fingerprint(rule) ||
                    Text(context.Node.ContinuationData, "paymentInventoryDigest") != inventory || Number(context.Node.ContinuationData, "paymentSourceGeneration") != p.PaymentGeneration ||
                    Number(context.Node.ContinuationData, "paymentSourceDraftRevision") != (previous?.DraftRevision ?? 0)) return EffectStepResult.Failed("payment_substitution_resume_identity_invalid");
                var answer = context.GetLatestInteractionAnswer();
                if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue)
                    return EffectStepResult.Completed(Object("outcomeCode", NormalizedValue.CreateString("substitution_cancelled"))).WithFlowStage("finished");
                if (answer?.Kind != NormalizedValueKind.String || !decoder(answer.StringValue, rows.Keys.ToList(), 0, maximum, rows, out var amounts, out _))
                    return EffectStepResult.Failed("payment_substitution_invalid");
                var allocation = new ResourceSet(); foreach (var type in BasicTypes) allocation.Set(type, amounts.TryGetValue(type.ToString(), out int amount) ? amount : 0);
                if (!ResourcePaymentDraftService.TrySetSourceDraft(context.State, context.Registry, owner, rule.SourceRef.SourceKey, rule.RuleId, p.PaymentGeneration,
                    previous?.DraftRevision ?? 0, allocation, Array.Empty<string>(), "", out string reason)) return EffectStepResult.Failed(reason);
                return EffectStepResult.Completed(Object("paymentId", NormalizedValue.CreateString(p.PaymentId))).WithFlowStage("finished");
            }
            if (context.Node.FlowStage.Length != 0) return EffectStepResult.Failed("payment_substitution_stage_invalid");
            Set(context.Node, "paymentRuleFingerprint", NormalizedValue.CreateString(ResourcePaymentSourceService.Fingerprint(rule)));
            Set(context.Node, "paymentInventoryDigest", NormalizedValue.CreateString(inventory));
            Set(context.Node, "paymentSourceGeneration", NormalizedValue.CreateInteger(p.PaymentGeneration));
            Set(context.Node, "paymentSourceDraftRevision", NormalizedValue.CreateInteger(previous?.DraftRevision ?? 0));
            var request = new EffectInteractionSpec { InteractionTypeId = InteractionTypeId, AnswerSchema = AnswerSchema, AnsweringPlayerId = owner.PlayerId,
                Visibility = "owner", AllowDecline = true, MinSelections = 0, MaxSelections = 1,
                PromptParameters = Object("paymentId", NormalizedValue.CreateString(p.PaymentId), "minAmount", NormalizedValue.CreateInteger(0), "maxAmount", NormalizedValue.CreateInteger(maximum),
                    "rowIds", Strings(rows.Keys), "rowMaximums", NormalizedValue.CreateObject(rows.Select(row => new NormalizedValueEntry { Name = row.Key, Value = NormalizedValue.CreateInteger(row.Value) }).ToList())),
                Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:operator.passenger.payment.prompt",
                    new GameplayTextArgument { Name = "maximum", Kind = "integer", Value = NormalizedValue.CreateInteger(maximum) }) } };
            foreach (var type in BasicTypes.Where(rule.AllowedResourceTypes.Contains))
            {
                string icon = type == ResourceType.Originium ? "core:resource.orirock" : type == ResourceType.OriginiumShard ? "core:resource.originium_shard" : "core:resource.iron";
                request.CandidateIds.Add(type.ToString()); request.Presentation.QuantityRows.Add(new GameplayQuantityRowPresentation { RowId = type.ToString(), IconKey = icon, Label = new GameplayTextRef(icon + ".alt") });
            }
            return EffectStepResult.Continue("allocating").AddInteraction(request);
        }

        public static ZoneInstanceState FindEligibleZone(GameState state, int playerId, string zoneId)
        {
            var zone = state?.ItemZones?.Zones.Find(z => z.ZoneId == zoneId && z.ParentKind == ZoneParentKind.Item &&
                z.Configuration?.PaymentPolicyId == "core.basic_resource_substitution" && z.Configuration.MaxMarkersPerPayment == 2);
            if (zone == null || !state.ItemZones.Items.Any(i => i.ItemId == zone.ParentId && i.ControllerSubject.PlayerId == playerId &&
                state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.character_persistent")) ||
                !state.CharacterUses.Any(u => u.CardInstanceId == zone.ParentId && u.Modes.Any(m => m.SkillType == "persistent" && m.Activated && m.SubscriptionActive))) return null;
            return PaymentSource(state, playerId, zone, out _) == null ? null : zone;
        }
        // 兼容投影的资格绑定同卡的真实payment纯规则来源，不能借tactic的装载许可或零transferable计数推断。
        private static AbilitySourceRef PaymentSource(GameState state, int player, ZoneInstanceState zone, out List<string> accepted)
        {
            accepted = new List<string>(); if (zone == null) return null;
            AbilitySourceRef found = null;
            foreach (var source in AbilitySourceService.QuerySources(state, state.ContentDefinitions.AbilitySources, player, "ruleQuery", queryKind: ResourcePaymentSourceService.QueryKind).Where(s => s.ItemId == zone.ParentId))
            {
                var resolved = CardMarkerCarrier.ResolveWithSource(state, source, player, zone.ParentId, zone.LocalZoneId, "transfer.original_supply", state.ContentDefinitions.AbilitySources, out var types, out _);
                if (resolved?.ZoneId != zone.ZoneId) continue;
                if (found != null) { accepted.Clear(); return null; }
                found = source; accepted = types;
            }
            return found;
        }
        public static List<string> MarkerIds(GameState state, ZoneInstanceState zone, int playerId)
        {
            if (PaymentSource(state, playerId, zone, out var types) == null) return new List<string>();
            return state.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId && i.TypeId == "core.player_marker" && CardMarkerCarrier.Accepts(state, zone, i, playerId, types) &&
                state.ItemZones.Zones.Any(z => z.ZoneId == i.OriginalSupplyZoneId))
                .Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
        private static bool SameCost(ResourceSet left, ResourceSet right) => Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().All(t => left.Get(t) == right.Get(t));
    }
}
