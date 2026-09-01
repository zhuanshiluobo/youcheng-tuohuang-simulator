using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.CityStyles
{
    public sealed class DeclareCityStyleService
    {
        private readonly CityStylePatternMatcher patternMatcher;

        public DeclareCityStyleService()
            : this(new CityStylePatternMatcher())
        {
        }

        public DeclareCityStyleService(CityStylePatternMatcher patternMatcher)
        {
            this.patternMatcher = patternMatcher ?? throw new ArgumentNullException(nameof(patternMatcher));
        }

        public DeclareCityStyleResult Declare(GameState state, int playerId, string cityStyleId)
        {
            return DeclareInternal(state, playerId, cityStyleId, null, false);
        }

        public DeclareCityStyleResult Declare(
            GameState state,
            int playerId,
            string cityStyleId,
            IEnumerable<int> selectedSlotIndexes)
        {
            var selectedSlots = selectedSlotIndexes == null
                ? null
                : new List<int>(selectedSlotIndexes);
            return DeclareInternal(state, playerId, cityStyleId, selectedSlots, true);
        }

        private DeclareCityStyleResult DeclareInternal(
            GameState state,
            int playerId,
            string cityStyleId,
            List<int> selectedSlotIndexes,
            bool useExplicitSelection)
        {
            var validation = ValidateInternal(
                state,
                playerId,
                cityStyleId,
                selectedSlotIndexes,
                useExplicitSelection);
            if (!validation.IsValid)
            {
                return DeclareCityStyleResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            var cityStyle = CityStyleDatabase.Get(cityStyleId);
            var match = useExplicitSelection
                ? patternMatcher.MatchSelected(state, playerId, cityStyle, selectedSlotIndexes)
                : patternMatcher.Match(state, playerId, cityStyle);
            if (!match.Succeeded)
            {
                return DeclareCityStyleResult.Failure(match.Validation);
            }

            var previousDeclarationCount = CountDeclarations(player, cityStyle.CityStyleId);
            var declaration = CreateDeclarationState(
                cityStyle,
                playerId,
                previousDeclarationCount);
            declaration.UsedFacilityIds.AddRange(match.UsedFacilityIds);
            declaration.UsedCityBoardSlotIndexes.AddRange(match.UsedCityBoardSlotIndexes);

            var settledResources = player.Resources == null
                ? new ResourceSet()
                : player.Resources.Clone();
            settledResources.Add(cityStyle.DeclarationReward ?? new ResourceSet());
            var settledDeclarations = player.DeclaredCityStyles == null
                ? new List<CityStyleDeclarationState>()
                : new List<CityStyleDeclarationState>(player.DeclaredCityStyles);
            settledDeclarations.Add(declaration);

            player.InfluenceSupply -= 1;
            player.Score += cityStyle.Score;
            player.Resources = settledResources;
            player.DeclaredCityStyles = settledDeclarations;

            return DeclareCityStyleResult.Success(cityStyle, match);
        }

        private static CityStyleDeclarationState CreateDeclarationState(
            CityStyleDefinition cityStyle,
            int playerId,
            int previousDeclarationCount)
        {
            var markerArea = CityStyleMarkerAreas.Declared;
            var remainingSpecialActionUses = 0;
            if (previousDeclarationCount == 0 && cityStyle.Level >= 2)
            {
                markerArea = CityStyleMarkerAreas.UsesTwo;
                remainingSpecialActionUses = 2;
            }
            else if (previousDeclarationCount == 0 && !string.IsNullOrEmpty(cityStyle.SpecialActionId))
            {
                markerArea = CityStyleMarkerAreas.Unused;
                remainingSpecialActionUses = 1;
            }

            return new CityStyleDeclarationState
            {
                InfluenceMarkerId = cityStyle.CityStyleId + ":" + playerId + ":" + (previousDeclarationCount + 1),
                CityStyleId = cityStyle.CityStyleId,
                MarkerArea = markerArea,
                UnlockedSpecialActionId = previousDeclarationCount == 0
                    ? cityStyle.SpecialActionId ?? string.Empty
                    : string.Empty,
                RemainingSpecialActionUses = remainingSpecialActionUses
            };
        }

        public ValidationResult Validate(GameState state, int playerId, string cityStyleId)
        {
            return ValidateInternal(state, playerId, cityStyleId, null, false);
        }

        public ValidationResult Validate(
            GameState state,
            int playerId,
            string cityStyleId,
            IEnumerable<int> selectedSlotIndexes)
        {
            var selectedSlots = selectedSlotIndexes == null
                ? null
                : new List<int>(selectedSlotIndexes);
            return ValidateInternal(state, playerId, cityStyleId, selectedSlots, true);
        }

        private ValidationResult ValidateInternal(
            GameState state,
            int playerId,
            string cityStyleId,
            List<int> selectedSlotIndexes,
            bool useExplicitSelection)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "宣告玩家不存在。");
            }

            CityStyleDefinition cityStyle;
            if (!CityStyleDatabase.TryGet(cityStyleId, out cityStyle))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "未知城市样式。");
            }

            var declaredCount = CountDeclarations(player, cityStyle.CityStyleId);

            if (declaredCount >= cityStyle.MaxDeclarationsPerPlayer)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该城市样式已经达到宣告次数上限。");
            }

            if (player.InfluenceSupply <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientInfluence, "玩家供应堆没有可用影响力。");
            }

            var match = useExplicitSelection
                ? patternMatcher.MatchSelected(state, playerId, cityStyle, selectedSlotIndexes)
                : patternMatcher.Match(state, playerId, cityStyle);
            return match.Succeeded ? ValidationResult.Success : match.Validation;
        }

        private static int CountDeclarations(PlayerState player, string cityStyleId)
        {
            if (player == null)
            {
                return 0;
            }

            var count = 0;
            if (player.DeclaredCityStyles != null)
            {
                for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
                {
                    var declaration = player.DeclaredCityStyles[i];
                    if (declaration != null && declaration.CityStyleId == cityStyleId)
                    {
                        count += 1;
                    }
                }
            }

            return count;
        }
    }
}
