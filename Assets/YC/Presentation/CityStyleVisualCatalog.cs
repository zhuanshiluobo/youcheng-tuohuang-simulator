using System;
using UnityEngine;
using YC.Domain.CityStyles;

namespace YC.Presentation
{
    /// <summary>卡面版本、源矩形和安全区成组配置；这里只映射公开状态，不决定规则。</summary>
    [CreateAssetMenu(menuName = "YC/Presentation/City Style Visual Catalog")]
    public sealed class CityStyleVisualCatalog : ScriptableObject
    {
        [Serializable] public sealed class Card
        {
            public string id, version, artworkSpriteId;
            public string trackLayout;
        }
        [Serializable] public sealed class Track
        {
            public string layout, area, sourceKey;
            public Rect safeRect;
        }
        [SerializeField] private Card[] cards = Array.Empty<Card>();
        [SerializeField] private Track[] tracks = Array.Empty<Track>();
        [SerializeField] private Vector2 cardSize = new Vector2(930, 600);
        [SerializeField] private Color[] playerColors = Array.Empty<Color>();
        [SerializeField] private Sprite[] playerMarkerSprites = Array.Empty<Sprite>();
        public int PlayerIndex(Color color) => Array.FindIndex(playerColors, value => value == color);
        public Sprite PlayerMarkerSprite(Color color)
        {
            var index = PlayerIndex(color);
            return index >= 0 && index < playerMarkerSprites.Length ? playerMarkerSprites[index] : null;
        }
        [SerializeField] private int[] visualPlayerOrder = { 0, 1, 3, 2 };
        public int VisualLane(Color color)
        {
            for (var i = 0; i < visualPlayerOrder.Length; i++)
                if (PlayerColor(visualPlayerOrder[i], Color.clear) == color) return i;
            return 0;
        }
        public Card Find(string id) => Array.Find(cards, c => c.id == id);
        public Rect Bounds(string id, string area)
        {
            var card = Find(id);
            var track = Array.Find(tracks, t => t.layout == (card == null ? "" : card.trackLayout) && t.area == area);
            if (track == null) track = Array.Find(tracks, t => t.layout == "all" && t.area == area);
            if (track == null) return Rect.zero;
            var r = track.safeRect;
            return new Rect(r.x / cardSize.x, 1 - r.yMax / cardSize.y, r.width / cardSize.x, r.height / cardSize.y);
        }
        public Color PlayerColor(int index, Color fallback) => index >= 0 && index < playerColors.Length ? playerColors[index] : fallback;
    }
}
