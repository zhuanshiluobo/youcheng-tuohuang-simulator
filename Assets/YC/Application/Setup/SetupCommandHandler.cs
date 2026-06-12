using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Setup
{
    public sealed class SetupCommandHandler : IGameCommandHandler
    {
        private const string EntranceEventChoiceType = "entrance_event";

        private readonly IMapQueryService mapQueryService;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;
        private readonly TurnOrderService turnOrderService;

        public SetupCommandHandler(IMapQueryService mapQueryService)
            : this(mapQueryService, new EventDeckService(), new ResourceTokenService(), new TurnOrderService())
        {
        }

        public SetupCommandHandler(
            IMapQueryService mapQueryService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService,
            TurnOrderService turnOrderService)
        {
            this.mapQueryService = mapQueryService ?? throw new ArgumentNullException(nameof(mapQueryService));
            this.eventDeckService = eventDeckService ?? throw new ArgumentNullException(nameof(eventDeckService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
            this.turnOrderService = turnOrderService ?? throw new ArgumentNullException(nameof(turnOrderService));
        }

        public bool CanHandle(GameCommand command)
        {
            if (command == null)
            {
                return false;
            }

            return command.Kind == GameCommandKind.ChooseStartPlayer ||
                   command.Kind == GameCommandKind.ChooseInitialLocation ||
                   command.Kind == GameCommandKind.ResolveEntranceEvent;
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
                case GameCommandKind.ResolveEntranceEvent:
                    return HandleResolveEntranceEvent(state, command);
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

            if (state.HasPendingChoice())
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "Resolve the pending entrance event before choosing another initial city location.");
            }

            if (state.CurrentPlayerId >= 0 && state.CurrentPlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "Initial city locations must be chosen in entrance turn order.");
            }

            if (!string.IsNullOrEmpty(player.CityLocationId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Player already has an initial city location.");
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

            var message = string.Format("Player {0} placed their initial city at {1}.", command.PlayerId, command.TargetId);
            var events = new List<GameEvent>
            {
                GameEvent.Log(message)
            };

            if (TryOpenEntranceEventChoice(state, command, events))
            {
                return CommandResult.SuccessResult(events, message);
            }

            CompleteEntranceStep(state, command.PlayerId);
            return CommandResult.SuccessResult(events, message);
        }

        private CommandResult HandleResolveEntranceEvent(GameState state, GameCommand command)
        {
            if (state.Phase != GamePhase.Entrance)
            {
                return Invalid(CommandErrorCode.WrongPhase, "Entrance events can only be resolved during entrance.");
            }

            var player = state.FindPlayer(command.PlayerId);
            if (player == null)
            {
                return Invalid(CommandErrorCode.InvalidPlayer, "Player must exist before resolving an entrance event.");
            }

            var pendingChoice = state.PendingChoice;
            if (pendingChoice == null || pendingChoice.ChoiceType != EntranceEventChoiceType)
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "There is no pending entrance event to resolve.");
            }

            if (pendingChoice.PlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "Only the player with the pending entrance event can resolve it.");
            }

            var selectedOptionId = GetSelectedOptionId(command);
            var choiceIndex = ParseChoiceIndex(selectedOptionId);
            if (choiceIndex < 0)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Entrance event option must be a valid option id.");
            }

            var card = EventCardDatabase.Get(pendingChoice.CardId);
            if (card == null)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Pending entrance event card is unknown.");
            }

            if (choiceIndex >= card.ChoiceRewards.Count || !pendingChoice.OptionIds.Contains(selectedOptionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Entrance event option is not available.");
            }

            player.Resources.Add(card.ChoiceRewards[choiceIndex]);
            state.PendingChoice = null;
            CompleteEntranceStep(state, command.PlayerId);

            var message = string.Format("Player {0} resolved entrance event {1} with option {2}.", command.PlayerId, card.CardId, selectedOptionId);
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = command.PlayerId,
                    SubjectId = card.CardId,
                    Message = message
                }
            }, message);
        }

        private bool TryOpenEntranceEventChoice(GameState state, GameCommand command, List<GameEvent> events)
        {
            if (resourceTokenService.HasResourceToken(state.Map, command.TargetId))
            {
                return false;
            }

            var eventColor = StaticMapDefinitions.GetEventColor(command.TargetId);
            if (eventDeckService.RemainingCount(state.Decks, eventColor) <= 0)
            {
                return false;
            }

            var cardId = eventDeckService.Draw(state.Decks, eventColor);
            var card = EventCardDatabase.Get(cardId);
            if (card == null || card.ChoiceRewards.Count == 0)
            {
                return false;
            }

            resourceTokenService.PlaceToken(state.Map, command.TargetId, card.ResourceType, card.ResourceAmount);

            var optionIds = new List<string>();
            for (var i = 0; i < card.ChoiceRewards.Count; i++)
            {
                optionIds.Add(i.ToString());
            }

            state.PendingChoice = new PendingChoiceState
            {
                ChoiceId = "entrance_event:" + card.CardId,
                PlayerId = command.PlayerId,
                ChoiceType = EntranceEventChoiceType,
                CardId = card.CardId,
                TargetId = command.TargetId,
                OptionIds = optionIds,
                SourceCommandId = command.CommandId
            };

            events.Add(new GameEvent
            {
                Kind = GameEventKind.ChoiceOpened,
                PlayerId = command.PlayerId,
                SubjectId = card.CardId,
                Message = "Entrance event choice opened for " + card.CardId + "."
            });

            return true;
        }

        private bool IsInitialLocationAllowed(string locationId)
        {
            if (mapQueryService.Map.MapId != StaticMapDefinitions.FourPlayerMapId)
            {
                return true;
            }

            return StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(locationId);
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

        private void CompleteEntranceStep(GameState state, int playerId)
        {
            if (AllPlayersHaveInitialCities(state))
            {
                if (state.StartPlayerId < 0)
                {
                    state.StartPlayerId = playerId;
                }

                GrantInitialGoldVouchers(state);
                state.CurrentPlayerId = state.StartPlayerId;
                state.Phase = GamePhase.ActionRound1;
                state.ActionRound = 1;
                state.Round = 1;
                return;
            }

            state.CurrentPlayerId = FindNextEntrancePlayerId(state, playerId);
        }

        private int FindNextEntrancePlayerId(GameState state, int playerId)
        {
            var turnOrder = turnOrderService.GetTurnOrder(state);
            var currentIndex = 0;
            for (var i = 0; i < turnOrder.Count; i++)
            {
                if (turnOrder[i] == playerId)
                {
                    currentIndex = i;
                    break;
                }
            }

            for (var offset = 1; offset <= turnOrder.Count; offset++)
            {
                var nextPlayerId = turnOrder[(currentIndex + offset) % turnOrder.Count];
                var player = state.FindPlayer(nextPlayerId);
                if (player != null && string.IsNullOrEmpty(player.CityLocationId))
                {
                    return nextPlayerId;
                }
            }

            return playerId;
        }

        private void GrantInitialGoldVouchers(GameState state)
        {
            var turnOrder = turnOrderService.GetTurnOrder(state);
            for (var i = 0; i < turnOrder.Count; i++)
            {
                var player = state.FindPlayer(turnOrder[i]);
                if (player == null)
                {
                    continue;
                }

                player.Resources.GoldVoucher += GetInitialGoldVoucherAmount(state.Players.Count, i);
            }
        }

        private static int GetInitialGoldVoucherAmount(int playerCount, int playerOrderIndex)
        {
            if (playerOrderIndex <= 0)
            {
                return 10;
            }

            if (playerCount == 3)
            {
                return playerOrderIndex == 1 ? 12 : 18;
            }

            if (playerCount == 4)
            {
                switch (playerOrderIndex)
                {
                    case 1:
                        return 12;
                    case 2:
                        return 14;
                    default:
                        return 18;
                }
            }

            return playerOrderIndex == 1 ? 18 : 0;
        }

        private static string GetSelectedOptionId(GameCommand command)
        {
            if (command.OptionIds != null && command.OptionIds.Count > 0)
            {
                return command.OptionIds[0];
            }

            return command.TargetId;
        }

        private static int ParseChoiceIndex(string optionId)
        {
            int choiceIndex;
            if (!int.TryParse(optionId, out choiceIndex))
            {
                return -1;
            }

            return choiceIndex;
        }

        private static CommandResult Invalid(CommandErrorCode errorCode, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(errorCode, reason));
        }
    }
}
