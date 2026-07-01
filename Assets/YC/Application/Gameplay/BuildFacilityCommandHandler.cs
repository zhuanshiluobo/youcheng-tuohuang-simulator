using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class BuildFacilityCommandHandler : IGameCommandHandler
    {
        public const string FacilityIdParameter = "facilityId";
        public const string CityBoardSlotIndexParameter = "cityBoardSlotIndex";
        public const string PaymentModeParameter = "paymentMode";

        private readonly BuildFacilityService buildFacilityService;
        private readonly RoundAdvanceService roundAdvanceService;

        public BuildFacilityCommandHandler()
            : this(new BuildFacilityService(), new RoundAdvanceService())
        {
        }

        public BuildFacilityCommandHandler(BuildFacilityService buildFacilityService, RoundAdvanceService roundAdvanceService)
        {
            this.buildFacilityService = buildFacilityService;
            this.roundAdvanceService = roundAdvanceService;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.BuildFacility;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var guard = MainActionCommandGuard.Validate(state, command);
            if (!guard.IsValid)
            {
                return CommandResult.Invalid(guard);
            }

            var facilityId = GetFacilityId(command);
            int cityBoardSlotIndex;
            var slotValidation = ResolveCityBoardSlotIndex(state, command, out cityBoardSlotIndex);
            if (!slotValidation.IsValid)
            {
                return CommandResult.Invalid(slotValidation);
            }

            var result = buildFacilityService.Build(
                state,
                command.PlayerId,
                facilityId,
                cityBoardSlotIndex,
                GetParameter(command, PaymentModeParameter));
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);

            var message = "Player " + command.PlayerId + " built " + result.Facility.Name + ".";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.FacilityBuilt,
                    PlayerId = command.PlayerId,
                    SubjectId = result.Facility.FacilityId,
                    Message = message,
                    Data =
                    {
                        { "facilityName", result.Facility.Name },
                        { "cityBoardSlotIndex", result.CityBoardSlotIndex.ToString() },
                        { "paymentMode", result.PaymentMode },
                        { "score", result.Facility.Score.ToString() },
                        { "effectType", result.Facility.EffectType }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.Facility.FacilityId,
                    Message = "Build facility paid cost and applied on-build reward."
                },
                new GameEvent
                {
                    Kind = GameEventKind.ScoreChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.Facility.FacilityId,
                    Message = "Build facility changed score."
                }
            }, message);
        }

        private static ValidationResult ResolveCityBoardSlotIndex(
            GameState state,
            GameCommand command,
            out int cityBoardSlotIndex)
        {
            cityBoardSlotIndex = -1;
            var encoded = GetParameter(command, CityBoardSlotIndexParameter);
            if (string.IsNullOrEmpty(encoded))
            {
                cityBoardSlotIndex = BuildFacilityService.FindFirstEmptyCityBoardSlot(state, command.PlayerId);
                return cityBoardSlotIndex >= 0
                    ? ValidationResult.Success
                    : ValidationResult.Failure(CommandErrorCode.OccupiedSlot, "城市面板没有空槽位。");
            }

            if (!int.TryParse(encoded, out cityBoardSlotIndex))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "城市面板槽位必须是数字。");
            }

            return ValidationResult.Success;
        }

        private static string GetFacilityId(GameCommand command)
        {
            var facilityId = GetParameter(command, FacilityIdParameter);
            return string.IsNullOrEmpty(facilityId) ? command.TargetId : facilityId;
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
