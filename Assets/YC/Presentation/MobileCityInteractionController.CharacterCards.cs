using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private CharacterCardEffectChoiceDialog characterCardEffectChoiceDialog;
        private CharacterCardEffectInteractionUiCoordinator characterCardEffectInteraction;
        private string automaticSecondEffectMode = string.Empty;
        private bool submittingSecondEffectDecision;

        private void BuildCharacterCardEffectInteraction()
        {
            characterCardEffectChoiceDialog = new CharacterCardEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry,
                GetUiCanvasTransform());
            characterCardEffectInteraction = new CharacterCardEffectInteractionUiCoordinator(
                () => session == null ? null : session.State,
                () => localPlayerId,
                characterCardPresenter,
                characterCardEffectChoiceDialog,
                (mode, effect) => characterMapInteraction != null && characterMapInteraction.TryBeginEffect(mode, effect),
                () => characterMapInteraction != null && characterMapInteraction.TryBeginTinManPendingMove(),
                (facilityIds, select, cancel) => buildInfoPanel != null &&
                                                 buildInfoPanel.BeginFacilityEffectSelection(facilityIds, select, cancel),
                () => buildInfoPanel?.EndFacilityEffectSelection(),
                (mode, parameters) => SubmitUseCharacterCard(mode, string.Empty, parameters),
                SubmitResolvePendingCharacterChoice,
                SetPrompt);
        }

        private void DisposeCharacterCardEffectInteraction()
        {
            characterCardEffectInteraction?.HideDialog();
            characterCardEffectInteraction = null;
        }

        private bool TryCancelCharacterFacilityEffectSelection()
        {
            if (buildInfoPanel == null || !buildInfoPanel.TryCancelFacilityEffectSelection())
            {
                return false;
            }

            return true;
        }

        private void OnUseCharacterActionClicked()
        {
            var view = characterCardPresenter == null
                ? null
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            if (view == null || !view.CanUse)
            {
                SetPrompt(view == null ? "角色牌信息尚未初始化。" : view.InteractionStatus);
                return;
            }

            showCharacterUseOptions = false;
            characterSettlementInProgress = true;
            characterCardEffectInteraction?.HideDialog();
            characterMapInteraction?.Cancel();
            actionPanel.ResetCharacterCardReveal();
            actionPanel.ConfigureCharacterActions(OnCharacterStrategyClicked, OnCharacterTacticClicked);
            actionPanel.ShowCharacterCard(view);
        }

        private void SubmitSecondEffectDecision(bool continueSecondEffect)
        {
            if (!continueSecondEffect)
            {
                automaticSecondEffectMode = string.Empty;
            }
            showCharacterUseOptions = continueSecondEffect;
            var parameters = new Dictionary<string, string>
            {
                [CharacterEffectParameterKeys.Choice] = continueSecondEffect
                    ? CharacterEffectChoiceIds.ContinueSecondEffect
                    : CharacterEffectChoiceIds.FinishCharacterUse
            };
            var command = characterCardPresenter.CreateResolvePendingCommand(localPlayerId, parameters);
            SubmitCharacterCardCommand(
                command,
                continueSecondEffect ? "请选择并结算第二个角色牌效果。" : "角色牌使用已结束。",
                "第二效果选择已发送给主机，等待确认。");
        }

        private void ContinueWithSecondEffect(string effectMode)
        {
            automaticSecondEffectMode = effectMode ?? string.Empty;
            submittingSecondEffectDecision = true;
            try
            {
                SubmitSecondEffectDecision(true);
            }
            finally
            {
                submittingSecondEffectDecision = false;
            }

            TryBeginAutomaticSecondEffect();
        }

        private void TryBeginAutomaticSecondEffect()
        {
            if (submittingSecondEffectDecision || string.IsNullOrEmpty(automaticSecondEffectMode))
            {
                return;
            }

            var view = characterCardPresenter == null || session == null
                ? null
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            if (view == null || !view.IsSecondEffectExecution)
            {
                if (view == null || !view.IsSecondEffectDecision || IsSecondEffectUnavailable())
                {
                    automaticSecondEffectMode = string.Empty;
                }
                return;
            }

            var effectMode = automaticSecondEffectMode;
            automaticSecondEffectMode = string.Empty;
            BeginCharacterEffect(effectMode);
        }

        private bool IsSecondEffectUnavailable()
        {
            var pending = session?.State?.PendingCharacterEffect;
            return pending != null && pending.IsValid() &&
                   pending.PlayerId == localPlayerId && pending.ChoiceType == CharacterPendingChoiceTypes.SecondEffectDecision &&
                   !pending.OptionIds.Contains(CharacterEffectChoiceIds.ContinueSecondEffect);
        }

        private bool FinishCharacterUseOnFlip()
        {
            var view = characterCardPresenter == null || session == null
                ? null
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            if (view == null || !view.IsSecondEffectDecision)
            {
                return false;
            }

            automaticSecondEffectMode = string.Empty;
            SubmitSecondEffectDecision(false);
            return true;
        }

        private void OnCharacterStrategyClicked()
        {
            BeginCharacterEffect(UseCharacterCardCommandHandler.Strategy);
        }

        private void OnCharacterTacticClicked()
        {
            BeginCharacterEffect(UseCharacterCardCommandHandler.Tactic);
        }

        private void BeginCharacterEffect(string effectMode)
        {
            var view = characterCardPresenter == null
                ? null
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            var useStrategy = effectMode == UseCharacterCardCommandHandler.Strategy;
            var canUse = view != null && (useStrategy ? view.CanUseStrategy : view.CanUseTactic);
            if (!canUse)
            {
                SetPrompt(view == null ? "角色牌信息尚未初始化。" : "当前不能发动该角色牌效果。");
                return;
            }

            if (view.IsSecondEffectDecision)
            {
                ContinueWithSecondEffect(effectMode);
                return;
            }

            characterSettlementInProgress = true;
            actionPanel.ConfigureCharacterActions(OnCharacterStrategyClicked, OnCharacterTacticClicked);
            actionPanel.ShowCharacterCard(view);
            var effect = useStrategy ? view.StrategyEffect : view.TacticEffect;
            if (characterCardEffectInteraction != null &&
                characterCardEffectInteraction.TryBeginEffect(effectMode, effect))
            {
                return;
            }

            SetPrompt("该角色牌效果尚未接入结算流程。");
        }

    }
}
