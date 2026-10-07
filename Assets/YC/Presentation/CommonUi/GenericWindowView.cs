using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>通用窗口的交互与动态内容入口；布局和静态文案由预制体持有。</summary>
    public sealed class GenericWindowView : MonoBehaviour
    {
        [SerializeField] private RectTransform content;
        [SerializeField] private Text titleText;
        [SerializeField] private Text subtitleText;
        [SerializeField] private Text messageText;
        [SerializeField] private ScrollRect contentScroll;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Button confirmButton;
        [SerializeField] private WindowCloseInputHandler closeInput;
        [SerializeField] private bool closeOnEscape = true;
        [SerializeField] private bool closeAfterConfirm = true;
        [SerializeField] private UnityEvent confirmed = new UnityEvent();
        [SerializeField] private UnityEvent cancelled = new UnityEvent();

        private GameObject previousSelection;
        private bool interactionEnabled = true;

        public RectTransform Content => content;
        public Button ConfirmButton => confirmButton;
        public Button CancelButton => cancelButton;
        public UnityEvent Confirmed => confirmed;
        public UnityEvent Cancelled => cancelled;

        private void OnEnable()
        {
            closeButton.onClick.AddListener(Cancel);
            if (cancelButton != null) cancelButton.onClick.AddListener(Cancel);
            confirmButton.onClick.AddListener(Confirm);
            closeInput.Configure(Cancel);
            if (EventSystem.current == null) return;
            previousSelection = EventSystem.current.currentSelectedGameObject;
            // 无底部取消按钮的窗口将初始焦点放在右上角关闭，避免键盘误确认。
            EventSystem.current.SetSelectedGameObject(cancelButton != null ? cancelButton.gameObject : closeButton.gameObject);
        }

        private void OnDisable()
        {
            closeButton.onClick.RemoveListener(Cancel);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(Cancel);
            confirmButton.onClick.RemoveListener(Confirm);
            closeInput.Configure(null);
            var events = EventSystem.current;
            if (events == null) return;
            var selected = events.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(transform))
                events.SetSelectedGameObject(previousSelection != null && previousSelection.activeInHierarchy
                    ? previousSelection : null);
            previousSelection = null;
        }

        private void Update()
        {
            if (closeOnEscape && Input.GetKeyDown(KeyCode.Escape)) Cancel();
        }

        public void Show()
        {
            gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
            contentScroll.verticalNormalizedPosition = 1f;
        }

        public void Hide() => gameObject.SetActive(false);

        public void SetInteractionEnabled(bool value)
        {
            interactionEnabled = value;
            closeButton.interactable = value;
            if (cancelButton != null) cancelButton.interactable = value;
            closeInput.Configure(value ? (UnityAction)Cancel : null);
        }

        /// <summary>调用方提供本次窗口的动态文案；不调用时沿用检查器文案。</summary>
        public void SetContent(string title, string message, string subtitle = null)
        {
            titleText.text = title;
            messageText.text = message;
            if (subtitle != null) subtitleText.text = subtitle;
        }

        public void Confirm()
        {
            if (!isActiveAndEnabled || !interactionEnabled || !confirmButton.IsInteractable()) return;
            if (closeAfterConfirm) Hide();
            confirmed.Invoke();
        }

        public void Cancel()
        {
            if (!isActiveAndEnabled || !interactionEnabled) return;
            Hide();
            cancelled.Invoke();
        }
    }
}
