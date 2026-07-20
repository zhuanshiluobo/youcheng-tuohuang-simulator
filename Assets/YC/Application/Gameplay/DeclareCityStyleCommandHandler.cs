using System;
using System.Collections.Generic;
using System.Globalization;
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
        public const string UsedCityBoardSlotIndexesParameter = "usedCityBoardSlotIndexes";

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
            string selectedSlotsValue;
            var hasExplicitSelection = TryGetParameter(
                command,
                UsedCityBoardSlotIndexesParameter,
                out selectedSlotsValue);
            if (!hasExplicitSelection)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "必须明确选择用于宣告的建设槽位。"));
            }

            List<int> selectedSlotIndexes;
            if (!TryParseSelectedSlotIndexes(selectedSlotsValue, out selectedSlotIndexes))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "用于宣告的建设槽位参数无效。"));
            }

            var result = declareCityStyleService.Declare(
                state,
                command.PlayerId,
                cityStyleId,
                selectedSlotIndexes);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            var message = "Player " + command.PlayerId + " declared " + result.CityStyle.Name + ".";
            var reward = result.CityStyle.DeclarationReward ?? new ResourceSet();
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
                        { "usedCityBoardSlots", string.Join(",", result.Match.UsedCityBoardSlotIndexes.ToArray()) },
                        { "rotationDegrees", result.Match.RotationDegrees.ToString(CultureInfo.InvariantCulture) },
                        { "specialActionId", result.CityStyle.SpecialActionId ?? string.Empty },
                        { "rewardOriginium", reward.Originium.ToString(CultureInfo.InvariantCulture) },
                        { "rewardOriginiumShard", reward.OriginiumShard.ToString(CultureInfo.InvariantCulture) },
                        { "rewardIron", reward.Iron.ToString(CultureInfo.InvariantCulture) }
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
            string value;
            return TryGetParameter(command, key, out value) ? value : string.Empty;
        }

        private static bool TryGetParameter(GameCommand command, string key, out string value)
        {
            value = string.Empty;
            if (command == null || command.Parameters == null)
            {
                return false;
            }

            return command.Parameters.TryGetValue(key, out value);
        }

        private static bool TryParseSelectedSlotIndexes(string value, out List<int> selectedSlotIndexes)
        {
            selectedSlotIndexes = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split(',');
            var parsed = new List<int>(parts.Length);
            var unique = new HashSet<int>();
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                int slotIndex;
                if (part.Length == 0 ||
                    !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out slotIndex) ||
                    !unique.Add(slotIndex))
                {
                    return false;
                }

                parsed.Add(slotIndex);
            }

            selectedSlotIndexes = parsed;
            return true;
        }
    }
}
