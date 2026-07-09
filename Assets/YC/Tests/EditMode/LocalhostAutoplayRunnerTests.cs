using NUnit.Framework;
using YC.Application.DevTools;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;

namespace YC.Tests.EditMode
{
    public sealed class LocalhostAutoplayRunnerTests
    {
        [Test]
        public void RunToRound8Settlement_FillsFourSeatsAndOutputsSnapshot()
        {
            var result = LocalhostAutoplayRunner.RunToRound8Settlement();

            Assert.That(result.Succeeded, Is.True, result.Snapshot);
            Assert.That(result.Seats, Has.Count.EqualTo(4));
            Assert.That(result.FinalState.Players, Has.Count.EqualTo(4));
            Assert.That(result.FinalState.Round, Is.EqualTo(8));
            Assert.That(result.FinalState.Phase, Is.EqualTo(GamePhase.FinalScoring));
            Assert.That(result.FinalState.FinalScoring, Is.Not.Null);
            Assert.That(result.FinalState.FinalScoring.IsResolved, Is.True);
            Assert.That(result.FinalState.FinalScoring.PlayerScores, Has.Count.EqualTo(4));
            Assert.That(result.FinalState.FinalScoring.WinnerPlayerIds, Is.Not.Empty);
            Assert.That(RoundTrackRule.GetRoundIndex(result.FinalState), Is.EqualTo(RoundTrackRule.FinalIndex));
            Assert.That(result.AcceptedCommands, Is.EqualTo(result.SubmittedCommands));
            Assert.That(result.ExploreLocationAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.ExploreLocationSuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.ExploredLocationIds, Is.Not.Empty, result.Snapshot);
            Assert.That(result.MoveCityAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.MoveCitySuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.MoveCityRoutes, Is.Not.Empty, result.Snapshot);
            Assert.That(result.DispatchInfluenceAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.DispatchInfluenceSuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.DispatchInfluenceRoutes, Is.Not.Empty, result.Snapshot);
            Assert.That(result.BuildFacilityAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.BuildFacilitySuccesses, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.FormalSupplyBuilds, Is.Not.Empty, result.Snapshot);
            Assert.That(result.FormalSupplyBuilds.Exists(
                evidence => evidence.Contains("facility=" + FacilityCardDatabase.TradeDistrict)), Is.True, result.Snapshot);
            Assert.That(result.FormalSupplyBuilds.Exists(
                evidence => evidence.Contains("facility=" + FacilityCardDatabase.EquipmentWarehouse)), Is.True, result.Snapshot);
            Assert.That(result.DeclareCityStyleAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.DeclareCityStyleSuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.CityStyleDeclarations, Is.Not.Empty, result.Snapshot);
            Assert.That(result.CityStyleDeclarations.Exists(
                evidence => evidence.Contains("cityStyle=" + CityStyleDatabase.MilitaryIndustrialArea) &&
                            evidence.Contains("score=2") &&
                            evidence.Contains("slots=0,1")), Is.True, result.Snapshot);
            Assert.That(result.DeployInfluenceAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.DeployInfluenceSuccesses, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.FinalState.Map.Facilities, Has.Count.EqualTo(result.BuildFacilitySuccesses + result.Seats.Count));
            Assert.That(result.FinalState.Map.Influences.Count, Is.GreaterThanOrEqualTo(result.DeployInfluenceSuccesses));
            Assert.That(result.ResourceCollectionSubmissions, Is.GreaterThanOrEqualTo(4), result.Snapshot);
            Assert.That(result.ResourceCollectionSubmissions % result.FinalState.Players.Count, Is.EqualTo(0), result.Snapshot);
            Assert.That(result.PaidRouteCollectionAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.PaidRouteCollectionSuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.PaidRouteCollectionRoutes, Is.Not.Empty, result.Snapshot);
            Assert.That(result.OpponentRouteRecipientCollectionAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.OpponentRouteRecipientCollectionSuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.OpponentRouteRecipientCollections, Is.Not.Empty, result.Snapshot);
            Assert.That(result.Snapshot, Does.Contain("本地联机自动跑局快照"));
            Assert.That(result.Snapshot, Does.Contain("Phase: FinalScoring"));
            Assert.That(result.Snapshot, Does.Contain("CollectedPlayers: 4/4"));
            Assert.That(result.Snapshot, Does.Contain("ExploreLocationSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("ExploredLocations:"));
            Assert.That(result.Snapshot, Does.Contain("MoveCitySuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("MoveCityRoutes:"));
            Assert.That(result.Snapshot, Does.Contain("DispatchInfluenceSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("DispatchInfluenceRoutes:"));
            Assert.That(result.Snapshot, Does.Contain("BuildFacilitySuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("FormalSupplyBuilds:"));
            Assert.That(result.Snapshot, Does.Contain("DeclareCityStyleSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("CityStyleDeclarations:"));
            Assert.That(result.Snapshot, Does.Contain("BuiltFacilities:"));
            Assert.That(result.Snapshot, Does.Contain("DeployInfluenceSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("InfluencePlacements:"));
            Assert.That(result.Snapshot, Does.Contain("PaidRouteCollectionSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("PaidRouteCollections:"));
            Assert.That(result.Snapshot, Does.Contain("OpponentRouteRecipientCollectionSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("OpponentRouteRecipientCollections:"));
            Assert.That(result.Snapshot, Does.Contain("FinalScoringResolved: True"));
            Assert.That(result.Snapshot, Does.Contain("FinalScores:"));
            Assert.That(result.Snapshot, Does.Contain("facility="));
            Assert.That(result.Snapshot, Does.Contain("cityStyle="));
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("explored")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("moved city")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("dispatched influence")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("built")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("deployed influence")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("collected resources")), Is.True, result.Snapshot);
            Assert.That(HasAnyCollectedResource(result.FinalState), Is.True, result.Snapshot);
            Assert.That(HasAnyFinalFacilityScore(result.FinalState), Is.True, result.Snapshot);
            Assert.That(HasAnyFinalCityStyleScore(result.FinalState), Is.True, result.Snapshot);
            Assert.That(HasAnyFinalRegionScore(result.FinalState), Is.True, result.Snapshot);
            TestContext.WriteLine(result.Snapshot);
        }

        private static bool HasAnyFinalRegionScore(YC.Domain.State.GameState state)
        {
            for (var i = 0; i < state.FinalScoring.PlayerScores.Count; i++)
            {
                if (state.FinalScoring.PlayerScores[i].RegionScore != 0)
                {
                    return true;
                }
            }

            return false;
        }
        private static bool HasAnyFinalFacilityScore(YC.Domain.State.GameState state)
        {
            for (var i = 0; i < state.FinalScoring.PlayerScores.Count; i++)
            {
                if (state.FinalScoring.PlayerScores[i].FacilityScore != 0)
                {
                    return true;
                }
            }

            return false;
        }
        private static bool HasAnyFinalCityStyleScore(YC.Domain.State.GameState state)
        {
            for (var i = 0; i < state.FinalScoring.PlayerScores.Count; i++)
            {
                if (state.FinalScoring.PlayerScores[i].CityStyleScore != 0)
                {
                    return true;
                }
            }

            return false;
        }
        private static bool HasAnyCollectedResource(YC.Domain.State.GameState state)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                var resources = state.Players[i].Resources;
                if (resources.Originium > 0 ||
                    resources.OriginiumShard > 0 ||
                    resources.Iron > 0 ||
                    resources.PureOriginium > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}



