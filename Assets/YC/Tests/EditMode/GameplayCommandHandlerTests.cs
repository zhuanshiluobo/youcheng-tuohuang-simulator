using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class GameplayCommandHandlerTests
    {
        [Test]
        public void DeployInfluence_SucceedsAndMarksMainActionComplete()
        {
            var state = CreateActionState();
            AddResourceToken(state, "city-a");
            var handler = new DeployInfluenceCommandHandler(CreateInfluenceService());

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.DeployInfluence,
                PlayerId = 1,
                TargetId = InfluenceService.GetLocationSlotId("city-a", 0)
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void DeployInfluence_WhenNotCurrentPlayer_FailsWithoutMutating()
        {
            var state = CreateActionState();
            AddResourceToken(state, "city-a");
            var handler = new DeployInfluenceCommandHandler(CreateInfluenceService());

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.DeployInfluence,
                PlayerId = 2,
                TargetId = InfluenceService.GetLocationSlotId("city-a", 0)
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NotCurrentPlayer));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void DeployInfluence_OnRouteSlot_SucceedsAndMarksMainActionComplete()
        {
            var state = CreateActionState();
            var handler = new DeployInfluenceCommandHandler(CreateInfluenceService());

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.DeployInfluence,
                PlayerId = 1,
                TargetId = InfluenceService.GetRouteSlotId("route-a-b", 0)
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(InfluenceService.GetRouteSlotId("route-a-b", 0)));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void DispatchInfluence_MovesOneInfluenceAndMarksMainActionComplete()
        {
            var state = CreateActionState();
            AddResourceToken(state, "city-a");
            AddResourceToken(state, "harbor-c");
            var influenceService = CreateInfluenceService();
            var source = InfluenceService.GetLocationSlotId("city-a", 0);
            var target = InfluenceService.GetLocationSlotId("harbor-c", 0);
            influenceService.Place(state, 1, source);
            var handler = new DispatchInfluenceCommandHandler(influenceService);

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.DispatchInfluence,
                PlayerId = 1,
                SourceId = source,
                TargetId = target
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(target));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void DispatchInfluence_MovesTwoInfluencesAndMarksMainActionComplete()
        {
            var state = CreateActionState();
            AddResourceToken(state, "C-01");
            AddResourceToken(state, "D-01");
            var influenceService = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            var firstSource = InfluenceService.GetLocationSlotId("A-01", 0);
            var firstTarget = InfluenceService.GetLocationSlotId("C-01", 0);
            var secondSource = InfluenceService.GetLocationSlotId("B-01", 0);
            var secondTarget = InfluenceService.GetLocationSlotId("D-01", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = firstSource,
                LocationId = "A-01"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = secondSource,
                LocationId = "B-01"
            });
            var handler = new DispatchInfluenceCommandHandler(influenceService);

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.DispatchInfluence,
                PlayerId = 1,
                SourceId = firstSource,
                TargetId = firstTarget,
                Parameters =
                {
                    { "source2", secondSource },
                    { "target2", secondTarget }
                }
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences.Exists(influence => influence.SlotId == firstTarget), Is.True);
            Assert.That(state.Map.Influences.Exists(influence => influence.SlotId == secondTarget), Is.True);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void DispatchInfluence_WhenParametersAreNull_FailsWithoutMutatingState()
        {
            var scenario = CreateDispatchScenario();
            var stateBefore = JsonUtility.ToJson(scenario.State);

            var result = new DispatchInfluenceCommandHandler(scenario.Service).Handle(
                scenario.State,
                new GameCommand
                {
                    Kind = GameCommandKind.DispatchInfluence,
                    PlayerId = 1,
                    SourceId = scenario.FirstSource,
                    TargetId = scenario.FirstTarget,
                    Parameters = null
                });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(JsonUtility.ToJson(scenario.State), Is.EqualTo(stateBefore));
        }

        [Test]
        public void DispatchInfluence_WhenSecondMoveParametersAreIncomplete_FailsWithoutMutatingState()
        {
            var scenario = CreateDispatchScenario();
            var stateBefore = JsonUtility.ToJson(scenario.State);

            var result = new DispatchInfluenceCommandHandler(scenario.Service).Handle(
                scenario.State,
                new GameCommand
                {
                    Kind = GameCommandKind.DispatchInfluence,
                    PlayerId = 1,
                    SourceId = scenario.FirstSource,
                    TargetId = scenario.FirstTarget,
                    Parameters =
                    {
                        { "source2", scenario.SecondSource }
                    }
                });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(JsonUtility.ToJson(scenario.State), Is.EqualTo(stateBefore));
        }

        [Test]
        public void DispatchInfluence_WhenSecondMoveIsIllegal_FailsWithoutMutatingState()
        {
            var scenario = CreateDispatchScenario();
            var stateBefore = JsonUtility.ToJson(scenario.State);

            var result = new DispatchInfluenceCommandHandler(scenario.Service).Handle(
                scenario.State,
                new GameCommand
                {
                    Kind = GameCommandKind.DispatchInfluence,
                    PlayerId = 1,
                    SourceId = scenario.FirstSource,
                    TargetId = scenario.FirstTarget,
                    Parameters =
                    {
                        { "source2", scenario.SecondSource },
                        { "target2", "location:missing:0" }
                    }
                });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(JsonUtility.ToJson(scenario.State), Is.EqualTo(stateBefore));
        }

        [Test]
        public void DispatchInfluence_WhenSameInfluenceIsMovedTwice_FailsWithoutMutatingState()
        {
            var scenario = CreateDispatchScenario();
            var stateBefore = JsonUtility.ToJson(scenario.State);

            var result = new DispatchInfluenceCommandHandler(scenario.Service).Handle(
                scenario.State,
                new GameCommand
                {
                    Kind = GameCommandKind.DispatchInfluence,
                    PlayerId = 1,
                    SourceId = scenario.FirstSource,
                    TargetId = scenario.FirstTarget,
                    Parameters =
                    {
                        { "source2", scenario.FirstTarget },
                        { "target2", scenario.SecondTarget }
                    }
                });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidSource));
            Assert.That(JsonUtility.ToJson(scenario.State), Is.EqualTo(stateBefore));
        }

        [Test]
        public void MoveCity_Succeeds_PaysCostRemovesOpponentsPlacesSourceInfluenceAndMarksMainActionComplete()
        {
            var state = CreateActionState();
            AddResourceToken(state, "city-a");
            AddResourceToken(state, "mine-b");
            state.FindPlayer(1).CityLocationId = "city-a";
            state.FindPlayer(1).Resources.OriginiumShard = 3;
            state.FindPlayer(2).InfluenceSupply = 29;
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("route-a-b", 0),
                RouteId = "route-a-b"
            });
            var handler = CreateMoveCityHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "mine-b"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("mine-b"));
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(30));
            Assert.That(state.Map.Influences.Exists(influence => influence.PlayerId == 2), Is.False);
            Assert.That(state.Map.Influences.Exists(influence => influence.PlayerId == 1 && influence.LocationId == "city-a"), Is.True);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.True);
        }

        [Test]
        public void MoveCity_WithInsufficientResource_FailsWithoutMoving()
        {
            var state = CreateActionState();
            AddResourceToken(state, "city-a");
            AddResourceToken(state, "mine-b");
            state.FindPlayer(1).CityLocationId = "city-a";
            state.FindPlayer(1).Resources.OriginiumShard = 2;
            var handler = CreateMoveCityHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "mine-b"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("city-a"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.Map.Influences, Is.Empty);
        }

        [Test]
        public void EndAction_AfterMainActionComplete_AdvancesCurrentPlayer()
        {
            var state = CreateActionState();
            state.FindPlayer(1).ActedMainActionThisTurn = true;
            var handler = new EndActionCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
        }

        [Test]
        public void EndAction_BeforeMainActionComplete_FailsWithoutAdvancing()
        {
            var state = CreateActionState();
            var handler = new EndActionCommandHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void RoundAdvance_WithSeatTurnOrder_AdvancesActionRoundsAsOneTwoThreeFour()
        {
            var state = CreateFourPlayerActionState();
            var service = new RoundAdvanceService();

            service.CompleteMainAction(state, 1);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            service.CompleteMainAction(state, 2);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            service.CompleteMainAction(state, 3);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(4));
            service.CompleteMainAction(state, 4);

            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound2));
            Assert.That(state.ActionRound, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));

            service.CompleteMainAction(state, 1);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            service.CompleteMainAction(state, 2);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(3));
            service.CompleteMainAction(state, 3);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(4));
            service.CompleteMainAction(state, 4);

            Assert.That(state.Round, Is.EqualTo(1));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(state.ActionRound, Is.EqualTo(0));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
            Assert.That(state.FindPlayer(2).ActedMainActionThisTurn, Is.False);
            Assert.That(state.FindPlayer(3).ActedMainActionThisTurn, Is.False);
            Assert.That(state.FindPlayer(4).ActedMainActionThisTurn, Is.False);

            EndResourceCollectionAndCleanup(state, service);

            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.ActionRound, Is.Zero);
            Assert.That(state.StartPlayerId, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(-1));
        }

        [Test]
        public void RoundAdvance_WithSinglePlayer_EndsSecondActionRoundIntoNextRound()
        {
            var state = CreateSinglePlayerSecondActionRoundState();
            var service = new RoundAdvanceService();

            service.CompleteMainAction(state, 1);

            Assert.That(state.Round, Is.EqualTo(1));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(state.ActionRound, Is.EqualTo(0));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(0));

            EndResourceCollectionAndCleanup(state, service);

            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.ActionRound, Is.Zero);
            Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.False);
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(-1));
        }

        [Test]
        public void RoundAdvance_OnLastRoundFirstActionRound_AdvancesToSecondActionRound()
        {
            var state = CreateSinglePlayerActionRoundState(GamePhase.ActionRound1, 8, 1);
            var service = new RoundAdvanceService();

            service.CompleteMainAction(state, 1);

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound2));
            Assert.That(state.ActionRound, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void RoundAdvance_OnLastRoundSecondActionRound_EntersFinalScoring()
        {
            var state = CreateSinglePlayerActionRoundState(GamePhase.ActionRound2, 8, 2);
            var service = new RoundAdvanceService();

            service.CompleteMainAction(state, 1);

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(state.ActionRound, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);

            EndResourceCollectionAndCleanup(state, service);

            Assert.That(state.Round, Is.EqualTo(8));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.FinalScoring));
            Assert.That(state.ActionRound, Is.EqualTo(0));
        }

        private static MoveCityCommandHandler CreateMoveCityHandler()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var influenceService = new InfluenceService(mapQuery);
            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(mapQuery, influenceService, travelCostService);
            return new MoveCityCommandHandler(movementService);
        }

        private static InfluenceService CreateInfluenceService()
        {
            return new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));
        }

        private static DispatchScenario CreateDispatchScenario()
        {
            var scenario = new DispatchScenario
            {
                State = CreateActionState(),
                Service = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap())),
                FirstSource = InfluenceService.GetLocationSlotId("A-01", 0),
                FirstTarget = InfluenceService.GetLocationSlotId("C-01", 0),
                SecondSource = InfluenceService.GetLocationSlotId("B-01", 0),
                SecondTarget = InfluenceService.GetLocationSlotId("D-01", 0)
            };
            AddResourceToken(scenario.State, "C-01");
            AddResourceToken(scenario.State, "D-01");
            scenario.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = scenario.FirstSource,
                LocationId = "A-01"
            });
            scenario.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = scenario.SecondSource,
                LocationId = "B-01"
            });
            return scenario;
        }

        private sealed class DispatchScenario
        {
            public GameState State;
            public InfluenceService Service;
            public string FirstSource;
            public string FirstTarget;
            public string SecondSource;
            public string SecondTarget;
        }

        private static GameState CreateActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue
                    }
                }
            };
        }

        private static GameState CreateSinglePlayerSecondActionRoundState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound2,
                Round = 1,
                ActionRound = 2,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Blue,
                        HasMovedCityThisRound = true
                    }
                }
            };
        }

        private static GameState CreateSinglePlayerActionRoundState(GamePhase phase, int round, int actionRound)
        {
            return new GameState
            {
                Phase = phase,
                Round = round,
                MaxRounds = 8,
                ActionRound = actionRound,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Blue,
                        HasMovedCityThisRound = true
                    }
                }
            };
        }

        private static GameState CreateFourPlayerActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                UseSeatTurnOrder = true,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue
                    },
                    new PlayerState
                    {
                        PlayerId = 3,
                        Color = PlayerColor.Green
                    },
                    new PlayerState
                    {
                        PlayerId = 4,
                        Color = PlayerColor.Yellow
                    }
                }
            };
        }

        private static void AddResourceToken(GameState state, string locationId)
        {
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
        }

        private static void EndResourceCollectionAndCleanup(GameState state, RoundAdvanceService service)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].HasCollectedResourcesThisRound = true;
            }

            service.AdvanceResourceCollectionToCleanup(state);
            var result = service.EndCompletedAction(state, state.CurrentPlayerId);
            Assert.That(result.IsValid, Is.True, result.Reason);
        }
    }
}
