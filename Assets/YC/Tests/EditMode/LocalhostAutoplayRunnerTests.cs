using NUnit.Framework;
using YC.Application.DevTools;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;

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
            Assert.That(result.FinalState.PendingChoice, Is.Null, result.Snapshot);
            Assert.That(result.FinalState.PendingCardSession, Is.Null, result.Snapshot);
            Assert.That(result.FinalState.PendingSpecialAction, Is.Null, result.Snapshot);
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
            Assert.That(result.CharacterCardUseAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.CharacterCardUseSuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.CharacterCardExecutions, Is.Not.Empty, result.Snapshot);
            Assert.That(result.CharacterCardExecutions.Exists(
                evidence => evidence.Contains("card=") &&
                            evidence.Contains("mode=" + CharacterEffectModes.Strategy) &&
                            evidence.Contains("discardCount=1")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(
                log => log.CommandId.StartsWith("autoplay-use-character-")), Is.True, result.Snapshot);
            Assert.That(result.DeclareCityStyleAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.DeclareCityStyleSuccesses, Is.GreaterThanOrEqualTo(1), result.Snapshot);
            Assert.That(result.CityStyleDeclarations, Is.Not.Empty, result.Snapshot);
            Assert.That(result.CityStyleDeclarations.Exists(
                evidence => evidence.Contains("cityStyle=" + CityStyleDatabase.MilitaryIndustrialArea) &&
                            evidence.Contains("score=2") &&
                            evidence.Contains("slots=0,1")), Is.True, result.Snapshot);
            Assert.That(result.CityStyleDeclarations.Exists(
                evidence => evidence.Contains("cityStyle=" + CityStyleDatabase.SourceStoneIndustrialHub) &&
                            evidence.Contains("score=6") &&
                            evidence.Contains("slots=0,3,4,6,7,8")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.FindPlayer(4).DeclaredCityStyleIds,
                Does.Contain(CityStyleDatabase.MilitaryIndustrialArea), result.Snapshot);
            Assert.That(result.FinalState.FindPlayer(3).DeclaredCityStyleIds,
                Does.Contain(CityStyleDatabase.SourceStoneIndustrialHub), result.Snapshot);
            var levelOneDeclaration = result.FinalState.FindPlayer(4).DeclaredCityStyles.Find(
                declaration => declaration.CityStyleId == CityStyleDatabase.MilitaryIndustrialArea);
            var levelTwoDeclaration = result.FinalState.FindPlayer(3).DeclaredCityStyles.Find(
                declaration => declaration.CityStyleId == CityStyleDatabase.SourceStoneIndustrialHub);
            Assert.That(levelOneDeclaration, Is.Not.Null, result.Snapshot);
            Assert.That(levelOneDeclaration.MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Unused), result.Snapshot);
            Assert.That(levelOneDeclaration.RemainingSpecialActionUses, Is.EqualTo(1), result.Snapshot);
            Assert.That(levelTwoDeclaration, Is.Not.Null, result.Snapshot);
            Assert.That(levelTwoDeclaration.MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesOne), result.Snapshot);
            Assert.That(levelTwoDeclaration.RemainingSpecialActionUses, Is.EqualTo(1), result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(
                log => log.CommandId.StartsWith("autoplay-declare-city-style-") && log.PlayerId == 4),
                Is.True,
                result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(
                log => log.CommandId.StartsWith("autoplay-declare-city-style-") && log.PlayerId == 3),
                Is.True,
                result.Snapshot);
            Assert.That(result.SeededSpecialActionFacilityCount, Is.EqualTo(5), result.Snapshot);
            Assert.That(result.SpecialActionFixtures, Has.Count.EqualTo(6), result.Snapshot);
            Assert.That(result.SpecialActionAttempts, Is.GreaterThanOrEqualTo(2), result.Snapshot);
            Assert.That(result.SpecialActionSuccesses, Is.GreaterThanOrEqualTo(2), result.Snapshot);
            Assert.That(result.SpecialActionExecutions.Exists(
                evidence => evidence.Contains("action=" + SpecialActionDatabase.MilitaryIndustrialArea) &&
                            evidence.Contains("area=") &&
                            evidence.Contains("remainingMainActions=") &&
                            evidence.Contains("pendingStep=None") &&
                            evidence.Contains("beginCommand=autoplay-use-special-")), Is.True, result.Snapshot);
            Assert.That(result.SpecialActionExecutions.Exists(
                evidence => evidence.Contains("action=" + SpecialActionDatabase.SourceStoneIndustrialHub) &&
                            evidence.Contains("area=") &&
                            evidence.Contains("remainingMainActions=2") &&
                            evidence.Contains("pendingStep=None") &&
                            evidence.Contains("beginCommand=autoplay-use-special-")), Is.True, result.Snapshot);
            Assert.That(result.SpecialActionPendingSteps.Exists(
                evidence => evidence.Contains("action=" + SpecialActionDatabase.MilitaryIndustrialArea) &&
                            evidence.Contains("step=" + SpecialActionPendingSteps.AwaitMilitaryTargets)),
                Is.True,
                result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(
                log => log.CommandId.StartsWith("autoplay-use-special-") && log.PlayerId == 3),
                Is.True,
                result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(
                log => log.CommandId.StartsWith("autoplay-resolve-special-") && log.PlayerId == 4),
                Is.True,
                result.Snapshot);
            Assert.That(result.DeployInfluenceAttempts, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.DeployInfluenceSuccesses, Is.GreaterThan(0), result.Snapshot);
            Assert.That(result.FinalState.Map.Facilities,
                Has.Count.GreaterThanOrEqualTo(
                    result.BuildFacilitySuccesses +
                    result.Seats.Count +
                    result.SeededSpecialActionFacilityCount));
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
            Assert.That(result.Snapshot, Does.Contain("CharacterCardUseAttempts:"));
            Assert.That(result.Snapshot, Does.Contain("CharacterCardUseSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("CharacterCardExecutions:"));
            Assert.That(result.Snapshot, Does.Contain("DeclareCityStyleSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("CityStyleDeclarations:"));
            Assert.That(result.Snapshot, Does.Contain("SpecialActionAttempts:"));
            Assert.That(result.Snapshot, Does.Contain("SpecialActionSuccesses:"));
            Assert.That(result.Snapshot, Does.Contain("SpecialActionFixtures:"));
            Assert.That(result.Snapshot, Does.Contain("SpecialActionPendingSteps:"));
            Assert.That(result.Snapshot, Does.Contain("SpecialActionExecutions:"));
            Assert.That(result.Snapshot, Does.Contain("PendingSpecialActionStep: None"));
            Assert.That(result.Snapshot, Does.Contain("SpecialActionMarkers:"));
            Assert.That(result.Snapshot, Does.Contain("action=" + SpecialActionDatabase.MilitaryIndustrialArea));
            Assert.That(result.Snapshot, Does.Contain("action=" + SpecialActionDatabase.SourceStoneIndustrialHub));
            Assert.That(result.Snapshot, Does.Contain("area="));
            Assert.That(result.Snapshot, Does.Contain("remainingMainActions="));
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
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("\u63a2\u7d22\u4e86\u5730\u70b9")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("\u5c06\u57ce\u5e02\u79fb\u52a8\u81f3\u5730\u70b9")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("\u8c03\u5ea6\u4e86")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("\u5efa\u9020\u4e86\u5efa\u7b51")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(
                log => log.CommandId.StartsWith("autoplay-resolve-facility-")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("\u90e8\u7f72\u4e86")), Is.True, result.Snapshot);
            Assert.That(result.FinalState.Logs.Exists(log => log.Message.Contains("\u6536\u96c6\u4e86") || log.Message.Contains("\u8d44\u6e90\u6536\u96c6")), Is.True, result.Snapshot);
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



