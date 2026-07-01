using System.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class FinalScoringServiceTests
    {
        [Test]
        public void Resolve_InFinalScoring_AddsRegionAndResourceScoresAndWinners()
        {
            var state = CreateFinalState();
            state.FindPlayer(1).Score = 5;
            state.FindPlayer(1).Resources.PureOriginium = 1;
            state.FindPlayer(1).Resources.Iron = 6;
            state.FindPlayer(1).Resources.OriginiumShard = 8;
            state.FindPlayer(1).Resources.Originium = 9;
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            state.FindPlayer(2).Resources.GoldVoucher = 7;
            AddLocationInfluence(state, 1, "A-01", 0);
            AddLocationInfluence(state, 1, "A-02", 0);
            AddLocationInfluence(state, 2, "B-01", 0);
            AddRouteInfluence(state, 2, "R1", 0);
            var service = CreateService();

            var result = service.Resolve(state);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FinalScoring, Is.Not.Null);
            Assert.That(state.FinalScoring.IsResolved, Is.True);
            Assert.That(GetPlayerScore(state, 1).RegionScore, Is.EqualTo(3));
            Assert.That(GetPlayerScore(state, 1).ResourceScore, Is.EqualTo(4));
            Assert.That(GetPlayerScore(state, 1).TotalScore, Is.EqualTo(12));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(12));
            Assert.That(state.FinalScoring.WinnerPlayerIds, Is.EqualTo(new[] { 1 }));
            Assert.That(GetRegionScore(state, "R").ControllerPlayerId, Is.EqualTo(2));
        }

        [Test]
        public void Resolve_SplitsBuiltFacilityScoresAndDeclaredCityStyleScores()
        {
            var state = CreateFinalState();
            var player = state.FindPlayer(1);
            player.Score = 8;
            player.BuiltFacilityIds.Add(FacilityCardDatabase.BoroughAdministrativeDistrict);
            player.BuiltFacilityIds.Add(FacilityCardDatabase.UrbanizedArea);
            player.DeclaredCityStyleIds.Add(CityStyleDatabase.SourceStoneIndustrialHub);
            player.DeclaredCityStyleIds.Add("unknown-city-style");
            var service = CreateService();

            var result = service.Resolve(state);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(GetPlayerScore(state, 1).BaseScore, Is.EqualTo(4));
            Assert.That(GetPlayerScore(state, 1).FacilityScore, Is.EqualTo(4));
            Assert.That(GetPlayerScore(state, 1).CityStyleScore, Is.EqualTo(6));
            Assert.That(GetPlayerScore(state, 1).TotalScore, Is.EqualTo(14));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(14));
        }

        [Test]
        public void Resolve_WhenTotalScoreTied_UsesGoldThenPureOriginiumTiebreakers()
        {
            var state = CreateFinalState();
            state.FindPlayer(1).Score = 10;
            state.FindPlayer(1).Resources.GoldVoucher = 3;
            state.FindPlayer(2).Score = 10;
            state.FindPlayer(2).Resources.GoldVoucher = 5;
            var service = CreateService();

            var result = service.Resolve(state);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FinalScoring.WinnerPlayerIds, Is.EqualTo(new[] { 2 }));
            Assert.That(state.FinalScoring.TiebreakSummary, Does.Contain("剩余金券"));
        }

        [Test]
        public void Resolve_WhenNotFinalScoring_FailsWithoutMutatingScores()
        {
            var state = CreateFinalState();
            state.Phase = GamePhase.Cleanup;
            state.FindPlayer(1).Score = 7;
            AddLocationInfluence(state, 1, "A-01", 0);
            var service = CreateService();

            var result = service.Resolve(state);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(state.FinalScoring, Is.Null);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(7));
        }

        [Test]
        public void EndAction_WhenCleanupEntersFinalScoring_ResolvesFinalScoresInApplicationLayer()
        {
            var state = CreateFinalState();
            state.Phase = GamePhase.Cleanup;
            state.Round = 8;
            state.MaxRounds = 8;
            state.StartPlayerId = 1;
            state.CurrentPlayerId = 1;
            state.FindPlayer(1).Score = 4;
            state.FindPlayer(1).Resources.PureOriginium = 2;
            var handler = new EndActionCommandHandler(new RoundAdvanceService(), CreateService());

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.FinalScoring));
            Assert.That(state.FinalScoring, Is.Not.Null);
            Assert.That(GetPlayerScore(state, 1).TotalScore, Is.EqualTo(6));
        }

        private static FinalScoringService CreateService()
        {
            return new FinalScoringService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
        }

        private static GameState CreateFinalState()
        {
            return new GameState
            {
                Phase = GamePhase.FinalScoring,
                Round = 8,
                MaxRounds = 8,
                UseSeatTurnOrder = true,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Red }
                }
            };
        }

        private static void AddLocationInfluence(GameState state, int playerId, string locationId, int slotIndex)
        {
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = InfluenceService.GetLocationSlotId(locationId, slotIndex),
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

        private static FinalPlayerScoreState GetPlayerScore(GameState state, int playerId)
        {
            return state.FinalScoring.PlayerScores.Single(score => score.PlayerId == playerId);
        }

        private static FinalRegionScoreState GetRegionScore(GameState state, string regionId)
        {
            return state.FinalScoring.RegionScores.Single(score => score.RegionId == regionId);
        }
    }
}
