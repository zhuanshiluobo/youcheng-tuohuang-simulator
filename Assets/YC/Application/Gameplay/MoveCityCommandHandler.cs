using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class MoveCityCommandHandler : IGameCommandHandler
    {
        private readonly CityMovementService cityMovementService;
        private readonly RoundAdvanceService roundAdvanceService;

        public MoveCityCommandHandler(CityMovementService cityMovementService)
            : this(cityMovementService, new RoundAdvanceService())
        {
        }

        public MoveCityCommandHandler(CityMovementService cityMovementService, RoundAdvanceService roundAdvanceService)
        {
            this.cityMovementService = cityMovementService;
            this.roundAdvanceService = roundAdvanceService;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.MoveCity;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var result = cityMovementService.MoveCity(state, command.PlayerId, command.TargetId);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);

            return CommandResult.SuccessResult(
                new List<GameEvent>
                {
                    new GameEvent
                    {
                        Kind = GameEventKind.CityMoved,
                        PlayerId = command.PlayerId,
                        Message = "City moved from " + result.SourceLocationId + " to " + result.TargetLocationId + "."
                    },
                    new GameEvent
                    {
                        Kind = GameEventKind.InfluencePlaced,
                        PlayerId = command.PlayerId,
                        Message = "City movement attempted source influence placement."
                    }
                },
                "Player " + command.PlayerId + " moved city to " + result.TargetLocationId + ".");
        }
    }
}
