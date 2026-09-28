using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    [CreateAssetMenu(menuName = "YC/企业与科室原板")]
    public sealed class EnterpriseBoardCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Board
        {
            public string visualKey;
            public Texture2D texture;
            public Rect uv = new Rect(0, 0, 1, 1);
            public float aspect;
            public Vector2[] rewardAnchors;
        }
        [SerializeField] private Board[] boards;
        public Board Find(string key) => Array.Find(boards ?? Array.Empty<Board>(), b => b.visualKey == key);
        public bool TryValidateConfiguration(out string reason)
        {
            if (boards == null || boards.Length == 0) { reason = "缺少企业／科室原板。"; return false; }
            var keys = new System.Collections.Generic.HashSet<string>();
            foreach (var board in boards)
                if (board == null || string.IsNullOrEmpty(board.visualKey) || !keys.Add(board.visualKey) ||
                    board.texture == null || board.aspect <= 0 || board.uv.width <= 0 || board.uv.height <= 0)
                { reason = "企业／科室原板映射无效：" + board?.visualKey + "，贴图=" + board?.texture +
                    "，比例=" + board?.aspect + "，UV=" + board?.uv; return false; }
            reason = string.Empty;
            return true;
        }
        public static void Bind(RawImage image, AspectRatioFitter aspect, Board board)
        {
            image.texture = board?.texture;
            image.uvRect = board?.uv ?? new Rect(0, 0, 1, 1);
            image.enabled = board != null;
            if (board != null) aspect.aspectRatio = board.aspect;
        }
    }
}
