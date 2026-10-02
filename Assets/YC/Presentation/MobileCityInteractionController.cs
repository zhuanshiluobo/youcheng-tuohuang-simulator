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
        private int localPlayerId = 1;
        private int lastDebugCoordinateLogFrame = -1;
        private static int interactionEscapeConsumedFrame = -1;
        private bool showCharacterUseOptions;
        private bool characterSettlementInProgress;
        private int lastPresentedGameLogSequence;
        private BuildFacilityInteractionUiCoordinator buildFacilityInteraction;
        private FacilityInteractionUiCoordinator facilityInteraction;
        private SpecialActionInteractionUiCoordinator specialActionInteraction;
        private enum BuildDialogStep { None, Facilities, Slots, Payments, Quote }
        private BuildDialogStep buildDialogStep;
        private bool buildDialogActive;
        private string selectedBuildFacilityId = string.Empty;

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


            if (mapViewBinding == null)
            {
                Debug.LogError("[MobileCityInteractionController] 缺少固定 MapView 场景引用。", this);
                enabled = false;
                return;
            }

            mapRenderer = mapViewBinding.MapRenderer;

            if (targetCamera == null) targetCamera = Camera.main;



            ReadRightCardSmokeCommandLine();
            // 三人场景固定绑定三人视图；直接从编辑器运行时创建单城上下文。
            var sceneMapId = mapViewBinding.CoordinateSpace.Layout.MapId;
            if (sceneMapId == StaticMapDefinitions.ThreePlayerMapId &&
                (GameLaunchContext.Instance == null || GameLaunchContext.Instance.Players.Count == 0))
            {
                var seats = new List<PlayerSeat>();
                var colors = new[] { PlayerColor.Blue, PlayerColor.Red, PlayerColor.Green };
                for (var i = 0; i < 1; i++)
                    seats.Add(new PlayerSeat { PlayerId = i + 1, PlayerName = "玩家" + (i + 1),
                        Color = colors[i], IsReady = true });
                GameLaunchContext.Ensure().Configure(LaunchMode.Local, 1, "three-player-editor", seats, sceneMapId);
            }
            BuildSession();
            if (mapQuery.Map.MapId != sceneMapId)
            {
                Debug.LogError("场景地图与启动人数不一致，请从开始菜单选择对应地图。", this);
                enabled = false;
                return;
            }
            if (!TabletopRuntimeBootstrap.TryConfigure(gameplayInteractionHud.TabletopCanvas, targetCamera, mapRenderer, this)) return;
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
                RefreshLocalPlayerUi,
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
            turnActionPresenter.ActionPanelPresenter.CharacterCoverPrompt =
                gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile.CharacterCoverSelectionDescription;
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
            RefreshLocalPlayerUi(false);
            var informationPages = gameplayInteractionHud.GetComponent<GameplayInformationPages>();
            if (informationPages != null)
                informationPages.Configure(() => CurrentState, () => localPlayerId, mapQuery,
                    gameplayInteractionHud.DialogRegistry);
            EnsureSettingsMenu();
            ShowInitialPlacementChoices();
            facilityInteraction = new FacilityInteractionUiCoordinator(
                () => session == null ? null : session.State,
                () => localPlayerId,
                GetUiCanvasTransform,
                gameplayInteractionHud.DialogRegistry,
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                SubmitPendingEffectCommand,
                SetPrompt);
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
            BuildEventCardInteraction();
            buildFacilityInteraction?.Dispose();
            buildFacilityInteraction = new BuildFacilityInteractionUiCoordinator(
                buildInfoPanel, turnActionPresenter);
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
            RefreshFinalScoreFromState();
            PrepareRightCardSmokePresentation();
        }

        private void Update()
        {
            if (UpdateEffectMapConfirmationCancellation()) return;
            workflowView?.RefreshCityStylePreview();
            gameplayInteractionHud?.Frame?.UpdateInteractionMessage(
                (mapInteractionRouter != null && mapInteractionRouter.HasPendingConfirmation) ||
                (interactionRouter != null && interactionRouter.HasPendingMapConfirmation));
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
            characterUsePage?.Hide();
            specialActionUsePage?.Hide();
            if (facilityBuildPage != null) facilityBuildPage.Hide();
            if (characterViewer != null) characterViewer.Dismiss();
            characterViewer = null;
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
                facilityInteraction?.Dispose();
            }

            flowCoordinator?.ResetToHidden();
            characterCardInteraction = null;
            characterCardEffectInteraction = null;
            characterMapInteraction = null;
            specialActionInteraction = null;
            facilityInteraction = null;

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

        private int lastMapButtonInputFrame = -1;

        public void OnMapButtonClicked(WorkflowHighlightTargetKind kind, string targetId)
        {
            if (lastMapButtonInputFrame == Time.frameCount || interactionRouter == null ||
                mapView == null || !mapView.IsButtonInteractive(kind, targetId)) return;
            lastMapButtonInputFrame = Time.frameCount;
            if (kind == WorkflowHighlightTargetKind.Location) interactionRouter.OnLocationClicked(targetId, Time.frameCount);
            else if (kind == WorkflowHighlightTargetKind.Route) interactionRouter.OnRouteClicked(targetId, Time.frameCount);
            else interactionRouter.OnInfluenceSlotClicked(targetId, Time.frameCount);
            RefreshRoutedMapPresentation();
        }

        private void RefreshRoutedMapPresentation()
        {
            var presentation = interactionRouter.BuildActivePresentation();
            if (presentation.ReplacesHighlights) workflowView.SetHighlights(presentation.Highlights);
        }

        private bool UpdateEffectMapConfirmationCancellation()
        {
            if (interactionRouter == null || !interactionRouter.HasPendingMapConfirmation) return false;
            if (Input.GetKeyDown(KeyCode.Escape)) return TryHandleInteractionEscape();
            var cancel = false;
            if (Input.GetMouseButtonUp(0) && TabletopPointerClassifier.CanRouteMapPointer(Input.mousePosition))
            {
                cancel = true;
                var camera = Camera.main;
                if (camera != null)
                    foreach (var hit in Physics2D.GetRayIntersectionAll(camera.ScreenPointToRay(Input.mousePosition)))
                        if (hit.collider != null && hit.collider.GetComponent<MapButtonView>() != null)
                        { cancel = false; break; }
            }
            if (!cancel) return false;
            interactionRouter.ClearMapConfirmation();
            RefreshRoutedMapPresentation();
            return true;
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
            RefreshCityViewsFromState();
            RefreshResourceDisplay();
            RefreshInfluenceDisplay();
            RefreshActionPanel();
            RefreshFinalScoreFromState();

            if (facilityInteraction != null && facilityInteraction.Synchronize())
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

        private void RefreshLocalPlayerUi()
        {
            RefreshLocalPlayerUi(true);
        }

        private void RefreshLocalPlayerUi(bool animate)
        {
            var player = session.State.FindPlayer(localPlayerId);
            if (player == null) return;
            gameplayInteractionHud?.MainModules?.RenderResources(player.Resources);
            gameplayInteractionHud?.MainModules?.RenderScore(player.Score);
            var characterView = characterCardPresenter == null
                ? CharacterCardPanelViewModel.Empty("角色牌信息尚未初始化。")
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            if (!characterView.CanUse)
            {
                showCharacterUseOptions = false;
            }
            gameplayInteractionHud?.MainModules?.RenderDiscardCount(
                characterView.DiscardCards == null ? 0 : characterView.DiscardCards.Count);
            characterHandPanel?.Render(localPlayerId, characterView);
            RefreshCharacterCoverSelection(characterView);
            RefreshBuildInfoPanel();
            RefreshBuildFacilityDialog();
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
            if (CardViewer.HasOpenViewer() || CardViewer.WasEscapeConsumedThisFrame()) return false;
            if (interactionEscapeConsumedFrame == Time.frameCount)
            {
                return true;
            }

            if (gameplayInteractionHud.GetComponent<GameplayInformationPages>()?.TryHandleEscape() == true)
            {
                interactionEscapeConsumedFrame = Time.frameCount;
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

            var hadMapConfirmation = interactionRouter.HasPendingMapConfirmation;
            var result = interactionRouter.OnEscape();
            if (result.Kind == InteractionResultKind.Passthrough)
            {
                return false;
            }

            interactionEscapeConsumedFrame = Time.frameCount;
            if (hadMapConfirmation) RefreshRoutedMapPresentation();
            return true;
        }

        public static bool WasInteractionEscapeConsumedThisFrame()
        {
            return interactionEscapeConsumedFrame == Time.frameCount;
        }

        private RectTransform GetUiCanvasTransform() =>
            uiCanvas == null ? null : uiCanvas.GetComponent<RectTransform>();

        private void HideEventCardOptions()
        {
            ClearEventCardInteraction();
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
                SetPrompt, commandId =>
                {
                    NotifyCoverCommandSettled(commandId);
                    NotifyCharacterUseSettled(commandId);
                    interactionRouter?.NotifyCommandSettled(commandId);
                }, (commandId, applied) => turnActionPresenter?.CityStyleInteraction.ResolveSubmission(commandId, applied));
            commandSubmission.Initialize();
        }

        private void BuildInteractionRouting()
        {
            interactionRouter = new InteractionRouter(SetPrompt);
            RegisterEventCardInteraction();
            interactionRouter.Register(new SpecialActionInteractionAdapter(specialActionInteraction));
            characterCardInteraction = new CharacterCardInteraction(
                characterCardEffectInteraction,
                characterMapInteraction,
                HasLocalPendingCharacterResolution,
                () => buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive,
                TryCancelCharacterFacilityEffectSelection);
            interactionRouter.Register(characterCardInteraction);
            interactionRouter.Register(new FacilityInteractionAdapter(facilityInteraction));
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
            RefreshFinalScoreFromState();
            gameplayInteractionHud.GetComponent<GameplayInformationPages>()?.Refresh();
        }

        private void SynchronizeInteractionFromState()
        {
            turnActionPresenter.SynchronizeFromState();
            interactionRouter?.ClearMapConfirmation();
            mapInteractionRouter?.ClearConfirmation(false);
            var preserveCollection = flowCoordinator.IsActive(resourceCollectionPresenter) && resourceCollectionPresenter.CanResumeDraft;
            if (!preserveCollection) flowCoordinator.ResetToChooseAction();
            if (!preserveCollection) ClearPendingDispatch();
            // 同一城市样式支付请求由路由器按 ID/版本刷新，保留收起状态与尚未支付的选择。
            if (preserveCollection) eventChoiceDialog.Hide();
            else if (specialActionInteraction == null || !specialActionInteraction.HasAuthoritativeRequest)
                HideEventCardOptions();
            RefreshAllFromState();
            if (preserveCollection) resourceCollectionPresenter.RefreshFromState();
            RefreshPendingChoiceOrHighlights();
            SynchronizeCharacterSettlementPresentation();
            PresentLatestCharacterSettlementBroadcast();
            TryBeginAutomaticSecondEffect();
            ObserveSharedCityStyleMirrorState();
            PresentLatestResourceSaleReceipt();
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
            if (SynchronizeEventCardInteraction())
            {
                return;
            }

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
            actionPanel = ActionPanelController.Bind(
                gameplayInteractionHud.ActionPanelView,
                gameplayInteractionHud.DialogRegistry.CardVisualCatalog,
                OnUseCharacterActionClicked,
                OnDeclareCityStyleClicked,
                BeginDeployAction, BeginDispatchAction, BeginExploreAction, BeginMoveAction,
                EndCurrentAction, OnBuildActionClicked, OnSpecialActionClicked);
            if (actionPanel == null) { Debug.LogError("[MobileCityInteractionController] 交互 HUD 行为绑定失败。", this); return false; }
            characterHandPanel = gameplayInteractionHud.CharacterHandPanel;
            if (characterHandPanel == null ||
                !characterHandPanel.Configure(
                    gameplayInteractionHud.DialogRegistry.CardVisualCatalog))
            {
                Debug.LogError("[MobileCityInteractionController] 手牌面板行为绑定失败。", this);
                return false;
            }
            gameplayInteractionHud.MainModules.ConfigureDiscardPreview(
                () => characterHandPanel?.OpenDiscardPreview());
            gameplayInteractionHud.MainModules.ConfigureCoveredPreview(
                () => characterHandPanel?.OpenCoveredCharacterCardViewer());
            RefreshActionPanel();
            return true;
        }

        private void RefreshActionPanel()
        {
            if (specialActionUsePage != null && specialActionUsePage.IsShowing && specialActionPageIsCurrent != null && !specialActionPageIsCurrent())
                specialActionUsePage.Hide();
            if (actionPanel == null || !actionPanel.IsReady || turnActionPresenter == null)
            {
                return;
            }

            var characterView = characterCardPresenter == null
                ? null
                : characterCardPresenter.BuildView(session.State, localPlayerId);
            if (characterUsePage != null && characterUsePage.IsShowing &&
                characterUsePageKey != CharacterUseKey(characterView))
            {
                characterUsePage.Hide();
                characterUsePageKey = string.Empty;
            }
            var actionViewModel = turnActionPresenter.BuildActionPanelViewModel();
            actionPanel.Render(actionViewModel);
            actionPanel.RenderCharacterQuickAction(characterView);
            gameplayInteractionHud.Frame.Refresh(session.State, actionViewModel,
                turnActionPresenter.ActionPanelPresenter.GetUnavailableEndActionPrompt(),
                actionViewModel.AllCollectionLocationsSelected
                    ? gameplayInteractionHud.ActionPanelView.AllCollectionLocationsSelectedText : string.Empty);
            gameplayInteractionHud.MainModules.Render(session.State, session.View, localPlayerId,
                gameplayInteractionHud.DialogRegistry.CardVisualCatalog);
            gameplayInteractionHud.MainModules.RenderDiscardCount(
                characterView == null || characterView.DiscardCards == null
                    ? 0 : characterView.DiscardCards.Count);
            if (characterView != null && characterView.IsSecondEffectDecision)
            {
                workflowView.ClearHighlights();
                eventChoiceDialog.Hide();
                ShowCharacterUsePage(characterView);
                SetPrompt(IsSecondEffectUnavailable()
                    ? "当前角色牌效果没有合法的地图目标，请使用结束操作完成结算。"
                    : "第一个角色牌效果已结算，可继续使用第二个效果，也可结束角色牌使用。");
                return;
            }

            if (SynchronizeEventCardInteraction())
            {
                return;
            }

            if (characterSettlementInProgress &&
                characterView != null &&
                characterView.IsSecondEffectExecution)
            {
                ShowCharacterUsePage(characterView);
            }
            var state = session == null ? null : session.State;
            if (state != null && state.HasPendingChoice())
            {
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
            var command = characterCardPresenter.CreateCoverCommand(localPlayerId, cardId);
            submittedCoverCommandId = command.CommandId;
            SubmitCharacterCardCommand(command, "角色牌已盖放。", "盖放角色牌命令已发送给主机，等待确认。");
        }

        private void SubmitUseCharacterCard(
            string effectMode,
            string effectOrder,
            IReadOnlyDictionary<string, string> effectParameters)
        {
            var player = session.State.FindPlayer(localPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CoveredCharacterCardId))
            {
                SetPrompt("当前没有可使用的盖放角色牌。");
                return;
            }

            characterSettlementInProgress = true;
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
                    "你的选择已提交，正在同步结算结果，无需主机代选。")
                {
                    BeforeRejectedPrompt = _ => interactionRouter?.NotifyCommandSettled(commandId),
                    AfterRejectedPrompt = _ =>
                    {
                        specialActionInteraction?.Synchronize();
                        facilityInteraction?.Synchronize();
                        SynchronizeEventCardInteraction();
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
                SynchronizeEventCardInteraction();
            }
        }

        private void SubmitCharacterCardCommand(GameCommand command, string localSuccessPrompt, string remotePrompt)
        {
            if (command != null && command.Kind == GameCommandKind.UseCharacterCard)
            {
                if (!string.IsNullOrEmpty(characterUseCommandId)) return;
                characterUseCommandId = command.CommandId;
            }
            commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    message =>
                    {
                        SetPrompt(message);
                        RefreshLocalPlayerUi();
                    },
                    remotePrompt)
                {
                    BeforeRejectedPrompt = _ =>
                    {
                        characterSettlementInProgress = false;
                        NotifyCoverCommandSettled(command == null ? string.Empty : command.CommandId);
                        NotifyCharacterUseSettled(command == null ? string.Empty : command.CommandId);
                    },
                    OnAppliedLocally = _ => CompleteLocalCharacterCardCommand(
                        command,
                        localSuccessPrompt)
                });
        }

        private void CompleteLocalCharacterCardCommand(GameCommand command, string localSuccessPrompt)
        {
            NotifyCoverCommandSettled(command == null ? string.Empty : command.CommandId);
            NotifyCharacterUseSettled(command == null ? string.Empty : command.CommandId);
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
                    ? "当前角色牌效果没有合法的地图目标，请使用结束操作完成结算。"
                    : "第一个角色牌效果已完成，可继续使用第二个效果，也可结束角色牌使用。");
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

        private CharacterCardEffectChoiceDialog specialActionUsePage;
        private Func<bool> specialActionPageIsCurrent;

        private void OnSpecialActionClicked()
        {
            var interaction = turnActionPresenter.CityStyleInteraction;
            if (!interaction.CanUseSpecialAction) return;
            var owner = session;
            var player = localPlayerId;
            var revision = session.State.EffectRuntime?.StateRevision ?? 0;
            bool Current() => session == owner && localPlayerId == player &&
                (session.State.EffectRuntime?.StateRevision ?? 0) == revision &&
                interaction.CanUseSpecialAction;
            var options = new System.Collections.Generic.List<EffectDialogOption>();
            foreach (var option in interaction.QuerySpecialActions().Options)
            {
                var captured = option;
                options.Add(new EffectDialogOption(option.Name + "\n" + option.Description +
                    (option.CanUse ? option.Warning : "\n" + option.DisabledReason),
                    () => interaction.SubmitSpecialAction(captured.SpecialActionId, captured.DeclarationMarkerId),
                    option.CanUse) { StableId = option.DeclarationMarkerId + ":" + option.SpecialActionId,
                        SourceLabel = option.Name,
                        DescriptionLabel = option.Description + (option.CanUse ? option.Warning : "\n" + option.DisabledReason) });
            }
            if (specialActionUsePage == null) specialActionUsePage = new CharacterCardEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry, uiCanvas.transform as RectTransform);
            specialActionPageIsCurrent = Current;
            var copy = specialActionUsePage.Copy;
            specialActionUsePage.ShowExecutionChoices(copy.SpecialActionTitle, copy.SpecialActionDescription,
                options, Current);
        }

        private void OnBuildActionClicked()
        {
            if (turnActionPresenter.BuildActionPanelViewModel().CanViewFacilitySupply)
            {
                supplyInspectionActive = true;
                selectedBuildFacilityId = string.Empty;
                PresentFacilitySupplyInspection();
                return;
            }
            if (buildDialogActive && turnActionPresenter?.BuildBuildFacilityDraftViewModel() != null)
                return;
            buildDialogActive = true;
            buildDialogStep = BuildDialogStep.Facilities;
            selectedBuildFacilityId = string.Empty;
            turnActionPresenter.BeginBuildAction();
            buildFacilityInteraction?.Synchronize();
            buildInfoPanel?.SetExternalFacilitySupplyVisible(false);
            PresentBuildFacilityDialog();
        }

        private void RefreshBuildFacilityDialog()
        {
            if (supplyInspectionActive)
            {
                if (turnActionPresenter != null && turnActionPresenter.BuildActionPanelViewModel().CanViewFacilitySupply)
                    PresentFacilitySupplyInspection();
                else CloseFacilitySupplyInspection();
                return;
            }
            if (!buildDialogActive || turnActionPresenter == null) return;
            buildInfoPanel?.SetExternalFacilitySupplyVisible(false);
            if (turnActionPresenter.BuildInteraction.IsSubmissionInFlight) return;
            var state = session == null ? null : session.State;
            if (state == null || state.CurrentPlayerId != localPlayerId ||
                (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2))
            {
                turnActionPresenter.CancelBuildFacility();
                buildFacilityInteraction?.Synchronize();
                buildInfoPanel?.SetExternalFacilitySupplyVisible(
                    buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive);
                buildDialogActive = false;
                buildDialogStep = BuildDialogStep.None;
                selectedBuildFacilityId = string.Empty;
                characterCardEffectChoiceDialog?.Hide();
                if (!buildDialogActive && facilityBuildPage != null) facilityBuildPage.Hide();
                return;
            }
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model == null)
            {
                buildInfoPanel?.SetExternalFacilitySupplyVisible(
                    buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive);
                buildDialogActive = false;
                buildDialogStep = BuildDialogStep.None;
                selectedBuildFacilityId = string.Empty;
                characterCardEffectChoiceDialog?.Hide();
                if (!buildDialogActive && facilityBuildPage != null) facilityBuildPage.Hide();
                return;
            }
            if (model.Phase == BuildFacilityDraftPhase.Selecting)
                buildDialogStep = BuildDialogStep.Facilities;
            else if (model.Phase == BuildFacilityDraftPhase.Dragging)
                buildDialogStep = BuildDialogStep.Slots;
            else if (model.Phase == BuildFacilityDraftPhase.Focused)
                buildDialogStep = BuildDialogStep.Payments;
            else if (model.Phase == BuildFacilityDraftPhase.Confirming)
                buildDialogStep = BuildDialogStep.Quote;
            PresentBuildFacilityDialog();
        }

        private FacilityBuildDialogView facilityBuildPage;
        private bool supplyInspectionActive;

        private void PresentFacilitySupplyInspection()
        {
            if (!supplyInspectionActive || session?.State == null) return;
            var owner = session;
            var player = localPlayerId;
            bool Current() => supplyInspectionActive && session == owner && localPlayerId == player &&
                turnActionPresenter.BuildActionPanelViewModel().CanViewFacilitySupply;
            var model = new BuildFacilityDraftViewModel(BuildFacilityDraftPhase.Selecting,
                new BuildFacilityOptionQueryService().Query(session.State, player), null, null, -1,
                string.Empty, string.Empty, null, _ => { });
            if (facilityBuildPage == null)
                facilityBuildPage = gameplayInteractionHud.DialogRegistry.InstantiateFacilityBuild(uiCanvas.transform as RectTransform);
            facilityBuildPage.RefreshSupplyStatus(session.State, localPlayerId);
            facilityBuildPage.Show(model, session.State.Map.Facilities, player,
                gameplayInteractionHud.DialogRegistry.CardVisualCatalog,
                gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile,
                selectedBuildFacilityId, string.Empty, Current,
                id => { if (Current()) { selectedBuildFacilityId = id; PresentFacilitySupplyInspection(); } },
                _ => { }, _ => { }, () => { }, CloseFacilitySupplyInspection, true);
            SetPrompt(string.Empty);
        }

        private void CloseFacilitySupplyInspection()
        {
            supplyInspectionActive = false;
            selectedBuildFacilityId = string.Empty;
            if (facilityBuildPage != null) facilityBuildPage.Hide();
        }

        private void PresentBuildFacilityDialog()
        {
            if (!buildDialogActive || turnActionPresenter == null) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model == null) return;
            characterCardEffectChoiceDialog?.Hide();
            eventChoiceDialog.Hide();
            if (facilityBuildPage == null)
                facilityBuildPage = gameplayInteractionHud.DialogRegistry.InstantiateFacilityBuild(uiCanvas.transform as RectTransform);
            facilityBuildPage.RefreshSupplyStatus(session.State, localPlayerId);
            facilityBuildPage.Show(model, session.State.Map.Facilities, localPlayerId,
                gameplayInteractionHud.DialogRegistry.CardVisualCatalog,
                gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile,
                selectedBuildFacilityId, BuildQuoteSummary(model), IsCurrentBuildDialog,
                SelectBuildCardInPanel, SelectBuildSlotInPanel, PayBuildFacilityInPanel,
                ConfirmBuildFacilityQuote, CancelBuildFacilityDialog);
            // 建设提示由建设页的底部信息区显示。
            SetPrompt(string.Empty);
        }

        private void PayBuildFacilityInPanel(string mode)
        {
            if (!IsCurrentBuildDialog()) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model?.SelectedOption == null || model.SelectedOption.FacilityId != selectedBuildFacilityId) return;
            if (!turnActionPresenter.BuildInteraction.TryPreparePayment(mode))
            {
                PresentBuildFacilityDialog();
                return;
            }
            ConfirmBuildFacilityQuote();
        }

        private void SelectBuildCardInPanel(string id)
        {
            if (!IsCurrentBuildDialog()) return;
            selectedBuildFacilityId = id;
            turnActionPresenter.BuildInteraction.SelectFacility(id);
            PresentBuildFacilityDialog();
        }

        private void SelectBuildSlotInPanel(int index)
        {
            if (!IsCurrentBuildDialog()) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model.Phase != BuildFacilityDraftPhase.Dragging)
                turnActionPresenter.BeginBuildFacilityDrag(selectedBuildFacilityId);
            SelectBuildFacilitySlot(index);
        }

        private bool IsCurrentBuildDialog()
        {
            var state = session == null ? null : session.State;
            return buildDialogActive && state != null &&
                   state.CurrentPlayerId == localPlayerId &&
                   (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2) &&
                   turnActionPresenter != null &&
                   !turnActionPresenter.BuildInteraction.IsSubmissionInFlight &&
                   turnActionPresenter.BuildBuildFacilityDraftViewModel() != null;
        }

        private void SelectBuildFacility(string facilityId)
        {
            if (!IsCurrentBuildDialog()) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model?.Options == null) return;
            foreach (var option in model.Options)
                if (option != null && option.FacilityId == facilityId && option.CanBuild)
                {
                    selectedBuildFacilityId = facilityId;
                    PresentBuildFacilityDialog();
                    return;
                }
        }

        private void ConfirmBuildFacilityCandidate()
        {
            if (!IsCurrentBuildDialog() || string.IsNullOrEmpty(selectedBuildFacilityId)) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model?.Options == null) return;
            foreach (var option in model.Options)
                if (option != null && option.FacilityId == selectedBuildFacilityId && option.CanBuild)
                {
                    turnActionPresenter.BeginBuildFacilityDrag(selectedBuildFacilityId);
                    buildFacilityInteraction?.Synchronize();
                    var updated = turnActionPresenter.BuildBuildFacilityDraftViewModel();
                    if (updated == null) return;
                    buildDialogStep = updated.Phase == BuildFacilityDraftPhase.Dragging
                        ? BuildDialogStep.Slots : BuildDialogStep.Facilities;
                    PresentBuildFacilityDialog();
                    return;
                }
        }

        private void SelectBuildFacilitySlot(int slotIndex)
        {
            if (!IsCurrentBuildDialog()) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model?.SelectedOption == null) return;
            var legal = false;
            foreach (var slot in model.SelectedOption.SlotOptions)
                if (slot != null && slot.CityBoardSlotIndex == slotIndex && slot.IsLegal)
                { legal = true; break; }
            if (!legal) return;
            turnActionPresenter.DropBuildFacility(slotIndex);
            buildFacilityInteraction?.Synchronize();
            var updated = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            buildDialogStep = updated != null && updated.Phase == BuildFacilityDraftPhase.Focused
                ? BuildDialogStep.Payments : BuildDialogStep.Slots;
            PresentBuildFacilityDialog();
        }

        private void SelectBuildPayment(string paymentMode)
        {
            if (!IsCurrentBuildDialog()) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model?.SelectedOption == null) return;
            var available = paymentMode == BuildFacilityService.PaymentModeResources
                ? model.SelectedOption.ResourcesPayment.IsAvailable
                : paymentMode == BuildFacilityService.PaymentModeGold &&
                  model.SelectedOption.GoldPayment.IsAvailable;
            if (!available) return;
            turnActionPresenter.SelectBuildFacilityPayment(paymentMode);
            buildFacilityInteraction?.Synchronize();
            var updated = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            buildDialogStep = updated != null && updated.Phase == BuildFacilityDraftPhase.Confirming
                ? BuildDialogStep.Quote
                : BuildDialogStep.Payments;
            PresentBuildFacilityDialog();
        }

        private void ConfirmBuildFacilityQuote()
        {
            if (!IsCurrentBuildDialog()) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model == null || model.Phase != BuildFacilityDraftPhase.Confirming ||
                model.SelectedOption == null || !model.SelectedOption.CanBuild ||
                (model.PaymentMode == BuildFacilityService.PaymentModeResources &&
                 !model.SelectedOption.ResourcesPayment.IsAvailable) ||
                (model.PaymentMode == BuildFacilityService.PaymentModeGold &&
                 !model.SelectedOption.GoldPayment.IsAvailable))
            {
                RefreshBuildFacilityDialog();
                return;
            }

            turnActionPresenter.ConfirmBuildFacility();
            buildFacilityInteraction?.Synchronize();
            if (turnActionPresenter.BuildInteraction.IsSubmissionInFlight) return;
            var current = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (current == null)
            {
                buildInfoPanel?.SetExternalFacilitySupplyVisible(
                    buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive);
                buildDialogActive = false;
                buildDialogStep = BuildDialogStep.None;
                characterCardEffectChoiceDialog?.Hide();
                if (facilityBuildPage != null) facilityBuildPage.Hide();
                SynchronizeEventCardInteraction();
                return;
            }
            buildDialogStep = BuildDialogStep.Quote;
            PresentBuildFacilityDialog();
        }

        private void ReturnToBuildPayments()
        {
            if (!IsCurrentBuildDialog()) return;
            turnActionPresenter.BackToBuildFacilityPayment();
            buildFacilityInteraction?.Synchronize();
            var updated = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            buildDialogStep = updated != null && updated.Phase == BuildFacilityDraftPhase.Focused
                ? BuildDialogStep.Payments : BuildDialogStep.Quote;
            PresentBuildFacilityDialog();
        }

        private void CancelBuildFacilityDialog()
        {
            if (!buildDialogActive || turnActionPresenter == null) return;
            turnActionPresenter.CancelBuildFacility();
            buildFacilityInteraction?.Synchronize();
            buildInfoPanel?.SetExternalFacilitySupplyVisible(
                buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive);
            buildDialogActive = false;
            buildDialogStep = BuildDialogStep.None;
            selectedBuildFacilityId = string.Empty;
            characterCardEffectChoiceDialog?.Hide();
                if (!buildDialogActive && facilityBuildPage != null) facilityBuildPage.Hide();
            RefreshActionPanel();
        }

        private string BuildQuoteSummary(BuildFacilityDraftViewModel model)
        {
            var facilityName = model?.Facility == null ? string.Empty : model.Facility.Name;
            var profile = gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile;
            var paymentName = model?.PaymentMode == BuildFacilityService.PaymentModeGold
                ? profile.BuildPaymentGoldLabel : profile.BuildPaymentResourcesLabel;
            var slot = model == null ? 0 : model.CityBoardSlotIndex + 1;
            var option = model?.SelectedOption;
            if (option == null) return profile.BuildQuotePendingLabel;
            var original = model.Facility == null ? new ResourceSet() : model.Facility.ResourceCost;
            var resources = option.EffectiveResourceCost;
            var gold = model.Facility == null ? 0 : model.Facility.GoldVoucherCost;
            var originalText = string.Format(profile.BuildResourceCostFormat,
                original.Originium, original.OriginiumShard, original.Iron, original.PureOriginium);
            var currentText = string.Format(profile.BuildResourceCostFormat,
                resources.Originium, resources.OriginiumShard, resources.Iron, resources.PureOriginium);
            var quote = string.Format(profile.BuildPaymentQuoteFormat, originalText, currentText, gold);
            var status = string.IsNullOrEmpty(model.ErrorMessage)
                ? profile.BuildQuoteValidLabel : model.ErrorMessage;
            return facilityName + " · " + string.Format(profile.BuildSlotLabelFormat, slot) +
                   " · " + paymentName + "\n" + quote + "\n" + status;
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
            RefreshLocalPlayerUi();
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

        private void RefreshFinalScoreFromState()
        {
            var finalScore = FindObjectOfType<FinalScoreController>();
            if (finalScore != null)
            {
                finalScore.RefreshFromState(session.State);
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

        private void SetPrompt(string message) => gameplayInteractionHud?.Frame?.SetInteractionMessage(
            message, interactionRouter?.GetPendingPrompt());

    }

}
