using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>主界面共用槽位；卡图完整适配，命中范围沿用旋转后的槽位矩形。</summary>
    public sealed class GameplayCardSlotView : MonoBehaviour,
        IBeginDragHandler, IDragHandler
    {
        [SerializeField] private RectTransform root;
        [SerializeField] private RawImage artwork;
        [SerializeField] private AspectRatioFitter artworkAspect;
        [SerializeField] private Button button;
        [SerializeField] private Graphic hitSurface;
        private Action clicked;
        public RectTransform Root => root;
        public RawImage Artwork => artwork;

        public void Bind(Texture texture, Action onClick)
        {
            artwork.texture = texture;
            artwork.enabled = texture != null;
            if (texture != null) artworkAspect.aspectRatio = (float)texture.width / texture.height;
            clicked = onClick;
            button.onClick.RemoveListener(Click);
            button.onClick.AddListener(Click);
            button.interactable = onClick != null;
            // 弃牌装饰层不拦截整个牌堆入口。
            hitSurface.raycastTarget = onClick != null;
        }

        private void Click() => clicked?.Invoke();
        public void OnBeginDrag(PointerEventData e)
        {
            e.eligibleForClick = false;
        }
        public void OnDrag(PointerEventData e)
        {
            e.eligibleForClick = false;
        }
    }
}
