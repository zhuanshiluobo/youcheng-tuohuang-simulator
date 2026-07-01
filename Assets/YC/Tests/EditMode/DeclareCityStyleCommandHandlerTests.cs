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
        [Test]
        public void DeclareCityStyle_WhenRequirementMet_SucceedsWritesStateAndKeepsMainActionAvailable()
        {
            var state = CreateActionState();
            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 1);
            AddFacility(state, FacilityCardDatabase.TradeDistrict, 2);
            var handler = new DeclareCityStyleCommandHandler();

            var result = handler.Handle(state, CreateCommand(CityStyleDatabase.SourceStoneIndustrialHub));

            Assert.That(result.Succeeded, Is.True);
            var player = state.FindPlayer(1);
            Assert.That(player.DeclaredCityStyleIds, Is.EqualTo(new[] { CityStyleDatabase.SourceStoneIndustrialHub }));
            Assert.That(player.DeclaredCityStyles, Has.Count.EqualTo(1));
            Assert.That(player.DeclaredCityStyles[0].UsedCityBoardSlotIndexes, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(player.InfluenceSupply, Is.EqualTo(29));
            Assert.That(player.ActedMainActionThisTurn, Is.False);
        }

        [Test]
        public void DeclareCityStyle_WhenRequirementMissing_FailsWithoutMutatingState()
        {
            var state = CreateActionState();
            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 1);
            var handler = new DeclareCityStyleCommandHandler();

            var result = handler.Handle(state, CreateCommand(CityStyleDatabase.SourceStoneIndustrialHub));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            var player = state.FindPlayer(1);
            Assert.That(player.DeclaredCityStyleIds, Is.Empty);
            Assert.That(player.DeclaredCityStyles, Is.Empty);
            Assert.That(player.InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void DeclareCityStyle_WhenAlreadyDeclared_FailsWithoutConsumingInfluence()
        {
            var state = CreateActionState();
            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 1);
            AddFacility(state, FacilityCardDatabase.TradeDistrict, 2);
            var handler = new DeclareCityStyleCommandHandler();

            var first = handler.Handle(state, CreateCommand(CityStyleDatabase.SourceStoneIndustrialHub));
            var second = handler.Handle(state, CreateCommand(CityStyleDatabase.SourceStoneIndustrialHub));

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
            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 1);
            AddFacility(state, FacilityCardDatabase.TradeDistrict, 2);
            var declareResult = new DeclareCityStyleCommandHandler().Handle(
                state,
                CreateCommand(CityStyleDatabase.SourceStoneIndustrialHub));
            Assert.That(declareResult.Succeeded, Is.True);
            state.Phase = GamePhase.FinalScoring;
            state.Round = 8;
            state.MaxRounds = 8;
            var scoring = new FinalScoringService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));

            var result = scoring.Resolve(state);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FinalScoring.PlayerScores[0].CityStyleScore, Is.EqualTo(6));
            Assert.That(state.FinalScoring.PlayerScores[0].TotalScore, Is.EqualTo(6));
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
    }
}
