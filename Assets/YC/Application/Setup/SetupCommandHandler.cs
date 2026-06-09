using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Setup
{
    public sealed class SetupCommandHandler : IGameCommandHandler
    {
        private static readonly HashSet<string> FourPlayerInitialLocationIds = new HashSet<string>
        {
            "G-01",
            "A-01",
            "A-02",
            "B-01",
            "B-02",
            "C-01"
        };

        private readonly IMapQueryService mapQueryService;

        public SetupCommandHandler(IMapQueryService mapQueryService)
        {
            this.mapQueryService = mapQueryService ?? throw new ArgumentNullException(nameof(mapQueryService));
        }

        public bool CanHandle(GameCommand command)
        {
            if (command == null)
            {
                return false;
            }

            return command.Kind == GameCommandKind.ChooseStartPlayer ||
                   command.Kind == GameCommandKind.ChooseInitialLocation;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            switch (command.Kind)
            {
                case GameCommandKind.ChooseStartPlayer:
                    return HandleChooseStartPlayer(state, command);
                case GameCommandKind.ChooseInitialLocation:
                    return HandleChooseInitialLocation(state, command);
                default:
                    return CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.UnknownCommand,
                        "Setup command handler cannot handle this command."));
            }
        }

        private static CommandResult HandleChooseStartPlayer(GameState state, GameCommand command)
        {
            if (state.Phase != GamePhase.Setup)
            {
                return Invalid(CommandErrorCode.WrongPhase, "Start player can only be chosen during setup.");
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return Invalid(CommandErrorCode.InvalidPlayer, "Start player must exist in the game.");
            }

            state.StartPlayerId = command.PlayerId;
            state.CurrentPlayerId = command.PlayerId;
            state.Phase = GamePhase.Entrance;

            var message = string.Format("Player {0} was chosen as the start player. Entrance phase begins.", command.PlayerId);
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                GameEvent.Log(message)
            }, message);
        }

        private CommandResult HandleChooseInitialLocation(GameState state, GameCommand command)
        {
            if (state.Phase != GamePhase.Entrance)
            {
                return Invalid(CommandErrorCode.WrongPhase, "Initial city location can only be chosen during entrance.");
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return Invalid(CommandErrorCode.InvalidPlayer, "Player must exist before choosing an initial city location.");
            }

            MapLocationDefinition location;
            try
            {
                location = mapQueryService.GetLocation(command.TargetId);
            }
            catch (ArgumentException)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Initial city location target must be a known map location.");
            }

            if (!location.CanDockCity)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Initial city location must allow city docking.");
            }

            if (!IsInitialLocationAllowed(command.TargetId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Initial city location must be one of the allowed entrance locations.");
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var otherPlayer = state.Players[i];
                if (otherPlayer.PlayerId != command.PlayerId &&
                    otherPlayer.CityLocationId == command.TargetId)
                {
                    return Invalid(CommandErrorCode.OccupiedSlot, "Initial city location is already occupied by another player's city.");
                }
            }

            player.CityLocationId = command.TargetId;
            if (!state.Map.OpenLocationIds.Contains(command.TargetId))
            {
                state.Map.OpenLocationIds.Add(command.TargetId);
            }

            if (AllPlayersHaveInitialCities(state))
            {
                if (state.StartPlayerId < 0)
                {
                    state.StartPlayerId = command.PlayerId;
                }

                state.CurrentPlayerId = state.StartPlayerId;
                state.Phase = GamePhase.ActionRound1;
                state.ActionRound = 1;
                state.Round = 1;
            }

            var message = string.Format("Player {0} placed their initial city at {1}.", command.PlayerId, command.TargetId);
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                GameEvent.Log(message)
            }, message);
        }

        private bool IsInitialLocationAllowed(string locationId)
        {
            if (mapQueryService.Map.MapId != "map-four-players")
            {
                return true;
            }

            return FourPlayerInitialLocationIds.Contains(locationId);
        }

        private static bool AllPlayersHaveInitialCities(GameState state)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                if (string.IsNullOrEmpty(state.Players[i].CityLocationId))
                {
                    return false;
                }
            }

            return state.Players.Count > 0;
        }

        private static CommandResult Invalid(CommandErrorCode errorCode, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(errorCode, reason));
        }
    }
}
