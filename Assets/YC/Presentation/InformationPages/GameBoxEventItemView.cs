using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>事件图及游戏盒卡边；不继承角色/设施选择卡槽的比例与状态。</summary>
    public sealed class GameBoxEventItemView : MonoBehaviour
    {
        [SerializeField] private RectTransform root;
        [SerializeField] private RawImage artwork;
        [SerializeField] private CardPickerCardButton inspectButton;
        [SerializeField] private LayoutElement layout;
        public RectTransform Root => root;
        public RawImage Artwork => artwork;
        public Sprite Sprite { get; private set; }
        public bool IsConfigured => root != null && artwork != null && inspectButton != null && layout != null;
        public void Bind(string id, Sprite sprite, Action inspect, Func<bool> isCurrent)
        {
            Sprite = sprite;
            CardArtworkView.Set(artwork, sprite);
            artwork.enabled = sprite != null;
            artwork.raycastTarget = inspect != null;
            inspectButton.enabled = inspect != null;
            inspectButton.interactable = inspect != null;
            inspectButton.ConfigurePreview(inspect, isCurrent);
            inspectButton.onClick.RemoveAllListeners();
            if (inspect != null) inspectButton.onClick.AddListener(() =>
            {
                if (isCurrent == null || isCurrent()) inspect();
            });
            YC.PlayerJourney.PlayerAutomationId.Attach(gameObject, "gamebox.event." + id);
        }
        public Vector2 Measure(Vector2 maximum)
        {
            if (Sprite == null || Sprite.rect.width <= 0 || Sprite.rect.height <= 0) return Vector2.zero;
            var size = Sprite.rect.size;
            return size * Mathf.Min(maximum.x / size.x, maximum.y / size.y);
        }
        public void SetSize(Vector2 size, bool inLayout)
        {
            layout.ignoreLayout = !inLayout;
            layout.minWidth = layout.preferredWidth = size.x;
            layout.minHeight = layout.preferredHeight = size.y;
            layout.flexibleWidth = layout.flexibleHeight = 0;
            if (!inLayout) root.sizeDelta = size;
        }
    }
}
