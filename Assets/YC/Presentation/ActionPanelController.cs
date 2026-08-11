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
        private readonly ActionPanelView view;
        private readonly CardVisualCatalog cardVisualCatalog;
        private Action cardPrimaryAction;
        private Action cardSecondaryAction;
        private Action openCharacterCardAction;
        private ZoomableImageViewerController hintCardImageViewer;
        private Func<bool> handleCharacterFlip;
        private ActionPanelFace currentFace;
        private string currentCharacterCardId = string.Empty;
        private string revealedCharacterCardId = string.Empty;

        private ActionPanelController(
            ActionPanelView view,
            CardVisualCatalog configuredCardVisualCatalog,
            Action onUseCharacter,
            Action onDeclareCityStyle,
            Action onDeploy,
            Action onDispatch,
            Action onExplore,
            Action onMoveCity,
            Action onEndRound)
        {
            this.view = view;
            cardVisualCatalog = configuredCardVisualCatalog;
            BindButton(view.UseCharacterButton, onUseCharacter);
            BindButton(view.DeclareCityStyleButton, onDeclareCityStyle);
            BindButton(view.DeployButton, onDeploy);
            BindButton(view.DispatchButton, onDispatch);
            BindButton(view.ExploreButton, onExplore);
            BindButton(view.MoveCityButton, onMoveCity);
            BindButton(view.EndRoundButton, onEndRound);
            BindButton(view.CardPrimaryButton, InvokeCardPrimaryAction);
            BindButton(view.CardSecondaryButton, InvokeCardSecondaryAction);
            BindButton(view.CardImageButton, InvokeCardImageAction);
            BindButton(view.FlipButton, Flip);
            ShowMainFace();
        }

        public bool IsReady => view != null;
        public ActionPanelFace CurrentFace => currentFace;

        public static ActionPanelController Bind(
            ActionPanelView view,
            CardVisualCatalog cardVisualCatalog,
            Action onUseCharacter,
            Action onDeclareCityStyle,
            Action onDeploy,
            Action onDispatch,
            Action onExplore,
            Action onMoveCity,
            Action onEndRound)
        {
            var reason = string.Empty;
            if (view == null || cardVisualCatalog == null ||
                !view.TryValidateConfiguration(out reason) ||
                !cardVisualCatalog.TryValidateConfiguration(out reason))
            {
                Debug.LogError("[ActionPanelController] 无法绑定行动面板 View：" +
                               (view == null ? "引用为空。" : reason));
                return null;
            }

            return new ActionPanelController(
                view,
                cardVisualCatalog,
                onUseCharacter,
                onDeclareCityStyle,
                onDeploy,
                onDispatch,
                onExplore,
                onMoveCity,
                onEndRound);
        }

        public void SetHeader(string currentPlayer, string phase)
        {
            view.CurrentPlayerText.text = currentPlayer ?? string.Empty;
            view.PhaseText.text = phase ?? string.Empty;
        }

        public void SetLocalPlayerColor(Color color)
        {
            view.LocalPlayerColorSwatch.color = color;
        }

        public void SetRemainingInfluence(int amount)
        {
            view.RemainingInfluenceText.text = "× " + Mathf.Max(0, amount);
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
            SetButtonInteractable(view.UseCharacterButton, canUseCharacter);
            SetButtonInteractable(view.DeclareCityStyleButton, canDeclareCityStyle);
            SetButtonInteractable(view.DeployButton, canDeploy);
            SetButtonInteractable(view.DispatchButton, canDispatch);
            SetButtonInteractable(view.ExploreButton, canExplore);
            SetButtonInteractable(view.MoveCityButton, canMoveCity);
            SetButtonInteractable(view.EndRoundButton, canEndRound);
        }

        public void SetStatus(string status)
        {
            view.StatusText.text = status ?? string.Empty;
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
            view.CardImage.texture = view.HintCardTexture;
            view.CardImage.color = Color.white;
            view.CardImageButton.interactable = true;
            view.CardPlaceholderText.gameObject.SetActive(false);
            view.CardShade.color = Color.clear;
            view.CardFaceOutline.effectColor = Color.clear;
            view.CardTitleText.text = string.Empty;
            view.CardHintText.text = string.Empty;
            view.CardPrimaryButton.gameObject.SetActive(false);
            view.CardSecondaryButton.gameObject.SetActive(false);
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
                view.CardImage.texture = null;
                view.CardImage.color = Color.clear;
                view.CardImageButton.interactable = false;
                view.CardPlaceholderText.text = "本回合已使用过角色卡";
                view.CardPlaceholderText.gameObject.SetActive(true);
                view.CardShade.color = Color.clear;
                view.CardFaceOutline.effectColor = UiTheme.GoldOutlineThin;
                view.CardTitleText.text = "角色牌";
                view.CardHintText.text = string.Empty;
                view.CardPrimaryButton.gameObject.SetActive(false);
                view.CardSecondaryButton.gameObject.SetActive(false);
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
            var isRevealed = revealedCharacterCardId == viewModel.CoveredCardId;
            view.CardImage.texture = isRevealed
                ? cardVisualCatalog.GetCharacterFront(viewModel.CoveredCardId)
                : cardVisualCatalog.GetCharacterBack(viewModel.CoveredCardBackColor);
            view.CardImage.color = Color.white;
            view.CardImageButton.interactable = view.CardImage.texture != null && openCharacterCardAction != null;
            view.CardPlaceholderText.gameObject.SetActive(false);
            view.CardShade.color = Color.clear;
            view.CardFaceOutline.effectColor = UiTheme.GoldOutlineThin;
            view.CardTitleText.text = "已盖放角色牌（" + cardName + "）";
            view.CardHintText.text = viewModel.IsSecondEffectDecision
                ? (viewModel.CanUseStrategy || viewModel.CanUseTactic
                    ? "可继续使用第二个效果；点击翻转则结束角色卡使用"
                    : "当前角色牌效果没有合法的地图目标，请点击翻转完成结算。")
                : (isRevealed
                    ? "角色牌已翻开；正在结算所选效果"
                    : "点击卡背查看正面；点击策略或计谋开始结算");
            ConfigureCardAction(view.CardPrimaryButton, view.CardPrimaryLabel, "策略", viewModel.CanUseStrategy);
            ConfigureCardAction(view.CardSecondaryButton, view.CardSecondaryLabel, "计谋", viewModel.CanUseTactic);
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

        public void ShowCharacterCoverDropZone(string cardId)
        {
            ConfigureCharacterCoverFace(cardId, false, null, null);
        }

        public void ShowCharacterCoverConfirmation(
            string cardId,
            Action confirm,
            Action cancel)
        {
            ConfigureCharacterCoverFace(cardId, true, confirm, cancel);
        }

        public bool IsPointerNearPanel(Vector2 screenPosition, float padding = 70f)
        {
            var rect = view.PanelObject.GetComponent<RectTransform>();
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
            string cardId,
            bool awaitingConfirmation,
            Action confirm,
            Action cancel)
        {
            ConfigureCharacterContainerLayout();
            view.CardImage.texture = cardVisualCatalog.GetCharacterFront(cardId);
            view.CardImage.color = Color.white;
            view.CardImageButton.interactable = false;
            view.CardPlaceholderText.gameObject.SetActive(false);
            view.CardShade.color = awaitingConfirmation
                ? new Color(0.05f, 0.18f, 0.06f, 0.18f)
                : new Color(0.08f, 0.35f, 0.12f, 0.30f);
            view.CardFaceOutline.effectColor = new Color(0.35f, 1f, 0.42f, 1f);
            view.CardTitleText.text = string.Empty;
            view.CardHintText.text = string.Empty;
            cardPrimaryAction = confirm;
            cardSecondaryAction = cancel;
            ConfigureCardAction(view.CardPrimaryButton, view.CardPrimaryLabel, "确认盖放", awaitingConfirmation && confirm != null);
            ConfigureCardAction(view.CardSecondaryButton, view.CardSecondaryLabel, "取消", awaitingConfirmation && cancel != null);
            view.CardPrimaryButton.gameObject.SetActive(awaitingConfirmation);
            view.CardSecondaryButton.gameObject.SetActive(awaitingConfirmation);
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
            view.MainFaceObject.SetActive(face == ActionPanelFace.Main);
            view.CardFaceObject.SetActive(face != ActionPanelFace.Main);
            view.FlipButton.gameObject.SetActive(true);
            view.FlipButton.transform.SetAsLastSibling();
        }

        private void ConfigureHintContainerLayout()
        {
            Stretch(view.CardImageContainer);
            Stretch(view.CardImage.rectTransform);
            view.CardImageContainerBackground.color = Color.clear;
        }

        private void ConfigureCharacterContainerLayout()
        {
            ConfigureCharacterContainerRect(view.CardImageContainer);
            StretchWithInset(view.CardImage.rectTransform, 6f);
            view.CardImageContainerBackground.color = UiTheme.ScrollBackground;
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
                string.IsNullOrEmpty(currentCharacterCardId))
            {
                return;
            }

            var frontTexture = cardVisualCatalog.GetCharacterFront(currentCharacterCardId);
            if (frontTexture == null)
            {
                return;
            }

            revealedCharacterCardId = currentCharacterCardId;
            view.CardImage.texture = frontTexture;
            view.CardHintText.text = "角色牌已翻开；正在结算所选效果";
        }

        private void ClearCharacterRevealState()
        {
            currentCharacterCardId = string.Empty;
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
            var texture = view.CardImage.texture as Texture2D;
            if (texture == null)
            {
                return;
            }

            if (hintCardImageViewer == null)
            {
                hintCardImageViewer = ZoomableImageViewerController.InstantiateRegistered(
                    view.PanelObject.transform,
                    "Hint Card Image Viewer");
                if (hintCardImageViewer == null)
                {
                    return;
                }
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

        private static void BindButton(Button button, Action action)
        {
            button.onClick.RemoveAllListeners();
            if (action != null)
            {
                button.onClick.AddListener(() => action());
            }
        }

        private static void SetButtonInteractable(Button button, bool interactable)
        {
            button.interactable = interactable;
            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = interactable ? UiTheme.ButtonBackground : UiTheme.DisabledButtonBackground;
            }
        }
    }
}
