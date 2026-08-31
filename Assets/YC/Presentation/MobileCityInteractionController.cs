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
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Presentation.Maps;
using YC.Presentation.Workflows;
using UnityEngine;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private MapView mapViewBinding;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;
        [SerializeField] private GameSettingsMenuController settingsMenu;
        [SerializeField] private GameplayInteractionHudView gameplayInteractionHud;
        [SerializeField] private ResourceCounterBoard resourceCounterBoard;
        [SerializeField] private BuildInfoPanel buildInfoPanel;

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
        private InteractionRouter interactionRouter;
        private CommandSubmissionController commandSubmission;
        private MobileCityGameplayAdapter gameplayAdapter;
        private CommandGateway commandGateway;
        private MobileCityWorkflowViewAdapter workflowView;
        private MapInteractionRouter mapInteractionRouter;
        private EventChoiceDialog eventChoiceDialog;
        private MapViewPresenter mapView;
        private Canvas uiCanvas;
        private ActionPanelController actionPanel;
        private CharacterHandPanel characterHandPanel;
        private CharacterMapInteractionCoordinator characterMapInteraction;
        private CharacterCardInteraction characterCardInteraction;
        private PromptPresenter promptPresenter;
        private int localPlayerId = 1;
        private int lastDebugCoordinateLogFrame = -1;
        private static int interactionEscapeConsumedFrame = -1;
        private bool showCharacterUseOptions;
        private bool characterSettlementInProgress;
        private CharacterCardCoverDragCoordinator characterCardCoverDrag;
        private int lastPresentedGameLogSequence;
        private BuildFacilityInteractionUiCoordinator buildFacilityInteraction;
        private FacilityEffectChoiceDialog facilityEffectChoiceDialog;
        private FacilityEffectInteractionUiCoordinator facilityEffectInteraction;
        private SpecialActionInteractionUiCoordinator specialActionInteraction;

        public GameState CurrentState => session == null ? null : session.State;
        private PendingCardChoiceView CurrentPendingChoice =>
            session == null ? null : CardFlowStateAdapter.GetPendingChoiceView(session.State);
        public bool CanEndCurrentAction() =>
            turnActionPresenter != null && turnActionPresenter.CanEndCurrentAction();

        public void EndCurrentAction() => turnActionPresenter?.EndCurrentAction();
        private void Awake()
        {
            if (!GameplayInteractionHudView.TryValidateSceneBinding(
                    gameplayInteractionHud,
                    this,
                    resourceCounterBoard,
                    buildInfoPanel,
                    out var hudReason))
            {
                Debug.LogError("[MobileCityInteractionController] 交互 HUD 配置无效：" + hudReason, this);
                enabled = false; return;
            }

            var cardVisualCatalog = gameplayInteractionHud.DialogRegistry.CardVisualCatalog;
            if (!buildInfoPanel.ConfigureCardVisualCatalog(cardVisualCatalog) ||
                !buildInfoPanel.Bind(buildInfoPanel.View))
            {
                Debug.LogError("[MobileCityInteractionController] 信息面板固定 View 绑定失败。", this);
                enabled = false;
                return;
            }

            eventChoiceDialog = new EventChoiceDialog(
                gameplayInteractionHud.DialogRegistry,
                GetUiCanvasTransform);

            buildInfoPanel.CityStyleClicked += OnBuildInfoCityStyleClicked;

            if (mapViewBinding == null)
            {
                Debug.LogError("[MobileCityInteractionController] 缺少固定 MapView 场景引用。", this);
                enabled = false;
                return;
            }

            mapRenderer = mapViewBinding.MapRenderer;

            if (targetCamera == null) targetCamera = Camera.main;

            if (!TabletopRuntimeBootstrap.TryConfigure(gameplayInteractionHud.TabletopCanvas, targetCamera, mapRenderer, this)) return;

            ReadRightCardSmokeCommandLine();
            BuildSession();
            flowCoordinator = new InteractionFlowCoordinator();
            gameplayAdapter = new MobileCityGameplayAdapter(
                () => session == null ? null : session.State,
                () => localPlayerId,
                ShouldControlCurrentPlayerLocally,
                value => localPlayerId = value,
                () => commandSubmission, () => mapView?.RefreshScoreTrackDisplay(session == null ? null : session.State));
            commandGateway = new CommandGateway(gameplayAdapter);
            workflowView = new MobileCityWorkflowViewAdapter(
                () => session == null ? null : session.State,
                () => localPlayerId,
                GetUiCanvasTransform,
                gameplayInteractionHud.DialogRegistry,
                () => mapView,
                mapQuery,
                eventChoiceDialog,
                SetPrompt,
                SynchronizeInteractionFromState,
                RefreshResourceCounter,
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
            mapView = new MapViewPresenter(this, mapViewBinding, mapQuery, influenceService);
            string mapViewReason;
            if (!mapView.BuildViews(out mapViewReason))
            {
                Debug.LogError("[MobileCityInteractionController] 地图固定 View 绑定失败：" + mapViewReason, this);
                enabled = false;
                return;
            }
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
            if (!BindGameplayInteractionHud())
            {
                enabled = false;
                return;
            }
            RefreshResourceCounter(false);
            EnsureSettingsMenu();
            ShowInitialPlacementChoices();
            facilityEffectChoiceDialog = new FacilityEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry,
                GetUiCanvasTransform());
            facilityEffectInteraction = new FacilityEffectInteractionUiCoordinator(
                () => session == null ? null : session.State,
                () => localPlayerId,
                mapQuery,
                facilityEffectChoiceDialog,
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                (pending, optionId) => turnActionPresenter.BeginAdditionalExploreAction(pending, optionId),
                () => turnActionPresenter.CancelAdditionalExploreAction(),
                SubmitPendingEffectCommand,
                SetPrompt);
            facilityEffectInteraction.ConfigureAdditionalBuildDraftView(workflowView.ShowBuildFacilityDraft, workflowView.HideBuildFacilityDraft);
            specialActionInteraction = new SpecialActionInteractionUiCoordinator(
                () => session == null ? null : session.State,
                () => localPlayerId,
                GetUiCanvasTransform,
                gameplayInteractionHud.DialogRegistry,
                new SpecialActionOptionQueryService(
                    mapQuery, influenceService, movementService, new SpecialActionLifecycleService()),
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                SubmitPendingEffectCommand,
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
            BuildInteractionRouting();
            BuildCommandSubmission(GameLaunchContext.Instance);
            RefreshPendingChoiceOrHighlights();
            PrepareRightCardSmokePresentation();
        }

        private void Update()
        {
            UpdatePromptAnimation();
            buildFacilityInteraction?.Synchronize();
            if (Input.GetKeyDown(KeyCode.Escape) && TryHandleInteractionEscape())
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
            if (interactionRouter != null)
            {
                try
                {
                    interactionRouter.CancelAll();
                }
                catch (AggregateException exception)
                {
                    Debug.LogException(exception, this);
                }

                interactionRouter = null;
            }
            else
            {
                DisposeCharacterCardEffectInteraction();
                characterMapInteraction?.Cancel();
                specialActionInteraction?.Dispose();
                facilityEffectInteraction?.Dispose();
            }

            flowCoordinator?.ResetToHidden();
            characterCardInteraction = null;
            characterCardEffectInteraction = null;
            characterMapInteraction = null;
            specialActionInteraction = null;
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

        public void BeginNextRound() => EndCurrentAction();

        public void OnHotspotClicked(string locationId)
        {
            interactionRouter.OnLocationClicked(locationId);
        }

        public void OnMobileCityClicked()
        {
            interactionRouter.OnMobileCityClicked();
        }

        public void OnInfluenceSlotClicked(string slotId)
        {
            interactionRouter.OnInfluenceSlotClicked(slotId);
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

        private void ClearPendingDispatch() => influenceActionPresenter?.Clear();

        private bool HasPendingDispatchFirstMove() =>
            influenceActionPresenter != null && influenceActionPresenter.HasPendingFirstMove;

        private void EnsureSettingsMenu()
        {
            if (settingsMenu != null)
            {
                settingsMenu.ConfigureActionLog(session);
                return;
            }

            Debug.LogError("MobileCityInteractionController 缺少 GameSettingsMenuController 场景引用。", this);
        }

        private void RefreshResourceCounter()
        {
            RefreshResourceCounter(true);
        }

        private void RefreshResourceCounter(bool animate)
        {
            if (resourceCounterBoard == null) return;
            var player = session.State.FindPlayer(localPlayerId);
            if (player == null) return;
            resourceCounterBoard.Render(player.Resources, animate);
            var characterView = characterCardPresenter == null
                ? CharacterCardPanelViewModel.Empty("角色牌信息尚未初始化。")
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            if (!characterView.CanUse)
            {
                showCharacterUseOptions = false;
            }
            characterHandPanel?.Render(localPlayerId, characterView);
            RefreshBuildInfoPanel();
        }

        private void RefreshBuildInfoPanel()
        {
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

        public bool TryHandleInteractionEscape()
        {
            if (interactionEscapeConsumedFrame == Time.frameCount)
            {
                return true;
            }

            if (characterHandPanel != null && characterHandPanel.TryHandleEscape())
            {
                interactionEscapeConsumedFrame = Time.frameCount;
                return true;
            }
            if (interactionRouter == null)
            {
                return false;
            }

            var result = interactionRouter.OnEscape();
            if (result.Kind == InteractionResultKind.Passthrough)
            {
                return false;
            }

            interactionEscapeConsumedFrame = Time.frameCount;
            return true;
        }

        public static bool WasInteractionEscapeConsumedThisFrame()
        {
            return interactionEscapeConsumedFrame == Time.frameCount;
        }

        private void OnBuildInfoCityStyleClicked(string cityStyleId)
        {
            if (!string.IsNullOrEmpty(cityStyleId)) turnActionPresenter.OpenCityStylePreview(cityStyleId);
        }

        private RectTransform GetUiCanvasTransform() =>
            uiCanvas == null ? null : uiCanvas.GetComponent<RectTransform>();

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
            var result = GameSessionBootstrapper.Build(
                GameLaunchContext.Instance,
                useRightCardSmokeState,
                prepareSharedCityStyleSmokeState);
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
                SetPrompt, commandId => interactionRouter?.NotifyCommandSettled(commandId));
            commandSubmission.Initialize();
        }

        private void BuildInteractionRouting()
        {
            interactionRouter = new InteractionRouter(SetPrompt);
            interactionRouter.Register(new SpecialActionInteractionAdapter(specialActionInteraction));
            characterCardInteraction = new CharacterCardInteraction(
                characterCardEffectInteraction,
                characterMapInteraction,
                HasLocalPendingCharacterResolution,
                () => buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive,
                TryCancelCharacterFacilityEffectSelection);
            interactionRouter.Register(characterCardInteraction);
            interactionRouter.Register(new FacilityEffectInteractionAdapter(facilityEffectInteraction));
            interactionRouter.Register(turnActionPresenter.BuildInteraction);
            interactionRouter.Register(turnActionPresenter.MoveInteraction);
            interactionRouter.Register(turnActionPresenter.DeployInteraction);
            interactionRouter.Register(turnActionPresenter.DispatchInteraction);
            interactionRouter.Register(turnActionPresenter.ExploreInteraction);
            interactionRouter.Register(resourceCollectionPresenter);
            interactionRouter.Register(mapInteractionRouter);
        }
        private bool HasLocalPendingCharacterResolution()
        {
            var state = session == null ? null : session.State;
            var pending = state == null ? null : state.PendingCharacterEffect;
            return pending != null &&
                   pending.IsValid() &&
                   pending.PlayerId == localPlayerId;
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
            ObserveSharedCityStyleMirrorState();
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
            characterHandPanel?.CloseCharacterCardViewer();
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
            else if (!flowCoordinator.IsActive(resourceCollectionPresenter))
            {
                // 采集交互本身会按阶段报告为 Active，因此必须先完成查询初始化，
                // 否则空的展示状态会以 Busy 提前返回，路费航道也永远不会高亮。
                BeginResourceCollectionSelection();
            }

            var interactionPresentation = interactionRouter == null
                ? InteractionPresentation.Empty
                : interactionRouter.BuildActivePresentation();
            if (interactionPresentation.ReplacesHighlights)
            {
                workflowView.SetHighlights(interactionPresentation.Highlights);
            }

            if (!string.IsNullOrEmpty(interactionPresentation.PromptText))
            {
                SetPrompt(interactionPresentation.PromptText);
            }

            if (interactionPresentation.PanelMode == InteractionMode.Busy)
            {
                RefreshActionPanel();
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

        private bool BindGameplayInteractionHud()
        {
            uiCanvas = gameplayInteractionHud.Canvas;
            promptPresenter = PromptPresenter.Bind(gameplayInteractionHud.PromptView);
            actionPanel = ActionPanelController.Bind(
                gameplayInteractionHud.ActionPanelView,
                gameplayInteractionHud.DialogRegistry.CardVisualCatalog,
                OnUseCharacterActionClicked,
                OnDeclareCityStyleClicked,
                BeginDeployAction, BeginDispatchAction, BeginExploreAction, BeginMoveAction, EndCurrentAction);
            if (promptPresenter == null || actionPanel == null) { Debug.LogError("[MobileCityInteractionController] 交互 HUD 行为绑定失败。", this); return false; }
            characterHandPanel = gameplayInteractionHud.CharacterHandPanel;
            characterCardCoverDrag = new CharacterCardCoverDragCoordinator(
                () => actionPanel,
                SubmitCoverCharacterCard,
                SetPrompt,
                confirmed => characterHandPanel?.ResolvePendingCover(confirmed));
            if (characterHandPanel == null ||
                !characterHandPanel.Configure(
                    gameplayInteractionHud.DialogRegistry.CardVisualCatalog,
                    characterCardCoverDrag.Begin,
                    characterCardCoverDrag.Update,
                    characterCardCoverDrag.End))
            {
                Debug.LogError("[MobileCityInteractionController] 手牌面板行为绑定失败。", this);
                return false;
            }
            actionPanel.ConfigureCharacterActions(OnCharacterStrategyClicked, OnCharacterTacticClicked);
            actionPanel.ConfigureCharacterCardViewerAction(
                () => characterHandPanel?.OpenCoveredCharacterCardViewer());
            actionPanel.ConfigureCharacterFlipAction(FinishCharacterUseOnFlip);
            RefreshActionPanel();
            return true;
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
                SetPrompt(IsSecondEffectUnavailable()
                    ? "当前角色牌效果没有合法的地图目标，请点击翻转完成结算。"
                    : "第一个角色牌效果已结算，可继续使用第二个效果；点击翻转则结束角色卡使用。");
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
        private void BeginMoveAction() => turnActionPresenter.BeginMoveAction();
        private void BeginExploreAction() => turnActionPresenter.BeginExploreAction();

        private void BeginDeployAction() => turnActionPresenter.BeginDeployAction();

        private void BeginDispatchAction() => turnActionPresenter.BeginDispatchAction();
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

        private void SubmitPendingEffectCommand(GameCommand command)
        {
            var commandId = command == null ? string.Empty : command.CommandId;
            var outcome = commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    SetPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("待结算选择"))
                {
                    BeforeRejectedPrompt = _ => interactionRouter?.NotifyCommandSettled(commandId),
                    AfterRejectedPrompt = _ =>
                    {
                        specialActionInteraction?.Synchronize();
                        facilityEffectInteraction?.Synchronize();
                    },
                    OnAppliedLocally = _ =>
                    {
                        interactionRouter?.NotifyCommandSettled(commandId);
                        SynchronizeInteractionFromState();
                    }
                });
            if (outcome.Kind == SubmitOutcomeKind.NoResult)
            {
                interactionRouter?.NotifyCommandSettled(commandId);
            }
        }

        private void SubmitCharacterCardCommand(GameCommand command, string localSuccessPrompt, string remotePrompt)
        {
            commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    message =>
                    {
                        SetPrompt(message);
                        RefreshResourceCounter();
                    },
                    remotePrompt)
                {
                    OnAppliedLocally = _ => CompleteLocalCharacterCardCommand(
                        command,
                        localSuccessPrompt)
                });
        }

        private void CompleteLocalCharacterCardCommand(GameCommand command, string localSuccessPrompt)
        {
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
                SetPrompt(IsSecondEffectUnavailable()
                    ? "当前角色牌效果没有合法的地图目标，请点击翻转完成结算。"
                    : "第一个角色牌效果已完成，可继续使用第二个效果；点击翻转则结束角色卡使用。");
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

        private void OnDeclareCityStyleClicked() => turnActionPresenter.BeginDeclareCityStyle();

        private void OnBuildActionClicked()
        {
            turnActionPresenter.BeginBuildAction();
            buildFacilityInteraction?.Synchronize();
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
            RefreshResourceCounter();
            RefreshResourceTokenDisplay();
        }

        private void RefreshResourceTokenDisplay() =>
            mapView?.RefreshResourceTokenDisplay(session == null ? null : session.State);

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

        private static bool ShouldControlCurrentPlayerLocally() =>
            GameLaunchContext.Instance == null || GameLaunchContext.Instance.Mode == LaunchMode.Local;

        private void UpdateEntranceOrActionPrompt() => UpdateEntranceOrActionPrompt(string.Empty);

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
            if (!MapCameraGeometry.TryScreenToMapPlane(targetCamera, screenPosition, mapRenderer, out var worldPosition)) return;

            var normalizedPosition = mapView.ToNormalizedMapPosition(worldPosition);
            Debug.Log(string.Format(
                "Map click world=({0:F3}, {1:F3}) normalized=({2:F3}, {3:F3})",
                worldPosition.x,
                worldPosition.y,
                normalizedPosition.x,
                normalizedPosition.y));
        }

        private void ApplyDebugHotspotHighlights() =>
            mapView.ApplyDebugHotspotHighlights(debugClicks);

        private void SetPrompt(string message) => promptPresenter?.SetPrompt(message);

        private void UpdatePromptAnimation()
        {
            if (promptPresenter != null)
            {
                promptPresenter.Update(mapInteractionRouter != null && mapInteractionRouter.HasPendingConfirmation);
            }
        }

    }

}
