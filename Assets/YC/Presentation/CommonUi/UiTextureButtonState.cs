using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>完整按钮贴图的状态切换；静态样式由预制体配置。</summary>
    [RequireComponent(typeof(Button))]
    public sealed class UiTextureButtonState : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField] private RawImage face;
        [SerializeField] private Text label;
        [SerializeField] private Texture normal;
        [SerializeField] private Texture hover;
        [SerializeField] private Texture pressed;
        [SerializeField] private Texture disabled;
        [SerializeField] private Color normalInk = Color.white;
        [SerializeField] private Color hoverInk = Color.white;
        [SerializeField] private Color pressedInk = Color.white;
        [SerializeField] private Color disabledInk = Color.gray;
        [SerializeField] private float pressedLabelOffset = 1f;
        [SerializeField] private float referenceHeight = 64f;
        private Button button;
        private bool hovered;
        private bool pointerDown;
        private bool hasRestPosition;
        private Vector2 labelRestPosition;

        private void OnEnable()
        {
            button = GetComponent<Button>();
            if (label != null)
            {
                labelRestPosition = label.rectTransform.anchoredPosition;
                hasRestPosition = true;
            }
            Refresh();
        }

        private void OnDisable()
        {
            hovered = pointerDown = false;
            if (label != null && hasRestPosition) label.rectTransform.anchoredPosition = labelRestPosition;
            hasRestPosition = false;
        }

        private void LateUpdate() => Refresh();
        public void OnPointerEnter(PointerEventData data) { hovered = true; Refresh(); }
        public void OnPointerExit(PointerEventData data) { hovered = pointerDown = false; Refresh(); }
        public void OnPointerDown(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left) return;
            pointerDown = button != null && button.IsInteractable();
            Refresh();
        }
        public void OnPointerUp(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left) return;
            pointerDown = false;
            Refresh();
        }

        public void Refresh()
        {
            if (face == null || button == null) return;
            var unavailable = !button.IsInteractable();
            if (unavailable) pointerDown = false;
            face.texture = unavailable ? disabled : pointerDown ? pressed : hovered ? hover : normal;
            if (label == null) return;
            label.color = unavailable ? disabledInk : pointerDown ? pressedInk : hovered ? hoverInk : normalInk;
            if (hasRestPosition)
            {
                var scale = referenceHeight > 0 ? ((RectTransform)transform).rect.height / referenceHeight : 1f;
                label.rectTransform.anchoredPosition = labelRestPosition +
                    (pointerDown ? Vector2.down * (pressedLabelOffset * scale) : Vector2.zero);
            }
        }
    }
}
