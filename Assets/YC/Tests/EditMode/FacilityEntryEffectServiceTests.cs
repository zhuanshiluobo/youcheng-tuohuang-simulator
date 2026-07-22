using NUnit.Framework;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class FacilityEntryEffectServiceTests
    {
        [Test]
        public void UrbanizedArea_GainsFourGoldPerFacilityOrthogonallyAdjacentToCore()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            AddFacility(state, 1, FacilityCardDatabase.CoreCommandTower, 7);
            AddFacility(state, 1, "building_014", 4);
            AddFacility(state, 1, "building_015", 6);
            AddFacility(state, 1, "building_018", 3); // diagonal/non-adjacent

            new FacilityEntryEffectService().Resolve(
                state,
                player,
                FacilityCardDatabase.Get("building_015"),
                6);

            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(8));
        }

        [Test]
        public void FederalCouncil_RecordsLatestBuilderImmediatelyOnEntry()
        {
            var state = CreateState();

            new FacilityEntryEffectService().Resolve(
                state,
                state.FindPlayer(1),
                FacilityCardDatabase.Get("building_007"),
                0);

            Assert.That(state.LastFederalCouncilBuilderThisRoundPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void CopyAdjacentEntry_OffersOnlyOrthogonalNonRainbowEntryEffects()
        {
            var state = CreateState();
            AddFacility(state, 1, "building_004", 4);
            AddFacility(state, 1, "building_014", 1); // above
            AddFacility(state, 1, "building_001", 3); // left, rainbow
            AddFacility(state, 1, "building_022", 5); // right, passive effect only
            AddFacility(state, 1, "building_018", 0); // diagonal

            new FacilityEntryEffectService().Resolve(
                state,
                state.FindPlayer(1),
                FacilityCardDatabase.Get("building_004"),
                4);

            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.ChoiceType,
                Is.EqualTo(FacilityPendingChoiceTypes.CopyAdjacentEntryEffect));
            Assert.That(state.PendingCardSession.OptionIds, Is.EquivalentTo(new[] { "1" }));
        }

        [Test]
        public void LogisticsHub_OffersUnusedExtensionHubsAndExplicitSkip()
        {
            var state = CreateState();
            AddFacility(state, 2, FacilityCardDatabase.ExtensionHubBlue, 0);

            new FacilityEntryEffectService().Resolve(
                state,
                state.FindPlayer(1),
                FacilityCardDatabase.Get("building_012"),
                0);

            Assert.That(state.PendingCardSession.OptionIds, Is.EquivalentTo(new[]
            {
                FacilityCardDatabase.ExtensionHubYellow,
                FacilityCardDatabase.ExtensionHubRed,
                FacilityPendingChoiceTypes.SkipOption
            }));
        }

        [TestCase("building_019", FacilityPendingChoiceTypes.SellResources)]
        [TestCase("building_025", FacilityPendingChoiceTypes.FreeCityMove)]
        [TestCase("building_029", FacilityPendingChoiceTypes.ChooseFiveBasicResources)]
        [TestCase("building_034", FacilityPendingChoiceTypes.ReplaceOneInfluence)]
        [TestCase("building_037", FacilityPendingChoiceTypes.DeployTwoInfluences)]
        [TestCase("building_039", FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore)]
        public void ChoiceFacility_OpensExpectedFacilitySession(string facilityId, string choiceType)
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = "location:A-02:0",
                LocationId = "A-02"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetRouteSlotId("B1", 0),
                RouteId = "B1"
            });

            new FacilityEntryEffectService().Resolve(
                state,
                player,
                FacilityCardDatabase.Get(facilityId),
                0);

            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.ScenarioId, Is.EqualTo(FacilityPendingChoiceTypes.ScenarioId));
            Assert.That(state.PendingCardSession.ChoiceType, Is.EqualTo(choiceType));
        }

        [Test]
        public void MercenaryCommand_WithReplaceAndDeployTargets_OffersBothBranchesInOrder()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("A1", 0),
                RouteId = "A1"
            });

            Resolve(state, "building_034");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.ReplaceOneInfluence);
            Assert.That(state.PendingCardSession.OptionIds, Is.EqualTo(new[]
            {
                FacilityPendingChoiceTypes.ReplaceInfluenceOption,
                FacilityPendingChoiceTypes.DeployInfluenceOption
            }));
        }

        [Test]
        public void MercenaryCommand_WithoutOpponentInfluence_StillOffersDeployBranch()
        {
            var state = CreateState();

            Resolve(state, "building_034");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.ReplaceOneInfluence);
            Assert.That(state.PendingCardSession.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.DeployInfluenceOption }));
        }

        [Test]
        public void MercenaryCommand_WithoutSupply_StillOffersReplaceBranch()
        {
            var state = CreateState();
            state.FindPlayer(1).InfluenceSupply = 0;
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("A1", 0),
                RouteId = "A1"
            });

            Resolve(state, "building_034");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.ReplaceOneInfluence);
            Assert.That(state.PendingCardSession.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.ReplaceInfluenceOption }));
        }

        [Test]
        public void MercenaryCommand_WithNoLegalBranch_OpensClosablePendingSession()
        {
            var state = CreateState();
            state.FindPlayer(1).InfluenceSupply = 0;

            Resolve(state, "building_034");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.ReplaceOneInfluence);
            Assert.That(state.PendingCardSession.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.SkipOption }));
            Assert.That(state.PendingCardSession.IsValid(), Is.True);
        }

        [Test]
        public void FreeCityMove_WithNoLegalDestination_DoesNotOpenPendingSession()
        {
            var state = CreateState();
            state.FindPlayer(1).CityLocationId = "A-01";
            state.FindPlayer(2).CityLocationId = "A-02";

            Resolve(state, "building_025");

            Assert.That(state.PendingCardSession, Is.Null);
        }

        [Test]
        public void FreeCityMove_WithLegalDestination_OpensPendingSession()
        {
            var state = CreateState();
            state.FindPlayer(1).CityLocationId = "A-01";
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-02",
                ResourceType = ResourceType.Iron
            });

            Resolve(state, "building_025");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.FreeCityMove);
        }

        [Test]
        public void Escort_WithNoLegalInfluenceSlot_DoesNotOpenPendingSession()
        {
            var state = CreateState();
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            for (var routeIndex = 0; routeIndex < map.Routes.Count; routeIndex++)
            {
                var route = map.Routes[routeIndex];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    state.Map.Influences.Add(new InfluencePlacement
                    {
                        PlayerId = 2,
                        SlotId = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex),
                        RouteId = route.RouteId
                    });
                }
            }

            Resolve(state, "building_037");

            Assert.That(state.PendingCardSession, Is.Null);
        }

        [Test]
        public void Escort_WithOnlyOneLegalInfluenceSlot_DoesNotOpenPendingSession()
        {
            var state = CreateState();
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var keptEmptySlot = InfluenceService.GetRouteSlotId(map.Routes[0].RouteId, 0);
            for (var routeIndex = 0; routeIndex < map.Routes.Count; routeIndex++)
            {
                var route = map.Routes[routeIndex];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(route.RouteId, slotIndex);
                    if (slotId == keptEmptySlot)
                    {
                        continue;
                    }

                    state.Map.Influences.Add(new InfluencePlacement
                    {
                        PlayerId = 2,
                        SlotId = slotId,
                        RouteId = route.RouteId
                    });
                }
            }

            Resolve(state, "building_037");

            Assert.That(state.PendingCardSession, Is.Null);
        }

        [Test]
        public void Escort_WithLegalInfluenceSlot_OpensPendingSession()
        {
            var state = CreateState();
            state.FindPlayer(1).CityLocationId = "A-01";
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "A-01",
                ResourceType = ResourceType.Iron
            });

            Resolve(state, "building_037");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.DeployTwoInfluences);
        }

        [Test]
        public void VehicleWarehouse_WithNoInfluenceAndNoLegalExplore_OpensClosablePendingSession()
        {
            var state = CreateState();
            state.FindPlayer(1).CityLocationId = "A-01";

            Resolve(state, "building_039");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            Assert.That(state.PendingCardSession.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.SkipOption }));
            Assert.That(state.PendingCardSession.IsValid(), Is.True);
        }

        [Test]
        public void VehicleWarehouse_WithLegalRemoveThenDispatch_OnlyOffersThatBranch()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("A1", 0),
                RouteId = "A1"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetRouteSlotId("B1", 0),
                RouteId = "B1"
            });

            Resolve(state, "building_039");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            Assert.That(state.PendingCardSession.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.RemoveDispatchOption }));
        }

        [Test]
        public void VehicleWarehouse_WithoutInfluenceButWithLegalExplore_OpensPendingSession()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            player.Resources.GoldVoucher = 2;
            state.Decks.EventDeckGreen.Add("event_green_01");

            Resolve(state, "building_039");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            Assert.That(state.PendingCardSession.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.ExploreOption }));
        }

        [Test]
        public void VehicleWarehouse_WithBothLegalBranches_OffersRemoveThenExploreInOrder()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            player.Resources.GoldVoucher = 2;
            state.Decks.EventDeckGreen.Add("event_green_01");
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = InfluenceService.GetRouteSlotId("A1", 0),
                RouteId = "A1"
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetRouteSlotId("B1", 0),
                RouteId = "B1"
            });

            Resolve(state, "building_039");

            AssertPendingChoice(state, FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            Assert.That(state.PendingCardSession.OptionIds, Is.EqualTo(new[]
            {
                FacilityPendingChoiceTypes.RemoveDispatchOption,
                FacilityPendingChoiceTypes.ExploreOption
            }));
        }

        private static GameState CreateState()
        {
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Round = 4,
                MapId = StaticMapDefinitions.FourPlayerMapId
            };
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "P1" });
            state.Players.Add(new PlayerState { PlayerId = 2, Name = "P2" });
            return state;
        }

        private static void Resolve(GameState state, string facilityId)
        {
            new FacilityEntryEffectService().Resolve(
                state,
                state.FindPlayer(1),
                FacilityCardDatabase.Get(facilityId),
                0);
        }

        private static void AssertPendingChoice(GameState state, string choiceType)
        {
            Assert.That(state.PendingCardSession, Is.Not.Null);
            Assert.That(state.PendingCardSession.ChoiceType, Is.EqualTo(choiceType));
        }

        private static void AddFacility(GameState state, int playerId, string facilityId, int slotIndex)
        {
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = playerId,
                FacilityCardId = facilityId,
                CityBoardSlotIndex = slotIndex
            });
        }
    }
}
