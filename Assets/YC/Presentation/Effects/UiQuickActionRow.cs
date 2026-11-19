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
        private bool gameplayBindingActive;
        private string gameplayBindingSource;
        public Button Button => button;

        public void Bind(string text, bool available, System.Action execute)
        {
            ClearGameplayBinding();
            BindButton(text, available, execute);
        }

        public void BindWithGameplayText(string text, bool available, System.Action execute,
            YC.Domain.Presentation.GameplayTextDocument document,
            System.Action<Component, YC.Domain.Presentation.GameplayTextDocument, bool> render)
        {
            if (gameplayBindingActive && gameplayBindingSource != (text ?? string.Empty)) ClearGameplayBinding();
            BindButton(text, available, execute);
            BindGameplayText(document, render);
        }

        private void BindButton(string text, bool available, System.Action execute)
        {
            label.text = text ?? string.Empty;
            button.onClick.RemoveAllListeners();
            if (execute != null) button.onClick.AddListener(() => { if (button.interactable) execute(); });
            SetAvailable(available);
        }

        public void SetCharacterName(string name)
        {
            ClearGameplayBinding();
            label.text = string.Format(characterFormat, string.IsNullOrEmpty(name) ? emptyCharacterName : name);
        }

        public void BindGameplayText(YC.Domain.Presentation.GameplayTextDocument document,
            System.Action<Component, YC.Domain.Presentation.GameplayTextDocument, bool> render)
        {
            var host = label.GetComponent<GameplayTextView>();
            if (document == null) { ClearGameplayBinding(); return; }
            if (render == null) { ClearGameplayBinding(); label.text = document.ToPlainText(); return; }
            // 动态行只补已有文本渲染组件，不改变模板资产、文字框或交互结构。
            if (host == null) host = label.gameObject.AddComponent<GameplayTextView>();
            gameplayBindingActive = true;
            gameplayBindingSource = label.text;
            host.SetVisibilityBindingGuard(IsGameplayBindingCurrent);
            render(host, document, false);
        }

        private bool IsGameplayBindingCurrent() => this != null && gameplayBindingActive && label != null &&
            label.text == gameplayBindingSource;

        private void ClearGameplayBinding()
        {
            gameplayBindingActive = false;
            gameplayBindingSource = null;
            var host = label != null ? label.GetComponent<GameplayTextView>() : null;
            if (host == null) return;
            host.SetVisibilityBindingGuard(null);
            host.ClearBinding();
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
