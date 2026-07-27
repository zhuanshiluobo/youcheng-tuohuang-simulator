using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class MoveInteraction : InteractionBase
    {
        private enum MoveStage
        {
            Inactive,
            SelectingTarget
        }

        private readonly IWritableGameplayContext context;
        private readonly CommandGateway commandGateway;
        private readonly ITurnActionView view;
        private readonly InteractionFlowCoordinator flowCoordinator;
        private readonly IMapQueryService mapQuery;
        private readonly Action activateWorkflowOwner;
        private readonly Func<bool> canStartMainAction;
        private readonly Action<string> completeAction;
        private MoveStage stage;

        public MoveInteraction(
            IWritableGameplayContext context,
            IGameCommandPort commandPort,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            Action activateWorkflowOwner,
            Func<bool> canStartMainAction,
            Action<string> completeAction)
            : this(
                context,
                new CommandGateway(
                    commandPort ?? throw new ArgumentNullException(nameof(commandPort))),
                view,
                flowCoordinator,
                mapQuery,
                activateWorkflowOwner,
                canStartMainAction,
                completeAction)
        {
        }

        internal MoveInteraction(
            IWritableGameplayContext context,
            CommandGateway commandGateway,
            ITurnActionView view,
            InteractionFlowCoordinator flowCoordinator,
            IMapQueryService mapQuery,
            Action activateWorkflowOwner,
            Func<bool> canStartMainAction,
            Action<string> completeAction)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.commandGateway = commandGateway ??
                                  throw new ArgumentNullException(nameof(commandGateway));
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            this.flowCoordinator = flowCoordinator ??
                                   throw new ArgumentNullException(nameof(flowCoordinator));
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.activateWorkflowOwner = activateWorkflowOwner ??
                                         throw new ArgumentNullException(
                                             nameof(activateWorkflowOwner));
            this.canStartMainAction = canStartMainAction ??
                                      throw new ArgumentNullException(nameof(canStartMainAction));
            this.completeAction = completeAction ??
                                  throw new ArgumentNullException(nameof(completeAction));
        }

        public override string Id
        {
            get { return "move-city"; }
        }

        public override InteractionPriority Priority
        {
            get { return InteractionPriority.ActiveAction; }
        }

        public override bool IsActive
        {
            get { return stage == MoveStage.SelectingTarget; }
        }

        public InteractionMode Mode
        {
            get
            {
                return IsActive
                    ? InteractionMode.Busy
                    : InteractionMode.ChooseAction;
            }
        }

        public bool IsSelectingMoveTarget
        {
            get { return IsActive; }
        }

        public bool IsAwaitingInitialPlacement
        {
            get
            {
                var state = context.CurrentState;
                if (state == null || state.Phase != GamePhase.Entrance)
                {
                    return false;
                }

                var player = state.FindPlayer(context.LocalPlayerId);
                return player == null || string.IsNullOrEmpty(player.CityLocationId);
            }
        }

        public override InteractionResult OnLocationClicked(string locationId)
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnInfluenceSlotClicked(string slotId)
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnMobileCityClicked()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionResult OnEscape()
        {
            return InteractionResult.Passthrough;
        }

        public override InteractionPresentation BuildPresentation()
        {
            if (!IsActive)
            {
                return InteractionPresentation.Empty;
            }

            return new InteractionPresentation(
                BuildMoveTargetHighlights(),
                "\u57ce\u5e02\u79fb\u52a8\uff1a\u9009\u62e9\u9ad8\u4eae\u8d44\u6e90\u70b9",
                InteractionMode.Busy);
        }

        public void Activate()
        {
            stage = MoveStage.SelectingTarget;
            PresentMoveTargets();
        }

        public override void Cancel()
        {
            stage = MoveStage.Inactive;
            view.ClearHighlights();
        }

        public void Begin()
        {
            if (!canStartMainAction())
            {
                return;
            }

            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                view.ShowPrompt("\u73a9\u5bb6\u57ce\u5e02\u4e0d\u5728\u573a\u4e0a\u3002");
                return;
            }

            activateWorkflowOwner();
            view.RefreshActionPanel();
            view.ShowPrompt(
                "\u57ce\u5e02\u79fb\u52a8\uff1a\u9009\u62e9\u4e00\u4e2a\u9ad8\u4eae\u8d44\u6e90\u70b9\u3002");
        }

        public void Move(string locationId)
        {
            commandGateway.Submit(
                new GameCommand
                {
                    Kind = GameCommandKind.MoveCity,
                    PlayerId = context.LocalPlayerId,
                    TargetId = locationId ?? string.Empty
                },
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("\u79fb\u52a8\u547d\u4ee4"))
                {
                    OnAppliedLocally = result =>
                    {
                        if (context.CurrentState.HasPendingChoice())
                        {
                            flowCoordinator.SetMode(InteractionMode.Busy);
                            view.RefreshFromState();
                            view.ShowPendingChoice();
                            return;
                        }

                        completeAction("\u57ce\u5e02\u79fb\u52a8");
                    }
                });
        }

        public void RestorePresentation()
        {
            if (IsActive)
            {
                PresentMoveTargets();
            }
        }

        public bool IsLocalPlayersTurn()
        {
            var state = context.CurrentState;
            return state == null || state.CurrentPlayerId == context.LocalPlayerId;
        }

        public void PlaceInitialCity(string locationId)
        {
            commandGateway.Submit(
                new GameCommand
                {
                    Kind = GameCommandKind.ChooseInitialLocation,
                    PlayerId = context.LocalPlayerId,
                    TargetId = locationId ?? string.Empty
                },
                new SubmitCallbacks(
                    view.ShowPrompt,
                    CommandGateway.BuildWaitingForHostPrompt("\u5165\u573a\u547d\u4ee4"))
                {
                    OnAppliedLocally = result => view.RefreshFromState()
                });
        }

        public IReadOnlyList<WorkflowHighlight> BuildInitialPlacementHighlights()
        {
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

        private void PresentMoveTargets()
        {
            view.SetHighlights(BuildMoveTargetHighlights());
        }

        private IReadOnlyList<WorkflowHighlight> BuildMoveTargetHighlights()
        {
            var state = context.CurrentState;
            var player = state == null ? null : state.FindPlayer(context.LocalPlayerId);
            var highlights = new List<WorkflowHighlight>();
            if (player == null || string.IsNullOrEmpty(player.CityLocationId))
            {
                return highlights;
            }

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

            return highlights;
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
                if (player.PlayerId != context.LocalPlayerId &&
                    player.CityLocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
