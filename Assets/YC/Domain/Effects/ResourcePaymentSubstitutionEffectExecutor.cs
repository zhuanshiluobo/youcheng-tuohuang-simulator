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

        public static ResourcePaymentRuntimeState InitializePayment(EffectNodeRuntimeState node, ResourceSet cost)
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
            return ReadPayment(node);
        }

        // 原选费页尚未消费的费用草稿可改选；执行阶段继续沿严格InitializePayment。
        public static bool TryUpdateUninvokedPaymentCost(GameState state, EffectNodeRuntimeState owner, ResourceSet cost, out string reason)
        {
            reason = "payment_draft_already_consumed";
            var payment = ReadPayment(owner);
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
            reason = "payment_substitution_invalid";
            var payment = ReadPayment(owner);
            if (state == null || payment == null || !payment.AllowsSubstitution || payment.IsCommitted || payment.PlayerId != owner.PlayerId ||
                payment.SubstitutionBudget < 1 || payment.SubstitutionBudget > 2 || declaredBudget < 1 || declaredBudget > 2 ||
                amounts == null || amounts.Keys.Any(k => !BasicTypes.Any(t => t.ToString() == k))) return false;
            var zone = FindEligibleZone(state, payment.PlayerId, sourceZoneId);
            if (zone == null) { reason = "payment_substitution_source_changed"; return false; }
            var allocation = new ResourceSet(); int total = 0;
            foreach (var type in BasicTypes)
            {
                int amount = amounts.TryGetValue(type.ToString(), out int value) ? value : 0;
                if (amount < 0 || amount > payment.PaymentCost.Get(type) || amount > 2) return false;
                allocation.Set(type, amount); total += amount;
            }
            int maximum = Math.Min(Math.Min(declaredBudget, payment.SubstitutionBudget), Math.Min(2, MarkerIds(state, zone, payment.PlayerId).Count));
            if (total > maximum) return false;
            payment.SelectedSubstitution = total == 0 ? null : new PaymentSubstitutionSelection {
                SourceZoneId = zone.ZoneId, SourceItemId = zone.ParentId, Allocation = allocation };
            reason = ""; return true;
        }

        public static void CancelDraft(EffectNodeRuntimeState owner)
        {
            if (ReadPayment(owner)?.IsCommitted == true) throw new InvalidOperationException("payment_already_committed");
            var payment = ReadPayment(owner);
            if (payment == null) throw new InvalidOperationException("payment_context_missing");
            payment.SelectedSubstitution = null; payment.IsCommitRequested = false;
        }

        public static bool TryValidatePaymentRecords(GameState state, out string reason)
        {
            reason = "payment_record_invalid";
            if (state?.EffectRuntime == null) return false;
            foreach (var node in state.EffectRuntime.EffectNodes.Where(n => n.Payment != null))
            {
                var payment = node.Payment;
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
                    if (preparation.TargetId == null || preparation.BusinessBeforeEventId == null || preparation.SourceLocationId == null || preparation.PromotedActionEffectId == null ||
                        preparation.PreparationOwnerEffectId != node.EffectId || operation == null || preparation.ActionKind != payment.SourceKind ||
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
                var draft = payment.SelectedSubstitution;
                if (draft == null) continue;
                if (draft.Allocation == null || string.IsNullOrEmpty(draft.SourceItemId) || string.IsNullOrEmpty(draft.SourceZoneId) ||
                    draft.Allocation.GoldVoucher != 0 || draft.Allocation.PureOriginium != 0 ||
                    BasicTypes.Any(t => draft.Allocation.Get(t) < 0 || draft.Allocation.Get(t) > payment.PaymentCost.Get(t)) ||
                    BasicTypes.Sum(t => (long)draft.Allocation.Get(t)) > payment.SubstitutionBudget) return false;
                var zone = state.ItemZones.Zones.Find(z => z.ZoneId == draft.SourceZoneId && z.ParentId == draft.SourceItemId &&
                    z.ParentKind == ZoneParentKind.Item && z.Configuration?.PaymentPolicyId == "core.basic_resource_substitution" && z.Configuration.MaxMarkersPerPayment == 2);
                if (zone == null || !payment.IsCommitted && (FindEligibleZone(state, payment.PlayerId, zone.ZoneId) == null ||
                    MarkerIds(state, zone, payment.PlayerId).Count < BasicTypes.Sum(t => draft.Allocation.Get(t)))) return false;
            }
            reason = ""; return true;
        }

        public static EffectStepResult CommitPayment(EffectExecutionContext context, ResourceSet requested, NormalizedValue result)
        {
            ResourcePaymentRuntimeState payment;
            try { payment = InitializePayment(context.Node, requested); }
            catch (InvalidOperationException error) { return EffectStepResult.Failed(error.Message); }
            if (payment.IsCommitted) return EffectStepResult.Completed(context.Node.NormalizedResult.Clone());
            var player = context.State.FindPlayer(payment.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            var remaining = GameStateCloneService.DeepClone(payment.PaymentCost);
            ItemTransferRequest transfer = null; ZoneInstanceState zone = null;
            if (payment.SelectedSubstitution != null)
            {
                var draft = payment.SelectedSubstitution;
                var amounts = BasicTypes.ToDictionary(t => t.ToString(), t => draft.Allocation.Get(t), StringComparer.Ordinal);
                if (!TrySetDraft(context.State, context.Node, draft.SourceZoneId, amounts, payment.SubstitutionBudget, out string reason))
                    return EffectStepResult.Failed(reason);
                zone = FindEligibleZone(context.State, payment.PlayerId, draft.SourceZoneId);
                if (zone?.ParentId != draft.SourceItemId) return EffectStepResult.Failed("payment_substitution_source_changed");
                int total = BasicTypes.Sum(t => draft.Allocation.Get(t));
                if (draft.Allocation.GoldVoucher != 0 || draft.Allocation.PureOriginium != 0) return EffectStepResult.Failed("payment_substitution_resource_rejected");
                foreach (var type in BasicTypes) remaining.Set(type, remaining.Get(type) - draft.Allocation.Get(type));
                if (total > 0)
                {
                    transfer = new ItemTransferRequest { ExecutingPlayerId = payment.PlayerId, ItemIds = MarkerIds(context.State, zone, payment.PlayerId).Take(total).ToList(),
                        ExpectedSource = new ItemPositionRef { ZoneId = zone.ZoneId }, Destination = new ZoneRef { ZoneId = FindZone(context.State, "player_markers", payment.PlayerId).ZoneId },
                        InsertionKind = "unordered", OperationProfileId = "core.cube_storage", RequiredCount = total };
                    var valid = new ItemTransferService().Validate(context.State, transfer);
                    if (!valid.IsValid) return EffectStepResult.Failed(valid.ReasonCode);
                }
            }
            if (!player.Resources.CanPay(remaining)) return EffectStepResult.Failed("insufficient_resource");
            if (transfer != null) new ItemTransferService().RelocateItems(context, transfer);
            if (!player.Resources.TryPay(remaining)) throw new InvalidOperationException("payment_balance_changed_during_commit");
            payment.IsCommitted = true; payment.IsCommitRequested = false;
            if (transfer != null) CharacterLifecycleService.ExitEmptyCarriers(context, new[] { zone.ZoneId });
            return EffectStepResult.Completed(result).WithFlowStage("payment_finished");
        }

        private static EffectStepResult Execute(EffectExecutionContext context, BoundedResourceAllocationDecoder decoder)
        {
            string invalid = ValidateSpec(EffectRegistry.FromRuntimeSpec(new EffectSpecRuntimeState { EffectTypeId = TypeId, NormalizedArguments = context.Node.NormalizedArguments }));
            if (invalid.Length > 0) return EffectStepResult.Failed(invalid);
            if (CharacterLifecycleService.ParsePlayer(Text(context.Node.NormalizedArguments, "player")) != context.Node.PlayerId)
                return EffectStepResult.Failed("invalid_player");
            string ownerId = Text(context.Node.NormalizedArguments, "paymentOwnerEffectId");
            var owner = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == ownerId && n.PlayerId == context.Node.PlayerId);
            if (owner == null || !SettlementEntryService.IsWithin(context.State.EffectRuntime, context.Node.EffectId, ownerId) ||
                ReadPayment(owner)?.IsCommitted != false)
                return EffectStepResult.Failed("payment_substitution_context_invalid");
            if (context.Node.FlowStage == "finished") return EffectStepResult.Completed();
            var payment = ReadPayment(owner); string source = Text(context.Node.NormalizedArguments, "sourceZoneId");
            var zone = FindEligibleZone(context.State, owner.PlayerId, source);
            if (zone == null) return EffectStepResult.Failed("payment_substitution_source_changed");
            int budget = Number(context.Node.NormalizedArguments, "substitutionBudget");
            int maximum = (int)Math.Min(Math.Min(budget, payment.SubstitutionBudget),
                Math.Min(MarkerIds(context.State, zone, owner.PlayerId).Count, BasicTypes.Sum(t => (long)payment.PaymentCost.Get(t))));
            // 行上限表达本次能替代的数量；大额费用不能突破有界答案协议的0…2范围。
            var rows = BasicTypes.ToDictionary(t => t.ToString(), t => Math.Min(maximum, payment.PaymentCost.Get(t)), StringComparer.Ordinal);
            if (context.Node.FlowStage == "allocating")
            {
                var answer = context.GetLatestInteractionAnswer();
                if (answer?.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue)
                { CancelDraft(owner); return EffectStepResult.Completed(Object("outcomeCode", NormalizedValue.CreateString("substitution_cancelled"))).WithFlowStage("finished"); }
                if (answer?.Kind != NormalizedValueKind.String || !decoder(answer.StringValue, rows.Keys.ToList(), 0, maximum, rows, out var amounts, out _) ||
                    !TrySetDraft(context.State, owner, source, amounts, budget, out _)) return EffectStepResult.Failed("payment_substitution_invalid");
                // 分配只保存计划；恢复原宿主后，原最终确认操作才请求扣款。
                payment.IsCommitRequested = false;
                return EffectStepResult.Completed(Object("paymentId", NormalizedValue.CreateString(payment.PaymentId))).WithFlowStage("finished");
            }
            if (context.Node.FlowStage.Length != 0) return EffectStepResult.Failed("payment_substitution_stage_invalid");
            var request = new EffectInteractionSpec { InteractionTypeId = InteractionTypeId, AnswerSchema = AnswerSchema,
                AnsweringPlayerId = owner.PlayerId, Visibility = "owner", AllowDecline = true,
                MinSelections = 0, MaxSelections = 1,
                PromptParameters = Object("paymentId", NormalizedValue.CreateString(payment.PaymentId), "minAmount", NormalizedValue.CreateInteger(0),
                    "maxAmount", NormalizedValue.CreateInteger(maximum), "rowIds", Strings(rows.Keys),
                    "rowMaximums", NormalizedValue.CreateObject(rows.Select(row => new NormalizedValueEntry { Name = row.Key, Value = NormalizedValue.CreateInteger(row.Value) }).ToList())),
                Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:operator.passenger.payment.prompt",
                    new GameplayTextArgument { Name = "maximum", Kind = "integer", Value = NormalizedValue.CreateInteger(maximum) }) } };
            foreach (var type in BasicTypes)
            {
                string icon = type == ResourceType.Originium ? "core:resource.orirock" : type == ResourceType.OriginiumShard ? "core:resource.originium_shard" : "core:resource.iron";
                request.CandidateIds.Add(type.ToString());
                request.Presentation.QuantityRows.Add(new GameplayQuantityRowPresentation { RowId = type.ToString(), IconKey = icon,
                    Label = new GameplayTextRef(icon + ".alt") });
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
            return zone;
        }

        public static List<string> MarkerIds(GameState state, ZoneInstanceState zone, int playerId) =>
            state.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId && i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == playerId).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        private static bool SameCost(ResourceSet left, ResourceSet right) => Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().All(t => left.Get(t) == right.Get(t));
    }
}
