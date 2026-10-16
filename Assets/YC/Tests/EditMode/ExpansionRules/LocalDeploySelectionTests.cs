using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Exploration;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class LocalDeploySelectionTests
    {
        [Test]
        public void OpenAndLocalCancelDoNotSubmitOrChangeAuthoritativeState()
        {
            var f = new Fixture(); string before = JsonConvert.SerializeObject(f.Context.State);
            f.Turn.BeginDeployAction();
            Assert.That(f.Flow.IsActive(f.Influence), Is.True);
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.True);
            Assert.That(f.View.Highlights.Any(h => h.TargetId == f.Target), Is.True);
            f.Influence.SelectSlot(f.Target);
            Assert.That(f.Influence.HasPendingConfirmation, Is.True);
            Assert.That(f.Commands.Commands, Is.Empty);
            f.Turn.BeginDeployAction();
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.False);
            Assert.That(f.Influence.HasPendingConfirmation, Is.False);
            Assert.That(f.View.Highlights, Is.Empty);
            Assert.That(f.Commands.Commands, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(f.Context.State), Is.EqualTo(before));
        }

        [Test]
        public void OnlySecondSelectionSubmitsOneCommandWithTheConcreteTarget()
        {
            var f = new Fixture(); f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target);
            Assert.That(f.Commands.Commands, Is.Empty);
            f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            Assert.That(f.Commands.Commands.Count, Is.EqualTo(1));
            var command = f.Commands.Commands.Single();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.DeployInfluence));
            Assert.That(command.PlayerId, Is.EqualTo(1)); Assert.That(command.TargetId, Is.EqualTo(f.Target));
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.False);
        }

        [Test]
        public void SwitchingFromDispatchToDeployActivatesTheSharedPresenterAgain()
        {
            var f = new Fixture(); f.Turn.BeginDispatchAction();
            Assert.That(f.Influence.IsSelectingDispatchSource, Is.True);
            f.Turn.BeginDeployAction();
            Assert.That(f.Flow.IsActive(f.Influence), Is.True);
            Assert.That(f.Influence.IsSelectingDispatchSource, Is.False);
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.True);
            Assert.That(f.Commands.Commands, Is.Empty);
        }

        [TestCase("other_player")]
        [TestCase("missing_player")]
        [TestCase("wrong_phase")]
        [TestCase("no_budget")]
        public void UnavailableMainActionCannotOpenOrSubmitDeployment(string reason)
        {
            var f = new Fixture();
            if (reason == "other_player") f.Context.LocalPlayerId = 2;
            if (reason == "missing_player") f.Context.LocalPlayerId = 99;
            if (reason == "wrong_phase") f.Context.State.Phase = GamePhase.Cleanup;
            if (reason == "no_budget") f.Context.State.FindPlayer(1).RemainingMainActionsThisTurn = 0;
            f.Turn.BeginDeployAction();
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.False);
            Assert.That(f.Commands.Commands, Is.Empty); Assert.That(f.View.Highlights, Is.Empty);
        }

        [TestCase("player")]
        [TestCase("game")]
        [TestCase("state")]
        [TestCase("phase")]
        [TestCase("round")]
        [TestCase("budget")]
        public void OldConfirmationIsDiscardedWhenItsContextChanges(string change)
        {
            var f = new Fixture(); f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target);
            if (change == "player") { f.Context.LocalPlayerId = 2; f.Context.State.CurrentPlayerId = 2; }
            if (change == "game") f.Context.State.GameId = "another-game";
            if (change == "state") f.Context.State = GameStateCloneService.DeepClone(f.Context.State);
            if (change == "phase") f.Context.State.Phase = GamePhase.ActionRound2;
            if (change == "round") f.Context.State.Round++;
            if (change == "budget") f.Context.State.FindPlayer(1).RemainingMainActionsThisTurn = 0;
            f.Influence.SelectSlot(f.Target);
            Assert.That(f.Commands.Commands, Is.Empty); Assert.That(f.Influence.HasPendingConfirmation, Is.False);
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.False); Assert.That(f.View.Highlights, Is.Empty);
        }

        [Test]
        public void SameRevisionOccupancyChangeInvalidatesTheSecondClickAndRefreshesTargets()
        {
            var f = new Fixture(); f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target);
            int revision = f.Context.State.EffectRuntime.StateRevision;
            f.Context.State.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, LocationId = "A", SlotId = f.Target });
            f.Influence.SelectSlot(f.Target);
            Assert.That(f.Context.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(f.Commands.Commands, Is.Empty); Assert.That(f.Influence.HasPendingConfirmation, Is.False);
            Assert.That(f.View.Highlights.Any(h => h.TargetId == f.Target), Is.False);
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RejectionOrMissingResultUnlocksAndRequiresTwoFreshSelections(bool missingResult)
        {
            var f = new Fixture();
            f.Commands.Handler = command => missingResult ? null : new WorkflowSubmissionResult(
                CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "目标已失效")), false);
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            Assert.That(f.Commands.Commands.Count, Is.EqualTo(1)); Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.False);
            Assert.That(f.Influence.HasPendingConfirmation, Is.False); Assert.That(f.Influence.IsSelectingDeployTarget, Is.True);
            Assert.That(f.View.Highlights.Any(h => h.TargetId == f.Target), Is.True);
            f.Influence.SelectSlot(f.Target); Assert.That(f.Commands.Commands.Count, Is.EqualTo(1));
            f.Influence.SelectSlot(f.Target); Assert.That(f.Commands.Commands.Count, Is.EqualTo(2));
        }

        [Test]
        public void WaitingForHostBlocksRepeatedClicksAndIgnoresUnrelatedReceipts()
        {
            var f = new Fixture(); f.Commands.Handler = command => Success(false);
            var router = new InteractionRouter(message => { }); router.Register(f.Turn.DeployInteraction);
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            string id = f.Commands.Commands.Single().CommandId;
            f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target); f.Turn.BeginDeployAction();
            router.NotifyCommandSettled("unrelated-command");
            Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.True); Assert.That(f.Commands.Commands.Count, Is.EqualTo(1));
            router.NotifyCommandSettled(id);
            Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.False); Assert.That(f.Influence.HasPendingConfirmation, Is.False);
            Assert.That(f.View.Highlights.Any(h => h.TargetId == f.Target), Is.True);
        }

        [Test]
        public void SwitchingActionDoesNotAllowResubmitOrLetAnOldReceiptRestoreDeployment()
        {
            var f = new Fixture(); f.Commands.Handler = command => Success(false);
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            f.Turn.BeginDispatchAction(); Assert.That(f.Influence.IsSelectingDispatchSource, Is.True);
            f.Turn.BeginDeployAction(); Assert.That(f.Influence.IsSelectingDispatchSource, Is.True);
            Assert.That(f.Commands.Commands.Count, Is.EqualTo(1));
            f.Influence.NotifyDeployCommandSettled(f.Commands.Commands.Single().CommandId);
            Assert.That(f.Influence.IsSelectingDispatchSource, Is.True);
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.False); Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.False);
        }

        [Test]
        public void ReentrantClicksDuringSubmitAreBlockedBeforeThePortReturns()
        {
            var f = new Fixture(); f.Commands.Handler = command =>
            {
                f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
                return Success(false);
            };
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            Assert.That(f.Commands.Commands.Count, Is.EqualTo(1)); Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.True);
        }

        [Test]
        public void APortExceptionRestoresSelectableTargetsAndReleasesTheGate()
        {
            var f = new Fixture(); f.Commands.Handler = command => throw new InvalidOperationException("expected-port-failure");
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target);
            Assert.Throws<InvalidOperationException>(() => f.Influence.SelectSlot(f.Target));
            Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.False); Assert.That(f.Influence.HasPendingConfirmation, Is.False);
            Assert.That(f.View.Highlights.Any(h => h.TargetId == f.Target), Is.True);
        }

        [Test]
        public void LocalSuccessAfterReentrantActionSwitchDoesNotCompleteTheNewAction()
        {
            var f = new Fixture(); f.Commands.Handler = command => { f.Turn.BeginDispatchAction(); return Success(true); };
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            Assert.That(f.Influence.IsSelectingDispatchSource, Is.True);
            Assert.That(f.View.Completed, Is.Empty); Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.False);
        }

        [Test]
        public void AReceiptFromThePreviousGameCannotUnlockTheNewGameCommand()
        {
            var f = new Fixture(); f.Commands.Handler = command => Success(false);
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            string oldId = f.Commands.Commands.Single().CommandId;
            f.Flow.ResetToChooseAction(); f.Context.State.GameId = "new-game";
            f.Turn.BeginDeployAction(); f.Influence.SelectSlot(f.Target); f.Influence.SelectSlot(f.Target);
            Assert.That(f.Commands.Commands.Count, Is.EqualTo(2));
            f.Influence.NotifyDeployCommandSettled(oldId);
            Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.True);
            Assert.That(f.Influence.IsSelectingDeployTarget, Is.True);
            f.Influence.NotifyDeployCommandSettled(f.Commands.Commands[1].CommandId);
            Assert.That(f.Influence.IsDeploySubmissionInFlight, Is.False);
        }

        private static WorkflowSubmissionResult Success(bool local) =>
            new WorkflowSubmissionResult(CommandResult.SuccessResult(new List<GameEvent>(), "ok"), local);

        private sealed class Fixture
        {
            public readonly TestContext Context = new TestContext();
            public readonly CommandPort Commands = new CommandPort();
            public readonly TestView View = new TestView();
            public readonly InteractionFlowCoordinator Flow = new InteractionFlowCoordinator();
            public readonly InfluenceActionPresenter Influence;
            public readonly TurnActionPresenter Turn;
            public readonly string Target = InfluenceService.GetLocationSlotId("A", 0);
            public Fixture()
            {
                var map = new GameMapDefinition { MapId = "local-deploy-selection" };
                map.Locations.Add(new MapLocationDefinition { LocationId = "A", InfluenceSlotCount = 2 });
                var state = new GameState { GameId = "local-deploy-selection", MapId = map.MapId,
                    Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1 };
                state.Players.Add(new PlayerState { PlayerId = 1, InfluenceSupply = 5 });
                state.Players.Add(new PlayerState { PlayerId = 2, InfluenceSupply = 5 });
                state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "A", ResourceType = ResourceType.Iron });
                Context.State = state;
                var query = new MapQueryService(map); var service = new InfluenceService(query);
                Influence = new InfluenceActionPresenter(Context, Commands, View, query, service);
                var collection = new ResourceCollectionPresenter(Context, Commands, View, query, new ResourceCollectionService(query));
                var exploration = new ExplorationEventPresenter(Context, Commands, View, query, new ExplorationService(query), service);
                Turn = new TurnActionPresenter(Context, Commands, View, Flow, query, collection, Influence, exploration);
                Flow.ResetToChooseAction();
            }
        }

        private sealed class TestContext : IWritableGameplayContext
        {
            public GameState State;
            public GameState CurrentState => State;
            public int LocalPlayerId { get; set; } = 1;
            public bool ControlsCurrentPlayerLocally => false;
            public void SetLocalPlayerId(int playerId) { LocalPlayerId = playerId; }
        }

        private sealed class CommandPort : IGameCommandPort
        {
            public readonly List<GameCommand> Commands = new List<GameCommand>();
            public Func<GameCommand, WorkflowSubmissionResult> Handler = command => Success(true);
            public WorkflowSubmissionResult Submit(GameCommand command) { Commands.Add(command); return Handler(command); }
        }

        private sealed class TestView : ITurnActionView, IResourceCollectionView, IInfluenceActionView, IExplorationEventView
        {
            public readonly List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();
            public string Completed = string.Empty;
            public void ShowPrompt(string message) { }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights) { Highlights.Clear(); if (highlights != null) Highlights.AddRange(highlights); }
            public void ClearHighlights() { Highlights.Clear(); }
            public void RefreshFromState() { }
            public void RefreshInformation() { }
            public void RefreshActionPanel() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string actionName) { Completed = actionName; }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel viewModel) { }
            public void HideBuildFacilityDraft() { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel viewModel) { }
            public void ShowRoutePaymentOptions(string routeId, int cost, IReadOnlyList<int> recipients) { }
            public string GetPlayerDisplayName(int playerId) => playerId.ToString();
            public void RefreshSelectionView() { }
            public void SetInteractionMode(InteractionMode mode) { }
            public void ShowDispatchDecision(DispatchDecisionViewModel viewModel) { }
            public void HideDispatchDecision() { }
            public void RefreshInfluencePreview(bool pending, string source, string target) { }
            public void CompleteAction(string actionName) { Completed = actionName; }
            public void ShowExplorePathOptions(ExplorePathOptionsViewModel viewModel) { }
            public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel viewModel) { }
            public void ShowEventCardOptions(EventCardOptionsViewModel viewModel) { }
            public void CollapseEventOptions() { }
            public void HideEventOptions() { }
            public void RefreshResourceAndInfluence() { }
        }
    }
}
