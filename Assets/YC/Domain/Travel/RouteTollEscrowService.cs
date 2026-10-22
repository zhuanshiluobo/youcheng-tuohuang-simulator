using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Travel
{
    public sealed class GoldVoucherBalance
    {
        public int Available { get; internal set; }
        public int Reserved { get; internal set; }
        public int Owned { get; internal set; }
    }

    // FrozenPayments是资金收据的持久化记录，选择资格和路线来自权威选择承诺。
    public sealed class RouteTollEscrowReceipt
    {
        public string ReservationId { get; internal set; }
        public string OwnerEffectId { get; internal set; }
        public string SelectionOwnerEffectId { get; internal set; }
        public string SelectionSessionId { get; internal set; }
        public int SelectionRevision { get; internal set; }
        public int PayerPlayerId { get; internal set; }
        public int StartAvailableGoldVoucher { get; internal set; }
        public int OriginalAmount { get; internal set; }
        public int EscrowAmount { get; internal set; }
        public string State { get; internal set; }
        public NormalizedValue FrozenPayments { get; internal set; }
        public string SettlementReceiptId { get; internal set; }
        public string ReleaseReceiptId { get; internal set; }
    }

    public sealed class RouteTollEscrowService
    {
        public const string Protocol = "route_toll_escrow.v1";
        public const string ReceiptField = "routeTollEscrow";
        public const string AwaitingEventStage = "awaiting_escrow_exploration";
        // 银行是独立接收主体，不映射到玩家。
        public const string BankSubjectKey = "bank";
        // 1个Continuation root + 1个receipt Object + 12个标量 + 1个数组 + 4N <=128。
        public const int MaximumPaymentLines = 28;

        private readonly IMapQueryService map;
        public RouteTollEscrowService(IMapQueryService mapQuery)
        {
            map = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
        }

        // 调用前先解析并绑定Host选择承诺；客户端不能传入资源数字或收款计划。
        public ValidationResult TryReserve(GameState work, EffectNodeRuntimeState owner,
            RouteSelectionSession session, RouteSelectionCommitment commitment,
            out RouteTollEscrowReceipt receipt, out bool changed)
            => PrepareReserve(work, owner, session, commitment, false, out receipt, out changed);

        public ValidationResult TryValidateReserve(GameState state, EffectNodeRuntimeState owner,
            RouteSelectionSession session, RouteSelectionCommitment commitment)
            => PrepareReserve(state, owner, session, commitment, true, out _, out _);

        private ValidationResult PrepareReserve(GameState work, EffectNodeRuntimeState owner,
            RouteSelectionSession session, RouteSelectionCommitment commitment, bool validateOnly,
            out RouteTollEscrowReceipt receipt, out bool changed)
        {
            receipt = null; changed = false;
            if (session == null || commitment == null || session.SchemaVersion != 1 ||
                session.Purpose != RouteSelectionPurpose.Exploration || session.MapId != work?.MapId ||
                session.PlayerId != owner?.PlayerId || commitment.PlayerId != session.PlayerId ||
                commitment.OwnerEffectId != session.OwnerEffectId || commitment.SessionId != session.SessionId ||
                commitment.BudgetBindingId != session.BudgetBindingId || string.IsNullOrEmpty(session.BudgetBindingId) ||
                string.IsNullOrEmpty(commitment.CommitmentId) || session.Stage != RouteSelectionStage.Committed ||
                commitment.AcceptedAtStateRevision < 0) return Reject("route_toll_commitment_binding_invalid");
            var preview = commitment.AcceptedPreview;
            if (preview?.FinalizationValidation == null || !preview.FinalizationValidation.IsValid ||
                preview.PaymentPlan?.Lines == null || preview.ChargeRouteIds == null ||
                !preview.ChargeRouteIds.SequenceEqual(preview.PaymentPlan.Lines.Select(line => line?.RouteId)) ||
                preview.SelectedLocationIds == null || preview.SelectedLocationIds.Count != 1)
                return Reject("route_toll_commitment_preview_invalid");
            long sum = 0;
            var lines = new List<NormalizedValue>();
            if (preview.PaymentPlan.Lines.Count > MaximumPaymentLines) return Reject("route_toll_receipt_capacity_exceeded");
            try
            {
                foreach (var line in preview.PaymentPlan.Lines)
                {
                    if (line == null) return Reject("route_toll_payment_line_invalid");
                    sum += line.GoldVoucherAmount;
                    lines.Add(NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                        Entry("routeId", NormalizedValue.CreateStableReference("route", line.RouteId)),
                        Entry("receiverSubjectKey", NormalizedValue.CreateString(line.ReceiverSubjectKey)),
                        Entry("amount", NormalizedValue.CreateInteger(line.GoldVoucherAmount)) }));
                }
            }
            catch (ArgumentException) { return Reject("route_toll_payment_line_invalid"); }
            if (sum != preview.PaymentPlan.TotalGoldVoucherAmount || sum < 0)
                return Reject("route_toll_plan_total_mismatch");
            return TryReserveValidatedPlan(work, owner, session.OwnerEffectId, session.SessionId,
                commitment.AcceptedAtStateRevision, session.StartGoldVoucherAmount,
                NormalizedValue.CreateArray(lines), out receipt, out changed, validateOnly);
        }

        // 只消费已经过权威复验的冻结计划，不作为客户端命令入口。
        // 返回失败必须零变更，所有成功写入均由调用方同一RuleCommit提交。
        public ValidationResult TryReserveValidatedPlan(GameState work, EffectNodeRuntimeState owner,
            string selectionOwnerEffectId, string selectionSessionId, int selectionRevision,
            int startAvailableGoldVoucher, NormalizedValue frozenPayments,
            out RouteTollEscrowReceipt receipt, out bool changed, bool validateOnly = false)
        {
            receipt = null; changed = false;
            if (!TryValidateOwner(work, owner, out var diagnostic)) return Reject(diagnostic);
            if (frozenPayments == null || frozenPayments.Kind != NormalizedValueKind.Array ||
                !frozenPayments.IsValid()) return Reject("route_toll_plan_invalid");
            if (!TryValidatePayments(work, owner.PlayerId, frozenPayments, out int total, out diagnostic))
                return Reject(diagnostic);

            var proposed = new RouteTollEscrowReceipt
            {
                ReservationId = ReservationId(work, owner), OwnerEffectId = owner.EffectId,
                SelectionOwnerEffectId = selectionOwnerEffectId, SelectionSessionId = selectionSessionId,
                SelectionRevision = selectionRevision, PayerPlayerId = owner.PlayerId,
                StartAvailableGoldVoucher = startAvailableGoldVoucher, OriginalAmount = total,
                EscrowAmount = total, State = "held", FrozenPayments = frozenPayments.Clone(),
                SettlementReceiptId = string.Empty, ReleaseReceiptId = string.Empty
            };
            if (!TryValidateReceipt(work, owner, proposed, out diagnostic)) return Reject(diagnostic);
            if (Value(owner.ContinuationData, ReceiptField) != null)
            {
                if (!TryReadReceipt(owner, out receipt, out diagnostic) ||
                    !TryValidateReceipt(work, owner, receipt, out diagnostic)) return Reject(diagnostic);
                if (!SameReservation(receipt, proposed)) return Reject("route_toll_duplicate_plan_mismatch");
                if (receipt.State == "released") return Reject("route_toll_reservation_released");
                return ValidationResult.Success; // held或settled重复均不扣钱包。
            }
            if (owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Failed ||
                owner.Status == EffectNodeStatus.Faulted || owner.PendingOutcome != EffectPendingOutcome.None ||
                owner.ChildEffectIds.Count != 0) return Reject("route_toll_reserve_boundary_invalid");
            var player = work.FindPlayer(owner.PlayerId);
            if (!player.Resources.CanPay(new ResourceSet { GoldVoucher = total }))
                return Reject("route_toll_current_balance_insufficient");
            if (!TryPrepareContinuation(owner, proposed, out var next, out diagnostic)) return Reject(diagnostic);
            if (validateOnly) { receipt = proposed; return ValidationResult.Success; }
            // 所有检查先完成，以下两处写入没有后续可失败的计算。
            player.Resources.GoldVoucher -= total;
            owner.ContinuationData = next;
            receipt = proposed; changed = true;
            return ValidationResult.Success;
        }

        public ValidationResult TrySettle(GameState work, EffectNodeRuntimeState owner,
            out RouteTollEscrowReceipt receipt, out bool changed)
        {
            receipt = null; changed = false;
            if (!TryValidateOwner(work, owner, out var diagnostic) ||
                !TryReadReceipt(owner, out receipt, out diagnostic) ||
                !TryValidateReceipt(work, owner, receipt, out diagnostic)) return Reject(diagnostic);
            if (receipt.State == "settled") return ValidationResult.Success;
            if (receipt.State != "held") return Reject("route_toll_not_held");
            if (!TryValidateCompletedEvent(work, owner, out diagnostic)) return Reject(diagnostic);

            var credits = new Dictionary<int, long>();
            foreach (var line in receipt.FrozenPayments.Items)
            {
                string key = Text(line, "receiverSubjectKey");
                if (key == BankSubjectKey) continue;
                if (!TryResolvePlayer(work, key, out var receiver)) return Reject("route_toll_receiver_invalid");
                credits.TryGetValue(receiver.PlayerId, out long amount);
                credits[receiver.PlayerId] = amount + Number(line, "amount");
            }
            var balances = new Dictionary<int, int>();
            foreach (var credit in credits)
            {
                var player = work.FindPlayer(credit.Key);
                long balance = (long)player.Resources.GoldVoucher + credit.Value;
                if (balance < 0 || balance > int.MaxValue ||
                    (long)QueryBalance(work, credit.Key).Owned + credit.Value > int.MaxValue)
                    return Reject("route_toll_receiver_balance_overflow");
                balances.Add(credit.Key, (int)balance);
            }
            receipt.EscrowAmount = 0; receipt.State = "settled";
            receipt.SettlementReceiptId = EndReceiptId(work, owner, "route-toll-settlement");
            if (!TryPrepareContinuation(owner, receipt, out var next, out diagnostic)) return Reject(diagnostic);
            foreach (var balance in balances) work.FindPlayer(balance.Key).Resources.GoldVoucher = balance.Value;
            owner.ContinuationData = next; changed = true;
            // 不再次扣payer、不创建Pay/Gain子节点，不发布虚构的资源支付通知。
            return ValidationResult.Success;
        }

        public ValidationResult TryReleaseBeforeEventStart(GameState work, EffectNodeRuntimeState owner,
            out RouteTollEscrowReceipt receipt, out bool changed)
        {
            receipt = null; changed = false;
            if (!TryValidateOwner(work, owner, out var diagnostic) ||
                !TryReadReceipt(owner, out receipt, out diagnostic) ||
                !TryValidateReceipt(work, owner, receipt, out diagnostic)) return Reject(diagnostic);
            if (receipt.State == "released") return ValidationResult.Success;
            if (receipt.State != "held") return Reject("route_toll_not_held");
            // 仅允许同工作副本的未启动事件安全边界；不从空cardId推断是否已翻牌。
            if (owner.ChildEffectIds.Count != 0 || work.EffectRuntime.EffectNodes.Any(node =>
                    node != null && node.ParentEffectId == owner.EffectId) ||
                work.EffectRuntime.RuleEvents.Any(ruleEvent => ruleEvent != null &&
                    (ruleEvent.OwnerNodeId == owner.EffectId || ruleEvent.SourceEffectId == owner.EffectId)))
                return Reject("route_toll_event_already_started");
            var player = work.FindPlayer(receipt.PayerPlayerId);
            long balance = (long)player.Resources.GoldVoucher + receipt.EscrowAmount;
            if (balance > int.MaxValue) return Reject("route_toll_release_balance_overflow");
            receipt.State = "released"; receipt.EscrowAmount = 0;
            receipt.ReleaseReceiptId = EndReceiptId(work, owner, "route-toll-release");
            if (!TryPrepareContinuation(owner, receipt, out var next, out diagnostic)) return Reject(diagnostic);
            player.Resources.GoldVoucher = (int)balance;
            owner.ContinuationData = next; changed = true;
            return ValidationResult.Success;
        }

        public static GoldVoucherBalance QueryBalance(GameState state, int playerId)
        {
            var player = state?.FindPlayer(playerId);
            if (player?.Resources == null || player.Resources.GoldVoucher < 0 || state.EffectRuntime?.EffectNodes == null)
                throw new KernelException("route_toll_balance_invalid", "金券钱包不存在或为负数。");
            long reserved = 0;
            foreach (var node in state.EffectRuntime.EffectNodes)
            {
                if (node == null || node.PlayerId != playerId) continue;
                var raw = Value(node.ContinuationData, ReceiptField);
                if (raw == null)
                {
                    if (node.FlowStage == AwaitingEventStage)
                        throw new KernelException("route_toll_receipt_missing", "探索等待阶段缺少路费托管收据。");
                    continue;
                }
                if (!TryReadReceipt(node, out var receipt, out var diagnostic))
                    throw new KernelException("route_toll_receipt_invalid", diagnostic);
                if (receipt.PayerPlayerId != playerId || receipt.OwnerEffectId != node.EffectId ||
                    receipt.ReservationId != ReservationId(state, node))
                    throw new KernelException("route_toll_receipt_owner_invalid", "路费托管收据所属玩家不一致。");
                if (receipt.State == "held") reserved += receipt.EscrowAmount;
            }
            long owned = player.Resources.GoldVoucher + reserved;
            if (reserved > int.MaxValue || owned > int.MaxValue)
                throw new KernelException("route_toll_balance_overflow", "金券拥有量超过支持范围。");
            return new GoldVoucherBalance
            { Available = player.Resources.GoldVoucher, Reserved = (int)reserved, Owned = (int)owned };
        }

        // 资金与节点基础校验；恢复入口另核验冻结选择及其唯一消费方。
        public bool TryValidateStateBase(GameState state, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (state?.EffectRuntime?.EffectNodes == null) return Fail("route_toll_runtime_missing", out diagnostic);
            var reservations = new HashSet<string>(StringComparer.Ordinal);
            var selectionOwners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in state.EffectRuntime.EffectNodes)
            {
                if (node == null) continue;
                bool newProtocol = Text(node.NormalizedArguments, "paymentProtocol") == Protocol;
                var raw = Value(node.ContinuationData, ReceiptField);
                if (raw == null)
                {
                    if (node.FlowStage == AwaitingEventStage) return Fail("route_toll_receipt_missing", out diagnostic);
                    continue; // 新节点尚未开始或历史节点无资金。
                }
                if (!newProtocol || !TryReadReceipt(node, out var receipt, out diagnostic) ||
                    !TryValidateReceipt(state, node, receipt, out diagnostic))
                    return Fail(diagnostic.Length == 0 ? "route_toll_protocol_invalid" : diagnostic, out diagnostic);
                if (!reservations.Add(receipt.ReservationId) || !selectionOwners.Add(receipt.SelectionOwnerEffectId))
                    return Fail("route_toll_duplicate_reservation", out diagnostic);
                if (receipt.State == "held")
                {
                    if (node.FlowStage != AwaitingEventStage || node.Status == EffectNodeStatus.Completed ||
                        node.Status == EffectNodeStatus.Failed || node.PendingOutcome != EffectPendingOutcome.None)
                        return Fail("route_toll_held_stage_invalid", out diagnostic);
                    if (!HasSingleEventChild(state, node)) return Fail("route_toll_event_binding_invalid", out diagnostic);
                }
                if (receipt.State == "settled" && !TryValidateCompletedEvent(state, node, out diagnostic, false)) return false;
                if (receipt.State == "released" && node.ChildEffectIds.Count != 0)
                    return Fail("route_toll_release_event_conflict", out diagnostic);
            }
            foreach (var player in state.Players)
            {
                try { QueryBalance(state, player.PlayerId); }
                catch (KernelException exception) { return Fail(exception.Code, out diagnostic); }
            }
            return true;
        }

        public static bool TryReadReceipt(EffectNodeRuntimeState owner,
            out RouteTollEscrowReceipt receipt, out string diagnostic)
        {
            receipt = null; diagnostic = string.Empty;
            var raw = Value(owner?.ContinuationData, ReceiptField);
            if (owner?.EffectTypeId != ExplorationEffectTypeIds.Explore ||
                owner.NormalizedArguments?.Kind != NormalizedValueKind.Object || !owner.NormalizedArguments.IsValid() ||
                !Required(owner.NormalizedArguments, "paymentProtocol", NormalizedValueKind.String) ||
                Text(owner.NormalizedArguments, "paymentProtocol") != Protocol ||
                owner.ContinuationData?.Kind != NormalizedValueKind.Object || !owner.ContinuationData.IsValid() ||
                raw?.Kind != NormalizedValueKind.Object || !raw.IsValid() || raw.Properties.Count != 13)
                return Fail("route_toll_receipt_shape_invalid", out diagnostic);
            if (!Required(raw, "reservationId", NormalizedValueKind.String) ||
                !Reference(raw, "ownerEffectId", "effect") || !Reference(raw, "selectionOwnerEffectId", "effect") ||
                !Required(raw, "selectionSessionId", NormalizedValueKind.String) ||
                !Required(raw, "selectionRevision", NormalizedValueKind.Integer) ||
                !Required(raw, "payerPlayerId", NormalizedValueKind.Integer) ||
                !Required(raw, "startAvailableGoldVoucher", NormalizedValueKind.Integer) ||
                !Required(raw, "originalAmount", NormalizedValueKind.Integer) ||
                !Required(raw, "escrowAmount", NormalizedValueKind.Integer) ||
                !Required(raw, "state", NormalizedValueKind.String) ||
                !Required(raw, "frozenPayments", NormalizedValueKind.Array) ||
                !Required(raw, "settlementReceiptId", NormalizedValueKind.String) ||
                !Required(raw, "releaseReceiptId", NormalizedValueKind.String))
                return Fail("route_toll_receipt_fields_missing", out diagnostic);
            receipt = new RouteTollEscrowReceipt
            {
                ReservationId = Text(raw, "reservationId"), OwnerEffectId = Value(raw, "ownerEffectId").ReferenceId,
                SelectionOwnerEffectId = Value(raw, "selectionOwnerEffectId").ReferenceId,
                SelectionSessionId = Text(raw, "selectionSessionId"), SelectionRevision = Number(raw, "selectionRevision"),
                PayerPlayerId = Number(raw, "payerPlayerId"), StartAvailableGoldVoucher = Number(raw, "startAvailableGoldVoucher"),
                OriginalAmount = Number(raw, "originalAmount"), EscrowAmount = Number(raw, "escrowAmount"),
                State = Text(raw, "state"), FrozenPayments = Value(raw, "frozenPayments").Clone(),
                SettlementReceiptId = Text(raw, "settlementReceiptId"), ReleaseReceiptId = Text(raw, "releaseReceiptId")
            };
            if (receipt.SelectionRevision < 0 || receipt.PayerPlayerId < 0 || receipt.StartAvailableGoldVoucher < 0 ||
                receipt.OriginalAmount < 0 || receipt.OriginalAmount > receipt.StartAvailableGoldVoucher ||
                receipt.EscrowAmount < 0 || string.IsNullOrEmpty(receipt.SelectionSessionId) ||
                string.IsNullOrEmpty(receipt.ReservationId)) return Fail("route_toll_receipt_values_invalid", out diagnostic);
            if (receipt.FrozenPayments.Items.Count > MaximumPaymentLines)
                return Fail("route_toll_receipt_capacity_exceeded", out diagnostic);
            int planTotal = 0;
            var routes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in receipt.FrozenPayments.Items)
            {
                if (line.Kind != NormalizedValueKind.Object || line.Properties.Count != 3 ||
                    !Reference(line, "routeId", "route") || !Required(line, "receiverSubjectKey", NormalizedValueKind.String) ||
                    !Required(line, "amount", NormalizedValueKind.Integer) ||
                    Number(line, "amount") != RouteTollService.RouteCostGoldVoucher ||
                    !routes.Add(Value(line, "routeId").ReferenceId))
                    return Fail("route_toll_payment_line_invalid", out diagnostic);
                planTotal += RouteTollService.RouteCostGoldVoucher;
            }
            if (planTotal != receipt.OriginalAmount) return Fail("route_toll_plan_total_mismatch", out diagnostic);
            if (receipt.State == "held" && (receipt.EscrowAmount != receipt.OriginalAmount ||
                receipt.SettlementReceiptId.Length != 0 || receipt.ReleaseReceiptId.Length != 0) ||
                receipt.State == "settled" && (receipt.EscrowAmount != 0 || receipt.SettlementReceiptId.Length == 0 ||
                receipt.ReleaseReceiptId.Length != 0) || receipt.State == "released" && (receipt.EscrowAmount != 0 ||
                receipt.ReleaseReceiptId.Length == 0 || receipt.SettlementReceiptId.Length != 0) ||
                receipt.State != "held" && receipt.State != "settled" && receipt.State != "released")
                return Fail("route_toll_receipt_state_invalid", out diagnostic);
            return true;
        }

        private bool TryValidateReceipt(GameState state, EffectNodeRuntimeState owner,
            RouteTollEscrowReceipt receipt, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (receipt == null || receipt.OwnerEffectId != owner.EffectId || receipt.PayerPlayerId != owner.PlayerId ||
                receipt.ReservationId != ReservationId(state, owner) || receipt.SelectionRevision < 0 ||
                receipt.StartAvailableGoldVoucher < 0 || receipt.StartAvailableGoldVoucher > NormalizedValue.MaxInteger ||
                string.IsNullOrEmpty(receipt.SelectionSessionId)) return Fail("route_toll_binding_invalid", out diagnostic);
            var selectionOwner = state.EffectRuntime.EffectNodes.Find(node => node != null &&
                node.EffectId == receipt.SelectionOwnerEffectId && node.PlayerId == owner.PlayerId);
            if (selectionOwner == null || !IsAncestor(state, owner, selectionOwner.EffectId))
                return Fail("route_toll_selection_owner_invalid", out diagnostic);
            if (!Reference(owner.NormalizedArguments, "selectionOwnerEffectId", "effect") ||
                Value(owner.NormalizedArguments, "selectionOwnerEffectId").ReferenceId != receipt.SelectionOwnerEffectId ||
                Text(owner.NormalizedArguments, "selectionSessionId") != receipt.SelectionSessionId ||
                !Required(owner.NormalizedArguments, "selectionRevision", NormalizedValueKind.Integer) ||
                Number(owner.NormalizedArguments, "selectionRevision") != receipt.SelectionRevision)
                return Fail("route_toll_selection_binding_mismatch", out diagnostic);
            if (!TryValidatePayments(state, owner.PlayerId, receipt.FrozenPayments, out int total, out diagnostic)) return false;
            if (total != receipt.OriginalAmount || total > receipt.StartAvailableGoldVoucher)
                return Fail("route_toll_start_budget_insufficient", out diagnostic);
            if (receipt.State == "settled" && receipt.SettlementReceiptId != EndReceiptId(state, owner, "route-toll-settlement") ||
                receipt.State == "released" && receipt.ReleaseReceiptId != EndReceiptId(state, owner, "route-toll-release"))
                return Fail("route_toll_end_receipt_invalid", out diagnostic);
            return true;
        }

        private bool TryValidatePayments(GameState state, int payer, NormalizedValue payments,
            out int total, out string diagnostic)
        {
            total = 0; diagnostic = string.Empty;
            if (payments?.Kind != NormalizedValueKind.Array || payments.Items == null || !payments.IsValid())
                return Fail("route_toll_plan_invalid", out diagnostic);
            if (payments.Items.Count > MaximumPaymentLines) return Fail("route_toll_receipt_capacity_exceeded", out diagnostic);
            var routes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in payments.Items)
            {
                if (line?.Kind != NormalizedValueKind.Object || line.Properties.Count != 3 ||
                    !Reference(line, "routeId", "route") || !Required(line, "receiverSubjectKey", NormalizedValueKind.String) ||
                    !Required(line, "amount", NormalizedValueKind.Integer) ||
                    Number(line, "amount") != RouteTollService.RouteCostGoldVoucher)
                    return Fail("route_toll_payment_line_invalid", out diagnostic);
                string route = Value(line, "routeId").ReferenceId;
                if (!routes.Add(route)) return Fail("route_toll_route_duplicate", out diagnostic);
                try { map.GetRoute(route); }
                catch (ArgumentException) { return Fail("route_toll_route_unknown", out diagnostic); }
                string key = Text(line, "receiverSubjectKey");
                if (key.StartsWith("company:", StringComparison.Ordinal))
                    return Fail("route_toll_company_recipient_unsupported", out diagnostic);
                if (key != BankSubjectKey && (!TryResolvePlayer(state, key, out var receiver) || receiver.PlayerId == payer))
                    return Fail("route_toll_receiver_invalid", out diagnostic);
                total += RouteTollService.RouteCostGoldVoucher;
            }
            return true;
        }

        private static bool TryValidateOwner(GameState state, EffectNodeRuntimeState owner, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (state?.EffectRuntime?.EffectNodes == null || owner == null || string.IsNullOrEmpty(owner.EffectId) ||
                state.FindPlayer(owner.PlayerId)?.Resources == null || state.FindPlayer(owner.PlayerId).Resources.GoldVoucher < 0 ||
                !state.EffectRuntime.EffectNodes.Any(node => ReferenceEquals(node, owner)) ||
                owner.NormalizedArguments?.Kind != NormalizedValueKind.Object || !owner.NormalizedArguments.IsValid() ||
                !Required(owner.NormalizedArguments, "paymentProtocol", NormalizedValueKind.String) ||
                owner.EffectTypeId != ExplorationEffectTypeIds.Explore ||
                Text(owner.NormalizedArguments, "paymentProtocol") != Protocol)
                return Fail("route_toll_owner_invalid", out diagnostic);
            return true;
        }

        private static bool TryPrepareContinuation(EffectNodeRuntimeState owner, RouteTollEscrowReceipt receipt,
            out NormalizedValue next, out string diagnostic)
        {
            next = null; diagnostic = string.Empty;
            if (owner.ContinuationData?.Kind != NormalizedValueKind.Object || !owner.ContinuationData.IsValid())
                return Fail("route_toll_continuation_invalid", out diagnostic);
            var entries = owner.ContinuationData.Properties.Where(entry => entry.Name != ReceiptField)
                .Select(entry => entry.Clone()).ToList();
            try
            {
                var fields = new List<NormalizedValueEntry>
                {
                    Entry("reservationId", NormalizedValue.CreateString(receipt.ReservationId)),
                    Entry("ownerEffectId", NormalizedValue.CreateStableReference("effect", receipt.OwnerEffectId)),
                    Entry("selectionOwnerEffectId", NormalizedValue.CreateStableReference("effect", receipt.SelectionOwnerEffectId)),
                    Entry("selectionSessionId", NormalizedValue.CreateString(receipt.SelectionSessionId)),
                    Entry("selectionRevision", NormalizedValue.CreateInteger(receipt.SelectionRevision)),
                    Entry("payerPlayerId", NormalizedValue.CreateInteger(receipt.PayerPlayerId)),
                    Entry("startAvailableGoldVoucher", NormalizedValue.CreateInteger(receipt.StartAvailableGoldVoucher)),
                    Entry("originalAmount", NormalizedValue.CreateInteger(receipt.OriginalAmount)),
                    Entry("escrowAmount", NormalizedValue.CreateInteger(receipt.EscrowAmount)),
                    Entry("state", NormalizedValue.CreateString(receipt.State)),
                    Entry("frozenPayments", receipt.FrozenPayments.Clone()),
                    Entry("settlementReceiptId", NormalizedValue.CreateString(receipt.SettlementReceiptId)),
                    Entry("releaseReceiptId", NormalizedValue.CreateString(receipt.ReleaseReceiptId))
                };
                entries.Add(Entry(ReceiptField, NormalizedValue.CreateObject(fields)));
                next = NormalizedValue.CreateObject(entries);
                if (!next.TryValidate(out diagnostic)) return Fail("route_toll_receipt_capacity_or_shape_invalid", out diagnostic);
                return true;
            }
            catch (ArgumentException) { return Fail("route_toll_receipt_capacity_or_shape_invalid", out diagnostic); }
        }

        private static bool TryValidateCompletedEvent(GameState state, EffectNodeRuntimeState owner, out string diagnostic,
            bool requireAllChildren = true)
        {
            diagnostic = string.Empty;
            if (!HasSingleEventChild(state, owner)) return Fail("route_toll_event_binding_invalid", out diagnostic);
            foreach (string id in owner.ChildEffectIds)
            {
                var child = state.EffectRuntime.EffectNodes.Find(node => node != null && node.EffectId == id);
                // child.Completed意味着该子节点的完成响应也结束；不接受Failed/Faulted。
                if (child == null || child.ParentEffectId != owner.EffectId ||
                    (requireAllChildren || child.EffectTypeId == EventCardEffectTypeIds.Resolve) && child.Status != EffectNodeStatus.Completed)
                    return Fail("route_toll_event_not_completed", out diagnostic);
            }
            return true;
        }

        private static bool HasSingleEventChild(GameState state, EffectNodeRuntimeState owner) =>
            owner.ChildEffectIds.Distinct(StringComparer.Ordinal).Count() == owner.ChildEffectIds.Count &&
            owner.ChildEffectIds.Count(id => state.EffectRuntime.EffectNodes.Any(node => node != null &&
                node.EffectId == id && node.ParentEffectId == owner.EffectId && node.EffectTypeId == EventCardEffectTypeIds.Resolve)) == 1;

        private static bool IsAncestor(GameState state, EffectNodeRuntimeState owner, string ancestorId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string parent = owner.ParentEffectId;
            while (!string.IsNullOrEmpty(parent) && seen.Add(parent))
            {
                if (parent == ancestorId) return true;
                parent = state.EffectRuntime.EffectNodes.Find(node => node != null && node.EffectId == parent)?.ParentEffectId;
            }
            return false;
        }

        private static bool TryResolvePlayer(GameState state, string key, out PlayerState player)
        {
            player = null;
            if (key == null || !key.StartsWith("player:", StringComparison.Ordinal) ||
                !int.TryParse(key.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                id < 0 || key != "player:" + id.ToString(CultureInfo.InvariantCulture)) return false;
            player = state.FindPlayer(id);
            return player?.Resources != null && player.Resources.GoldVoucher >= 0;
        }

        private static bool SameReservation(RouteTollEscrowReceipt left, RouteTollEscrowReceipt right) =>
            left.ReservationId == right.ReservationId && left.OwnerEffectId == right.OwnerEffectId &&
            left.SelectionOwnerEffectId == right.SelectionOwnerEffectId && left.SelectionSessionId == right.SelectionSessionId &&
            left.SelectionRevision == right.SelectionRevision && left.PayerPlayerId == right.PayerPlayerId &&
            left.StartAvailableGoldVoucher == right.StartAvailableGoldVoucher && left.OriginalAmount == right.OriginalAmount &&
            left.FrozenPayments.ToDeterministicString() == right.FrozenPayments.ToDeterministicString();

        private static string ReservationId(GameState state, EffectNodeRuntimeState owner) =>
            StableIdFactory.Create("route-toll-reservation", state.GameId, owner.EffectId, Protocol);
        private static string EndReceiptId(GameState state, EffectNodeRuntimeState owner, string kind) =>
            StableIdFactory.Create(kind, state.GameId, owner.EffectId, ReservationId(state, owner));
        private static NormalizedValue Value(NormalizedValue value, string name) =>
            value?.Properties?.Find(entry => entry != null && entry.Name == name)?.Value;
        private static string Text(NormalizedValue value, string name) => Value(value, name)?.StringValue ?? string.Empty;
        private static int Number(NormalizedValue value, string name) => (int)Value(value, name).IntegerValue;
        private static bool Required(NormalizedValue value, string name, NormalizedValueKind kind) => Value(value, name)?.Kind == kind;
        private static bool Reference(NormalizedValue value, string name, string kind) =>
            Required(value, name, NormalizedValueKind.StableReference) && Value(value, name).ReferenceType == kind &&
            !string.IsNullOrEmpty(Value(value, name).ReferenceId);
        private static NormalizedValueEntry Entry(string name, NormalizedValue value) => new NormalizedValueEntry { Name = name, Value = value };
        private static bool Fail(string code, out string diagnostic) { diagnostic = code; return false; }
        private static ValidationResult Reject(string diagnostic) => ValidationResult.Failure(
            YC.Domain.Rules.CommandErrorCode.InvalidTarget, diagnostic);
    }

    // 只计算余额，不生成或重建权威会话和开始快照。
    public sealed class RouteSelectionEscrowBudgetReader : IRouteSelectionBudgetReader
    {
        public ValidationResult TryReadBudget(GameState state, RouteSelectionSession session,
            out RouteSelectionBudget budget)
        {
            budget = null;
            if (session == null || session.SchemaVersion != 1 || session.MapId != state?.MapId ||
                string.IsNullOrEmpty(session.SessionId) || string.IsNullOrEmpty(session.BudgetBindingId) ||
                session.StartGoldVoucherAmount < 0 || session.StartGoldVoucherAmount > NormalizedValue.MaxInteger ||
                state.FindPlayer(session.PlayerId)?.Resources == null)
                return ValidationResult.Failure(YC.Domain.Rules.CommandErrorCode.InvalidTarget, "route_toll_budget_binding_invalid");
            int available = state.FindPlayer(session.PlayerId).Resources.GoldVoucher;
            if (available < 0) return ValidationResult.Failure(YC.Domain.Rules.CommandErrorCode.InvalidTarget, "route_toll_balance_invalid");
            budget = new RouteSelectionBudget
            {
                BudgetBindingId = session.BudgetBindingId, StartGoldVoucherAmount = session.StartGoldVoucherAmount,
                AvailableSelectionGoldVoucherAmount = session.Stage == RouteSelectionStage.Selecting
                    ? Math.Min(available, session.StartGoldVoucherAmount) : 0
            };
            return ValidationResult.Success;
        }
    }
}
