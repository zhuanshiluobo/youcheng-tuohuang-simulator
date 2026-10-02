using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private CharacterCardEffectChoiceDialog characterCardEffectChoiceDialog;
        private CharacterCardEffectChoiceDialog characterCoverChoiceDialog;
        private CharacterCardEffectInteractionUiCoordinator characterCardEffectInteraction; private CharacterAbilityInteractionUiCoordinator characterAbilityInteraction;
        private string automaticSecondEffectMode = string.Empty;
        private bool submittingSecondEffectDecision;
        private CardViewer characterViewer;
        private string characterUseCommandId = string.Empty;

        private void NotifyCharacterUseSettled(string commandId)
        {
            if (string.IsNullOrEmpty(commandId) || commandId == characterUseCommandId)
                characterUseCommandId = string.Empty;
        }
        private readonly List<string> coverCandidates = new List<string>();
        private string selectedCoverCardId = string.Empty;
        private string coverCandidateKey = string.Empty;
        private bool coverSubmissionInFlight;
        private string submittedCoverCardId = string.Empty;
        private string submittedCoverCommandId = string.Empty;
        private int coverContextVersion;

        private void NotifyCoverCommandSettled(string commandId)
        {
            // 空 ID 是权威初始快照；普通回包只能解除它自己的提交锁。
            if (!string.IsNullOrEmpty(commandId) && commandId != submittedCoverCommandId) return;
            if (!coverSubmissionInFlight && !string.IsNullOrEmpty(commandId)) return;
            coverSubmissionInFlight = false;
            submittedCoverCardId = string.Empty;
            submittedCoverCommandId = string.Empty;
            coverCandidateKey = string.Empty;
            coverContextVersion++;
        }

        private void BuildCharacterCardEffectInteraction()
        {
            characterCardEffectChoiceDialog = new CharacterCardEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry,
                GetUiCanvasTransform());
            // 盖放是回合阶段页面，不能与会在旧效果结束时被 Clear 的结算弹窗共用实例。
            characterCoverChoiceDialog = new CharacterCardEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry,
                GetUiCanvasTransform());
            characterCardEffectInteraction = new CharacterCardEffectInteractionUiCoordinator(
                () => session == null ? null : session.State,
                () => localPlayerId,
                characterCardPresenter,
                characterCardEffectChoiceDialog,
                () => characterMapInteraction != null && characterMapInteraction.TryBeginTinManPendingMove(),
                () => buildInfoPanel?.EndFacilityEffectSelection(),
                (mode, parameters) => SubmitUseCharacterCard(mode, string.Empty, parameters),
                SubmitResolvePendingCharacterChoice,
                SetPrompt);
            characterAbilityInteraction = new CharacterAbilityInteractionUiCoordinator(() => session == null ? null : session.State, () => localPlayerId, characterCardEffectChoiceDialog, highlights => workflowView.SetHighlights(highlights), () => workflowView.ClearHighlights(), SubmitPendingEffectCommand, SetPrompt);
            characterAbilityInteraction.GetVisibleState = VisibleSaleState;
        }

        private void DisposeCharacterCardEffectInteraction()
        {
            characterCoverChoiceDialog?.Hide();
            characterCoverChoiceDialog = null;
            characterCardEffectInteraction?.HideDialog(); characterAbilityInteraction?.Dispose();
            characterCardEffectInteraction = null; characterAbilityInteraction = null;
        }

        private void RefreshCharacterCoverSelection(CharacterCardPanelViewModel model)
        {
            var state = session == null ? null : session.State;
            var eligible = state != null && state.Phase == GamePhase.CharacterCover &&
                           state.CurrentPlayerId == localPlayerId && model != null && model.CanCover;
            if (!eligible)
            {
                if (coverCandidateKey.Length > 0) characterCoverChoiceDialog?.Hide();
                if (coverCandidateKey.Length > 0 || coverSubmissionInFlight) coverContextVersion++;
                coverSubmissionInFlight = false;
                submittedCoverCardId = string.Empty;
                submittedCoverCommandId = string.Empty;
                coverCandidateKey = string.Empty;
                selectedCoverCardId = string.Empty;
                coverCandidates.Clear();
                return;
            }

            coverCandidates.Clear();
            if (model.HandCards != null)
                foreach (var card in model.HandCards)
                    if (card != null && card.CanCover && !string.IsNullOrEmpty(card.CardId) &&
                        !coverCandidates.Contains(card.CardId)) coverCandidates.Add(card.CardId);

            var nextKey = state.GameId + ":" + state.Round + ":" + localPlayerId + ":" +
                          string.Join("|", coverCandidates);
            if (coverSubmissionInFlight)
            {
                if (nextKey == coverCandidateKey && coverCandidates.Contains(submittedCoverCardId)) return;
                coverSubmissionInFlight = false;
                submittedCoverCardId = string.Empty;
                submittedCoverCommandId = string.Empty;
                selectedCoverCardId = string.Empty;
            }
            if (!coverCandidates.Contains(selectedCoverCardId)) selectedCoverCardId = string.Empty;
            if (nextKey == coverCandidateKey && characterCoverChoiceDialog != null &&
                characterCoverChoiceDialog.IsShowing) return;
            if (nextKey != coverCandidateKey) coverContextVersion++;
            coverCandidateKey = nextKey;
            ShowCurrentCoverSelection();
            if (coverCandidates.Count == 0)
                SetPrompt(string.IsNullOrEmpty(model.InteractionStatus)
                    ? "当前没有可盖放的角色牌。"
                    : model.InteractionStatus);
        }

        private void SelectCoverCard(string cardId)
        {
            if (string.IsNullOrEmpty(cardId) || !coverCandidates.Contains(cardId) ||
                !IsCurrentCoverCandidate(cardId)) return;
            selectedCoverCardId = cardId;
            ShowCurrentCoverSelection();
        }

        private void ShowCurrentCoverSelection()
        {
            var version = coverContextVersion;
            characterCoverChoiceDialog?.ShowCoverSelection(
                coverCandidates, selectedCoverCardId,
                id => version == coverContextVersion && IsCurrentCoverCandidate(id),
                id => { if (version == coverContextVersion) SelectCoverCard(id); },
                () => { if (version == coverContextVersion) ConfirmCoverSelection(); });
        }

        private bool IsCurrentCoverCandidate(string cardId)
        {
            if (coverSubmissionInFlight || session == null || session.State == null || characterCardPresenter == null ||
                session.State.Phase != GamePhase.CharacterCover ||
                session.State.CurrentPlayerId != localPlayerId) return false;
            var current = characterCardPresenter.BuildView(session.State, localPlayerId);
            if (current == null || !current.CanCover || current.HandCards == null) return false;
            foreach (var card in current.HandCards)
                if (card != null && card.CanCover && card.CardId == cardId) return true;
            return false;
        }

        private void ConfirmCoverSelection()
        {
            if (coverSubmissionInFlight) return;
            if (!IsCurrentCoverCandidate(selectedCoverCardId))
            {
                SetPrompt("当前盖牌选择已失效，请重新选择。");
                RefreshCharacterCoverSelection(characterCardPresenter?.BuildView(session?.State, localPlayerId));
                return;
            }

            var cardId = selectedCoverCardId;
            coverSubmissionInFlight = true;
            submittedCoverCardId = cardId;
            characterCoverChoiceDialog?.Hide();
            SubmitCoverCharacterCard(cardId);
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
            if (!string.IsNullOrEmpty(characterUseCommandId)) return;
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
            ShowCharacterUsePage(view);
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

        private CharacterCardEffectChoiceDialog characterUsePage;
        private string characterUsePageKey = string.Empty;

        private string CharacterUseKey(CharacterCardPanelViewModel model)
        {
            return model == null ? string.Empty : localPlayerId + ":" + session?.State?.Round + ":" +
                session?.State?.Phase + ":" + model.CoveredCardId + ":" + model.IsSecondEffectDecision +
                ":" + model.IsSecondEffectExecution + ":" + model.CanUseStrategy + ":" + model.CanUseTactic;
        }

        private void ShowCharacterUsePage(CharacterCardPanelViewModel model)
        {
            if (model == null || gameplayInteractionHud == null) return;
            if (!model.IsSecondEffectDecision && !model.IsSecondEffectExecution)
            {
                OpenCharacterViewer(model);
                return;
            }
            var key = CharacterUseKey(model);
            if (characterUsePage != null && characterUsePage.IsShowing && characterUsePageKey == key) return;
            if (characterUsePage == null)
                characterUsePage = new CharacterCardEffectChoiceDialog(gameplayInteractionHud.DialogRegistry,
                    gameplayInteractionHud.Frame.ContentRect);
            characterUsePageKey = key;
            bool Current() => characterUsePageKey == key && session != null &&
                CharacterUseKey(characterCardPresenter.BuildView(session.State, localPlayerId)) == key;
            characterUsePage.ShowSecondEffectStep(model, () =>
            {
                if (Current()) BeginCharacterEffect(UseCharacterCardCommandHandler.Strategy);
            }, () =>
            {
                if (Current()) BeginCharacterEffect(UseCharacterCardCommandHandler.Tactic);
            }, () =>
            {
                if (Current()) FinishCharacterUseButtonClicked();
            }, Current, () =>
            {
                characterUsePageKey = string.Empty;
                characterSettlementInProgress = false;
            });
        }

        private void OpenCharacterViewer(CharacterCardPanelViewModel model)
        {
            if (!model.CanUse || !string.IsNullOrEmpty(characterUseCommandId)) return;
            var texture = gameplayInteractionHud.DialogRegistry.CardVisualCatalog.GetCharacterFront(model.CoveredCardId);
            if (texture == null) return;
            if (characterViewer == null) characterViewer = gameplayInteractionHud.DialogRegistry.InstantiateCardViewer();
            var ownerSession = session;
            var ownerPlayer = localPlayerId;
            var revision = session.State.EffectRuntime?.StateRevision ?? 0;
            var key = CharacterUseKey(model);
            bool Current() => session == ownerSession && localPlayerId == ownerPlayer &&
                string.IsNullOrEmpty(characterUseCommandId) && session.State != null &&
                (session.State.EffectRuntime?.StateRevision ?? 0) == revision &&
                CharacterUseKey(characterCardPresenter.BuildView(session.State, localPlayerId)) == key;
            characterViewer.OpenCharacter(model.CoveredCardId, texture, model.CanUseTactic, model.CanUseStrategy,
                characterCardPresenter.GetEffectUnavailableReason(session.State, localPlayerId, false),
                characterCardPresenter.GetEffectUnavailableReason(session.State, localPlayerId, true), Current,
                effect =>
                {
                    if (!Current()) return;
                    // 源包 plot 明确映射到工程 Tactic，不能按按钮索引选择。
                    BeginCharacterEffect(effect == CardViewerEffect.Plot
                        ? UseCharacterCardCommandHandler.Tactic : UseCharacterCardCommandHandler.Strategy);
                }, () => characterSettlementInProgress = false);
        }

        private bool FinishCharacterUseButtonClicked()
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
            if (!string.IsNullOrEmpty(characterUseCommandId)) return;
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
            characterUsePage?.Hide();
            characterUsePageKey = string.Empty;
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
