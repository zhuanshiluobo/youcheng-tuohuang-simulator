using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
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
        private string stoppedCharacterUseId = string.Empty;
        private string stoppedCharacterCommandId = string.Empty;

        public bool CanStopPendingCharacterMode => CanSubmitGameplayCommand && string.IsNullOrEmpty(characterUseCommandId) &&
            characterCardPresenter?.GetPendingModeStop(UiReadState(), localPlayerId, VisibleSaleState()) != null;

        public bool TryStopPendingCharacterMode()
        {
            if (!CanSubmitGameplayCommand || !string.IsNullOrEmpty(characterUseCommandId)) return false;
            var visible = VisibleSaleState();
            var ability = characterCardPresenter?.GetPendingModeStop(UiReadState(), localPlayerId, visible);
            if (ability == null) return false;
            var command = characterCardPresenter.CreateStopRemainingModeCommand(ability, localPlayerId, visible.StateRevision);
            stoppedCharacterUseId = ability.PendingUseId; stoppedCharacterCommandId = command.CommandId;
            SubmitCharacterCardCommand(command, string.Empty, string.Empty);
            return true;
        }

        // 共享控制器刷新权威快照后调用；外部响应尚未结束时保留本地结束意图。
        private void RefreshStoppedCharacterMode()
        {
            if (stoppedCharacterCommandId.Length == 0 || UiReadState() == null) return;
            var visible = VisibleSaleState();
            var use = visible?.CharacterUses.Find(value => value.UseId == stoppedCharacterUseId);
            if (use?.StopCommandId != stoppedCharacterCommandId) return;
            if (!use.Finished) return;
            stoppedCharacterCommandId = string.Empty; stoppedCharacterUseId = string.Empty;
            ClearCharacterUseSelectionPage();
            if (turnActionPresenter.BuildActionPanelViewModel().CanDeploy)
                gameplayInteractionHud.GetComponentInChildren<UiMainActionTabs>(true)?.Select(0);
        }

        private void NotifyCharacterUseSettled(string commandId)
        {
            if (string.IsNullOrEmpty(commandId) || commandId == characterUseCommandId)
                characterUseCommandId = string.Empty;
            if (commandId == stoppedCharacterCommandId &&
                VisibleSaleState()?.CharacterUses.Exists(use => use.UseId == stoppedCharacterUseId && use.StopCommandId == commandId) != true)
            { stoppedCharacterCommandId = string.Empty; stoppedCharacterUseId = string.Empty; }
        }
        private readonly List<string> coverCandidates = new List<string>();
        private string selectedCoverCardId = string.Empty;
        private string coverCandidateKey = string.Empty;
        private bool coverSubmissionInFlight;
        private string submittedCoverCardId = string.Empty;
        private string submittedCoverCommandId = string.Empty;
        private string submittedCoverGameId;
        private readonly InteractionCommandIntent coverIntent = new InteractionCommandIntent();
        private InteractionRequestProjection coverRequest;
        private string coverGameId;
        private int coverContextVersion;

        private void NotifyCoverCommandSettled(string commandId)
        {
            if (coverIntent.IsBusy || string.IsNullOrEmpty(commandId) || commandId != submittedCoverCommandId) return;
            if (!coverSubmissionInFlight && !string.IsNullOrEmpty(commandId)) return;
            coverSubmissionInFlight = false;
            submittedCoverCardId = string.Empty;
            submittedCoverCommandId = string.Empty;
            coverCandidateKey = string.Empty;
            coverContextVersion++;
        }

        private void NotifyCoverCommandResolved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode)
        {
            if (gameId != submittedCoverGameId || string.IsNullOrEmpty(commandId) || commandId != submittedCoverCommandId) return;
            coverIntent.Resolve(gameId, commandId, accepted, errorCode,
                commandSubmission?.RequiresExplicitRefreshForCurrentResult == true);
            if (!coverIntent.IsBusy) NotifyCoverCommandSettled(commandId);
        }

        private void BuildCharacterCardEffectInteraction()
        {
            characterCardEffectChoiceDialog = new CharacterCardEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry,
                GetUiCanvasTransform());
            characterCardEffectChoiceDialog.RenderPaymentConfirmation = eventChoiceDialog.ShowConditionPayment;
            characterCardEffectChoiceDialog.IsPaymentConfirmationShowing = () => eventChoiceDialog.IsShowing;
            characterCardEffectChoiceDialog.HidePaymentConfirmation = eventChoiceDialog.Hide;
            // 盖放是回合阶段页面，不能与会在旧效果结束时被 Clear 的结算弹窗共用实例。
            characterCoverChoiceDialog = new CharacterCardEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry,
                GetUiCanvasTransform());
            characterCardEffectInteraction = new CharacterCardEffectInteractionUiCoordinator(
                () => UiReadState(),
                () => localPlayerId,
                characterCardPresenter,
                characterCardEffectChoiceDialog,
                () => characterMapInteraction != null && characterMapInteraction.TryBeginTinManPendingMove(),
                () => buildInfoPanel?.EndFacilityEffectSelection(),
                (mode, parameters) => SubmitUseCharacterCard(mode, string.Empty, parameters),
                SubmitResolvePendingCharacterChoice,
                SetPrompt);
            characterAbilityInteraction = new CharacterAbilityInteractionUiCoordinator(() => UiReadState(), () => localPlayerId, characterCardEffectChoiceDialog, highlights => workflowView.SetHighlights(highlights), () => workflowView.ClearHighlights(), SubmitPendingEffectCommand, SetPrompt);
            characterAbilityInteraction.GetVisibleState = VisibleSaleState;
            characterCardEffectInteraction.GetVisibleState = VisibleSaleState;
            characterCardEffectInteraction.RenderLegacyMessageDocument = gameplayInteractionHud.Frame.SetLegacyInteractionMessageDocument;
            gameplayInteractionHud.Frame.BindLegacyGameplayDefinitionNames(id => CharacterCardDatabase.IsInitialized
                ? CharacterCardDatabase.Get(id)?.Name : null);
            characterCardEffectInteraction.SubmitSelection = characterAbilityInteraction.TrySubmitUseSelection;
            characterAbilityInteraction.RenderUseSelection = request =>
            {
                var view = characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState());
                if (view.CurrentUseSelection?.InteractionId == request.InteractionId) ShowCharacterUsePage(view);
            };
            characterAbilityInteraction.ClearUseSelection = ClearCharacterUseSelectionPage;
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
            var state = UiReadState();
            if (!string.IsNullOrEmpty(state?.GameId) && coverIntent.IsBusy && !coverIntent.BelongsToContext(state.GameId, localPlayerId)) coverIntent.Reset();
            var eligible = state != null && state.Phase == GamePhase.CharacterCover &&
                           model != null && model.CanCover && (model.UsesEntityLifecycle
                               ? model.CurrentCoverSelection != null : HasLocalOperationPermission(state));
            if (ShouldControlCurrentPlayerLocally() && coverIntent.WaitingForRefresh)
            {
                if (coverIntent.TryRebuild(state?.GameId, model?.CurrentCoverSelection, string.Empty, out var rebuilt))
                {
                    coverRequest = model.CurrentCoverSelection; submittedCoverCommandId = rebuilt.CommandId;
                    coverSubmissionInFlight = true;
                    SubmitCharacterCardCommand(rebuilt, "角色牌已盖放。", "盖放角色牌命令已发送给主机，等待确认。");
                    return;
                }
                if (!coverIntent.IsBusy) { coverSubmissionInFlight = false; submittedCoverCommandId = string.Empty; }
            }
            if (coverIntent.IsBusy) return;
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
                          InteractionCommandIntent.ContractKey(model.CurrentCoverSelection) + ":" +
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
                characterCoverChoiceDialog.IsShowing)
            {
                // 联机确认前缀只改变提交许可，盖牌合同及候选不变；复用页面也须重算动态资格。
                ShowCurrentCoverSelection();
                return;
            }
            if (nextKey != coverCandidateKey) coverContextVersion++;
            coverCandidateKey = nextKey;
            coverRequest = model.CurrentCoverSelection; coverGameId = state.GameId;
            ShowCurrentCoverSelection();
            if (coverCandidates.Count == 0)
                SetPrompt(model.InteractionStatusText ?? new GameplayTextRef("core:legacy.character.unavailable.cover_none"));
        }

        private void SelectCoverCard(string cardId)
        {
            using var visibleRead = BeginVisibleRead();
            if (string.IsNullOrEmpty(cardId) || !coverCandidates.Contains(cardId) ||
                !IsCurrentCoverCandidate(cardId)) return;
            selectedCoverCardId = cardId;
            ShowCurrentCoverSelection();
        }

        private void ShowCurrentCoverSelection()
        {
            using var visibleRead = BeginVisibleRead();
            var version = coverContextVersion;
            if (characterCoverChoiceDialog != null)
                characterCoverChoiceDialog.ResolveCharacterDefinition = id => VisibleSaleState()?.ItemZones.Items.Find(item => item.ItemId == id)?.DefinitionId ?? id;
            characterCoverChoiceDialog?.ShowCoverSelection(
                coverCandidates, selectedCoverCardId,
                id => version == coverContextVersion && IsCurrentCoverCandidate(id),
                id => { if (version == coverContextVersion) SelectCoverCard(id); },
                () => { if (version == coverContextVersion) ConfirmCoverSelection(); },
                version + ":" + coverCandidateKey);
        }

        private bool IsCurrentCoverCandidate(string cardId)
        {
            using var visibleRead = BeginVisibleRead();
            if (!CanSubmitGameplayCommand || coverSubmissionInFlight || session == null || UiReadState() == null || characterCardPresenter == null ||
                UiReadState().Phase != GamePhase.CharacterCover) return false;
            var current = characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState());
            if (current == null || !current.CanCover || current.HandCards == null) return false;
            if (current.UsesEntityLifecycle && (!InteractionCommandIntent.SameContract(coverGameId, coverRequest,
                UiReadState().GameId, current.CurrentCoverSelection) || !current.CurrentCoverSelection.CandidateIds.Contains(cardId))) return false;
            if (!current.UsesEntityLifecycle && !HasLocalOperationPermission(UiReadState())) return false;
            foreach (var card in current.HandCards)
                if (card != null && card.CanCover && card.CardId == cardId) return true;
            return false;
        }

        private void ConfirmCoverSelection()
        {
            using var visibleRead = BeginVisibleRead();
            if (coverSubmissionInFlight) return;
            if (!IsCurrentCoverCandidate(selectedCoverCardId))
            {
                SetPrompt(new GameplayTextRef("core:legacy.character.unavailable.cover_selection_stale"));
                RefreshCharacterCoverSelection(characterCardPresenter?.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState()));
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

        private void OnUseCharacterActionClicked(string cardId)
        {
            using var visibleRead = BeginVisibleRead();
            if (!CanBeginGameplayAction || !string.IsNullOrEmpty(characterUseCommandId) ||
                !string.IsNullOrEmpty(pendingEffectCommandId) || turnActionPresenter?.IsMainActionSubmissionInFlight == true) return;
            var view = characterCardPresenter == null
                ? null
                : characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState(), cardId);
            if (view == null || !view.CanUse)
            {
                SetPrompt(view?.InteractionStatusText ?? new GameplayTextRef("core:legacy.character.unavailable.not_initialized"));
                return;
            }
            turnActionPresenter?.RunAfterClearingMainActionDraft(() => BeginCharacterAfterDraftCleared(cardId));
        }
        private void BeginCharacterAfterDraftCleared(string cardId)
        {
            if (!CanBeginGameplayAction) return;
            var view = characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState(), cardId);
            if (view?.CanUse != true) return;

            showCharacterUseOptions = false;
            characterSettlementInProgress = true;
            characterCardEffectInteraction?.HideDialog();
            characterMapInteraction?.Cancel();
            gameplayInteractionHud.Frame.ResumeEffectPage();
            SubmitCharacterCardCommand(characterCardPresenter.CreateBeginUseCommand(localPlayerId, view.CoveredCardId),
                string.Empty, string.Empty);
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
                : characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState());
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
            var pending = UiReadState()?.PendingCharacterEffect;
            return pending != null && pending.IsValid() &&
                   pending.PlayerId == localPlayerId && pending.ChoiceType == CharacterPendingChoiceTypes.SecondEffectDecision &&
                   !pending.OptionIds.Contains(CharacterEffectChoiceIds.ContinueSecondEffect);
        }

        private CharacterCardEffectChoiceDialog characterUsePage;
        private InteractionRequestProjection characterViewerUseRequest;
        private Func<bool> characterViewerRequestIsCurrent;
        private string characterUsePageKey = string.Empty;

        private string CharacterUseKey(CharacterCardPanelViewModel model)
        {
            return model == null ? string.Empty : localPlayerId + ":" + UiReadState()?.Round + ":" +
                UiReadState()?.Phase + ":" + model.CoveredCardId + ":" + model.IsSecondEffectDecision +
                ":" + model.IsSecondEffectExecution + ":" + model.CanUseStrategy + ":" + model.CanUseTactic +
                ":" + model.CurrentUseId + ":" + model.CurrentUseSelection?.InteractionId + ":" +
                model.CurrentUseSelection?.StateRevision + ":" + model.CurrentUseSelection?.CandidateSetId + ":" + model.CurrentUseSelection?.CandidateSetVersion;
        }

        private void ClearCharacterUseSelectionPage()
        {
            characterViewerUseRequest = null; characterViewerRequestIsCurrent = null;
            if (characterViewer != null)
            {
                gameplayInteractionHud?.Frame.ReleasePage(characterViewer.gameObject);
                characterViewer.Dismiss();
            }
            characterUsePage?.Hide();
            characterUsePageKey = string.Empty;
        }

        private void ShowCharacterUsePage(CharacterCardPanelViewModel model)
        {
            if (model == null || gameplayInteractionHud == null) return;
            if (model.UsesEntityLifecycle)
            {
                if (!string.IsNullOrEmpty(model.CurrentUseId))
                {
                    // 首模式仍由现成卡面消费；第二模式由Host自动接续。
                    if (CharacterCardPanelPresenter.UsesOriginalModeViewer(model.CurrentUseSelection))
                    {
                        OpenCharacterViewer(model);
                        return;
                    }
                    return;
                }
                else
                {
                    if (!model.CanUse || !string.IsNullOrEmpty(characterUseCommandId)) return;
                    SubmitCharacterCardCommand(characterCardPresenter.CreateBeginUseCommand(localPlayerId, model.CoveredCardId),
                        string.Empty, string.Empty);
                    return;
                }
            }
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
                CharacterUseKey(characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState())) == key;
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
            var selection = model.CurrentUseSelection;
            if (model.UsesEntityLifecycle && !CharacterCardPanelPresenter.UsesOriginalModeViewer(selection)) return;
            if (model.UsesEntityLifecycle && characterViewer != null && !string.IsNullOrEmpty(characterViewer.CardId) &&
                characterUsePageKey == CharacterUseKey(model) && characterViewerRequestIsCurrent?.Invoke() == true)
            { RefreshCharacterViewerSubmissionAvailability(CanSubmitGameplayCommand); return; }
            var texture = gameplayInteractionHud.DialogRegistry.CardVisualCatalog.GetCharacterFront(model.ResolveDefinitionId(model.CoveredCardId));
            if (texture == null) return;
            if (characterViewer == null) characterViewer = gameplayInteractionHud.DialogRegistry.InstantiateCardViewer();
            bool preserveFold = model.UsesEntityLifecycle && GameplayHudFrame.EffectInputSuspended;
            if (model.UsesEntityLifecycle)
            {
                // 替换同一请求的投影前释放旧页面；OpenCharacter 的资料页入口不能把新使用页意外收起。
                gameplayInteractionHud.Frame.ReleasePage(characterViewer.gameObject);
                characterViewer.Dismiss();
            }
            var key = CharacterUseKey(model);
            if (model.UsesEntityLifecycle) characterUsePageKey = key;
            var current = CreateCharacterViewerCurrentGuard(model, key);
            characterViewerUseRequest = selection;
            characterViewerRequestIsCurrent = current;
            characterViewer.BindGameplayText(gameplayInteractionHud.Frame.RenderGameplayText);
            characterViewer.OpenCharacter(model.CoveredCardId, texture, model.CanUseTactic, model.CanUseStrategy,
                gameplayInteractionHud.ExpansionUi.ResolveGameplayText(characterCardPresenter.GetEffectUnavailableReasonText(model, false)),
                gameplayInteractionHud.ExpansionUi.ResolveGameplayText(characterCardPresenter.GetEffectUnavailableReasonText(model, true)), current,
                effect =>
                {
                    if (!current() || !CanSubmitGameplayCommand || !string.IsNullOrEmpty(characterUseCommandId) ||
                        !string.IsNullOrEmpty(pendingEffectCommandId)) return;
                    // 源包 plot 明确映射到工程 Tactic，不能按按钮索引选择。
                    BeginCharacterEffect(effect == CardViewerEffect.Plot
                        ? UseCharacterCardCommandHandler.Tactic : UseCharacterCardCommandHandler.Strategy);
                }, () =>
                {
                    if (model.UsesEntityLifecycle && !selection.CandidateIds.Contains(CharacterLifecycleService.CancelChoiceId))
                    {
                        // 消费来源后的返回不能取消用牌或退回快速入口；仍呈现同一请求的原卡面。
                        if (current()) OpenCharacterViewer(model);
                        return;
                    }
                    characterSettlementInProgress = false;
                    characterUsePageKey = string.Empty;
                    // CardViewer 的取消/返回是明确取消；HUD 收起仅隐藏，不触发此回调。
                    if (model.UsesEntityLifecycle && selection.CandidateIds.Contains(CharacterLifecycleService.CancelChoiceId))
                        characterAbilityInteraction.TrySubmitUseSelection(selection, CharacterLifecycleService.CancelChoiceId);
                });
            characterViewer.CanSubmit = () => CanSubmitGameplayCommand && string.IsNullOrEmpty(characterUseCommandId) &&
                string.IsNullOrEmpty(pendingEffectCommandId);
            RefreshCharacterViewerSubmissionAvailability(CanSubmitGameplayCommand);
            if (model.UsesEntityLifecycle)
            {
                gameplayInteractionHud.Frame.ShowPage(characterViewer.gameObject, true);
                if (preserveFold) gameplayInteractionHud.Frame.SuspendEffectForInformation();
            }
        }

        private Func<bool> CreateCharacterViewerCurrentGuard(CharacterCardPanelViewModel model, string key)
        {
            var simulated = commandSubmission?.UsesDeterministicSimulation == true;
            var owner = session; var state = simulated ? commandSubmission.PublishedState : session?.State; var visible = VisibleSaleState();
            var player = localPlayerId; var game = state?.GameId; var content = state?.ContentPackHash;
            var revision = UiReadState()?.EffectRuntime?.StateRevision ?? 0;
            return () =>
            {
                var currentState = simulated ? commandSubmission?.PublishedState : session?.State;
                if (owner == null || !ReferenceEquals(session, owner) || !ReferenceEquals(currentState, state) ||
                    localPlayerId != player || currentState?.GameId != game || currentState?.ContentPackHash != content) return false;
                var read = UiReadState();
                if (read == null || (read.EffectRuntime?.StateRevision ?? 0) != revision) return false;
                if (!simulated)
                    return CharacterUseKey(characterCardPresenter.BuildProjectedView(read, player, VisibleSaleState())) == key;
                var current = VisibleSaleState();
                if (current == null || !ReferenceEquals(current, visible)) return false;
                var bound = model.CurrentUseSelection;
                if (bound != null)
                {
                    var request = current.Interactions.Find(value => value.InteractionId == bound.InteractionId);
                    if (request == null || request.Status != "open" || request.AnsweringPlayerId != player ||
                        !YC.Domain.State.GameStateVisibilityPolicy.IsVisible(request.Visibility, request.AnsweringPlayerId,
                            YC.Domain.State.GameStateViewer.Player(player)) ||
                        request.StateRevision != bound.StateRevision || request.CandidateSetId != bound.CandidateSetId ||
                        request.CandidateSetVersion != bound.CandidateSetVersion) return false;
                }
                return CharacterUseKey(model) == key;
            };
        }

        private void RefreshCharacterViewerSubmissionAvailability(bool allowed)
        {
            if (characterViewer != null && characterViewer.Mode == CardViewerMode.UseCharacter)
            {
                characterViewer.RefreshSubmissionAvailability(allowed && string.IsNullOrEmpty(characterUseCommandId) &&
                    string.IsNullOrEmpty(pendingEffectCommandId));
                characterViewer.CancelButton.interactable = allowed && (characterViewerUseRequest == null ||
                    characterViewerUseRequest.CandidateIds.Contains(CharacterLifecycleService.CancelChoiceId));
            }
        }

        private bool IsCharacterViewerUseRequest(InteractionRequestProjection request) =>
            request != null && characterViewerUseRequest != null && characterViewer != null &&
            characterViewer.Mode == CardViewerMode.UseCharacter && !string.IsNullOrEmpty(characterViewer.CardId) &&
            characterViewerRequestIsCurrent?.Invoke() == true && request.InteractionId == characterViewerUseRequest.InteractionId &&
            request.Status == "open" && request.VisibleToViewer && request.AnsweringPlayerId == localPlayerId &&
            request.InteractionTypeId == characterViewerUseRequest.InteractionTypeId &&
            request.StateRevision == characterViewerUseRequest.StateRevision &&
            request.CandidateSetId == characterViewerUseRequest.CandidateSetId &&
            request.CandidateSetVersion == characterViewerUseRequest.CandidateSetVersion;

        private bool FinishCharacterUseButtonClicked()
        {
            var view = characterCardPresenter == null || session == null
                ? null
                : characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState());
            if (view == null || !view.IsSecondEffectDecision)
            {
                return false;
            }

            if (view.UsesEntityLifecycle)
                return characterAbilityInteraction.TrySubmitUseSelection(view.CurrentUseSelection, CharacterLifecycleService.FinishChoiceId);

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
                : characterCardPresenter.BuildProjectedView(UiReadState(), localPlayerId, VisibleSaleState());
            var useStrategy = effectMode == UseCharacterCardCommandHandler.Strategy;
            var canUse = view != null && (useStrategy ? view.CanUseStrategy : view.CanUseTactic);
            if (!canUse)
            {
                SetPrompt(new GameplayTextRef(view == null ? "core:legacy.character.unavailable.not_initialized" : "core:legacy.character.unavailable.cannot_activate"));
                return;
            }

            if (view.UsesEntityLifecycle && string.IsNullOrEmpty(view.CurrentUseId))
            {
                ShowCharacterUsePage(view);
                return;
            }
            if (view.IsSecondEffectDecision && !view.UsesEntityLifecycle)
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

            SetPrompt(new GameplayTextRef("core:legacy.character.unavailable.effect_not_registered"));
        }
    }
}
