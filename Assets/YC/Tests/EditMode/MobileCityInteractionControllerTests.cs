using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Domain.Commands;
using YC.Domain.Exploration;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class MobileCityInteractionControllerTests
    {
        private GameObject owner;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
                owner = null;
            }
        }

        [Test]
        public void BeginNextRound_DuringCleanup_SubmitsEndActionCommand()
        {
            var state = CreateState(GamePhase.Cleanup);
            var originalState = JsonUtility.ToJson(state);
            var commands = new RecordingCommandPort();
            var controller = CreateController(state, commands);

            InvokeBeginNextRound(controller);

            Assert.That(commands.ReceivedCommand, Is.Not.Null);
            Assert.That(commands.ReceivedCommand.Kind, Is.EqualTo(GameCommandKind.EndAction));
            Assert.That(commands.ReceivedCommand.PlayerId, Is.EqualTo(1));
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(originalState));
        }

        [Test]
        public void BeginNextRound_DuringWrongPhase_DoesNotSubmitOrMutateState()
        {
            var state = CreateState(GamePhase.RoundStart);
            var originalState = JsonUtility.ToJson(state);
            var commands = new RecordingCommandPort();
            var controller = CreateController(state, commands);

            InvokeBeginNextRound(controller);

            Assert.That(commands.ReceivedCommand, Is.Null);
            Assert.That(JsonUtility.ToJson(state), Is.EqualTo(originalState));
        }

        private Component CreateController(GameState state, RecordingCommandPort commands)
        {
            var controllerType = Type.GetType(
                "YC.Presentation.MobileCityInteractionController, Assembly-CSharp",
                false);
            Assert.That(controllerType, Is.Not.Null, "Missing MobileCityInteractionController.");

            owner = new GameObject("Mobile City Interaction Controller Test");
            owner.SetActive(false);
            var controller = owner.AddComponent(controllerType);

            SetPrivateField(controller, "turnActionPresenter", CreateTurnActionPresenter(state, commands));
            return controller;
        }

        private static TurnActionPresenter CreateTurnActionPresenter(
            GameState state,
            RecordingCommandPort commands)
        {
            var context = new FakeContext { State = state };
            var view = new FakeView();
            var mapQuery = new MapQueryService(new GameMapDefinition
            {
                MapId = "controller-forwarding-test",
                Locations =
                {
                    new MapLocationDefinition
                    {
                        LocationId = "A",
                        CanDockCity = true,
                        InfluenceSlotCount = 1
                    }
                }
            });
            var influenceService = new InfluenceService(mapQuery);
            var resourceCollection = new ResourceCollectionPresenter(
                context,
                commands,
                view,
                mapQuery,
                new ResourceCollectionService(mapQuery));
            var influence = new InfluenceActionPresenter(
                context,
                commands,
                view,
                mapQuery,
                influenceService);
            var exploration = new ExplorationEventPresenter(
                context,
                commands,
                view,
                mapQuery,
                new ExplorationService(mapQuery),
                influenceService);
            var coordinator = new InteractionFlowCoordinator();
            coordinator.ResetToChooseAction();
            return new TurnActionPresenter(
                context,
                commands,
                view,
                coordinator,
                mapQuery,
                resourceCollection,
                influence,
                exploration);
        }

        private static void InvokeBeginNextRound(Component controller)
        {
            var method = controller.GetType().GetMethod(
                "BeginNextRound",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing BeginNextRound.");
            method.Invoke(controller, null);
        }

        private static void SetPrivateField(Component controller, string fieldName, object value)
        {
            var field = controller.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing MobileCityInteractionController." + fieldName + ".");
            field.SetValue(controller, value);
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                Round = 3,
                MaxRounds = 8,
                ActionRound = 0,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        ActedMainActionThisTurn = true,
                        HasMovedCityThisRound = true,
                        HasCollectedResourcesThisRound = true
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue,
                        ActedMainActionThisTurn = true,
                        HasMovedCityThisRound = true,
                        HasCollectedResourcesThisRound = true
                    }
                }
            };
        }

        private sealed class FakeContext : IWritableGameplayContext
        {
            public GameState State;

            public GameState CurrentState
            {
                get { return State; }
            }

            public int LocalPlayerId { get; private set; } = 1;

            public bool ControlsCurrentPlayerLocally
            {
                get { return true; }
            }

            public void SetLocalPlayerId(int playerId) { LocalPlayerId = playerId; }
        }

        private sealed class RecordingCommandPort : IGameCommandPort
        {
            public GameCommand ReceivedCommand { get; private set; }

            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                ReceivedCommand = command;
                return new WorkflowSubmissionResult(
                    CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.InvalidTarget,
                        "Test command port rejected the command.")),
                    false);
            }
        }

        private sealed class FakeView : ITurnActionView, IResourceCollectionView,
            IInfluenceActionView, IExplorationEventView
        {
            public void ShowPrompt(string message) { }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights) { }
            public void ClearHighlights() { }
            public void RefreshFromState() { }
            public void RefreshInformation() { }
            public void RefreshActionPanel() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string actionName) { }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel viewModel) { }
            public void HideBuildFacilityDraft() { }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel viewModel) { }
            public void ShowRoutePaymentOptions(
                string routeId,
                int cost,
                IReadOnlyList<int> recipientPlayerIds) { }
            public string GetPlayerDisplayName(int playerId) { return playerId.ToString(); }
            public void RefreshSelectionView() { }
            public void SetInteractionMode(InteractionMode mode) { }
            public void ShowDispatchDecision(DispatchDecisionViewModel viewModel) { }
            public void HideDispatchDecision() { }
            public void RefreshInfluencePreview(
                bool hasPendingFirstMove,
                string firstSourceSlotId,
                string firstTargetSlotId) { }
            public void CompleteAction(string actionName) { }
            public void ShowExplorePathOptions(ExplorePathOptionsViewModel viewModel) { }
            public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel viewModel) { }
            public void ShowEventCardOptions(EventCardOptionsViewModel viewModel) { }
            public void CollapseEventOptions() { }
            public void HideEventOptions() { }
            public void RefreshResourceAndInfluence() { }
        }
    }
}
