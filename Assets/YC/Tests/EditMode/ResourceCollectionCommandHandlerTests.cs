using NUnit.Framework;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ResourceCollectionCommandHandlerTests
    {
        [Test]
        public void CollectResource_WithMultipleTargets_SharesRouteCostPaysChosenRecipientAndAdvancesToCleanup()
        {
            var state = CreateCollectionState();
            state.Players.Add(new PlayerState { PlayerId = 3, Color = PlayerColor.Green });
            state.FindPlayer(1).Resources.GoldVoucher = 6;
            state.FindPlayer(2).HasCollectedResourcesThisRound = true;
            state.FindPlayer(3).HasCollectedResourcesThisRound = true;
            AddToken(state, "mine-c", ResourceType.Iron, 1);
            AddToken(state, "quarry-d", ResourceType.OriginiumShard, 2);
            AddLocationInfluence(state, 1, "mine-c");
            AddLocationInfluence(state, 1, "quarry-d");
            AddRouteInfluence(state, 2, "route-a-hub", 0);
            AddRouteInfluence(state, 3, "route-a-hub", 1);
            AddRouteInfluence(state, 1, "route-hub-mine", 0);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                Parameters =
                {
                    { CollectResourceCommandHandler.LocationIdsParameter, "mine-c,quarry-d" },
                    { CollectResourceCommandHandler.PaymentRecipientsParameter, "route-a-hub=3" }
                }
            });

            Assert.That(result.Succeeded, Is.True, result.Validation == null ? string.Empty : result.Validation.Reason);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Cleanup));
            Assert.That(result.Events[0].Data["paymentCount"], Is.EqualTo("2"));
        }

        [Test]
        public void CollectResource_WhenUsingGoldGainedDuringCollectionPhaseToPay_FailsWithoutReward()
        {
            var state = CreateCollectionState();
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            state.FindPlayer(1).ResourceCollectionStartGoldVoucher = 0;
            AddToken(state, "mine-c", ResourceType.GoldVoucher, 3);
            AddLocationInfluence(state, 1, "mine-c");
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "mine-c"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
        }

        [Test]
        public void CollectResource_WithInvalidRecipient_FailsWithoutPaying()
        {
            var state = CreateCollectionState();
            state.FindPlayer(1).Resources.GoldVoucher = 4;
            AddToken(state, "mine-c", ResourceType.Iron, 1);
            AddLocationInfluence(state, 1, "mine-c");
            AddRouteInfluence(state, 2, "route-a-hub", 0);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "mine-c",
                Parameters =
                {
                    { CollectResourceCommandHandler.PaymentRecipientsParameter, "route-a-hub=3" }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
        }

        [Test]
        public void CollectResource_WhenTargetHasNoOwnInfluenceOrCity_Fails()
        {
            var state = CreateCollectionState();
            state.FindPlayer(1).Resources.GoldVoucher = 4;
            AddToken(state, "mine-c", ResourceType.Iron, 1);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "mine-c"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
        }

        private static CollectResourceCommandHandler CreateHandler()
        {
            var mapQuery = new MapQueryService(CreateCollectionMap());
            return new CollectResourceCommandHandler(new ResourceCollectionService(mapQuery, new ResourceTokenService()));
        }

        private static GameState CreateCollectionState()
        {
            return new GameState
            {
                Phase = GamePhase.ResourceCollection,
                Round = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "city-a"
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue
                    }
                }
            };
        }

        private static GameMapDefinition CreateCollectionMap()
        {
            return new GameMapDefinition
            {
                MapId = "resource-collection-test",
                MinPlayers = 2,
                MaxPlayers = 4,
                Locations = new List<MapLocationDefinition>
                {
                    CreateLocation("city-a"),
                    CreateLocation("hub-b"),
                    CreateLocation("mine-c"),
                    CreateLocation("quarry-d")
                },
                Routes = new List<MapRouteDefinition>
                {
                    CreateRoute("route-a-hub", "city-a", "hub-b", 2),
                    CreateRoute("route-hub-mine", "hub-b", "mine-c", 2),
                    CreateRoute("route-hub-quarry", "hub-b", "quarry-d", 2)
                }
            };
        }

        private static MapLocationDefinition CreateLocation(string locationId)
        {
            return new MapLocationDefinition
            {
                LocationId = locationId,
                ResourceType = ResourceType.Iron,
                CanDockCity = true,
                ResourceSlotCount = 1,
                InfluenceSlotCount = 2
            };
        }

        private static MapRouteDefinition CreateRoute(string routeId, string fromLocationId, string toLocationId, int cost)
        {
            return new MapRouteDefinition
            {
                RouteId = routeId,
                FromLocationId = fromLocationId,
                ToLocationId = toLocationId,
                InfluenceSlotCount = 2,
                BaseCost = cost
            };
        }

        private static void AddToken(GameState state, string locationId, ResourceType resourceType, int amount)
        {
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = resourceType,
                Amount = amount
            });
        }

        private static void AddLocationInfluence(GameState state, int playerId, string locationId)
        {
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = InfluenceService.GetLocationSlotId(locationId, 0),
                LocationId = locationId
            });
        }

        private static void AddRouteInfluence(GameState state, int playerId, string routeId, int slotIndex)
        {
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = InfluenceService.GetRouteSlotId(routeId, slotIndex),
                RouteId = routeId
            });
        }
    }
}
