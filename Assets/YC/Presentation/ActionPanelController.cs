using System;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal enum ActionPanelFace
    {
        Main,
        Hint,
        Character,
        CharacterCover
    }

    internal sealed class ActionPanelController
    {
        private const float ActionPanelWidth = 360f;
        private const float ActionPanelHeight = 502f;
        private const string HintCardResourcePath = "ProjectAssetLibrary/HintCards/提示卡";

        private readonly GameObject panelObject;
        private readonly GameObject mainFaceObject;
        private readonly GameObject cardFaceObject;
        private readonly RectTransform cardImageContainer;
        private readonly Image cardImageContainerBackground;
        private readonly RawImage cardImage;
        private readonly Button cardImageButton;
        private readonly Image cardShade;
        private readonly Outline cardFaceOutline;
        private readonly Text cardPlaceholderText;
        private readonly Text cardTitleText;
        private readonly Text cardHintText;
        private readonly Button cardPrimaryButton;
        private readonly Text cardPrimaryLabel;
        private readonly Button cardSecondaryButton;
        private readonly Text cardSecondaryLabel;
        private readonly Button flipButton;
        private readonly Text currentPlayerText;
        private readonly Text phaseText;
        private readonly Image localPlayerColorSwatch;
        private readonly Text remainingInfluenceText;
        private readonly Text statusText;
        private readonly Button useCharacterButton;
        private readonly Button declareCityStyleButton;
        private readonly Button deployButton;
        private readonly Button dispatchButton;
        private readonly Button exploreButton;
        private readonly Button moveCityButton;
        private readonly Button buildButton;
        private readonly Button endRoundButton;
        private Action cardPrimaryAction;
        private Action cardSecondaryAction;
        private Action openCharacterCardAction;
        private ZoomableImageViewerController hintCardImageViewer;
        private Func<bool> handleCharacterFlip;
        private ActionPanelFace currentFace;
        private string currentCharacterCardId = string.Empty;
        private string currentCharacterFrontImageRelativePath = string.Empty;
        private string revealedCharacterCardId = string.Empty;

        private ActionPanelController(
            GameObject panelObject,
            GameObject mainFaceObject,
            GameObject cardFaceObject,
            RectTransform cardImageContainer,
            Image cardImageContainerBackground,
            RawImage cardImage,
            Button cardImageButton,
            Image cardShade,
            Outline cardFaceOutline,
            Text cardPlaceholderText,
            Text cardTitleText,
            Text cardHintText,
            Button cardPrimaryButton,
            Text cardPrimaryLabel,
            Button cardSecondaryButton,
            Text cardSecondaryLabel,
            Button flipButton,
            Text currentPlayerText,
            Text phaseText,
            Image localPlayerColorSwatch,
            Text remainingInfluenceText,
            Text statusText,
            Button useCharacterButton,
            Button declareCityStyleButton,
            Button deployButton,
            Button dispatchButton,
            Button exploreButton,
            Button moveCityButton,
            Button buildButton,
            Button endRoundButton)
        {
            this.panelObject = panelObject;
            this.mainFaceObject = mainFaceObject;
            this.cardFaceObject = cardFaceObject;
            this.cardImageContainer = cardImageContainer;
            this.cardImageContainerBackground = cardImageContainerBackground;
            this.cardImage = cardImage;
            this.cardImageButton = cardImageButton;
            this.cardShade = cardShade;
            this.cardFaceOutline = cardFaceOutline;
            this.cardPlaceholderText = cardPlaceholderText;
            this.cardTitleText = cardTitleText;
            this.cardHintText = cardHintText;
            this.cardPrimaryButton = cardPrimaryButton;
            this.cardPrimaryLabel = cardPrimaryLabel;
            this.cardSecondaryButton = cardSecondaryButton;
            this.cardSecondaryLabel = cardSecondaryLabel;
            this.flipButton = flipButton;
            this.currentPlayerText = currentPlayerText;
            this.phaseText = phaseText;
            this.localPlayerColorSwatch = localPlayerColorSwatch;
            this.remainingInfluenceText = remainingInfluenceText;
            this.statusText = statusText;
            this.useCharacterButton = useCharacterButton;
            this.declareCityStyleButton = declareCityStyleButton;
            this.deployButton = deployButton;
            this.dispatchButton = dispatchButton;
            this.exploreButton = exploreButton;
            this.moveCityButton = moveCityButton;
            this.buildButton = buildButton;
            this.endRoundButton = endRoundButton;
            cardPrimaryButton.onClick.AddListener(InvokeCardPrimaryAction);
            cardSecondaryButton.onClick.AddListener(InvokeCardSecondaryAction);
            cardImageButton.onClick.AddListener(InvokeCardImageAction);
            flipButton.onClick.AddListener(Flip);
            ShowMainFace();
        }

        public bool IsReady
        {
            get { return panelObject != null; }
        }

        public ActionPanelFace CurrentFace
        {
            get { return currentFace; }
        }

        public static ActionPanelController Build(
            Canvas uiCanvas,
            Action onUseCharacter,
            Action onDeclareCityStyle,
            Action onDeploy,
            Action onDispatch,
            Action onExplore,
            Action onMoveCity,
            Action onBuild,
            Action onEndRound)
        {
            if (uiCanvas == null)
            {
                return null;
            }

            var canvasTransform = uiCanvas.GetComponent<RectTransform>();
            var panelObject = new GameObject("Action Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(canvasTransform, false);

            var panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 0f);
            panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(1f, 0f);
            panelRect.sizeDelta = new Vector2(ActionPanelWidth, ActionPanelHeight);
            panelRect.anchoredPosition = Vector2.zero;

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(3f, -3f);

            var mainFaceObject = CreateFace(panelRect, "Main Action Face");
            var mainRect = mainFaceObject.GetComponent<RectTransform>();
            // 顶部单独保留翻转行，避免与当前玩家信息重叠。
            var localPlayerColorSwatch = CreateColorSwatch(mainRect, new Vector2(-150f, -98f));
            var remainingInfluenceText = CreateText(mainRect, "× 0", 16, new Vector2(-105f, -98f), FontStyle.Bold);
            remainingInfluenceText.gameObject.name = "Remaining Influence Text";
            remainingInfluenceText.rectTransform.sizeDelta = new Vector2(64f, 30f);
            remainingInfluenceText.alignment = TextAnchor.MiddleLeft;

            var currentPlayerText = CreateText(mainRect, "当前玩家", 20, new Vector2(0f, -68f), FontStyle.Bold);
            var phaseText = CreateText(mainRect, "阶段", 16, new Vector2(0f, -98f), FontStyle.Normal);

            CreateText(mainRect, "快速行动", 16, new Vector2(0f, -130f), FontStyle.Bold);
            var useCharacterButton = CreateButton(mainRect, "使用角色牌", new Vector2(-86f, -162f), onUseCharacter);
            var declareCityStyleButton = CreateButton(mainRect, "宣告样式", new Vector2(86f, -162f), onDeclareCityStyle);

            CreateText(mainRect, "主要行动", 16, new Vector2(0f, -204f), FontStyle.Bold);
            var deployButton = CreateButton(mainRect, "部署", new Vector2(-86f, -236f), onDeploy);
            var dispatchButton = CreateButton(mainRect, "调度", new Vector2(86f, -236f), onDispatch);
            var exploreButton = CreateButton(mainRect, "探索", new Vector2(-86f, -288f), onExplore);
            var moveCityButton = CreateButton(mainRect, "城市移动", new Vector2(86f, -288f), onMoveCity);
            Button buildButton = null;
            var endRoundButton = CreateButton(mainRect, "结束本回合", new Vector2(0f, -340f), onEndRound);

            var statusText = CreateText(mainRect, "状态", 15, new Vector2(0f, -424f), FontStyle.Normal);
            statusText.rectTransform.sizeDelta = new Vector2(316f, 56f);
            statusText.resizeTextForBestFit = true;
            statusText.resizeTextMinSize = 11;
            statusText.resizeTextMaxSize = 15;

            var cardFaceObject = CreateFace(panelRect, "Action Card Face");
            var cardFaceRect = cardFaceObject.GetComponent<RectTransform>();
            var cardContainerObject = new GameObject(
                "Action Card Image Container",
                typeof(RectTransform),
                typeof(Image),
                typeof(Outline));
            cardContainerObject.transform.SetParent(cardFaceRect, false);
            var cardImageContainer = cardContainerObject.GetComponent<RectTransform>();
            ConfigureCharacterContainerRect(cardImageContainer);
            var cardImageContainerBackground = cardContainerObject.GetComponent<Image>();
            cardImageContainerBackground.color = UiTheme.ScrollBackground;
            cardImageContainerBackground.raycastTarget = false;
            var cardFaceOutline = cardContainerObject.GetComponent<Outline>();
            cardFaceOutline.effectColor = UiTheme.GoldOutlineThin;
            cardFaceOutline.effectDistance = new Vector2(2f, -2f);

            var cardImageObject = new GameObject(
                "Action Card Image",
                typeof(RectTransform),
                typeof(RawImage),
                typeof(Button));
            cardImageObject.transform.SetParent(cardImageContainer, false);
            StretchWithInset(cardImageObject.GetComponent<RectTransform>(), 6f);
            var cardImage = cardImageObject.GetComponent<RawImage>();
            cardImage.color = Color.white;
            cardImage.raycastTarget = true;
            var cardImageButton = cardImageObject.GetComponent<Button>();
            cardImageButton.transition = Selectable.Transition.None;
            cardImageButton.interactable = false;

            var shadeObject = new GameObject("Action Card Shade", typeof(RectTransform), typeof(Image));
            shadeObject.transform.SetParent(cardImageContainer, false);
            Stretch(shadeObject.GetComponent<RectTransform>());
            var cardShade = shadeObject.GetComponent<Image>();
            cardShade.color = new Color(0f, 0f, 0f, 0f);
            cardShade.raycastTarget = false;

            var placeholderObject = new GameObject(
                "Action Card Empty Message",
                typeof(RectTransform),
                typeof(Text));
            placeholderObject.transform.SetParent(cardImageContainer, false);
            StretchWithInset(placeholderObject.GetComponent<RectTransform>(), 14f);
            var cardPlaceholderText = placeholderObject.GetComponent<Text>();
            cardPlaceholderText.text = string.Empty;
            cardPlaceholderText.alignment = TextAnchor.MiddleCenter;
            cardPlaceholderText.color = UiTheme.GoldText;
            cardPlaceholderText.fontSize = 18;
            cardPlaceholderText.fontStyle = FontStyle.Bold;
            cardPlaceholderText.font = FontUtility.GetCjkFont(18);
            cardPlaceholderText.horizontalOverflow = HorizontalWrapMode.Wrap;
            cardPlaceholderText.verticalOverflow = VerticalWrapMode.Truncate;
            placeholderObject.SetActive(false);

            var cardTitleText = CreateText(cardFaceRect, string.Empty, 19, new Vector2(0f, -28f), FontStyle.Bold);
            cardTitleText.gameObject.name = "Action Card Title";
            cardTitleText.color = Color.white;
            var cardHintText = CreateText(cardFaceRect, string.Empty, 15, new Vector2(0f, -382f), FontStyle.Bold);
            cardHintText.gameObject.name = "Action Card Hint";
            cardHintText.rectTransform.sizeDelta = new Vector2(316f, 58f);
            cardHintText.color = Color.white;

            var cardPrimaryButton = CreateButton(cardFaceRect, "策略", new Vector2(-86f, -454f), null);
            cardPrimaryButton.gameObject.name = "Character Strategy Button";
            var cardPrimaryLabel = cardPrimaryButton.transform.Find("Label").GetComponent<Text>();
            var cardSecondaryButton = CreateButton(cardFaceRect, "计谋", new Vector2(86f, -454f), null);
            cardSecondaryButton.gameObject.name = "Character Tactic Button";
            var cardSecondaryLabel = cardSecondaryButton.transform.Find("Label").GetComponent<Text>();

            var flipButton = CreateButton(
                panelRect,
                "翻转",
                new Vector2(132f, -24f),
                null,
                new Vector2(74f, 32f),
                14);
            flipButton.gameObject.name = "Action Panel Flip Button";
            flipButton.transform.SetAsLastSibling();

            var controller = new ActionPanelController(
                panelObject,
                mainFaceObject,
                cardFaceObject,
                cardImageContainer,
                cardImageContainerBackground,
                cardImage,
                cardImageButton,
                cardShade,
                cardFaceOutline,
                cardPlaceholderText,
                cardTitleText,
                cardHintText,
                cardPrimaryButton,
                cardPrimaryLabel,
                cardSecondaryButton,
                cardSecondaryLabel,
                flipButton,
                currentPlayerText,
                phaseText,
                localPlayerColorSwatch,
                remainingInfluenceText,
                statusText,
                useCharacterButton,
                declareCityStyleButton,
                deployButton,
                dispatchButton,
                exploreButton,
                moveCityButton,
                buildButton,
                endRoundButton);
            return controller;
        }

        public void SetHeader(string currentPlayer, string phase)
        {
            currentPlayerText.text = currentPlayer ?? string.Empty;
            phaseText.text = phase ?? string.Empty;
        }

        public void SetLocalPlayerColor(Color color)
        {
            if (localPlayerColorSwatch != null)
            {
                localPlayerColorSwatch.color = color;
            }
        }

        public void SetRemainingInfluence(int amount)
        {
            if (remainingInfluenceText != null)
            {
                remainingInfluenceText.text = "× " + Mathf.Max(0, amount);
            }
        }

        public void SetButtonStates(
            bool canUseCharacter,
            bool canDeclareCityStyle,
            bool canDeploy,
            bool canDispatch,
            bool canExplore,
            bool canMoveCity,
            bool canBuild,
            bool canEndRound)
        {
            SetButtonInteractable(useCharacterButton, canUseCharacter);
            SetButtonInteractable(declareCityStyleButton, canDeclareCityStyle);
            SetButtonInteractable(deployButton, canDeploy);
            SetButtonInteractable(dispatchButton, canDispatch);
            SetButtonInteractable(exploreButton, canExplore);
            SetButtonInteractable(moveCityButton, canMoveCity);
            SetButtonInteractable(buildButton, canBuild);
            SetButtonInteractable(endRoundButton, canEndRound);
        }

        public void SetStatus(string status)
        {
            statusText.text = status ?? string.Empty;
        }

        public void Render(ActionPanelViewModel viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            SetHeader(viewModel.CurrentPlayerLabel, viewModel.PhaseLabel);
            SetLocalPlayerColor(viewModel.HasLocalPlayer
                ? UiTheme.GetPlayerColor(viewModel.LocalPlayerColor, 1f)
                : Color.white);
            SetRemainingInfluence(viewModel.RemainingInfluence);
            SetButtonStates(
                viewModel.CanUseCharacter,
                viewModel.CanDeclareCityStyle,
                viewModel.CanDeploy,
                viewModel.CanDispatch,
                viewModel.CanExplore,
                viewModel.CanMoveCity,
                viewModel.CanBuild,
                viewModel.CanEndAction);
            SetStatus(viewModel.StatusText);
        }

        public void ShowMainFace()
        {
            SetFace(ActionPanelFace.Main);
        }

        public void ShowHintFace()
        {
            ConfigureHintContainerLayout();
            cardImage.texture = Resources.Load<Texture2D>(HintCardResourcePath);
            cardImage.color = Color.white;
            cardImageButton.interactable = cardImage.texture != null;
            cardPlaceholderText.gameObject.SetActive(false);
            cardShade.color = new Color(0f, 0f, 0f, 0f);
            cardFaceOutline.effectColor = new Color(0f, 0f, 0f, 0f);
            cardTitleText.text = string.Empty;
            cardHintText.text = string.Empty;
            cardPrimaryButton.gameObject.SetActive(false);
            cardSecondaryButton.gameObject.SetActive(false);
            SetFace(ActionPanelFace.Hint);
        }

        public void ShowCharacterCard(CharacterCardPanelViewModel viewModel)
        {
            if (viewModel == null)
            {
                ShowMainFace();
                return;
            }

            ConfigureCharacterContainerLayout();
            if (viewModel.UsedCharacterThisRound && string.IsNullOrEmpty(viewModel.CoveredCardId))
            {
                ClearCharacterRevealState();
                cardImage.texture = null;
                cardImage.color = Color.clear;
                cardImageButton.interactable = false;
                cardPlaceholderText.text = "本回合已使用过角色卡";
                cardPlaceholderText.gameObject.SetActive(true);
                cardShade.color = new Color(0f, 0f, 0f, 0f);
                cardFaceOutline.effectColor = UiTheme.GoldOutlineThin;
                cardTitleText.text = "角色牌";
                cardHintText.text = string.Empty;
                cardPrimaryButton.gameObject.SetActive(false);
                cardSecondaryButton.gameObject.SetActive(false);
                SetFace(ActionPanelFace.Character);
                return;
            }

            if (string.IsNullOrEmpty(viewModel.CoveredCardId))
            {
                ShowMainFace();
                return;
            }

            var cardName = CharacterCardPanelPresenter.ResolveCardDisplayName(viewModel.CoveredCardId);
            currentCharacterCardId = viewModel.CoveredCardId;
            currentCharacterFrontImageRelativePath = viewModel.CoveredFrontImageRelativePath;
            var isRevealed = revealedCharacterCardId == viewModel.CoveredCardId;
            cardImage.texture = LoadTexture(isRevealed
                ? viewModel.CoveredFrontImageRelativePath
                : viewModel.CoveredBackImageRelativePath);
            cardImage.color = Color.white;
            cardImageButton.interactable = cardImage.texture != null && openCharacterCardAction != null;
            cardPlaceholderText.gameObject.SetActive(false);
            cardShade.color = new Color(0f, 0f, 0f, 0f);
            cardFaceOutline.effectColor = UiTheme.GoldOutlineThin;
            cardTitleText.text = "已盖放角色牌（" + cardName + "）";
            cardHintText.text = viewModel.IsSecondEffectDecision
                ? (viewModel.CanUseStrategy || viewModel.CanUseTactic
                    ? "可继续使用第二个效果；点击翻转则结束角色卡使用"
                    : "当前角色牌效果没有合法的地图目标，请点击翻转完成结算。")
                : (isRevealed
                    ? "角色牌已翻开；正在结算所选效果"
                    : "点击卡背查看正面；点击策略或计谋开始结算");
            ConfigureCardAction(cardPrimaryButton, cardPrimaryLabel, "策略", viewModel.CanUseStrategy);
            ConfigureCardAction(cardSecondaryButton, cardSecondaryLabel, "计谋", viewModel.CanUseTactic);
            SetFace(ActionPanelFace.Character);
        }

        public void ConfigureCharacterActions(Action strategy, Action tactic)
        {
            cardPrimaryAction = strategy;
            cardSecondaryAction = tactic;
        }

        public void ConfigureCharacterCardViewerAction(Action openCharacterCard)
        {
            openCharacterCardAction = openCharacterCard;
        }

        public void ConfigureCharacterFlipAction(Func<bool> characterFlipHandler)
        {
            handleCharacterFlip = characterFlipHandler;
        }

        public void ResetCharacterCardReveal()
        {
            ClearCharacterRevealState();
        }

        public void ShowCharacterCoverDropZone(string imageRelativePath)
        {
            ConfigureCharacterCoverFace(imageRelativePath, false, null, null);
        }

        public void ShowCharacterCoverConfirmation(
            string imageRelativePath,
            Action confirm,
            Action cancel)
        {
            ConfigureCharacterCoverFace(imageRelativePath, true, confirm, cancel);
        }

        public bool IsPointerNearPanel(Vector2 screenPosition, float padding = 70f)
        {
            var rect = panelObject == null ? null : panelObject.GetComponent<RectTransform>();
            if (rect == null)
            {
                return false;
            }

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            var max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            var screenRect = Rect.MinMaxRect(
                Mathf.Min(min.x, max.x) - padding,
                Mathf.Min(min.y, max.y) - padding,
                Mathf.Max(min.x, max.x) + padding,
                Mathf.Max(min.y, max.y) + padding);
            return screenRect.Contains(screenPosition);
        }

        private void ConfigureCharacterCoverFace(
            string imageRelativePath,
            bool awaitingConfirmation,
            Action confirm,
            Action cancel)
        {
            ConfigureCharacterContainerLayout();
            cardImage.texture = LoadTexture(imageRelativePath);
            cardImage.color = Color.white;
            cardImageButton.interactable = false;
            cardPlaceholderText.gameObject.SetActive(false);
            cardShade.color = awaitingConfirmation
                ? new Color(0.05f, 0.18f, 0.06f, 0.18f)
                : new Color(0.08f, 0.35f, 0.12f, 0.30f);
            cardFaceOutline.effectColor = new Color(0.35f, 1f, 0.42f, 1f);
            cardTitleText.text = string.Empty;
            cardHintText.text = string.Empty;
            cardPrimaryAction = confirm;
            cardSecondaryAction = cancel;
            ConfigureCardAction(cardPrimaryButton, cardPrimaryLabel, "确认盖放", awaitingConfirmation && confirm != null);
            ConfigureCardAction(cardSecondaryButton, cardSecondaryLabel, "取消", awaitingConfirmation && cancel != null);
            cardPrimaryButton.gameObject.SetActive(awaitingConfirmation);
            cardSecondaryButton.gameObject.SetActive(awaitingConfirmation);
            SetFace(ActionPanelFace.CharacterCover);
        }

        private void Flip()
        {
            if (currentFace == ActionPanelFace.Character &&
                handleCharacterFlip != null &&
                handleCharacterFlip())
            {
                ShowMainFace();
                return;
            }

            if (currentFace == ActionPanelFace.Main)
            {
                ShowHintFace();
            }
            else
            {
                ShowMainFace();
            }
        }

        private void SetFace(ActionPanelFace face)
        {
            currentFace = face;
            mainFaceObject.SetActive(face == ActionPanelFace.Main);
            cardFaceObject.SetActive(face != ActionPanelFace.Main);
            flipButton.gameObject.SetActive(true);
            flipButton.transform.SetAsLastSibling();
        }

        private void ConfigureHintContainerLayout()
        {
            Stretch(cardImageContainer);
            Stretch(cardImage.rectTransform);
            cardImageContainerBackground.color = Color.clear;
        }

        private void ConfigureCharacterContainerLayout()
        {
            ConfigureCharacterContainerRect(cardImageContainer);
            StretchWithInset(cardImage.rectTransform, 6f);
            cardImageContainerBackground.color = UiTheme.ScrollBackground;
        }

        private void InvokeCardPrimaryAction()
        {
            RevealCharacterCard();
            cardPrimaryAction?.Invoke();
        }

        private void InvokeCardSecondaryAction()
        {
            RevealCharacterCard();
            cardSecondaryAction?.Invoke();
        }

        private void RevealCharacterCard()
        {
            if (currentFace != ActionPanelFace.Character ||
                string.IsNullOrEmpty(currentCharacterCardId) ||
                string.IsNullOrEmpty(currentCharacterFrontImageRelativePath))
            {
                return;
            }

            var frontTexture = LoadTexture(currentCharacterFrontImageRelativePath);
            if (frontTexture == null)
            {
                return;
            }

            revealedCharacterCardId = currentCharacterCardId;
            cardImage.texture = frontTexture;
            cardHintText.text = "角色牌已翻开；正在结算所选效果";
        }

        private void ClearCharacterRevealState()
        {
            currentCharacterCardId = string.Empty;
            currentCharacterFrontImageRelativePath = string.Empty;
            revealedCharacterCardId = string.Empty;
        }

        private void InvokeCardImageAction()
        {
            if (currentFace == ActionPanelFace.Hint)
            {
                OpenHintCardViewer();
                return;
            }

            if (currentFace == ActionPanelFace.Character)
            {
                openCharacterCardAction?.Invoke();
            }
        }

        private void OpenHintCardViewer()
        {
            var texture = cardImage.texture as Texture2D;
            if (texture == null)
            {
                return;
            }

            if (hintCardImageViewer == null)
            {
                var viewerObject = new GameObject("Hint Card Image Viewer");
                hintCardImageViewer = viewerObject.AddComponent<ZoomableImageViewerController>();
            }

            hintCardImageViewer.DisableReferenceCollapse();
            hintCardImageViewer.Configure("Hint Card", "提示卡", 1, _ => texture);
            hintCardImageViewer.ConfigureActions(string.Empty, null);
            hintCardImageViewer.Open();
        }

        private static void ConfigureCardAction(Button button, Text label, string text, bool interactable)
        {
            button.gameObject.SetActive(true);
            label.text = text;
            SetButtonInteractable(button, interactable);
        }

        private static Texture2D LoadTexture(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            var normalized = relativePath.Replace('\\', '/');
            const string marker = "/Resources/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var resourcePath = normalized.Substring(markerIndex + marker.Length);
            var extensionIndex = resourcePath.LastIndexOf('.');
            if (extensionIndex > 0)
            {
                resourcePath = resourcePath.Substring(0, extensionIndex);
            }

            return Resources.Load<Texture2D>(resourcePath);
        }

        private static GameObject CreateFace(RectTransform parent, string name)
        {
            var face = new GameObject(name, typeof(RectTransform));
            face.transform.SetParent(parent, false);
            Stretch(face.GetComponent<RectTransform>());
            return face;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void StretchWithInset(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private static void ConfigureCharacterContainerRect(RectTransform rect)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(222f, 310f);
            rect.anchoredPosition = new Vector2(0f, -58f);
        }

        private static Text CreateText(RectTransform parent, string value, int size, Vector2 position, FontStyle style)
        {
            var textObject = new GameObject(value + " Text", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(316f, 30f);
            rect.anchoredPosition = position;

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = size;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(size);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Button CreateButton(
            RectTransform parent,
            string label,
            Vector2 position,
            Action action,
            Vector2? size = null,
            int fontSize = 16)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size ?? new Vector2(150f, 42f);
            rect.anchoredPosition = position;

            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            var button = buttonObject.GetComponent<Button>();
            if (action != null)
            {
                button.onClick.AddListener(() => action());
            }
            buttonObject.AddComponent<ActionButtonPressFeedback>().Configure(button, outline);

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);

            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 0f);
            labelRect.offsetMax = new Vector2(-8f, 0f);

            var text = labelObject.GetComponent<Text>();
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = UiTheme.GoldText;
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 11;
            text.resizeTextMaxSize = fontSize;
            return button;
        }

        private static Image CreateColorSwatch(RectTransform parent, Vector2 position)
        {
            var swatchObject = new GameObject("Local Player Color Swatch", typeof(RectTransform), typeof(Image), typeof(Outline));
            swatchObject.transform.SetParent(parent, false);

            var rect = swatchObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(22f, 22f);
            rect.anchoredPosition = position;

            var image = swatchObject.GetComponent<Image>();
            image.color = Color.white;

            var outline = swatchObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            return image;
        }

        private static void SetButtonInteractable(Button button, bool interactable)
        {
            if (button == null)
            {
                return;
            }

            button.interactable = interactable;
            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = interactable ? UiTheme.ButtonBackground : UiTheme.DisabledButtonBackground;
            }
        }
    }
}
