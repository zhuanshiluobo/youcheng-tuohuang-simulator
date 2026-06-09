using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class MoveCityCommandHandlerTests
    {
        [Test]
        public void MoveCity_ToAdjacentOpenLocation_Succeeds()
        {
            var state = CreateState();
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-02"));
            Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.True);
            Assert.That(result.Events[0].Kind, Is.EqualTo(GameEventKind.CityMoved));
        }

        [Test]
        public void MoveCity_ToNonAdjacentLocation_FailsWithoutChangingState()
        {
            var state = CreateState();
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "C-01"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NoRoute));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.False);
        }

        [Test]
        public void MoveCity_ToLocationOccupiedByAnotherCity_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.FindPlayer(2).CityLocationId = "A-02";
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(2).CityLocationId, Is.EqualTo("A-02"));
        }

        [Test]
        public void MoveCity_WhenPlayerAlreadyTookMainAction_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.FindPlayer(1).ActedMainActionThisTurn = true;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
        }

        [Test]
        public void MoveCity_WhenNotCurrentPlayer_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.CurrentPlayerId = 2;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NotCurrentPlayer));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
        }

        [Test]
        public void MoveCity_InWrongPhase_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.Phase = GamePhase.Entrance;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
        }

        [Test]
        public void PhaseFlow_AdvancingToNextRound_ResetsCityMoveFlags()
        {
            var state = CreateState();
            state.Phase = GamePhase.Cleanup;
            state.Round = 1;
            state.MaxRounds = 8;
            state.FindPlayer(1).HasMovedCityThisRound = true;

            PhaseFlow.Advance(state);

            Assert.That(state.Phase, Is.EqualTo(GamePhase.RoundStart));
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).HasMovedCityThisRound, Is.False);
        }

        private static MoveCityCommandHandler CreateHandler()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap());
            var influenceService = new InfluenceService(mapQuery);
            var travelCostService = new TravelCostService(mapQuery);
            var movementService = new CityMovementService(mapQuery, influenceService, travelCostService);
            return new MoveCityCommandHandler(movementService);
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
                        Name = "Player 1",
                        Color = PlayerColor.Red,
                        CityLocationId = "A-01",
                        Resources =
                        {
                            OriginiumShard = 3
                        }
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "Player 2",
                        Color = PlayerColor.Blue,
                        CityLocationId = "B-01"
                    }
                },
                Map =
                {
                    OpenLocationIds = { "A-01", "A-02", "B-01" },
                    ResourceTokens =
                    {
                        new ResourceTokenState
                        {
                            LocationId = "A-01",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        },
                        new ResourceTokenState
                        {
                            LocationId = "A-02",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        }
                    }
                }
            };
        }
    }
}
