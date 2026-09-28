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
        [Tooltip("主动启用后才使用脚本状态皮肤；默认保留 Image、Text 和 Button 的检查器设置。")]
        [SerializeField] private bool useStateVisuals;
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
        private bool appearanceCaptured;
        private Sprite authoredFace;
        private Sprite authoredIcon;
        private Color authoredInk;
        private int appliedState = -1;

        public bool IsSelected => selected;

        private void Awake()
        {
            button = GetComponent<Button>();
            if (useStateVisuals)
            {
                button.transition = Selectable.Transition.None;
                button.targetGraphic = face;
            }
            if (icon != null) icon.raycastTarget = false;
            if (label != null) label.raycastTarget = false;
            Refresh();
        }

        private void OnEnable() { Refresh(); }
        private void OnDisable()
        {
            hovered = focused = pointerDown = false;
            if (appearanceCaptured)
            {
                RestoreAppearance();
                appliedState = -1;
            }
        }
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

        private void RestoreAppearance()
        {
            if (face != null) face.sprite = authoredFace;
            if (icon != null) icon.sprite = authoredIcon;
            if (label != null) label.color = authoredInk;
        }

        public void Refresh()
        {
            if (!useStateVisuals || face == null) return;
            if (button == null) button = GetComponent<Button>();
            if (!appearanceCaptured)
            {
                // 普通外观来自预制体，不使用脚本默认皮肤覆盖手工配置。
                authoredFace = face.sprite;
                authoredIcon = icon != null ? icon.sprite : null;
                authoredInk = label != null ? label.color : Color.white;
                appearanceCaptured = true;
            }
            var unavailable = pending || button == null || !button.IsInteractable();
            var highlighted = hovered || focused;
            var useSelected = selected && selectedFace != null;
            var state = unavailable ? 1 : pointerDown ? 2 :
                useSelected && highlighted ? 3 : useSelected ? 4 : highlighted ? 5 : 0;
            if (state == appliedState) return;
            // 普通状态下的检查器调整也作为下一次交互结束后的基准。
            if (appliedState == 0)
            {
                authoredFace = face.sprite;
                authoredIcon = icon != null ? icon.sprite : null;
                authoredInk = label != null ? label.color : Color.white;
            }
            appliedState = state;
            RestoreAppearance();
            var stateFace = unavailable ? disabled : pointerDown ? pressed :
                useSelected && highlighted && selectedHover != null ? selectedHover :
                useSelected ? selectedFace : highlighted ? hover : null;
            if (stateFace != null) face.sprite = stateFace;
            if (icon != null)
            {
                var stateIcon = unavailable ? mutedIcon : useSelected ? darkIcon : null;
                if (stateIcon != null) icon.sprite = stateIcon;
            }
            if (label != null && (unavailable || useSelected))
                label.color = unavailable ? mutedInk : darkInk;
        }
    }
}
