using System;
using System.Collections.Generic;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class TurnActionPresenter : IInteractionWorkflow
    {
        private readonly IWritableGameplayContext context;
        private readonly IGameCommandPort commandPort;
        private readonly ITurnActionView view;
        private readonly InteractionFlowCoordinator flowCoordinator;
        private readonly IMapQueryService mapQuery;
        private readonly ResourceCollectionPresenter resourceCollectionPresenter;
        private readonly InfluenceActionPresenter influenceActionPresenter;
        private readonly ExplorationEventPresenter explorationEventPresenter;
        private readonly BuildFacilitySelectionController buildFacilitySelection;
        private readonly CityStyleSelectionController cityStyleSelection;
        private InteractionMode mode = InteractionMode.ChooseAction;
        private string completedMainActionName = string.Empty;

        public TurnActionPresenter(
            IWritableGameplayContext context,
            IGameCommandPort commandPort,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            ResourceCollectionPresenter resourceCollectionPresenter,
            InfluenceActionPresenter influenceActionPresenter,
            ExplorationEventPresenter explorationEventPresenter)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ?? throw new ArgumentNullException(nameof(flowCoordinator));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.resourceCollectionPresenter = resourceCollectionPresenter ?? throw new ArgumentNullException(nameof(resourceCollectionPresenter));
            this.influenceActionPresenter = influenceActionPresenter ?? throw new ArgumentNullException(nameof(influenceActionPresenter));
            this.explorationEventPresenter = explorationEventPresenter ?? throw new ArgumentNullException(nameof(explorationEventPresenter));
            buildFacilitySelection = new BuildFacilitySelectionController();
            cityStyleSelection = new CityStyleSelectionController();
        }

        public InteractionMode Mode
        {
            get { return mode; }
        }

        public string CompletedMainActionName
        {
            get { return completedMainActionName; }
        }

        public bool IsAwaitingInitialPlacement
        {
            get
            {
                SynchronizeLocalPlayerForHotseat();
                var state = context.CurrentState;
                if (state == null || state.Phase != GamePhase.Entrance)
                {
                    return false;
                }

                var player = state.FindPlayer(context.LocalPlayerId);
                return player == null || string.IsNullOrEmpty(player.CityLocationId);
            }
        }

        public void Activate()
        {
            mode = InteractionMode.ResolvingMoveTarget;
            PresentMoveTargets();
        }

        public void Cancel()
        {
            mode = InteractionMode.ChooseAction;
            view.ClearHighlights();
        }

        public void SynchronizeFromState()
        {
            SynchronizeLocalPlayerForHotseat();
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null || !player.ActedMainActionThisTurn)
            {
                completedMainActionName = string.Empty;
            }
        }

        public bool IsLocalPlayersTurn()
        {
            SynchronizeLocalPlayerForHotseat();
            var state = context.CurrentState;
            return state == null || state.CurrentPlayerId == context.LocalPlayerId;
        }

        public bool CanEndCurrentAction()
        {
            SynchronizeLocalPlayerForHotseat();
            var state = context.CurrentState;
            if (state == null || state.HasPendingChoice())
            {
                return false;
            }

            var player = state.FindPlayer(context.LocalPlayerId);
            if (state.Phase == GamePhase.ResourceCollection)
            {
                return player != null && !player.HasCollectedResourcesThisRound;
            }

            if (state.Phase == GamePhase.Cleanup)
            {
                return player != null &&
                       (context.LocalPlayerId == state.StartPlayerId || context.LocalPlayerId == state.CurrentPlayerId);
            }

            return IsLocalPlayersTurn() &&
                   (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2) &&
                   player != null &&
                   player.ActedMainActionThisTurn;
        }

        public void EndCurrentAction()
        {
            if (!CanEndCurrentAction())
            {
                view.ShowPrompt(GetUnavailableEndActionPrompt());
                return;
            }

            var state = context.CurrentState;
            if (state.Phase == GamePhase.ResourceCollection)
            {
                resourceCollectionPresenter.Submit();
                return;
            }

            var submission = commandPort.Submit(new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = context.LocalPlayerId
            });
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u7ed3\u675f\u56de\u5408\u547d\u4ee4\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            completedMainActionName = string.Empty;
            flowCoordinator.ResetToChooseAction();
            view.RefreshFromState();
            view.ShowPrompt(BuildEndActionPrompt(context.CurrentState));
        }

        public void BeginNextRound()
        {
            EndCurrentAction();
        }

        public void PlaceInitialCity(string locationId)
        {
            SynchronizeLocalPlayerForHotseat();
            var submission = commandPort.Submit(new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = context.LocalPlayerId,
                TargetId = locationId ?? string.Empty
            });
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u5165\u573a\u547d\u4ee4\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            view.RefreshFromState();
        }

        public IReadOnlyList<WorkflowHighlight> BuildInitialPlacementHighlights()
        {
            SynchronizeLocalPlayerForHotseat();
            var result = new List<WorkflowHighlight>();
            if (!IsAwaitingInitialPlacement || !IsLocalPlayersTurn())
            {
                return result;
            }

            var locations = mapQuery.Map.Locations;
            for (var i = 0; i < locations.Count; i++)
            {
                var locationId = locations[i].LocationId;
                if (CanUseInitialPlacementLocation(locationId))
                {
                    result.Add(new WorkflowHighlight(
                        WorkflowHighlightTargetKind.Location,
                        locationId,
                        WorkflowHighlightSemantic.InitialPlacement));
                }
            }

            return result;
        }

        public void BeginMoveAction()
        {
            if (!CanStartMainAction())
            {
                return;
            }

            var player = context.CurrentState.FindPlayer(context.LocalPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                view.ShowPrompt("\u73a9\u5bb6\u57ce\u5e02\u4e0d\u5728\u573a\u4e0a\u3002");
                return;
            }

            flowCoordinator.Activate(this);
            view.RefreshActionPanel();
            view.ShowPrompt("\u57ce\u5e02\u79fb\u52a8\uff1a\u9009\u62e9\u4e00\u4e2a\u9ad8\u4eae\u8d44\u6e90\u70b9\u3002");
        }

        public void MoveCity(string locationId)
        {
            var submission = commandPort.Submit(new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = context.LocalPlayerId,
                TargetId = locationId ?? string.Empty
            });
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u79fb\u52a8\u547d\u4ee4\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            if (context.CurrentState.HasPendingChoice())
            {
                flowCoordinator.SetMode(InteractionMode.PendingChoice);
                view.RefreshFromState();
                view.ShowPendingChoice();
                return;
            }

            CompleteAction("\u57ce\u5e02\u79fb\u52a8");
        }

        public void RestoreMovePresentation()
        {
            if (mode == InteractionMode.ResolvingMoveTarget)
            {
                PresentMoveTargets();
            }
        }

        public void BeginExploreAction()
        {
            if (CanStartMainAction())
            {
                flowCoordinator.Activate(explorationEventPresenter);
            }
        }

        public void BeginDeployAction()
        {
            if (CanStartMainAction())
            {
                flowCoordinator.Activate(influenceActionPresenter);
            }
        }

        public void BeginDispatchAction()
        {
            if (!influenceActionPresenter.HasPendingFirstMove && !CanStartMainAction())
            {
                return;
            }

            if (!flowCoordinator.IsActive(influenceActionPresenter))
            {
                flowCoordinator.Activate(influenceActionPresenter);
            }

            influenceActionPresenter.BeginDispatch();
        }

        public void BeginResourceCollection()
        {
            SynchronizeLocalPlayerForHotseat();
            var player = context.CurrentState == null ? null : context.CurrentState.FindPlayer(context.LocalPlayerId);
            if (player == null || player.HasCollectedResourcesThisRound)
            {
                flowCoordinator.SetMode(InteractionMode.WaitingForNextPlayer);
                return;
            }

            flowCoordinator.Activate(resourceCollectionPresenter);
        }

        public void BeginBuildAction()
        {
            if (!CanStartMainAction())
            {
                return;
            }

            var state = context.CurrentState;
            var player = state.FindPlayer(context.LocalPlayerId);
            var cityBoardSlotIndex = BuildFacilityService.FindFirstEmptyCityBoardSlot(state, context.LocalPlayerId);
            if (player == null)
            {
                view.ShowPrompt("\u5f53\u524d\u73a9\u5bb6\u4e0d\u5b58\u5728\u3002");
                return;
            }

            if (cityBoardSlotIndex < 0)
            {
                view.ShowPrompt("\u57ce\u5e02\u9762\u677f\u6ca1\u6709\u7a7a\u69fd\u4f4d\u3002");
                return;
            }

            if (state.Decks.FacilitySupply.Count == 0)
            {
                view.ShowPrompt("\u8bbe\u65bd\u4f9b\u5e94\u533a\u4e3a\u7a7a\u3002");
                return;
            }

            flowCoordinator.ResetToChooseAction();
            view.ClearHighlights();
            view.RefreshActionPanel();
            view.ShowBuildFacilityOptions(new BuildFacilityOptionsViewModel(
                state.Decks.FacilitySupply.AsReadOnly(),
                player,
                cityBoardSlotIndex,
                SubmitBuildFacility,
                () => view.ShowPrompt("\u5df2\u53d6\u6d88\u5efa\u8bbe\u3002")));
            view.ShowPrompt("\u5efa\u8bbe\uff1a\u9009\u62e9\u8bbe\u65bd\u548c\u652f\u4ed8\u65b9\u5f0f\u3002");
        }

        public void SubmitBuildFacility(string facilityId, string paymentMode)
        {
            var state = context.CurrentState;
            var submission = commandPort.Submit(buildFacilitySelection.CreateCommand(
                state,
                context.LocalPlayerId,
                facilityId,
                paymentMode));
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u5efa\u8bbe\u547d\u4ee4\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            view.RefreshInformation();
            CompleteAction("\u5efa\u8bbe");
        }

        public void BeginDeclareCityStyle()
        {
            if (!CanStartQuickAction())
            {
                return;
            }

            flowCoordinator.ResetToChooseAction();
            view.ClearHighlights();
            view.RefreshActionPanel();
            var options = cityStyleSelection.BuildOptions(context.CurrentState, context.LocalPlayerId);
            view.ShowCityStyleOptions(new CityStyleOptionsViewModel(
                options.AsReadOnly(),
                SubmitDeclareCityStyle,
                () => view.ShowPrompt("\u5df2\u53d6\u6d88\u5ba3\u544a\u57ce\u5e02\u6837\u5f0f\u3002")));
            view.ShowPrompt("\u5ba3\u544a\u57ce\u5e02\u6837\u5f0f\uff1a\u67e5\u770b\u53ef\u5ba3\u544a\u6837\u5f0f\u548c\u4e0d\u53ef\u5ba3\u544a\u539f\u56e0\u3002");
        }

        public void SubmitDeclareCityStyle(string cityStyleId)
        {
            var submission = commandPort.Submit(cityStyleSelection.CreateCommand(context.LocalPlayerId, cityStyleId));
            if (!submission.CommandResult.Succeeded)
            {
                view.ShowPrompt(submission.CommandResult.Validation.Reason);
                return;
            }

            if (!submission.AppliedLocally)
            {
                view.ShowPrompt("\u5ba3\u544a\u57ce\u5e02\u6837\u5f0f\u547d\u4ee4\u5df2\u53d1\u9001\u7ed9\u4e3b\u673a\uff0c\u7b49\u5f85\u786e\u8ba4\u3002");
                return;
            }

            view.RefreshInformation();
            view.RefreshActionPanel();
            view.ShowPrompt("\u57ce\u5e02\u6837\u5f0f\u5ba3\u544a\u6210\u529f\uff0c\u6700\u7ec8\u8ba1\u5206\u4f1a\u8ba1\u5165\u8be5\u6837\u5f0f\u5206\u3002");
        }

        public void CompleteAction(string actionName)
        {
            completedMainActionName = actionName ?? string.Empty;
            flowCoordinator.ResetToChooseAction();
            view.CompleteMainActionPresentation(completedMainActionName);
        }

        public ActionPanelViewModel BuildActionPanelViewModel()
        {
            SynchronizeLocalPlayerForHotseat();
            var state = context.CurrentState;
            if (state == null)
            {
                return EmptyViewModel();
            }

            var player = state.FindPlayer(context.LocalPlayerId);
            var isActionPhase = state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2;
            var isLocalTurn = state.CurrentPlayerId == context.LocalPlayerId;
            var hasPendingChoice = state.HasPendingChoice();
            var mainActionDone = player != null && player.ActedMainActionThisTurn;
            var canChooseQuickAction = isActionPhase && isLocalTurn && !hasPendingChoice;
            var canChooseMainAction = canChooseQuickAction && !mainActionDone;
            var displayedMode = ResolveDisplayedMode(state, player, isActionPhase, isLocalTurn, hasPendingChoice, mainActionDone);
            var waiting = displayedMode == InteractionMode.WaitingForNextPlayer ||
                          (hasPendingChoice && CardFlowStateAdapter.GetPendingChoiceView(state)?.PlayerId != context.LocalPlayerId);

            return new ActionPanelViewModel(
                displayedMode,
                "\u672c\u673a\uff1a" + GetPlayerDisplayName(context.LocalPlayerId) + " / \u884c\u52a8\uff1a" + GetPlayerDisplayName(state.CurrentPlayerId),
                "\u7b2c " + state.Round + " \u56de\u5408 / \u884c\u52a8\u8f6e " + state.ActionRound,
                BuildStatus(state, player, isActionPhase, isLocalTurn, hasPendingChoice, mainActionDone, displayedMode),
                player != null,
                player == null ? PlayerColor.Red : player.Color,
                player == null ? 0 : player.InfluenceSupply,
                canChooseQuickAction && player != null && !player.UsedCharacterThisRound,
                canChooseQuickAction,
                canChooseMainAction,
                canChooseMainAction,
                canChooseMainAction,
                canChooseMainAction && player != null,
                canChooseMainAction,
                canChooseMainAction && player != null && player.UsedSpecialActionIdsThisRound.Count == 0,
                CanEndCurrentAction() && !RoundTrackRule.IsFinalState(state),
                waiting);
        }

        private bool CanStartMainAction()
        {
            return CanStartAction(false);
        }

        private bool CanStartQuickAction()
        {
            return CanStartAction(true);
        }

        private bool CanStartAction(bool quickAction)
        {
            SynchronizeLocalPlayerForHotseat();
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null)
            {
                view.ShowPrompt("\u5f53\u524d\u73a9\u5bb6\u4e0d\u5b58\u5728\u3002");
                return false;
            }

            if (state.CurrentPlayerId != context.LocalPlayerId)
            {
                view.ShowPrompt("\u7b49\u5f85\u73a9\u5bb6 " + state.CurrentPlayerId + " \u884c\u52a8\u3002");
                return false;
            }

            if (state.HasPendingChoice())
            {
                view.ShowPrompt("\u8bf7\u5148\u5904\u7406\u5f85\u9009\u62e9\u9879\u3002");
                return false;
            }

            if (!quickAction && player.ActedMainActionThisTurn)
            {
                view.ShowPrompt("\u5f53\u524d\u73a9\u5bb6\u5df2\u7ecf\u6267\u884c\u8fc7\u4e3b\u8981\u884c\u52a8\u3002");
                return false;
            }

            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                view.ShowPrompt(quickAction
                    ? "\u5f53\u524d\u9636\u6bb5\u4e0d\u80fd\u6267\u884c\u5feb\u901f\u884c\u52a8\u3002"
                    : "\u5f53\u524d\u9636\u6bb5\u4e0d\u80fd\u6267\u884c\u884c\u52a8\u3002");
                return false;
            }

            return true;
        }

        private void PresentMoveTargets()
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            var highlights = new List<WorkflowHighlight>();
            if (player != null && !string.IsNullOrEmpty(player.CityLocationId))
            {
                var adjacent = mapQuery.GetAdjacentLocations(player.CityLocationId);
                for (var i = 0; i < adjacent.Count; i++)
                {
                    if (!IsOccupiedByAnotherCity(adjacent[i].LocationId))
                    {
                        highlights.Add(new WorkflowHighlight(
                            WorkflowHighlightTargetKind.Location,
                            adjacent[i].LocationId,
                            WorkflowHighlightSemantic.MoveTarget));
                    }
                }
            }

            view.SetHighlights(highlights);
        }

        private bool CanUseInitialPlacementLocation(string locationId)
        {
            MapLocationDefinition location;
            try
            {
                location = mapQuery.GetLocation(locationId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!location.CanDockCity ||
                (mapQuery.Map.MapId == StaticMapDefinitions.FourPlayerMapId &&
                 !StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(locationId)))
            {
                return false;
            }

            return !IsOccupiedByAnotherCity(locationId);
        }

        private bool IsOccupiedByAnotherCity(string locationId)
        {
            var state = context.CurrentState;
            if (state == null)
            {
                return false;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player.PlayerId != context.LocalPlayerId && player.CityLocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private void SynchronizeLocalPlayerForHotseat()
        {
            var state = context.CurrentState;
            if (!context.ControlsCurrentPlayerLocally || state == null || state.CurrentPlayerId <= 0)
            {
                return;
            }

            if (state.Phase == GamePhase.ResourceCollection)
            {
                var playerId = FindFirstUncollectedResourceCollectionPlayerId(state);
                if (playerId > 0)
                {
                    context.SetLocalPlayerId(playerId);
                }
                return;
            }

            context.SetLocalPlayerId(state.CurrentPlayerId);
        }

        private static int FindFirstUncollectedResourceCollectionPlayerId(GameState state)
        {
            var order = new TurnOrderService().GetTurnOrder(state);
            for (var i = 0; i < order.Count; i++)
            {
                var player = state.FindPlayer(order[i]);
                if (player != null && !player.HasCollectedResourcesThisRound)
                {
                    return player.PlayerId;
                }
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                if (!state.Players[i].HasCollectedResourcesThisRound)
                {
                    return state.Players[i].PlayerId;
                }
            }

            return -1;
        }

        private InteractionMode ResolveDisplayedMode(
            GameState state,
            PlayerState player,
            bool isActionPhase,
            bool isLocalTurn,
            bool hasPendingChoice,
            bool mainActionDone)
        {
            if (hasPendingChoice)
            {
                return flowCoordinator.CurrentMode == InteractionMode.ResolvingEventInfluenceTarget
                    ? flowCoordinator.CurrentMode
                    : InteractionMode.PendingChoice;
            }

            if (state.Phase == GamePhase.ResourceCollection)
            {
                return player != null && !player.HasCollectedResourcesThisRound
                    ? InteractionMode.ResolvingResourceCollection
                    : InteractionMode.WaitingForNextPlayer;
            }

            if (state.Phase == GamePhase.Cleanup || (isActionPhase && (!isLocalTurn || mainActionDone)))
            {
                return InteractionMode.WaitingForNextPlayer;
            }

            return flowCoordinator.CurrentMode;
        }

        private string BuildStatus(
            GameState state,
            PlayerState player,
            bool isActionPhase,
            bool isLocalTurn,
            bool hasPendingChoice,
            bool mainActionDone,
            InteractionMode displayedMode)
        {
            if (state.Phase == GamePhase.Entrance)
            {
                return "\u5165\u573a\u9636\u6bb5\uff1a\u9009\u62e9\u521d\u59cb\u79fb\u52a8\u57ce\u5e02\u4f4d\u7f6e";
            }
            if (state.Phase == GamePhase.ResourceCollection)
            {
                return player == null
                    ? "\u91c7\u96c6\u9636\u6bb5\uff1a\u5f53\u524d\u73a9\u5bb6\u4e0d\u5b58\u5728"
                    : player.HasCollectedResourcesThisRound
                        ? "\u91c7\u96c6\u9636\u6bb5\uff1a\u5df2\u63d0\u4ea4\u91c7\u96c6\uff0c\u7b49\u5f85\u5176\u4ed6\u73a9\u5bb6"
                        : resourceCollectionPresenter.BuildStatus();
            }
            if (state.Phase == GamePhase.Cleanup)
            {
                return CanEndCurrentAction()
                    ? "\u6536\u5c3e\u9636\u6bb5\uff1a\u70b9\u51fb\u7ed3\u675f\u672c\u56de\u5408\u8fdb\u5165\u4e0b\u4e00\u56de\u5408"
                    : "\u6536\u5c3e\u9636\u6bb5\uff1a\u7b49\u5f85\u8d77\u59cb\u73a9\u5bb6\u7ed3\u675f\u672c\u56de\u5408";
            }
            if (!isActionPhase) return "\u5f53\u524d\u9636\u6bb5\u4e0d\u80fd\u6267\u884c\u884c\u52a8";
            if (hasPendingChoice)
            {
                var choice = CardFlowStateAdapter.GetPendingChoiceView(state);
                return choice != null && choice.PlayerId != context.LocalPlayerId
                    ? "\u7b49\u5f85\u73a9\u5bb6 " + choice.PlayerId + " \u5904\u7406\u4e8b\u4ef6\u9009\u62e9"
                    : "\u8bf7\u5148\u5904\u7406\u4e8b\u4ef6\u9009\u62e9";
            }
            if (!isLocalTurn) return "\u7b49\u5f85\u73a9\u5bb6 " + state.CurrentPlayerId + " \u884c\u52a8";
            if (mainActionDone) return BuildCompletedMainActionMessage(completedMainActionName);
            switch (displayedMode)
            {
                case InteractionMode.ResolvingMoveTarget: return "\u57ce\u5e02\u79fb\u52a8\uff1a\u9009\u62e9\u9ad8\u4eae\u8d44\u6e90\u70b9";
                case InteractionMode.ResolvingExploreTarget: return "\u63a2\u7d22\uff1a\u9009\u62e9\u9ad8\u4eae\u8d44\u6e90\u70b9";
                case InteractionMode.ResolvingDeployTarget: return "\u90e8\u7f72\uff1a\u9009\u62e9\u5f71\u54cd\u529b\u7a7a\u683c";
                case InteractionMode.ResolvingDispatchSource: return "\u8c03\u5ea6\uff1a\u9009\u62e9\u6765\u6e90\u5f71\u54cd\u529b";
                case InteractionMode.ResolvingDispatchTarget: return "\u8c03\u5ea6\uff1a\u9009\u62e9\u76ee\u6807\u8d44\u6e90\u70b9";
                case InteractionMode.ResolvingDispatchDecision: return "\u8c03\u5ea6\uff1a\u9009\u62e9\u7ee7\u7eed\u8c03\u5ea6\u6216\u5b8c\u6210";
                default: return player == null ? "\u672a\u77e5\u73a9\u5bb6" : "\u5c1a\u672a\u6267\u884c\u4e3b\u8981\u884c\u52a8";
            }
        }

        private string GetUnavailableEndActionPrompt()
        {
            var state = context.CurrentState;
            if (state == null) return "\u5f53\u524d\u6ca1\u6709\u53ef\u7ed3\u675f\u7684\u56de\u5408\u3002";
            if (state.Phase == GamePhase.ResourceCollection) return "\u5f53\u524d\u73a9\u5bb6\u5df2\u7ecf\u63d0\u4ea4\u8fc7\u91c7\u96c6\uff0c\u7b49\u5f85\u5176\u4ed6\u73a9\u5bb6\u3002";
            if (state.Phase == GamePhase.Cleanup) return "\u5f53\u524d\u53ea\u6709\u8d77\u59cb\u73a9\u5bb6\u53ef\u4ee5\u7ed3\u675f\u6536\u5c3e\u9636\u6bb5\u3002";
            return "\u5b8c\u6210\u4e3b\u8981\u884c\u52a8\u540e\u624d\u80fd\u7ed3\u675f\u672c\u56de\u5408\u3002";
        }

        private static string BuildEndActionPrompt(GameState state)
        {
            if (state != null && state.Phase == GamePhase.ResourceCollection) return "\u91c7\u96c6\u5df2\u63d0\u4ea4\uff0c\u7b49\u5f85\u5176\u4ed6\u73a9\u5bb6\u5b8c\u6210\u91c7\u96c6\u3002";
            if (state != null && state.Phase == GamePhase.Cleanup) return "\u91c7\u96c6\u7ed3\u7b97\u5b8c\u6210\u3002\u8bf7\u7ed3\u675f\u6536\u5c3e\u9636\u6bb5\u8fdb\u5165\u4e0b\u4e00\u56de\u5408\u3002";
            if (state != null && state.Phase == GamePhase.ActionRound1 && state.ActionRound == 1) return "\u5df2\u8fdb\u5165\u4e0b\u4e00\u56de\u5408\uff0c\u8bf7\u7ee7\u7eed\u884c\u52a8\u3002";
            return "\u672c\u56de\u5408\u5df2\u7ed3\u675f\uff0c\u7b49\u5f85\u4e0b\u4e00\u4f4d\u73a9\u5bb6\u884c\u52a8\u3002";
        }

        public static string BuildCompletedMainActionMessage(string actionName)
        {
            return string.IsNullOrEmpty(actionName) ? "\u4e3b\u8981\u884c\u52a8\u5df2\u5b8c\u6210" : actionName + "\u5df2\u5b8c\u6210";
        }

        private string GetPlayerDisplayName(int playerId)
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(playerId);
            return player == null ? playerId.ToString() : string.IsNullOrEmpty(player.Name) ? "Player " + playerId : player.Name;
        }

        private static ActionPanelViewModel EmptyViewModel()
        {
            return new ActionPanelViewModel(
                InteractionMode.Hidden, string.Empty, string.Empty, string.Empty, false, PlayerColor.Red, 0,
                false, false, false, false, false, false, false, false, false, false);
        }
    }
}
