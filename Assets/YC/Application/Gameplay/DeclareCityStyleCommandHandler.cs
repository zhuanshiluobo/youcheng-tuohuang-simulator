using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class DeclareCityStyleCommandHandler : IGameCommandHandler
    {
        public const string CityStyleIdParameter = "cityStyleId";

        private readonly DeclareCityStyleService declareCityStyleService;

        public DeclareCityStyleCommandHandler()
            : this(new DeclareCityStyleService())
        {
        }

        public DeclareCityStyleCommandHandler(DeclareCityStyleService declareCityStyleService)
        {
            this.declareCityStyleService = declareCityStyleService;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.DeclareCityStyle;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var guard = QuickActionCommandGuard.Validate(state, command);
            if (!guard.IsValid)
            {
                return CommandResult.Invalid(guard);
            }

            var cityStyleId = GetCityStyleId(command);
            var result = declareCityStyleService.Declare(state, command.PlayerId, cityStyleId);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            var message = "Player " + command.PlayerId + " declared " + result.CityStyle.Name + ".";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ScoreChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.CityStyle.CityStyleId,
                    Message = message,
                    Data =
                    {
                        { "cityStyleName", result.CityStyle.Name },
                        { "score", result.CityStyle.Score.ToString() },
                        { "usedCityBoardSlots", string.Join(",", result.Match.UsedCityBoardSlotIndexes.ToArray()) }
                    }
                }
            }, message);
        }

        private static string GetCityStyleId(GameCommand command)
        {
            var cityStyleId = GetParameter(command, CityStyleIdParameter);
            return string.IsNullOrEmpty(cityStyleId) ? command.TargetId : cityStyleId;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command.Parameters == null)
            {
                return string.Empty;
            }

            string value;
            return command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }
    }
}
