using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Scoring;
using YC.Domain.SpecialActions;
using YC.Domain.State;

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
        /// 下一回合创建前的不变量：上一回合的 Effect 子树和 Interaction 必须已经终结。
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
                    if (request != null && request.Status == "open")
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
                    if (node != null && !IsTerminalEffectNode(node.Status))
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
                includeEntrance);

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
                if (report.Faulted)
                {
                    return ValidationResult.Failure(CommandErrorCode.WrongPhase, report.FaultCode);
                }

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
                    (node == null || request.OwnerEffectId != node.ExecutionEffectId))
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
            return EffectSpec.Create(
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
        }

        private EffectStepResult ExecuteMainlineStage(EffectExecutionContext context)
        {
            string mainNodeId = GetStringProperty(context.Node.NormalizedArguments, "mainNodeId");
            var mainNode = context.State.EffectRuntime.MainNodes.Find(candidate =>
                candidate != null && candidate.NodeId == mainNodeId);
            if (mainNode == null)
            {
                throw new InvalidOperationException("主链节点不存在：" + mainNodeId);
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance)
            {
                if (context.Node.FlowStage == string.Empty)
                    return EffectStepResult.Continue("entering").AddChild(PlayerEntranceEffectExecutor.Create(mainNode.PlayerId));
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("等待玩家入场及事件子树结算。");
                foreach (var child in context.ChildNodes)
                    if (child.Status == EffectNodeStatus.Failed) throw new InvalidOperationException("玩家入场子效果失败：" + child.FailureReason);
                mainNode.CompletionRequested = true;
                return EffectStepResult.Completed();
            }

            // 开局补偿在所有人的入场事件结算后、第一回合开始前统一发放。
            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.RoundStarted && mainNode.RoundNumber == 1 &&
                context.Node.FlowStage == string.Empty && context.State.EffectRuntime.MainNodes.Exists(node =>
                    node.RoundExecutionId == mainNode.RoundExecutionId && node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance))
                return EffectStepResult.Continue("initial_gold").AddChildren(
                    PlayerEntranceEffectExecutor.CreateInitialGoldEffects(context.State.EffectRuntime.PlayerOrderSnapshot));

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.CharacterCover)
            {
                if (context.Node.ChildEffectIds == null || context.Node.ChildEffectIds.Count == 0)
                {
                    return EffectStepResult.Continue("cover_tasks_created")
                        .AddChildren(CreateCharacterCoverTaskSpecs(context.State, mainNode));
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
                    var started = EffectStepResult.Continue("cleanup_started");
                    AppendLifecycleEvents(context.State, context.Node, mainNode, started);
                    return started;
                }

                if (HasNonTerminalChild(context))
                {
                    return EffectStepResult.NoProgress("玩家收尾子 Effect 尚未结束。");
                }

                mainNode.CompletionRequested = true;
                var cleanupCompleted = EffectStepResult.Completed();
                AppendLifecycleEvents(context.State, context.Node, mainNode, cleanupCompleted);
                return cleanupCompleted;
            }

            if (mainNode.NodeTypeId == RoundMainlineNodeTypeIds.Collection)
            {
                if (context.Node.FlowStage == string.Empty)
                {
                    var collectionStarted = EffectStepResult.Continue("collection_open");
                    AppendLifecycleEvents(context.State, context.Node, mainNode, collectionStarted);
                    AddCollectionTasks(context.State, context.Node, mainNode, collectionStarted);
                    return collectionStarted;
                }

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
                    context.Node.FlowStage == string.Empty && !mainNode.CompletionRequested)
                {
                    characterCardService.CleanupRound(context.State);
                    specialActionLifecycleService.CleanupRound(context.State);
                    var federalCouncilStartPlayerId = FederalCouncilEffectService.ConsumeLatestBuilder(context.State);
                    if (context.State.EffectRuntime.PendingNextRoundStartPlayerId < 0)
                        context.State.EffectRuntime.PendingNextRoundStartPlayerId = federalCouncilStartPlayerId > 0
                        ? federalCouncilStartPlayerId
                        : GetNextStartPlayerId(context.State);
                    ResetRoundScopedState(context.State);
                    mainNode.CompletionRequested = true;
                }

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
                    (child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed))
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
            if (state == null || effectNode == null || effectNode.EffectTypeId != MainlineEffectTypeId ||
                state.EffectRuntime == null)
            {
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

            for (var i = 0; i < state.EffectRuntime.PlayerOrderSnapshot.Count; i++)
            {
                var player = state.FindPlayer(state.EffectRuntime.PlayerOrderSnapshot[i]);
                if (player == null || player.HasCollectedResourcesThisRound) continue;
                result.AddInteraction(new EffectInteractionSpec
                {
                    InteractionTypeId = "collection.task",
                    AnsweringPlayerId = player.PlayerId,
                    PromptKey = "round.collection.task",
                    AnswerSchema = "boolean_accept",
                    MinSelections = 0,
                    MaxSelections = 0
                });
            }
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
