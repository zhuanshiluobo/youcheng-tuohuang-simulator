// 共用选路接口/DTO。资金会话由Host创建，本地输入通过只读构造方法编辑。
// 查询与会话、探索托管和界面分别使用同一合同。
// 集合在实际实现中复制并封为只读；UI本地编辑，最终一次candidate_ids回答。
using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Harvest;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Travel
{
    public interface IRouteResourceSelectionService
    {
        ValidationResult TryCreateExplorationPreview(GameState state, int playerId,
            out RouteSelectionSession session, out RouteSelectionDraft draft, out RouteSelectionProjection projection);

        // 联机客户端由已有本人公共投影提供牌堆数量和观察版本，绝不补入隐藏牌 ID。
        ValidationResult TryCreateExplorationPreview(GameState state, GameStateView publicView, int playerId,
            out RouteSelectionSession session, out RouteSelectionDraft draft, out RouteSelectionProjection projection);

        RouteSelectionProjection Query(
            GameState state, RouteSelectionSession session, RouteSelectionDraft draft,
            IReadOnlyList<string> selectableLocationIds);

        // 只读复核同一冻结事实和计划；成员投影无Host runtime也可调用，不生成提交收据。
        ValidationResult TryValidatePlan(
            GameState state, RouteSelectionSession session, RouteSelectionRequestBinding requestBinding,
            RouteSelectionDraft draft, IReadOnlyList<string> selectableLocationIds,
            out RouteSelectionPreview preview);

        // requestBinding来自Host已保存的请求，draft由该请求的candidate_ids答案解码。
        // 不接收客户端金额、开始预算或可写PaymentPlan；不扣款、不翻牌。
        ValidationResult TryFinalize(
            GameState state, RouteSelectionSession session, RouteSelectionRequestBinding requestBinding,
            RouteSelectionDraft draft, IReadOnlyList<string> selectableLocationIds,
            out RouteSelectionCommitment commitment);
    }

    public interface IRouteSelectionAnswerCodec
    {
        ValidationResult TryParse(
            InteractionRequest request, IReadOnlyList<string> selectedCandidateIds,
            out RouteSelectionAnswer answer);
    }

    // 托管服务提供，钱包已是可支配余额；托管held不允许普通支付访问。
    public interface IRouteSelectionBudgetReader
    {
        ValidationResult TryReadBudget(
            GameState state, RouteSelectionSession session, out RouteSelectionBudget budget);
    }

    // 会话服务提供；只能在Host工作副本的选择owner节点上写入开始快照和请求绑定。
    public interface IRouteSelectionSessionAuthority
    {
        ValidationResult TryOpen(
            GameState state, EffectNodeRuntimeState ownerNode, RouteSelectionOptions options,
            out RouteSelectionSession session);

        ValidationResult TryRestore(
            GameState state, EffectNodeRuntimeState ownerNode,
            out RouteSelectionSession session, out RouteSelectionRequestBinding requestBinding);

        // 仅首次发请求或事实失效后的显式刷新调用，绝不每次点路线调用。
        ValidationResult TryPrepareRequest(
            GameState state, EffectNodeRuntimeState ownerNode, RouteSelectionSession session,
            IReadOnlyList<string> selectableLocationIds,
            out RouteSelectionRequestBinding requestBinding,
            out IReadOnlyList<string> candidateIds);

        // Host只读：从选择owner的封存记录解析，不信任consumer raw arguments中的金额/计划。
        ValidationResult TryResolveFinalized(
            GameState state, string selectionOwnerEffectId,
            out RouteSelectionSession session, out RouteSelectionCommitment commitment);

        // Host工作副本：绑定唯一合法consumer；同一consumer重复Claim幂等，另一consumer拒绝。
        // 与托管服务的reserve处于同次RuleCommit，失败不留半次claim。
        ValidationResult TryClaimFinalized(
            GameState state, EffectNodeRuntimeState consumerNode,
            string selectionOwnerEffectId, string expectedCommitmentId,
            out RouteSelectionSession session, out RouteSelectionCommitment commitment);
    }

    // Domain内部构造只读投影会话；不能据此在客户端Open/Claim/Finalize资金。
    // 使用现有GameStateView.Interactions/InteractionView，避免公开Host ContinuationData。
    public interface IRouteSelectionClientViewReader
    {
        ValidationResult TryRead(
            GameStateView playerView, InteractionView interaction,
            out RouteSelectionSession session, out RouteSelectionRequestBinding requestBinding);
    }

    public enum RouteSelectionStage { Selecting = 0, Committed = 1, Cancelled = 2, Settled = 3 }
    public enum RouteSelectionPurpose { NormalHarvest = 0, ExtraHarvest = 1, Exploration = 2 }
    public enum RouteFreeReason { None = 0, OwnInfluence = 1, PublicRoad = 2, OwnInfluenceAndPublicRoad = 3 }
    public enum RouteRecipientKind { Bank = 0, Player = 1 }
    public enum RouteSelectionCandidateKind { Commit = 0, Cancel = 1, Location = 2, RouteAndRecipient = 3 }

    // Purpose只决定原资格/阶段入口，不改变共同剪枝或RouteId收费规则。
    public sealed class RouteSelectionOptions
    {
        internal RouteSelectionOptions() { }
        public RouteSelectionOptions(RouteSelectionPurpose purpose, int playerId, string startLocationId,
            IReadOnlyList<string> selectableLocationUpperBound, int minimumLocations, int maximumLocations,
            bool allowCancelBeforeCommit = false, string actionContextId = "", string existingStartBudgetBindingId = "")
        {
            if (selectableLocationUpperBound == null) throw new ArgumentNullException(nameof(selectableLocationUpperBound));
            Purpose = purpose; PlayerId = playerId; StartLocationId = startLocationId;
            SelectableLocationUpperBound = Array.AsReadOnly(new List<string>(selectableLocationUpperBound).ToArray());
            MinimumLocations = minimumLocations; MaximumLocations = maximumLocations; AllowCancelBeforeCommit = allowCancelBeforeCommit;
            ActionContextId = actionContextId ?? ""; ExistingStartBudgetBindingId = existingStartBudgetBindingId ?? "";
        }
        public int SchemaVersion { get; internal set; } = 1;
        public RouteSelectionPurpose Purpose { get; internal set; }
        public int PlayerId { get; internal set; }
        public string StartLocationId { get; internal set; }
        public string ActionContextId { get; internal set; }
        public IReadOnlyList<string> SelectableLocationUpperBound { get; internal set; }
        public int MinimumLocations { get; internal set; }
        public int MaximumLocations { get; internal set; }
        public bool AllowCancelBeforeCommit { get; internal set; }
        // 原正常/extra采集预算凭据；探索为空，由会话服务在Open时读取可用钱包。
        // 凭据必须指向Host已存快照，不能是客户端传来的金额。
        public string ExistingStartBudgetBindingId { get; internal set; }
    }

    public sealed class RouteSelectionSession
    {
        public int SchemaVersion { get; internal set; } = 1;
        public string SessionId { get; internal set; }
        public string OwnerEffectId { get; internal set; }
        public RouteSelectionPurpose Purpose { get; internal set; }
        public int PlayerId { get; internal set; }
        public string MapId { get; internal set; }
        public string StartLocationId { get; internal set; }
        public string ActionContextId { get; internal set; }
        public int OpenedAtStateRevision { get; internal set; }
        public int StartGoldVoucherAmount { get; internal set; }
        public string BudgetBindingId { get; internal set; }
        public IReadOnlyList<string> SelectableLocationUpperBound { get; internal set; }
        public int MinimumLocations { get; internal set; }
        public int MaximumLocations { get; internal set; }
        public bool AllowCancelBeforeCommit { get; internal set; }
        public RouteSelectionStage Stage { get; internal set; }
        public RouteSelectionRequestBinding ActiveRequestBinding { get; internal set; }
        public CollectionFreeLocationResult CollectionFreeLocations { get; internal set; }
    }

    public sealed class RouteSelectionRequestBinding
    {
        public int SchemaVersion { get; internal set; } = 1;
        public string SessionId { get; internal set; }
        public string CandidateSetId { get; internal set; }
        public int CandidateSetVersion { get; internal set; }
        public string ScopeKey { get; internal set; }
        public string FrozenFactsDigest { get; internal set; }
        // 当前InteractionRequest的revision，不能用Effect执行前revision冒充。
        public int RequestStateRevision { get; internal set; }
    }

    // 本地状态，不逐点击写Host；数组顺序保留用户确认顺序。
    public sealed class RouteSelectionDraft
    {
        public string SessionId { get; }
        public int LocalDraftVersion { get; }
        public IReadOnlyList<string> ConfirmedRouteIds { get; }
        public IReadOnlyList<string> SelectedLocationIds { get; }
        public IReadOnlyList<RouteRecipientChoice> RecipientChoices { get; }
        public string EditingRouteId { get; }
        // 只有用户明确“完成0点采集”才true；未选目标的编辑草稿始终false。
        public bool ConfirmEmptySelection { get; }

        public RouteSelectionDraft(
            string sessionId, int localDraftVersion, IReadOnlyList<string> confirmedRouteIds,
            IReadOnlyList<string> selectedLocationIds, IReadOnlyList<RouteRecipientChoice> recipientChoices,
            string editingRouteId = "", bool confirmEmptySelection = false)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("会话ID不能为空。", nameof(sessionId));
            if (localDraftVersion < 0) throw new ArgumentOutOfRangeException(nameof(localDraftVersion));
            SessionId = sessionId;
            LocalDraftVersion = localDraftVersion;
            ConfirmedRouteIds = Copy(confirmedRouteIds, nameof(confirmedRouteIds));
            SelectedLocationIds = Copy(selectedLocationIds, nameof(selectedLocationIds));
            RecipientChoices = Copy(recipientChoices, nameof(recipientChoices));
            EditingRouteId = editingRouteId ?? string.Empty;
            ConfirmEmptySelection = confirmEmptySelection;
        }

        public RouteSelectionDraft WithSelection(
            IReadOnlyList<string> confirmedRouteIds, IReadOnlyList<string> selectedLocationIds,
            IReadOnlyList<RouteRecipientChoice> recipientChoices,
            string editingRouteId = "", bool confirmEmptySelection = false)
        {
            return new RouteSelectionDraft(SessionId, checked(LocalDraftVersion + 1), confirmedRouteIds,
                selectedLocationIds, recipientChoices, editingRouteId, confirmEmptySelection);
        }

        private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source, string parameterName)
        {
            if (source == null) throw new ArgumentNullException(parameterName);
            var values = new T[source.Count];
            for (int i = 0; i < values.Length; i++) values[i] = source[i];
            return Array.AsReadOnly(values);
        }
    }

    public sealed class RouteSelectionAnswer
    {
        public bool IsCancellation { get; internal set; }
        public string ScopeKey { get; internal set; }
        public RouteSelectionDraft Draft { get; internal set; }
    }

    // ID具有稳定typed编码；只解码已存在request.CandidateIds中的ID，无第二份持久映射。
    public sealed class RouteSelectionCandidate
    {
        public string CandidateId { get; internal set; }
        public RouteSelectionCandidateKind Kind { get; internal set; }
        public string LocationId { get; internal set; }
        public string RouteId { get; internal set; }
        public string ReceiverSubjectKey { get; internal set; }
    }

    public sealed class RouteRecipient
    {
        public RouteRecipientKind Kind { get; internal set; }
        public string SubjectKey { get; internal set; } // bank / player:<正整数>
        public int? PlayerId { get; internal set; } // 公司不伪装成玩家
    }

    public sealed class RouteRecipientChoice
    {
        public string RouteId { get; }
        public string ReceiverSubjectKey { get; }

        public RouteRecipientChoice(string routeId, string receiverSubjectKey)
        {
            if (string.IsNullOrEmpty(routeId)) throw new ArgumentException("航道ID不能为空。", nameof(routeId));
            if (string.IsNullOrEmpty(receiverSubjectKey)) throw new ArgumentException("接收主体不能为空。", nameof(receiverSubjectKey));
            RouteId = routeId;
            ReceiverSubjectKey = receiverSubjectKey;
        }
    }

    public sealed class RouteSelectionBudget
    {
        public string BudgetBindingId { get; internal set; }
        public int StartGoldVoucherAmount { get; internal set; }
        public int AvailableSelectionGoldVoucherAmount { get; internal set; } // min(开始预算,当前钱包可用)
    }

    public sealed class RouteSelectionRouteOption
    {
        public string RouteId { get; internal set; }
        public IReadOnlyList<string> CoveredLocationIds { get; internal set; }
        public RouteFreeReason FreeReason { get; internal set; }
        public bool IsFrontier { get; internal set; }
        public bool IsOriginallyConfirmed { get; internal set; }
        public bool IsAutomaticallySelectedFreeRoute { get; internal set; }
        public bool IsRetainedInPreview { get; internal set; }
        public bool IsPrunedInPreview { get; internal set; }
        public int TollGoldVoucherAmount { get; internal set; }
        public bool CanAffordIfCharged { get; internal set; }
        public IReadOnlyList<RouteRecipient> EligibleRecipients { get; internal set; }
    }

    public sealed class RouteSelectionLocationOption
    {
        public string LocationId { get; internal set; }
        public bool IsReachableInOriginalDraft { get; internal set; }
        public bool IsReachableInPreview { get; internal set; }
        public bool IsSelected { get; internal set; }
        public bool IsFreeCollection { get; internal set; }
        public bool CanSelect => IsFreeCollection || IsReachableInOriginalDraft;
    }

    // 一个共享父链接树，只证明连通，不用它挑收费路线。
    public sealed class RouteConnectivityLink
    {
        public string LocationId { get; internal set; }
        public string ParentLocationId { get; internal set; }
        public string ViaRouteId { get; internal set; }
    }

    public sealed class RouteTollPlanLine
    {
        public string RouteId { get; internal set; }
        public string ReceiverSubjectKey { get; internal set; }
        public int GoldVoucherAmount { get; internal set; }
    }

    public sealed class RouteTollPlan
    {
        public IReadOnlyList<RouteTollPlanLine> Lines { get; internal set; }
        public int TotalGoldVoucherAmount { get; internal set; }
    }

    public sealed class RouteSelectionPreview
    {
        public string ScopeKey { get; internal set; }
        public string PlanDigest { get; internal set; } // Host可由同一答案+冻结事实确定重算
        public CollectionPartition Collection { get; internal set; }
        public IReadOnlyList<string> SelectedLocationIds { get; internal set; }
        public IReadOnlyList<string> OriginalConfirmedRouteIds { get; internal set; }
        public IReadOnlyList<string> PrunedRouteIds { get; internal set; }
        public IReadOnlyList<string> EffectiveRouteIds { get; internal set; }
        public IReadOnlyList<string> ChargeRouteIds { get; internal set; }
        public IReadOnlyList<string> PrunedFreeRouteIds { get; internal set; }
        public IReadOnlyList<RouteConnectivityLink> ConnectivityWitness { get; internal set; }
        public RouteTollPlan PaymentPlan { get; internal set; }
        public ValidationResult FinalizationValidation { get; internal set; }
        public int RemainingStartGoldVoucherAmount { get; internal set; }
    }

    public sealed class RouteSelectionProjection
    {
        public ValidationResult Validation { get; internal set; }
        public string SessionId { get; internal set; }
        public int LocalDraftVersion { get; internal set; }
        public int ObservedStateRevision { get; internal set; }
        public RouteSelectionBudget Budget { get; internal set; }
        public IReadOnlyList<string> ReachableLocationIds { get; internal set; }
        public IReadOnlyList<string> AutomaticallySelectedFreeRouteIds { get; internal set; }
        public IReadOnlyList<RouteSelectionRouteOption> RouteOptions { get; internal set; }
        public IReadOnlyList<RouteSelectionLocationOption> LocationOptions { get; internal set; }
        public int ProvisionalGoldVoucherAmount { get; internal set; } // 未定目标时按草案确认路暂估
        public RouteSelectionPreview Preview { get; internal set; } // 没目标且非明确0点完成时null
        public CollectionFreeLocationResult CollectionFreeLocations { get; internal set; }
    }

    public sealed class RouteSelectionCommitment
    {
        public string CommitmentId { get; internal set; }
        public string SessionId { get; internal set; }
        public string OwnerEffectId { get; internal set; }
        public int PlayerId { get; internal set; }
        public int AcceptedAtStateRevision { get; internal set; }
        public RouteSelectionPreview AcceptedPreview { get; internal set; }
        public string BudgetBindingId { get; internal set; } // 此DTO不是已交付收据
    }
}
