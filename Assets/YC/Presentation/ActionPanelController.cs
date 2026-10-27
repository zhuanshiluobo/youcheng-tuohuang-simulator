using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class ActionPanelController
    {
        private readonly ActionPanelView view;
        private readonly Action<string> useCharacter;
        private readonly Dictionary<string, UiQuickActionRow> characterRows = new Dictionary<string, UiQuickActionRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> characterAvailability = new Dictionary<string, bool>(StringComparer.Ordinal);
        public Func<bool> CanSubmitCharacter { private get; set; }
        public Func<bool> CanBeginAction { private get; set; }
        public Func<IDisposable> BeginInputRead { private get; set; }
        public Action<YC.Domain.Effects.AbilityActionOption> DeclaredActionRequested { private get; set; }
        public Func<YC.Domain.Presentation.GameplayTextRef, YC.Domain.Presentation.GameplayTextDocument> ResolveActionText { private get; set; }
        public Action<Component, YC.Domain.Presentation.GameplayTextDocument, bool> RenderActionText { private get; set; }
        private ActionPanelController(
            ActionPanelView view,
            CardVisualCatalog configuredCardVisualCatalog,
            Action<string> onUseCharacter,
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
            useCharacter = onUseCharacter;
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
            view.UseCharacterButton.onClick.RemoveAllListeners();
            BindButton(view.DeclareCityStyleButton, onDeclareCityStyle);
            BindButton(view.DeployButton, GuardAction(onDeploy));
            BindButton(view.DispatchButton, GuardAction(onDispatch));
            BindButton(view.ExploreButton, GuardAction(onExplore));
            BindButton(view.MoveCityButton, GuardAction(onMoveCity));
            if (view.BuildButton != null) BindButton(view.BuildButton, onBuild);
            BindButton(view.EndRoundButton, onEndRound);
            if (view.SpecialButton != null)
            {
                BindButton(view.SpecialButton, GuardAction(onSpecial));
                YC.PlayerJourney.PlayerAutomationId.Attach(view.SpecialButton.gameObject, "action.special");
            }
            YC.PlayerJourney.PlayerAutomationId.Attach(view.DeclareCityStyleButton.gameObject, "action.declare");
            ShowMainFace();
        }

        public bool IsReady => view != null;
        public static ActionPanelController Bind(
            ActionPanelView view,
            CardVisualCatalog cardVisualCatalog,
            Action<string> onUseCharacter,
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
            bool canBegin = CanBeginAction?.Invoke() != false;
            canUseCharacter &= canBegin;
            canDeploy &= canBegin; canDispatch &= canBegin; canExplore &= canBegin;
            canMoveCity &= canBegin; canSpecial &= canBegin;
            SetButtonInteractable(view.UseCharacterButton, canUseCharacter);
            RefreshCharacterRowsPermission();
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
                viewModel.CanInspectCityStyles,
                viewModel.CanDeploy,
                viewModel.CanDispatch,
                viewModel.CanExplore,
                viewModel.CanMoveCity,
                viewModel.CanBuild || viewModel.CanViewFacilitySupply,
                viewModel.CanEndAction, viewModel.CanUseSpecialAction);
            view.SetBuildSupplyMode(viewModel.CanViewFacilitySupply);
            SetStatus(viewModel.StatusText + (viewModel.AllCollectionLocationsSelected
                ? "\n" + view.AllCollectionLocationsSelectedText : string.Empty));
        }

        public void RenderCharacterQuickAction(CharacterCardPanelViewModel model)
        {
            if (model?.HasAbilityActionCatalog == true) { RenderDeclaredQuickActions(model.DeclaredQuickActions); return; }
            var template = view.UseCharacterButton.GetComponent<UiQuickActionRow>();
            if (template == null) throw new InvalidOperationException("既有角色快捷行缺少UiQuickActionRow。");
            template.gameObject.SetActive(false);
            var present = new HashSet<string>(StringComparer.Ordinal);
            if (model?.CoveredCards != null)
            {
                foreach (var card in model.CoveredCards)
                {
                    var id = card.CardId;
                    if (string.IsNullOrEmpty(id) || !present.Add(id)) continue;
                    if (!characterRows.TryGetValue(id, out var row))
                    {
                        row = UnityEngine.Object.Instantiate(template, template.transform.parent, false);
                        characterRows.Add(id, row);
                        row.Bind(string.Empty, false, () => {
                            using var inputRead = BeginInputRead?.Invoke();
                            using var input = UiRegressionMetrics.Measure("action.character.click");
                            if (CanBeginAction?.Invoke() != false && CanSubmitCharacter?.Invoke() != false) useCharacter?.Invoke(id);
                        });
                        YC.PlayerJourney.PlayerAutomationId.Attach(row.gameObject, "action.character." + id);
                    }
                    row.SetCharacterName(card.DisplayName);
                    characterAvailability[id] = card.CanUse;
                    row.gameObject.SetActive(true);
                    row.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + present.Count);
                }
            }
            foreach (var id in new List<string>(characterRows.Keys))
                if (!present.Contains(id))
                { EnterpriseBoardItemView.Retire(characterRows[id].gameObject); characterRows.Remove(id); characterAvailability.Remove(id); }
            RefreshCharacterRowsPermission();
        }

        private void RenderDeclaredQuickActions(IReadOnlyList<YC.Domain.Effects.AbilityActionOption> options)
        {
            var template = view.UseCharacterButton.GetComponent<UiQuickActionRow>(); template.gameObject.SetActive(false);
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                if (!present.Add(option.OptionId)) continue;
                if (!characterRows.TryGetValue(option.OptionId, out var row))
                { row = UnityEngine.Object.Instantiate(template, template.transform.parent, false); characterRows.Add(option.OptionId, row); }
                var current = option;
                row.Bind(option.DisplayName + (option.Enabled ? "" : "\n" + YC.Domain.Effects.AbilityActionReasons.Display(option.DisabledReason)),
                    option.Enabled && CanBeginAction?.Invoke() != false && CanSubmitCharacter?.Invoke() != false, () => {
                        using var inputRead = BeginInputRead?.Invoke();
                        using var input = UiRegressionMetrics.Measure("action.declared.click");
                        DeclaredActionRequested?.Invoke(current);
                    });
                row.BindGameplayText(YC.Application.Presentation.AbilityActionTextDocuments.Name(option, ResolveActionText,
                    option.Enabled ? "" : "\n" + YC.Domain.Effects.AbilityActionReasons.Display(option.DisabledReason)), RenderActionText);
                characterAvailability[option.OptionId] = option.Enabled;
                row.gameObject.SetActive(true); row.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + present.Count);
                YC.PlayerJourney.PlayerAutomationId.Attach(row.gameObject, "action.declared." + option.OptionId);
            }
            foreach (string id in new List<string>(characterRows.Keys)) if (!present.Contains(id))
            { EnterpriseBoardItemView.Retire(characterRows[id].gameObject); characterRows.Remove(id); characterAvailability.Remove(id); }
        }

        private void RefreshCharacterRowsPermission()
        {
            bool allowed = CanBeginAction?.Invoke() != false && CanSubmitCharacter?.Invoke() != false;
            foreach (var row in characterRows) row.Value.SetAvailable(allowed && characterAvailability[row.Key]);
        }

        public void ShowMainFace() => view.MainFaceObject.SetActive(true);

        private Action GuardAction(Action action) => action == null ? null : (Action)(() =>
        { if (CanBeginAction?.Invoke() != false) action(); });

        private void BindButton(Button button, Action action)
        {
            button.onClick.RemoveAllListeners();
            if (action != null)
            {
                button.onClick.AddListener(() => {
                    using var inputRead = BeginInputRead?.Invoke();
                    using var input = UiRegressionMetrics.Measure("action.button.click");
                    action();
                });
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
