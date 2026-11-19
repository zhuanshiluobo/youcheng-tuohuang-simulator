using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>游戏盒合作板列表的原图条目，不使用设施卡槽。</summary>
    public sealed class GameBoxEnterpriseBoardItemView : MonoBehaviour
    {
        [SerializeField] private RectTransform root;
        [SerializeField] private RawImage artwork;
        [SerializeField] private CardPickerCardButton inspectButton;
        [SerializeField] private LayoutElement layout;
        [SerializeField, Min(0)] private float verticalMargin = 7f;
        [SerializeField, Min(.1f)] private float artworkScale = 1.2f;
        [SerializeField, Min(0)] private float headerSpacing = 5f, footerSpacing = 11f;
        [SerializeField] private TextAnchor artworkAlignment = TextAnchor.UpperCenter;
        public RawImage Artwork => artwork;
        public Sprite Sprite { get; private set; }
        public float VerticalMargin => verticalMargin;
        public float ArtworkScale => artworkScale;
        public float HeaderSpacing => headerSpacing;
        public float FooterSpacing => footerSpacing;
        public TextAnchor ArtworkAlignment => artworkAlignment;
        public bool IsConfigured => root != null && artwork != null && inspectButton != null && layout != null;

        public void Bind(string id, Sprite sprite, Action inspect, Func<bool> isCurrent)
        {
            Sprite = sprite;
            CardArtworkView.Set(artwork, sprite);
            artwork.enabled = sprite != null;
            artwork.raycastTarget = inspect != null;
            inspectButton.interactable = inspect != null;
            inspectButton.ConfigurePreview(inspect, isCurrent);
            inspectButton.onClick.RemoveAllListeners();
            if (inspect != null) inspectButton.onClick.AddListener(() =>
            {
                if (isCurrent == null || isCurrent()) inspect();
            });
            YC.PlayerJourney.PlayerAutomationId.Attach(gameObject, "gamebox.enterprise." + id);
        }

        public Vector2 Measure(Vector2 maximum)
        {
            if (Sprite == null || Sprite.rect.width <= 0 || Sprite.rect.height <= 0) return Vector2.zero;
            var size = Sprite.rect.size;
            return size * Mathf.Min(maximum.x / size.x, maximum.y / size.y);
        }

        public void SetSize(Vector2 size)
        {
            layout.minWidth = layout.preferredWidth = size.x;
            layout.minHeight = layout.preferredHeight = size.y;
            layout.flexibleWidth = layout.flexibleHeight = 0;
        }
    }
}
