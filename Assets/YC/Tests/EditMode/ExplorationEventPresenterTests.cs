using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class ExplorationEventPresenterTests
    {
        [Test]
        public void ExploreInteraction_WrapsPresenterWithoutOwningMapInputSideEffects()
        {
            var fixture = CreateFixture(false);
            var interaction = new ExploreInteraction(fixture.Presenter);

            Assert.That(interaction.Id, Is.EqualTo("active.explore"));
            Assert.That(interaction.Priority, Is.EqualTo(InteractionPriority.ActiveAction));
            Assert.That(interaction.IsActive, Is.False);
            Assert.That(interaction.BuildPresentation().IsEmpty, Is.True);

            fixture.Presenter.BeginTargetSelection();

            Assert.That(interaction.IsActive, Is.True);
            Assert.That(interaction.BuildPresentation().PanelMode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(interaction.BuildPresentation().PromptText, Does.Contain("探索"));
            Assert.That(
                interaction.OnLocationClicked("B"),
                Is.SameAs(InteractionResult.Passthrough));
            interaction.Cancel();

            Assert.That(fixture.Presenter.IsActive, Is.False);
            Assert.That(interaction.BuildPresentation().IsEmpty, Is.True);
        }

        [Test]
        public void SinglePathWithoutOpponentChoiceSubmitsEncodedExploreCommand()
        {
            var fixture = CreateFixture(false);

            fixture.Presenter.SelectTarget("B");

            Assert.That(fixture.View.PathOptions, Is.Null);
            Assert.That(fixture.View.PaymentOptions, Is.Null);
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.ExploreLocation));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo("B"));
            Assert.That(fixture.Commands.LastCommand.Parameters[ExploreLocationCommandHandler.PathLocationIdsParameter],
                Is.EqualTo("A,B"));
            Assert.That(fixture.Commands.LastCommand.Parameters[ExploreLocationCommandHandler.RouteIdsParameter],
                Is.EqualTo("R1"));
            Assert.That(fixture.Commands.LastCommand.Parameters.ContainsKey(
                ResolveFacilityEffectCommandHandler.PendingSessionIdParameter), Is.False);
        }

        [Test]
        public void OpponentTollShowsRecipientChoiceAndEncodesSelection()
        {
            var fixture = CreateFixture(true);

            fixture.Presenter.SelectTarget("B");
            Assert.That(fixture.Commands.LastCommand, Is.Null);
            Assert.That(fixture.View.PaymentOptions, Is.Not.Null);
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsChoosingPaymentRecipient, Is.True);
            Assert.That(
                fixture.Presenter.CurrentPrompt,
                Is.EqualTo("选择每条路线的过路费接收者，然后确认探索。"));

            fixture.View.PaymentOptions.SelectRecipient("R1", 2);
            fixture.View.PaymentOptions.Confirm();

            Assert.That(fixture.Commands.LastCommand.Parameters[ExploreLocationCommandHandler.PaymentRecipientsParameter],
                Is.EqualTo("R1=2"));
        }

        [Test]
        public void AdditionalExploreReusesPathAndPaymentFlowAndBuildsFacilityResolveCommand()
        {
            var fixture = CreateFixture(true);
            var pending = new PendingCardSessionState
            {
                SessionId = "warehouse-session",
                ScenarioId = FacilityPendingChoiceTypes.ScenarioId,
                ChoiceType = FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore,
                CardId = "building_031",
                PlayerId = 1,
                OptionIds = { FacilityPendingChoiceTypes.ExploreOption }
            };
            fixture.Context.State.PendingCardSession = pending;
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            fixture.Presenter.PrepareAdditionalExplore(
                pending,
                FacilityPendingChoiceTypes.ExploreOption);
            fixture.Presenter.Activate();

            fixture.Presenter.SelectTarget("B");
            Assert.That(fixture.View.PaymentOptions, Is.Not.Null);
            Assert.That(fixture.Presenter.IsChoosingPaymentRecipient, Is.True);
            fixture.View.PaymentOptions.SelectRecipient("R1", 2);
            fixture.View.PaymentOptions.Confirm();

            var command = fixture.Commands.LastCommand;
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.ResolvePendingChoice));
            Assert.That(command.SourceId, Is.EqualTo("building_031"));
            Assert.That(command.OptionIds, Is.EqualTo(new[] { FacilityPendingChoiceTypes.ExploreOption }));
            Assert.That(command.Parameters[ResolveFacilityEffectCommandHandler.PendingSessionIdParameter],
                Is.EqualTo("warehouse-session"));
            Assert.That(command.Parameters[ResolveFacilityEffectCommandHandler.OptionIdParameter],
                Is.EqualTo(FacilityPendingChoiceTypes.ExploreOption));
            Assert.That(command.Parameters[ResolveFacilityEffectCommandHandler.TargetLocationIdParameter],
                Is.EqualTo("B"));
            Assert.That(command.Parameters[ExploreLocationCommandHandler.PathLocationIdsParameter],
                Is.EqualTo("A,B"));
            Assert.That(command.Parameters[ExploreLocationCommandHandler.RouteIdsParameter],
                Is.EqualTo("R1"));
            Assert.That(command.Parameters[ExploreLocationCommandHandler.PaymentRecipientsParameter],
                Is.EqualTo("R1=2"));
        }

        [Test]
        public void DifferentOpponentTollsAcrossBestPathsShowPathChoice()
        {
            var fixture = CreateDiamondFixture();

            fixture.Presenter.SelectTarget("D");

            Assert.That(fixture.View.PathOptions, Is.Not.Null);
            Assert.That(fixture.View.PathOptions.Choices.Count, Is.EqualTo(2));
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsChoosingPath, Is.True);
            fixture.View.PathOptions.SelectPath(1);
            Assert.That(fixture.View.PaymentOptions, Is.Not.Null);
            Assert.That(fixture.Presenter.IsChoosingPath, Is.False);
            Assert.That(fixture.Presenter.IsChoosingPaymentRecipient, Is.True);
        }

        [Test]
        public void PendingExploreAndMoveCityChoicesBuildResolveCommands()
        {
            var fixture = CreateFixture(false);
            SetPending(fixture.Context.State, ExploreLocationCommandHandler.ExploreEventChoiceType, "event_green_01");
            fixture.Presenter.ShowPendingChoice();
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsChoosingEventOption, Is.True);
            fixture.View.EventOptions.SelectChoice(0);
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.ResolvePendingChoice));
            Assert.That(fixture.Commands.LastCommand.OptionIds, Is.EqualTo(new[] { "0" }));

            fixture.Commands.LastCommand = null;
            SetPending(fixture.Context.State, MoveCityCommandHandler.MoveCityEventChoiceType, "event_green_01");
            fixture.Context.State.PendingSpecialAction = new PendingSpecialActionState
            {
                SessionId = "special-session",
                PlayerId = 1,
                SpecialActionId = SpecialActionDatabase.EfficientMobileManagementSystem,
                DeclarationMarkerId = "marker",
                Step = SpecialActionPendingSteps.AwaitMoveEvent,
                RemainingRepetitions = 1
            };
            fixture.Presenter.ShowPendingChoice();
            fixture.View.EventOptions.SelectChoice(1);
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.ResolvePendingChoice));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo("B"));
            Assert.That(
                fixture.Commands.LastCommand.Parameters[UseSpecialActionCommandHandler.SessionIdParameter],
                Is.EqualTo("special-session"));
        }

        [Test]
        public void OneInfluenceTargetIsValidatedAndEncoded()
        {
            var fixture = CreateFixture(false);
            SetPending(fixture.Context.State, ExploreLocationCommandHandler.ExploreEventChoiceType, "event_red_01");
            fixture.Presenter.ShowPendingChoice();
            Assert.That(fixture.Presenter.IsChoosingEventOption, Is.True);

            fixture.View.EventOptions.SelectChoice(2);
            Assert.That(fixture.Presenter.IsChoosingEventOption, Is.False);
            Assert.That(fixture.Presenter.IsSelectingInfluenceTarget, Is.True);
            var slotId = fixture.View.FirstHighlightedSlot();
            Assert.That(slotId, Is.Not.Empty);
            fixture.Presenter.SelectInfluenceSlot(slotId);

            Assert.That(
                fixture.Commands.LastCommand.Parameters[ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter],
                Is.EqualTo(slotId));
        }

        [Test]
        public void MultipleInfluenceTargetsRejectDuplicateAndEncodeAllSlots()
        {
            var fixture = CreateFixture(false);
            var card = new EventCardDefinition
            {
                CardId = "test_place_four_influence",
                Name = "测试放置四个影响力",
                ChoiceDescriptions = { "放置四个影响力" },
                ChoiceRewards = { new ResourceSet() },
                ChoicePendingEffects =
                {
                    new List<EventEffect>
                    {
                        EventEffect.PlaceInfluence(EventEffectTargetScope.None, 4)
                    }
                }
            };
            var path = new MapPath
            {
                LocationIds = { "A", "B" },
                RouteIds = { "R1" }
            };
            fixture.Presenter.BeginWithPathAndCard("B", path, card);
            Assert.That(fixture.Presenter.IsChoosingEventOption, Is.True);
            fixture.Presenter.SelectEventChoice(0);
            Assert.That(fixture.Presenter.IsSelectingInfluenceTarget, Is.True);

            var first = fixture.View.FirstHighlightedSlot();
            Assert.That(first, Is.Not.Empty);
            fixture.Presenter.SelectInfluenceSlot(first);
            fixture.Presenter.SelectInfluenceSlot(first);
            Assert.That(fixture.Presenter.SelectedInfluenceSlotIds.Count, Is.EqualTo(1));
            Assert.That(fixture.View.Prompt, Does.Contain("\u91cd\u590d"));

            var selectedSlotIds = new List<string> { first };
            for (var selectionIndex = 1; selectionIndex < 4; selectionIndex++)
            {
                var next = string.Empty;
                for (var highlightIndex = 0; highlightIndex < fixture.View.Highlights.Count; highlightIndex++)
                {
                    var highlight = fixture.View.Highlights[highlightIndex];
                    if (highlight.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot &&
                        !selectedSlotIds.Contains(highlight.TargetId))
                    {
                        next = highlight.TargetId;
                        break;
                    }
                }

                Assert.That(next, Is.Not.Empty, "第 " + (selectionIndex + 1) + " 个影响力槽位应可选。");
                selectedSlotIds.Add(next);
                fixture.Presenter.SelectInfluenceSlot(next);
            }

            var encoded = fixture.Commands.LastCommand.Parameters[
                ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter];
            var encodedSlotIds = encoded.Split(',');
            Assert.That(encodedSlotIds.Length, Is.EqualTo(4));
            Assert.That(new HashSet<string>(encodedSlotIds).Count, Is.EqualTo(4));
            Assert.That(encodedSlotIds, Is.EquivalentTo(selectedSlotIds));
        }

        [Test]
        public void InvalidSlotAndSameFrameInputDoNotAdvanceSelection()
        {
            var fixture = CreateFixture(false);
            SetPending(fixture.Context.State, ExploreLocationCommandHandler.ExploreEventChoiceType, "event_yellow_04");
            fixture.Presenter.ShowPendingChoice();
            fixture.View.EventOptions.SelectChoice(0);
            fixture.Presenter.SelectInfluenceSlot("unknown", 10);
            Assert.That(fixture.Presenter.SelectedInfluenceSlotIds, Is.Empty);

            var first = fixture.View.FirstHighlightedSlot();
            fixture.Presenter.SelectInfluenceSlot(first, 11);
            var second = fixture.View.FirstHighlightedSlot();
            fixture.Presenter.SelectInfluenceSlot(second, 11);
            Assert.That(fixture.Presenter.SelectedInfluenceSlotIds.Count, Is.EqualTo(1));
        }

        [Test]
        public void RemoteWaitingAndFailureKeepWorkflowState()
        {
            var fixture = CreateFixture(false);
            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.Activate();
            Assert.That(fixture.Presenter.IsSelectingExploreTarget, Is.True);
            fixture.Presenter.SelectTarget("B");
            Assert.That(fixture.Presenter.SelectedPath, Is.Not.Null);
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsSelectingExploreTarget, Is.True);
            Assert.That(fixture.View.Prompt, Does.Contain("\u7b49\u5f85\u786e\u8ba4"));

            fixture.Commands.NextResult = new WorkflowSubmissionResult(
                CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "rejected")),
                false);
            fixture.Presenter.ConfirmExploreStart();
            Assert.That(fixture.Presenter.SelectedPath, Is.Not.Null);
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsSelectingExploreTarget, Is.True);
            Assert.That(fixture.View.Prompt, Is.EqualTo("rejected"));
        }

        [Test]
        public void AppliedLocallyClearsWorkflowStage()
        {
            var fixture = CreateFixture(false);
            fixture.Commands.NextResult = Success(true);
            fixture.Presenter.Activate();

            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Presenter.IsSelectingExploreTarget, Is.True);

            fixture.Presenter.SelectTarget("B");

            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Presenter.IsSelectingExploreTarget, Is.False);
            Assert.That(fixture.Presenter.CurrentPrompt, Is.Empty);
        }

        [Test]
        public void CancelClearsEveryTemporarySelectionAndView()
        {
            var fixture = CreateFixture(true);
            fixture.Presenter.SelectTarget("B");

            fixture.Presenter.Cancel();

            Assert.That(fixture.Presenter.TargetLocationId, Is.Empty);
            Assert.That(fixture.Presenter.SelectedPath, Is.Null);
            Assert.That(fixture.Presenter.PaymentRecipients, Is.Empty);
            Assert.That(fixture.Presenter.SelectedInfluenceSlotIds, Is.Empty);
            Assert.That(fixture.Presenter.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Presenter.IsSelectingExploreTarget, Is.False);
            Assert.That(fixture.Presenter.IsChoosingPath, Is.False);
            Assert.That(fixture.Presenter.IsChoosingPaymentRecipient, Is.False);
            Assert.That(fixture.Presenter.IsChoosingEventOption, Is.False);
            Assert.That(fixture.Presenter.IsSelectingInfluenceTarget, Is.False);
            Assert.That(fixture.Presenter.CurrentPrompt, Is.Empty);
            Assert.That(fixture.View.Highlights, Is.Empty);
            Assert.That(fixture.View.HideCount, Is.GreaterThan(0));
        }

        private static Fixture CreateFixture(bool opponentToll)
        {
            var map = new GameMapDefinition
            {
                MapId = "exploration-presenter",
                Locations =
                {
                    new MapLocationDefinition { LocationId = "A", CanDockCity = true, InfluenceSlotCount = 5 },
                    new MapLocationDefinition { LocationId = "B", InfluenceSlotCount = 5 },
                    new MapLocationDefinition { LocationId = "C", InfluenceSlotCount = 5 }
                },
                Routes =
                {
                    new MapRouteDefinition
                    {
                        RouteId = "R1", FromLocationId = "A", ToLocationId = "B", InfluenceSlotCount = 2
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "R2", FromLocationId = "B", ToLocationId = "C", InfluenceSlotCount = 2
                    }
                }
            };
            var state = CreateState();
            if (opponentToll)
            {
                state.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = 2,
                    SlotId = InfluenceService.GetRouteSlotId("R1", 0),
                    RouteId = "R1"
                });
            }

            return BuildFixture(map, state);
        }

        private static Fixture CreateDiamondFixture()
        {
            var map = new GameMapDefinition
            {
                MapId = "exploration-diamond",
                Locations =
                {
                    new MapLocationDefinition { LocationId = "A", InfluenceSlotCount = 5 },
                    new MapLocationDefinition { LocationId = "B", InfluenceSlotCount = 5 },
                    new MapLocationDefinition { LocationId = "C", InfluenceSlotCount = 5 },
                    new MapLocationDefinition { LocationId = "D", InfluenceSlotCount = 5 }
                },
                Routes =
                {
                    new MapRouteDefinition { RouteId = "AB", FromLocationId = "A", ToLocationId = "B", InfluenceSlotCount = 2 },
                    new MapRouteDefinition { RouteId = "BD", FromLocationId = "B", ToLocationId = "D", InfluenceSlotCount = 2 },
                    new MapRouteDefinition { RouteId = "AC", FromLocationId = "A", ToLocationId = "C", InfluenceSlotCount = 2 },
                    new MapRouteDefinition { RouteId = "CD", FromLocationId = "C", ToLocationId = "D", InfluenceSlotCount = 2 }
                }
            };
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2, SlotId = InfluenceService.GetRouteSlotId("AB", 0), RouteId = "AB"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 3, SlotId = InfluenceService.GetRouteSlotId("AC", 0), RouteId = "AC"
            });
            return BuildFixture(map, state);
        }

        private static Fixture BuildFixture(GameMapDefinition map, GameState state)
        {
            var query = new MapQueryService(map);
            var context = new FakeContext { State = state };
            var commands = new FakeCommandPort { NextResult = Success(false) };
            var view = new FakeView();
            return new Fixture
            {
                Context = context,
                Commands = commands,
                View = view,
                Presenter = new ExplorationEventPresenter(
                    context,
                    commands,
                    view,
                    query,
                    new ExplorationService(query),
                    new InfluenceService(query))
            };
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        CityLocationId = "A",
                        InfluenceSupply = 10,
                        Resources = { GoldVoucher = 30 }
                    },
                    new PlayerState { PlayerId = 2 },
                    new PlayerState { PlayerId = 3 }
                },
                Decks =
                {
                    EventDeckGreen = { "event_green_01" },
                    EventDeckYellow = { "event_yellow_04" },
                    EventDeckRed = { "event_red_01" }
                },
                Map = { OpenLocationIds = { "A" } }
            };
        }

        private static void SetPending(GameState state, string choiceType, string cardId)
        {
            state.PendingCardSession = null;
            state.PendingChoice = new PendingChoiceState
            {
                ChoiceId = "choice",
                PlayerId = 1,
                ChoiceType = choiceType,
                CardId = cardId,
                TargetId = "B",
                OptionIds = { "0", "1", "2" }
            };
        }

        private static WorkflowSubmissionResult Success(bool appliedLocally)
        {
            return new WorkflowSubmissionResult(
                CommandResult.SuccessResult(new List<GameEvent>(), "ok"),
                appliedLocally);
        }

        private sealed class Fixture
        {
            public FakeContext Context;
            public FakeCommandPort Commands;
            public FakeView View;
            public ExplorationEventPresenter Presenter;
        }

        private sealed class FakeContext : IGameplayContext
        {
            public GameState State;
            public GameState CurrentState { get { return State; } }
            public int LocalPlayerId { get { return 1; } }
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

        private sealed class FakeView : IExplorationEventView
        {
            public readonly List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();
            public string Prompt = string.Empty;
            public ExplorePathOptionsViewModel PathOptions;
            public ExplorePaymentOptionsViewModel PaymentOptions;
            public EventCardOptionsViewModel EventOptions;
            public int HideCount;
            public InteractionMode Mode;

            public void SetInteractionMode(InteractionMode mode) { Mode = mode; }
            public void ShowPrompt(string message) { Prompt = message; }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights)
            {
                Highlights.Clear();
                if (highlights != null) Highlights.AddRange(highlights);
            }
            public void ClearHighlights() { Highlights.Clear(); }
            public void ShowExplorePathOptions(ExplorePathOptionsViewModel viewModel) { PathOptions = viewModel; }
            public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel viewModel) { PaymentOptions = viewModel; }
            public void ShowEventCardOptions(EventCardOptionsViewModel viewModel) { EventOptions = viewModel; }
            public void CollapseEventOptions() { }
            public void HideEventOptions() { HideCount += 1; EventOptions = null; PathOptions = null; PaymentOptions = null; }
            public string GetPlayerDisplayName(int playerId) { return "\u73a9\u5bb6" + playerId; }
            public void RefreshActionPanel() { }
            public void RefreshResourceAndInfluence() { }
            public void RefreshFromState() { }
            public void CompleteAction(string actionName) { }

            public string FirstHighlightedSlot()
            {
                for (var i = 0; i < Highlights.Count; i++)
                {
                    if (Highlights[i].TargetKind == WorkflowHighlightTargetKind.InfluenceSlot)
                    {
                        return Highlights[i].TargetId;
                    }
                }

                return string.Empty;
            }
        }
    }
}
