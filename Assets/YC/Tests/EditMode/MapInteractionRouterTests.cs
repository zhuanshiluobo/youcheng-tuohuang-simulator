using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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
    public sealed class MapInteractionRouterTests
    {
        [Test]
        public void OnLocationClicked_ActiveTurnMoveStage_CreatesMoveConfirmation()
        {
            var fixture = CreateFixture(GamePhase.ActionRound1);
            fixture.Coordinator.Activate(fixture.Turn);
            fixture.HighlightLocation("B");

            fixture.ClickLocation("B");

            Assert.That(fixture.Coordinator.ActiveWorkflow, Is.SameAs(fixture.Turn));
            Assert.That(fixture.Turn.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Turn.IsSelectingMoveTarget, Is.True);
            Assert.That(fixture.HasPendingConfirmation, Is.True);
            Assert.That(fixture.Influence.HasPendingConfirmation, Is.False);
        }

        [Test]
        public void OnLocationClicked_ActiveExplorationTargetStage_CreatesExploreConfirmation()
        {
            var fixture = CreateFixture(GamePhase.ActionRound1);
            fixture.Coordinator.Activate(fixture.Exploration);
            fixture.HighlightLocation("B");

            fixture.ClickLocation("B");

            Assert.That(fixture.Coordinator.ActiveWorkflow, Is.SameAs(fixture.Exploration));
            Assert.That(fixture.Exploration.IsSelectingExploreTarget, Is.True);
            Assert.That(fixture.HasPendingConfirmation, Is.True);
            Assert.That(fixture.Influence.HasPendingConfirmation, Is.False);
        }

        [Test]
        public void OnInfluenceSlotClicked_ActiveInfluenceDeployStage_RoutesToDeploySelection()
        {
            var fixture = CreateFixture(GamePhase.ActionRound1);
            var deployTarget = InfluenceService.GetLocationSlotId("D", 1);
            fixture.Coordinator.Activate(fixture.Influence);
            fixture.HighlightInfluenceSlot(deployTarget);

            fixture.ClickInfluenceSlot(deployTarget);

            Assert.That(fixture.Coordinator.ActiveWorkflow, Is.SameAs(fixture.Influence));
            Assert.That(fixture.Influence.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Influence.IsSelectingDeployTarget, Is.True);
            Assert.That(fixture.Influence.HasPendingConfirmation, Is.True);
        }

        [Test]
        public void OnInfluenceSlotClicked_ActiveInfluenceDispatchSourceStage_RoutesToDispatchSelection()
        {
            var fixture = CreateFixture(GamePhase.ActionRound1);
            var sourceSlot = InfluenceService.GetLocationSlotId("A", 0);
            fixture.Coordinator.Activate(fixture.Influence);
            fixture.Influence.BeginDispatch();
            fixture.HighlightInfluenceSlot(sourceSlot);

            fixture.ClickInfluenceSlot(sourceSlot);

            Assert.That(fixture.Coordinator.ActiveWorkflow, Is.SameAs(fixture.Influence));
            Assert.That(fixture.Influence.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Influence.IsSelectingDispatchTarget, Is.True);
            Assert.That(fixture.Influence.DispatchSourceSlotId, Is.EqualTo(sourceSlot));
        }

        [Test]
        public void OnLocationClicked_ActiveResourceCollection_RoutesToLocationSelection()
        {
            var fixture = CreateFixture(GamePhase.ResourceCollection);
            fixture.Coordinator.Activate(fixture.Collection);
            Assert.That(fixture.Collection.SelectedLocationIds, Does.Contain("B"));
            fixture.HighlightLocation("B");

            fixture.ClickLocation("B");

            Assert.That(fixture.Coordinator.ActiveWorkflow, Is.SameAs(fixture.Collection));
            Assert.That(fixture.Collection.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.Collection.SelectedLocationIds, Does.Not.Contain("B"));
        }

        [Test]
        public void OnInfluenceSlotClicked_ActiveResourceCollection_RoutesToTollPayment()
        {
            var fixture = CreateFixture(GamePhase.ResourceCollection);
            var tollSlot = InfluenceService.GetRouteSlotId("R2", 0);
            fixture.Coordinator.Activate(fixture.Collection);
            fixture.HighlightInfluenceSlot(tollSlot);

            fixture.ClickInfluenceSlot(tollSlot);

            Assert.That(fixture.Coordinator.ActiveWorkflow, Is.SameAs(fixture.Collection));
            Assert.That(fixture.Collection.Mode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(fixture.View.PaymentRouteId, Is.EqualTo("R2"));
        }

        [Test]
        public void Clicks_WhenActiveWorkflowIsNullAndRestingModeIsBusy_DoNotRouteToBusyPresenters()
        {
            var fixture = CreateFixture(GamePhase.ResourceCollection);
            fixture.Coordinator.SetMode(InteractionMode.Busy);

            // Keep every presenter's local stage active without registering it as ActiveWorkflow.
            fixture.Turn.Activate();
            fixture.Exploration.BeginTargetSelection();
            fixture.Influence.BeginDeploy();
            fixture.Collection.Begin();

            var collectionInitiallyContainsB = Contains(fixture.Collection.SelectedLocationIds, "B");
            var deployTarget = InfluenceService.GetLocationSlotId("D", 1);
            fixture.HighlightLocation("B");
            fixture.HighlightInfluenceSlot(deployTarget);

            fixture.ClickLocation("B");
            fixture.ClickInfluenceSlot(deployTarget);

            Assert.That(fixture.Coordinator.ActiveWorkflow, Is.Null);
            Assert.That(fixture.Coordinator.CurrentMode, Is.EqualTo(InteractionMode.Busy));
            Assert.That(
                Contains(fixture.Collection.SelectedLocationIds, "B"),
                Is.EqualTo(collectionInitiallyContainsB));
            Assert.That(fixture.Influence.HasPendingConfirmation, Is.False);
            Assert.That(fixture.View.PaymentRouteId, Is.Empty);
            Assert.That(fixture.HasPendingConfirmation, Is.False);
        }

        private static Fixture CreateFixture(GamePhase phase)
        {
            var map = CreateMap();
            var state = CreateState(phase);
            var context = new FakeContext { State = state };
            var view = new FakeView();
            var commandPort = new RecordingCommandPort();
            var mapQuery = new MapQueryService(map);
            var influenceService = new InfluenceService(mapQuery);
            var collection = new ResourceCollectionPresenter(
                context,
                commandPort,
                view,
                mapQuery,
                new ResourceCollectionService(mapQuery));
            var influence = new InfluenceActionPresenter(
                context,
                commandPort,
                view,
                mapQuery,
                influenceService);
            var exploration = new ExplorationEventPresenter(
                context,
                commandPort,
                view,
                mapQuery,
                new ExplorationService(mapQuery),
                influenceService);
            var coordinator = new InteractionFlowCoordinator();
            var turn = new TurnActionPresenter(
                context,
                commandPort,
                view,
                coordinator,
                mapQuery,
                collection,
                influence,
                exploration);
            var mapView = CreateMapView(mapQuery, influenceService);
            var router = CreateRouter(
                mapView,
                mapQuery,
                coordinator,
                turn,
                collection,
                influence,
                exploration,
                view,
                context);

            return new Fixture
            {
                Context = context,
                View = view,
                Coordinator = coordinator,
                Turn = turn,
                Collection = collection,
                Influence = influence,
                Exploration = exploration,
                MapView = mapView,
                Router = router
            };
        }

        private static object CreateMapView(
            MapQueryService mapQuery,
            InfluenceService influenceService)
        {
            var mapViewType = GetAssemblyCSharpType("YC.Presentation.MapViewPresenter");
            var constructor = FindConstructor(mapViewType, 5);
            return constructor.Invoke(new object[]
            {
                null,
                null,
                null,
                mapQuery,
                influenceService
            });
        }

        private static object CreateRouter(
            object mapView,
            IMapQueryService mapQuery,
            InteractionFlowCoordinator coordinator,
            TurnActionPresenter turn,
            ResourceCollectionPresenter collection,
            InfluenceActionPresenter influence,
            ExplorationEventPresenter exploration,
            IInteractionView view,
            FakeContext context)
        {
            var routerType = GetAssemblyCSharpType("YC.Presentation.MapInteractionRouter");
            var mapViewType = mapView.GetType();
            var getMapViewType = typeof(Func<>).MakeGenericType(mapViewType);
            var getMapView = Expression.Lambda(
                getMapViewType,
                Expression.Constant(mapView, mapViewType),
                new ParameterExpression[0]).Compile();
            var constructor = FindConstructor(routerType, 14);

            return constructor.Invoke(new object[]
            {
                getMapView,
                (Func<Camera>)(() => null),
                mapQuery,
                coordinator,
                turn,
                collection,
                influence,
                exploration,
                view,
                (Func<int>)(() => context.LocalPlayerId),
                (Func<bool>)(() => false),
                (Action)(() => { }),
                (Action)(() => { }),
                (Action)(() => { })
            });
        }

        private static ConstructorInfo FindConstructor(Type type, int parameterCount)
        {
            var constructors = type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < constructors.Length; i++)
            {
                if (constructors[i].GetParameters().Length == parameterCount)
                {
                    return constructors[i];
                }
            }

            Assert.Fail(
                "Expected " + type.FullName + " constructor with " + parameterCount + " parameters.");
            return null;
        }

        private static Type GetAssemblyCSharpType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing runtime type " + fullName + ".");
            return type;
        }

        private static bool Contains(IEnumerable<string> values, string expected)
        {
            foreach (var value in values)
            {
                if (value == expected)
                {
                    return true;
                }
            }

            return false;
        }

        private static GameMapDefinition CreateMap()
        {
            return new GameMapDefinition
            {
                MapId = "map-interaction-router-tests",
                Locations =
                {
                    new MapLocationDefinition
                    {
                        LocationId = "A",
                        CanDockCity = true,
                        InfluenceSlotCount = 2
                    },
                    new MapLocationDefinition { LocationId = "B", InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "C", InfluenceSlotCount = 2 },
                    new MapLocationDefinition { LocationId = "D", InfluenceSlotCount = 2 }
                },
                Routes =
                {
                    new MapRouteDefinition
                    {
                        RouteId = "R1",
                        FromLocationId = "A",
                        ToLocationId = "B",
                        InfluenceSlotCount = 1
                    },
                    new MapRouteDefinition
                    {
                        RouteId = "R2",
                        FromLocationId = "B",
                        ToLocationId = "C",
                        InfluenceSlotCount = 1
                    }
                }
            };
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "A",
                        InfluenceSupply = 5,
                        Resources =
                        {
                            GoldVoucher = 5,
                            OriginiumShard = 5
                        }
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue,
                        InfluenceSupply = 5
                    }
                },
                Map =
                {
                    OpenLocationIds = { "A" },
                    ResourceTokens =
                    {
                        new ResourceTokenState
                        {
                            LocationId = "A",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        },
                        new ResourceTokenState
                        {
                            LocationId = "B",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        },
                        new ResourceTokenState
                        {
                            LocationId = "C",
                            ResourceType = ResourceType.Originium,
                            Amount = 1
                        },
                        new ResourceTokenState
                        {
                            LocationId = "D",
                            ResourceType = ResourceType.PureOriginium,
                            Amount = 1
                        }
                    },
                    Influences =
                    {
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetLocationSlotId("A", 0),
                            LocationId = "A"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetLocationSlotId("B", 0),
                            LocationId = "B"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetLocationSlotId("C", 0),
                            LocationId = "C"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetRouteSlotId("R1", 0),
                            RouteId = "R1"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 2,
                            SlotId = InfluenceService.GetRouteSlotId("R2", 0),
                            RouteId = "R2"
                        }
                    }
                }
            };
        }

        private sealed class Fixture
        {
            public FakeContext Context;
            public FakeView View;
            public InteractionFlowCoordinator Coordinator;
            public TurnActionPresenter Turn;
            public ResourceCollectionPresenter Collection;
            public InfluenceActionPresenter Influence;
            public ExplorationEventPresenter Exploration;
            public object MapView;
            public object Router;

            public bool HasPendingConfirmation
            {
                get
                {
                    var property = Router.GetType().GetProperty(
                        "HasPendingConfirmation",
                        BindingFlags.Instance | BindingFlags.Public);
                    Assert.That(property, Is.Not.Null);
                    return (bool)property.GetValue(Router, null);
                }
            }

            public void HighlightLocation(string locationId)
            {
                Invoke(MapView, "SetHighlighted", locationId, Color.yellow);
            }

            public void HighlightInfluenceSlot(string slotId)
            {
                Invoke(MapView, "HighlightInfluenceSlot", slotId);
            }

            public void ClickLocation(string locationId)
            {
                Invoke(Router, "OnLocationClicked", locationId);
            }

            public void ClickInfluenceSlot(string slotId)
            {
                Invoke(Router, "OnInfluenceSlotClicked", slotId);
            }

            private static void Invoke(object target, string methodName, params object[] arguments)
            {
                var method = target.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.That(method, Is.Not.Null, "Missing " + target.GetType().FullName + "." + methodName + ".");
                method.Invoke(target, arguments);
            }
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

            public void SetLocalPlayerId(int playerId)
            {
                LocalPlayerId = playerId;
            }
        }

        private sealed class RecordingCommandPort : IGameCommandPort
        {
            public GameCommand LastCommand;

            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                LastCommand = command;
                return new WorkflowSubmissionResult(
                    CommandResult.Invalid(
                        ValidationResult.Failure(
                            CommandErrorCode.InvalidTarget,
                            "MapInteractionRouterTests does not apply commands.")),
                    false);
            }
        }

        private sealed class FakeView : ITurnActionView, IResourceCollectionView,
            IInfluenceActionView, IExplorationEventView
        {
            public string Prompt = string.Empty;
            public string PaymentRouteId = string.Empty;

            public void ShowPrompt(string message) { Prompt = message ?? string.Empty; }
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
                IReadOnlyList<int> recipientPlayerIds)
            {
                PaymentRouteId = routeId ?? string.Empty;
            }

            public string GetPlayerDisplayName(int playerId)
            {
                return "Player " + playerId;
            }

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
