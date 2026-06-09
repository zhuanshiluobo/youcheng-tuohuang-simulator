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
    public sealed class MoveCityPreconditionTests
    {
        [Test]
        public void MoveCity_ToAdjacentOpenLocation_WithEnoughOriginiumShard_PaysCostAndMoves()
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
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(0));
        }

        [Test]
        public void MoveCity_ToRedZoneLocationBeforeRound4_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.Round = 2;
            state.FindPlayer(1).CityLocationId = "F-02";
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "F-01"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.ClosedLocation));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("F-02"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
        }

        [Test]
        public void MoveCity_WithoutThreeOriginiumShard_FailsWithoutChangingState()
        {
            var state = CreateState();
            state.FindPlayer(1).Resources.OriginiumShard = 2;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.MoveCity,
                PlayerId = 1,
                TargetId = "A-02"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("A-01"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
        }

        [Test]
        public void MoveCity_ToLocationOccupiedByAnotherCity_FailsBeforePayingCost()
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
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(3));
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
                        CityLocationId = "B-01",
                        Resources =
                        {
                            OriginiumShard = 3
                        }
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
