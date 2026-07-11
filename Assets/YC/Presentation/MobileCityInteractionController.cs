using System;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.CardFlows;
using YC.Domain.CityStyles;
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
    public sealed class MobileCityInteractionController : MonoBehaviour
    {
        private enum RightCardSmokeDialog
        {
            None,
            Build,
            Declare
        }

        private const string RightCardSmokeArg = "--yc-dev-right-card-smoke";
        private const string RightCardSmokeBuildArg = "--yc-dev-right-card-smoke-build";
        private const string RightCardSmokeDeclareArg = "--yc-dev-right-card-smoke-declare";
        private const string RightCardSmokeNoDialogArg = "--yc-dev-right-card-smoke-no-dialog";

        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool debugClicks;

        private GameSession session;
        private MapQueryService mapQuery;
        private InfluenceService influenceService;
        private ExplorationService explorationService;
        private ResourceCollectionService resourceCollectionService;
        private ResourceCollectionPresenter resourceCollectionPresenter;
        private InfluenceActionPresenter influenceActionPresenter;
        private ExplorationEventPresenter explorationEventPresenter;
        private TurnActionPresenter turnActionPresenter;
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
        private PromptPresenter promptPresenter;
        private int localPlayerId = 1;
        private int lastDebugCoordinateLogFrame = -1;
        private bool useRightCardSmokeState;
        private RightCardSmokeDialog rightCardSmokeDialog = RightCardSmokeDialog.None;

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
            PrepareRightCardSmokePresentation();
        }

        private void ReadRightCardSmokeCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            useRightCardSmokeState = HasCommandLineArg(args, RightCardSmokeArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildArg) ||
                                     HasCommandLineArg(args, RightCardSmokeDeclareArg) ||
                                     HasCommandLineArg(args, RightCardSmokeNoDialogArg);
            if (!useRightCardSmokeState)
            {
                return;
            }

            rightCardSmokeDialog = RightCardSmokeDialog.Build;
            if (HasCommandLineArg(args, RightCardSmokeDeclareArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.Declare;
            }
            else if (HasCommandLineArg(args, RightCardSmokeNoDialogArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.None;
            }
        }

        private void PrepareRightCardSmokePresentation()
        {
            if (!useRightCardSmokeState || session == null || session.State == null)
            {
                return;
            }

            if (session.State.FindPlayer(localPlayerId) == null)
            {
                Debug.LogWarning("Right card smoke setup skipped because no local player exists.");
                return;
            }

            flowCoordinator.ResetToChooseAction();
            turnActionPresenter.SynchronizeFromState();
            ClearPendingDispatch();
            workflowView.ClearHighlights();
            eventChoiceDialog.Hide();
            SynchronizeInteractionFromState();

            if (buildInfoPanel != null)
            {
                buildInfoPanel.SetExpanded(true);
            }

            if (rightCardSmokeDialog == RightCardSmokeDialog.Declare)
            {
                OnDeclareCityStyleClicked();
            }
            else if (rightCardSmokeDialog == RightCardSmokeDialog.Build)
            {
                OnBuildActionClicked();
            }

            Debug.Log("Right card smoke setup ready. Dialog=" + rightCardSmokeDialog + ", PlayerId=" + localPlayerId + ".");
        }

        private static bool HasCommandLineArg(string[] args, string expectedValue)
        {
            if (args == null)
            {
                return false;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void Update()
        {
            UpdatePromptAnimation();
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
            mapInteractionRouter.OnLocationClicked(locationId);
        }

        public void OnMobileCityClicked()
        {
            mapInteractionRouter.OnMobileCityClicked();
        }

        public void OnInfluenceSlotClicked(string slotId)
        {
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
            buildInfoPanel.FacilityClicked += OnBuildInfoFacilityClicked;
            buildInfoPanel.CityStyleClicked += OnBuildInfoCityStyleClicked;
            buildInfoPanel.CityBoardSlotClicked += OnBuildInfoCityBoardSlotClicked;
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

            buildInfoPanel.Refresh(session.State, localPlayerId);
        }

        private void OnBuildInfoFacilityClicked(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId))
            {
                SetPrompt("建设面板：已取消设施选择。");
                return;
            }

            var facility = FacilityCardDatabase.Get(facilityId);
            SetPrompt(facility == null
                ? "建设面板：已选择设施。"
                : "建设面板：已选择设施 " + facility.Name + "。");
        }

        private void OnBuildInfoCityStyleClicked(string cityStyleId)
        {
            if (string.IsNullOrEmpty(cityStyleId))
            {
                SetPrompt("建设面板：已取消城市样式选择。");
                return;
            }

            var cityStyle = CityStyleDatabase.Get(cityStyleId);
            SetPrompt(cityStyle == null
                ? "建设面板：已选择城市样式。"
                : "建设面板：已选择城市样式 " + cityStyle.Name + "。");
        }

        private void OnBuildInfoCityBoardSlotClicked(int slotIndex)
        {
            SetPrompt("建设面板：已选择城市面板槽位 " + (slotIndex + 1) + "。");
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
        }

        private void RefreshPendingChoiceOrHighlights()
        {
            if (session.State.Phase != GamePhase.ResourceCollection)
            {
                ClearCollectionSelection();
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
            RefreshActionPanel();
        }

        private void RefreshActionPanel()
        {
            if (actionPanel == null || !actionPanel.IsReady || turnActionPresenter == null)
            {
                return;
            }

            actionPanel.Render(turnActionPresenter.BuildActionPanelViewModel());
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
        private void OnUseCharacterActionClicked()
        {
            SetPrompt("使用角色牌暂未实现。");
        }

        private void OnDeclareCityStyleClicked()
        {
            turnActionPresenter.BeginDeclareCityStyle();
        }

        private void OnBuildActionClicked()
        {
            turnActionPresenter.BeginBuildAction();
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
