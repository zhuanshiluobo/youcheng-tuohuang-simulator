using System;
using System.Collections.Generic;
using System.Linq;
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
using YC.Domain.Presentation;
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
        private static readonly Unity.Profiling.ProfilerMarker LocalPlayerUiRefreshMarker = new Unity.Profiling.ProfilerMarker("YC.LocalPlayerUiRefresh");
        private static readonly Unity.Profiling.ProfilerMarker ActionPanelRefreshMarker = new Unity.Profiling.ProfilerMarker("YC.ActionPanelRefresh");
        private static readonly Unity.Profiling.ProfilerMarker BuildInfoRefreshMarker = new Unity.Profiling.ProfilerMarker("YC.BuildInfoRefresh");
        private static readonly Unity.Profiling.ProfilerMarker ActionPanelViewModelMarker = new Unity.Profiling.ProfilerMarker("YC.ActionPanelViewModel");
        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private MapView mapViewBinding;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;
        [SerializeField] private GameSettingsMenuController settingsMenu;
        [SerializeField] private GameplayInteractionHudView gameplayInteractionHud;
        [SerializeField] private BuildInfoPanel buildInfoPanel;

        private GameSession session;
        private YC.Domain.Effects.EffectRegistry sessionEffectRegistry;
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

        public GameState CurrentState => ReadUiStateWithTrace();
        // 由共享装配提供活动目录，禁止在此构造平行目录或读取 Host 实例来补显示名。
        public void ConfigureGameplayPresentation(YC.Domain.Presentation.IGameplayTextCatalogSnapshot catalog,
            IReadOnlyDictionary<string, string> definitionNames)
            => gameplayInteractionHud.ConfigureGameplayPresentation(catalog, definitionNames);
        private PendingCardChoiceView CurrentPendingChoice =>
            session == null ? null : CardFlowStateAdapter.GetPendingChoiceView(ReadUiStateWithTrace());
        private RouteResourceSelectionInteractionUiCoordinator routeResourceSelection;
        private BuildFacilityOptionQueryService buildFacilityOptions;
        private FacilityBuildCandidateCache supplyInspectionCandidates;
        private bool routeCanStopPendingCharacterMode;
        private Func<bool> routeStopSnapshotIsCurrent;

        private string pendingEffectCommandId = string.Empty;
        private bool CanStopCurrentEffect => CanSubmitGameplayCommand && string.IsNullOrEmpty(pendingEffectCommandId) &&
            !GameplayHudFrame.EffectInputSuspended && interactionRequestRouter?.CurrentRequest?.AllowDecline == true &&
            interactionRequestRouter.CurrentRequest.AnsweringPlayerId == localPlayerId;
        private bool CanUseSecondModeStop => (interactionRequestRouter?.CurrentRequest == null ||
            interactionRequestRouter.CurrentRequest.InteractionTypeId == YC.Domain.Cards.CharacterLifecycleService.SecondModeInteractionTypeId) &&
            CanStopPendingCharacterMode;

        public bool CanEndCurrentAction()
        {
            using var latency = InteractionLatencyTrace.Begin("gate.end_evaluate");
            if (!CanSubmitGameplayCommand) return TraceEndGate(false, "submit.unavailable");
            if (!string.IsNullOrEmpty(pendingEffectCommandId)) return TraceEndGate(false, "effect.command_pending");
            if (GameplayHudFrame.EffectInputSuspended) return TraceEndGate(false, "effect.input_suspended");
            var view = VisibleSaleState();
            if (!string.IsNullOrEmpty(view?.ActiveSettlementWindowId))
            {
                var end = view.SettlementEndAction;
                if (end == null) return TraceEndGate(false, "settlement.end_missing", view);
                if (end.WindowId != view.ActiveSettlementWindowId) return TraceEndGate(false, "settlement.window_mismatch", view);
                if (end.StateRevision != view.StateRevision) return TraceEndGate(false, "settlement.revision_mismatch", view);
                return string.IsNullOrEmpty(end.CurrentNodeEffectId)
                    ? TraceEndGate(end.CanCloseCleanupWindow, "settlement.can_close", view)
                    : TraceEndGate(end.CanStopCurrentNode, "settlement.can_stop_node", view);
            }
            if (CanStopCurrentEffect || CanUseSecondModeStop) return TraceEndGate(true, "effect.can_stop", view);
            if (routeResourceSelection?.IsActive == true) return TraceEndGate(routeResourceSelection.CanConfirm, "route.can_confirm", view);
            if (interactionRequestRouter?.CurrentRequest != null) return TraceEndGate(false, "request.open", view);
            if (turnActionPresenter == null) return TraceEndGate(false, "turn.presenter_missing", view);
            return TraceEndGate(turnActionPresenter.CanEndCurrentAction(), "turn.can_end", view);
        }

        public void EndCurrentAction()
        {
            using var latency = InteractionLatencyTrace.Begin("click.end_action");
            if (!CanSubmitGameplayCommand || !string.IsNullOrEmpty(pendingEffectCommandId) || GameplayHudFrame.EffectInputSuspended) return;
            var view = VisibleSaleState();
            if (!string.IsNullOrEmpty(view?.ActiveSettlementWindowId))
            {
                var end = view.SettlementEndAction;
                if (end == null || end.WindowId != view.ActiveSettlementWindowId || end.StateRevision != view.StateRevision ||
                    !(string.IsNullOrEmpty(end.CurrentNodeEffectId) ? end.CanCloseCleanupWindow : end.CanStopCurrentNode)) return;
                var command = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = localPlayerId };
                command.Parameters["windowId"] = end.WindowId;
                command.Parameters["currentNodeEffectId"] = end.CurrentNodeEffectId;
                command.Parameters["interactionId"] = end.InteractionId;
                command.Parameters["expectedRevision"] = end.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                SubmitPendingEffectCommand(command); return;
            }
            var request = interactionRequestRouter?.CurrentRequest;
            if (CanStopCurrentEffect)
            {
                var current = GetVisibleInteractionRequests()?.FirstOrDefault(value =>
                    value.InteractionId == request.InteractionId && value.StateRevision == request.StateRevision &&
                    value.CandidateSetId == request.CandidateSetId && value.CandidateSetVersion == request.CandidateSetVersion &&
                    value.Status == "open" && value.AnsweringPlayerId == localPlayerId && value.AllowDecline);
                if (current != null)
                    SubmitPendingEffectCommand(YC.Application.Interactions.EffectInteractionCommands.Answer(request, localPlayerId, null, true));
                return;
            }
            if (CanUseSecondModeStop && TryStopPendingCharacterMode()) return;
            if (routeResourceSelection?.IsActive == true) routeResourceSelection.Confirm();
            else if (request == null) turnActionPresenter?.EndCurrentAction();
        }
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
            buildInfoPanel.DialogRegistry = gameplayInteractionHud.DialogRegistry;
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
                () => ReadUiStateWithTrace(),
                () => localPlayerId,
                ShouldControlCurrentPlayerLocally,
                SetLocalViewer,
                () => commandSubmission, () => mapView?.RefreshScoreTrackDisplay(ReadUiStateWithTrace()));
            commandGateway = new CommandGateway(gameplayAdapter);
            workflowView = new MobileCityWorkflowViewAdapter(
                () => ReadUiStateWithTrace(),
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
            workflowView.BindBuildDraftView(ShowBuildDraftInOriginalPanel, HideBuildDraftInOriginalPanel);
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
                explorationEventPresenter,
                () => CanStopCurrentEffect || CanUseSecondModeStop, buildFacilityOptions);
            turnActionPresenter.ActionPanelPresenter.CharacterCoverPrompt =
                gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile.CharacterCoverSelectionDescription;
            characterCardPresenter = new CharacterCardPanelPresenter(
                new CharacterCardOptionQueryService(
                    mapQuery,
                    influenceService,
                    movementService));
            characterCardPresenter.ResolveGameplayText = gameplayInteractionHud.ExpansionUi.ResolveGameplayText;
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
                () => ShouldControlCurrentPlayerLocally() ? ReadUiStateWithTrace()?.CurrentPlayerId ?? localPlayerId : localPlayerId,
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
                    gameplayInteractionHud.DialogRegistry, VisibleSaleState);
            EnsureSettingsMenu();
            ShowInitialPlacementChoices();
            facilityInteraction = new FacilityInteractionUiCoordinator(
                () => ReadUiStateWithTrace(),
                () => localPlayerId,
                GetUiCanvasTransform,
                gameplayInteractionHud.DialogRegistry,
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                SubmitPendingEffectCommand,
                SetPrompt);
            facilityInteraction.GetVisibleState = VisibleSaleState;
            facilityInteraction.ExpansionUi = gameplayInteractionHud.ExpansionUi;
            specialActionInteraction = new SpecialActionInteractionUiCoordinator(
                () => ReadUiStateWithTrace(),
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
            specialActionInteraction.ExpansionUi = gameplayInteractionHud.ExpansionUi;
            specialActionInteraction.QueryPaymentSources = query => commandSubmission?.ReadPaymentSourcePage(query);
            specialActionInteraction.CanSubmitCommand = () => CanSubmitGameplayCommand && string.IsNullOrEmpty(pendingEffectCommandId);
            specialActionInteraction.PaymentDraftStateChanged = gameplayInteractionHud.ExpansionUi.InvalidateSettlementAvailability;
            gameplayInteractionHud.ExpansionUi.CanInvokeSettlement = option => specialActionInteraction?.CanInvokeSettlement(option) != false;
            buildFacilityInteraction?.Dispose();
            buildFacilityInteraction = new BuildFacilityInteractionUiCoordinator(
                buildInfoPanel, turnActionPresenter);
            buildFacilityInteraction.Refresh(ReadUiStateWithTrace(), localPlayerId);
            characterMapInteraction = new CharacterMapInteractionCoordinator(
                () => ReadUiStateWithTrace(),
                () => localPlayerId,
                characterCardPresenter,
                highlights => workflowView.SetHighlights(highlights),
                () => workflowView.ClearHighlights(),
                (mode, parameters) => SubmitUseCharacterCard(mode, string.Empty, parameters),
                SubmitResolvePendingCharacterChoice,
                SetPrompt);
            characterMapInteraction.ResolveGameplayText = gameplayInteractionHud.ExpansionUi.ResolveGameplayText;
            characterMapInteraction.ShowGameplayPrompt = SetPrompt;
            BuildCharacterCardEffectInteraction();
            BuildInteractionRouting();
            BuildCommandSubmission(GameLaunchContext.Instance);
            // 完成初始热座视角同步，避免把读档时的玩家切换延迟到第一笔付款准备回执。
            StartInteractionTrace();
            SynchronizeInteractionFromState();
            turnActionPresenter.RestorePreparedMove();
            RefreshPendingChoiceOrHighlights();
            PrepareRightCardSmokePresentation();
        }

        private void Update()
        {
            PulseInteractionTrace();
            workflowView?.RefreshCityStylePreview();
            gameplayInteractionHud?.Frame?.UpdateInteractionMessage(
                (mapInteractionRouter != null && mapInteractionRouter.HasPendingConfirmation) ||
                (interactionRouter != null && interactionRouter.HasPendingMapConfirmation));
            buildFacilityInteraction?.Synchronize();
            // 存档窗口只拦截局内输入；关闭当帧仍由窗口消费，状态和付款同步继续。
            if (MatchSaveWindowView.IsAnyOpen || MatchSaveWindowView.WasClosedThisFrame) return;
            if (UpdateEffectMapConfirmationCancellation()) return;
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
            StopInteractionTrace();
            uiShuttingDown = true;
            workflowView?.BeginShutdown();
            // 场景销毁只释放 UI 意图，不能把保存中的领域付款准备当成玩家主动取消。
            turnActionPresenter?.BeginShutdown();
            gameplayInteractionHud?.ExpansionUi?.BeginShutdown();
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
            using var visibleRead = BeginVisibleRead();
            interactionRouter.OnLocationClicked(locationId);
        }

        private int lastMapButtonInputFrame = -1;

        public void OnMapButtonClicked(WorkflowHighlightTargetKind kind, string targetId)
        {
            using var latency = InteractionLatencyTrace.Begin("click.map");
            using var visibleRead = BeginVisibleRead();
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
            using var visibleRead = BeginVisibleRead();
            interactionRouter.OnMobileCityClicked();
        }

        public void OnInfluenceSlotClicked(string slotId)
        {
            using var visibleRead = BeginVisibleRead();
            interactionRouter.OnInfluenceSlotClicked(slotId);
        }

        private void CompleteActionCommandUi(string actionName)
        {
            using var visibleRead = BeginVisibleRead();
            using var characterRead = characterCardPresenter?.BeginPresentationRead();
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
            using var refreshSample = LocalPlayerUiRefreshMarker.Auto();
            using var visibleRead = BeginVisibleRead();
            var player = ReadUiStateWithTrace()?.FindPlayer(localPlayerId);
            if (player == null) return;
            gameplayInteractionHud?.MainModules?.RenderResources(player.Resources);
            gameplayInteractionHud?.MainModules?.RenderScore(player.Score);
            var characterView = characterCardPresenter == null
                ? CharacterCardPanelViewModel.Empty("角色牌信息尚未初始化。")
                : characterCardPresenter.BuildProjectedView(ReadUiStateWithTrace(), localPlayerId, VisibleSaleState());
            if (!characterView.CanUse)
            {
                showCharacterUseOptions = false;
            }
            RefreshCharacterCoverSelection(characterView);
            RefreshBuildInfoPanel(characterView);
            RefreshBuildFacilityDialog();
        }

        private void RefreshBuildInfoPanel(CharacterCardPanelViewModel characterView = null)
        {
            using var refreshSample = BuildInfoRefreshMarker.Auto();
            if (buildInfoPanel == null || session == null || ReadUiStateWithTrace() == null)
            {
                return;
            }

            if (buildFacilityInteraction != null)
            {
                buildFacilityInteraction.Refresh(ReadUiStateWithTrace(), localPlayerId);
            }
            else
            {
                buildInfoPanel.Refresh(ReadUiStateWithTrace(), localPlayerId);
            }
            RefreshViewedCityPanel(characterView);
        }

        private int displayedCityPlayerId = -1, cityInspectionViewer = -1;
        private string cityInspectionGame;
        private void InspectCity(int playerId)
        {
            using var latency = InteractionLatencyTrace.Begin("local.inspect_city");
            displayedCityPlayerId = playerId < 0 ? localPlayerId : playerId;
            RefreshViewedCityPanel();
        }
        private void RefreshViewedCityPanel(CharacterCardPanelViewModel characterView = null)
        {
            using var visibleRead = BeginVisibleRead();
            var visible = VisibleSaleState();
            if (visible == null || visible.ViewerPlayerId != localPlayerId)
            {
                displayedCityPlayerId = localPlayerId;
                cityInspectionViewer = -1;
                cityInspectionGame = null;
                buildInfoPanel?.RefreshViewedCity(null, localPlayerId);
                gameplayInteractionHud.MainModules.RenderCityInspection(null, localPlayerId);
                characterHandPanel?.RenderInspection(null);
                gameplayInteractionHud.ExpansionUi.RefreshViewedEntrepreneurCards(null, localPlayerId);
                return;
            }
            if (cityInspectionGame != visible.GameId || cityInspectionViewer != localPlayerId ||
                !visible.Players.Exists(p => p.PlayerId == displayedCityPlayerId)) displayedCityPlayerId = localPlayerId;
            cityInspectionGame = visible.GameId;
            cityInspectionViewer = localPlayerId;
            gameplayInteractionHud.ExpansionUi.RefreshViewedEntrepreneurCards(visible, displayedCityPlayerId);
            buildInfoPanel?.RefreshViewedCity(visible, displayedCityPlayerId);
            gameplayInteractionHud.MainModules.RenderCityInspection(visible, displayedCityPlayerId);
            var piles = ViewedCharacterPilesViewModel.BuildInspectionView(visible, displayedCityPlayerId);
            if (displayedCityPlayerId == localPlayerId)
            {
                characterView = characterView ?? characterCardPresenter?.BuildProjectedView(ReadUiStateWithTrace(), localPlayerId, visible);
                characterHandPanel?.Render(localPlayerId, characterView, piles);
            }
            else characterHandPanel?.RenderInspection(piles);
        }

        public bool TryHandleInteractionEscape()
        {
            if (MatchSaveWindowView.IsAnyOpen || MatchSaveWindowView.WasClosedThisFrame) return false;
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

        private void HideEventCardOptions(bool preserveRequestDraft)
        {
            if (!preserveRequestDraft || !CanPreserveRequestDraftForRefresh())
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
            var result = GameSessionBootstrapper.BuildForGameplay(
                GameLaunchContext.Instance,
                useRightCardSmokeState,
                prepareSharedCityStyleSmokeState);
            session = result.Session;
            sessionEffectRegistry = result.EffectRegistry;
            gameplayInteractionHud.DialogRegistry.CardVisualCatalog.BindRuntimeDefinitions(
                CreateWeakRuntimeDefinitionResolver(this));
            mapQuery = result.MapQuery;
            influenceService = result.InfluenceService;
            movementService = result.MovementService;
            explorationService = result.ExplorationService;
            resourceCollectionService = result.ResourceCollectionService;
            localPlayerId = result.LocalPlayerId;
            // 此时尚未调用 BuildCommandSubmission/绑定网络owner，仅初始化启动展示。
            if (!ShouldControlCurrentPlayerLocally()) session.RefreshLocalProjection(GameStateViewer.Player(localPlayerId));
            gameplayInteractionHud.ConfigureGameplayPresentation(result.GameplayTextCatalog, result.GameplayDefinitionNames);
            buildFacilityOptions = new BuildFacilityOptionQueryService(
                new BuildFacilityService(), result.FacilityBuildCandidates);
            supplyInspectionCandidates = new FacilityBuildCandidateCache(buildFacilityOptions);
            gameplayInteractionHud.ExpansionUi.Configure(VisibleSaleState, () => localPlayerId,
                result.EnterpriseDefinitions, result.CharacterModes, SubmitPendingEffectCommand);
            gameplayInteractionHud.ExpansionUi.BindSessionPresentation(result.ActivePresentation, () => session);
            gameplayInteractionHud.ExpansionUi.CreateSnapshotGuard = CreateVisibleSnapshotGuard;
            gameplayInteractionHud.ExpansionUi.CharacterDefinition = id => CharacterCardDatabase.Get(ReadUiStateWithTrace(), id);
            gameplayInteractionHud.ExpansionUi.DetailDefinitions = result.PlayerDetailDefinitions;
        }

        // 单独静态作用域只捕获弱句柄，避免与BuildSession中的其它this回调共用闭包。
        private static Func<string, string> CreateWeakRuntimeDefinitionResolver(MobileCityInteractionController owner)
        {
            var visibleController = new System.WeakReference<MobileCityInteractionController>(owner);
            return id => visibleController.TryGetTarget(out var activeController)
                ? YC.Domain.Rules.GameContentDefinitions.ResolveDefinitionId(activeController.ReadUiStateWithTrace(), id) : id;
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
                    NotifyPendingEffectSettled(commandId);
                    gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(commandId);
                    interactionRouter?.NotifyCommandSettled(commandId);
                }, (commandId, applied) =>
                {
                    turnActionPresenter?.CityStyleInteraction.ResolveSubmission(commandId, applied);
                    specialActionInteraction?.NotifyPaymentDraftSettled(commandId, applied);
                }, (gameId, commandId, accepted, errorCode) =>
                {
                    // 准确拒绝可能保持同一revision，仍须让原路由重新呈现已解除等待的请求。
                    simulationUiResultRequiresSynchronization = commandSubmission?.UsesDeterministicSimulation == true;
                    using (InteractionLatencyTrace.Begin("callback.mobile.release_wait", commandId))
                        ReleaseRejectedSubmissionWait(gameId, commandId, accepted);
                    using (InteractionLatencyTrace.Begin("callback.mobile.cover_result", commandId))
                        NotifyCoverCommandResolved(gameId, commandId, accepted, errorCode);
                    using (InteractionLatencyTrace.Begin("callback.mobile.router_result", commandId))
                        interactionRouter?.NotifyInteractionCommandResolved(gameId, commandId, accepted, errorCode);
                });
            commandSubmission.PaymentSourceViewerPlayerId = () => localPlayerId;
            commandSubmission.Initialize(sessionEffectRegistry);
            MatchSaveController.Ensure().Bind(session, launchContext);
        }

        private void ReleaseRejectedSubmissionWait(string gameId, string commandId, bool accepted)
        {
            // 共享提交账本已核验提交身份；本层只释放当前局准确失败命令的本地等待。
            if (accepted || string.IsNullOrEmpty(gameId) || gameId != ReadUiStateWithTrace()?.GameId ||
                string.IsNullOrEmpty(commandId)) return;
            NotifyPendingEffectSettled(commandId);
            if (commandId == characterUseCommandId) characterSettlementInProgress = false;
            NotifyCharacterUseSettled(commandId);
        }

        private void BuildInteractionRouting()
        {
            turnActionPresenter.ActionPanelPresenter.HasCommittedSettlement = HasCommittedSettlement;
            turnActionPresenter.HasExplorationDraft = () => routeResourceSelection?.HasUnsubmittedMainActionDraft == true;
            turnActionPresenter.HasExplorationSubmission = () => routeResourceSelection?.IsMainActionSubmissionInFlight == true;
            influenceActionPresenter.ObservedStateRevision = () => VisibleSaleState()?.StateRevision ?? ReadUiStateWithTrace()?.EffectRuntime?.StateRevision ?? 0;
            turnActionPresenter.MoveInteraction.ObservedStateRevision = () => VisibleSaleState()?.StateRevision ?? ReadUiStateWithTrace()?.EffectRuntime?.StateRevision ?? 0;
            turnActionPresenter.MoveInteraction.GetVisibleState = VisibleSaleState;
            turnActionPresenter.CityStyleInteraction.GetDisplayedPlayerId = () => displayedCityPlayerId;
            turnActionPresenter.CityStyleInteraction.GetPresentationGeneration = () => gameplayInteractionHud.Frame.PresentationGeneration;
            turnActionPresenter.BuildInteraction.GetVisibleState = VisibleSaleState;
            turnActionPresenter.MainActionDraftCleared = () => {
                interactionRouter?.ClearMapConfirmation(); mapInteractionRouter?.ClearConfirmation(false);
                routeResourceSelection?.CancelLocalPreview();
                if (!turnActionPresenter.BuildInteraction.IsActive)
                {
                    buildDialogActive = false; buildDialogStep = BuildDialogStep.None;
                    selectedBuildFacilityId = string.Empty; buildDialogGeneration++;
                    if (facilityBuildPage != null) facilityBuildPage.Hide();
                }
            };
            interactionRouter = new InteractionRouter(SetPrompt);
            RegisterEventCardInteraction();
            characterCardEffectChoiceDialog.BindGameplayText(gameplayInteractionHud.ExpansionUi.ResolveGameplayText,
                gameplayInteractionHud.ExpansionUi.RenderEnterpriseDetails);
            influenceEffectInteraction.GetVisibleState = VisibleSaleState;
            influenceEffectInteraction.BindGameplayText(gameplayInteractionHud.ExpansionUi.ResolveGameplayText,
                gameplayInteractionHud.Frame.SetInteractionDocument);
            routeResourceSelection = new RouteResourceSelectionInteractionUiCoordinator(
                () => ReadUiStateWithTrace(), VisibleSaleState, () => localPlayerId, GetVisibleInteractionRequests,
                new YC.Domain.Travel.RouteSelectionSessionAuthority(mapQuery),
                new YC.Domain.Travel.RouteResourceSelectionService(mapQuery),
                eventChoiceDialog, workflowView, SubmitPendingEffectCommand, RefreshRouteSelectionDraft);
            routeResourceSelection.CanSubmit = () => CanSubmitGameplayCommand && string.IsNullOrEmpty(pendingEffectCommandId);
            routeResourceSelection.IsNetworkSession = () => commandSubmission?.UsesDeterministicSimulation == true;
            enterpriseInteraction.IsNetworkSession = () => commandSubmission?.UsesDeterministicSimulation == true;
            characterAbilityInteraction.CanSubmit = () => CanSubmitGameplayCommand && string.IsNullOrEmpty(pendingEffectCommandId);
            enterpriseInteraction.CanSubmit = () => CanSubmitGameplayCommand && string.IsNullOrEmpty(pendingEffectCommandId);
            routeResourceSelection.RequiresExplicitRefresh = () => commandSubmission?.RequiresExplicitRefreshForCurrentResult == true;
            characterAbilityInteraction.RequiresExplicitRefresh = () => commandSubmission?.RequiresExplicitRefreshForCurrentResult == true;
            enterpriseInteraction.RequiresExplicitRefresh = () => commandSubmission?.RequiresExplicitRefreshForCurrentResult == true;
            interactionRequestRouter.Register(routeResourceSelection);
            interactionRouter.Register(routeResourceSelection);
            explorationEventPresenter.BeginRouteSelection = () => routeResourceSelection.BeginLocalExploration();
            resourceCollectionPresenter.HasRouteSelectionRequest = () => routeResourceSelection.IsActive;
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
            var state = ReadUiStateWithTrace();
            var pending = state == null ? null : state.PendingCharacterEffect;
            return pending != null &&
                   pending.IsValid() &&
                   pending.PlayerId == localPlayerId;
        }

        private void RefreshAllFromState()
        {
            using var measurement = UiRegressionMetrics.Measure("hud.full_refresh");
            if (uiShuttingDown) return;
            using var latency = InteractionLatencyTrace.Begin("ui.full");
            using var fullTiming = new UiExecutionTimingScope(session?.ExecutionTimings, "ui.full");
            using var uiSample = GameplayUiRefreshMarker.Auto();
            using var visibleRead = BeginVisibleRead();
            using var characterRead = characterCardPresenter?.BeginPresentationRead();
            using (InteractionLatencyTrace.Begin("ui.city_views")) RefreshCityViewsFromState();
            using (InteractionLatencyTrace.Begin("ui.resources")) RefreshResourceDisplay();
            using (InteractionLatencyTrace.Begin("ui.influence")) RefreshInfluenceDisplay();
            using (InteractionLatencyTrace.Begin("ui.score_track")) mapView?.RefreshScoreTrackDisplay(ReadUiStateWithTrace());
            RefreshActionPanel();
            using (InteractionLatencyTrace.Begin("ui.final_score")) RefreshFinalScoreFromState();
            using (InteractionLatencyTrace.Begin("ui.information")) gameplayInteractionHud.GetComponent<GameplayInformationPages>()?.Refresh();
        }

        private void SynchronizeInteractionFromState()
        {
            using var measurement = UiRegressionMetrics.Measure("hud.synchronize");
            if (uiShuttingDown) return;
            using var latency = InteractionLatencyTrace.Begin("ui.sync");
            using var syncTiming = new UiExecutionTimingScope(session?.ExecutionTimings, "ui.sync");
            using var synchronizeSample = GameplaySynchronizeMarker.Auto();
            using var visibleRead = BeginVisibleRead();
            InvalidateVisibleRead();
            gameplayInteractionHud.ExpansionUi.RebindSessionPresentation(VisibleSaleState());
            bool permissionOnly;
            using (InteractionLatencyTrace.Begin("ui.permission_path")) permissionOnly = RefreshSimulationPermissionOnly();
            if (permissionOnly) { InteractionLatencyTrace.Point("ui.permission_only_return"); return; }
            turnActionPresenter.SynchronizeFromState();
            // 只在联机同一观察者的普通发表刷新中保草稿；切人清理仍走原分支。
            var preserveRequestDraft = !viewerChanged && commandSubmission?.UsesDeterministicSimulation == true;
            var preserveMove = !viewerChanged && turnActionPresenter.MoveInteraction.CanResumePreparedDraft;
            var preserveBuild = !viewerChanged && turnActionPresenter.BuildInteraction.CanResumePreparedDraft;
            if (viewerChanged)
            {
                viewerChanged = false;
                ClearEventCardInteraction();
                characterHandPanel?.CloseCharacterCardViewer();
                characterHandPanel?.CloseDiscardPreview();
                characterUsePage?.Hide();
                showCharacterUseOptions = false;
                gameplayInteractionHud?.ExpansionUi?.Clear();
            }
            if (!preserveMove) interactionRouter?.ClearMapConfirmation();
            mapInteractionRouter?.ClearConfirmation(false);
            var preserveCollection = flowCoordinator.IsActive(resourceCollectionPresenter) && resourceCollectionPresenter.CanResumeDraft;
            var preserveDeploy = flowCoordinator.IsActive(influenceActionPresenter) && influenceActionPresenter.CanResumeDeployDraft;
            var preserveDispatch = flowCoordinator.IsActive(influenceActionPresenter) && influenceActionPresenter.CanResumeDispatchDraft;
            if (!preserveCollection && !preserveDeploy && !preserveDispatch && !preserveMove && !preserveBuild) flowCoordinator.ResetToChooseAction();
            if (!preserveCollection && !preserveDeploy && !preserveDispatch) ClearPendingDispatch();
            routeResourceSelection?.RefreshLocalPreview();
            // 同一城市样式支付请求由路由器按 ID/版本刷新，保留收起状态与尚未支付的选择。
            if (preserveCollection) eventChoiceDialog.Hide();
            else if (specialActionInteraction == null || !specialActionInteraction.HasAuthoritativeRequest)
                HideEventCardOptions(preserveRequestDraft);
            RefreshAllFromState();
            if (preserveCollection) resourceCollectionPresenter.RefreshFromState();
            if (preserveDeploy) influenceActionPresenter.RefreshDeployDraft();
            if (preserveDispatch) influenceActionPresenter.RestorePresentation();
            if (preserveMove) turnActionPresenter.RestoreMovePresentation();
            RefreshPendingChoiceOrHighlights();
            SynchronizeCharacterSettlementPresentation();
            TryBeginAutomaticSecondEffect();
            ObserveSharedCityStyleMirrorState();
            PresentLatestResourceSaleReceipt();
            RefreshStoppedCharacterMode();
        }

        private void SynchronizeCharacterSettlementPresentation()
        {
            if (!characterSettlementInProgress || session == null || ReadUiStateWithTrace() == null)
            {
                return;
            }

            var player = ReadUiStateWithTrace().FindPlayer(localPlayerId);
            if (player == null || ReadUiStateWithTrace().PendingCharacterEffect != null ||
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
            if (session == null || ReadUiStateWithTrace() == null || ReadUiStateWithTrace().Logs == null || ReadUiStateWithTrace().Logs.Count == 0)
            {
                return;
            }

            string settlementMessage = null;
            for (var i = 0; i < ReadUiStateWithTrace().Logs.Count; i++)
            {
                var entry = ReadUiStateWithTrace().Logs[i];
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
            using var visibleRead = BeginVisibleRead();
            if (ReadUiStateWithTrace() == null) return;
            if (SynchronizeEventCardInteraction())
            {
                RefreshCurrentRequestEndButton();
                RefreshRouteSelectionConfirmation();
                return;
            }

            if (ReadUiStateWithTrace().Phase != GamePhase.ResourceCollection)
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

            if (interactionPresentation.PromptDocument != null)
            {
                if (characterMapInteraction?.OwnsPromptDocument(interactionPresentation.PromptDocument) == true)
                    SetPrompt(interactionPresentation.PromptDocument, characterMapInteraction.CapturePendingPromptCurrentness());
                else gameplayInteractionHud.Frame.SetInteractionDocument(influenceEffectInteraction.PresentationRequest,
                    interactionPresentation.PromptDocument);            }
            else if (!string.IsNullOrEmpty(interactionPresentation.PromptText))
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
                ReadUiStateWithTrace(),
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
            actionPanel.BeginInputRead = () => BeginVisibleRead();
            gameplayInteractionHud.DialogRegistry.BeginInputRead = () => BeginVisibleRead();
            actionPanel.CanBeginAction = () => CanBeginGameplayAction;
            actionPanel.ResolveActionText = gameplayInteractionHud.ExpansionUi.ResolveGameplayText;
            actionPanel.RenderActionText = gameplayInteractionHud.Frame.RenderGameplayText;
            actionPanel.DeclaredActionRequested = option => SubmitPendingEffectCommand(YC.Application.Interactions.AbilityActionCommand.Create(localPlayerId, option));
            turnActionPresenter.CityStyleInteraction.GetVisibleState = () => VisibleSaleState();
            actionPanel.CanSubmitCharacter = () => CanSubmitGameplayCommand && string.IsNullOrEmpty(characterUseCommandId) &&
                turnActionPresenter?.IsMainActionSubmissionInFlight != true &&
                string.IsNullOrEmpty(pendingEffectCommandId);
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
            gameplayInteractionHud.MainModules.ConfigureCityInspection(InspectCity);
            RefreshActionPanel();
            return true;
        }

        private void RefreshActionPanel()
        {
            using var measurement = UiRegressionMetrics.Measure("hud.action_panel");
            if (uiShuttingDown) return;
            using var latency = InteractionLatencyTrace.Begin("ui.action_panel");
            using var refreshSample = ActionPanelRefreshMarker.Auto();
            using var visibleRead = BeginVisibleRead();
            if (specialActionUsePage != null && specialActionUsePage.IsShowing && specialActionPageIsCurrent != null && !specialActionPageIsCurrent())
                specialActionUsePage.Hide();
            if (actionPanel == null || !actionPanel.IsReady || turnActionPresenter == null)
            {
                return;
            }

            var characterView = characterCardPresenter == null
                ? null
                : characterCardPresenter.BuildProjectedView(ReadUiStateWithTrace(), localPlayerId, VisibleSaleState());
            if (characterUsePage != null && characterUsePage.IsShowing &&
                characterUsePageKey != CharacterUseKey(characterView))
            {
                characterUsePage.Hide();
                characterUsePageKey = string.Empty;
            }
            ActionPanelViewModel actionViewModel;
            using (ActionPanelViewModelMarker.Auto())
            {
                actionViewModel = turnActionPresenter.BuildActionPanelViewModel();
            }
            lastActionPanelPermissionModel = actionViewModel;
            actionPanel.Render(actionViewModel);
            actionPanel.RenderCharacterQuickAction(characterView);
            var visibleState = VisibleSaleState();
            gameplayInteractionHud.Frame.Refresh(ReadUiStateWithTrace(), actionViewModel,
                turnActionPresenter.ActionPanelPresenter.GetUnavailableEndActionPrompt(),
                actionViewModel.AllCollectionLocationsSelected
                    ? gameplayInteractionHud.ActionPanelView.AllCollectionLocationsSelectedText : string.Empty,
                visibleState);
            gameplayInteractionHud.MainModules.Render(ReadUiStateWithTrace(), visibleState, localPlayerId,
                gameplayInteractionHud.DialogRegistry.CardVisualCatalog);
            RefreshViewedCityPanel(characterView);
            gameplayInteractionHud.ExpansionUi.Refresh();
            ApplySubmissionPermission(actionViewModel);
            TraceVisibleBinding(visibleState);
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
                RefreshRouteSelectionConfirmation();
                return;
            }

            if (characterSettlementInProgress &&
                characterView != null &&
                characterView.IsSecondEffectExecution)
            {
                ShowCharacterUsePage(characterView);
            }
            var state = ReadUiStateWithTrace();
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
        private void BeginMoveAction() { if (CanBeginGameplayAction) turnActionPresenter.BeginMoveAction(); }

        private bool CanBeginGameplayAction => CanSubmitGameplayCommand &&
            string.IsNullOrEmpty(characterUseCommandId) && string.IsNullOrEmpty(pendingEffectCommandId) &&
            turnActionPresenter?.IsMainActionSubmissionInFlight != true && !HasCommittedSettlement();

        private bool HasCommittedSettlement()
        {
            var state = ReadUiStateWithTrace();
            var visible = VisibleSaleState();
            return TurnActionPanelPresenter.HasCommittedSettlementContext(state, visible, localPlayerId);
        }
        private void RefreshCurrentRequestEndButton()
        {
            if (uiShuttingDown) return;
            var enabled = CanEndCurrentAction();
            gameplayInteractionHud.Frame.EndActionButton.interactable = enabled;
            gameplayInteractionHud.ActionPanelView.EndRoundButton.interactable = enabled;
            gameplayInteractionHud.Frame.EndActionButton.GetComponent<UiMainButtonState>()?.SetAvailable(enabled);
            gameplayInteractionHud.ActionPanelView.EndRoundButton.GetComponent<UiMainButtonState>()?.SetAvailable(enabled);
        }

        private void RefreshRouteSelectionConfirmation()
        {
            if (routeResourceSelection?.IsActive != true) return;
            routeCanStopPendingCharacterMode = CanStopCurrentEffect || CanUseSecondModeStop;
            routeStopSnapshotIsCurrent = CreateVisibleSnapshotGuard(VisibleSaleState());
            RefreshRouteSelectionDraft();
        }

        private void RefreshRouteSelectionDraft()
        {
            if (routeResourceSelection?.IsActive != true) return;
            if (!string.IsNullOrEmpty(VisibleSaleState()?.ActiveSettlementWindowId))
            { RefreshCurrentRequestEndButton(); return; }
            var enabled = string.IsNullOrEmpty(pendingEffectCommandId) &&
                (routeCanStopPendingCharacterMode && routeStopSnapshotIsCurrent?.Invoke() == true ||
                routeResourceSelection.CanConfirm);
            gameplayInteractionHud.Frame.EndActionButton.interactable = enabled;
            gameplayInteractionHud.ActionPanelView.EndRoundButton.interactable = enabled;
            gameplayInteractionHud.Frame.EndActionButton.GetComponent<UiMainButtonState>()?.SetAvailable(enabled);
            gameplayInteractionHud.ActionPanelView.EndRoundButton.GetComponent<UiMainButtonState>()?.SetAvailable(enabled);
        }

        private void BeginExploreAction() { if (CanBeginGameplayAction) turnActionPresenter.BeginExploreAction(); }

        private void BeginDeployAction() { if (CanBeginGameplayAction) turnActionPresenter.BeginDeployAction(); }

        private void BeginDispatchAction() { if (CanBeginGameplayAction) turnActionPresenter.BeginDispatchAction(); }
        private void SubmitCoverCharacterCard(string cardId)
        {
            var current = characterCardPresenter.BuildProjectedView(ReadUiStateWithTrace(), localPlayerId, VisibleSaleState());
            var request = current.CurrentCoverSelection;
            var command = current.UsesEntityLifecycle && request != null
                ? YC.Application.Interactions.EffectInteractionCommands.Answer(request, localPlayerId, new[] { cardId })
                : characterCardPresenter.CreateCoverCommand(localPlayerId, cardId);
            if (current.UsesEntityLifecycle && (request == null || !coverIntent.Begin(ReadUiStateWithTrace().GameId, request, command, ShouldControlCurrentPlayerLocally())))
            { coverSubmissionInFlight = false; return; }
            submittedCoverGameId = ReadUiStateWithTrace().GameId;
            submittedCoverCommandId = command.CommandId;
            SubmitCharacterCardCommand(command, "角色牌已盖放。", "盖放角色牌命令已发送给主机，等待确认。");
        }

        private void SubmitUseCharacterCard(
            string effectMode,
            string effectOrder,
            IReadOnlyDictionary<string, string> effectParameters)
        {
            if (!CanSubmitGameplayCommand || ReadUiStateWithTrace() == null) return;
            var player = ReadUiStateWithTrace().FindPlayer(localPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CoveredCharacterCardId))
            {
                SetPrompt("当前没有可使用的盖放角色牌。");
                return;
            }

            characterSettlementInProgress = true;
            var visible = VisibleSaleState();
            if (visible?.HasAbilityActionCatalog == true)
            {
                var candidate = visible.AbilityActions.FirstOrDefault(a => a.EntryKind == "quick" && a.SourceRef.ItemId == player.CoveredCharacterCardId);
                if (candidate == null || !candidate.Enabled) { SetPrompt(candidate == null ? "当前角色没有可用行动入口。" : YC.Domain.Effects.AbilityActionReasons.Display(candidate.DisabledReason)); return; }
                var declared = YC.Application.Interactions.AbilityActionCommand.Create(localPlayerId, candidate);
                declared.Parameters["mode"] = effectMode ?? ""; declared.Parameters["order"] = effectOrder ?? "";
                SubmitCharacterCardCommand(declared, "角色行动已受理。", "角色行动已发送，等待主机确认。"); return;
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
            using var visibleRead = BeginVisibleRead();
            if (command == null) return;
            var visible = VisibleSaleState();
            var declaredOption = command.Kind == GameCommandKind.InvokeSettlementOption && command.Parameters.TryGetValue("intent", out var declaredIntent) && declaredIntent == YC.Application.Interactions.AbilityActionCommand.Intent && command.Parameters.TryGetValue("optionId", out var declaredId)
                ? visible?.AbilityActions.Find(option => option.OptionId == declaredId) : null;
            var standaloneOption = command.Kind == GameCommandKind.InvokeSettlementOption &&
                command.Parameters.TryGetValue("optionId", out var optionId)
                ? visible?.CharacterSettlementOptions.Find(option => option.OptionId == optionId && string.IsNullOrEmpty(option.EntryKind)) : null;
            if (command.Kind == GameCommandKind.UseCharacterCard || standaloneOption != null || declaredOption != null)
            {
                if (!CanBeginGameplayAction) { gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(command.CommandId); return; }
                if (turnActionPresenter?.RunAfterClearingMainActionDraft(() =>
                {
                    if (declaredOption != null)
                    {
                        var fresh = VisibleSaleState()?.AbilityActions.Find(option => option.OptionId == declaredOption.OptionId && option.SourceRef.ItemId == declaredOption.SourceRef.ItemId && option.Enabled);
                        if (fresh == null) { gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(command.CommandId); return; }
                        command.Parameters["requestId"] = fresh.RequestRef.InteractionId; command.Parameters["factsDigest"] = fresh.RequestRef.FactsDigest; command.Parameters["contentHash"] = fresh.VersionRef.ContentPackHash;
                    }
                    if (standaloneOption != null)
                    {
                        var fresh = VisibleSaleState()?.CharacterSettlementOptions.Find(option =>
                            option.OptionId == standaloneOption.OptionId && option.SourceItemId == standaloneOption.SourceItemId &&
                            string.IsNullOrEmpty(option.EntryKind) && option.IsEnabled && option.AnsweringPlayerId == localPlayerId);
                        if (fresh == null) { gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(command.CommandId); return; }
                        command.Parameters["expectedRevision"] = fresh.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    SubmitPendingEffectCommandCore(command);
                }) != true) gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(command.CommandId);
                return;
            }
            SubmitPendingEffectCommandCore(command);
        }
        private void SubmitPendingEffectCommandCore(GameCommand command)
        {
            using var measurement = UiRegressionMetrics.Measure("command.dispatch");
            using var visibleRead = BeginVisibleRead();
            if (!CanSubmitGameplayCommand || !string.IsNullOrEmpty(pendingEffectCommandId))
            {
                // 交叉点击底栏与页面时，解除未送出的本地锁；不产生第二条命令。
                routeResourceSelection?.NotifyMainActionCommandNotDispatched(command);
                interactionRouter?.NotifyCommandSettled(command.CommandId);
                specialActionInteraction?.NotifyPaymentDraftSettled(command.CommandId, false);
                gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(command.CommandId);
                return;
            }
            var commandId = command == null ? string.Empty : command.CommandId;
            var commandGameId = ReadUiStateWithTrace()?.GameId;
            pendingEffectCommandId = commandId;
            InteractionLatencyTrace.Point("gate.effect_wait_set", commandId);
            RefreshCurrentRequestEndButton();
            try
            {
            var outcome = commandGateway.Submit(
                command,
                new SubmitCallbacks(
                    SetPrompt,
                    "你的选择已提交，正在同步结算结果，无需主机代选。")
                {
                    BeforeRejectedPrompt = result =>
                    { routeResourceSelection?.NotifyInteractionCommandResolved(commandGameId, commandId, false, result.Validation.ErrorCode); NotifyPendingEffectSettled(commandId); specialActionInteraction?.NotifyPaymentDraftSettled(commandId, false); gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(commandId); interactionRouter?.NotifyCommandSettled(commandId); },
                    AfterRejectedPrompt = _ =>
                    {
                        specialActionInteraction?.Synchronize();
                        facilityInteraction?.Synchronize();
                        SynchronizeEventCardInteraction();
                        RefreshCurrentRequestEndButton();
                    },
                    OnAppliedLocally = _ =>
                    {
                        routeResourceSelection?.NotifyInteractionCommandResolved(commandGameId, commandId, true, default);
                        NotifyPendingEffectSettled(commandId);
                        specialActionInteraction?.NotifyPaymentDraftSettled(commandId, true);
                        gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(commandId);
                        interactionRouter?.NotifyCommandSettled(commandId);
                        SynchronizeInteractionFromState();
                    }
                });
            if (outcome.Kind == SubmitOutcomeKind.NoResult)
            {
                NotifyPendingEffectSettled(commandId);
                specialActionInteraction?.NotifyPaymentDraftSettled(commandId, false);
                gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(commandId);
                interactionRouter?.NotifyCommandSettled(commandId);
                SynchronizeEventCardInteraction();
                RefreshCurrentRequestEndButton();
            }
            }
            catch { NotifyPendingEffectSettled(commandId); specialActionInteraction?.NotifyPaymentDraftSettled(commandId, false); gameplayInteractionHud.ExpansionUi.NotifyCommandSettled(commandId); RefreshCurrentRequestEndButton(); throw; }
        }

        private void NotifyPendingEffectSettled(string commandId)
        {
            if (!string.IsNullOrEmpty(commandId) && pendingEffectCommandId == commandId)
            {
                pendingEffectCommandId = string.Empty;
                InteractionLatencyTrace.Point("gate.effect_wait_cleared", commandId);
            }
        }

        private void SubmitCharacterCardCommand(GameCommand command, string localSuccessPrompt, string remotePrompt)
        {
            if (!CanSubmitGameplayCommand) return;
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

            var pending = ReadUiStateWithTrace().PendingCharacterEffect;
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

        private void OnDeclareCityStyleClicked()
        {
            using var measurement = UiRegressionMetrics.Measure("declare.button");
            var panel = turnActionPresenter.BuildActionPanelViewModel();
            if (!panel.CanInspectCityStyles) return;
            if (CanBeginGameplayAction && panel.CanDeclareCityStyle) turnActionPresenter.BeginDeclareCityStyle();
            else turnActionPresenter.CityStyleInteraction.OpenPreview(string.Empty);
        }

        private CharacterCardEffectChoiceDialog specialActionUsePage;
        private Func<bool> specialActionPageIsCurrent;

        private void OnSpecialActionClicked()
        {
            if (!CanBeginGameplayAction) return;
            if (!CanSubmitGameplayCommand || turnActionPresenter.IsMainActionSubmissionInFlight) return;
            var interaction = turnActionPresenter.CityStyleInteraction;
            if (!interaction.CanUseSpecialAction) return;
            turnActionPresenter.RunAfterClearingMainActionDraft(OpenSpecialActionPage);
        }
        private void OpenSpecialActionPage()
        {
            var interaction = turnActionPresenter.CityStyleInteraction;
            if (!CanBeginGameplayAction || !interaction.CanUseSpecialAction) return;
            var owner = session;
            var player = localPlayerId;
            var revision = ReadUiStateWithTrace().EffectRuntime?.StateRevision ?? 0;
            bool Current() => CanSubmitGameplayCommand && session == owner && localPlayerId == player &&
                ReadUiStateWithTrace() != null && (ReadUiStateWithTrace().EffectRuntime?.StateRevision ?? 0) == revision &&
                interaction.CanUseSpecialAction;
            var options = new System.Collections.Generic.List<EffectDialogOption>();
            foreach (var option in interaction.QuerySpecialActions().Options)
            {
                var captured = option;
                string details = option.DeclaredAction == null ? option.Description : YC.Application.Presentation.AbilityActionTextDocuments.PlainDetails(option.DeclaredAction);
                string suffix = option.CanUse ? option.Warning : "\n" + option.DisabledReason;
                options.Add(new EffectDialogOption(option.Name + "\n" + details +
                    (option.CanUse ? option.Warning : "\n" + option.DisabledReason),
                    () => interaction.SubmitSpecialAction(captured.SpecialActionId, captured.DeclarationMarkerId),
                    option.CanUse) { StableId = option.DeclarationMarkerId + ":" + option.SpecialActionId,
                        SourceLabel = option.Name,
                        DescriptionLabel = details + suffix,
                        LabelDocument = option.DeclaredAction == null ? null : YC.Application.Presentation.AbilityActionTextDocuments.Name(option.DeclaredAction, gameplayInteractionHud.ExpansionUi.ResolveGameplayText),
                        DescriptionDocument = option.DeclaredAction == null ? null : YC.Application.Presentation.AbilityActionTextDocuments.Details(option.DeclaredAction, gameplayInteractionHud.ExpansionUi.ResolveGameplayText, suffix) });
            }
            if (specialActionUsePage == null) specialActionUsePage = new CharacterCardEffectChoiceDialog(
                gameplayInteractionHud.DialogRegistry, uiCanvas.transform as RectTransform);
            specialActionUsePage.BindGameplayText(gameplayInteractionHud.ExpansionUi.ResolveGameplayText, gameplayInteractionHud.Frame.RenderGameplayText);
            specialActionPageIsCurrent = Current;
            var copy = specialActionUsePage.Copy;
            specialActionUsePage.ShowExecutionChoices(copy.SpecialActionTitle, copy.SpecialActionDescription,
                options, Current);
        }

        private void OnBuildActionClicked()
        {
            using var latency = InteractionLatencyTrace.Begin("local.open_build");
            var canBegin = CanBeginGameplayAction;
            var panel = turnActionPresenter.BuildActionPanelViewModel();
            if (panel.CanViewFacilitySupply && (!canBegin || !panel.CanBuild))
            {
                supplyInspectionActive = true;
                if (canBegin) selectedBuildFacilityId = string.Empty;
                PresentFacilitySupplyInspection();
                return;
            }
            if (!canBegin) return;
            if (buildDialogActive && turnActionPresenter?.BuildBuildFacilityDraftViewModel() != null)
                return;
            turnActionPresenter.RunAfterClearingMainActionDraft(OpenBuildActionPage);
        }
        private void OpenBuildActionPage()
        {
            buildDialogActive = true;
            buildDialogGeneration++;
            buildDialogStep = BuildDialogStep.Facilities;
            selectedBuildFacilityId = string.Empty;
            turnActionPresenter.BuildInteraction.Begin();
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
            var state = ReadUiStateWithTrace();
            var leaving = GameLaunchContext.Instance?.IsLeavingGameplay == true;
            // 联网核验期间的临时禁交不代表建设草稿失效，不能借刷新取消正式付款准备。
            if (state == null || leaving || state.CurrentPlayerId != localPlayerId ||
                (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2))
            {
                // 先退役页面入口；取消的同步拒绝回执不能再次进入同一自动取消分支。
                buildDialogActive = false;
                buildDialogStep = BuildDialogStep.None;
                selectedBuildFacilityId = string.Empty;
                if (facilityBuildPage != null) facilityBuildPage.Hide();
                if (!leaving) turnActionPresenter.CancelBuildFacility();
                buildFacilityInteraction?.Synchronize();
                buildInfoPanel?.SetExternalFacilitySupplyVisible(
                    buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive);
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
        private int buildDialogGeneration;
        private bool supplyInspectionActive;

        private void ShowBuildDraftInOriginalPanel(BuildFacilityDraftViewModel model)
        {
            if (uiShuttingDown || model == null || turnActionPresenter == null ||
                !turnActionPresenter.BuildInteraction.IsActive || turnActionPresenter.BuildInteraction.DraftPlayerId != localPlayerId) return;
            var state = ReadUiStateWithTrace();
            if (state == null || GameLaunchContext.Instance?.IsLeavingGameplay == true || state.CurrentPlayerId != localPlayerId ||
                (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)) return;
            if (!buildDialogActive) { buildDialogActive = true; buildDialogGeneration++; }
            selectedBuildFacilityId = model.SelectedOption?.FacilityId ?? turnActionPresenter.BuildInteraction.FacilityId;
            PresentBuildFacilityDialog();
        }

        private void HideBuildDraftInOriginalPanel()
        {
            if (!buildDialogActive) return;
            buildDialogActive = false; buildDialogStep = BuildDialogStep.None;
            selectedBuildFacilityId = string.Empty; buildDialogGeneration++;
            if (facilityBuildPage != null) facilityBuildPage.Hide();
        }

        private void PresentFacilitySupplyInspection()
        {
            if (!supplyInspectionActive || ReadUiStateWithTrace() == null) return;
            var owner = session;
            var player = localPlayerId;
            bool Current() => supplyInspectionActive && session == owner && localPlayerId == player &&
                turnActionPresenter.BuildActionPanelViewModel().CanViewFacilitySupply;
            var model = new BuildFacilityDraftViewModel(BuildFacilityDraftPhase.Selecting,
                supplyInspectionCandidates.Query(ReadUiStateWithTrace(), player), null, null, -1,
                string.Empty, string.Empty, null, _ => { }, supplyInspectionCandidates.IsDefaultSupplySet);
            if (facilityBuildPage == null)
                facilityBuildPage = gameplayInteractionHud.DialogRegistry.InstantiateFacilityBuild(uiCanvas.transform as RectTransform);
            facilityBuildPage.RefreshSupplyStatus(ReadUiStateWithTrace(), localPlayerId);
            facilityBuildPage.Show(model, ReadUiStateWithTrace().Map.Facilities.ToArray(), player,
                gameplayInteractionHud.DialogRegistry.CardVisualCatalog,
                gameplayInteractionHud.DialogRegistry.EffectDialogLayoutProfile,
                selectedBuildFacilityId, string.Empty, Current,
                id => { if (Current()) { selectedBuildFacilityId = id; PresentFacilitySupplyInspection(); } },
                _ => { }, _ => { }, () => { }, CloseFacilitySupplyInspection, true);
            SetPrompt(string.Empty);
        }

        private void CloseFacilitySupplyInspection()
        {
            supplyInspectionCandidates?.Invalidate();
            supplyInspectionActive = false;
            selectedBuildFacilityId = string.Empty;
            if (facilityBuildPage != null) facilityBuildPage.Hide();
        }

        private void PresentBuildFacilityDialog()
        {
            if (!buildDialogActive || turnActionPresenter == null || turnActionPresenter.BuildInteraction.DraftPlayerId != localPlayerId) return;
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model == null) return;
            // 必要响应/异客分配暂用正式效果页；建设草稿留在原宿主，返回才刷新费用和按钮。
            if (ActionPaymentPreparation.HasBlockingChoice(ReadUiStateWithTrace(), localPlayerId)) return;
            if (facilityBuildPage == null)
                facilityBuildPage = gameplayInteractionHud.DialogRegistry.InstantiateFacilityBuild(uiCanvas.transform as RectTransform);
            var owner = session;
            var player = localPlayerId;
            var generation = buildDialogGeneration;
            gameplayInteractionHud.Frame.RegisterActionPage(facilityBuildPage.gameObject, generation.ToString(),
                () => session == owner && localPlayerId == player && generation == buildDialogGeneration && IsCurrentBuildDialog(),
                PresentBuildFacilityDialog);
            facilityBuildPage.RefreshSupplyStatus(ReadUiStateWithTrace(), localPlayerId);
            facilityBuildPage.Show(model, ReadUiStateWithTrace().Map.Facilities.ToArray(), localPlayerId,
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
            var state = ReadUiStateWithTrace();
            return buildDialogActive && state != null &&
                   HasLocalOperationPermission(state) &&
                   (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2) &&
                   turnActionPresenter != null &&
                   !turnActionPresenter.BuildInteraction.IsSubmissionInFlight &&
                   turnActionPresenter.BuildInteraction.IsActive && turnActionPresenter.BuildInteraction.DraftPlayerId == localPlayerId;
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
                model.SelectedOption == null ||
                (model.PaymentPreparation != null ? !model.PaymentPreparation.CanConfirm :
                !model.SelectedOption.CanBuild || (model.PaymentMode == BuildFacilityService.PaymentModeResources &&
                 !model.SelectedOption.ResourcesPayment.IsAvailable) ||
                (model.PaymentMode == BuildFacilityService.PaymentModeGold &&
                 !model.SelectedOption.GoldPayment.IsAvailable)))
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
            if (!buildDialogActive || turnActionPresenter == null || turnActionPresenter.BuildInteraction.IsSubmissionInFlight) return;
            buildDialogActive = false;
            buildDialogStep = BuildDialogStep.None;
            selectedBuildFacilityId = string.Empty;
            if (facilityBuildPage != null) facilityBuildPage.Hide();
            turnActionPresenter.CancelBuildFacility();
            buildFacilityInteraction?.Synchronize();
            buildInfoPanel?.SetExternalFacilitySupplyVisible(
                buildInfoPanel != null && buildInfoPanel.IsFacilityEffectSelectionActive);
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
            var player = ReadUiStateWithTrace()?.FindPlayer(playerId);
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
            mapView?.RefreshRoadDisplay(VisibleSaleState()?.ItemZones);
        }

        private void RefreshResourceTokenDisplay() =>
            mapView?.RefreshResourceTokenDisplay(ReadUiStateWithTrace());

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

        private bool viewerChanged;
        private void SetLocalViewer(int playerId)
        {
            if (playerId <= 0 || playerId == localPlayerId || !ShouldControlCurrentPlayerLocally()) return;
            localPlayerId = playerId;
            viewerChanged = true;
            InvalidateVisibleRead();
        }

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
            mapView.RefreshCityViewsFromState(ReadUiStateWithTrace(), localPlayerId);
        }

        private void RefreshFinalScoreFromState()
        {
            var finalScore = FindObjectOfType<FinalScoreController>();
            if (finalScore != null)
            {
                finalScore.RefreshFromState(ReadUiStateWithTrace());
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

        private void SetPrompt(GameplayTextRef reference) => SetPrompt(reference, null);

        private void SetPrompt(GameplayTextRef reference, Func<bool> current)
        {
            if (uiShuttingDown) return;
            var document = gameplayInteractionHud?.ExpansionUi?.ResolveGameplayText(reference);
            if (document != null) SetPrompt(document, current);
        }

        private void SetPrompt(GameplayTextDocument document, Func<bool> current)
        {
            if (uiShuttingDown) return;
            var state = ReadUiStateWithTrace(); var frame = gameplayInteractionHud?.Frame;
            if (state == null || frame == null || document == null) return;
            frame.BindGameplaySnapshot(VisibleSaleState());
            var game = state.GameId; var viewer = localPlayerId; var revision = state.EffectRuntime?.StateRevision ?? 0;
            var ownerSession = session; var content = state.ContentPackHash;
            frame.SetScopedInteractionMessageDocument(game, viewer, revision, document, () =>
                session == ownerSession && ReadUiStateWithTrace()?.GameId == game && localPlayerId == viewer &&
                ReadUiStateWithTrace().ContentPackHash == content && (ReadUiStateWithTrace().EffectRuntime?.StateRevision ?? 0) == revision && current?.Invoke() != false);
        }

        private bool uiShuttingDown;
        private void SetPrompt(string message)
        {
            if (!uiShuttingDown) gameplayInteractionHud?.Frame?.SetInteractionMessage(message, interactionRouter?.GetPendingPrompt());
        }

    }

}
