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
    public sealed class GameplayCommandHandlerTests
    {
        [Test]
        public void DeployInfluence_SucceedsAndAdvancesCurrentPlayer()
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
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
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
        public void DispatchInfluence_MovesOneInfluenceAndAdvances()
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
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
        }

        [Test]
        public void MoveCity_Succeeds_PaysCostRemovesOpponentsPlacesSourceInfluenceAndAdvancesRound()
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
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
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

        private static void AddResourceToken(GameState state, string locationId)
        {
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
        }
    }
}
