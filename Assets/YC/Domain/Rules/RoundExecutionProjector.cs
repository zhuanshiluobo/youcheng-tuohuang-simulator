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
                case RoundMainlineNodeTypeIds.RoundStarted:
                    state.Phase = GamePhase.RoundStart;
                    state.ActionRound = 0;
                    state.CurrentPlayerId = -1;
                    break;

                case RoundMainlineNodeTypeIds.CharacterCover:
                    state.Phase = GamePhase.CharacterCover;
                    state.ActionRound = 0;
                    state.CurrentPlayerId = ResolveCharacterCoverPlayer(state, node);
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
                    // 采集是开放顺序窗口，旧字段不能伪造一个当前玩家。
                    state.CurrentPlayerId = -1;
                    break;

                case RoundMainlineNodeTypeIds.PlayerCleanupWindow:
                    state.Phase = GamePhase.Cleanup;
                    state.ActionRound = 0;
                    state.CurrentPlayerId = node.PlayerId;
                    break;

                case RoundMainlineNodeTypeIds.RoundEnded:
                    state.Phase = GamePhase.RoundStart;
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
    }
}
