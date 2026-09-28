using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum CardViewerMode { Inspect, UseCharacter }
    public enum CardViewerEffect { Plot, Strategy }

    /// <summary>仅消费调用方有权展示的图像；使用资格与提交仍由正式工作流复核。</summary>
    public sealed class CardViewer : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private RectTransform window;
        [SerializeField] private RawImage cardImage;
        [SerializeField] private AspectRatioFitter cardAspect;
        [SerializeField] private Text title;
        [SerializeField] private Text hint;
        [SerializeField] private GameObject actions;
        [SerializeField] private GameObject inspectHint;
        [SerializeField] private Button plotButton;
        [SerializeField] private Button strategyButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private string inspectTitle = "卡牌详情";
        [SerializeField] private string useTitle = "使用角色牌";
        [SerializeField] private string useHint = "";
        [SerializeField] private string plotUnavailableFormat = "计谋：{0}";
        [SerializeField] private string strategyUnavailableFormat = "策略：{0}";
        private Func<bool> isCurrent;
        private Action closed;
        private Action<CardViewerEffect> requestEffect;
        private GameObject returnFocus;
        private bool pending;
        private static int escapeFrame = -1;
        public CardViewerMode Mode { get; private set; }
        public string CardId { get; private set; }
        public bool IsShowing => this != null && gameObject.activeInHierarchy;
        public RectTransform Window => window;
        public RawImage CardImage => cardImage;
        public Button PlotButton => plotButton;
        public Button StrategyButton => strategyButton;
        public Button CancelButton => cancelButton;
        public static bool WasEscapeConsumedThisFrame() => escapeFrame == Time.frameCount;
        public static bool HasOpenViewer() => FindObjectOfType<CardViewer>() != null;

        public bool TryValidateConfiguration(out string reason)
        {
            reason = window == null || cardImage == null || cardAspect == null || title == null ||
                hint == null || actions == null || inspectHint == null || plotButton == null ||
                strategyButton == null || cancelButton == null ? "卡牌查看器引用不完整。" : string.Empty;
            return reason.Length == 0;
        }

        private void Awake()
        {
            YC.PlayerJourney.PlayerAutomationId.Attach(plotButton.gameObject, "action.character.tactic");
            YC.PlayerJourney.PlayerAutomationId.Attach(strategyButton.gameObject, "action.character.strategy");
            plotButton.onClick.AddListener(() => Request(CardViewerEffect.Plot));
            strategyButton.onClick.AddListener(() => Request(CardViewerEffect.Strategy));
            cancelButton.onClick.AddListener(Close);
        }

        public static CardViewer InstantiateFor(Transform owner)
        {
            var registry = owner == null ? null : owner.GetComponentInParent<GameplayDialogRegistry>();
            if (registry == null) registry = FindObjectOfType<GameplayDialogRegistry>();
            return registry == null ? null : registry.InstantiateCardViewer();
        }

        public void OpenInspect(Texture texture, Action onClose = null, Func<bool> valid = null)
        {
            Present(CardViewerMode.Inspect, texture, valid, onClose);
            CardId = string.Empty;
            requestEffect = null;
            plotButton.interactable = strategyButton.interactable = false;
            hint.text = string.Empty;
        }

        public void OpenCharacter(string cardId, Texture texture, bool canPlot, bool canStrategy,
            string plotReason, string strategyReason, Func<bool> valid,
            Action<CardViewerEffect> onRequest, Action onClose)
        {
            if (string.IsNullOrEmpty(cardId) || valid == null || onRequest == null)
                throw new ArgumentException("角色使用必须提供明确卡牌、资格校验和唯一提交入口。");
            Present(CardViewerMode.UseCharacter, texture, valid, onClose);
            CardId = cardId;
            requestEffect = onRequest;
            plotButton.interactable = canPlot;
            strategyButton.interactable = canStrategy;
            hint.text = canPlot && canStrategy ? useHint :
                (!canPlot ? string.Format(plotUnavailableFormat, plotReason) : string.Empty) +
                (!canPlot && !canStrategy ? "  " : string.Empty) +
                (!canStrategy ? string.Format(strategyUnavailableFormat, strategyReason) : string.Empty);
        }

        private void Present(CardViewerMode mode, Texture texture, Func<bool> valid, Action onClose)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (!TryValidateConfiguration(out var reason)) throw new InvalidOperationException(reason);
            Mode = mode;
            pending = false;
            isCurrent = valid;
            closed = onClose;
            returnFocus = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            cardImage.texture = texture;
            cardAspect.aspectRatio = (float)texture.width / texture.height;
            title.text = mode == CardViewerMode.Inspect ? inspectTitle : useTitle;
            actions.SetActive(mode == CardViewerMode.UseCharacter);
            inspectHint.SetActive(mode == CardViewerMode.Inspect);
            GameplayHudFrame.Active?.ShowPage(gameObject, false);
            if (GameplayHudFrame.Active == null) gameObject.SetActive(true);
            EventSystem.current?.SetSelectedGameObject(null);
        }

        private void Request(CardViewerEffect effect)
        {
            if (!IsShowing || pending || Mode != CardViewerMode.UseCharacter || requestEffect == null ||
                !(effect == CardViewerEffect.Plot ? plotButton : strategyButton).interactable) return;
            if (isCurrent == null || !isCurrent()) { Dismiss(); return; }
            pending = true;
            var submit = requestEffect;
            // 在进入下一步骤之前解除旧回调，迟到点击不能影响新页面；不发出取消事件。
            Dismiss();
            submit(effect);
        }

        public void SetReturn(Action onClose, Func<bool> valid)
        {
            closed = onClose;
            isCurrent = valid;
        }

        /// <summary>用户返回；只有仍有效的来源可以恢复。</summary>
        public void Close()
        {
            if (!IsShowing) return;
            var callback = isCurrent == null || isCurrent() ? closed : null;
            var focus = returnFocus;
            Dismiss();
            callback?.Invoke();
            if (focus != null && focus.activeInHierarchy)
                EventSystem.current?.SetSelectedGameObject(focus);
        }

        /// <summary>被其他资料页替换或请求失效；不得跳回原页面。</summary>
        public void Dismiss()
        {
            closed = null;
            requestEffect = null;
            isCurrent = null;
            returnFocus = null;
            CardId = string.Empty;
            if (this == null) return;
            if (cardImage != null) cardImage.texture = null;
            var frame = GameplayHudFrame.Active;
            if (frame != null) frame.HidePage(gameObject);
            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (isCurrent != null && !isCurrent()) { Dismiss(); return; }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                escapeFrame = Time.frameCount;
                Close();
            }
        }

        private void OnDisable()
        {
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(transform))
                EventSystem.current.SetSelectedGameObject(null);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left &&
                (Mode == CardViewerMode.Inspect ||
                 !RectTransformUtility.RectangleContainsScreenPoint(window, eventData.position, eventData.pressEventCamera)))
                Close();
        }
    }
}
