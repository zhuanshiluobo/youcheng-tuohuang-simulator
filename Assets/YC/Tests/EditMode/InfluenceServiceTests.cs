using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceServiceTests
    {
        [Test]
        public void Place_OnLocationWithResourceToken_SucceedsAndConsumesSupply()
        {
            var state = CreateState();
            AddResourceToken(state, "city-a");
            var service = CreateService();
            var slotId = InfluenceService.GetLocationSlotId("city-a", 0);

            var result = service.Place(state, 1, slotId);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(slotId));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void Place_OnUnexploredLocation_FailsWithoutConsumingSupply()
        {
            var state = CreateState();
            var service = CreateService();

            var result = service.Place(state, 1, InfluenceService.GetLocationSlotId("city-a", 0));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.ClosedLocation));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void Place_OnLocationWithOpponentCity_Fails()
        {
            var state = CreateState();
            AddResourceToken(state, "city-a");
            state.FindPlayer(2).CityLocationId = "city-a";
            var service = CreateService();

            var result = service.Place(state, 1, InfluenceService.GetLocationSlotId("city-a", 0));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(state.Map.Influences, Is.Empty);
        }

        [Test]
        public void Place_OnRoadCoveredRoute_Fails()
        {
            var state = CreateState();
            state.Map.RoadRouteIds.Add("route-a-b");
            var service = CreateService();

            var result = service.Place(state, 1, InfluenceService.GetRouteSlotId("route-a-b", 0));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.Map.Influences, Is.Empty);
        }

        [Test]
        public void Place_WithNoSupply_Fails()
        {
            var state = CreateState();
            AddResourceToken(state, "city-a");
            state.FindPlayer(1).InfluenceSupply = 0;
            var service = CreateService();

            var result = service.Place(state, 1, InfluenceService.GetLocationSlotId("city-a", 0));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientInfluence));
            Assert.That(state.Map.Influences, Is.Empty);
        }

        [Test]
        public void Move_ExistingInfluence_UpdatesSlotWithoutChangingSupply()
        {
            var state = CreateState();
            AddResourceToken(state, "city-a");
            AddResourceToken(state, "harbor-c");
            var service = CreateService();
            var sourceSlot = InfluenceService.GetLocationSlotId("city-a", 0);
            var targetSlot = InfluenceService.GetLocationSlotId("harbor-c", 0);
            service.Place(state, 1, sourceSlot);

            var result = service.Move(state, 1, sourceSlot, targetSlot);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(targetSlot));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void Remove_ExistingInfluence_ReturnsSupply()
        {
            var state = CreateState();
            AddResourceToken(state, "city-a");
            var service = CreateService();
            var slotId = InfluenceService.GetLocationSlotId("city-a", 0);
            service.Place(state, 1, slotId);

            var result = service.Remove(state, slotId);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void CountInRegion_IncludesInfluenceAndCityAsTwo()
        {
            var state = CreateState();
            AddResourceToken(state, "city-a");
            state.FindPlayer(1).CityLocationId = "mine-b";
            var service = CreateService();
            service.Place(state, 1, InfluenceService.GetLocationSlotId("city-a", 0));

            var count = service.CountInRegion(state, 1, "north", true);

            Assert.That(count, Is.EqualTo(3));
        }

        [Test]
        public void CountInRegion_IncludesRouteInfluenceByRouteRegion()
        {
            var state = new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red
                    }
                },
                Map =
                {
                    Influences =
                    {
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetRouteSlotId("A1", 0),
                            RouteId = "A1"
                        },
                        new InfluencePlacement
                        {
                            PlayerId = 1,
                            SlotId = InfluenceService.GetRouteSlotId("R1", 0),
                            RouteId = "R1"
                        }
                    }
                }
            };
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            Assert.That(service.CountInRegion(state, 1, "A", false), Is.EqualTo(1));
            Assert.That(service.CountInRegion(state, 1, "R", false), Is.EqualTo(1));
        }

        private static InfluenceService CreateService()
        {
            return new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));
        }

        private static GameState CreateState()
        {
            return new GameState
            {
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
