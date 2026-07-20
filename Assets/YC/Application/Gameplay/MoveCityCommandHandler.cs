using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class MoveCityCommandHandler : IGameCommandHandler
    {
        public const string EventOptionIdParameter = "eventOptionId";
        public const string EventInfluenceSlotIdParameter = "eventInfluenceSlotId";
        public const string EventInfluenceSlotIdsParameter = "eventInfluenceSlotIds";
        public const string MoveCityEventChoiceType = "move_city_event";

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
            return command != null &&
                   (command.Kind == GameCommandKind.MoveCity ||
                    command.Kind == GameCommandKind.ResolvePendingChoice);
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (command.Kind == GameCommandKind.ResolvePendingChoice || IsResolvingMoveCityEvent(state, command))
            {
                return HandleResolveMoveCityEvent(state, command);
            }

            CityMovementResult ignored;
            return HandleMove(state, command, command.TargetId, false, out ignored);
        }

        public CommandResult HandleGrantedMove(
            GameState state,
            GameCommand sourceCommand,
            string targetLocationId,
            out CityMovementResult movementResult)
        {
            if (sourceCommand == null)
            {
                throw new ArgumentNullException(nameof(sourceCommand));
            }

            return HandleMove(state, sourceCommand, targetLocationId, true, out movementResult);
        }

        private CommandResult HandleMove(
            GameState state,
            GameCommand command,
            string targetLocationId,
            bool grantedByEffect,
            out CityMovementResult movementResult)
        {
            var selectedOptionIndex = grantedByEffect
                ? ResolveGrantedEventOptionIndex(command)
                : HasExplicitEventOption(command) ? ResolveSelectedOptionIndex(command) : -1;
            if (selectedOptionIndex < -1)
            {
                movementResult = null;
                return Invalid(CommandErrorCode.InvalidTarget, "Move city event option must be a valid number.");
            }

            var eventInfluenceSlotIds = ResolveEventInfluenceSlotIds(command);
            var result = grantedByEffect
                ? cityMovementService.MoveCityForFacility(
                    state,
                    command.PlayerId,
                    targetLocationId,
                    selectedOptionIndex,
                    eventInfluenceSlotIds,
                    command.CommandId)
                : cityMovementService.MoveCity(
                    state,
                    command.PlayerId,
                    targetLocationId,
                    selectedOptionIndex,
                    eventInfluenceSlotIds,
                    command.CommandId);
            movementResult = result;
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            var events = CreateMoveEvents(command.PlayerId, result);
            if (result.HasEventCard)
            {
                AddMoveCityEventEvents(state, command, result, events);
            }

            if (!grantedByEffect && (!result.HasEventCard || result.SelectedOptionIndex >= 0))
            {
                roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);
            }

            return CommandResult.SuccessResult(events, "Player " + command.PlayerId + " moved city to " + result.TargetLocationId + ".");
        }

        private CommandResult HandleResolveMoveCityEvent(GameState state, GameCommand command)
        {
            var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
            if (pendingChoice == null || pendingChoice.ChoiceType != MoveCityEventChoiceType)
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "No move city event is waiting for a choice.");
            }

            if (pendingChoice.PlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "Only the player resolving the move city event can choose an option.");
            }

            var selectedOptionId = GetSelectedOptionId(command);
            if (!pendingChoice.OptionIds.Contains(selectedOptionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Move city event option is not available.");
            }

            int selectedOptionIndex;
            if (!int.TryParse(selectedOptionId, out selectedOptionIndex))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "Move city event option must be a valid number.");
            }

            var consumeMainAction = CityMovementService.PendingMoveConsumesMainAction(state);
            var result = cityMovementService.ResolveMoveCityEvent(
                state,
                command.PlayerId,
                pendingChoice.TargetId,
                pendingChoice.CardId,
                selectedOptionIndex,
                ResolveEventInfluenceSlotIds(command));
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            if (consumeMainAction)
            {
                roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);
            }

            var card = EventCardDatabase.Get(result.EventCardId);
            var message = "Player " + command.PlayerId + " resolved move city event " + card.Name + ".";
            var events = new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = message,
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description },
                        { "targetLocationId", result.TargetLocationId },
                        { "selectedOptionIndex", result.SelectedOptionIndex.ToString() },
                        { "pendingEffect", GetPendingEffect(card, result.SelectedOptionIndex) }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.TargetLocationId,
                    Message = "Move city event granted reward."
                }
            };

            if (HasScorePendingEffect(card, result.SelectedOptionIndex))
            {
                events.Add(new GameEvent
                {
                    Kind = GameEventKind.ScoreChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Move city event changed score."
                });
            }

            return CommandResult.SuccessResult(events, message);
        }

        private static List<GameEvent> CreateMoveEvents(int playerId, CityMovementResult result)
        {
            return new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.CityMoved,
                    PlayerId = playerId,
                    Message = "City moved from " + result.SourceLocationId + " to " + result.TargetLocationId + "."
                },
                new GameEvent
                {
                    Kind = GameEventKind.InfluencePlaced,
                    PlayerId = playerId,
                    Message = "City movement attempted source influence placement."
                }
            };
        }

        private static void AddMoveCityEventEvents(
            GameState state,
            GameCommand command,
            CityMovementResult result,
            List<GameEvent> events)
        {
            var card = EventCardDatabase.Get(result.EventCardId);
            events.Add(new GameEvent
            {
                Kind = GameEventKind.CardMoved,
                PlayerId = command.PlayerId,
                SubjectId = result.EventCardId,
                Message = "Move city event revealed " + card.Name + ".",
                Data =
                {
                    { "cardName", card.Name },
                    { "cardDescription", card.Description },
                    { "eventColor", result.EventColor.ToString() },
                    { "targetLocationId", result.TargetLocationId },
                    { "selectedOptionIndex", result.SelectedOptionIndex.ToString() },
                    { "pendingEffect", GetPendingEffect(card, result.SelectedOptionIndex) }
                }
            });
            events.Add(new GameEvent
            {
                Kind = GameEventKind.ResourceChanged,
                PlayerId = command.PlayerId,
                SubjectId = result.TargetLocationId,
                Message = result.SelectedOptionIndex >= 0
                    ? "Move city placed resource token and granted event reward."
                    : "Move city placed resource token."
            });

            if (result.SelectedOptionIndex >= 0)
            {
                if (HasScorePendingEffect(card, result.SelectedOptionIndex))
                {
                    events.Add(new GameEvent
                    {
                        Kind = GameEventKind.ScoreChanged,
                        PlayerId = command.PlayerId,
                        SubjectId = result.EventCardId,
                        Message = "Move city event changed score."
                    });
                }

                return;
            }

            OpenMoveCityEventChoice(state, command, result, card, events);
        }

        private static void OpenMoveCityEventChoice(
            GameState state,
            GameCommand command,
            CityMovementResult result,
            EventCardDefinition card,
            List<GameEvent> events)
        {
            events.Add(new GameEvent
            {
                Kind = GameEventKind.ChoiceOpened,
                PlayerId = command.PlayerId,
                SubjectId = result.EventCardId,
                Message = "Move city event choice opened for " + card.Name + ".",
                Data =
                {
                    { "cardName", card.Name },
                    { "cardDescription", card.Description },
                    { "targetLocationId", result.TargetLocationId }
                }
            });
        }

        private static bool IsResolvingMoveCityEvent(GameState state, GameCommand command)
        {
            var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
            return command != null &&
                   command.Kind == GameCommandKind.MoveCity &&
                   pendingChoice != null &&
                   pendingChoice.ChoiceType == MoveCityEventChoiceType &&
                   HasExplicitEventOption(command);
        }

        private static bool HasExplicitEventOption(GameCommand command)
        {
            return !string.IsNullOrEmpty(GetParameter(command, EventOptionIdParameter)) ||
                   (command.OptionIds != null && command.OptionIds.Count > 0);
        }

        private static string GetSelectedOptionId(GameCommand command)
        {
            var optionId = GetParameter(command, EventOptionIdParameter);
            if (!string.IsNullOrEmpty(optionId))
            {
                return optionId;
            }

            if (command.OptionIds != null && command.OptionIds.Count > 0)
            {
                return command.OptionIds[0];
            }

            return string.Empty;
        }

        private static int ResolveSelectedOptionIndex(GameCommand command)
        {
            int selectedOptionIndex;
            return int.TryParse(GetSelectedOptionId(command), out selectedOptionIndex) ? selectedOptionIndex : -2;
        }

        private static int ResolveGrantedEventOptionIndex(GameCommand command)
        {
            var optionId = GetParameter(command, EventOptionIdParameter);
            if (string.IsNullOrEmpty(optionId))
            {
                return -1;
            }

            int selectedOptionIndex;
            return int.TryParse(optionId, out selectedOptionIndex) ? selectedOptionIndex : -2;
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

        private static List<string> ResolveEventInfluenceSlotIds(GameCommand command)
        {
            var result = SplitIds(GetParameter(command, EventInfluenceSlotIdsParameter));
            var singleSlotId = GetParameter(command, EventInfluenceSlotIdParameter);
            if (!string.IsNullOrEmpty(singleSlotId))
            {
                result.Add(singleSlotId.Trim());
            }

            return result;
        }

        private static List<string> SplitIds(string encoded)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            var parts = encoded.Split(new[] { ',', ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                result.Add(parts[i].Trim());
            }

            return result;
        }

        private static string GetPendingEffect(EventCardDefinition card, int selectedOptionIndex)
        {
            if (card == null ||
                selectedOptionIndex < 0 ||
                selectedOptionIndex >= card.ChoicePendingEffects.Count)
            {
                return string.Empty;
            }

            return EventEffectUtility.Format(card.ChoicePendingEffects[selectedOptionIndex]);
        }

        private static bool HasScorePendingEffect(EventCardDefinition card, int selectedOptionIndex)
        {
            if (card == null ||
                selectedOptionIndex < 0 ||
                selectedOptionIndex >= card.ChoicePendingEffects.Count)
            {
                return false;
            }

            return EventEffectUtility.HasScoreEffect(card.ChoicePendingEffects[selectedOptionIndex]);
        }

        private static CommandResult Invalid(CommandErrorCode errorCode, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(errorCode, reason));
        }
    }
}
