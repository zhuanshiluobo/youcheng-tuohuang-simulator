using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Application.Sessions
{
    public sealed class GameSession
    {
        private readonly List<IGameCommandHandler> commandHandlers = new List<IGameCommandHandler>();
        private PendingActionLog pendingActionLog;

        public GameState State { get; private set; }

        public GameSession(GameState initialState)
        {
            State = initialState ?? throw new ArgumentNullException(nameof(initialState));
            ClearInvalidPendingChoice();
        }

        public void RegisterHandler(IGameCommandHandler handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            commandHandlers.Add(handler);
        }

        public void ReplaceState(GameState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            pendingActionLog = null;
            ClearInvalidPendingChoice();
        }

        private void ClearInvalidPendingChoice()
        {
            if (State.PendingChoice != null && !State.PendingChoice.IsValid())
            {
                State.PendingChoice = null;
            }

            if (State.PendingCardSession != null && !State.PendingCardSession.IsValid())
            {
                State.PendingCardSession = null;
            }

            if (State.PendingSpecialAction != null &&
                (!State.PendingSpecialAction.IsValid(State) ||
                 (State.PendingSpecialAction.Step == SpecialActionPendingSteps.AwaitMoveEvent &&
                  (State.PendingCardSession == null ||
                   !State.PendingCardSession.IsValid() ||
                   State.PendingCardSession.ChoiceType != MoveCityCommandHandler.MoveCityEventChoiceType ||
                   State.PendingCardSession.PlayerId != State.PendingSpecialAction.PlayerId))))
            {
                State.PendingSpecialAction = null;
            }
        }

        public CommandResult Submit(GameCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (State.HasPendingChoice() &&
                !IsPendingChoiceResolutionCommand(command) &&
                !IsConfirmedSecondCharacterEffectCommand(State, command))
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    Domain.Rules.CommandErrorCode.PendingChoiceRequired,
                    "请先处理待选择项，再提交其他行动。"));
            }

            CommandResult deferredPendingChoiceResult = null;
            for (var i = 0; i < commandHandlers.Count; i++)
            {
                if (!commandHandlers[i].CanHandle(command))
                {
                    continue;
                }

                var result = commandHandlers[i].Handle(State, command);
                if (command.Kind == Domain.Rules.GameCommandKind.ResolvePendingChoice &&
                    !result.Succeeded &&
                    result.Validation != null &&
                    result.Validation.ErrorCode == Domain.Rules.CommandErrorCode.PendingChoiceRequired)
                {
                    if (deferredPendingChoiceResult == null)
                    {
                        deferredPendingChoiceResult = result;
                    }

                    continue;
                }

                AppendLog(command, result);
                return result;
            }

            var invalid = ValidationResult.Failure(Domain.Rules.CommandErrorCode.UnknownCommand, "没有为该命令注册处理器。");
            if (deferredPendingChoiceResult != null)
            {
                return deferredPendingChoiceResult;
            }

            return CommandResult.Invalid(invalid);
        }

        private static bool IsPendingChoiceResolutionCommand(GameCommand command)
        {
            return command != null &&
                   (command.Kind == Domain.Rules.GameCommandKind.ResolvePendingChoice ||
                    command.Kind == Domain.Rules.GameCommandKind.ResolveEntranceEvent);
        }

        private static bool IsConfirmedSecondCharacterEffectCommand(GameState state, GameCommand command)
        {
            if (state == null || command == null ||
                command.Kind != Domain.Rules.GameCommandKind.UseCharacterCard ||
                (state.PendingChoice != null && state.PendingChoice.IsValid()) ||
                (state.PendingCardSession != null && state.PendingCardSession.IsValid()))
            {
                return false;
            }

            var pending = state.PendingCharacterEffect;
            if (pending == null || !pending.IsValid() ||
                pending.ChoiceType != CharacterPendingChoiceTypes.SecondEffectExecution ||
                pending.PlayerId != command.PlayerId)
            {
                return false;
            }

            string cardId;
            if (command.Parameters == null || !command.Parameters.TryGetValue("cardId", out cardId) ||
                string.IsNullOrEmpty(cardId))
            {
                cardId = command.TargetId;
            }

            string effectMode;
            if (command.Parameters == null || !command.Parameters.TryGetValue("effectMode", out effectMode))
            {
                effectMode = string.Empty;
            }

            return cardId == pending.CardId && effectMode == pending.RemainingEffectMode;
        }

        private void AppendLog(GameCommand command, CommandResult result)
        {
            if (!result.Succeeded)
            {
                return;
            }

            if (pendingActionLog != null && pendingActionLog.PlayerId == command.PlayerId)
            {
                if (State.HasPendingChoice())
                {
                    return;
                }

                AppendLogEntry(
                    command.CommandId,
                    pendingActionLog.PlayerId,
                    pendingActionLog.Kind == GameCommandKind.UseSpecialAction
                        ? PublicActionLogFormatter.CombineSpecialActionSettlement(pendingActionLog.Message, result.LogMessage)
                        : pendingActionLog.Message);
                pendingActionLog = null;
                return;
            }

            string message;
            var handledAsPublicAction = PublicActionLogFormatter.TryFormat(command, result, out message);
            if (!handledAsPublicAction)
            {
                message = result.LogMessage;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (handledAsPublicAction &&
                PublicActionLogFormatter.ShouldWaitForSettlement(command.Kind) &&
                State.HasPendingChoice())
            {
                pendingActionLog = new PendingActionLog
                {
                    PlayerId = command.PlayerId,
                    Kind = command.Kind,
                    Message = message
                };
                return;
            }

            AppendLogEntry(command.CommandId, command.PlayerId, message);
        }

        private void AppendLogEntry(string commandId, int playerId, string message)
        {
            State.Logs.Add(new GameLogEntry
            {
                Sequence = State.Logs.Count + 1,
                CommandId = commandId,
                PlayerId = playerId,
                Message = message
            });
        }

        private sealed class PendingActionLog
        {
            public int PlayerId;
            public GameCommandKind Kind;
            public string Message = string.Empty;
        }
    }

    public interface IGameCommandHandler
    {
        bool CanHandle(GameCommand command);
        CommandResult Handle(GameState state, GameCommand command);
    }

    public static class PublicActionLogFormatter
    {
        public static bool ShouldWaitForSettlement(GameCommandKind kind)
        {
            return kind == GameCommandKind.BuildFacility ||
                   kind == GameCommandKind.ExploreLocation ||
                   kind == GameCommandKind.MoveCity ||
                   kind == GameCommandKind.UseCharacterCard ||
                   kind == GameCommandKind.UseSpecialAction;
        }

        public static bool TryFormat(GameCommand command, CommandResult result, out string message)
        {
            message = string.Empty;
            if (command == null || result == null || !result.Succeeded)
            {
                return false;
            }

            switch (command.Kind)
            {
                case GameCommandKind.ChooseStartPlayer:
                    message = "\u88ab\u9009\u4e3a\u8d77\u59cb\u73a9\u5bb6\uff0c\u5165\u573a\u9636\u6bb5\u5f00\u59cb\u3002";
                    return true;

                case GameCommandKind.ChooseInitialLocation:
                    message = "\u5c06\u521d\u59cb\u57ce\u5e02\u653e\u7f6e\u5728\u5730\u70b9 " + ValueOrFallback(command.TargetId, "?") + "\u3002";
                    return true;

                case GameCommandKind.ResolveEntranceEvent:
                    message = FormatResolvedEvent(result, "\u5165\u573a\u4e8b\u4ef6");
                    return true;

                case GameCommandKind.CoverCharacterCard:
                    return true;

                case GameCommandKind.DeployInfluence:
                    message = "\u5728" + DescribeInfluenceSlot(command.TargetId) + "\u90e8\u7f72\u4e86 1 \u4e2a\u5f71\u54cd\u529b\u3002";
                    return true;

                case GameCommandKind.DispatchInfluence:
                    message = "\u8c03\u5ea6\u4e86 " + GetDispatchCount(command) + " \u6b21\u5f71\u54cd\u529b\u3002";
                    return true;

                case GameCommandKind.ExploreLocation:
                    message = FormatExplore(command, result);
                    return true;

                case GameCommandKind.MoveCity:
                    message = FormatMoveCity(command, result);
                    return true;

                case GameCommandKind.BuildFacility:
                    message = FormatBuild(command, result);
                    return true;

                case GameCommandKind.UseCharacterCard:
                    message = FormatCharacterCard(command, result);
                    return true;

                case GameCommandKind.DeclareCityStyle:
                    message = FormatCityStyle(command, result);
                    return true;

                case GameCommandKind.UseSpecialAction:
                    message = FormatSpecialAction(command, result);
                    return true;

                case GameCommandKind.CollectResource:
                    message = FormatCollectedResources(result);
                    return true;

                case GameCommandKind.ResolvePendingChoice:
                    return TryFormatResolvedChoice(result, out message);

                case GameCommandKind.EndAction:
                    if (FindEvent(result, GameEventKind.PlayerAdvanced) != null)
                    {
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        private static string FormatBuild(GameCommand command, CommandResult result)
        {
            var buildEvent = FindEvent(result, GameEventKind.FacilityBuilt);
            var facilityName = GetData(buildEvent, "facilityName");
            if (string.IsNullOrEmpty(facilityName))
            {
                var facilityId = GetParameter(command, "facilityId");
                if (string.IsNullOrEmpty(facilityId))
                {
                    facilityId = command.TargetId;
                }

                var definition = FacilityCardDatabase.Get(facilityId);
                facilityName = definition == null ? facilityId : definition.Name;
            }

            var slotText = string.Empty;
            int slotIndex;
            if (int.TryParse(GetData(buildEvent, "cityBoardSlotIndex"), out slotIndex))
            {
                slotText = "\uff08\u57ce\u5e02\u9762\u677f\u7b2c " + (slotIndex + 1) + " \u683c\uff09";
            }

            return "\u5efa\u9020\u4e86\u5efa\u7b51\u201c" + ValueOrFallback(facilityName, "\u672a\u77e5\u5efa\u7b51") + "\u201d" + slotText + "\u3002";
        }

        private static string FormatCharacterCard(GameCommand command, CommandResult result)
        {
            var cardId = GetParameter(command, "cardId");
            if (string.IsNullOrEmpty(cardId))
            {
                cardId = command.TargetId;
            }

            if (string.IsNullOrEmpty(cardId))
            {
                var cardEvent = FindEvent(result, GameEventKind.CardMoved);
                cardId = cardEvent == null ? string.Empty : cardEvent.SubjectId;
            }

            var definition = CharacterCardDatabase.Get(cardId);
            var cardName = definition == null ? cardId : definition.Name;
            return "\u53d1\u52a8\u4e86\u89d2\u8272\u724c\u201c" + ValueOrFallback(cardName, "\u672a\u77e5\u89d2\u8272\u724c") +
                   "\u201d\uff0c\u5df2\u5b8c\u6210\u5168\u90e8\u7ed3\u7b97\u3002";
        }

        private static string FormatCityStyle(GameCommand command, CommandResult result)
        {
            var scoreEvent = FindEvent(result, GameEventKind.ScoreChanged);
            var styleName = GetData(scoreEvent, "cityStyleName");
            if (string.IsNullOrEmpty(styleName))
            {
                var styleId = GetParameter(command, "cityStyleId");
                if (string.IsNullOrEmpty(styleId))
                {
                    styleId = command.TargetId;
                }

                var definition = CityStyleDatabase.Get(styleId);
                styleName = definition == null ? styleId : definition.Name;
            }

            var score = GetData(scoreEvent, "score");
            var scoreText = string.IsNullOrEmpty(score) ? string.Empty : "\uff0c\u83b7\u5f97 " + score + " \u5206";
            return "\u5ba3\u544a\u4e86\u201c" + ValueOrFallback(styleName, "\u672a\u77e5\u57ce\u5efa") + "\u201d" + scoreText + "\u3002";
        }

        private static string FormatSpecialAction(GameCommand command, CommandResult result)
        {
            var specialActionId = GetParameter(command, "specialActionId");
            if (string.IsNullOrEmpty(specialActionId))
            {
                specialActionId = command.TargetId;
            }

            var definition = SpecialActionDatabase.Get(specialActionId);
            var actionName = definition == null ? specialActionId : definition.Name;
            var baseMessage = "发动了特殊行动“" + ValueOrFallback(actionName, "未知特殊行动") + "”";
            return FindEvent(result, GameEventKind.ChoiceOpened) != null
                ? baseMessage + "。"
                : CombineSpecialActionSettlement(baseMessage, result.LogMessage);
        }

        internal static string CombineSpecialActionSettlement(string baseMessage, string settlementSummary)
        {
            var prefix = string.IsNullOrWhiteSpace(baseMessage)
                ? "完成了特殊行动"
                : baseMessage.Trim().TrimEnd('。', '.', '；', ';');
            if (string.IsNullOrWhiteSpace(settlementSummary))
            {
                return prefix + "，已完成全部结算。";
            }

            var summary = settlementSummary.Trim().TrimEnd('。', '.');
            return prefix + "；" + summary + "。";
        }

        private static string FormatExplore(GameCommand command, CommandResult result)
        {
            var cardEvent = FindEventWithData(result, "targetLocationId");
            var targetId = GetData(cardEvent, "targetLocationId");
            if (string.IsNullOrEmpty(targetId))
            {
                targetId = command.TargetId;
            }

            var cardName = GetData(cardEvent, "cardName");
            var eventText = string.IsNullOrEmpty(cardName)
                ? string.Empty
                : "\uff0c\u5e76\u7ed3\u7b97\u4e86\u4e8b\u4ef6\u201c" + cardName + "\u201d";
            return "\u63a2\u7d22\u4e86\u5730\u70b9 " + ValueOrFallback(targetId, "?") + eventText + "\u3002";
        }

        private static string FormatMoveCity(GameCommand command, CommandResult result)
        {
            var cardEvent = FindEventWithData(result, "targetLocationId");
            var targetId = GetData(cardEvent, "targetLocationId");
            if (string.IsNullOrEmpty(targetId))
            {
                targetId = command.TargetId;
            }

            var cardName = GetData(cardEvent, "cardName");
            var eventText = string.IsNullOrEmpty(cardName)
                ? string.Empty
                : "\uff0c\u5e76\u7ed3\u7b97\u4e86\u4e8b\u4ef6\u201c" + cardName + "\u201d";
            return "\u5c06\u57ce\u5e02\u79fb\u52a8\u81f3\u5730\u70b9 " + ValueOrFallback(targetId, "?") + eventText + "\u3002";
        }

        private static string FormatCollectedResources(CommandResult result)
        {
            var resourceEvent = FindEvent(result, GameEventKind.ResourceChanged);
            var resources = new List<string>();
            AddResource(resources, GetData(resourceEvent, "rewardOriginium"), "\u6e90\u77f3");
            AddResource(resources, GetData(resourceEvent, "rewardOriginiumShard"), "\u6e90\u77f3\u788e\u7247");
            AddResource(resources, GetData(resourceEvent, "rewardIron"), "\u94c1");
            AddResource(resources, GetData(resourceEvent, "rewardPureOriginium"), "\u81f3\u7eaf\u6e90\u77f3");
            AddResource(resources, GetData(resourceEvent, "rewardGoldVoucher"), "\u91d1\u5238");
            return resources.Count == 0
                ? "\u5b8c\u6210\u4e86\u8d44\u6e90\u6536\u96c6\u3002"
                : "\u6536\u96c6\u4e86" + string.Join("\u3001", resources.ToArray()) + "\u3002";
        }

        private static bool TryFormatResolvedChoice(CommandResult result, out string message)
        {
            message = string.Empty;
            var cardEvent = FindEvent(result, GameEventKind.CardMoved);
            if (cardEvent != null)
            {
                var character = CharacterCardDatabase.Get(cardEvent.SubjectId);
                if (character != null)
                {
                    message = "\u53d1\u52a8\u4e86\u89d2\u8272\u724c\u201c" + character.Name + "\u201d\uff0c\u5df2\u5b8c\u6210\u5168\u90e8\u7ed3\u7b97\u3002";
                    return true;
                }
            }

            var choiceEvent = FindEvent(result, GameEventKind.ChoiceResolved);
            if (choiceEvent != null)
            {
                var facility = FacilityCardDatabase.Get(choiceEvent.SubjectId);
                if (facility != null)
                {
                    message = "\u7ed3\u7b97\u4e86\u5efa\u7b51\u201c" + facility.Name + "\u201d\u7684\u6548\u679c\u3002";
                    return true;
                }

                var cardName = GetData(choiceEvent, "cardName");
                if (!string.IsNullOrEmpty(cardName))
                {
                    message = "\u7ed3\u7b97\u4e86\u4e8b\u4ef6\u201c" + cardName + "\u201d\u3002";
                    return true;
                }
            }

            if (ContainsChinese(result.LogMessage))
            {
                message = result.LogMessage;
                return true;
            }

            return false;
        }

        private static string FormatResolvedEvent(CommandResult result, string eventKind)
        {
            var choiceEvent = FindEvent(result, GameEventKind.ChoiceResolved);
            var cardName = GetData(choiceEvent, "cardName");
            return "\u7ed3\u7b97\u4e86" + eventKind + "\u201c" + ValueOrFallback(cardName, "\u672a\u77e5\u4e8b\u4ef6") + "\u201d\u3002";
        }

        private static int GetDispatchCount(GameCommand command)
        {
            return string.IsNullOrEmpty(GetParameter(command, "source2")) ? 1 : 2;
        }

        private static string DescribeInfluenceSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId))
            {
                return "\u76ee\u6807\u4f4d\u7f6e";
            }

            const string locationPrefix = "location:";
            const string routePrefix = "route:";
            if (slotId.StartsWith(locationPrefix, StringComparison.Ordinal))
            {
                return "\u5730\u70b9 " + RemoveTrailingSlotIndex(slotId.Substring(locationPrefix.Length));
            }

            if (slotId.StartsWith(routePrefix, StringComparison.Ordinal))
            {
                return "\u8def\u7ebf " + RemoveTrailingSlotIndex(slotId.Substring(routePrefix.Length));
            }

            return "\u4f4d\u7f6e " + slotId;
        }

        private static string RemoveTrailingSlotIndex(string value)
        {
            var separatorIndex = value.LastIndexOf(':');
            return separatorIndex <= 0 ? value : value.Substring(0, separatorIndex);
        }

        private static void AddResource(List<string> resources, string encodedAmount, string resourceName)
        {
            int amount;
            if (int.TryParse(encodedAmount, out amount) && amount > 0)
            {
                resources.Add(resourceName + "\u00d7" + amount);
            }
        }

        private static GameEvent FindEvent(CommandResult result, GameEventKind kind)
        {
            if (result.Events == null)
            {
                return null;
            }

            for (var i = 0; i < result.Events.Count; i++)
            {
                var gameEvent = result.Events[i];
                if (gameEvent != null && gameEvent.Kind == kind)
                {
                    return gameEvent;
                }
            }

            return null;
        }

        private static GameEvent FindEventWithData(CommandResult result, string key)
        {
            if (result.Events == null)
            {
                return null;
            }

            for (var i = 0; i < result.Events.Count; i++)
            {
                var gameEvent = result.Events[i];
                if (gameEvent != null && gameEvent.Data != null && gameEvent.Data.ContainsKey(key))
                {
                    return gameEvent;
                }
            }

            return null;
        }

        private static string GetData(GameEvent gameEvent, string key)
        {
            if (gameEvent == null || gameEvent.Data == null)
            {
                return string.Empty;
            }

            string value;
            return gameEvent.Data.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command == null || command.Parameters == null)
            {
                return string.Empty;
            }

            string value;
            return command.Parameters.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
        }

        private static string ValueOrFallback(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static bool ContainsChinese(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] >= '\u4e00' && value[i] <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
