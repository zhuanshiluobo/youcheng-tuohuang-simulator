using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Effects;

namespace YC.Domain.Scoring
{
    public sealed class FinalScoringService
    {
        private readonly RegionControlService regionControlService;

        public FinalScoringService(IMapQueryService mapQuery)
            : this(new RegionControlService(mapQuery))
        {
        }

        public FinalScoringService(RegionControlService regionControlService)
        {
            this.regionControlService = regionControlService ?? throw new ArgumentNullException(nameof(regionControlService));
        }

        public FinalScoringResult Resolve(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Phase != GamePhase.FinalScoring)
            {
                return FinalScoringResult.Failure(ValidationResult.Failure(
                    CommandErrorCode.WrongPhase,
                    "最终计分只能在 FinalScoring 阶段结算。"));
            }

            if (EffectTreeExecutor.GetDeferredResponseDependencies(state, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.FinalizeGame }).Count > 0)
                return FinalScoringResult.Failure(ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "最终计分前需要本人完成尚未回答的影响力去向。"));

            if (state.FinalScoring != null && state.FinalScoring.IsResolved)
            {
                return FinalScoringResult.Success(state.FinalScoring);
            }

            if (FinalScoringSessionService.IsActiveOwner(state) || state.FinalScoring?.Session != null ||
                RegionControlService.HasDeclaredModifiers(state) || state.ContentDefinitions?.AbilitySources.Any(d => d.ObservedEvents.Contains("FinalScoringStarted")) == true)
                return FinalScoringResult.Failure(ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "终局须由原回合节点继续结算，不能同步跳过待结算效果。"));

            var scoring = new FinalScoringState
            {
                IsResolved = true
            };

            var playerScores = new Dictionary<int, FinalPlayerScoreState>();
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                var facilityScore = CalculateFacilityScore(player, state);
                var cityStyleScore = CalculateCityStyleScore(player, state);
                var playerScore = new FinalPlayerScoreState
                {
                    PlayerId = player.PlayerId,
                    BaseScore = player.Score - facilityScore - cityStyleScore,
                    ResourceScore = CalculateResourceScore(player.Resources),
                    FacilityScore = facilityScore,
                    CityStyleScore = cityStyleScore,
                    GoldVoucherTiebreaker = player.Resources.GoldVoucher,
                    PureOriginiumTiebreaker = player.Resources.PureOriginium,
                    RemainingResources = player.Resources.Clone()
                };

                playerScores[player.PlayerId] = playerScore;
                scoring.PlayerScores.Add(playerScore);
            }

            var regions = regionControlService.Evaluate(state);
            for (var i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                var regionScore = new FinalRegionScoreState
                {
                    RegionId = region.RegionId,
                    ScoreValue = region.ScoreValue,
                    ControllerPlayerId = region.ControllerPlayerId.HasValue ? region.ControllerPlayerId.Value : -1
                };

                foreach (var pair in region.InfluenceCounts)
                {
                    regionScore.InfluenceCounts.Add(new PlayerInfluenceCountState
                    {
                        PlayerId = pair.Key,
                        Count = pair.Value
                    });
                }

                scoring.RegionScores.Add(regionScore);

                if (region.ControllerPlayerId.HasValue &&
                    playerScores.ContainsKey(region.ControllerPlayerId.Value))
                {
                    var controller = playerScores[region.ControllerPlayerId.Value];
                    controller.RegionScore += region.ScoreValue;
                    controller.ControlledRegionIds.Add(region.RegionId);
                }
            }

            for (var i = 0; i < scoring.PlayerScores.Count; i++)
            {
                var playerScore = scoring.PlayerScores[i];
                playerScore.TotalScore = playerScore.BaseScore +
                                         playerScore.RegionScore +
                                         playerScore.ResourceScore +
                                         playerScore.FacilityScore +
                                         playerScore.CityStyleScore;

                var player = state.FindPlayer(playerScore.PlayerId);
                if (player != null)
                {
                    player.Score = playerScore.TotalScore;
                }
            }

            ResolveWinners(scoring);
            state.FinalScoring = scoring;
            return FinalScoringResult.Success(scoring);
        }

        public static int CalculateResourceScore(ResourceSet resources)
        {
            if (resources == null)
            {
                return 0;
            }

            return resources.PureOriginium +
                   resources.Iron / 6 +
                   resources.OriginiumShard / 8 +
                   resources.Originium / 9;
        }

        public static int CalculateFacilityScore(PlayerState player, GameState state = null)
        {
            if (player == null || player.BuiltFacilityIds == null)
            {
                return 0;
            }

            var score = 0;
            for (var i = 0; i < player.BuiltFacilityIds.Count; i++)
            {
                FacilityCardDefinition facility;
                if (FacilityCardDatabase.TryGet(state, player.BuiltFacilityIds[i], out facility))
                {
                    score += facility.Score;
                }
            }

            return score;
        }

        public static int CalculateCityStyleScore(PlayerState player, GameState state = null)
        {
            if (player == null || player.DeclaredCityStyles == null)
            {
                return 0;
            }

            var score = 0;
            for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                var declaration = player.DeclaredCityStyles[i];
                CityStyleDefinition cityStyle;
                if (declaration != null && CityStyleDatabase.TryGet(state, declaration.CityStyleId, out cityStyle))
                {
                    score += cityStyle.Score;
                }
            }

            return score;
        }

        internal static void ResolveWinners(FinalScoringState scoring)
        {
            var candidates = new List<FinalPlayerScoreState>();
            var bestScore = int.MinValue;
            for (var i = 0; i < scoring.PlayerScores.Count; i++)
            {
                var playerScore = scoring.PlayerScores[i];
                if (playerScore.TotalScore > bestScore)
                {
                    bestScore = playerScore.TotalScore;
                    candidates.Clear();
                    candidates.Add(playerScore);
                }
                else if (playerScore.TotalScore == bestScore)
                {
                    candidates.Add(playerScore);
                }
            }

            if (candidates.Count <= 1)
            {
                AddWinners(scoring, candidates);
                scoring.TiebreakSummary = candidates.Count == 1
                    ? "最高总分直接获胜。"
                    : "没有可判定赢家。";
                return;
            }

            candidates = FilterByMax(candidates, score => score.GoldVoucherTiebreaker);
            if (candidates.Count == 1)
            {
                AddWinners(scoring, candidates);
                scoring.TiebreakSummary = "总分平局，剩余金券更多者获胜。";
                return;
            }

            candidates = FilterByMax(candidates, score => score.PureOriginiumTiebreaker);
            AddWinners(scoring, candidates);
            scoring.TiebreakSummary = candidates.Count == 1
                ? "总分和剩余金券平局，剩余至纯源石更多者获胜。"
                : "总分、剩余金券和剩余至纯源石均相同，共同胜利。";
        }

        public static int CalculateResourceScore(ResourceSet resources, PlayerResourceScoreProfile profile)
        {
            if (profile == null) return CalculateResourceScore(resources);
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            int score = 0;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in profile.Entries)
            {
                if (entry == null || entry.Units <= 0 || entry.Points <= 0 || !Enum.GetNames(typeof(ResourceType)).Contains(entry.ResourceType) || !used.Add(entry.ResourceType)) throw new InvalidOperationException("scoring_resource_profile_invalid");
                int amount = resources.Get((ResourceType)Enum.Parse(typeof(ResourceType), entry.ResourceType));
                if (amount < 0) throw new InvalidOperationException("scoring_resources_negative");
                score = checked(score + checked((amount / entry.Units) * entry.Points));
            }
            return score;
        }

        // 这里只汇总已经真实入账的三阶段结果，不再发分或资源。
        internal static void CompleteSession(GameState state, FinalScoringSessionState session)
        {
            if (state.FinalScoring == null || state.FinalScoring.Session != session || string.IsNullOrEmpty(session.RegionCommitReceipt) || string.IsNullOrEmpty(session.ResourceCommitReceipt)) throw new InvalidOperationException("scoring_completion_receipt_missing");
            var scoring = state.FinalScoring;
            if (scoring.IsResolved) throw new InvalidOperationException("scoring_completion_duplicate");
            scoring.PlayerScores.Clear(); scoring.RegionScores.Clear(); scoring.WinnerPlayerIds.Clear();
            foreach (var ledger in session.Players.OrderBy(p => p.PlayerId))
            {
                int total = checked(ledger.StartScore + ledger.SpecialScore + ledger.RegionScore + ledger.ResourceScore);
                if (state.FindPlayer(ledger.PlayerId)?.Score != total || ledger.Resources == null) throw new InvalidOperationException("scoring_total_does_not_reconcile");
                scoring.PlayerScores.Add(new FinalPlayerScoreState { PlayerId = ledger.PlayerId, BaseScore = checked(ledger.StartScore - ledger.FacilityScore - ledger.CityStyleScore),
                    SpecialScore = ledger.SpecialScore, FacilityScore = ledger.FacilityScore, CityStyleScore = ledger.CityStyleScore, RegionScore = ledger.RegionScore, ResourceScore = ledger.ResourceScore, TotalScore = total,
                    RemainingResources = ledger.Resources.Clone(), GoldVoucherTiebreaker = ledger.Resources.GoldVoucher, PureOriginiumTiebreaker = ledger.Resources.PureOriginium,
                    ControlledRegionIds = session.Control.Regions.Where(r => r.ControllerPlayerId == ledger.PlayerId).Select(r => r.RegionId).ToList() });
            }
            foreach (var region in session.Control.Regions)
                scoring.RegionScores.Add(new FinalRegionScoreState { RegionId = region.RegionId, ScoreValue = region.ScoreValue, ControllerPlayerId = region.ControllerPlayerId ?? -1,
                    InfluenceCounts = region.InfluenceCounts.OrderBy(p => p.Key).Select(p => new PlayerInfluenceCountState { PlayerId = p.Key, Count = p.Value }).ToList() });
            ResolveWinners(scoring);
            scoring.IsResolved = true;
        }

        private static void AddWinners(FinalScoringState scoring, List<FinalPlayerScoreState> winners)
        {
            for (var i = 0; i < winners.Count; i++)
            {
                scoring.WinnerPlayerIds.Add(winners[i].PlayerId);
            }
        }

        private static List<FinalPlayerScoreState> FilterByMax(
            List<FinalPlayerScoreState> candidates,
            Func<FinalPlayerScoreState, int> selector)
        {
            var best = int.MinValue;
            var result = new List<FinalPlayerScoreState>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var value = selector(candidates[i]);
                if (value > best)
                {
                    best = value;
                    result.Clear();
                    result.Add(candidates[i]);
                }
                else if (value == best)
                {
                    result.Add(candidates[i]);
                }
            }

            return result;
        }
    }

    public sealed class FinalScoringResult
    {
        private FinalScoringResult(bool succeeded, ValidationResult validation, FinalScoringState scoring)
        {
            Succeeded = succeeded;
            Validation = validation;
            Scoring = scoring;
        }

        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public FinalScoringState Scoring { get; private set; }

        public static FinalScoringResult Success(FinalScoringState scoring)
        {
            return new FinalScoringResult(true, ValidationResult.Success, scoring);
        }

        public static FinalScoringResult Failure(ValidationResult validation)
        {
            return new FinalScoringResult(false, validation, null);
        }
    }
}
