using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.State
{
    public enum GameBoxCardKind { Character, Event }

    /// <summary>只记录规则明确放回的组件；不由弃牌、剩余牌堆或玩家库存推算。</summary>
    [Serializable]
    public sealed class GameBoxState
    {
        public List<GameBoxCardState> Cards = new List<GameBoxCardState>();
        public List<GameBoxMarkerState> Markers = new List<GameBoxMarkerState>();
        public List<GameBoxEnterpriseState> Enterprises = new List<GameBoxEnterpriseState>();
        public List<GameBoxTokenState> Tokens = new List<GameBoxTokenState>();

        public static GameBoxState ForViewer(GameBoxState source, GameStateViewer viewer)
        {
            var result = source == null ? new GameBoxState() : GameStateCloneService.DeepClone(source);
            foreach (var card in result.Cards)
                if (card != null && !card.FaceUp && !viewer.IsHost) card.CardId = string.Empty;
            if (!viewer.IsHost)
                result.Enterprises.RemoveAll(board => board == null ||
                    viewer.Role != GameStateViewerRole.Player || board.OwnerPlayerId != viewer.PlayerId);
            return result;
        }
    }

    [Serializable]
    public sealed class GameBoxCardState
    {
        public string InstanceId = string.Empty;
        public GameBoxCardKind Kind;
        public string CardId = string.Empty;
        public bool FaceUp = true;
        public PlayerColor BackColor;
        public string BackVisualKey = string.Empty;
    }

    [Serializable]
    public sealed class GameBoxMarkerState { public PlayerColor Color; public int Count; }

    [Serializable]
    public sealed class GameBoxEnterpriseState
    {
        public int OwnerPlayerId;
        public string VisualKey = string.Empty;
    }

    [Serializable]
    public sealed class GameBoxTokenState
    {
        public string InstanceId = string.Empty;
        public string VisualKey = string.Empty;
        public string State = string.Empty;
        public int Count = 1;
    }
}
