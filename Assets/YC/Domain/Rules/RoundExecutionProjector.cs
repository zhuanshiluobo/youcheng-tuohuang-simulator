using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    /// <summary>
    /// 将持久化主链的当前位置投影到迁移期旧字段。
    /// 除本类型外，回合流程代码不得写入 Phase、CurrentPlayerId、Round 或 ActionRound。
    /// </summary>
    public sealed class RoundExecutionProjector
    {
        public bool Project(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.EffectRuntime == null ||
                string.IsNullOrEmpty(state.EffectRuntime.ActiveMainNodeId) ||
                state.EffectRuntime.MainNodes == null)
            {
                return false;
            }

            var node = state.EffectRuntime.MainNodes.Find(candidate =>
                candidate != null && candidate.NodeId == state.EffectRuntime.ActiveMainNodeId);
            if (node == null)
            {
                return false;
            }

            state.Round = node.RoundNumber;
            state.StartPlayerId = state.EffectRuntime.CurrentRoundStartPlayerId > 0
                ? state.EffectRuntime.CurrentRoundStartPlayerId
                : node.StartPlayerId;

            switch (node.NodeTypeId)
            {
                case RoundMainlineNodeTypeIds.PlayerEntrance:
                    state.Phase = GamePhase.Entrance;
                    state.ActionRound = 0;
                    state.CurrentPlayerId = node.PlayerId;
                    break;
                case RoundMainlineNodeTypeIds.PlayerEntranceSetup:
                    state.Phase = GamePhase.Entrance;
                    state.ActionRound = 0;
                    var entranceExecution = state.EffectRuntime.EffectNodes.Find(effect => effect.EffectId == node.ExecutionEffectId);
                    node.ActiveTaskPlayerId = RoundExecutionService.GetEntrancePicker(state.EffectRuntime, entranceExecution);
                    state.CurrentPlayerId = node.ActiveTaskPlayerId;
                    break;
                case RoundMainlineNodeTypeIds.RoundStarted:
                    state.Phase = GamePhase.RoundStart;
                    state.ActionRound = 0;
                    state.CurrentPlayerId = -1;
                    break;

                case RoundMainlineNodeTypeIds.CharacterCover:
                    state.Phase = GamePhase.CharacterCover;
                    state.ActionRound = 0;
                    var coverExecution = state.EffectRuntime.EffectNodes.Find(effect => effect.EffectId == node.ExecutionEffectId);
                    state.CurrentPlayerId = RoundExecutionService.IsParallelCoverExecution(coverExecution)
                        ? -1 : ResolveCharacterCoverPlayer(state, node);
                    break;

                case RoundMainlineNodeTypeIds.PlayerActionWindow:
                    state.Phase = node.ActionRound == 2
                        ? GamePhase.ActionRound2
                        : GamePhase.ActionRound1;
                    state.ActionRound = node.ActionRound;
                    state.CurrentPlayerId = node.PlayerId;
                    break;

                case RoundMainlineNodeTypeIds.Collection:
                    state.Phase = GamePhase.ResourceCollection;
                    state.ActionRound = 0;
                    var collectionExecution = state.EffectRuntime.EffectNodes.Find(effect => effect.EffectId == node.ExecutionEffectId);
                    // 创建请求前先投影同一策略，避免请求观察到 -1 后又被本机投影改为首位。
                    bool sequentialCollection = collectionExecution == null
                        ? state.RuleConfiguration?.PlayerStageSchedulingMode == PlayerStageSchedulingMode.Sequential
                        : RoundExecutionService.IsSequentialCollectionExecution(collectionExecution);
                    state.CurrentPlayerId = sequentialCollection
                        ? ResolveSequentialCollectionPlayer(state, collectionExecution) : -1;
                    break;

                case RoundMainlineNodeTypeIds.PlayerCleanupWindow:
                    state.Phase = GamePhase.Cleanup;
                    state.ActionRound = 0;
                    state.CurrentPlayerId = node.PlayerId;
                    break;

                case RoundMainlineNodeTypeIds.RoundEnded:
                    var finalExecution = state.EffectRuntime.EffectNodes.Find(effect => effect.EffectId == node.ExecutionEffectId);
                    state.Phase = node.RoundNumber >= state.MaxRounds && YC.Domain.Scoring.FinalScoringSessionService.IsFinalStage(finalExecution?.FlowStage)
                        ? GamePhase.FinalScoring : GamePhase.RoundStart;
                    state.ActionRound = 0;
                    state.CurrentPlayerId = -1;
                    break;

                default:
                    return false;
            }

            return true;
        }

        public void ProjectEntrance(GameState state, int startPlayerId, int currentPlayerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.StartPlayerId = startPlayerId;
            state.CurrentPlayerId = currentPlayerId;
            state.Phase = GamePhase.Entrance;
            state.ActionRound = 0;
        }

        public void ProjectSetup(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.Phase = GamePhase.Setup;
            state.ActionRound = 0;
            state.CurrentPlayerId = -1;
        }

        public void ProjectFinalScoring(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.Phase = GamePhase.FinalScoring;
            state.ActionRound = 0;
            state.CurrentPlayerId = -1;
        }

        private static int ResolveCharacterCoverPlayer(
            GameState state,
            MainlineNodeRuntimeState node)
        {
            var execution = state.EffectRuntime.EffectNodes.Find(effect => effect.EffectId == node.ExecutionEffectId);
            if (execution != null)
                foreach (string id in execution.ChildEffectIds)
                {
                    var task = state.EffectRuntime.EffectNodes.Find(effect => effect.EffectId == id);
                    if (task != null && task.EffectTypeId == YC.Domain.Cards.CharacterCoverEffectExecutor.EffectTypeId &&
                        task.Status != EffectNodeStatus.Completed && task.Status != EffectNodeStatus.Failed && task.Status != EffectNodeStatus.Faulted)
                        return task.PlayerId;
                }
            if (node.ActiveTaskPlayerId > 0)
            {
                return node.ActiveTaskPlayerId;
            }

            IList<int> order = state.EffectRuntime.PlayerOrderSnapshot;
            if (order != null)
            {
                for (var i = 0; i < order.Count; i++)
                {
                    var player = state.FindPlayer(order[i]);
                    if (player != null && string.IsNullOrEmpty(player.CoveredCharacterCardId))
                    {
                        return player.PlayerId;
                    }
                }
            }

            return -1;
        }

        private static int ResolveSequentialCollectionPlayer(GameState state, EffectNodeRuntimeState execution)
        {
            // 已记录采集结果但必要响应尚未结束时，仍由该任务所属玩家持有控制权。
            foreach (string id in execution?.ChildEffectIds ?? new List<string>())
            {
                var child = state.EffectRuntime.EffectNodes.Find(effect => effect.EffectId == id);
                if (child != null && child.EffectTypeId == YC.Domain.Effects.RouteResourceSelectionEffectExecutor.TypeId &&
                    YC.Domain.Travel.RouteSelectionSessionAuthority.Integer(child.NormalizedArguments, "purpose", -1) ==
                        (int)YC.Domain.Travel.RouteSelectionPurpose.NormalHarvest &&
                    child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed && child.Status != EffectNodeStatus.Faulted)
                    return child.PlayerId;
            }
            foreach (int playerId in state.EffectRuntime.PlayerOrderSnapshot)
                if (state.FindPlayer(playerId)?.HasCollectedResourcesThisRound == false) return playerId;
            return -1;
        }
    }
}
