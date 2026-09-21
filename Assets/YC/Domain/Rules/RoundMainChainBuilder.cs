using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public static class RoundMainlineNodeTypeIds
    {
        public const string PlayerEntrance = "player_entrance";
        public const string RoundStarted = "round_started";
        public const string CharacterCover = "character_cover";
        public const string PlayerActionWindow = "player_action_window";
        public const string Collection = "collection";
        public const string PlayerCleanupWindow = "player_cleanup_window";
        public const string RoundEnded = "round_ended";
    }

    [Serializable]
    public sealed class RoundMainChain
    {
        public string RoundExecutionId = string.Empty;
        public int RoundNumber;
        public int StartPlayerId = -1;
        public List<int> PlayerOrderSnapshot = new List<int>();
        public string FirstNodeId = string.Empty;
        public List<MainlineNodeRuntimeState> Nodes = new List<MainlineNodeRuntimeState>();
    }

    /// <summary>
    /// 构造一回合的固定主链。该类型只处理输入、顺序和稳定 ID，不读取场景或 Unity 状态。
    /// </summary>
    public sealed class RoundMainChainBuilder
    {
        public const string MainlineIdKind = "mainline";
        public const string RoundExecutionIdKind = "round_execution";

        public RoundMainChain Build(
            string gameId,
            string roundExecutionId,
            int roundNumber,
            IList<int> playerOrder,
            int startPlayerId = -1,
            bool includeEntrance = false)
        {
            ValidateInput(roundNumber, playerOrder);

            var order = new List<int>(playerOrder);
            if (string.IsNullOrEmpty(roundExecutionId))
            {
                roundExecutionId = CreateRoundExecutionId(gameId, roundNumber, order);
            }

            if (startPlayerId <= 0)
            {
                startPlayerId = order[0];
            }

            var chain = new RoundMainChain
            {
                RoundExecutionId = roundExecutionId,
                RoundNumber = roundNumber,
                StartPlayerId = startPlayerId,
                PlayerOrderSnapshot = order,
                Nodes = new List<MainlineNodeRuntimeState>()
            };

            if (includeEntrance)
                foreach (int playerId in order) AddNode(chain, RoundMainlineNodeTypeIds.PlayerEntrance, playerId, 0);
            AddNode(chain, RoundMainlineNodeTypeIds.RoundStarted, -1, 0);
            AddNode(chain, RoundMainlineNodeTypeIds.CharacterCover, -1, 0, order[0]);

            for (var actionRound = 1; actionRound <= 2; actionRound++)
            {
                for (var i = 0; i < order.Count; i++)
                {
                    AddNode(
                        chain,
                        RoundMainlineNodeTypeIds.PlayerActionWindow,
                        order[i],
                        actionRound);
                }
            }

            AddNode(chain, RoundMainlineNodeTypeIds.Collection, -1, 0);
            for (var i = 0; i < order.Count; i++)
            {
                AddNode(
                    chain,
                    RoundMainlineNodeTypeIds.PlayerCleanupWindow,
                    order[i],
                    0);
            }

            AddNode(chain, RoundMainlineNodeTypeIds.RoundEnded, -1, 0);
            chain.FirstNodeId = chain.Nodes[0].NodeId;
            return chain;
        }

        public List<MainlineNodeRuntimeState> BuildNodes(
            string gameId,
            string roundExecutionId,
            int roundNumber,
            IList<int> playerOrder,
            int startPlayerId = -1)
        {
            return Build(gameId, roundExecutionId, roundNumber, playerOrder, startPlayerId).Nodes;
        }

        public string CreateRoundExecutionId(string gameId, int roundNumber, IList<int> playerOrder)
        {
            ValidateInput(roundNumber, playerOrder);
            var components = new List<string>
            {
                gameId ?? string.Empty,
                roundNumber.ToString(CultureInfo.InvariantCulture),
                playerOrder.Count.ToString(CultureInfo.InvariantCulture)
            };
            for (var i = 0; i < playerOrder.Count; i++)
            {
                components.Add(playerOrder[i].ToString(CultureInfo.InvariantCulture));
            }

            return StableIdFactory.Create(RoundExecutionIdKind, components.ToArray());
        }

        private static void ValidateInput(int roundNumber, IList<int> playerOrder)
        {
            if (roundNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(roundNumber), "回合编号必须为正数。");
            }

            if (playerOrder == null || playerOrder.Count == 0)
            {
                throw new ArgumentException("回合主链至少需要一名玩家。", nameof(playerOrder));
            }

            var seen = new HashSet<int>();
            for (var i = 0; i < playerOrder.Count; i++)
            {
                if (playerOrder[i] <= 0 || !seen.Add(playerOrder[i]))
                {
                    throw new ArgumentException("玩家顺序必须包含不重复的正数玩家 ID。", nameof(playerOrder));
                }
            }
        }

        private static void AddNode(
            RoundMainChain chain,
            string nodeTypeId,
            int playerId,
            int actionRound,
            int activeTaskPlayerId = -1)
        {
            var sequence = chain.Nodes.Count + 1;
            var nodeId = StableIdFactory.Create(
                MainlineIdKind,
                chain.RoundExecutionId,
                nodeTypeId,
                sequence.ToString(CultureInfo.InvariantCulture),
                playerId < 0 ? string.Empty : playerId.ToString(CultureInfo.InvariantCulture),
                actionRound.ToString(CultureInfo.InvariantCulture));
            var node = new MainlineNodeRuntimeState
            {
                NodeId = nodeId,
                RoundExecutionId = chain.RoundExecutionId,
                NodeTypeId = nodeTypeId,
                RoundNumber = chain.RoundNumber,
                MainlineIndex = sequence,
                StartPlayerId = chain.StartPlayerId,
                ActionRound = actionRound,
                PlayerId = playerId,
                ActiveTaskPlayerId = activeTaskPlayerId,
                Status = sequence == 1 ? EffectNodeStatus.Ready : EffectNodeStatus.Created
            };

            if (chain.Nodes.Count > 0)
            {
                var previous = chain.Nodes[chain.Nodes.Count - 1];
                previous.NextNodeId = node.NodeId;
                node.PreviousNodeId = previous.NodeId;
            }

            chain.Nodes.Add(node);
        }
    }
}
