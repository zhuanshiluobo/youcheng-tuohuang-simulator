using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class DeclareCityStyleCommandHandlerTests
    {
        private const string YellowSourceStoneRefinery = "building_028";
        private const string RedIronRefinery = "building_032";

        [TestCase(CityStyleDatabase.MilitaryIndustrialArea, 2, 2)]
        [TestCase(CityStyleDatabase.MobilizationSupportSystem, 3, 3)]
        [TestCase(CityStyleDatabase.CompositePowerSystem, 3, 3)]
        [TestCase(CityStyleDatabase.MaterialRelayStation, 2, 2)]
        [TestCase(CityStyleDatabase.SourceStoneIndustrialHub, 6, 6)]
        [TestCase(CityStyleDatabase.EfficientMobileManagementSystem, 7, 6)]
        public void DeclareCityStyle_WhenManifestPatternMet_SucceedsWritesStateAndKeepsMainActionAvailable(
            string cityStyleId,
            int expectedScore,
            int expectedUsedSlotCount)
        {
            var state = CreateActionState();
            AddFacilitiesForStyle(state, cityStyleId);
            var handler = new DeclareCityStyleCommandHandler();

            var result = handler.Handle(state, CreateCommand(cityStyleId));

            Assert.That(result.Succeeded, Is.True);
            var player = state.FindPlayer(1);
            Assert.That(player.DeclaredCityStyleIds, Is.EqualTo(new[] { cityStyleId }));
            Assert.That(player.DeclaredCityStyles, Has.Count.EqualTo(1));
            Assert.That(player.DeclaredCityStyles[0].UsedCityBoardSlotIndexes, Has.Count.EqualTo(expectedUsedSlotCount));
            Assert.That(CityStyleDatabase.Get(cityStyleId).Score, Is.EqualTo(expectedScore));
            Assert.That(player.Score, Is.EqualTo(expectedScore));
            Assert.That(player.InfluenceSupply, Is.EqualTo(29));
            Assert.That(player.ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void DeclareCityStyle_WhenRequirementMissing_FailsWithoutMutatingState()
        {
            var state = CreateActionState();
            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 2);
            var handler = new DeclareCityStyleCommandHandler();

            var result = handler.Handle(state, CreateCommand(CityStyleDatabase.MilitaryIndustrialArea));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            var player = state.FindPlayer(1);
            Assert.That(player.DeclaredCityStyleIds, Is.Empty);
            Assert.That(player.DeclaredCityStyles, Is.Empty);
            Assert.That(player.Score, Is.Zero);
            Assert.That(player.InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void DeclareCityStyle_WhenRepeatableStyleHasAnotherPattern_SucceedsAgain()
        {
            var state = CreateActionState();
            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 1);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 3);
            AddFacility(state, RedIronRefinery, 4);
            var handler = new DeclareCityStyleCommandHandler();

            var first = handler.Handle(state, CreateCommand(CityStyleDatabase.MilitaryIndustrialArea));
            var second = handler.Handle(state, CreateCommand(CityStyleDatabase.MilitaryIndustrialArea));

            Assert.That(first.Succeeded, Is.True);
            Assert.That(second.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).DeclaredCityStyleIds, Has.Count.EqualTo(2));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(28));
        }

        [Test]
        public void DeclareCityStyle_WhenOneTimeStyleAlreadyDeclared_FailsWithoutConsumingInfluence()
        {
            var state = CreateActionState();
            AddFacility(state, FacilityCardDatabase.IronRefinery, 0);
            AddFacility(state, YellowSourceStoneRefinery, 1);
            AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 3);
            AddFacility(state, FacilityCardDatabase.OriginiumPurificationPlant, 4);
            var handler = new DeclareCityStyleCommandHandler();

            var first = handler.Handle(state, CreateCommand(CityStyleDatabase.MaterialRelayStation));
            var second = handler.Handle(state, CreateCommand(CityStyleDatabase.MaterialRelayStation));

            Assert.That(first.Succeeded, Is.True);
            Assert.That(second.Succeeded, Is.False);
            Assert.That(second.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).DeclaredCityStyleIds, Has.Count.EqualTo(1));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void FinalScoring_AfterDeclaredCityStyle_AddsCityStyleScore()
        {
            var state = CreateActionState();
            AddFacilitiesForStyle(state, CityStyleDatabase.EfficientMobileManagementSystem);
            var declareResult = new DeclareCityStyleCommandHandler().Handle(
                state,
                CreateCommand(CityStyleDatabase.EfficientMobileManagementSystem));
            Assert.That(declareResult.Succeeded, Is.True);
            state.Phase = GamePhase.FinalScoring;
            state.Round = 8;
            state.MaxRounds = 8;
            var scoring = new FinalScoringService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            var result = scoring.Resolve(state);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FinalScoring.PlayerScores[0].CityStyleScore, Is.EqualTo(7));
            Assert.That(state.FinalScoring.PlayerScores[0].TotalScore, Is.EqualTo(7));
        }

        [Test]
        public void CityStyleDatabase_DefaultSupplyUsesSixManifestCardsAndScores()
        {
            Assert.That(CityStyleDatabase.DefaultSupplyIds, Is.EqualTo(new[]
            {
                CityStyleDatabase.MilitaryIndustrialArea,
                CityStyleDatabase.MobilizationSupportSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleDatabase.MaterialRelayStation,
                CityStyleDatabase.SourceStoneIndustrialHub,
                CityStyleDatabase.EfficientMobileManagementSystem
            }));

            Assert.That(CityStyleDatabase.Get(CityStyleDatabase.MilitaryIndustrialArea).Score, Is.EqualTo(2));
            Assert.That(CityStyleDatabase.Get(CityStyleDatabase.MobilizationSupportSystem).Score, Is.EqualTo(3));
            Assert.That(CityStyleDatabase.Get(CityStyleDatabase.CompositePowerSystem).Score, Is.EqualTo(3));
            Assert.That(CityStyleDatabase.Get(CityStyleDatabase.MaterialRelayStation).Score, Is.EqualTo(2));
            Assert.That(CityStyleDatabase.Get(CityStyleDatabase.SourceStoneIndustrialHub).Score, Is.EqualTo(6));
            Assert.That(CityStyleDatabase.Get(CityStyleDatabase.EfficientMobileManagementSystem).Score, Is.EqualTo(7));
        }

        private static GameCommand CreateCommand(string cityStyleId)
        {
            return new GameCommand
            {
                Kind = GameCommandKind.DeclareCityStyle,
                PlayerId = 1,
                TargetId = cityStyleId
            };
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
                        Color = PlayerColor.Blue
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Red
                    }
                }
            };
        }

        private static void AddFacility(GameState state, string facilityId, int slotIndex)
        {
            state.FindPlayer(1).BuiltFacilityIds.Add(facilityId);
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = facilityId,
                CityBoardSlotIndex = slotIndex
            });
        }

        private static void AddFacilitiesForStyle(GameState state, string cityStyleId)
        {
            switch (cityStyleId)
            {
                case CityStyleDatabase.MilitaryIndustrialArea:
                    AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
                    AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 1);
                    break;
                case CityStyleDatabase.MobilizationSupportSystem:
                    AddFacility(state, YellowSourceStoneRefinery, 0);
                    AddFacility(state, FacilityCardDatabase.OriginiumPurificationPlant, 1);
                    AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 2);
                    break;
                case CityStyleDatabase.CompositePowerSystem:
                    AddFacility(state, YellowSourceStoneRefinery, 0);
                    AddFacility(state, RedIronRefinery, 3);
                    AddFacility(state, FacilityCardDatabase.OriginiumPurificationPlant, 4);
                    break;
                case CityStyleDatabase.MaterialRelayStation:
                    AddFacility(state, FacilityCardDatabase.IronRefinery, 0);
                    AddFacility(state, YellowSourceStoneRefinery, 1);
                    break;
                case CityStyleDatabase.SourceStoneIndustrialHub:
                    AddFacility(state, FacilityCardDatabase.TradeDistrict, 0);
                    AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 3);
                    AddFacility(state, FacilityCardDatabase.UrbanizedArea, 4);
                    AddFacility(state, YellowSourceStoneRefinery, 6);
                    AddFacility(state, FacilityCardDatabase.OriginiumPurificationPlant, 7);
                    AddFacility(state, RedIronRefinery, 8);
                    break;
                case CityStyleDatabase.EfficientMobileManagementSystem:
                    AddFacility(state, YellowSourceStoneRefinery, 0);
                    AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 3);
                    AddFacility(state, FacilityCardDatabase.OriginiumPurificationPlant, 4);
                    AddFacility(state, FacilityCardDatabase.UrbanizedArea, 6);
                    AddFacility(state, FacilityCardDatabase.TradeDistrict, 7);
                    AddFacility(state, RedIronRefinery, 8);
                    break;
            }
        }
    }
}
