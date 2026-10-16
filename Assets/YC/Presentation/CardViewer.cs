using System;
using System.Collections.Generic;
using YC.Domain.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public enum CardViewerMode { Inspect, UseCharacter, GameBoxCharacterInspect }
    public enum CardViewerEffect { Plot, Strategy }

    /// <summary>仅消费调用方有权展示的图像；使用资格与提交仍由正式工作流复核。</summary>
    public sealed class CardViewer : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private RectTransform window;
        [SerializeField] private RawImage cardImage;
        [SerializeField] private AspectRatioFitter cardAspect;
        [SerializeField] private GameObject gameBoxFaceGap;
        [SerializeField] private GameObject gameBoxBackContain;
        [SerializeField] private RawImage gameBoxBackImage;
        [SerializeField] private AspectRatioFitter gameBoxBackAspect;
        [SerializeField, Min(0)] private float gameBoxOpeningClickGuardSeconds = .3f;
        [SerializeField] private Text title;
        [SerializeField] private Text hint;
        [SerializeField] private GameplayTextView hintDocumentView;
        private Action<Component, GameplayTextDocument, bool> renderGameplayText;
        public void BindGameplayText(Action<Component, GameplayTextDocument, bool> render) => renderGameplayText = render;
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
        private bool boundCanPlot, boundCanStrategy, submissionAvailable;
        public Func<bool> CanSubmit { private get; set; }
        private float gameBoxOpenedAt;
        private static int escapeFrame = -1;
        public CardViewerMode Mode { get; private set; }
        public string CardId { get; private set; }
        public Sprite DisplayedSprite { get; private set; }
        public bool IsShowing => this != null && gameObject.activeInHierarchy;
        public RectTransform Window => window;
        public RawImage CardImage => cardImage;
        public RawImage GameBoxBackImage => gameBoxBackImage;
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

        public void OpenInspect(Sprite sprite, Action onClose = null, Func<bool> valid = null)
        {
            Present(CardViewerMode.Inspect, sprite, valid, onClose);
            CardId = string.Empty;
            requestEffect = null;
            plotButton.interactable = strategyButton.interactable = false;
            hintDocumentView?.ClearBinding();
            hint.text = string.Empty;
        }

        public void OpenGameBoxCharacter(string instanceId, Sprite front, Texture2D back,
            Action onClose = null, Func<bool> valid = null)
        {
            if (string.IsNullOrEmpty(instanceId) || front == null || back == null)
                throw new ArgumentException("游戏盒角色查看必须提供真实实例及其已授权正反面图像。");
            if (gameBoxFaceGap == null || gameBoxBackContain == null || gameBoxBackImage == null || gameBoxBackAspect == null)
                throw new InvalidOperationException("游戏盒角色双面查看引用不完整。");
            Present(CardViewerMode.GameBoxCharacterInspect, front, valid, onClose);
            CardId = instanceId;
            requestEffect = null;
            plotButton.interactable = strategyButton.interactable = false;
            hintDocumentView?.ClearBinding();
            hint.text = string.Empty;
            CardArtworkView.SetTexture(gameBoxBackImage, back);
            gameBoxBackAspect.aspectRatio = (float)back.width / back.height;
            gameBoxOpenedAt = Time.unscaledTime;
        }

        public void OpenCharacter(string cardId, Sprite sprite, bool canPlot, bool canStrategy,            GameplayTextDocument plotReason, GameplayTextDocument strategyReason, Func<bool> valid,
            Action<CardViewerEffect> onRequest, Action onClose)
        {
            if (hintDocumentView == null || renderGameplayText == null)
                throw new InvalidOperationException("角色查看器原hint文档引用尚未接线。");
            OpenCharacter(cardId, sprite, canPlot, canStrategy, string.Empty, string.Empty, valid, onRequest, onClose);
            renderGameplayText(hintDocumentView, BuildUnavailableHintDocument(canPlot, canStrategy, plotReason, strategyReason), false);
        }

        private GameplayTextDocument BuildUnavailableHintDocument(bool canPlot, bool canStrategy,
            GameplayTextDocument plotReason, GameplayTextDocument strategyReason)
        {
            var runs = new List<GameplayTextRun>();
            if (canPlot && canStrategy) runs.Add(new TextRun(useHint));
            else
            {
                if (!canPlot) AppendReason(runs, plotUnavailableFormat, plotReason);
                if (!canPlot && !canStrategy) runs.Add(new TextRun("  "));
                if (!canStrategy) AppendReason(runs, strategyUnavailableFormat, strategyReason);
            }
            return new GameplayTextDocument(runs, YC.Domain.State.StableIdFactory.Create("card-viewer-unavailable",
                canPlot.ToString(), canStrategy.ToString(), useHint, plotUnavailableFormat, strategyUnavailableFormat,
                plotReason?.Identity ?? string.Empty, strategyReason?.Identity ?? string.Empty));
        }

        private static void AppendReason(List<GameplayTextRun> runs, string format, GameplayTextDocument reason)
        {
            const string marker = "\u001f";
            var pieces = string.Format(format, marker).Split(new[] { marker }, StringSplitOptions.None);
            for (int index = 0; index < pieces.Length; index++)
            {
                runs.Add(new TextRun(pieces[index]));
                if (index + 1 < pieces.Length && reason != null) runs.AddRange(reason.Runs);
            }
        }

        public void OpenCharacter(string cardId, Sprite sprite, bool canPlot, bool canStrategy,
            string plotReason, string strategyReason, Func<bool> valid,
            Action<CardViewerEffect> onRequest, Action onClose)
        {
            if (string.IsNullOrEmpty(cardId) || valid == null || onRequest == null)
                throw new ArgumentException("角色使用必须提供明确卡牌、资格校验和唯一提交入口。");
            Present(CardViewerMode.UseCharacter, sprite, valid, onClose);
            CardId = cardId;
            requestEffect = onRequest;
            boundCanPlot = canPlot; boundCanStrategy = canStrategy;
            RefreshSubmissionAvailability(true);
            hintDocumentView?.ClearBinding();
            hint.text = canPlot && canStrategy ? useHint :
                (!canPlot ? string.Format(plotUnavailableFormat, plotReason) : string.Empty) +
                (!canPlot && !canStrategy ? "  " : string.Empty) +
                (!canStrategy ? string.Format(strategyUnavailableFormat, strategyReason) : string.Empty);
        }

        private void Present(CardViewerMode mode, Sprite sprite, Func<bool> valid, Action onClose)
        {
            if (sprite == null) throw new ArgumentNullException(nameof(sprite));
            if (!TryValidateConfiguration(out var reason)) throw new InvalidOperationException(reason);
            Mode = mode;
            pending = false;
            CanSubmit = null;
            isCurrent = valid;
            closed = onClose;
            returnFocus = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            DisplayedSprite = sprite;
            var gameBoxFaces = mode == CardViewerMode.GameBoxCharacterInspect;
            if (gameBoxBackImage != null) CardArtworkView.SetTexture(gameBoxBackImage, null);
            if (gameBoxFaceGap != null) gameBoxFaceGap.SetActive(gameBoxFaces);
            if (gameBoxBackContain != null) gameBoxBackContain.SetActive(gameBoxFaces);
            CardArtworkView.Set(cardImage, sprite);
            cardAspect.aspectRatio = CardArtworkView.AspectRatio(sprite);
            title.text = mode == CardViewerMode.UseCharacter ? useTitle : inspectTitle;
            actions.SetActive(mode == CardViewerMode.UseCharacter);
            inspectHint.SetActive(mode != CardViewerMode.UseCharacter);
            GameplayHudFrame.Active?.ShowPage(gameObject, false);
            if (GameplayHudFrame.Active == null) gameObject.SetActive(true);
            EventSystem.current?.SetSelectedGameObject(null);
        }

        private void Request(CardViewerEffect effect)
        {
            if (!IsShowing || pending || Mode != CardViewerMode.UseCharacter || requestEffect == null ||
                !submissionAvailable || CanSubmit?.Invoke() == false ||
                !(effect == CardViewerEffect.Plot ? plotButton : strategyButton).interactable) return;
            if (isCurrent == null || !isCurrent()) { Dismiss(); return; }
            pending = true;
            var submit = requestEffect;
            // 在进入下一步骤之前解除旧回调，迟到点击不能影响新页面；不发出取消事件。
            Dismiss();
            submit(effect);
        }

        public void RefreshSubmissionAvailability(bool canSubmit)
        {
            if (Mode != CardViewerMode.UseCharacter || requestEffect == null) return;
            submissionAvailable = canSubmit;
            plotButton.interactable = boundCanPlot && canSubmit && !pending;
            strategyButton.interactable = boundCanStrategy && canSubmit && !pending;
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
            CanSubmit = null; submissionAvailable = false;
            returnFocus = null;
            CardId = string.Empty;
            if (this == null) return;            hintDocumentView?.ClearBinding();
            DisplayedSprite = null;
            CardArtworkView.Set(cardImage, null);
            if (gameBoxBackImage != null) CardArtworkView.SetTexture(gameBoxBackImage, null);
            if (gameBoxFaceGap != null) gameBoxFaceGap.SetActive(false);
            if (gameBoxBackContain != null) gameBoxBackContain.SetActive(false);
            var frame = GameplayHudFrame.Active;
            if (frame != null) frame.HidePage(gameObject);
            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (isCurrent != null && !isCurrent()) { Dismiss(); return; }
            if (Mode == CardViewerMode.UseCharacter && CanSubmit != null)
                RefreshSubmissionAvailability(CanSubmit());
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
            if (Mode == CardViewerMode.GameBoxCharacterInspect &&
                Time.unscaledTime - gameBoxOpenedAt < gameBoxOpeningClickGuardSeconds) return;
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left &&
                (Mode != CardViewerMode.UseCharacter ||
                 !RectTransformUtility.RectangleContainsScreenPoint(window, eventData.position, eventData.pressEventCamera)))
                Close();
        }
    }
}
