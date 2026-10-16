using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Scoring;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Domain.Rules
{
    /// <summary>
    /// 回合主链的唯一推进入口。主链节点本身是持久化数据，实际的进入、等待和完成由
    /// EffectExecutor 驱动；RoundAdvanceService 只保留为本类型的兼容外观。
    /// </summary>
    public sealed class RoundExecutionService
    {
        public const string MainlineEffectTypeId = "round.mainline.node";
        public const string MainlineCompletionInteractionTypeId = "round.mainline.complete";
        public const string PlayerCharacterRecyclingTypeId = "round.player.recycle_characters";
        public const string PlayerEntranceTaskTypeId = "setup.player.entrance";

        private readonly TurnOrderService turnOrderService;
        private readonly CharacterCardService characterCardService;
        private readonly MainActionBudgetService mainActionBudgetService;
        private readonly SpecialActionLifecycleService specialActionLifecycleService;
        private readonly EffectRegistry effectRegistry;
        private readonly EffectRuntimeLimits effectRuntimeLimits;
        private readonly FinalScoringService finalScoringService;
        private readonly RoundMainChainBuilder chainBuilder;
        private readonly RoundExecutionProjector projector;

        public RoundExecutionService()
            : this(
                new TurnOrderService(),
                new CharacterCardService(),
                new MainActionBudgetService(),
                new SpecialActionLifecycleService(),
                new EffectRegistry(),
                null)
        {
        }

        public RoundExecutionService(EffectRegistry effectRegistry)
            : this(
                new TurnOrderService(),
                new CharacterCardService(),
                new MainActionBudgetService(),
                new SpecialActionLifecycleService(),
                effectRegistry,
                null)
        {
        }

        public RoundExecutionService(TurnOrderService turnOrderService)
            : this(
                turnOrderService,
                new CharacterCardService(turnOrderService),
                new MainActionBudgetService(),
                new SpecialActionLifecycleService())
        {
        }

        public RoundExecutionService(
            TurnOrderService turnOrderService,
            CharacterCardService characterCardService,
            MainActionBudgetService mainActionBudgetService,
            SpecialActionLifecycleService specialActionLifecycleService)
            : this(
                turnOrderService,
                characterCardService,
                mainActionBudgetService,
                specialActionLifecycleService,
                new EffectRegistry(),
                null)
        {
        }

        public RoundExecutionService(
            TurnOrderService turnOrderService,
            CharacterCardService characterCardService,
            MainActionBudgetService mainActionBudgetService,
            SpecialActionLifecycleService specialActionLifecycleService,
            EffectRegistry effectRegistry,
            EffectRuntimeLimits effectRuntimeLimits = null,
            FinalScoringService finalScoringService = null)
        {
            this.turnOrderService = turnOrderService ?? throw new ArgumentNullException(nameof(turnOrderService));
            this.characterCardService = characterCardService ?? throw new ArgumentNullException(nameof(characterCardService));
            this.mainActionBudgetService = mainActionBudgetService ?? throw new ArgumentNullException(nameof(mainActionBudgetService));
            this.specialActionLifecycleService = specialActionLifecycleService ?? throw new ArgumentNullException(nameof(specialActionLifecycleService));
            this.effectRegistry = effectRegistry ?? throw new ArgumentNullException(nameof(effectRegistry));
            this.effectRuntimeLimits = effectRuntimeLimits ?? EffectRuntimeLimits.Default;
            this.finalScoringService = finalScoringService;
            chainBuilder = new RoundMainChainBuilder();
            projector = new RoundExecutionProjector();
            EnsureMainlineRegistration();
        }

        public EffectRegistry EffectRegistry
        {
            get { return effectRegistry; }
        }

        public IReadOnlyList<MainlineNodeRuntimeState> GetCurrentMainChain(GameState state)
        {
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.MainNodes == null)
            {
                return new List<MainlineNodeRuntimeState>().AsReadOnly();
            }

            var result = new List<MainlineNodeRuntimeState>();
            for (var i = 0; i < state.EffectRuntime.MainNodes.Count; i++)
            {
                var node = state.EffectRuntime.MainNodes[i];
                if (node == null || node.RoundNumber != state.EffectRuntime.CurrentRoundNumber ||
                    (!string.IsNullOrEmpty(state.EffectRuntime.CurrentRoundExecutionId) &&
                     !string.IsNullOrEmpty(node.RoundExecutionId) &&
                     node.RoundExecutionId != state.EffectRuntime.CurrentRoundExecutionId))
                {
                    continue;
                }

                result.Add(node);
            }

            result.Sort((left, right) => left.MainlineIndex.CompareTo(right.MainlineIndex));
            return result.AsReadOnly();
        }

        public ValidationResult StartEntrance(GameState state)
        {
            if (state.Phase != GamePhase.Setup && state.Phase != GamePhase.Entrance)
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "只能在开局建立入场主链。");
            return CreateAndRunRound(state, 1, state.StartPlayerId, false, true);
        }

        public ValidationResult StartFirstRound(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (!string.IsNullOrEmpty(state.EffectRuntime == null ? string.Empty : state.EffectRuntime.ActiveMainNodeId))
            {
                return Advance(state);
            }

            var roundNumber = state.Round > 0 ? state.Round : 1;
            return CreateAndRunRound(state, roundNumber, state.StartPlayerId, false);
        }

        public ValidationResult CreateRound(GameState state, int roundNumber, int startPlayerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return CreateAndRunRound(state, roundNumber, startPlayerId, false);
        }

        /// <summary>
        /// 下一回合创建前，必要子树必须终结；真实独立旁路义务继续保管原实例。
        /// 这里只读检查，不清理残留状态，以便上层继续通过原交互完成恢复。
        /// </summary>
        public ValidationResult ValidateNextRoundCreation(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var runtime = state.EffectRuntime;
            if (runtime == null)
            {
                return ValidationResult.Success;
            }

            if (runtime.InteractionRequests != null)
            {
                for (var i = 0; i < runtime.InteractionRequests.Count; i++)
                {
                    var request = runtime.InteractionRequests[i];
                    if (request != null && request.Status == "open" &&
                        !EffectTreeExecutor.TryFindDeferredResponse(state, request.OwnerEffectId, out _))
                    {
                        return ValidationResult.Failure(
                            CommandErrorCode.PendingChoiceRequired,
                            "禁止创建下一回合：存在未终结的开放 Interaction（" +
                            request.GetStableInteractionId() + "）。");
                    }
                }
            }

            if (runtime.EffectNodes != null)
            {
                for (var i = 0; i < runtime.EffectNodes.Count; i++)
                {
                    var node = runtime.EffectNodes[i];
                    if (node != null && !IsTerminalEffectNode(node.Status) &&
                        !EffectTreeExecutor.TryFindDeferredResponse(state, node.EffectId, out _))
                    {
                        return ValidationResult.Failure(
                            CommandErrorCode.WrongPhase,
                            "禁止创建下一回合：存在未终结 Effect（" + node.EffectId +
                            ", status=" + node.Status + "）。");
                    }
                }
            }

            return ValidationResult.Success;
        }

        public ValidationResult PrepareActionWindowForSmoke(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.EffectRuntime == null)
            {
                state.EffectRuntime = new EffectRuntimeState();
            }

            if (!string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId))
            {
                return Advance(state);
            }

            var order = new List<int>(turnOrderService.GetTurnOrder(state, playerId));
            if (order.Count == 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "快速演示状态至少需要一名玩家。");
            }

            var chain = chainBuilder.Build(
                state.GameId,
                chainBuilder.CreateRoundExecutionId(state.GameId, 1, order),
                1,
                order,
                playerId);
            state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = chain.RoundNumber;
            state.EffectRuntime.CurrentRoundStartPlayerId = chain.StartPlayerId;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int>(chain.PlayerOrderSnapshot);
            state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.MainNodes.AddRange(chain.Nodes);

            MainlineNodeRuntimeState actionNode = null;
            for (var i = 0; i < chain.Nodes.Count; i++)
            {
                var node = chain.Nodes[i];
                if (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                    node.ActionRound == 1 && node.PlayerId == playerId)
                {
                    actionNode = node;
                    break;
                }
            }

            if (actionNode == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "快速演示状态的当前玩家不在主链顺序中。");
            }

            for (var i = 0; i < chain.Nodes.Count; i++)
            {
                if (chain.Nodes[i].MainlineIndex < actionNode.MainlineIndex)
                {
                    chain.Nodes[i].Status = EffectNodeStatus.Completed;
                }
            }

            state.EffectRuntime.ActiveMainNodeId = actionNode.NodeId;
            actionNode.Status = EffectNodeStatus.Ready;
            projector.Project(state);
            return RunActiveMainline(state);
        }

        public ValidationResult Advance(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.EffectRuntime == null)
            {
                state.EffectRuntime = new EffectRuntimeState();
            }

            if (state.EffectRuntime.Status != EffectRuntimeStatus.Active)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "回合主链已暂停于 Effect 故障。");
            }

            if (PauseFailedConcurrentStageIfNeeded(state))
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, state.EffectRuntime.LastFaultMessage);

            if (string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId))
            {
                return ValidationResult.Success;
            }

            return RunActiveMainline(state);
        }

        public ValidationResult CompleteCharacterCover(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var validation = EnsureLegacyMainlineIfNeeded(state);
            if (!validation.IsValid)
            {
                return validation;
            }

            var node = GetActiveNode(state);
            if (node == null || node.NodeTypeId != RoundMainlineNodeTypeIds.CharacterCover)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前不是统一盖放角色牌主链节点。");
            }

            var execution = FindEffectNode(state, node.ExecutionEffectId);
            if (IsParallelCoverExecution(execution))
            {
                // 新主链只能由真实 task 提交主牌；兼容完成调用不能代答或释放其子树。
                var task = execution.ChildEffectIds.Select(id => FindEffectNode(state, id)).SingleOrDefault(child =>
                    child != null && child.EffectTypeId == CharacterCoverEffectExecutor.EffectTypeId && child.PlayerId == playerId);
                if (task == null || !node.CompletedPlayerIds.Contains(playerId) ||
                    string.IsNullOrEmpty(state.FindPlayer(playerId)?.CoveredCharacterCardId))
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "尚未提交本人的正常盖放任务。");
                return Advance(state);
            }
            if (node.ActiveTaskPlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "请按主链固定顺序盖放角色牌。");
            }

            AddCompletedPlayer(node, playerId);
            var order = state.EffectRuntime.PlayerOrderSnapshot;
            if (AllPlayersCompleted(node, order))
            {
                node.ActiveTaskPlayerId = -1;
                node.CompletionRequested = true;
            }
            else
            {
                node.ActiveTaskPlayerId = FindNextIncompletePlayer(state, node, playerId);
                node.CompletionInteractionOrdinal += 1;
            }

            projector.Project(state);
            return ResolveManualCompletion(state, node);
        }

        public ValidationResult PrepareCollectionSubmission(GameState state, int playerId)
        {
            if (state == null || state.Phase != GamePhase.ResourceCollection)
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前不是采集阶段。");
            if (state.FindPlayer(playerId) == null)
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "采集玩家不存在。");
            // 必须在资源和完成标记写入前导入旧快照，否则最后一名玩家会在导入时
            // 自动走完收尾，随后 CompleteResourceCollection 再查节点就会错误拒绝。
            var prepared = EnsureLegacyMainlineIfNeeded(state);
            if (!prepared.IsValid) return prepared;
            var node = GetActiveNode(state);
            return node != null && node.NodeTypeId == RoundMainlineNodeTypeIds.Collection
                ? ValidationResult.Success
                : ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前不是采集主链节点。");
        }

        public ValidationResult CompleteResourceCollection(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            // 新共同请求只能由真实最终答案结束。旧完成标记不能越过请求与子树。
            if (state.Phase == GamePhase.ResourceCollection && HasOpenCollectionRouteSelection(state))
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired,
                    "请通过当前采集航道与资源选择请求提交采集，不能使用旧完成标记结束。");
            }

            var validation = EnsureLegacyMainlineIfNeeded(state);
            if (!validation.IsValid)
            {
                return validation;
            }

            var node = GetActiveNode(state);
            if (node == null || node.NodeTypeId != RoundMainlineNodeTypeIds.Collection)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前不是采集主链节点。");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "采集玩家不存在。");
            }

            if (!player.HasCollectedResourcesThisRound)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "采集完成标记尚未提交。");
            }

            for (var i = 0; i < state.EffectRuntime.PlayerOrderSnapshot.Count; i++)
            {
                var collectedPlayer = state.FindPlayer(state.EffectRuntime.PlayerOrderSnapshot[i]);
                if (collectedPlayer != null && collectedPlayer.HasCollectedResourcesThisRound)
                {
                    var taskValidation = CompleteCollectionTask(state, node, collectedPlayer.PlayerId);
                    if (!taskValidation.IsValid) return taskValidation;
                    AddCompletedPlayer(node, collectedPlayer.PlayerId);
                }
            }
            if (AllPlayersCompleted(node, state.EffectRuntime.PlayerOrderSnapshot))
            {
                node.CompletionRequested = true;
            }
            else
            {
                node.CompletionInteractionOrdinal += 1;
            }

            // 采集是开放顺序窗口。未收齐前不应让主链创建“完成确认”交互，
            // 否则通用 AnswerInteraction 会在仍有玩家未采集时提前推进到收尾。
            if (!node.CompletionRequested)
            {
                projector.Project(state);
                return ValidationResult.Success;
            }

            return ResolveManualCompletion(state, node);
        }

        private ValidationResult CompleteCollectionTask(
            GameState state,
            MainlineNodeRuntimeState node,
            int playerId)
        {
            if (state == null || state.EffectRuntime == null || node == null ||
                state.EffectRuntime.InteractionRequests == null ||
                state.EffectRuntime.Blockers == null)
            {
                return ValidationResult.Success;
            }

            for (var i = 0; i < state.EffectRuntime.InteractionRequests.Count; i++)
            {
                var request = state.EffectRuntime.InteractionRequests[i];
                if (request == null || request.Status != "open" ||
                    request.OwnerEffectId != node.ExecutionEffectId ||
                    request.InteractionTypeId != "collection.task" ||
                    request.AnsweringPlayerId != playerId)
                {
                    continue;
                }

                string diagnostic;
                // 采集是同一条开放顺序窗口内的内部任务提交；前一个玩家的
                // task 已推进运行时版本，因此后续尚未回答的 task 必须随当前
                // 权威版本重基线，不能把同一窗口误判为过期交互。
                request.StateRevision = state.EffectRuntime.StateRevision;
                var executor = CreateExecutor(state);
                if (!executor.TrySubmitInteraction(
                        request.RequestId,
                        playerId,
                        NormalizedValue.CreateBoolean(true),
                        out diagnostic))
                {
                    return ValidationResult.Failure(CommandErrorCode.InvalidTarget, diagnostic);
                }
                break;
            }

            return ValidationResult.Success;
        }

        public void MarkMainActionComplete(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            mainActionBudgetService.SpendCompletedMainAction(state, playerId);
        }

        public ValidationResult CompleteMainAction(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            MarkMainActionComplete(state, playerId);
            return EndCurrentPlayerWindow(state, playerId);
        }

        public ValidationResult EndCurrentPlayerWindow(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var validation = EnsureLegacyMainlineIfNeeded(state);
            if (!validation.IsValid)
            {
                return validation;
            }

            var node = GetActiveNode(state);
            if (node == null)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前没有可结束的回合主链节点。");
            }

            if (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow)
            {
                return EndActionWindow(state, node, playerId);
            }

            if (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow)
            {
                return EndCleanupWindow(state, node, playerId);
            }

            return ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前主链节点不是玩家窗口。");
        }

        public bool AllPlayersCollectedResources(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Players == null || state.Players.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                if (state.Players[i] == null || !state.Players[i].HasCollectedResourcesThisRound)
                {
                    return false;
                }
            }

            return true;
        }

        public ValidationResult AdvanceResourceCollectionToCleanup(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var validation = EnsureLegacyMainlineIfNeeded(state);
            if (!validation.IsValid)
            {
                return validation;
            }

            var node = GetActiveNode(state);
            if (node == null || node.NodeTypeId != RoundMainlineNodeTypeIds.Collection)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前不是采集主链节点。");
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                if (state.Players[i] != null)
                {
                    AddCompletedPlayer(node, state.Players[i].PlayerId);
                }
            }

            node.CompletionRequested = true;
            return ResolveManualCompletion(state, node);
        }

        public void ResetActionFlags(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                if (state.Players[i] != null)
                {
                    mainActionBudgetService.ResetForNewActionTurn(state, state.Players[i].PlayerId);
                }
            }
        }

        private ValidationResult CreateAndRunRound(
            GameState state,
            int roundNumber,
            int startPlayerId,
            bool legacyImport,
            bool includeEntrance = false)
        {
            if (state.EffectRuntime == null)
            {
                state.EffectRuntime = new EffectRuntimeState();
            }

            if (!string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId))
            {
                return Advance(state);
            }

            if (roundNumber <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "回合编号必须为正数。");
            }

            if (roundNumber > state.EffectRuntime.CurrentRoundNumber)
            {
                var transitionValidation = ValidateNextRoundCreation(state);
                if (!transitionValidation.IsValid)
                {
                    return transitionValidation;
                }
            }

            var order = ResolveOrder(state, startPlayerId);
            if (order.Count == 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "创建回合主链至少需要一名玩家。");
            }

            if (startPlayerId <= 0 || !ContainsPlayer(order, startPlayerId))
            {
                startPlayerId = order[0];
            }

            var roundExecutionId = chainBuilder.CreateRoundExecutionId(state.GameId, roundNumber, order);
            var chain = chainBuilder.Build(
                state.GameId,
                roundExecutionId,
                roundNumber,
                order,
                startPlayerId,
                includeEntrance,
                state.RuleConfiguration?.PlayerStageSchedulingMode ?? PlayerStageSchedulingMode.Concurrent);

            state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = chain.RoundNumber;
            state.EffectRuntime.CurrentRoundStartPlayerId = chain.StartPlayerId;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int>(chain.PlayerOrderSnapshot);
            state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.ActiveMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.MainNodes.AddRange(chain.Nodes);

            if (legacyImport)
            {
                ImportLegacyPosition(state, chain.Nodes);
            }

            projector.Project(state);
            return RunActiveMainline(state);
        }

        private ValidationResult EnsureLegacyMainlineIfNeeded(GameState state)
        {
            if (state.EffectRuntime == null)
            {
                state.EffectRuntime = new EffectRuntimeState();
            }

            if (!string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId))
            {
                return ValidationResult.Success;
            }

            if (state.Phase == GamePhase.Setup || state.Phase == GamePhase.Entrance ||
                state.Phase == GamePhase.FinalScoring || state.Phase == GamePhase.GameOver)
            {
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "当前阶段没有可推进的回合主链。");
            }

            var roundNumber = state.Round > 0 ? state.Round : 1;
            return CreateAndRunRound(state, roundNumber, state.StartPlayerId, true);
        }

        private ValidationResult RunActiveMainline(GameState state)
        {
            var result = RunActiveMainlineCore(state);
            if (result.IsValid) OperationWindowService.Synchronize(state);
            return result;
        }

        private ValidationResult RunActiveMainlineCore(GameState state)
        {
            for (var guard = 0; guard < 1024; guard++)
            {
                if (state.EffectRuntime.Status != EffectRuntimeStatus.Active)
                {
                    return ValidationResult.Failure(CommandErrorCode.WrongPhase, "回合主链已暂停于 Effect 故障。");
                }

                var mainNode = GetActiveNode(state);
                if (mainNode == null)
                {
                    return ValidationResult.Success;
                }

                // 命令层可能使用通用 EffectExecutor 提交交互；恢复/重入时主链观察者
                // 需要补偿性消费已经终止的执行节点，确保主节点不会停留在旧的 Blocked 状态。
                var persistedExecution = FindEffectNode(state, mainNode.ExecutionEffectId);
                if (persistedExecution != null &&
                    (persistedExecution.Status == EffectNodeStatus.Completed ||
                     persistedExecution.Status == EffectNodeStatus.Failed) &&
                    mainNode.Status != EffectNodeStatus.Completed &&
                    mainNode.Status != EffectNodeStatus.Failed)
                {
                    OnEffectTerminal(state, persistedExecution);
                    continue;
                }

                if (mainNode.Status == EffectNodeStatus.Created)
                {
                    mainNode.Status = EffectNodeStatus.Ready;
                }

                if (mainNode.Status == EffectNodeStatus.Ready)
                {
                    mainNode.Status = EffectNodeStatus.Running;
                    projector.Project(state);
                }

                if (mainNode.Status == EffectNodeStatus.Blocked)
                {
                    var blockedEffect = FindEffectNode(state, mainNode.ExecutionEffectId);
                    if (blockedEffect == null ||
                        (blockedEffect.Status != EffectNodeStatus.Ready &&
                         blockedEffect.Status != EffectNodeStatus.Running))
                    {
                        RefreshConcurrentInteractionRevisions(state);
                        projector.Project(state);
                        return ValidationResult.Success;
                    }

                    mainNode.Status = EffectNodeStatus.Running;
                    projector.Project(state);
                }

                if (mainNode.Status != EffectNodeStatus.Running &&
                    mainNode.Status != EffectNodeStatus.Blocked)
                {
                    return ValidationResult.Failure(CommandErrorCode.WrongPhase, "活动主链节点状态无效。");
                }

                if (string.IsNullOrEmpty(mainNode.ExecutionEffectId))
                {
                    var executor = CreateExecutor(state);
                    string effectId;
                    if (!executor.TryCreateRoot(
                            CreateMainlineEffectSpec(state, mainNode),
                            mainNode.RoundNumber.ToString(CultureInfo.InvariantCulture),
                            mainNode.NodeId,
                            out effectId))
                    {
                        return ValidationResult.Failure(CommandErrorCode.WrongPhase, executor.LastDiagnostic);
                    }

                    mainNode.ExecutionEffectId = effectId;
                }

                var runExecutor = CreateExecutor(state);
                var report = runExecutor.RunUntilQuiescent();
                RefreshConcurrentInteractionRevisions(state);
                if (report.Faulted)
                {
                    return ValidationResult.Failure(CommandErrorCode.WrongPhase, report.FaultCode + "：" + state.EffectRuntime.LastFaultMessage);
                }
                if (PauseFailedConcurrentStageIfNeeded(state))
                    return ValidationResult.Failure(CommandErrorCode.WrongPhase, state.EffectRuntime.LastFaultMessage);

                var after = GetActiveNode(state);
                if (after == null)
                {
                    return ValidationResult.Success;
                }

                var afterEffect = FindEffectNode(state, after.ExecutionEffectId);
                if (after.NodeId == mainNode.NodeId &&
                    (report.WaitingForInput ||
                     (afterEffect != null && afterEffect.Status == EffectNodeStatus.Blocked)))
                {
                    after.Status = EffectNodeStatus.Blocked;
                    projector.Project(state);
                    return ValidationResult.Success;
                }

                if (after.NodeId == mainNode.NodeId &&
                    after.Status == EffectNodeStatus.Blocked)
                {
                    projector.Project(state);
                    return ValidationResult.Success;
                }

                if (after.NodeId == mainNode.NodeId &&
                    after.Status == EffectNodeStatus.Running)
                {
                    return ValidationResult.Failure(CommandErrorCode.WrongPhase, "主链 Effect 已停止但节点没有进入等待状态。");
                }
            }

            return ValidationResult.Failure(CommandErrorCode.WrongPhase, "回合主链连续推进超过保护上限。");
        }

        private ValidationResult EndActionWindow(
            GameState state,
            MainlineNodeRuntimeState node,
            int playerId)
        {
            if (node.PlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "只有当前主链玩家可以结束行动窗口。");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "玩家必须存在才能结束行动窗口。");
            }

            if (HasPendingChoiceOutsideMainlineCompletion(state, node))
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择效果。");
            }

            if (player.CompletedMainActionsThisTurn <= 0 && !player.ActedMainActionThisTurn)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "结束行动前必须完成主要行动，或明确放弃规则允许的预算。");
            }

            mainActionBudgetService.EndActionTurn(state, playerId);
            node.CompletionRequested = true;
            return ResolveManualCompletion(state, node);
        }

        private ValidationResult EndCleanupWindow(
            GameState state,
            MainlineNodeRuntimeState node,
            int playerId)
        {
            if (node.PlayerId != playerId)
            {
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "只有当前主链玩家可以结束收尾窗口。");
            }

            if (HasPendingChoiceOutsideMainlineCompletion(state, node))
            {
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择效果。");
            }
            // 收尾内容只允许从当前 player_cleanup_window 的生命周期 Event 挂载。
            // EndCleanupWindow 不再创建第二棵清理树，也不负责回合级复位；空收尾会
            // 在主链执行器下一次推进时自动完成。
            return Advance(state);
        }

        private void ResetRoundScopedState(GameState state)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player == null)
                {
                    continue;
                }

                mainActionBudgetService.ResetForNewActionTurn(state, player.PlayerId);
                player.HasMovedCityThisRound = false;
                player.HasCollectedResourcesThisRound = false;
                player.ResourceCollectionStartGoldVoucher = -1;
            }
        }

        private void PrepareResourceCollection(GameState state)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player == null)
                {
                    continue;
                }

                player.HasCollectedResourcesThisRound = false;
                player.ResourceCollectionStartGoldVoucher = player.Resources.GoldVoucher;
            }
        }

        private ValidationResult ResolveManualCompletion(GameState state, MainlineNodeRuntimeState node)
        {
            projector.Project(state);
            var executionEffect = FindEffectNode(state, node.ExecutionEffectId);
            if (executionEffect == null)
            {
                return RunActiveMainline(state);
            }

            var request = node.CompletionRequested
                ? FindOpenCompletionRequest(state, executionEffect.EffectId)
                : null;
            if (request != null)
            {
                // 收尾阶段可能先执行角色/城市样式清理 Effect，导致主链完成交互的
                // 投影版本落后于当前运行时；这是同一条权威命令内的内部提交，刷新
                // 请求版本后再走通用回答入口即可保持乐观并发校验有效。
                request.StateRevision = state.EffectRuntime.StateRevision;
                string diagnostic;
                var executor = CreateExecutor(state);
                if (!executor.TrySubmitInteraction(
                        request.RequestId,
                        request.AnsweringPlayerId >= 0 ? request.AnsweringPlayerId : ResolveAnsweringPlayer(node),
                        NormalizedValue.CreateBoolean(true),
                        out diagnostic))
                {
                    return ValidationResult.Failure(CommandErrorCode.WrongPhase, diagnostic);
                }
            }

            return RunActiveMainline(state);
        }

        private static bool HasPendingChoiceOutsideMainlineCompletion(
            GameState state,
            MainlineNodeRuntimeState node)
        {
            if (state == null)
            {
                return false;
            }

            if ((state.PendingChoice != null && state.PendingChoice.IsValid()) ||
                (state.PendingCardSession != null && state.PendingCardSession.IsValid()) ||
                (state.PendingCharacterEffect != null && state.PendingCharacterEffect.IsValid()) ||
                (state.PendingSpecialAction != null && state.PendingSpecialAction.IsValid(state)))
            {
                return true;
            }

            if (state.EffectRuntime == null || state.EffectRuntime.InteractionRequests == null)
            {
                return false;
            }

            for (var i = 0; i < state.EffectRuntime.InteractionRequests.Count; i++)
            {
                var request = state.EffectRuntime.InteractionRequests[i];
                if (request != null && request.Status == "open" &&
                    (node == null || request.OwnerEffectId != node.ExecutionEffectId) &&
                    EffectTreeExecutor.BlocksCommand(state, request, state.EffectRuntime.CurrentOperationContext))
                {
                    return true;
                }
            }

            return false;
        }

        private int ResolveAnsweringPlayer(MainlineNodeRuntimeState node)
        {
            return node.PlayerId > 0 ? node.PlayerId : node.ActiveTaskPlayerId;
        }

        private InteractionRequest FindOpenCompletionRequest(GameState state, string ownerEffectId)
        {
            for (var i = state.EffectRuntime.InteractionRequests.Count - 1; i >= 0; i--)
            {
                var request = state.EffectRuntime.InteractionRequests[i];
                if (request != null && request.OwnerEffectId == ownerEffectId &&
                    request.Status == "open" &&
                    request.InteractionTypeId.StartsWith(MainlineCompletionInteractionTypeId, StringComparison.Ordinal))
                {
                    return request;
                }
            }

            return null;
        }

        private EffectTreeExecutor CreateExecutor(GameState state)
        {
            return new EffectExecutor(
                state,
                effectRegistry,
                effectRuntimeLimits,
                OnEffectTerminal);
        }

        public EffectExecutor CreateCommandExecutor(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return new EffectExecutor(
                state,
                effectRegistry,
                effectRuntimeLimits,
                OnEffectTerminal);
        }

        private void EnsureMainlineRegistration()
        {
            CharacterCoverEffectExecutor.Register(effectRegistry);
            CharacterAbilityEffectExecutor.Register(effectRegistry);
            CityStyleSpecialActionEffectExecutor.RegisterCleanup(effectRegistry);
            GenericLuaEffectExecutor.Register(effectRegistry);
            ExpansionSetupFlow.Register(effectRegistry);
            if (!effectRegistry.TryGet(PlayerEntranceTaskTypeId, out _))
                effectRegistry.Register(PlayerEntranceTaskTypeId, ExecuteEntrancePlayerTask, EffectExecutorKind.IntrinsicFlow);
            if (!effectRegistry.TryGet(PlayerCharacterRecyclingTypeId, out _)) effectRegistry.Register(PlayerCharacterRecyclingTypeId, context =>
            {
                var player = context.State.FindPlayer(context.Node.PlayerId);
                var recycled = InfectedRoundRules.RecycleCharacters(context, player.PlayerId);
                if (recycled.PendingOutcome != EffectPendingOutcome.Completed) return recycled;
                player = context.State.FindPlayer(context.Node.PlayerId);
                player.UsedCharacterThisRound = false; player.UsedCharacterThisTurn = false; player.CharacterCardLockedThisTurn = false;
                return recycled;
            });
            if (!effectRegistry.TryGet(MainlineEffectTypeId, out _))
            {
                effectRegistry.Register(new EffectRegistration(
                    MainlineEffectTypeId,
                    ExecuteMainlineStage,
                    EffectExecutorKind.IntrinsicFlow));
            }
        }

        private EffectSpec CreateMainlineEffectSpec(GameState state, MainlineNodeRuntimeState node)
        {
            var spec = EffectSpec.Create(
                MainlineEffectTypeId,
                CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry
                    {
                        Name = "mainNodeId",
                        Value = NormalizedValue.CreateString(node.NodeId)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "nodeTypeId",
                        Value = NormalizedValue.CreateString(node.NodeTypeId)
                    }
                }),
                node.PlayerId);
            if (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup)
                RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "setupProtocolVersion", NormalizedValue.CreateInteger(2));
            if (node.NodeTypeId == RoundMainlineNodeTypeIds.CharacterCover &&
                state.RuleConfiguration?.PlayerStageSchedulingMode != PlayerStageSchedulingMode.Sequential)
                RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "coverTaskSchedulingVersion", NormalizedValue.CreateInteger(1));
            if (node.NodeTypeId == RoundMainlineNodeTypeIds.Collection &&
                state.RuleConfiguration?.PlayerStageSchedulingMode == PlayerStageSchedulingMode.Sequential)
                RouteSelectionSessionAuthority.Set(spec.NormalizedArguments, "collectionTaskSchedulingVersion", NormalizedValue.CreateInteger(2));
            return spec;
        }

        private EffectStepResult ExecuteMainlineStage(EffectExecutionContext context)
        {
            CharacterLifecycleService.ResumeDeferredDependencies(context);
            string mainNodeId = GetStringProperty(context.Node.NormalizedArguments, "mainNodeId");
            var mainNode = context.State.EffectRuntime.MainNodes.Find(candidate =>
                candidate != null && candidate.NodeId == mainNodeId);
            if (mainNode == null)
            {
                throw new InvalidOperationException("主链节点不存在：" + mainNodeId);
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup)
                return ExecuteEntranceSetup(context, mainNode);

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance)
            {
                if (context.Node.FlowStage == string.Empty && context.State.SetupRecipe?.EnterpriseEntrance == true && mainNode.PlayerId == context.State.EffectRuntime.PlayerOrderSnapshot[0])
                    return EffectStepResult.Continue("preparing_enterprises").AddChild(ExpansionSetupFlow.PreparationSpec(mainNode.PlayerId, false));
                if (context.Node.FlowStage == string.Empty || context.Node.FlowStage == "preparing_enterprises")
                {
                    if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("等待企业入场。");
                    foreach (var child in context.ChildNodes) if (child.Status == EffectNodeStatus.Failed) throw new InvalidOperationException(child.FailureReason);
                    return EffectStepResult.Continue("entering").AddChild(PlayerEntranceEffectExecutor.Create(mainNode.PlayerId));
                }
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("等待玩家入场及事件子树结算。");
                foreach (var child in context.ChildNodes)
                    if (child.Status == EffectNodeStatus.Failed) throw new InvalidOperationException("玩家入场子效果失败：" + child.FailureReason);
                if (context.Node.FlowStage == "entering" && context.State.SetupRecipe != null)
                    return EffectStepResult.Continue("retaining_characters").AddChild(ExpansionSetupFlow.InitialInfluenceSpec(mainNode.PlayerId)).AddChild(ExpansionSetupFlow.RetentionSpec(mainNode.PlayerId));
                mainNode.CompletionRequested = true;
                return EffectStepResult.Completed();
            }

            // 开局补偿在所有人的入场事件结算后、第一回合开始前统一发放。
            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.RoundStarted && mainNode.RoundNumber == 1 &&
                context.Node.FlowStage == string.Empty && context.State.EffectRuntime.MainNodes.Exists(node =>
                    node.RoundExecutionId == mainNode.RoundExecutionId && (node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance ||
                    node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup)))
                return EffectStepResult.Continue("initial_gold").AddChildren(
                    PlayerEntranceEffectExecutor.CreateInitialGoldEffects(context.State.EffectRuntime.PlayerOrderSnapshot));

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.RoundStarted && context.State.SetupRecipe != null)
            {
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("等待回合入口子树。");
                foreach (var child in context.ChildNodes) if (child.Status == EffectNodeStatus.Failed) throw new InvalidOperationException(child.FailureReason);
                if (context.Node.FlowStage == string.Empty || context.Node.FlowStage == "initial_gold")
                {
                    if (mainNode.RoundNumber == 1 && context.State.Players.Count == 2 && context.Node.FlowStage == "initial_gold")
                        context.State.FindPlayer(context.State.EffectRuntime.PlayerOrderSnapshot[1]).Score += 3;
                    var opened = EffectStepResult.Continue("round_opened");
                    AppendLifecycleEvents(context.State, context.Node, mainNode, opened);
                    return opened;
                }
                if (context.Node.FlowStage == "round_opened")
                    return EffectStepResult.Continue("preparing_cover").AddChild(ExpansionSetupFlow.RoundPreparationSpec(context.State.EffectRuntime.PlayerOrderSnapshot[0]));
                if (context.Node.FlowStage != "preparing_cover") throw new InvalidOperationException("round_started_stage_invalid");
                return EffectStepResult.Completed();
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && context.Node.FlowStage == string.Empty &&
                mainNode.PlayerId == context.State.EffectRuntime.PlayerOrderSnapshot[0] && !mainNode.PublishedEventKeys.Contains("ActionRoundStarted"))
            {
                mainNode.PublishedEventKeys.Add("ActionRoundStarted");
                return EffectStepResult.Continue("action_round_started").AddEvent(CreateLifecycleEvent(context.State, context.Node, mainNode, "ActionRoundStarted", "action_round"));
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.CharacterCover)
            {
                if (context.Node.ChildEffectIds == null || context.Node.ChildEffectIds.Count == 0)
                {
                    var created = EffectStepResult.Continue("cover_tasks_created");
                    var tasks = CreateCharacterCoverTaskSpecs(context.State, mainNode);
                    if (IsParallelCoverExecution(context.Node))
                    {
                        mainNode.ActiveTaskPlayerId = -1;
                        return created.AddIndependentChildren("character_cover_players", tasks);
                    }
                    return created.AddChildren(tasks);
                }

                if (IsParallelCoverExecution(context.Node))
                {
                    foreach (var child in context.ChildNodes)
                        if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                            return EffectStepResult.Failed(child.FailureReason);
                    if (!AreCharacterCoverTasksTerminal(context.State, context.Node))
                        return EffectStepResult.NoProgress("等待全部正常盖放及必要响应。");
                    if (!AllPlayersCompleted(mainNode, context.State.EffectRuntime.PlayerOrderSnapshot))
                        return EffectStepResult.Failed("cover_completion_receipt_missing");
                    mainNode.CompletionRequested = true;
                }

                if (!AreCharacterCoverTasksTerminal(context.State, context.Node))
                {
                    return EffectStepResult.NoProgress("统一盖放子任务尚未全部进入终态。");
                }

                // 子任务的合法失败只释放自己的 blocker。只要仍有玩家未完成盖放，
                // 父节点保持在盖放阶段并等待显式处理，不把失败升级为主链 fault，
                // 也不伪造 CharacterCoverCompleted。
                if (!AllPlayersCompleted(mainNode, context.State.EffectRuntime.PlayerOrderSnapshot))
                {
                    var unresolved = new EffectStepResult();
                    unresolved.AddInteraction(new EffectInteractionSpec
                    {
                        InteractionTypeId = MainlineCompletionInteractionTypeId + ":cover-legal-failure",
                        AnsweringPlayerId = FindNextIncompletePlayer(
                            context.State,
                            mainNode,
                            -1),
                        PromptKey = "character.cover.resolve_failure",
                        AnswerSchema = "boolean_accept",
                        MinSelections = 0,
                        MaxSelections = 0
                    });
                    unresolved.WithFlowStage("awaiting_cover_failure_resolution");
                    return unresolved;
                }

                var completedCover = EffectStepResult.Completed();
                AppendLifecycleEvents(context.State, context.Node, mainNode, completedCover);
                return completedCover;
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow)
            {
                if (context.Node.FlowStage == string.Empty)
                {
                    context.Node.SettlementWindow = SettlementEntryService.CreateWindow(context.State, context.Node,
                        SettlementEntryService.PlayerCleanup, mainNode.PlayerId, "",
                        CreateLifecycleEvent(context.State, context.Node, mainNode, "PlayerCleanupStarted", "").EventId);
                    context.State.EffectRuntime.ActiveSettlementWindowId = context.Node.SettlementWindow.Context.Id;
                    var started = EffectStepResult.Continue("cleanup_started");
                    AppendLifecycleEvents(context.State, context.Node, mainNode, started);
                    return started;
                }

                if (context.Node.SettlementWindow != null)
                {
                    var window = context.Node.SettlementWindow;
                    foreach (var child in context.ChildNodes) SettlementEntryService.ObserveTerminal(window, child);
                    if (!window.IsClosed && window.Entries.Count > 0 &&
                        window.Entries.Any(e => e.SettlementState == "available" || e.SettlementState == "executing"))
                        return EffectStepResult.NoProgress("等待玩家选择已挂收尾段或结束当前合法收尾窗口。");
                    if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("玩家收尾子 Effect 尚未结束。");
                    if (!window.IsClosed && !SettlementEntryService.TryCloseCleanup(window, out _, out string closeReason))
                        return EffectStepResult.NoProgress(closeReason);
                    if (context.State.EffectRuntime.ActiveSettlementWindowId == window.Context.Id)
                        context.State.EffectRuntime.ActiveSettlementWindowId = "";
                    mainNode.CompletionRequested = true;
                    var completed = EffectStepResult.Completed();
                    AppendLifecycleEvents(context.State, context.Node, mainNode, completed);
                    return completed;
                }

                // 没有类型化窗口的旧在途版本仍消费原保存记录；新 Event 不进入此分支。
                if (HasNonTerminalChild(context))
                {
                    return EffectStepResult.NoProgress("玩家收尾子 Effect 尚未结束。");
                }

                // 可选来源只登记待执行正文；强制子式全部结束后，才开放统一底栏窗口。
                // 插入正文时内核将阶段改回 cleanup_started，避免消费上一次窗口的旧回答。
                if (context.Node.FlowStage != "cleanup_settlement" && CharacterContentEffectExecutor.HasPendingCleanupOffers(context.State, context.Node))
                {
                    return EffectStepResult.Continue("cleanup_settlement").AddInteraction(new EffectInteractionSpec {
                        InteractionTypeId = CharacterContentEffectExecutor.CleanupInteractionTypeId,
                        PromptKey = CharacterContentEffectExecutor.CleanupInteractionTypeId,
                        AnsweringPlayerId = mainNode.PlayerId, Visibility = "owner", AnswerSchema = "boolean_accept",
                        MinSelections = 0, MaxSelections = 0, AllowDecline = true
                    });
                }
                if (context.Node.FlowStage == "cleanup_settlement" && context.GetLatestInteractionAnswer()?.Kind != NormalizedValueKind.Boolean)
                    return EffectStepResult.Failed("cleanup_window_answer_invalid");

                mainNode.CompletionRequested = true;
                var cleanupCompleted = EffectStepResult.Completed();
                AppendLifecycleEvents(context.State, context.Node, mainNode, cleanupCompleted);
                return cleanupCompleted;
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.Collection)
            {
                if (context.Node.FlowStage == string.Empty)
                {
                    // 开始响应与玩家任务必须跨持久化阶段，不能依赖同一结果中的装配顺序。
                    CaptureCollectionStartBudgets(context.State, context.Node);
                    var collectionStarted = EffectStepResult.Continue("collection_started");
                    AppendLifecycleEvents(context.State, context.Node, mainNode, collectionStarted);
                    return collectionStarted;
                }

                foreach (var child in context.ChildNodes)
                {
                    if (child.Status == EffectNodeStatus.Failed)
                        return EffectStepResult.Failed(child.FailureReason);
                }
                if (HasNonTerminalChild(context))
                    return EffectStepResult.NoProgress("等待采集开始响应或玩家采集子树结束。");

                if (context.Node.FlowStage == "collection_started")
                {
                    var collectionOpen = EffectStepResult.Continue("collection_open");
                    AddCollectionTasks(context.State, context.Node, mainNode, collectionOpen);
                    return collectionOpen;
                }

                // 老 collection_open/awaiting_completion 继续消费已保存的任务与请求，
                // 不重新发布开始事件，不重建选择节点或覆盖原候选/开始预算。

                IList<int> collectionOrder = context.State.EffectRuntime.PlayerOrderSnapshot;
                if (collectionOrder != null)
                {
                    for (var i = 0; i < collectionOrder.Count; i++)
                    {
                        var player = context.State.FindPlayer(collectionOrder[i]);
                        if (player != null && player.HasCollectedResourcesThisRound)
                        {
                            AddCompletedPlayer(mainNode, player.PlayerId);
                        }
                    }
                }

                if (!mainNode.CompletionRequested &&
                    AllPlayersCompleted(mainNode, collectionOrder))
                {
                    mainNode.CompletionRequested = true;
                }

                if (mainNode.CompletionRequested)
                {
                    var collectionCompleted = EffectStepResult.Completed();
                    AppendLifecycleEvents(context.State, context.Node, mainNode, collectionCompleted);
                    return collectionCompleted;
                }

                return EffectStepResult.NoProgress("采集任务尚未全部完成。");
            }

            if (!IsManualNode(mainNode.NodeTypeId))
            {
                if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded &&
                    !mainNode.CompletionRequested)
                {
                    if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(context.State))
                    {
                        if (context.Node.FlowStage == string.Empty)
                        {
                            var recycling = EffectStepResult.Continue("round_cleanup");
                            foreach (int player in context.State.EffectRuntime.PlayerOrderSnapshot)
                            {
                                recycling.AddChild(EffectSpec.Create(PlayerCharacterRecyclingTypeId, playerId: player));
                                recycling.AddChild(CityStyleSpecialActionEffectSpecFactory.Cleanup(player));
                            }
                            return recycling;
                        }
                        if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("等待真实角色与样式收尾。");
                        foreach (var child in context.ChildNodes) if (child.Status == EffectNodeStatus.Failed) throw new InvalidOperationException(child.FailureReason);
                    }
                    else
                    {
                        characterCardService.CleanupRound(context.State);
                        specialActionLifecycleService.CleanupRound(context.State);
                    }
                    var federalCouncilStartPlayerId = FederalCouncilEffectService.ConsumeLatestBuilder(context.State);
                    if (context.State.EffectRuntime.PendingNextRoundStartPlayerId < 0)
                        context.State.EffectRuntime.PendingNextRoundStartPlayerId = federalCouncilStartPlayerId > 0
                        ? federalCouncilStartPlayerId
                        : GetNextStartPlayerId(context.State);
                    ResetRoundScopedState(context.State);
                    mainNode.CompletionRequested = true;
                }

                if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded && mainNode.RoundNumber >= context.State.MaxRounds &&
                    CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.FinalizeGame }))
                    return CharacterLifecycleService.DeferredDependencyWait();

                var completed = EffectStepResult.Completed();
                AppendLifecycleEvents(context.State, context.Node, mainNode, completed);
                if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded &&
                    mainNode.RoundNumber >= context.State.MaxRounds &&
                    !mainNode.PublishedEventKeys.Contains("FinalScoringStarted"))
                {
                    mainNode.PublishedEventKeys.Add("FinalScoringStarted");
                    completed.AddEvent(CreateLifecycleEvent(
                        context.State,
                        context.Node,
                        mainNode,
                        "FinalScoringStarted",
                        "final"));
                }
                return completed;
            }

            // 通用 AnswerInteraction 入口回答主链完成交互后，主链 Effect 的阶段已经
            // 持久化为 awaiting_completion；这里把该回答转换为主链的完成请求，避免
            // 恢复时再次生成同一个 round.mainline.complete 交互。
            if (context.Node.FlowStage == "awaiting_completion")
            {
                mainNode.CompletionRequested = true;
            }

            if (mainNode.CompletionRequested)
            {
                var completed = EffectStepResult.Completed();
                AppendLifecycleEvents(context.State, context.Node, mainNode, completed);
                return completed;
            }

            var result = new EffectStepResult();
            AppendLifecycleEvents(context.State, context.Node, mainNode, result);

            result.AddInteraction(new EffectInteractionSpec
            {
                InteractionTypeId = MainlineCompletionInteractionTypeId + ":" +
                                    mainNode.CompletionInteractionOrdinal.ToString(CultureInfo.InvariantCulture),
                AnsweringPlayerId = ResolveInteractionPlayer(mainNode),
                PromptKey = "round.mainline.complete",
                AnswerSchema = "boolean_accept",
                MinSelections = 0,
                MaxSelections = 0
            });
            result.WithFlowStage("awaiting_completion");
            return result;
        }

        private static int ResolveInteractionPlayer(MainlineNodeRuntimeState node)
        {
            if (node.NodeTypeId == RoundMainlineNodeTypeIds.Collection)
            {
                return -1;
            }

            return node.PlayerId > 0 ? node.PlayerId : node.ActiveTaskPlayerId;
        }

        private void AppendLifecycleEvents(
            GameState state,
            EffectNodeRuntimeState executionEffect,
            MainlineNodeRuntimeState node,
            EffectStepResult result)
        {
            var eventType = GetLifecycleEventType(node.NodeTypeId);
            if (!string.IsNullOrEmpty(eventType) &&
                !node.PublishedEventKeys.Contains(eventType))
            {
                node.PublishedEventKeys.Add(eventType);
                result.AddEvent(CreateLifecycleEvent(state, executionEffect, node, eventType, string.Empty));
            }

            string completedEventType = GetCompletedLifecycleEventType(node.NodeTypeId);
            if (node.CompletionRequested && !string.IsNullOrEmpty(completedEventType) &&
                !node.PublishedEventKeys.Contains(completedEventType))
            {
                node.PublishedEventKeys.Add(completedEventType);
                result.AddEvent(CreateLifecycleEvent(state, executionEffect, node, completedEventType, "completed"));
            }

            if (node.NodeTypeId != RoundMainlineNodeTypeIds.CharacterCover)
            {
                return;
            }

            var order = state.EffectRuntime.PlayerOrderSnapshot;
            var completedKey = "CharacterCoverCompleted";
            if (AreCharacterCoverTasksTerminal(state, executionEffect) &&
                AllPlayersCompleted(node, order) && !node.PublishedEventKeys.Contains(completedKey))
            {
                node.PublishedEventKeys.Add(completedKey);
                result.AddEvent(CreateCharacterCoverCompletedEvent(state, executionEffect, node));
            }
        }

        private static List<EffectSpec> CreateCharacterCoverTaskSpecs(
            GameState state,
            MainlineNodeRuntimeState node)
        {
            var specs = new List<EffectSpec>();
            IList<int> order = state.EffectRuntime.PlayerOrderSnapshot;
            for (int i = 0; i < order.Count; i++)
            {
                int playerId = order[i];
                var spec = EffectSpec.Create(
                    CharacterCoverEffectExecutor.EffectTypeId,
                    CreateObject(new List<NormalizedValueEntry>
                    {
                        new NormalizedValueEntry
                        {
                            Name = "coverIndex",
                            Value = NormalizedValue.CreateInteger(i)
                        },
                        new NormalizedValueEntry
                        {
                            Name = "mainNodeId",
                            Value = NormalizedValue.CreateString(node.NodeId)
                        },
                        new NormalizedValueEntry
                        {
                            Name = "playerId",
                            Value = NormalizedValue.CreateInteger(playerId)
                        }
                    }),
                    playerId);
                spec.SourceId = node.NodeId;
                spec.StableKey = "cover-task:" + playerId.ToString(CultureInfo.InvariantCulture);
                spec.Visibility = GameStateVisibilityPolicy.Owner;
                specs.Add(spec);
            }

            return specs;
        }

        private static bool AreCharacterCoverTasksTerminal(
            GameState state,
            EffectNodeRuntimeState executionEffect)
        {
            if (executionEffect == null || executionEffect.ChildEffectIds == null ||
                executionEffect.ChildEffectIds.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < executionEffect.ChildEffectIds.Count; i++)
            {
                EffectNodeRuntimeState child = state.EffectRuntime.EffectNodes.Find(
                    candidate => candidate != null && candidate.EffectId == executionEffect.ChildEffectIds[i]);
                if (child == null || child.EffectTypeId != CharacterCoverEffectExecutor.EffectTypeId ||
                    (child.Status != EffectNodeStatus.Completed &&
                        (IsParallelCoverExecution(executionEffect) || child.Status != EffectNodeStatus.Failed)))
                {
                    return false;
                }
            }

            return true;
        }

        private static EffectEventRequest CreateCharacterCoverCompletedEvent(
            GameState state,
            EffectNodeRuntimeState executionEffect,
            MainlineNodeRuntimeState node)
        {
            return new EffectEventRequest
            {
                EventId = StableIdFactory.Create(
                    "event",
                    state.GameId ?? string.Empty,
                    state.EffectRuntime.CurrentRoundExecutionId ?? string.Empty,
                    "CharacterCoverCompleted",
                    node.NodeId),
                EventType = "CharacterCoverCompleted",
                SourceEffectId = executionEffect.EffectId,
                OwnerNodeId = executionEffect.EffectId,
                PlayerId = -1,
                Visibility = GameStateVisibilityPolicy.Public,
                ResponseKind = RuleEventResponseKind.Effects,
                SemanticKey = "completed",
                Payload = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry
                    {
                        Name = "coverNodeId",
                        Value = NormalizedValue.CreateString(node.NodeId)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "coveredPlayerIds",
                        Value = CreatePlayerOrder(state.EffectRuntime.PlayerOrderSnapshot)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "roundExecutionId",
                        Value = NormalizedValue.CreateString(state.EffectRuntime.CurrentRoundExecutionId ?? string.Empty)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "roundNumber",
                        Value = NormalizedValue.CreateInteger(node.RoundNumber)
                    }
                })
            };
        }

    private static EffectEventRequest CreateLifecycleEvent(
            GameState state,
            EffectNodeRuntimeState executionEffect,
            MainlineNodeRuntimeState node,
            string eventType,
            string key)
        {
            var eventId = StableIdFactory.Create(
                "event",
                state.GameId ?? string.Empty,
                state.EffectRuntime.CurrentRoundExecutionId ?? string.Empty,
                eventType,
                node.NodeId,
                key ?? string.Empty);
            var payloadEntries = new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "roundExecutionId",
                    Value = NormalizedValue.CreateString(state.EffectRuntime.CurrentRoundExecutionId ?? string.Empty)
                },
                new NormalizedValueEntry
                {
                    Name = "roundNumber",
                    Value = NormalizedValue.CreateInteger(node.RoundNumber)
                },
                new NormalizedValueEntry
                {
                    Name = "nodeId",
                    Value = NormalizedValue.CreateString(node.NodeId)
                },
                new NormalizedValueEntry
                {
                    Name = "playerId",
                    Value = NormalizedValue.CreateInteger(node.PlayerId)
                },
                new NormalizedValueEntry
                {
                    Name = "playerOrder",
                    Value = CreatePlayerOrder(state.EffectRuntime.PlayerOrderSnapshot)
                },
                new NormalizedValueEntry
                {
                    Name = "startPlayerId",
                    Value = NormalizedValue.CreateStableReference(
                        "player",
                        "p" + state.EffectRuntime.CurrentRoundStartPlayerId.ToString(CultureInfo.InvariantCulture))
                }
            };
            if (eventType == CityStyleSpecialActionEventTypeIds.CleanupStarted)
            {
                var markerIds = new List<NormalizedValue>();
                PlayerState player = state.FindPlayer(node.PlayerId);
                if (player != null && player.DeclaredCityStyles != null)
                {
                    for (int i = 0; i < player.DeclaredCityStyles.Count; i++)
                    {
                        CityStyleDeclarationState marker = player.DeclaredCityStyles[i];
                        if (marker != null && !string.IsNullOrEmpty(marker.InfluenceMarkerId))
                            markerIds.Add(NormalizedValue.CreateString(marker.InfluenceMarkerId));
                    }
                }
                payloadEntries.Add(new NormalizedValueEntry
                {
                    Name = "markerIds",
                    Value = NormalizedValue.CreateArray(markerIds)
                });
            }
            return new EffectEventRequest
            {
                EventId = eventId,
                EventType = eventType,
                SourceEffectId = executionEffect.EffectId,
                OwnerNodeId = executionEffect.EffectId,
                PlayerId = node.PlayerId,
                ResponseKind = RuleEventResponseKind.Effects,
                SemanticKey = key ?? string.Empty,
                Payload = CreateObject(payloadEntries)
            };
        }

        private static NormalizedValue CreatePlayerOrder(IList<int> playerOrder)
        {
            var values = new List<NormalizedValue>();
            if (playerOrder != null)
            {
                for (var i = 0; i < playerOrder.Count; i++)
                {
                    values.Add(NormalizedValue.CreateStableReference(
                        "player",
                        "p" + playerOrder[i].ToString(CultureInfo.InvariantCulture)));
                }
            }

            return NormalizedValue.CreateArray(values);
        }

        private void OnEffectTerminal(GameState state, EffectNodeRuntimeState effectNode)
        {
            if (state == null || effectNode == null || state.EffectRuntime == null)
            {
                return;
            }

            if (effectNode.EffectTypeId != MainlineEffectTypeId)
            {
                var collection = RuntimeNode(state.EffectRuntime, effectNode.ParentEffectId);
                if (effectNode.Status == EffectNodeStatus.Completed && effectNode.EffectTypeId == RouteResourceSelectionEffectExecutor.TypeId &&
                    ItemTransferEffectExecutor.Number(effectNode.NormalizedArguments, "purpose", -1) == (int)RouteSelectionPurpose.NormalHarvest &&
                    IsSequentialCollectionExecution(collection) && IsActiveMainlineExecution(state.EffectRuntime, collection) &&
                    collection.ChildEffectIds.Contains(effectNode.EffectId))
                {
                    // 内核会在本次终态后继续下一位；先交接身份，下一请求才会冻结同一份事实。
                    // 等完整采集子树终态再交接，仍保留采集后必要响应所属玩家的控制权。
                    projector.Project(state);
                }
                return;
            }

            var mainNode = state.EffectRuntime.MainNodes.Find(candidate =>
                candidate != null && candidate.ExecutionEffectId == effectNode.EffectId);
            if (mainNode == null || state.EffectRuntime.ActiveMainNodeId != mainNode.NodeId)
            {
                return;
            }

            mainNode.Status = effectNode.Status == EffectNodeStatus.Failed
                ? EffectNodeStatus.Failed
                : EffectNodeStatus.Completed;
            if ((mainNode.NodeTypeId == RoundMainlineNodeTypeIds.Collection ||
                 mainNode.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup ||
                 IsParallelCoverExecution(effectNode)) &&
                effectNode.Status == EffectNodeStatus.Failed)
            {
                // 观察者仍处于内核正常终态提交中，不能提前改成 PausedFault。
                // Advance 在该次提交完成后用独立故障提交暂停，保留原失败原因。
                projector.Project(state);
                return;
            }
            var next = FindNode(state, mainNode.NextNodeId);
            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.RoundEnded)
            {
                if (mainNode.RoundNumber >= state.MaxRounds)
                {
                    state.EffectRuntime.ActiveMainNodeId = string.Empty;
                    projector.ProjectFinalScoring(state);
                    ResolveFinalScoring(state);
                    return;
                }

                var transitionValidation = ValidateNextRoundCreation(state);
                if (!transitionValidation.IsValid)
                {
                    state.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
                    state.EffectRuntime.LastFaultCode = "round_transition_blocked";
                    state.EffectRuntime.LastFaultMessage = transitionValidation.Reason;
                    projector.Project(state);
                    return;
                }

                CreateNextRoundOnWorkingState(state, mainNode.RoundNumber + 1);
                return;
            }

            if (next == null)
            {
                state.EffectRuntime.ActiveMainNodeId = string.Empty;
                projector.ProjectFinalScoring(state);
                ResolveFinalScoring(state);
                return;
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                next.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                mainNode.ActionRound != next.ActionRound)
            {
                ResetActionFlags(state);
            }

            if (next.NodeTypeId == RoundMainlineNodeTypeIds.Collection)
            {
                ResetActionFlags(state);
                PrepareResourceCollection(state);
            }

            next.Status = EffectNodeStatus.Ready;
            state.EffectRuntime.ActiveMainNodeId = next.NodeId;
            projector.Project(state);
        }

        private void ResolveFinalScoring(GameState state)
        {
            if (finalScoringService == null || state == null)
            {
                return;
            }

            FinalScoringResult result = finalScoringService.Resolve(state);
            if (result.Succeeded)
            {
                return;
            }

            state.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
            state.EffectRuntime.LastFaultCode = "final_scoring_failed";
            state.EffectRuntime.LastFaultMessage = result.Validation == null
                ? "最终计分失败。"
                : result.Validation.Reason;
        }

        private void CreateNextRoundOnWorkingState(GameState state, int roundNumber)
        {
            var transitionValidation = ValidateNextRoundCreation(state);
            if (!transitionValidation.IsValid)
            {
                state.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
                state.EffectRuntime.LastFaultCode = "round_transition_blocked";
                state.EffectRuntime.LastFaultMessage = transitionValidation.Reason;
                projector.Project(state);
                return;
            }

            var startPlayerId = state.EffectRuntime.PendingNextRoundStartPlayerId;
            var order = ResolveOrder(state, startPlayerId);
            if (order.Count == 0)
            {
                throw new InvalidOperationException("下一回合没有有效玩家顺序。");
            }

            if (startPlayerId <= 0 || !ContainsPlayer(order, startPlayerId))
            {
                startPlayerId = order[0];
            }

            var roundExecutionId = chainBuilder.CreateRoundExecutionId(state.GameId, roundNumber, order);
            var chain = chainBuilder.Build(
                state.GameId,
                roundExecutionId,
                roundNumber,
                order,
                startPlayerId);
            state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = chain.RoundNumber;
            state.EffectRuntime.CurrentRoundStartPlayerId = chain.StartPlayerId;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int>(chain.PlayerOrderSnapshot);
            state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.ActiveMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.PendingNextRoundStartPlayerId = -1;
            state.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            projector.Project(state);
        }

        private static bool IsTerminalEffectNode(EffectNodeStatus status)
        {
            return status == EffectNodeStatus.Completed ||
                   status == EffectNodeStatus.Failed ||
                   status == EffectNodeStatus.Faulted;
        }

        private static bool HasNonTerminalChild(EffectExecutionContext context)
        {
            if (context == null || context.Node == null || context.Node.ChildEffectIds == null)
            {
                return false;
            }

            for (var i = 0; i < context.Node.ChildEffectIds.Count; i++)
            {
                var child = context.State.EffectRuntime.EffectNodes.Find(candidate =>
                    candidate != null && candidate.EffectId == context.Node.ChildEffectIds[i]);
                if (child != null && !IsTerminalEffectNode(child.Status))
                {
                    return true;
                }
            }

            return false;
        }

        private void ImportLegacyPosition(GameState state, List<MainlineNodeRuntimeState> nodes)
        {
            var target = nodes[0];
            if (state.Phase == GamePhase.CharacterCover)
            {
                target = nodes[1];
            }
            else if (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2)
            {
                var actionRound = state.Phase == GamePhase.ActionRound2 ? 2 : 1;
                target = nodes.Find(candidate => candidate.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                                                 candidate.ActionRound == actionRound &&
                                                 candidate.PlayerId == state.CurrentPlayerId);
                if (target == null)
                {
                    target = nodes.Find(candidate => candidate.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                                                     candidate.ActionRound == actionRound);
                }
            }
            else if (state.Phase == GamePhase.ResourceCollection)
            {
                target = nodes.Find(candidate => candidate.NodeTypeId == RoundMainlineNodeTypeIds.Collection);
            }
            else if (state.Phase == GamePhase.Cleanup)
            {
                target = nodes.Find(candidate => candidate.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow &&
                                                 candidate.PlayerId == state.CurrentPlayerId);
                if (target == null)
                {
                    target = nodes.Find(candidate => candidate.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow);
                }
            }

            if (target == null)
            {
                target = nodes[0];
            }

            state.EffectRuntime.ActiveMainNodeId = target.NodeId;

            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].MainlineIndex < target.MainlineIndex)
                {
                    nodes[i].Status = EffectNodeStatus.Completed;
                }
                else if (nodes[i].NodeId == target.NodeId)
                {
                    nodes[i].Status = EffectNodeStatus.Ready;
                }
            }

            if (target.NodeTypeId == RoundMainlineNodeTypeIds.CharacterCover)
            {
                var order = state.EffectRuntime.PlayerOrderSnapshot;
                var projectedCurrentPlayerId = state.CurrentPlayerId;
                for (var i = 0; i < order.Count; i++)
                {
                    var player = state.FindPlayer(order[i]);
                    if (player != null && player.PlayerId != projectedCurrentPlayerId &&
                        !string.IsNullOrEmpty(player.CoveredCharacterCardId))
                    {
                        AddCompletedPlayer(target, player.PlayerId);
                    }
                }

                target.ActiveTaskPlayerId = projectedCurrentPlayerId > 0 &&
                                             !target.CompletedPlayerIds.Contains(projectedCurrentPlayerId)
                    ? projectedCurrentPlayerId
                    : FindNextIncompletePlayer(state, target, -1);
                target.CompletionRequested = AllPlayersCompleted(target, order);
            }
        }

        private List<int> ResolveOrder(GameState state, int startPlayerId)
        {
            return new List<int>(turnOrderService.GetTurnOrder(
                state,
                startPlayerId > 0 ? startPlayerId : state.StartPlayerId));
        }

        private int GetNextStartPlayerId(GameState state)
        {
            var order = new List<int>(state.EffectRuntime.PlayerOrderSnapshot);
            if (order.Count == 0)
            {
                order.AddRange(turnOrderService.GetTurnOrder(state));
            }

            if (order.Count == 0)
            {
                return state.StartPlayerId;
            }

            var currentIndex = order.IndexOf(state.EffectRuntime.CurrentRoundStartPlayerId);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            return order[(currentIndex + 1) % order.Count];
        }

        private MainlineNodeRuntimeState GetActiveNode(GameState state)
        {
            if (state.EffectRuntime == null || string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId))
            {
                return null;
            }

            return FindNode(state, state.EffectRuntime.ActiveMainNodeId);
        }

        private static MainlineNodeRuntimeState FindNode(GameState state, string nodeId)
        {
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.MainNodes == null)
            {
                return null;
            }

            return state.EffectRuntime.MainNodes.Find(candidate =>
                candidate != null && candidate.NodeId == (nodeId ?? string.Empty));
        }

        private static EffectNodeRuntimeState FindEffectNode(GameState state, string effectId)
        {
            if (state == null || state.EffectRuntime == null || string.IsNullOrEmpty(effectId))
            {
                return null;
            }

            return state.EffectRuntime.EffectNodes.Find(candidate =>
                candidate != null && candidate.EffectId == effectId);
        }

        private static bool IsManualNode(string nodeTypeId)
        {
            return nodeTypeId == RoundMainlineNodeTypeIds.CharacterCover ||
                   nodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow ||
                   nodeTypeId == RoundMainlineNodeTypeIds.Collection ||
                   nodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow;
        }

        private static void AddCollectionTasks(
            GameState state,
            EffectNodeRuntimeState executionEffect,
            MainlineNodeRuntimeState node,
            EffectStepResult result)
        {
            if (state == null || executionEffect == null || node == null || result == null ||
                state.EffectRuntime == null || state.EffectRuntime.PlayerOrderSnapshot == null)
            {
                return;
            }

            var tasks = new List<EffectSpec>();
            for (var i = 0; i < state.EffectRuntime.PlayerOrderSnapshot.Count; i++)
            {
                var player = state.FindPlayer(state.EffectRuntime.PlayerOrderSnapshot[i]);
                if (player == null || player.HasCollectedResourcesThisRound) continue;
                var amounts = RouteSelectionSessionAuthority.Value(executionEffect.ContinuationData, "collectionStartGoldVoucherAmounts");
                var amount = amounts?.Items?.Find(value => RouteSelectionSessionAuthority.Integer(value, "playerId", -1) == player.PlayerId);
                var startBudget = RouteSelectionSessionAuthority.Integer(amount, "amount", -1);
                if (startBudget < 0) throw new InvalidOperationException("collection_start_budget_missing");
                // NormalHarvest 通过阶段快照消费额度；开始响应的增资不能改写这个适配值。
                player.ResourceCollectionStartGoldVoucher = startBudget;
                tasks.Add(RouteResourceSelectionEffectExecutor.CreateHarvest(player.PlayerId, false,
                    executionEffect.SourceId, executionEffect.EffectId));
            }
            if (tasks.Count > 0)
            {
                if (IsSequentialCollectionExecution(executionEffect)) result.AddChildren(tasks);
                else result.AddIndependentChildren("collection_players", tasks);
            }
        }

        private static void CaptureCollectionStartBudgets(GameState state, EffectNodeRuntimeState executionEffect)
        {
            if (RouteSelectionSessionAuthority.Value(executionEffect.ContinuationData, "collectionStartGoldVoucherAmounts") != null)
                return;
            var amounts = new List<NormalizedValue>();
            foreach (var id in state.EffectRuntime.PlayerOrderSnapshot)
            {
                var player = state.FindPlayer(id);
                if (player == null || player.ResourceCollectionStartGoldVoucher < 0)
                    throw new InvalidOperationException("collection_start_budget_missing");
                amounts.Add(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(id) },
                    new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(player.ResourceCollectionStartGoldVoucher) }
                }));
            }
            RouteSelectionSessionAuthority.Set(executionEffect.ContinuationData, "collectionStartGoldVoucherAmounts",
                NormalizedValue.CreateArray(amounts));
        }

        private static bool HasOpenCollectionRouteSelection(GameState state)
        {
            return state.EffectRuntime != null && state.EffectRuntime.InteractionRequests != null &&
                state.EffectRuntime.InteractionRequests.Any(request => request != null &&
                    request.Status == "open" && request.InteractionTypeId == "route_resource.selection" &&
                    RouteSelectionSessionAuthority.Integer(request.PromptParameters, "purpose", -1) ==
                    (int)RouteSelectionPurpose.NormalHarvest);
        }

        private bool PauseFailedConcurrentStageIfNeeded(GameState state)
        {
            var main = GetActiveNode(state);
            var execution = main == null ? null : FindEffectNode(state, main.ExecutionEffectId);
            if (main == null || execution == null) return false;
            bool collection = main.NodeTypeId == RoundMainlineNodeTypeIds.Collection;
            bool concurrent = main.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup || IsParallelCoverExecution(execution);
            if (!collection && !concurrent) return false;
            var failed = execution.Status == EffectNodeStatus.Failed || execution.Status == EffectNodeStatus.Faulted ? execution : null;
            if (failed == null && concurrent)
            {
                // 仍有其他玩家待答时也核必要失败；条件式自行消费左侧合法失败。
                var pending = new Queue<EffectNodeRuntimeState>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                pending.Enqueue(execution);
                while (pending.Count > 0 && failed == null)
                {
                    var owner = pending.Dequeue();
                    if (!visited.Add(owner.EffectId)) continue;
                    foreach (string id in owner.ChildEffectIds)
                    {
                        var child = FindEffectNode(state, id);
                        if (child == null || child.Status == EffectNodeStatus.Completed) continue;
                        if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                        { failed = child; break; }
                        if (child.EffectTypeId != EffectTypeIds.Condition) pending.Enqueue(child);
                    }
                }
            }
            if (failed == null) return false;
            var work = GameStateCloneService.DeepClone(state);
            work.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
            string code = collection ? "collection_failed" : IsParallelCoverExecution(execution) ? "character_cover_failed" : "entrance_setup_failed";
            work.EffectRuntime.LastFaultCode = code;
            work.EffectRuntime.LastFaultMessage = failed.FailureReason;
            RuleCommit.ApplyFault(work, "round." + code + ":" + main.NodeId, new RuleJournalEntry
            {
                Kind = RuleJournalEntryKind.EffectNode, EntityId = failed.EffectId,
                Detail = code, Payload = NormalizedValue.CreateString(failed.FailureReason)
            });
            projector.Project(work);
            GameStateCloneService.CopyTo(state, work);
            return true;
        }

        private static string GetLifecycleEventType(string nodeTypeId)
        {
            switch (nodeTypeId)
            {
                case RoundMainlineNodeTypeIds.RoundStarted:
                    return "RoundStarted";
                case RoundMainlineNodeTypeIds.PlayerActionWindow:
                    return "PlayerActionWindowStarted";
                case RoundMainlineNodeTypeIds.Collection:
                    return "CollectionStarted";
                case RoundMainlineNodeTypeIds.PlayerCleanupWindow:
                    return "PlayerCleanupStarted";
                case RoundMainlineNodeTypeIds.RoundEnded:
                    return "RoundEnded";
                default:
                    return string.Empty;
            }
        }

        private static string GetCompletedLifecycleEventType(string nodeTypeId)
        {
            switch (nodeTypeId)
            {
                case RoundMainlineNodeTypeIds.PlayerActionWindow:
                    return "PlayerActionWindowCompleted";
                case RoundMainlineNodeTypeIds.PlayerCleanupWindow:
                    return "PlayerCleanupCompleted";
                default:
                    return string.Empty;
            }
        }

        private static EffectStepResult ExecuteEntranceSetup(EffectExecutionContext context, MainlineNodeRuntimeState main)
        {
            if (ItemTransferEffectExecutor.Number(context.Node.NormalizedArguments, "setupProtocolVersion") != 2)
                return EffectStepResult.Failed("entrance_setup_protocol_invalid");
            if (context.Node.FlowStage == string.Empty)
                return EffectStepResult.Continue("preparing_enterprises").AddChild(
                    ExpansionSetupFlow.PreparationSpec(context.State.EffectRuntime.PlayerOrderSnapshot[0]));
            foreach (var child in context.ChildNodes)
                if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                    return EffectStepResult.Failed(child.FailureReason);
            if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("等待全部开局必要任务。");
            if (context.Node.FlowStage == "preparing_enterprises")
            {
                var tasks = new List<EffectSpec>();
                var order = context.State.EffectRuntime.PlayerOrderSnapshot;
                for (int index = 0; index < order.Count; index++)
                {
                    var spec = EffectSpec.Create(PlayerEntranceTaskTypeId,
                        ItemTransferEffectExecutor.Object("mainNodeId", NormalizedValue.CreateString(main.NodeId),
                            "playerIndex", NormalizedValue.CreateInteger(index), "setupProtocolVersion", NormalizedValue.CreateInteger(2)), order[index]);
                    spec.StableKey = "entrance-task:" + order[index].ToString(CultureInfo.InvariantCulture);
                    spec.Visibility = GameStateVisibilityPolicy.Owner;
                    tasks.Add(spec);
                }
                return EffectStepResult.Continue("awaiting_entrance_tasks").AddIndependentChildren("entrance_player_tasks", tasks);
            }
            if (context.Node.FlowStage != "awaiting_entrance_tasks" ||
                !AllPlayersCompleted(main, context.State.EffectRuntime.PlayerOrderSnapshot))
                return EffectStepResult.Failed("entrance_setup_completion_invalid");
            main.ActiveTaskPlayerId = -1;
            main.CompletionRequested = true;
            return EffectStepResult.Completed();
        }

        private static EffectStepResult ExecuteEntrancePlayerTask(EffectExecutionContext context)
        {
            var execution = FindEntranceExecution(context.State.EffectRuntime, context.Node);
            if (execution == null || ItemTransferEffectExecutor.Number(context.Node.NormalizedArguments, "setupProtocolVersion") != 2)
                return EffectStepResult.Failed("entrance_task_outside_setup");
            if (context.Node.FlowStage == string.Empty)
                return EffectStepResult.Continue("entering").AddChild(PlayerEntranceEffectExecutor.Create(context.Node.PlayerId));
            foreach (var child in context.ChildNodes)
                if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                    return EffectStepResult.Failed(child.FailureReason);
            if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("等待本人开局子树。");
            if (context.Node.FlowStage == "entering")
                return EffectStepResult.Continue("initial_influence").AddChild(ExpansionSetupFlow.InitialInfluenceSpec(context.Node.PlayerId));
            if (context.Node.FlowStage == "initial_influence")
                return EffectStepResult.Continue("retaining_characters").AddChild(ExpansionSetupFlow.RetentionSpec(context.Node.PlayerId));
            if (context.Node.FlowStage != "retaining_characters") return EffectStepResult.Failed("entrance_task_stage_invalid");
            var main = context.State.EffectRuntime.MainNodes.Find(node =>
                node.ExecutionEffectId == execution.EffectId);
            if (main == null) return EffectStepResult.Failed("entrance_task_main_missing");
            AddCompletedPlayer(main, context.Node.PlayerId);
            return EffectStepResult.Completed().WithFlowStage("finished");
        }

        internal static bool IsParallelCoverExecution(EffectNodeRuntimeState node) =>
            node != null && node.EffectTypeId == MainlineEffectTypeId &&
            GetStringProperty(node.NormalizedArguments, "nodeTypeId") == RoundMainlineNodeTypeIds.CharacterCover &&
            ItemTransferEffectExecutor.Number(node.NormalizedArguments, "coverTaskSchedulingVersion") == 1;

        internal static bool IsSequentialCollectionExecution(EffectNodeRuntimeState node) =>
            node != null && node.EffectTypeId == MainlineEffectTypeId &&
            GetStringProperty(node.NormalizedArguments, "nodeTypeId") == RoundMainlineNodeTypeIds.Collection &&
            ItemTransferEffectExecutor.Number(node.NormalizedArguments, "collectionTaskSchedulingVersion") == 2;

        private static EffectNodeRuntimeState RuntimeNode(EffectRuntimeState runtime, string id) =>
            string.IsNullOrEmpty(id) ? null : runtime?.EffectNodes?.Find(node => node != null && node.EffectId == id);

        private static EffectNodeRuntimeState FindEntranceExecution(EffectRuntimeState runtime, EffectNodeRuntimeState node)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (node != null && visited.Add(node.EffectId))
            {
                if (node.EffectTypeId == MainlineEffectTypeId &&
                    GetStringProperty(node.NormalizedArguments, "nodeTypeId") == RoundMainlineNodeTypeIds.PlayerEntranceSetup &&
                    ItemTransferEffectExecutor.Number(node.NormalizedArguments, "setupProtocolVersion") == 2 &&
                    runtime.MainNodes.Any(main => main.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup &&
                        main.ExecutionEffectId == node.EffectId && main.NodeId == GetStringProperty(node.NormalizedArguments, "mainNodeId")))
                    return node;
                var parent = RuntimeNode(runtime, node.ParentEffectId);
                if (parent != null && !parent.ChildEffectIds.Contains(node.EffectId)) return null;
                node = parent;
            }
            return null;
        }

        private static EffectChildSchedulingGroup EntranceGroup(EffectNodeRuntimeState execution) =>
            execution?.ChildSchedulingGroups?.FirstOrDefault(group => group != null && group.StableGroupKey == "entrance_player_tasks");

        private static EffectNodeRuntimeState TaskChild(EffectRuntimeState runtime, EffectNodeRuntimeState task, string type) =>
            task?.ChildEffectIds?.Select(id => RuntimeNode(runtime, id)).FirstOrDefault(child => child != null && child.EffectTypeId == type);

        private static string Reference(NormalizedValue value, string key)
        {
            var field = ItemTransferEffectExecutor.Value(value, key);
            return field?.Kind == NormalizedValueKind.StableReference ? field.ReferenceId :
                field?.Kind == NormalizedValueKind.String ? field.StringValue : string.Empty;
        }

        internal static bool HasEntrancePlacement(EffectRuntimeState runtime, EffectNodeRuntimeState enter)
        {
            if (runtime == null || enter == null || enter.EffectTypeId != PlayerEntranceEffectExecutor.TypeId || enter.FlowStage != "entered" ||
                enter.Status == EffectNodeStatus.Failed || enter.Status == EffectNodeStatus.Faulted ||
                Reference(enter.NormalizedResult, "player") != "p" + enter.PlayerId.ToString(CultureInfo.InvariantCulture) ||
                Reference(enter.NormalizedResult, "resourcePoint").Length == 0) return false;
            string eventId = StableIdFactory.Create("event", enter.EffectId, PlayerEntranceEffectExecutor.EventType);
            return runtime.RuleEvents.Any(evt => evt.EventId == eventId && evt.EventType == PlayerEntranceEffectExecutor.EventType &&
                evt.SourceEffectId == enter.EffectId && evt.OwnerNodeId == enter.EffectId && evt.PlayerId == enter.PlayerId &&
                evt.TargetEntityId == Reference(enter.NormalizedResult, "resourcePoint")) &&
                runtime.DispatchReceipts.Any(receipt => receipt.EventId == eventId &&
                    receipt.SubscriptionId == PlayerEntranceEffectExecutor.RulesSubscriptionId && receipt.Succeeded);
        }

        internal static int GetEntrancePicker(EffectRuntimeState runtime, EffectNodeRuntimeState execution)
        {
            var group = EntranceGroup(execution);
            if (group == null) return -1;
            foreach (string id in group.ChildEffectIds)
            {
                var task = RuntimeNode(runtime, id);
                if (task == null) return -1;
                if (!HasEntrancePlacement(runtime, TaskChild(runtime, task, PlayerEntranceEffectExecutor.TypeId))) return task.PlayerId;
            }
            return -1;
        }

        internal static bool IsEntranceSetupChildEligible(EffectRuntimeState runtime, EffectNodeRuntimeState owner, int childIndex)
        {
            if (runtime == null || owner == null || childIndex < 0 || childIndex >= owner.ChildEffectIds.Count) return false;
            var execution = FindEntranceExecution(runtime, owner);
            if (execution == null) return true;
            var group = EntranceGroup(execution);
            // 企业准备及其真实子树发生在入场玩家组建立之前，沿原通用屏障执行。
            if (group == null) return execution.FlowStage == "preparing_enterprises";
            var child = RuntimeNode(runtime, owner.ChildEffectIds[childIndex]);
            if (owner == execution && child?.EffectTypeId == PlayerEntranceTaskTypeId)
            {
                int index = group.ChildEffectIds.IndexOf(child.EffectId);
                return index == 0 || index > 0 && HasEntrancePlacement(runtime,
                    TaskChild(runtime, RuntimeNode(runtime, group.ChildEffectIds[index - 1]), PlayerEntranceEffectExecutor.TypeId));
            }
            var task = owner.EffectTypeId == PlayerEntranceTaskTypeId ? owner : RuntimeNode(runtime, owner.ParentEffectId);
            if (task?.EffectTypeId != PlayerEntranceTaskTypeId) return true;
            int taskIndex = group.ChildEffectIds.IndexOf(task.EffectId);
            if (taskIndex < 0) return false;
            if (taskIndex == 0) return true;
            var previous = RuntimeNode(runtime, group.ChildEffectIds[taskIndex - 1]);
            if (owner.EffectTypeId == PlayerEntranceEffectExecutor.TypeId)
                return TaskChild(runtime, previous, PlayerEntranceEffectExecutor.TypeId)?.Status == EffectNodeStatus.Completed;
            if (owner == task && child?.EffectTypeId == ExpansionSetupFlow.InitialInfluenceTypeId)
                return TaskChild(runtime, previous, ExpansionSetupFlow.InitialInfluenceTypeId)?.Status == EffectNodeStatus.Completed;
            return true;
        }

        internal static IReadOnlyList<string> GetEntranceSchedulingOwners(EffectRuntimeState runtime, string changedEffectId)
        {
            var changed = RuntimeNode(runtime, changedEffectId);
            if (changed == null ||
                !(changed.EffectTypeId == PlayerEntranceEffectExecutor.TypeId &&
                      (HasEntrancePlacement(runtime, changed) || changed.Status == EffectNodeStatus.Completed) ||
                  changed.EffectTypeId == ExpansionSetupFlow.InitialInfluenceTypeId && changed.Status == EffectNodeStatus.Completed))
                return Array.Empty<string>();
            var execution = FindEntranceExecution(runtime, changed);
            var group = EntranceGroup(execution);
            if (group == null) return Array.Empty<string>();
            var owners = new List<string> { execution.EffectId };
            foreach (string id in group.ChildEffectIds)
            {
                var task = RuntimeNode(runtime, id);
                if (task == null) continue;
                owners.Add(task.EffectId);
                var enter = TaskChild(runtime, task, PlayerEntranceEffectExecutor.TypeId);
                if (enter != null) owners.Add(enter.EffectId);
            }
            return owners.AsReadOnly();
        }

        public static void RefreshConcurrentInteractionRevisions(GameState state)
        {
            var runtime = state?.EffectRuntime;
            if (runtime == null) return;
            foreach (var request in runtime.InteractionRequests)
            {
                if (request == null || request.Status != "open") continue;
                var owner = RuntimeNode(runtime, request.OwnerEffectId);
                if (owner == null || owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Failed ||
                    owner.Status == EffectNodeStatus.Faulted || state.FindPlayer(request.AnsweringPlayerId) == null) continue;
                if (EffectTreeExecutor.TryFindDeferredResponse(state, owner.EffectId, out var deferred) && request.AnsweringPlayerId == deferred.AnsweringPlayerId)
                {
                    // 发布新的请求版本；已经发送的旧答案仍按原 expectedRevision 拒绝。
                    request.StateRevision = runtime.StateRevision;
                    continue;
                }
                if (IsNormalCollectionRequest(state, request, owner))
                {
                    RefreshCollectionSelectionRevision(state, request, owner);
                    continue;
                }
                bool concurrent = false;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var ancestor = owner; ancestor != null && visited.Add(ancestor.EffectId);)
                {
                    if (IsParallelCoverExecution(ancestor) && IsActiveMainlineExecution(runtime, ancestor) ||
                        ancestor.EffectTypeId == MainlineEffectTypeId &&
                            GetStringProperty(ancestor.NormalizedArguments, "nodeTypeId") == RoundMainlineNodeTypeIds.PlayerEntranceSetup &&
                            ItemTransferEffectExecutor.Number(ancestor.NormalizedArguments, "setupProtocolVersion") == 2 && IsActiveMainlineExecution(runtime, ancestor) ||
                        ancestor.EffectTypeId == ExpansionSetupFlow.PreparationTypeId &&
                            ItemTransferEffectExecutor.Number(ancestor.NormalizedArguments, "setupProtocolVersion") == 2 &&
                            FindEntranceExecution(runtime, ancestor) is EffectNodeRuntimeState setup && IsActiveMainlineExecution(runtime, setup))
                    { concurrent = true; break; }
                    var parent = RuntimeNode(runtime, ancestor.ParentEffectId);
                    if (parent != null && !parent.ChildEffectIds.Contains(ancestor.EffectId)) break;
                    ancestor = parent;
                }
                if (concurrent) request.StateRevision = runtime.StateRevision;
            }
        }

        private static bool IsNormalCollectionRequest(GameState state, InteractionRequest request, EffectNodeRuntimeState owner)
        {
            if (state.Phase != GamePhase.ResourceCollection || request.InteractionTypeId != "route_resource.selection" ||
                RouteSelectionSessionAuthority.Integer(request.PromptParameters, "purpose", -1) != (int)RouteSelectionPurpose.NormalHarvest ||
                owner.EffectTypeId != RouteResourceSelectionEffectExecutor.TypeId) return false;
            var parent = RuntimeNode(state.EffectRuntime, owner.ParentEffectId);
            return parent?.EffectTypeId == MainlineEffectTypeId &&
                GetStringProperty(parent.NormalizedArguments, "nodeTypeId") == RoundMainlineNodeTypeIds.Collection &&
                parent.ChildEffectIds.Contains(owner.EffectId) && IsActiveMainlineExecution(state.EffectRuntime, parent);
        }

        private static bool IsActiveMainlineExecution(EffectRuntimeState runtime, EffectNodeRuntimeState execution) =>
            execution?.EffectTypeId == MainlineEffectTypeId && runtime.MainNodes.Any(main => main.NodeId == runtime.ActiveMainNodeId &&
                main.ExecutionEffectId == execution.EffectId && main.NodeId == GetStringProperty(execution.NormalizedArguments, "mainNodeId"));

        private static void RefreshCollectionSelectionRevision(GameState state, InteractionRequest request, EffectNodeRuntimeState owner)
        {
            request.StateRevision = state.EffectRuntime.StateRevision;
            RouteSelectionSessionAuthority.Set(request.PromptParameters, "requestRevision", NormalizedValue.CreateInteger(request.StateRevision));
            var record = RouteSelectionSessionAuthority.Value(owner.ContinuationData, "routeSelection");
            if (record != null && RouteSelectionSessionAuthority.Text(record, "scopeKey") ==
                RouteSelectionSessionAuthority.Text(request.PromptParameters, "scopeKey"))
                RouteSelectionSessionAuthority.Set(record, "requestRevision", NormalizedValue.CreateInteger(request.StateRevision));
        }

        private static bool ValidatePlayerGroup(EffectRuntimeState runtime, EffectNodeRuntimeState owner,
            string key, IReadOnlyList<int> order, string type, out List<EffectNodeRuntimeState> tasks)
        {
            tasks = new List<EffectNodeRuntimeState>();
            var groups = owner.ChildSchedulingGroups.Where(group => group != null && group.StableGroupKey == key).ToList();
            if (groups.Count != 1) return false;
            var group = groups[0];
            if (group.ProtocolVersion != 1 || group.OwnerEffectId != owner.EffectId ||
                group.Mode != EffectChildSchedulingMode.Independent || group.ChildEffectIds.Count != order.Count ||
                group.ChildEffectIds.Distinct().Count() != order.Count || group.StartIndex < 0 ||
                !owner.ChildEffectIds.Skip(group.StartIndex).Take(order.Count).SequenceEqual(group.ChildEffectIds)) return false;
            for (int index = 0; index < order.Count; index++)
            {
                var task = RuntimeNode(runtime, group.ChildEffectIds[index]);
                if (task == null || task.ParentEffectId != owner.EffectId || !owner.ChildEffectIds.Contains(task.EffectId) ||
                    task.EffectTypeId != type || task.PlayerId != order[index] || task.Visibility != GameStateVisibilityPolicy.Owner)
                    return false;
                tasks.Add(task);
            }
            return true;
        }

        public static bool TryValidateSetupTasks(GameState state, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = "concurrent_setup_invalid";
            if (state?.EffectRuntime == null) return false;
            var runtime = state.EffectRuntime;
            foreach (var main in runtime.MainNodes.Where(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup))
            {
                if (main.RoundNumber != 1 || main.PlayerId != -1 ||
                    runtime.MainNodes.Count(node => node.RoundExecutionId == main.RoundExecutionId &&
                        node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup) != 1) return false;
                var execution = RuntimeNode(runtime, main.ExecutionEffectId);
                if (execution == null)
                {
                    if (!string.IsNullOrEmpty(main.ExecutionEffectId) ||
                        main.Status != EffectNodeStatus.Ready && main.Status != EffectNodeStatus.Created) return false;
                    continue;
                }
                if (execution.EffectTypeId != MainlineEffectTypeId || execution.PlayerId != -1 ||
                    ItemTransferEffectExecutor.Number(execution.NormalizedArguments, "setupProtocolVersion") != 2 ||
                    GetStringProperty(execution.NormalizedArguments, "mainNodeId") != main.NodeId ||
                    GetStringProperty(execution.NormalizedArguments, "nodeTypeId") != main.NodeTypeId) return false;
                var order = new TurnOrderService().GetTurnOrder(state, main.StartPlayerId);
                if (order.Count < 2 || order.Count > 4 || main.CompletedPlayerIds.Distinct().Count() != main.CompletedPlayerIds.Count ||
                    main.CompletedPlayerIds.Any(player => !order.Contains(player))) return false;
                if (execution.FlowStage == string.Empty)
                {
                    if (execution.ChildEffectIds.Count != 0) return false;
                    continue;
                }
                var preparation = execution.ChildEffectIds.Select(id => RuntimeNode(runtime, id))
                    .Where(node => node?.EffectTypeId == ExpansionSetupFlow.PreparationTypeId).ToList();
                if (preparation.Count != 1 || preparation[0].PlayerId != order[0] ||
                    preparation[0].ParentEffectId != execution.EffectId ||
                    ItemTransferEffectExecutor.Number(preparation[0].NormalizedArguments, "setupProtocolVersion") != 2) return false;
                if (execution.FlowStage == "preparing_enterprises")
                {
                    if (EntranceGroup(execution) != null) return false;
                    continue;
                }
                if (execution.FlowStage != "awaiting_entrance_tasks" ||
                    preparation[0].Status != EffectNodeStatus.Completed ||
                    !ValidatePlayerGroup(runtime, execution, "entrance_player_tasks", order, PlayerEntranceTaskTypeId, out var tasks)) return false;
                bool unfinishedPlacement = false;
                for (int index = 0; index < tasks.Count; index++)
                {
                    var task = tasks[index];
                    if (ItemTransferEffectExecutor.Number(task.NormalizedArguments, "setupProtocolVersion") != 2 ||
                        ItemTransferEffectExecutor.Number(task.NormalizedArguments, "playerIndex", -1) != index ||
                        GetStringProperty(task.NormalizedArguments, "mainNodeId") != main.NodeId) return false;
                    string[] stages = { "", "entering", "initial_influence", "retaining_characters", "finished" };
                    int stage = Array.IndexOf(stages, task.FlowStage);
                    if (stage < 0) return false;
                    var enter = TaskChild(runtime, task, PlayerEntranceEffectExecutor.TypeId);
                    var influence = TaskChild(runtime, task, ExpansionSetupFlow.InitialInfluenceTypeId);
                    var retention = TaskChild(runtime, task, ExpansionSetupFlow.RetentionTypeId);
                    string[] childTypes = { PlayerEntranceEffectExecutor.TypeId, ExpansionSetupFlow.InitialInfluenceTypeId, ExpansionSetupFlow.RetentionTypeId };
                    foreach (string type in childTypes)
                        if (task.ChildEffectIds.Select(id => RuntimeNode(runtime, id)).Count(child => child?.EffectTypeId == type) > 1) return false;
                    if (stage == 0 && task.ChildEffectIds.Count != 0 ||
                        stage >= 1 && enter == null || stage >= 2 && influence == null || stage >= 3 && retention == null ||
                        stage < 2 && influence != null || stage < 3 && retention != null) return false;
                    foreach (var child in new[] { enter, influence, retention }.Where(child => child != null))
                        if (child.ParentEffectId != task.EffectId || child.PlayerId != task.PlayerId) return false;
                    bool placed = HasEntrancePlacement(runtime, enter);
                    if (placed && unfinishedPlacement) return false;
                    if (!placed) unfinishedPlacement = true;
                    if (stage >= 2 && enter.Status != EffectNodeStatus.Completed ||
                        stage >= 3 && influence.Status != EffectNodeStatus.Completed ||
                        stage == 4 && retention.Status != EffectNodeStatus.Completed) return false;
                    if (placed && main.NodeId == runtime.ActiveMainNodeId &&
                        YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state))
                    {
                        string location = Reference(enter.NormalizedResult, "resourcePoint");
                        var city = state.ItemZones.Items.SingleOrDefault(item =>
                            item.PhysicalKind == YC.Domain.Items.ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == task.PlayerId);
                        var mapLocation = state.ItemZones.Zones.SingleOrDefault(zone =>
                            zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location);
                        var dock = mapLocation == null ? null : state.ItemZones.Zones.SingleOrDefault(zone =>
                            zone.TemplateId == "core.zone.city_dock" && zone.ParentId == mapLocation.ZoneId);
                        if (city == null || dock == null || city.ZoneId != dock.ZoneId &&
                            !enter.PositionChanges.Any(change => change.ItemId == city.ItemId && change.To.ZoneId == dock.ZoneId)) return false;
                    }
                    if (task.Status == EffectNodeStatus.Completed &&
                        (stage != 4 || !main.CompletedPlayerIds.Contains(task.PlayerId))) return false;
                    if (main.CompletedPlayerIds.Contains(task.PlayerId) && stage != 4) return false;
                    if (index > 0 && enter != null)
                    {
                        var priorEnter = TaskChild(runtime, tasks[index - 1], PlayerEntranceEffectExecutor.TypeId);
                        if ((enter.Status == EffectNodeStatus.Ready || enter.Status == EffectNodeStatus.Running || enter.Status == EffectNodeStatus.Blocked ||
                             enter.Status == EffectNodeStatus.Completed) && !HasEntrancePlacement(runtime, priorEnter)) return false;
                        if (enter.ChildEffectIds.Select(id => RuntimeNode(runtime, id)).Any(child =>
                                child != null && child.Status != EffectNodeStatus.Created) && priorEnter?.Status != EffectNodeStatus.Completed) return false;
                        if (influence != null && influence.Status != EffectNodeStatus.Created &&
                            TaskChild(runtime, tasks[index - 1], ExpansionSetupFlow.InitialInfluenceTypeId)?.Status != EffectNodeStatus.Completed) return false;
                    }
                }
                if ((main.CompletionRequested || execution.Status == EffectNodeStatus.Completed) &&
                    tasks.Any(task => task.Status != EffectNodeStatus.Completed)) return false;
                if (main.NodeId == runtime.ActiveMainNodeId && main.ActiveTaskPlayerId != GetEntrancePicker(runtime, execution)) return false;
            }
            foreach (var preparation in runtime.EffectNodes.Where(node =>
                node.EffectTypeId == ExpansionSetupFlow.PreparationTypeId &&
                ItemTransferEffectExecutor.Value(node.NormalizedArguments, "setupProtocolVersion") != null))
            {
                if (ItemTransferEffectExecutor.Number(preparation.NormalizedArguments, "setupProtocolVersion") != 2) return false;
                if (state.SetupRecipe?.EnterpriseEntrance != true || preparation.FlowStage == string.Empty) continue;
                var parent = RuntimeNode(runtime, preparation.ParentEffectId);
                var main = runtime.MainNodes.Find(node => node.ExecutionEffectId == parent?.EffectId);
                if (main == null || main.NodeTypeId != RoundMainlineNodeTypeIds.PlayerEntranceSetup) return false;
                var order = new TurnOrderService().GetTurnOrder(state, main.StartPlayerId);
                if (!ValidatePlayerGroup(runtime, preparation, "initial_enterprise_selections", order,
                    ExpansionSetupFlow.EnterpriseSelectionTypeId, out var tasks)) return false;
                var candidates = SetupIds(preparation.ContinuationData, "candidateEnterpriseItemIds");
                var unassigned = SetupIds(preparation.ContinuationData, "unassignedEnterpriseItemIds");
                int draw = order.Count == 2 ? 4 : 2, keep = order.Count == 2 ? 2 : 1;
                if (candidates.Count != draw * order.Count || candidates.Distinct().Count() != candidates.Count ||
                    unassigned.Distinct().Count() != unassigned.Count || candidates.Intersect(unassigned).Any() ||
                    candidates.Concat(unassigned).Any(id => !IsSetupEnterpriseItem(state, registry, id))) return false;
                var chosen = new List<string>();
                var selectedOwners = new List<KeyValuePair<string, int>>();
                for (int index = 0; index < tasks.Count; index++)
                {
                    var task = tasks[index];
                    if (ItemTransferEffectExecutor.Number(task.NormalizedArguments, "keepCount") != keep ||
                        !SetupIds(task.NormalizedArguments, "candidateEnterpriseItemIds").SequenceEqual(candidates.Skip(index * draw).Take(draw))) return false;
                    if (task.Status == EffectNodeStatus.Completed)
                    {
                        var selected = SetupIds(task.NormalizedResult, "selectedEnterpriseItemIds");
                        if (selected.Count != keep || selected.Distinct().Count() != keep ||
                            selected.Any(id => !candidates.Skip(index * draw).Take(draw).Contains(id))) return false;
                        chosen.AddRange(selected);
                        selectedOwners.AddRange(selected.Select(id => new KeyValuePair<string, int>(id, task.PlayerId)));
                    }
                    foreach (var request in runtime.InteractionRequests.Where(request => request.OwnerEffectId == task.EffectId && request.Status == "open"))
                        if (request.InteractionTypeId != ExpansionSetupFlow.EnterpriseChoiceInteraction ||
                            request.AnsweringPlayerId != task.PlayerId || request.Visibility != GameStateVisibilityPolicy.Owner ||
                            request.MinSelections != keep || request.MaxSelections != keep ||
                            !request.CandidateIds.OrderBy(id => id).SequenceEqual(candidates.Skip(index * draw).Take(draw).OrderBy(id => id))) return false;
                }
                if (order.Count == 3)
                {
                    string extra = GetStringProperty(preparation.ContinuationData, "randomEnterpriseItemId");
                    if (!unassigned.Contains(extra)) return false;
                    chosen.Add(extra);
                }
                if (preparation.FlowStage != "awaiting_initial_enterprise_tasks")
                {
                    var owners = ItemTransferEffectExecutor.Value(preparation.ContinuationData, "initialEnterprisePlayerIds");
                    if (tasks.Any(task => task.Status != EffectNodeStatus.Completed) || chosen.Count != 4 ||
                        !SetupIds(preparation.ContinuationData, "selectedEnterpriseItemIds").SequenceEqual(chosen) ||
                        owners?.Kind != NormalizedValueKind.Array || owners.Items.Count != selectedOwners.Count) return false;
                    for (int index = 0; index < selectedOwners.Count; index++)
                        if (GetStringProperty(owners.Items[index], "enterpriseItemId") != selectedOwners[index].Key ||
                            ItemTransferEffectExecutor.Number(owners.Items[index], "playerId", -1) != selectedOwners[index].Value) return false;
                    if (chosen.Any(id => !IsSetupEnterpriseItem(state, registry, id) || !state.ItemZones.Items.Any(item => item.ItemId == id &&
                        state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.LocalZoneId == "enterprises")))) return false;
                }
            }
            diagnostic = string.Empty;
            return true;
        }

        private static List<string> SetupIds(NormalizedValue value, string field) =>
            ItemTransferEffectExecutor.Value(value, field)?.Items?.Select(item =>
                item.Kind == NormalizedValueKind.StableReference ? item.ReferenceId : item.StringValue).ToList() ?? new List<string>();

        private static bool IsSetupEnterpriseItem(GameState state, EffectRegistry registry, string itemId)
        {
            var item = state.ItemZones?.Items?.Find(candidate => candidate.ItemId == itemId);
            if (item == null) return false;
            if (registry != null) return registry.EnterpriseDefinitions?.FindEnterprise(item.DefinitionId) != null;
            // 创建/追加归档没有 registry：只用本局已绑定的企业语义分类和真实实体类型。
            // Recover 传入 registry 时仍走上面的完整定义查找，不以快照替代内容验证。
            return item.TypeId == "core.board_card" && item.PhysicalKind == YC.Domain.Items.ItemPhysicalKind.Card &&
                state.ContentDefinitions?.EnterpriseDefinitionIds?.Contains(item.DefinitionId) == true;
        }

        public static bool TryValidateParallelCoverTasks(GameState state, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = "parallel_cover_invalid";
            if (state?.EffectRuntime == null) return false;
            var runtime = state.EffectRuntime;
            foreach (var execution in runtime.EffectNodes.Where(node => node.EffectTypeId == MainlineEffectTypeId &&
                GetStringProperty(node.NormalizedArguments, "nodeTypeId") == RoundMainlineNodeTypeIds.CharacterCover &&
                ItemTransferEffectExecutor.Value(node.NormalizedArguments, "coverTaskSchedulingVersion") != null))
            {
                if (!IsParallelCoverExecution(execution)) return false;
                var main = runtime.MainNodes.Find(node => node.NodeId == GetStringProperty(execution.NormalizedArguments, "mainNodeId") &&
                    node.ExecutionEffectId == execution.EffectId && node.NodeTypeId == RoundMainlineNodeTypeIds.CharacterCover);
                if (main == null) return false;
                if (execution.FlowStage == string.Empty && execution.ChildEffectIds.Count == 0) continue;
                var order = new TurnOrderService().GetTurnOrder(state, main.StartPlayerId);
                if (!ValidatePlayerGroup(runtime, execution, "character_cover_players", order,
                    CharacterCoverEffectExecutor.EffectTypeId, out var tasks) || main.ActiveTaskPlayerId != -1 ||
                    main.CompletedPlayerIds.Distinct().Count() != main.CompletedPlayerIds.Count ||
                    main.CompletedPlayerIds.Any(player => !order.Contains(player))) return false;
                for (int index = 0; index < tasks.Count; index++)
                {
                    var task = tasks[index];
                    if (GetStringProperty(task.NormalizedArguments, "mainNodeId") != main.NodeId ||
                        ItemTransferEffectExecutor.Number(task.NormalizedArguments, "coverIndex", -1) != index ||
                        ItemTransferEffectExecutor.Number(task.NormalizedArguments, "playerId", -1) != task.PlayerId ||
                        ItemTransferEffectExecutor.Number(task.NormalizedArguments, "apiVersion") == 2) return false;
                    if (task.Status == EffectNodeStatus.Completed && !main.CompletedPlayerIds.Contains(task.PlayerId)) return false;
                    foreach (var request in runtime.InteractionRequests.Where(request => request.OwnerEffectId == task.EffectId && request.Status == "open"))
                    {
                        if (request.InteractionTypeId != CharacterCoverEffectExecutor.InteractionTypeId ||
                            request.AnsweringPlayerId != task.PlayerId || request.Visibility != GameStateVisibilityPolicy.Owner ||
                            request.MinSelections != 1 || request.MaxSelections != 1 ||
                            request.CandidateIds.Count == 0 || request.CandidateIds.Distinct().Count() != request.CandidateIds.Count ||
                            main.CompletedPlayerIds.Contains(task.PlayerId)) return false;
                        if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state) && request.CandidateIds.Any(id =>
                            !state.ItemZones.Items.Any(item => item.ItemId == id && item.ControllerSubject.PlayerId == task.PlayerId &&
                                state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.OwnerPlayerId == task.PlayerId && zone.LocalZoneId == "hand")))) return false;
                    }
                }
                if ((main.CompletionRequested || execution.Status == EffectNodeStatus.Completed) &&
                    (tasks.Any(task => task.Status != EffectNodeStatus.Completed) || !AllPlayersCompleted(main, order.ToList()))) return false;
                if (main.NodeId == runtime.ActiveMainNodeId && main.CompletedPlayerIds.Any(player =>
                    !HasPrimaryCoverFact(state, tasks.Single(task => task.PlayerId == player)))) return false;
            }
            diagnostic = string.Empty;
            return true;
        }

        private static bool HasPrimaryCoverFact(GameState state, EffectNodeRuntimeState task)
        {
            if (!YC.Domain.Items.ItemZoneLegacyProjection.IsActive(state))
                return !string.IsNullOrEmpty(state.FindPlayer(task.PlayerId)?.CoveredCharacterCardId);
            // JSON snapshot 尚未绑定旧字段投影；以任务的真实主槽、卡实例和牌区关系核盖放事实。
            string slotId = StableIdFactory.Create("cover_slot", task.EffectId, "primary");
            var slot = state.CharacterCoverSlots?.Find(value => value.SlotId == slotId && value.PlayerId == task.PlayerId);
            if (slot == null || string.IsNullOrEmpty(slot.CardInstanceId)) return false;
            string sourceId = string.IsNullOrEmpty(task.SourceId) ? task.EffectId : task.SourceId;
            if (slot.SourceEffectId != sourceId || slot.Reusable) return false;
            var item = state.ItemZones.Items.Find(value => value.ItemId == slot.CardInstanceId);
            var zone = item == null ? null : state.ItemZones.Zones.Find(value => value.ZoneId == item.ZoneId);
            return item != null && item.SlotId == slot.SlotId && item.ControllerSubject?.Kind == YC.Domain.Items.ItemSubjectKind.Player &&
                item.ControllerSubject.PlayerId == task.PlayerId &&
                item.PhysicalKind == YC.Domain.Items.ItemPhysicalKind.Card &&
                YC.Domain.Items.ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId, "core.character_card") &&
                zone?.TemplateId == "core.zone.character_covered" && zone.LocalZoneId == "covered" && zone.OwnerPlayerId == task.PlayerId;
        }

        private static string GetStringProperty(NormalizedValue value, string name)
        {
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < value.Properties.Count; i++)
            {
                var entry = value.Properties[i];
                if (entry != null && entry.Name == name && entry.Value != null &&
                    entry.Value.Kind == NormalizedValueKind.String)
                {
                    return entry.Value.StringValue ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static NormalizedValue CreateObject(IList<NormalizedValueEntry> entries)
        {
            return NormalizedValue.CreateObject(entries);
        }

        private static void AddCompletedPlayer(MainlineNodeRuntimeState node, int playerId)
        {
            if (node.CompletedPlayerIds == null)
            {
                node.CompletedPlayerIds = new List<int>();
            }

            if (!node.CompletedPlayerIds.Contains(playerId))
            {
                node.CompletedPlayerIds.Add(playerId);
            }
        }

        private static bool ContainsPlayer(IList<int> order, int playerId)
        {
            if (order == null)
            {
                return false;
            }

            for (var i = 0; i < order.Count; i++)
            {
                if (order[i] == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AllPlayersCompleted(MainlineNodeRuntimeState node, IList<int> order)
        {
            if (node == null || order == null || order.Count == 0 || node.CompletedPlayerIds == null)
            {
                return false;
            }

            for (var i = 0; i < order.Count; i++)
            {
                if (!node.CompletedPlayerIds.Contains(order[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private int FindNextIncompletePlayer(GameState state, MainlineNodeRuntimeState node, int currentPlayerId)
        {
            var order = state.EffectRuntime.PlayerOrderSnapshot;
            if (order == null || order.Count == 0)
            {
                return -1;
            }

            var currentIndex = order.IndexOf(currentPlayerId);
            if (currentIndex < 0)
            {
                currentIndex = -1;
            }

            for (var offset = 1; offset <= order.Count; offset++)
            {
                var playerId = order[(currentIndex + offset) % order.Count];
                if (!node.CompletedPlayerIds.Contains(playerId))
                {
                    return playerId;
                }
            }

            return -1;
        }
    }

}
