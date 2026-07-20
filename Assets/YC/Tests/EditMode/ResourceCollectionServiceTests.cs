using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Harvest;
using YC.Domain.Influence;
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

        [Test]
        public void CollectResource_WhenRouteHasOpponentInfluence_PaysOpponent()
        {
            var state = CreateCollectionStateOnFourPlayerMap();
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            state.Players.Add(new PlayerState { PlayerId = 2, Color = PlayerColor.Blue });
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetLocationSlotId("A-02", 0),
                LocationId = "A-02"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("A1", 0),
                RouteId = "A1"
            });
            var service = new ResourceCollectionService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            var result = service.Collect(
                state,
                1,
                new List<string> { "A-02" },
                new List<string> { "A1" },
                null);

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(result.Payments, Has.Count.EqualTo(1));
            Assert.That(result.Payments[0].ReceiverPlayerId, Is.EqualTo(2));
        }

        [Test]
        public void CollectResource_WithSameRegionRoutes_PaysSharedPaymentKeyOnce()
        {
            var state = CreateCollectionStateOnFourPlayerMap();
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-03",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetLocationSlotId("A-02", 0),
                LocationId = "A-02"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetLocationSlotId("A-03", 0),
                LocationId = "A-03"
            });
            var service = new ResourceCollectionService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            var result = service.Collect(
                state,
                1,
                new List<string> { "A-02", "A-03" },
                new List<string> { "A1", "A2" },
                null);

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2));
            Assert.That(result.Payments, Has.Count.EqualTo(1));
            Assert.That(result.Payments[0].RouteId, Is.EqualTo("A1"));
        }

        [Test]
        public void CollectResource_FromF02ToD03_OwnInfluenceOnD1DoesNotExemptD2Toll()
        {
            var state = CreateCollectionStateOnFourPlayerMap();
            var player = state.FindPlayer(1);
            player.CityLocationId = "F-02";
            player.Resources.GoldVoucher = 2;
            state.Players.Add(new PlayerState { PlayerId = 2, Color = PlayerColor.Blue });
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "D-03",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetLocationSlotId("D-03", 0),
                LocationId = "D-03"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetRouteSlotId("D1", 0),
                RouteId = "D1"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("D1", 1),
                RouteId = "D1"
            });
            var service = new ResourceCollectionService(
                new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            var initial = service.QuerySelection(state, 1, null);

            Assert.That(initial.IsValid, Is.True, initial.Validation.Reason);
            Assert.That(initial.CandidateLocationIds, Does.Contain("D-03"));
            Assert.That(initial.PathsByLocationId.ContainsKey("D-03"), Is.False);
            ResourceCollectionRouteOption d2Toll;
            Assert.That(initial.TryGetRouteOption("D2", out d2Toll), Is.True);
            Assert.That(d2Toll.PaymentKey, Is.EqualTo("D"));
            Assert.That(d2Toll.CanAfford, Is.True);
            Assert.That(d2Toll.OpponentOwnerPlayerIds, Is.Empty);

            var afterConfirmation = service.QuerySelection(state, 1, new[] { "D2" });

            Assert.That(afterConfirmation.PathsByLocationId["D-03"].RouteIds, Is.EqualTo(new[] { "D2" }));
            Assert.That(afterConfirmation.ConfirmedTollCost, Is.EqualTo(2));

            var invalidRecipient = service.CanCollect(
                state,
                1,
                new List<string> { "D-03" },
                new List<string> { "D2" },
                new Dictionary<string, int> { { "D2", 2 } });

            Assert.That(invalidRecipient.IsValid, Is.False);
            Assert.That(invalidRecipient.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(invalidRecipient.Reason, Does.Contain("没有对手影响力"));

            var result = service.Collect(
                state,
                1,
                new List<string> { "D-03" },
                new List<string> { "D2" },
                null);

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(player.Resources.Iron, Is.EqualTo(1));
            Assert.That(result.Payments, Has.Count.EqualTo(1));
            Assert.That(result.Payments[0].RouteId, Is.EqualTo("D2"));
            Assert.That(result.Payments[0].PaidToSupply, Is.True);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
        }

        [Test]
        public void CollectResource_WithInvalidPaymentRecipient_FailsWithoutChangingResources()
        {
            var state = CreateCollectionStateOnFourPlayerMap();
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            state.Players.Add(new PlayerState { PlayerId = 2, Color = PlayerColor.Blue });
            state.Players.Add(new PlayerState { PlayerId = 3, Color = PlayerColor.Green });
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetLocationSlotId("A-02", 0),
                LocationId = "A-02"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("A1", 0),
                RouteId = "A1"
            });
            var service = new ResourceCollectionService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            var result = service.Collect(
                state,
                1,
                new List<string> { "A-02" },
                new List<string> { "A1" },
                new Dictionary<string, int> { { "A1", 3 } });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
        }

        [Test]
        public void QuerySelection_UsesOwnedRoutesAndConfirmedTollsWithoutChangingState()
        {
            var map = CreateCollectionQueryMap();
            var state = CreateCollectionQueryState();
            var service = new ResourceCollectionService(new MapQueryService(map));

            var initial = service.QuerySelection(state, 1, null);

            Assert.That(initial.IsValid, Is.True, initial.Validation.Reason);
            Assert.That(initial.CandidateLocationIds, Is.EquivalentTo(new[] { "B", "C" }));
            Assert.That(initial.PathsByLocationId.ContainsKey("B"), Is.True);
            Assert.That(initial.PathsByLocationId.ContainsKey("C"), Is.False);
            ResourceCollectionRouteOption tollRoute;
            Assert.That(initial.TryGetRouteOption("R2", out tollRoute), Is.True);
            Assert.That(tollRoute.Cost, Is.EqualTo(2));
            Assert.That(tollRoute.CanAfford, Is.True);
            Assert.That(tollRoute.OpponentOwnerPlayerIds, Is.EqualTo(new[] { 2 }));

            var afterConfirmation = service.QuerySelection(state, 1, new[] { "R2" });

            Assert.That(afterConfirmation.IsValid, Is.True, afterConfirmation.Validation.Reason);
            Assert.That(afterConfirmation.PathsByLocationId["C"].RouteIds, Is.EqualTo(new[] { "R1", "R2" }));
            Assert.That(afterConfirmation.ConfirmedTollCost, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(-1));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
        }

        [Test]
        public void QuerySelection_WhenTollIsUnaffordable_ReportsBoundaryRouteButNotReachableTarget()
        {
            var state = CreateCollectionQueryState();
            state.FindPlayer(1).Resources.GoldVoucher = 0;
            var service = new ResourceCollectionService(new MapQueryService(CreateCollectionQueryMap()));

            var result = service.QuerySelection(state, 1, null);

            ResourceCollectionRouteOption tollRoute;
            Assert.That(result.IsValid, Is.True, result.Validation.Reason);
            Assert.That(result.TryGetRouteOption("R2", out tollRoute), Is.True);
            Assert.That(tollRoute.CanAfford, Is.False);
            Assert.That(result.PathsByLocationId.ContainsKey("C"), Is.False);
        }

        [Test]
        public void QuerySelection_OutsideResourceCollectionPhase_ReturnsExplicitFailure()
        {
            var state = CreateCollectionQueryState();
            state.Phase = GamePhase.ActionRound1;
            var service = new ResourceCollectionService(new MapQueryService(CreateCollectionQueryMap()));

            var result = service.QuerySelection(state, 1, null);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(result.CandidateLocationIds, Is.Empty);
            Assert.That(state.FindPlayer(1).ResourceCollectionStartGoldVoucher, Is.EqualTo(-1));
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

        private static GameState CreateCollectionStateOnFourPlayerMap()
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
                        CityLocationId = "A-01"
                    }
                }
            };
        }

        private static GameMapDefinition CreateCollectionQueryMap()
        {
            return new GameMapDefinition
            {
                MapId = "collection-query",
                Locations =
                {
                    new MapLocationDefinition { LocationId = "A", CanDockCity = true, InfluenceSlotCount = 1 },
                    new MapLocationDefinition { LocationId = "B", InfluenceSlotCount = 1 },
                    new MapLocationDefinition { LocationId = "C", InfluenceSlotCount = 1 }
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

        private static GameState CreateCollectionQueryState()
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
                        CityLocationId = "A",
                        Resources = { GoldVoucher = 2 }
                    },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                },
                Map =
                {
                    ResourceTokens =
                    {
                        new ResourceTokenState { LocationId = "B", ResourceType = ResourceType.Iron, Amount = 1 },
                        new ResourceTokenState { LocationId = "C", ResourceType = ResourceType.Originium, Amount = 1 }
                    },
                    Influences =
                    {
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
    }
}
