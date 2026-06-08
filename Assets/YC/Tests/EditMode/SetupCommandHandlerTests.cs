using NUnit.Framework;
using YC.Application.Setup;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class SetupCommandHandlerTests
    {
        [Test]
        public void ChooseStartPlayer_Succeeds_SetsStartAndCurrentPlayerAndEntersEntrance()
        {
            var state = CreateState(GamePhase.Setup);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseStartPlayer,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.StartPlayerId, Is.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
            Assert.That(result.Events, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(result.LogMessage, Does.Contain("start player"));
        }

        [Test]
        public void ChooseStartPlayer_InWrongPhase_FailsWithoutChangingState()
        {
            var state = CreateState(GamePhase.Entrance);
            state.StartPlayerId = 2;
            state.CurrentPlayerId = 2;
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseStartPlayer,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(state.StartPlayerId, Is.EqualTo(2));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
        }

        [Test]
        public void ChooseInitialLocation_Succeeds_SetsCityLocationAndOpensLocation()
        {
            var state = CreateState(GamePhase.Entrance);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "city-a"
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo("city-a"));
            Assert.That(state.Map.OpenLocationIds, Does.Contain("city-a"));
            Assert.That(result.Events, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(result.LogMessage, Does.Contain("city-a"));
        }

        [Test]
        public void ChooseInitialLocation_WithUndockableLocation_FailsWithoutChangingState()
        {
            var state = CreateState(GamePhase.Entrance);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "mine-b"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.Empty);
            Assert.That(state.Map.OpenLocationIds, Is.Empty);
        }

        [Test]
        public void ChooseInitialLocation_WithOtherPlayerCityAtLocation_FailsWithoutChangingState()
        {
            var state = CreateState(GamePhase.Entrance);
            state.FindPlayer(2).CityLocationId = "city-a";
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "city-a"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(state.FindPlayer(1).CityLocationId, Is.Empty);
            Assert.That(state.FindPlayer(2).CityLocationId, Is.EqualTo("city-a"));
            Assert.That(state.Map.OpenLocationIds, Is.Empty);
        }

        private static SetupCommandHandler CreateHandler()
        {
            return new SetupCommandHandler(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "Player 1",
                        Color = PlayerColor.Red
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "Player 2",
                        Color = PlayerColor.Blue
                    }
                }
            };
        }
    }
}
