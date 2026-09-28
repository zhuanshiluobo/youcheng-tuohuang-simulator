using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>主界面整行快速行动；执行回调仍由行动面板绑定。</summary>
    public sealed class UiQuickActionRow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
        ISelectHandler, IDeselectHandler
    {
        [SerializeField] private Button button;
        [SerializeField] private Image background;
        [SerializeField] private Image frame;
        [SerializeField] private Text label;
        [SerializeField] private Sprite[] rowBackgrounds;
        [SerializeField] private Sprite[] rowFrames;
        [SerializeField] private string characterFormat = "使用角色牌：<color=#000000>{0}</color>";
        [SerializeField] private string emptyCharacterName = "未盖放";
        private bool hover;
        private bool focus;

        public void SetCharacterName(string name)
        {
            label.text = string.Format(characterFormat, string.IsNullOrEmpty(name) ? emptyCharacterName : name);
        }

        public void SetAvailable(bool available) { button.interactable = available; Refresh(); }
        public void OnPointerEnter(PointerEventData data) { hover = true; Refresh(); }
        public void OnPointerExit(PointerEventData data) { hover = false; Refresh(); }
        public void OnSelect(BaseEventData data) { focus = true; Refresh(); }
        public void OnDeselect(BaseEventData data) { focus = false; Refresh(); }
        private void OnDisable() { hover = focus = false; }
        private void OnEnable() => Refresh();
        private void Refresh()
        {
            var index = !button.interactable ? 3 : hover || focus ? 1 : 0;
            background.sprite = rowBackgrounds[index];
            frame.sprite = rowFrames[index];
        }
    }
}
