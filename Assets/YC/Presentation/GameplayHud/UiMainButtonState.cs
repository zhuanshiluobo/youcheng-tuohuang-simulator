using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>3.2 按钮的底板、图标和文字由同一状态驱动，避免 Button 与旧主题重复换图。</summary>
    [RequireComponent(typeof(Button), typeof(Image))]
    public sealed class UiMainButtonState : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler,
        ISelectHandler, IDeselectHandler
    {
        [SerializeField] private Image face;
        [SerializeField] private Image icon;
        [SerializeField] private Text label;
        [SerializeField] private Sprite normal;
        [SerializeField] private Sprite hover;
        [SerializeField] private Sprite pressed;
        [SerializeField] private Sprite disabled;
        [SerializeField] private Sprite selectedFace;
        [SerializeField] private Sprite selectedHover;
        [SerializeField] private Sprite lightIcon;
        [SerializeField] private Sprite darkIcon;
        [SerializeField] private Sprite mutedIcon;
        [SerializeField] private bool darkNormalInk;
        [SerializeField] private bool selected;
        [SerializeField] private Color lightInk = new Color(.925f, .922f, .855f, 1f);
        [SerializeField] private Color darkInk = new Color(.149f, .188f, .188f, 1f);
        [SerializeField] private Color mutedInk = new Color(.455f, .494f, .467f, 1f);

        private Button button;
        private bool hovered;
        private bool focused;
        private bool pointerDown;
        private bool pending;

        public bool IsSelected => selected;

        private void Awake()
        {
            button = GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = face;
            if (icon != null) icon.raycastTarget = false;
            if (label != null) label.raycastTarget = false;
            Refresh();
        }

        private void OnEnable() { Refresh(); }
        private void OnDisable() { hovered = focused = pointerDown = false; }
        private void LateUpdate() { Refresh(); }

        public void SetSelected(bool value) { selected = value; Refresh(); }

        public void SetAvailable(bool value)
        {
            if (button == null) button = GetComponent<Button>();
            button.interactable = value && !pending;
            Refresh();
        }

        public void SetPending(bool value)
        {
            pending = value;
            if (button == null) button = GetComponent<Button>();
            if (value) button.interactable = false;
            // 结束 pending 后由正式投影重新调用 SetAvailable，不恢复过期的可用性。
            Refresh();
        }

        public void OnPointerEnter(PointerEventData data) { hovered = true; Refresh(); }
        public void OnPointerExit(PointerEventData data) { hovered = pointerDown = false; Refresh(); }
        public void OnPointerDown(PointerEventData data)
        {
            pointerDown = button != null && button.IsInteractable();
            Refresh();
        }
        public void OnPointerUp(PointerEventData data) { pointerDown = false; Refresh(); }
        public void OnSelect(BaseEventData data) { focused = true; Refresh(); }
        public void OnDeselect(BaseEventData data) { focused = false; Refresh(); }

        public void Refresh()
        {
            if (face == null) return;
            if (button == null) button = GetComponent<Button>();
            var unavailable = pending || button == null || !button.IsInteractable();
            var highlighted = hovered || focused;
            var useSelected = selected && selectedFace != null;
            face.sprite = unavailable ? disabled : pointerDown ? pressed :
                useSelected && highlighted && selectedHover != null ? selectedHover :
                useSelected ? selectedFace : highlighted ? hover : normal;
            face.type = Image.Type.Sliced;
            face.color = Color.white;
            face.raycastTarget = true;
            var ink = unavailable ? mutedInk : useSelected || darkNormalInk ? darkInk : lightInk;
            if (icon != null)
            {
                icon.sprite = unavailable ? mutedIcon : useSelected || darkNormalInk ? darkIcon : lightIcon;
                icon.color = Color.white;
                icon.preserveAspect = true;
                icon.raycastTarget = false;
            }
            if (label != null)
            {
                label.color = ink;
                label.raycastTarget = false;
            }
        }
    }
}
