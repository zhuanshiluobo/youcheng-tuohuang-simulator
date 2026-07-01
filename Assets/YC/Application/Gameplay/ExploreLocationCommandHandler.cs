using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
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
        public const string EventInfluenceSlotIdParameter = "eventInfluenceSlotId";
        public const string EventInfluenceSlotIdsParameter = "eventInfluenceSlotIds";
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
            var eventInfluenceSlotIds = ResolveEventInfluenceSlotIds(command);
            Dictionary<string, int> paymentRecipients;
            var paymentRecipientsValidation = ResolvePaymentRecipients(command, out paymentRecipients);
            if (!paymentRecipientsValidation.IsValid)
            {
                return CommandResult.Invalid(paymentRecipientsValidation);
            }

            var result = explorationService.Explore(
                state,
                command.PlayerId,
                command.TargetId,
                path,
                selectedOptionIndex,
                influenceSlotId,
                paymentRecipients,
                eventInfluenceSlotIds);
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
            Dictionary<string, int> paymentRecipients;
            var paymentRecipientsValidation = ResolvePaymentRecipients(command, out paymentRecipients);
            if (!paymentRecipientsValidation.IsValid)
            {
                return CommandResult.Invalid(paymentRecipientsValidation);
            }

            var result = explorationService.BeginExploreEvent(
                state,
                command.PlayerId,
                command.TargetId,
                path,
                influenceSlotId,
                paymentRecipients,
                command.CommandId);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            var card = EventCardDatabase.Get(result.EventCardId);
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
            var pendingChoice = CardFlowStateAdapter.GetPendingChoiceView(state);
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
            var eventInfluenceSlotIds = ResolveEventInfluenceSlotIds(command);
            var result = explorationService.ResolveExploreEvent(
                state,
                command.PlayerId,
                pendingChoice.TargetId,
                pendingChoice.CardId,
                selectedOptionIndex,
                influenceSlotId,
                eventInfluenceSlotIds);
            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

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

        private static ValidationResult ResolvePaymentRecipients(
            GameCommand command,
            out Dictionary<string, int> paymentRecipients)
        {
            paymentRecipients = new Dictionary<string, int>();
            var encoded = GetParameter(command, PaymentRecipientsParameter);
            if (!string.IsNullOrEmpty(encoded))
            {
                var entries = encoded.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < entries.Length; i++)
                {
                    var parts = entries[i].Split(new[] { '=', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || string.IsNullOrEmpty(parts[0].Trim()))
                    {
                        return InvalidPaymentRecipients();
                    }

                    int playerId;
                    if (!int.TryParse(parts[1], out playerId))
                    {
                        return InvalidPaymentRecipients();
                    }

                    paymentRecipients[parts[0].Trim()] = playerId;
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
                    var routeId = entry.Key.Substring("payment:".Length).Trim();
                    if (string.IsNullOrEmpty(routeId) || !int.TryParse(entry.Value, out playerId))
                    {
                        return InvalidPaymentRecipients();
                    }

                    paymentRecipients[routeId] = playerId;
                }
            }

            return ValidationResult.Success;
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

        private static ValidationResult InvalidPaymentRecipients()
        {
            return ValidationResult.Failure(
                CommandErrorCode.InvalidTarget,
                "路费接收方参数格式不正确。");
        }
    }
}
