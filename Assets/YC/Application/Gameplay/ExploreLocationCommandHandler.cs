using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Exploration;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class ExploreLocationCommandHandler : IGameCommandHandler
    {
        public const string RouteIdsParameter = "routeIds";
        public const string PathLocationIdsParameter = "pathLocationIds";
        public const string EventOptionIdParameter = "eventOptionId";
        public const string InfluenceSlotIdParameter = "influenceSlotId";
        public const string PaymentRecipientsParameter = "paymentRecipients";
        public const string ExploreEventChoiceType = "explore_event";

        private readonly ExplorationService explorationService;
        private readonly RoundAdvanceService roundAdvanceService;

        public ExploreLocationCommandHandler(ExplorationService explorationService)
            : this(explorationService, new RoundAdvanceService())
        {
        }

        public ExploreLocationCommandHandler(
            ExplorationService explorationService,
            RoundAdvanceService roundAdvanceService)
        {
            this.explorationService = explorationService ?? throw new ArgumentNullException(nameof(explorationService));
            this.roundAdvanceService = roundAdvanceService ?? throw new ArgumentNullException(nameof(roundAdvanceService));
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null &&
                   (command.Kind == GameCommandKind.ExploreLocation ||
                    command.Kind == GameCommandKind.ResolvePendingChoice);
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (command.Kind == GameCommandKind.ResolvePendingChoice)
            {
                return HandleResolveExploreEvent(state, command);
            }

            MapPath path;
            var pathValidation = ResolvePath(state, command, out path);
            if (!pathValidation.IsValid)
            {
                return CommandResult.Invalid(pathValidation);
            }

            if (!HasExplicitEventOption(command))
            {
                return HandleBeginExploreEvent(state, command, path);
            }

            var selectedOptionIndex = ResolveSelectedOptionIndex(command);
            if (selectedOptionIndex < 0)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "探索事件选项必须是数字。");
            }

            var influenceSlotId = GetParameter(command, InfluenceSlotIdParameter);
            var paymentRecipients = ResolvePaymentRecipients(command);
            var result = explorationService.Explore(
                state,
                command.PlayerId,
                command.TargetId,
                path,
                selectedOptionIndex,
                influenceSlotId,
                paymentRecipients);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);

            var card = EventCardDatabase.Get(result.EventCardId);
            var message = "Player " + command.PlayerId + " explored " + result.TargetLocationId + ".";
            var events = new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.CardMoved,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event revealed " + card.Name + ".",
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description },
                        { "eventColor", result.EventColor.ToString() },
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
                    Message = "Exploration placed resource token and granted event reward."
                },
                new GameEvent
                {
                    Kind = GameEventKind.InfluencePlaced,
                    PlayerId = command.PlayerId,
                    SubjectId = result.InfluencePlacement != null ? result.InfluencePlacement.SlotId : string.Empty,
                    Message = "Exploration placed influence at " + result.TargetLocationId + "."
                }
            };

            if (HasScorePendingEffect(card, result.SelectedOptionIndex))
            {
                events.Add(new GameEvent
                {
                    Kind = GameEventKind.ScoreChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event changed score."
                });
            }

            return CommandResult.SuccessResult(events, message);
        }

        private CommandResult HandleBeginExploreEvent(GameState state, GameCommand command, MapPath path)
        {
            var influenceSlotId = GetParameter(command, InfluenceSlotIdParameter);
            var paymentRecipients = ResolvePaymentRecipients(command);
            var result = explorationService.BeginExploreEvent(
                state,
                command.PlayerId,
                command.TargetId,
                path,
                influenceSlotId,
                paymentRecipients);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            var card = EventCardDatabase.Get(result.EventCardId);
            var optionIds = new List<string>();
            for (var i = 0; i < card.ChoiceRewards.Count; i++)
            {
                optionIds.Add(i.ToString());
            }

            state.PendingChoice = new PendingChoiceState
            {
                ChoiceId = ExploreEventChoiceType + ":" + result.EventCardId,
                PlayerId = command.PlayerId,
                ChoiceType = ExploreEventChoiceType,
                CardId = result.EventCardId,
                TargetId = result.TargetLocationId,
                OptionIds = optionIds,
                SourceCommandId = command.CommandId
            };

            var message = "Player " + command.PlayerId + " began exploring " + result.TargetLocationId + ".";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.CardMoved,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event revealed " + card.Name + ".",
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description },
                        { "eventColor", result.EventColor.ToString() },
                        { "targetLocationId", result.TargetLocationId }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.TargetLocationId,
                    Message = "Exploration placed resource token."
                },
                new GameEvent
                {
                    Kind = GameEventKind.ChoiceOpened,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event choice opened for " + card.Name + ".",
                    Data =
                    {
                        { "cardName", card.Name },
                        { "cardDescription", card.Description },
                        { "targetLocationId", result.TargetLocationId }
                    }
                }
            }, message);
        }

        private CommandResult HandleResolveExploreEvent(GameState state, GameCommand command)
        {
            var pendingChoice = state.PendingChoice;
            if (pendingChoice == null || pendingChoice.ChoiceType != ExploreEventChoiceType)
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "当前没有待处理的探索事件。");
            }

            if (pendingChoice.PlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "只有正在探索的玩家可以处理该事件。");
            }

            var selectedOptionId = GetSelectedOptionId(command);
            if (!pendingChoice.OptionIds.Contains(selectedOptionId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "探索事件选项不可用。");
            }

            int selectedOptionIndex;
            if (!int.TryParse(selectedOptionId, out selectedOptionIndex))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "探索事件选项必须是有效选项编号。");
            }

            var influenceSlotId = GetParameter(command, InfluenceSlotIdParameter);
            var result = explorationService.ResolveExploreEvent(
                state,
                command.PlayerId,
                pendingChoice.TargetId,
                pendingChoice.CardId,
                selectedOptionIndex,
                influenceSlotId);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            state.PendingChoice = null;
            roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);

            var card = EventCardDatabase.Get(result.EventCardId);
            var message = "Player " + command.PlayerId + " resolved exploration event " + card.Name + ".";
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
                        { "selectedOptionIndex", result.SelectedOptionIndex.ToString() },
                        { "pendingEffect", GetPendingEffect(card, result.SelectedOptionIndex) }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.TargetLocationId,
                    Message = "Exploration event granted reward."
                },
                new GameEvent
                {
                    Kind = GameEventKind.InfluencePlaced,
                    PlayerId = command.PlayerId,
                    SubjectId = result.InfluencePlacement != null ? result.InfluencePlacement.SlotId : string.Empty,
                    Message = "Exploration placed influence at " + result.TargetLocationId + "."
                }
            };

            if (HasScorePendingEffect(card, result.SelectedOptionIndex))
            {
                events.Add(new GameEvent
                {
                    Kind = GameEventKind.ScoreChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.EventCardId,
                    Message = "Exploration event changed score."
                });
            }

            return CommandResult.SuccessResult(events, message);
        }

        private ValidationResult ResolvePath(GameState state, GameCommand command, out MapPath path)
        {
            path = null;
            if (!string.IsNullOrEmpty(GetParameter(command, RouteIdsParameter)))
            {
                path = new MapPath
                {
                    LocationIds = SplitIds(GetParameter(command, PathLocationIdsParameter)),
                    RouteIds = SplitIds(GetParameter(command, RouteIdsParameter))
                };
                return ValidationResult.Success;
            }

            try
            {
                path = explorationService.FindDefaultPath(state, command.PlayerId, command.TargetId);
                return path == null
                    ? ValidationResult.Failure(CommandErrorCode.NoRoute, "无法为探索目标建立默认路线。")
                    : ValidationResult.Success;
            }
            catch (ArgumentException)
            {
                return ValidationResult.Failure(CommandErrorCode.NoRoute, "无法为探索目标建立默认路线。");
            }
        }

        private static Dictionary<string, int> ResolvePaymentRecipients(GameCommand command)
        {
            var result = new Dictionary<string, int>();
            var encoded = GetParameter(command, PaymentRecipientsParameter);
            if (!string.IsNullOrEmpty(encoded))
            {
                var entries = encoded.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < entries.Length; i++)
                {
                    var parts = entries[i].Split(new[] { '=', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    int playerId;
                    if (int.TryParse(parts[1], out playerId))
                    {
                        result[parts[0]] = playerId;
                    }
                }
            }

            if (command.Parameters != null)
            {
                foreach (var entry in command.Parameters)
                {
                    if (!entry.Key.StartsWith("payment:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int playerId;
                    if (int.TryParse(entry.Value, out playerId))
                    {
                        result[entry.Key.Substring("payment:".Length)] = playerId;
                    }
                }
            }

            return result;
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

            return command.TargetId;
        }

        private static int ResolveSelectedOptionIndex(GameCommand command)
        {
            var optionId = GetParameter(command, EventOptionIdParameter);
            if (string.IsNullOrEmpty(optionId) && command.OptionIds != null && command.OptionIds.Count > 0)
            {
                optionId = command.OptionIds[0];
            }

            if (string.IsNullOrEmpty(optionId))
            {
                return 0;
            }

            int selectedOptionIndex;
            return int.TryParse(optionId, out selectedOptionIndex) ? selectedOptionIndex : -1;
        }

        private static List<string> SplitIds(string encoded)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return result;
            }

            var parts = encoded.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                result.Add(parts[i].Trim());
            }

            return result;
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

        private static string GetPendingEffect(EventCardDefinition card, int selectedOptionIndex)
        {
            if (card == null ||
                selectedOptionIndex < 0 ||
                selectedOptionIndex >= card.ChoicePendingEffects.Count)
            {
                return string.Empty;
            }

            return card.ChoicePendingEffects[selectedOptionIndex];
        }

        private static bool HasScorePendingEffect(EventCardDefinition card, int selectedOptionIndex)
        {
            return GetPendingEffect(card, selectedOptionIndex).Contains("分数");
        }

        private static CommandResult Invalid(CommandErrorCode errorCode, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(errorCode, reason));
        }
    }
}
