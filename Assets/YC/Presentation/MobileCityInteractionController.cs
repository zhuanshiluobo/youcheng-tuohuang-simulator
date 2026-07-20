using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.CardFlows;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Maps;
using YC.Presentation.Workflows;
using UnityEngine;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;

        private GameSession session;
        private MapQueryService mapQuery;
        private InfluenceService influenceService;
        private YC.Domain.Movement.CityMovementService movementService;
        private ExplorationService explorationService;
        private ResourceCollectionService resourceCollectionService;
        private ResourceCollectionPresenter resourceCollectionPresenter;
        private InfluenceActionPresenter influenceActionPresenter;
        private ExplorationEventPresenter explorationEventPresenter;
        private TurnActionPresenter turnActionPresenter;
        private CharacterCardPanelPresenter characterCardPresenter;
        private InteractionFlowCoordinator flowCoordinator;
        private CommandSubmissionController commandSubmission;
        private MobileCityGameplayAdapter gameplayAdapter;
        private MobileCityWorkflowViewAdapter workflowView;
        private MapInteractionRouter mapInteractionRouter;
        private ExpandableInfoPanel infoPanel;
        private BuildInfoPanel buildInfoPanel;
        private readonly EventChoiceDialog eventChoiceDialog = new EventChoiceDialog();
        private MapViewPresenter mapView;
        private Canvas uiCanvas;
        private ActionPanelController actionPanel;
        private CharacterMapInteractionCoordinator characterMapInteraction;
        private PromptPresenter promptPresenter;
        private int localPlayerId = 1;
        private int lastDebugCoordinateLogFrame = -1;
        private bool showCharacterUseOptions;
        private bool characterSettlementInProgress;
        private CharacterCardCoverDragCoordinator characterCardCoverDrag;
        private int lastPresentedGameLogSequence;
        private BuildFacilityInteractionUiCoordinator buildFacilityInteraction;
        private readonly FacilityEffectChoiceDialog facilityEffectChoiceDialog = new FacilityEffectChoiceDialog();
        private FacilityEffectInteractionUiCoordinator facilityEffectInteraction;

        public GameState CurrentState
        {
            get { return session == null ? null : session.State; }
        }

        private PendingCardChoiceView CurrentPendingChoice
        {
            get { return session == null ? null : CardFlowStateAdapter.GetPendingChoiceView(session.State); }
        }

        public bool CanEndCurrentAction()
        {
            return turnActionPresenter != null && turnActionPresenter.CanEndCurrentAction();
        }

        public void EndCurrentAction()
        {
            if (turnActionPresenter != null)
            {
                turnActionPresenter.EndCurrentAction();
            }
        }

        private void Awake()
        {
            if (mapRenderer == null)
            {
                mapRenderer = GetComponent<SpriteRenderer>();
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            ReadRightCardSmokeCommandLine();
            BuildSession();
            flowCoordinator = new InteractionFlowCoordinator();
            gameplayAdapter = new MobileCityGameplayAdapter(
                () => session == null ? null : session.State,
                () => localPlayerId,
                ShouldControlCurrentPlayerLocally,
                value => localPlayerId = value,
                () => commandSubmission);
            workflowView = new MobileCityWorkflowViewAdapter(
                () => session == null ? null : session.State,
                () => localPlayerId,
                GetUiCanvasTransform,
                () => mapView,
                mapQuery,
                eventChoiceDialog,
                SetPrompt,
                SynchronizeInteractionFromState,
                RefreshInfoPanel,
                RefreshActionPanel,
                RefreshResourceDisplay,
                RefreshInfluenceDisplay,
                CompleteActionCommandUi,
                GetPlayerDisplayName);
            resourceCollectionPresenter = new ResourceCollectionPresenter(
                gameplayAdapter,
                gameplayAdapter,
                workflowView,
                mapQuery,
                resourceCollectionService);
            influenceActionPresenter = new InfluenceActionPresenter(
                gameplayAdapter,
                gameplayAdapter,
                workflowView,
                mapQuery,
                influenceService);
            explorationEventPresenter = new ExplorationEventPresenter(
                gameplayAdapter,
                gameplayAdapter,
                workflowView,
                mapQuery,
                explorationService,
                influenceService);
            turnActionPresenter = new TurnActionPresenter(
                gameplayAdapter,
                gameplayAdapter,
                workflowView,
                flowCoordinator,
                mapQuery,
                resourceCollectionPresenter,
                influenceActionPresenter,
                explorationEventPresenter);
            characterCardPresenter = new CharacterCardPanelPresenter(
                new CharacterCardOptionQueryService(
                    mapQuery,
                    influenceService,
                    movementService));
            workflowView.Bind(
                flowCoordinator,
                resourceCollectionPresenter,
                influenceActionPresenter,
                explorationEventPresenter,
                turnActionPresenter);
            mapView = new MapViewPresenter(this, transform, mapRenderer, mapQuery, influenceService);
            mapView.BuildViews();
            mapInteractionRouter = new MapInteractionRouter(
                () => mapView,
                () => targetCamera,
                mapQuery,
                flowCoordinator,
                turnActionPresenter,
                resourceCollectionPresenter,
                influenceActionPresenter,
                explorationEventPresenter,
                workflowView,
                () => session.State.CurrentPlayerId,
                IsShiftDebugClick,
                LogPointerMapCoordinate,
                RefreshActionPanel,
                RefreshInfluenceDisplay);
            RefreshResourceTokenDisplay();
            RefreshInfluenceDisplay();
            BuildPromptPresenter();
            BuildActionPanel();
            EnsureInfoPanel();
            EnsureBuildInfoPanel();
            EnsureSettingsMenu();
            ShowInitialPlacementChoices();
            BuildCommandSubmission(GameLaunchContext.Instance);
            facilityEffectInteraction = new FacilityEffectInteractionUiCoordinator(
                () => session == null ? null : session.State,
                () => localPlayerId,
                GetUiCanvasTransform,
                mapQuery,
                facilityEffectChoiceDialog,
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                (pending, optionId) => turnActionPresenter.BeginAdditionalExploreAction(pending, optionId),
                () => turnActionPresenter.CancelAdditionalExploreAction(),
                SubmitFacilityEffectCommand,
                SetPrompt);
            buildFacilityInteraction?.Dispose();
            buildFacilityInteraction = new BuildFacilityInteractionUiCoordinator(
                buildInfoPanel, turnActionPresenter, facilityEffectInteraction);
            buildFacilityInteraction.Refresh(session.State, localPlayerId);
            characterMapInteraction = new CharacterMapInteractionCoordinator(
                () => session == null ? null : session.State,
                () => localPlayerId,
                characterCardPresenter,
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                (mode, parameters) => SubmitUseCharacterCard(mode, string.Empty, parameters),
                SubmitResolvePendingCharacterChoice,
                SetPrompt);
            BuildCharacterCardEffectInteraction();
            PrepareRightCardSmokePresentation();
        }

        private void Update()
        {
            UpdatePromptAnimation();
            buildFacilityInteraction?.Synchronize();
            if (Input.GetKeyDown(KeyCode.Escape) && TryHandleBuildFacilityEscape())
            {
                return;
            }

            if (mapInteractionRouter != null)
            {
                mapInteractionRouter.UpdateCancellation();
            }

            if (!IsShiftDebugClick())
            {
                return;
            }

            LogPointerMapCoordinate();
        }

        private void OnDestroy()
        {
            DisposeCharacterCardEffectInteraction();
            characterMapInteraction?.Cancel();
            characterMapInteraction = null;
            facilityEffectInteraction?.Dispose();
            facilityEffectInteraction = null;

            if (buildInfoPanel != null)
            {
                buildInfoPanel.CityStyleClicked -= OnBuildInfoCityStyleClicked;
            }

            buildFacilityInteraction?.Dispose();
            buildFacilityInteraction = null;

            if (commandSubmission != null)
            {
                commandSubmission.Dispose();
                commandSubmission = null;
            }
        }

        public void BeginNextRound()
        {
            EndCurrentAction();
        }

        public void OnHotspotClicked(string locationId)
        {
            if (characterMapInteraction != null &&
                characterMapInteraction.TryHandleLocationClicked(locationId))
            {
                return;
            }

            if (facilityEffectInteraction != null &&
                facilityEffectInteraction.TryHandleLocationClicked(locationId))
            {
                return;
            }

            mapInteractionRouter.OnLocationClicked(locationId);
        }

        public void OnMobileCityClicked()
        {
            if (characterMapInteraction != null && characterMapInteraction.IsActive)
            {
                return;
            }

            if (facilityEffectInteraction != null && facilityEffectInteraction.IsActive)
            {
                return;
            }

            mapInteractionRouter.OnMobileCityClicked();
        }

        public void OnInfluenceSlotClicked(string slotId)
        {
            if (characterMapInteraction != null &&
                characterMapInteraction.TryHandleInfluenceSlotClicked(slotId))
            {
                return;
            }

            if (facilityEffectInteraction != null &&
                facilityEffectInteraction.TryHandleInfluenceSlotClicked(slotId))
            {
                return;
            }

            mapInteractionRouter.OnInfluenceSlotClicked(slotId);
        }

        private void CompleteActionCommandUi(string actionName)
        {
            mapInteractionRouter.ClearConfirmation(false);
            ClearPendingDispatch();
            workflowView.ClearHighlights();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            RefreshActionPanel();
            RefreshRoundTrackerFromState();

            if (facilityEffectInteraction != null && facilityEffectInteraction.Synchronize())
            {
                SetPrompt("请先结算设施入场效果。");
                return;
            }

            UpdateEntranceOrActionPrompt(TurnActionPresenter.BuildCompletedMainActionMessage(actionName));
        }

        private void ClearPendingDispatch()
        {
            if (influenceActionPresenter != null)
            {
                influenceActionPresenter.Clear();
            }
        }

        private bool HasPendingDispatchFirstMove()
        {
            return influenceActionPresenter != null && influenceActionPresenter.HasPendingFirstMove;
        }

        private void EnsureInfoPanel()
        {
            infoPanel = FindObjectOfType<ExpandableInfoPanel>();
            if (infoPanel == null)
            {
                var go = new GameObject("ExpandableInfoPanel");
                go.transform.SetParent(transform, false);
                infoPanel = go.AddComponent<ExpandableInfoPanel>();
            }

            infoPanel.Initialize(infoPanel.transform);
            RefreshInfoPanel();
        }

        private void EnsureBuildInfoPanel()
        {
            buildInfoPanel = FindObjectOfType<BuildInfoPanel>();
            if (buildInfoPanel == null)
            {
                var go = new GameObject("BuildInfoPanel");
                go.transform.SetParent(transform, false);
                buildInfoPanel = go.AddComponent<BuildInfoPanel>();
            }

            buildInfoPanel.Initialize(buildInfoPanel.transform);
            buildInfoPanel.CityStyleClicked += OnBuildInfoCityStyleClicked;
            buildFacilityInteraction?.Dispose();
            buildFacilityInteraction = new BuildFacilityInteractionUiCoordinator(
                buildInfoPanel,
                turnActionPresenter,
                facilityEffectInteraction);
            RefreshBuildInfoPanel();
        }

        private void EnsureSettingsMenu()
        {
            var settingsMenu = GameSettingsMenuController.EnsureInScene(transform);
            settingsMenu.ConfigureActionLog(session);
        }

        private void RefreshInfoPanel()
        {
            if (infoPanel == null)
            {
                infoPanel = FindObjectOfType<ExpandableInfoPanel>();
            }

            if (infoPanel == null) return;

            var player = session.State.FindPlayer(localPlayerId);
            if (player == null) return;

            var r = player.Resources;
            infoPanel.SetRowValue("资源状态", "源岩", r.Originium.ToString());
            infoPanel.SetRowValue("资源状态", "源石", r.OriginiumShard.ToString());
            infoPanel.SetRowValue("资源状态", "异铁", r.Iron.ToString());
            infoPanel.SetRowValue("资源状态", "至纯源石", r.PureOriginium.ToString());
            infoPanel.SetRowValue("资源状态", "金券", r.GoldVoucher.ToString());
            infoPanel.SetPlayerDeclarations(session.State.Players);
            var characterView = characterCardPresenter == null
                ? CharacterCardPanelViewModel.Empty("角色牌信息尚未初始化。")
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            if (characterView.CanCover && !infoPanel.IsExpanded) infoPanel.SetExpandedState(true);
            if (!characterView.CanUse)
            {
                showCharacterUseOptions = false;
            }
            if (characterCardCoverDrag == null)
            {
                characterCardCoverDrag = new CharacterCardCoverDragCoordinator(
                    () => characterCardPresenter == null || session == null
                        ? null
                        : characterCardPresenter.BuildView(session.State, localPlayerId),
                    () => actionPanel,
                    SubmitCoverCharacterCard,
                    SetPrompt);
            }
            infoPanel.ConfigureCharacterCardDragInteraction(
                characterCardCoverDrag.Begin,
                characterCardCoverDrag.Update,
                characterCardCoverDrag.End);
            infoPanel.SetCharacterCards(
                characterView,
                showCharacterUseOptions,
                SubmitCoverCharacterCard,
                SubmitUseCharacterCard);

            RefreshBuildInfoPanel();
        }

        private void RefreshBuildInfoPanel()
        {
            if (buildInfoPanel == null)
            {
                buildInfoPanel = FindObjectOfType<BuildInfoPanel>();
            }

            if (buildInfoPanel == null || session == null || session.State == null)
            {
                return;
            }

            if (buildFacilityInteraction != null)
            {
                buildFacilityInteraction.Refresh(session.State, localPlayerId);
            }
            else
            {
                buildInfoPanel.Refresh(session.State, localPlayerId);
            }
        }

        public bool TryHandleBuildFacilityEscape()
        {
            return TryCancelCharacterFacilityEffectSelection() ||
                   (buildFacilityInteraction != null && buildFacilityInteraction.TryHandleEscape());
        }

        public static bool WasBuildEscapeConsumedThisFrame()
        {
            return characterFacilitySelectionEscapeConsumedFrame == Time.frameCount ||
                   BuildFacilityInteractionUiCoordinator.WasEscapeConsumedThisFrame();
        }

        private void OnBuildInfoCityStyleClicked(string cityStyleId)
        {
            if (!string.IsNullOrEmpty(cityStyleId)) turnActionPresenter.OpenCityStylePreview(cityStyleId);
        }

        private RectTransform GetUiCanvasTransform()
        {
            return uiCanvas == null ? null : uiCanvas.GetComponent<RectTransform>();
        }

        private void HideEventCardOptions()
        {
            if (explorationEventPresenter != null)
            {
                explorationEventPresenter.Cancel();
                return;
            }

            eventChoiceDialog.Hide();
        }

        private void BuildSession()
        {
            var result = GameSessionBootstrapper.Build(GameLaunchContext.Instance, useRightCardSmokeState);
            session = result.Session;
            mapQuery = result.MapQuery;
            influenceService = result.InfluenceService;
            movementService = result.MovementService;
            explorationService = result.ExplorationService;
            resourceCollectionService = result.ResourceCollectionService;
            localPlayerId = result.LocalPlayerId;
        }

        private void BuildCommandSubmission(GameLaunchContext launchContext)
        {
            commandSubmission = new CommandSubmissionController(
                session,
                launchContext,
                localPlayerId,
                this,
                SynchronizeInteractionFromState,
                SetPrompt);
            commandSubmission.Initialize();
        }

        private void RefreshAllFromState()
        {
            RefreshCityViewsFromState();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            mapView?.RefreshScoreTrackDisplay(session == null ? null : session.State);
            RefreshActionPanel();
            RefreshRoundTrackerFromState();
        }

        private void SynchronizeInteractionFromState()
        {
            turnActionPresenter.SynchronizeFromState();
            flowCoordinator.ResetToChooseAction();
            ClearPendingDispatch();
            HideEventCardOptions();
            RefreshAllFromState();
            RefreshPendingChoiceOrHighlights();
            SynchronizeCharacterSettlementPresentation();
            PresentLatestCharacterSettlementBroadcast();
            TryBeginAutomaticSecondEffect();
        }

        private void SynchronizeCharacterSettlementPresentation()
        {
            if (!characterSettlementInProgress || session == null || session.State == null)
            {
                return;
            }

            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || session.State.PendingCharacterEffect != null ||
                (!string.IsNullOrEmpty(player.CoveredCharacterCardId) && !player.UsedCharacterThisRound))
            {
                return;
            }

            characterSettlementInProgress = false;
            showCharacterUseOptions = false;
            infoPanel?.CloseCharacterCardViewer();
        }

        private void PresentLatestCharacterSettlementBroadcast()
        {
            if (session == null || session.State == null || session.State.Logs == null || session.State.Logs.Count == 0)
            {
                return;
            }

            string settlementMessage = null;
            for (var i = 0; i < session.State.Logs.Count; i++)
            {
                var entry = session.State.Logs[i];
                if (entry == null || entry.Sequence <= lastPresentedGameLogSequence)
                {
                    continue;
                }

                lastPresentedGameLogSequence = Mathf.Max(lastPresentedGameLogSequence, entry.Sequence);
                if (!string.IsNullOrEmpty(entry.Message) &&
                    entry.Message.Contains("角色牌") &&
                    entry.Message.Contains("结算"))
                {
                    settlementMessage = entry.Message;
                }
            }

            if (!string.IsNullOrEmpty(settlementMessage))
            {
                SetPrompt(settlementMessage);
            }
        }

        private void RefreshPendingChoiceOrHighlights()
        {
            if (session.State.Phase != GamePhase.ResourceCollection)
            {
                ClearCollectionSelection();
            }

            if (characterMapInteraction != null && characterMapInteraction.Synchronize())
            {
                return;
            }

            if (characterCardEffectInteraction != null && characterCardEffectInteraction.SynchronizePending())
            {
                return;
            }

            if (facilityEffectInteraction != null && facilityEffectInteraction.Synchronize())
            {
                return;
            }

            var pendingChoice = CurrentPendingChoice;
            if (pendingChoice != null && pendingChoice.PlayerId == localPlayerId)
            {
                workflowView.ClearHighlights();
                explorationEventPresenter.ShowPendingChoice();
                return;
            }

            if (turnActionPresenter.IsAwaitingInitialPlacement)
            {
                ShowInitialPlacementChoices();
                return;
            }

            if (session.State.Phase == GamePhase.ResourceCollection)
            {
                BeginResourceCollectionSelection();
                return;
            }

            workflowView.ClearHighlights();
            UpdateEntranceOrActionPrompt();
        }

        private void RefreshInfluenceDisplay()
        {
            if (mapView == null || session == null)
            {
                return;
            }

            mapView.RefreshInfluenceDisplay(
                session.State,
                HasPendingDispatchFirstMove(),
                influenceActionPresenter == null ? string.Empty : influenceActionPresenter.FirstSourceSlotId,
                influenceActionPresenter == null ? string.Empty : influenceActionPresenter.FirstTargetSlotId);
        }

        private void BuildPromptPresenter()
        {
            promptPresenter = PromptPresenter.Build(transform);
            uiCanvas = promptPresenter == null ? null : promptPresenter.Canvas;
        }

        private void BuildActionPanel()
        {
            actionPanel = ActionPanelController.Build(
                uiCanvas,
                OnUseCharacterActionClicked,
                OnDeclareCityStyleClicked,
                BeginDeployAction,
                BeginDispatchAction,
                BeginExploreAction,
                BeginMoveAction,
                OnBuildActionClicked,
                OnSpecialActionClicked,
                EndCurrentAction);
            actionPanel?.ConfigureCharacterActions(OnCharacterStrategyClicked, OnCharacterTacticClicked);
            actionPanel?.ConfigureCharacterCardViewerAction(() => infoPanel?.OpenCoveredCharacterCardViewer());
            actionPanel?.ConfigureCharacterFlipAction(FinishCharacterUseOnFlip);
            RefreshActionPanel();
        }

        private void RefreshActionPanel()
        {
            if (actionPanel == null || !actionPanel.IsReady || turnActionPresenter == null)
            {
                return;
            }

            var characterView = characterCardPresenter == null
                ? null
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            actionPanel.Render(turnActionPresenter.BuildActionPanelViewModel());
            if (actionPanel.CurrentFace == ActionPanelFace.Character && characterView != null) actionPanel.ShowCharacterCard(characterView);
            if (characterView != null && characterView.IsSecondEffectDecision)
            {
                workflowView.ClearHighlights();
                eventChoiceDialog.Hide();
                actionPanel.ConfigureCharacterActions(OnCharacterStrategyClicked, OnCharacterTacticClicked);
                actionPanel.ShowCharacterCard(characterView);
                SetPrompt("第一个角色牌效果已结算，可继续使用第二个效果；点击翻转则结束角色卡使用。");
                return;
            }

            if (characterSettlementInProgress &&
                characterView != null &&
                characterView.IsSecondEffectExecution)
            {
                actionPanel.ConfigureCharacterActions(OnCharacterStrategyClicked, OnCharacterTacticClicked);
                actionPanel.ShowCharacterCard(characterView);
            }
            var state = session == null ? null : session.State;
            if (state != null && state.HasPendingChoice())
            {
                if (state.PendingCardSession != null &&
                    state.PendingCardSession.IsValid() &&
                    state.PendingCardSession.ScenarioId == FacilityPendingChoiceTypes.ScenarioId)
                {
                    return;
                }

                var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
                if (pendingChoice != null &&
                    pendingChoice.PlayerId == localPlayerId &&
                    !eventChoiceDialog.IsShowing &&
                    !explorationEventPresenter.IsSelectingInfluenceTarget)
                {
                    explorationEventPresenter.ShowPendingChoice();
                }
            }
        }
        private void BeginMoveAction()
        {
            turnActionPresenter.BeginMoveAction();
        }

        private void BeginExploreAction()
        {
            turnActionPresenter.BeginExploreAction();
        }

        private void BeginDeployAction()
        {
            turnActionPresenter.BeginDeployAction();
        }

        private void BeginDispatchAction()
        {
            turnActionPresenter.BeginDispatchAction();
        }
        private void SubmitCoverCharacterCard(string cardId)
        {
            SubmitCharacterCardCommand(characterCardPresenter.CreateCoverCommand(localPlayerId, cardId), "角色牌已盖放。", "盖放角色牌命令已发送给主机，等待确认。");
        }

        private void SubmitUseCharacterCard(
            string effectMode,
            string effectOrder,
            IReadOnlyDictionary<string, string> effectParameters)
        {
            characterSettlementInProgress = true;
            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CoveredCharacterCardId))
            {
                SetPrompt("当前没有可使用的盖放角色牌。");
                return;
            }

            var command = characterCardPresenter.CreateUseCommand(
                localPlayerId,
                player.CoveredCharacterCardId,
                effectMode,
                effectOrder,
                effectParameters);
            SubmitCharacterCardCommand(command, "角色牌效果已结算。", "使用角色牌命令已发送给主机，等待确认。");
        }

        private void SubmitResolvePendingCharacterChoice(IReadOnlyDictionary<string, string> effectParameters)
        {
            var command = characterCardPresenter.CreateResolvePendingCommand(localPlayerId, effectParameters);
            SubmitCharacterCardCommand(command, "角色牌待选效果已处理。", "待选效果命令已发送给主机，等待确认。");
        }

        private void SubmitFacilityEffectCommand(GameCommand command)
        {
            var result = gameplayAdapter.Submit(command);
            if (!result.CommandResult.Succeeded)
            {
                SetPrompt(result.CommandResult.Validation.Reason);
                facilityEffectInteraction?.Synchronize();
                return;
            }

            if (!result.AppliedLocally)
            {
                SetPrompt("设施入场效果选择已发送给主机，等待确认。");
                return;
            }

            SynchronizeInteractionFromState();
        }

        private void SubmitCharacterCardCommand(GameCommand command, string localSuccessPrompt, string remotePrompt)
        {
            var result = gameplayAdapter.Submit(command);
            if (!result.CommandResult.Succeeded)
            {
                SetPrompt(result.CommandResult.Validation.Reason);
                RefreshInfoPanel();
                return;
            }

            if (!result.AppliedLocally)
            {
                SetPrompt(remotePrompt);
                RefreshInfoPanel();
                return;
            }

            SynchronizeInteractionFromState();
            if (command == null ||
                (command.Kind != GameCommandKind.UseCharacterCard && command.Kind != GameCommandKind.ResolvePendingChoice))
            {
                SetPrompt(localSuccessPrompt);
                return;
            }

            var pending = session.State.PendingCharacterEffect;
            if (pending == null || !pending.IsValid())
            {
                return;
            }

            if (pending.ChoiceType == CharacterPendingChoiceTypes.SecondEffectDecision)
            {
                SetPrompt("第一个角色牌效果已完成，可继续使用第二个效果；点击翻转则结束角色卡使用。");
            }
            else if (pending.ChoiceType == CharacterPendingChoiceTypes.SecondEffectExecution)
            {
                SetPrompt("请选择并结算第二个角色牌效果。");
            }
            else
            {
                SetPrompt("请继续完成当前角色牌效果选择，全部完成后将统一广播结算。");
            }
        }

        private void OnDeclareCityStyleClicked()
        {
            turnActionPresenter.BeginDeclareCityStyle();
        }

        private void OnBuildActionClicked()
        {
            turnActionPresenter.BeginBuildAction();
            buildFacilityInteraction?.Synchronize();
        }
        private void OnSpecialActionClicked()
        {
            SetPrompt("特殊行动暂未实现。");
        }

        private string GetPlayerDisplayName(int playerId)
        {
            var player = session.State.FindPlayer(playerId);
            if (player == null)
            {
                return playerId.ToString();
            }

            return string.IsNullOrEmpty(player.Name) ? "Player " + playerId : player.Name;
        }

        private void RefreshResourceDisplay()
        {
            RefreshInfoPanel();
            RefreshResourceTokenDisplay();
        }

        private void RefreshResourceTokenDisplay()
        {
            if (mapView == null)
            {
                return;
            }

            mapView.RefreshResourceTokenDisplay(session == null ? null : session.State);
        }

        private void ShowInitialPlacementChoices()
        {
            turnActionPresenter.SynchronizeFromState();
            workflowView.SetHighlights(turnActionPresenter.BuildInitialPlacementHighlights());

            UpdateEntranceOrActionPrompt();
            if (turnActionPresenter.IsAwaitingInitialPlacement &&
                turnActionPresenter.IsLocalPlayersTurn() &&
                (mapView == null || mapView.HighlightedLocationCount == 0))
            {
                SetPrompt("当前没有可放置移动城市的入场点。");
            }

            ApplyDebugHotspotHighlights();
        }

        private static bool ShouldControlCurrentPlayerLocally()
        {
            var launchContext = GameLaunchContext.Instance;
            return launchContext == null || launchContext.Mode == LaunchMode.Local;
        }

        private void UpdateEntranceOrActionPrompt()
        {
            UpdateEntranceOrActionPrompt(string.Empty);
        }

        private void UpdateEntranceOrActionPrompt(string actionMessage)
        {
            if (session == null || turnActionPresenter == null)
            {
                return;
            }

            RefreshActionPanel();
            if (!string.IsNullOrEmpty(actionMessage))
            {
                SetPrompt(actionMessage);
                return;
            }

            SetPrompt(turnActionPresenter.BuildActionPanelViewModel().StatusText);
        }

        private void BeginResourceCollectionSelection()
        {
            turnActionPresenter.BeginResourceCollection();
        }

        private void ClearCollectionSelection()
        {
            if (resourceCollectionPresenter != null)
            {
                resourceCollectionPresenter.Cancel();
            }
        }

        private void RefreshCityViewsFromState()
        {
            mapView.RefreshCityViewsFromState(session == null ? null : session.State, localPlayerId);
        }

        private void RefreshRoundTrackerFromState()
        {
            var roundTracker = FindObjectOfType<RoundTrackerController>();
            if (roundTracker != null)
            {
                roundTracker.RefreshFromState(session.State);
            }
        }

        private bool IsShiftDebugClick()
        {
            return debugClicks
                && Input.GetMouseButtonDown(0)
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        }

        private void LogPointerMapCoordinate()
        {
            if (lastDebugCoordinateLogFrame == Time.frameCount || targetCamera == null || mapRenderer == null)
            {
                return;
            }

            lastDebugCoordinateLogFrame = Time.frameCount;
            var screenPosition = Input.mousePosition;
            var worldPosition = targetCamera.ScreenToWorldPoint(new Vector3(screenPosition.x, screenPosition.y, -targetCamera.transform.position.z));
            var normalizedPosition = mapView.ToNormalizedMapPosition(worldPosition);
            Debug.Log(string.Format(
                "Map click world=({0:F3}, {1:F3}) normalized=({2:F3}, {3:F3})",
                worldPosition.x,
                worldPosition.y,
                normalizedPosition.x,
                normalizedPosition.y));
        }

        private void ApplyDebugHotspotHighlights()
        {
            mapView.ApplyDebugHotspotHighlights(debugClicks);
        }

        private void SetPrompt(string message)
        {
            if (promptPresenter != null)
            {
                promptPresenter.SetPrompt(message);
            }
        }

        private void UpdatePromptAnimation()
        {
            if (promptPresenter != null)
            {
                promptPresenter.Update(mapInteractionRouter != null && mapInteractionRouter.HasPendingConfirmation);
            }
        }

    }

}
