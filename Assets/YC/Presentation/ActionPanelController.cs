using System;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class ActionPanelController
    {
        private readonly ActionPanelView view;
        private ActionPanelController(
            ActionPanelView view,
            CardVisualCatalog configuredCardVisualCatalog,
            Action onUseCharacter,
            Action onDeclareCityStyle,
            Action onDeploy,
            Action onDispatch,
            Action onExplore,
            Action onMoveCity,
            Action onEndRound,
            Action onBuild,
            Action onSpecial)
        {
            this.view = view;
            YC.PlayerJourney.PlayerAutomationId.Attach(view.PanelObject, "character.cover_slot");
            YC.PlayerJourney.PlayerAutomationId.Attach(view.DeployButton.gameObject, "action.deploy");
            YC.PlayerJourney.PlayerAutomationId.Attach(view.DispatchButton.gameObject, "action.dispatch");
            YC.PlayerJourney.PlayerAutomationId.Attach(view.ExploreButton.gameObject, "action.explore");
            YC.PlayerJourney.PlayerAutomationId.Attach(view.MoveCityButton.gameObject, "action.move");
            if (view.BuildButton != null)
                YC.PlayerJourney.PlayerAutomationId.Attach(view.BuildButton.gameObject, "action.build");
            YC.PlayerJourney.PlayerAutomationId.Attach(view.EndRoundButton.gameObject, "action.end");
            YC.PlayerJourney.PlayerAutomationId.Attach(view.UseCharacterButton.gameObject, "action.character");
            YC.PlayerJourney.PlayerAutomationId.Attach(view.PhaseText.gameObject, "round.current");
            BindButton(view.UseCharacterButton, onUseCharacter);
            BindButton(view.DeclareCityStyleButton, onDeclareCityStyle);
            BindButton(view.DeployButton, onDeploy);
            BindButton(view.DispatchButton, onDispatch);
            BindButton(view.ExploreButton, onExplore);
            BindButton(view.MoveCityButton, onMoveCity);
            if (view.BuildButton != null) BindButton(view.BuildButton, onBuild);
            BindButton(view.EndRoundButton, onEndRound);
            if (view.SpecialButton != null)
            {
                BindButton(view.SpecialButton, onSpecial);
                YC.PlayerJourney.PlayerAutomationId.Attach(view.SpecialButton.gameObject, "action.special");
            }
            YC.PlayerJourney.PlayerAutomationId.Attach(view.DeclareCityStyleButton.gameObject, "action.declare");
            ShowMainFace();
        }

        public bool IsReady => view != null;
        public static ActionPanelController Bind(
            ActionPanelView view,
            CardVisualCatalog cardVisualCatalog,
            Action onUseCharacter,
            Action onDeclareCityStyle,
            Action onDeploy,
            Action onDispatch,
            Action onExplore,
            Action onMoveCity,
            Action onEndRound,
            Action onBuild = null,
            Action onSpecial = null)
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
                onEndRound,
                onBuild, onSpecial);
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
            bool canEndRound,
            bool canSpecial = false)
        {
            SetButtonInteractable(view.UseCharacterButton, canUseCharacter);
            // 宣告入口是覆盖行动区的透明点击层，不能套用有色按钮底板。
            view.DeclareCityStyleButton.interactable = canDeclareCityStyle;
            SetButtonInteractable(view.DeployButton, canDeploy);
            SetButtonInteractable(view.DispatchButton, canDispatch);
            SetButtonInteractable(view.ExploreButton, canExplore);
            SetButtonInteractable(view.MoveCityButton, canMoveCity);
            if (view.BuildButton != null) SetButtonInteractable(view.BuildButton, canBuild);
            if (view.SpecialButton != null) SetButtonInteractable(view.SpecialButton, canSpecial);
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
                viewModel.CanBuild || viewModel.CanViewFacilitySupply,
                viewModel.CanEndAction, viewModel.CanUseSpecialAction);
            view.SetBuildSupplyMode(viewModel.CanViewFacilitySupply);
            SetStatus(viewModel.StatusText);
        }

        public void RenderCharacterQuickAction(CharacterCardPanelViewModel model)
        {
            var name = model == null || string.IsNullOrEmpty(model.CoveredCardId) ? string.Empty :
                CharacterCardPanelPresenter.ResolveCardDisplayName(model.CoveredCardId);
            if (model != null && model.HasPendingCharacterChoice && !string.IsNullOrEmpty(model.PendingCardDisplayName))
                name = model.PendingCardDisplayName;
            view.UseCharacterButton.GetComponent<UiQuickActionRow>()?.SetCharacterName(name);
        }

        public void ShowMainFace() => view.MainFaceObject.SetActive(true);

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
            var quickRow = button.GetComponent<UiQuickActionRow>();
            if (quickRow != null)
            {
                quickRow.SetAvailable(interactable);
                return;
            }
            var mainState = button.GetComponent<UiMainButtonState>();
            if (mainState != null)
            {
                mainState.SetAvailable(interactable);
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
