using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ResourceCollectionServiceTests
    {
        [Test]
        public void CanCollect_WithDuplicateTargets_FailsWithoutWritingGoldSnapshot()
        {
            var state = CreateResourceCollectionState();
            var service = new ResourceCollectionService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));

            var result = service.CanCollect(
                state,
                1,
                new List<string> { "city-a", "city-a" },
                null);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(-1));
            Assert.That(state.FindPlayer(2).ResourceCollectionStartGoldVoucher, Is.EqualTo(-1));
        }

        [Test]
        public void CollectResource_WithMalformedPaymentRecipient_FailsWithoutCollecting()
        {
            var state = CreateResourceCollectionState();
            var service = new ResourceCollectionService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder()));
            var handler = new CollectResourceCommandHandler(service);

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "city-a",
                Parameters =
                {
                    { CollectResourceCommandHandler.PaymentRecipientsParameter, "route-a-b=player2" }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(-1));
        }

        [Test]
        public void CollectResource_WithExplicitConnectedRouteNetwork_UsesPaidRouteOnceAndOwnRoutesForMoreTargets()
        {
            var state = new GameState
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
                        Color = PlayerColor.Blue,
                        CityLocationId = "C-01",
                        Resources = { GoldVoucher = 2 }
                    }
                },
                Map =
                {
                    Influences =
                    {
                        new InfluencePlacement { PlayerId = 1, SlotId = "location:B-01:0", LocationId = "B-01" },
                        new InfluencePlacement { PlayerId = 1, SlotId = "location:A-02:0", LocationId = "A-02" },
                        new InfluencePlacement { PlayerId = 1, SlotId = "route:B2:0", RouteId = "B2" },
                        new InfluencePlacement { PlayerId = 1, SlotId = "route:R6:0", RouteId = "R6" }
                    },
                    ResourceTokens =
                    {
                        new ResourceTokenState
                        {
                            LocationId = "B-01",
                            ResourceType = ResourceType.OriginiumShard,
                            Amount = 2
                        },
                        new ResourceTokenState
                        {
                            LocationId = "A-02",
                            ResourceType = ResourceType.Iron,
                            Amount = 2
                        }
                    }
                }
            };
            var service = new ResourceCollectionService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            var handler = new CollectResourceCommandHandler(service);

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                Parameters =
                {
                    { CollectResourceCommandHandler.LocationIdsParameter, "B-01,A-02" },
                    { CollectResourceCommandHandler.RouteIdsParameter, "C2,B2,R6" }
                }
            });

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2));
            Assert.That(result.Events[0].Data["paymentCount"], Is.EqualTo("1"));
        }

        private static GameState CreateResourceCollectionState()
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
                        Color = PlayerColor.Blue,
                        CityLocationId = "city-a",
                        Resources = { GoldVoucher = 5 }
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Red,
                        CityLocationId = "mine-b",
                        Resources = { GoldVoucher = 5 }
                    }
                },
                Map =
                {
                    ResourceTokens =
                    {
                        new ResourceTokenState
                        {
                            LocationId = "city-a",
                            ResourceType = ResourceType.Iron,
                            Amount = 1
                        }
                    }
                }
            };
        }
    }
}
