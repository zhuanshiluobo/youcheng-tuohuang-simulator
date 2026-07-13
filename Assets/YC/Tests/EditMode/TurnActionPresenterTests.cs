using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class TurnActionPresenterTests
    {
        [Test]
        public void ActionPhaseViewModel_ReflectsMainActionAndEndAvailability()
        {
            var fixture = CreateFixture();

            var before = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(before.CanMoveCity, Is.True);
            Assert.That(before.CanBuild, Is.True);
            Assert.That(before.CanEndAction, Is.False);

            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            var after = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(after.CanMoveCity, Is.False);
            Assert.That(after.CanBuild, Is.False);
            Assert.That(after.CanEndAction, Is.True);
        }

        [Test]
        public void CharacterAction_RequiresCoveredUnusedCard()
        {
            var fixture = CreateFixture();
            var player = fixture.Context.State.FindPlayer(1);

            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseCharacter, Is.False);

            player.CoveredCharacterCardId = "character-red-texas";
            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseCharacter, Is.True);

            player.UsedCharacterThisRound = true;
            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseCharacter, Is.False);
        }

        [Test]
        public void ResourceCollectionAndCleanup_ExposeTheirOwnEndRules()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Phase = GamePhase.ResourceCollection;
            Assert.That(fixture.Presenter.CanEndCurrentAction(), Is.True);
            Assert.That(fixture.Presenter.BuildActionPanelViewModel().Mode,
                Is.EqualTo(InteractionMode.ResolvingResourceCollection));

            fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound = true;
            fixture.Context.State.FindPlayer(2).HasCollectedResourcesThisRound = true;
            Assert.That(fixture.Presenter.CanEndCurrentAction(), Is.False);

            fixture.Context.State.Phase = GamePhase.Cleanup;
            fixture.Context.State.StartPlayerId = 1;
            fixture.Context.State.CurrentPlayerId = 1;
            fixture.Context.LocalPlayerId = 1;
            Assert.That(fixture.Presenter.CanEndCurrentAction(), Is.True);
        }

        [Test]
        public void HotseatSynchronization_SelectsCurrentOrFirstUncollectedPlayer()
        {
            var fixture = CreateFixture();
            fixture.Context.State.CurrentPlayerId = 2;
            fixture.Presenter.SynchronizeFromState();
            Assert.That(fixture.Context.LocalPlayerId, Is.EqualTo(2));

            fixture.Context.State.Phase = GamePhase.ResourceCollection;
            fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound = true;
            fixture.Context.State.FindPlayer(2).HasCollectedResourcesThisRound = false;
            fixture.Context.LocalPlayerId = 1;
            fixture.Presenter.SynchronizeFromState();
            Assert.That(fixture.Context.LocalPlayerId, Is.EqualTo(2));
        }

        [Test]
        public void InitialPlacement_HighlightsDockAndBuildsCommand()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Phase = GamePhase.Entrance;
            fixture.Context.State.FindPlayer(1).CityLocationId = string.Empty;

            Assert.That(fixture.Presenter.BuildInitialPlacementHighlights().Count, Is.EqualTo(2));
            fixture.Presenter.PlaceInitialCity("A");

            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.ChooseInitialLocation));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo("A"));
            Assert.That(fixture.View.RefreshFromStateCount, Is.EqualTo(1));
        }

        [Test]
        public void MoveCity_SuccessFailureAndHostWaitUseDistinctOutcomes()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginMoveAction();
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingMoveTarget));
            Assert.That(fixture.View.Highlights.Count, Is.EqualTo(1));

            fixture.Commands.NextResult = Rejected("blocked");
            fixture.Presenter.MoveCity("B");
            Assert.That(fixture.View.Prompt, Is.EqualTo("blocked"));

            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.MoveCity("B");
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));

            fixture.Commands.NextResult = Success(true);
            fixture.Presenter.MoveCity("B");
            Assert.That(fixture.View.CompletedAction, Is.EqualTo("城市移动"));
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ChooseAction));
        }

        [Test]
        public void Build_DraftsLocallyAndSubmitsOnlyAfterFinalConfirmation()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginBuildAction();
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Selecting));
            Assert.That(fixture.Commands.LastCommand, Is.Null);

            fixture.Presenter.BeginBuildFacilityDrag(FacilityCardDatabase.TradeDistrict);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Dragging));
            fixture.Presenter.DropBuildFacility(3);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));
            fixture.Presenter.SelectBuildFacilityPayment(BuildFacilityService.PaymentModeGold);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Confirming));
            Assert.That(fixture.Commands.LastCommand, Is.Null, "支付预选不得提交命令。");

            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.ConfirmBuildFacility();
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.BuildFacility));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
            Assert.That(fixture.Commands.LastCommand.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter], Is.EqualTo("3"));
            Assert.That(fixture.Commands.LastCommand.Parameters[BuildFacilityCommandHandler.PaymentModeParameter], Is.EqualTo(BuildFacilityService.PaymentModeGold));
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        [Test]
        public void Declare_CreatesCommandAndKeepsNetworkingWaitSemantics()
        {
            var fixture = CreateFixture();
            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.SubmitDeclareCityStyle("style-test");
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.DeclareCityStyle));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo("style-test"));
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        [Test]
        public void SwitchingWorkflow_CancelsPreviousSelectionAndTracksDynamicMode()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginExploreAction();
            Assert.That(fixture.Flow.IsActive(fixture.Exploration), Is.True);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingExploreTarget));

            fixture.Presenter.BeginDeployAction();
            Assert.That(fixture.Exploration.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Flow.IsActive(fixture.Influence), Is.True);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingDeployTarget));
        }

        [Test]
        public void EndAction_WhenSentToHost_DoesNotRefreshOrResetWorkflow()
        {
            var fixture = CreateFixture();
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.BeginExploreAction();

            fixture.Presenter.EndCurrentAction();

            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.EndAction));
            Assert.That(fixture.View.RefreshFromStateCount, Is.Zero);
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        private static Fixture CreateFixture()
        {
            var map = new GameMapDefinition
            {
                MapId = "turn-presenter",
                Locations =
                {
                    new MapLocationDefinition { LocationId = "A", CanDockCity = true, InfluenceSlotCount = 1 },
                    new MapLocationDefinition { LocationId = "B", CanDockCity = true, InfluenceSlotCount = 1 }
                },
                Routes =
                {
                    new MapRouteDefinition
                    {
                        RouteId = "R1",
                        FromLocationId = "A",
                        ToLocationId = "B",
                        InfluenceSlotCount = 1
                    }
                }
            };
            var context = new FakeContext
            {
                State = new GameState
                {
                    Phase = GamePhase.ActionRound1,
                    Round = 1,
                    MaxRounds = 8,
                    ActionRound = 1,
                    StartPlayerId = 1,
                    CurrentPlayerId = 1,
                    Players =
                    {
                        new PlayerState
                        {
                            PlayerId = 1,
                            CityLocationId = "A",
                            Color = PlayerColor.Red,
                            Resources = new ResourceSet { GoldVoucher = 100 }
                        },
                        new PlayerState { PlayerId = 2, CityLocationId = string.Empty, Color = PlayerColor.Blue }
                    },
                    Decks =
                    {
                        FacilitySupply = { FacilityCardDatabase.TradeDistrict }
                    }
                }
            };
            var commands = new FakeCommandPort { NextResult = Success(true) };
            var view = new FakeView();
            var flow = new InteractionFlowCoordinator();
            flow.ResetToChooseAction();
            var mapQuery = new MapQueryService(map);
            var influenceService = new InfluenceService(mapQuery);
            var resource = new ResourceCollectionPresenter(
                context, commands, view, mapQuery, new ResourceCollectionService(mapQuery));
            var influence = new InfluenceActionPresenter(
                context, commands, view, mapQuery, influenceService);
            var exploration = new ExplorationEventPresenter(
                context, commands, view, mapQuery, new ExplorationService(mapQuery), influenceService);
            return new Fixture
            {
                Context = context,
                Commands = commands,
                View = view,
                Flow = flow,
                Influence = influence,
                Exploration = exploration,
                Presenter = new TurnActionPresenter(
                    context, commands, view, flow, mapQuery, resource, influence, exploration)
            };
        }

        private static WorkflowSubmissionResult Success(bool appliedLocally)
        {
            return new WorkflowSubmissionResult(
                CommandResult.SuccessResult(new List<GameEvent>(), "ok"), appliedLocally);
        }

        private static WorkflowSubmissionResult Rejected(string reason)
        {
            return new WorkflowSubmissionResult(
                CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason)), false);
        }

        private sealed class Fixture
        {
            public FakeContext Context;
            public FakeCommandPort Commands;
            public FakeView View;
            public InteractionFlowCoordinator Flow;
            public InfluenceActionPresenter Influence;
            public ExplorationEventPresenter Exploration;
            public TurnActionPresenter Presenter;
        }

        private sealed class FakeContext : IWritableGameplayContext
        {
            public GameState State;
            public int LocalPlayerId { get; set; } = 1;
            public bool ControlsCurrentPlayerLocally { get; set; } = true;
            public GameState CurrentState { get { return State; } }
            public void SetLocalPlayerId(int playerId) { LocalPlayerId = playerId; }
        }

        private sealed class FakeCommandPort : IGameCommandPort
        {
            public GameCommand LastCommand;
            public WorkflowSubmissionResult NextResult;
            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                LastCommand = command;
                return NextResult;
            }
        }

        private sealed class FakeView : ITurnActionView, IResourceCollectionView,
            IInfluenceActionView, IExplorationEventView
        {
            public readonly List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();
            public string Prompt = string.Empty;
            public string CompletedAction = string.Empty;
            public int RefreshFromStateCount;
            public BuildFacilityDraftViewModel BuildDraft;

            public void ShowPrompt(string message) { Prompt = message; }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights)
            {
                Highlights.Clear();
                if (highlights != null) Highlights.AddRange(highlights);
            }
            public void ClearHighlights() { Highlights.Clear(); }
            public void RefreshFromState() { RefreshFromStateCount += 1; }
            public void RefreshInformation() { }
            public void RefreshActionPanel() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string actionName) { CompletedAction = actionName; }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel viewModel) { BuildDraft = viewModel; }
            public void HideBuildFacilityDraft() { BuildDraft = null; }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel viewModel) { }
            public void ShowRoutePaymentOptions(string routeId, int cost, IReadOnlyList<int> recipients) { }
            public string GetPlayerDisplayName(int playerId) { return "Player " + playerId; }
            public void RefreshSelectionView() { }
            public void SetInteractionMode(InteractionMode mode) { }
            public void ShowDispatchDecision(DispatchDecisionViewModel viewModel) { }
            public void HideDispatchDecision() { }
            public void RefreshInfluencePreview(bool pending, string source, string target) { }
            public void CompleteAction(string actionName) { CompletedAction = actionName; }
            public void ShowExplorePathOptions(ExplorePathOptionsViewModel viewModel) { }
            public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel viewModel) { }
            public void ShowEventCardOptions(EventCardOptionsViewModel viewModel) { }
            public void CollapseEventOptions() { }
            public void HideEventOptions() { }
            public void RefreshResourceAndInfluence() { }
        }
    }
}
