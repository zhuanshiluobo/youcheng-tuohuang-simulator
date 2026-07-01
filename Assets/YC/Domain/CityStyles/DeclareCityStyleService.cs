using System;
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
            var validation = Validate(state, playerId, cityStyleId);
            if (!validation.IsValid)
            {
                return DeclareCityStyleResult.Failure(validation);
            }

            var player = state.FindPlayer(playerId);
            var cityStyle = CityStyleDatabase.Get(cityStyleId);
            var match = patternMatcher.Match(state, playerId, cityStyle);
            if (!match.Succeeded)
            {
                return DeclareCityStyleResult.Failure(match.Validation);
            }

            player.InfluenceSupply -= 1;
            player.DeclaredCityStyleIds.Add(cityStyle.CityStyleId);
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                CityStyleId = cityStyle.CityStyleId
            });

            var declaration = player.DeclaredCityStyles[player.DeclaredCityStyles.Count - 1];
            declaration.UsedFacilityIds.AddRange(match.UsedFacilityIds);
            declaration.UsedCityBoardSlotIndexes.AddRange(match.UsedCityBoardSlotIndexes);

            return DeclareCityStyleResult.Success(cityStyle, match);
        }

        public ValidationResult Validate(GameState state, int playerId, string cityStyleId)
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

            if (player.DeclaredCityStyleIds.Contains(cityStyle.CityStyleId))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "该城市样式已经宣告过。");
            }

            if (player.InfluenceSupply <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InsufficientInfluence, "玩家供应堆没有可用影响力。");
            }

            var match = patternMatcher.Match(state, playerId, cityStyle);
            return match.Succeeded ? ValidationResult.Success : match.Validation;
        }
    }
}
