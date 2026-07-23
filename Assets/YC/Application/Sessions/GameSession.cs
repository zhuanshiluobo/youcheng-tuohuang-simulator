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

                var before = PublicActionStateSnapshot.Capture(State);
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

                AppendLog(command, result, before);
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

        private void AppendLog(
            GameCommand command,
            CommandResult result,
            PublicActionStateSnapshot before)
        {
            if (!result.Succeeded)
            {
                return;
            }

            var after = PublicActionStateSnapshot.Capture(State);
            if (pendingActionLog != null && pendingActionLog.PlayerId == command.PlayerId)
            {
                pendingActionLog.Commands.Add(command);
                pendingActionLog.SettlementResult = result;
                if (State.HasPendingChoice())
                {
                    return;
                }

                string settledMessage;
                if (pendingActionLog.Kind == GameCommandKind.UseSpecialAction)
                {
                    settledMessage = PublicActionLogFormatter.CombineSpecialActionSettlement(
                        pendingActionLog.Message,
                        result.LogMessage);
                }
                else if (!PublicActionLogFormatter.TryFormat(
                             pendingActionLog.Command,
                             pendingActionLog.InitialResult,
                             pendingActionLog.Before,
                             after,
                             pendingActionLog.SettlementResult,
                             pendingActionLog.Commands,
                             out settledMessage))
                {
                    settledMessage = pendingActionLog.Message;
                }

                AppendLogEntry(
                    command.CommandId,
                    pendingActionLog.PlayerId,
                    settledMessage);
                pendingActionLog = null;
                return;
            }

            string message;
            var commands = new List<GameCommand> { command };
            var handledAsPublicAction = PublicActionLogFormatter.TryFormat(
                command,
                result,
                before,
                after,
                result,
                commands,
                out message);
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
                    Message = message,
                    Command = command,
                    InitialResult = result,
                    SettlementResult = result,
                    Before = before,
                    Commands = commands
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
            public GameCommand Command;
            public CommandResult InitialResult;
            public CommandResult SettlementResult;
            public PublicActionStateSnapshot Before;
            public List<GameCommand> Commands = new List<GameCommand>();
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
            return TryFormat(
                command,
                result,
                PublicActionStateSnapshot.Capture(null),
                PublicActionStateSnapshot.Capture(null),
                result,
                new List<GameCommand> { command },
                out message);
        }

        internal static bool TryFormat(
            GameCommand command,
            CommandResult result,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            CommandResult settlementResult,
            IReadOnlyList<GameCommand> settlementCommands,
            out string message)
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
                    message = FormatResolvedEvent(
                        settlementResult ?? result,
                        "\u5165\u573a\u4e8b\u4ef6",
                        before,
                        after,
                        command.PlayerId);
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
                    message = FormatExplore(
                        command,
                        result,
                        settlementResult,
                        before,
                        after);
                    return true;

                case GameCommandKind.MoveCity:
                    message = FormatMoveCity(command, result);
                    return true;

                case GameCommandKind.BuildFacility:
                    message = FormatBuild(command, result);
                    return true;

                case GameCommandKind.UseCharacterCard:
                    message = FormatCharacterCard(
                        command,
                        result,
                        before,
                        after,
                        settlementCommands);
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
                    return TryFormatResolvedChoice(
                        settlementResult ?? result,
                        before,
                        after,
                        command.PlayerId,
                        out message);

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

        private static string FormatCharacterCard(
            GameCommand command,
            CommandResult result,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            IReadOnlyList<GameCommand> settlementCommands)
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
            var modes = CollectCharacterEffectModes(command, settlementCommands);
            if (definition != null && definition.TemplateId == CharacterCardDatabase.Liskarm)
            {
                return FormatLiskarmEffects(
                    cardName,
                    command.PlayerId,
                    modes,
                    before,
                    after);
            }

            var changes = DescribePublicChanges(
                before,
                after,
                command.PlayerId,
                cardId,
                true);
            var detail = changes.Count == 0
                ? "\u5df2\u5b8c\u6210\u7ed3\u7b97"
                : string.Join("\uff1b", changes.ToArray());
            return "\u53d1\u52a8\u4e86\u89d2\u8272\u724c\u201c" +
                   ValueOrFallback(cardName, "\u672a\u77e5\u89d2\u8272\u724c") +
                   "\u201d\u7684" + FormatCharacterModeLabel(modes) + "\uff1a" + detail + "\u3002";
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

        private static string FormatExplore(
            GameCommand command,
            CommandResult result,
            CommandResult settlementResult,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after)
        {
            var cardEvent = FindEventWithData(result, "targetLocationId");
            if (cardEvent == null)
            {
                cardEvent = FindEventWithData(settlementResult, "targetLocationId");
            }

            var targetId = GetData(cardEvent, "targetLocationId");
            if (string.IsNullOrEmpty(targetId))
            {
                targetId = command.TargetId;
            }

            var cardName = GetData(cardEvent, "cardName");
            if (string.IsNullOrEmpty(cardName))
            {
                cardName = GetData(FindEventWithData(settlementResult, "cardName"), "cardName");
            }

            var eventText = string.IsNullOrEmpty(cardName)
                ? string.Empty
                : "\uff0c\u5e76\u7ed3\u7b97\u4e86\u4e8b\u4ef6\u201c" + cardName + "\u201d";
            var details = new List<string>();
            AddRewardDetail(details, settlementResult ?? result);
            AddScoreChangeDetail(details, before, after, command.PlayerId);
            AddInfluenceChangeDetails(details, before, after, command.PlayerId);
            var detailText = details.Count == 0
                ? string.Empty
                : "\uff1a" + string.Join("\uff1b", details.ToArray());
            return "\u63a2\u7d22\u4e86\u5730\u70b9 " + ValueOrFallback(targetId, "?") +
                   eventText + detailText + "\u3002";
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

        private static bool TryFormatResolvedChoice(
            CommandResult result,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId,
            out string message)
        {
            message = string.Empty;
            var cardEvent = FindEvent(result, GameEventKind.CardMoved);
            if (cardEvent != null)
            {
                var character = CharacterCardDatabase.Get(cardEvent.SubjectId);
                if (character != null)
                {
                    var changes = DescribePublicChanges(
                        before,
                        after,
                        playerId,
                        cardEvent.SubjectId,
                        true);
                    message = "\u7ed3\u7b97\u4e86\u89d2\u8272\u724c\u201c" + character.Name +
                              "\u201d\u7684\u6548\u679c\uff1a" +
                              (changes.Count == 0
                                  ? "\u5df2\u5b8c\u6210\u7ed3\u7b97"
                                  : string.Join("\uff1b", changes.ToArray())) +
                              "\u3002";
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

        private static string FormatResolvedEvent(
            CommandResult result,
            string eventKind,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId)
        {
            var choiceEvent = FindEvent(result, GameEventKind.ChoiceResolved);
            var cardName = GetData(choiceEvent, "cardName");
            var details = new List<string>();
            AddRewardDetail(details, result);
            AddScoreChangeDetail(details, before, after, playerId);
            AddInfluenceChangeDetails(details, before, after, playerId);
            var detailText = details.Count == 0
                ? "\u5df2\u5b8c\u6210\u7ed3\u7b97"
                : string.Join("\uff1b", details.ToArray());
            return "\u7ed3\u7b97\u4e86" + eventKind + "\u201c" +
                   ValueOrFallback(cardName, "\u672a\u77e5\u4e8b\u4ef6") +
                   "\u201d\uff1a" + detailText + "\u3002";
        }

        private static List<string> CollectCharacterEffectModes(
            GameCommand originalCommand,
            IReadOnlyList<GameCommand> settlementCommands)
        {
            var modes = new List<string>();
            if (settlementCommands != null)
            {
                for (var i = 0; i < settlementCommands.Count; i++)
                {
                    var command = settlementCommands[i];
                    if (command == null || command.Kind != GameCommandKind.UseCharacterCard)
                    {
                        continue;
                    }

                    AddCharacterEffectMode(modes, GetParameter(command, "effectMode"));
                }
            }

            if (modes.Count == 0)
            {
                AddCharacterEffectMode(modes, GetParameter(originalCommand, "effectMode"));
            }

            return modes;
        }

        private static void AddCharacterEffectMode(List<string> modes, string mode)
        {
            if (mode == CharacterEffectModes.Both)
            {
                AddUnique(modes, CharacterEffectModes.Strategy);
                AddUnique(modes, CharacterEffectModes.Tactic);
                return;
            }

            if (mode == CharacterEffectModes.Strategy || mode == CharacterEffectModes.Tactic)
            {
                AddUnique(modes, mode);
            }
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        private static string FormatCharacterModeLabel(List<string> modes)
        {
            var hasStrategy = modes.Contains(CharacterEffectModes.Strategy);
            var hasTactic = modes.Contains(CharacterEffectModes.Tactic);
            if (hasStrategy && hasTactic)
            {
                return "\u7b56\u7565\u4e0e\u8ba1\u8c0b\u6548\u679c";
            }

            if (hasStrategy)
            {
                return "\u7b56\u7565\u6548\u679c";
            }

            if (hasTactic)
            {
                return "\u8ba1\u8c0b\u6548\u679c";
            }

            return "\u6548\u679c";
        }

        private static string FormatLiskarmEffects(
            string cardName,
            int playerId,
            List<string> modes,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after)
        {
            var activations = new List<string>();
            if (modes.Contains(CharacterEffectModes.Strategy))
            {
                var placements = CollectNewInfluenceSlots(before, after, playerId);
                var detail = placements.Count == 0
                    ? "\u5df2\u5b8c\u6210\u7ed3\u7b97"
                    : "\u5728" + JoinTargets(placements) + "\u653e\u7f6e\u4e86\u5f71\u54cd\u529b";
                activations.Add(BuildCharacterEffectActivation(cardName, "\u7b56\u7565", detail));
            }

            if (modes.Contains(CharacterEffectModes.Tactic))
            {
                var tacticDetails = new List<string>();
                AddOpponentInfluenceChangeDetails(tacticDetails, before, after, playerId);
                AddActorResourceLossDetail(tacticDetails, before, after, playerId);
                activations.Add(BuildCharacterEffectActivation(
                    cardName,
                    "\u8ba1\u8c0b",
                    tacticDetails.Count == 0
                        ? "\u5df2\u5b8c\u6210\u7ed3\u7b97"
                        : string.Join("\uff0c\u5e76", tacticDetails.ToArray())));
            }

            if (activations.Count == 0)
            {
                activations.Add(BuildCharacterEffectActivation(
                    cardName,
                    string.Empty,
                    "\u5df2\u5b8c\u6210\u7ed3\u7b97"));
            }

            return string.Join("\uff1b", activations.ToArray()) + "\u3002";
        }

        private static string BuildCharacterEffectActivation(
            string cardName,
            string modeName,
            string detail)
        {
            var modeText = string.IsNullOrEmpty(modeName) ? string.Empty : modeName;
            return "\u53d1\u52a8\u4e86\u89d2\u8272\u724c\u201c" +
                   ValueOrFallback(cardName, "\u672a\u77e5\u89d2\u8272\u724c") +
                   "\u201d\u7684" + modeText + "\u6548\u679c\uff1a" + detail;
        }

        private static List<string> DescribePublicChanges(
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId,
            string characterCardId,
            bool includeOtherPlayers)
        {
            var details = new List<string>();
            AddResourceChangeDetails(details, before, after, playerId, includeOtherPlayers);
            AddScoreChangeDetail(details, before, after, playerId);
            AddCityMoveDetail(details, before, after, playerId);
            AddInfluenceChangeDetails(details, before, after, playerId);
            AddRecalledCardDetail(details, before, after, playerId, characterCardId);
            AddFacilitySupplyChangeDetails(details, before, after);
            return details;
        }

        private static void AddRewardDetail(List<string> details, CommandResult result)
        {
            var rewardEvent = FindEventWithData(result, "rewardOriginium");
            if (rewardEvent == null)
            {
                rewardEvent = FindEventWithData(result, "rewardOriginiumShard");
            }

            if (rewardEvent == null)
            {
                rewardEvent = FindEventWithData(result, "rewardIron");
            }

            if (rewardEvent == null)
            {
                rewardEvent = FindEventWithData(result, "rewardPureOriginium");
            }

            if (rewardEvent == null)
            {
                rewardEvent = FindEventWithData(result, "rewardGoldVoucher");
            }

            var resources = BuildResourceList(
                GetData(rewardEvent, "rewardOriginium"),
                GetData(rewardEvent, "rewardOriginiumShard"),
                GetData(rewardEvent, "rewardIron"),
                GetData(rewardEvent, "rewardPureOriginium"),
                GetData(rewardEvent, "rewardGoldVoucher"));
            if (resources.Count > 0)
            {
                details.Add("\u83b7\u5f97\u4e86" + string.Join("\u3001", resources.ToArray()));
            }
        }

        private static List<string> BuildResourceList(
            string originium,
            string originiumShard,
            string iron,
            string pureOriginium,
            string goldVoucher)
        {
            var resources = new List<string>();
            AddResource(resources, originium, "\u6e90\u77f3");
            AddResource(resources, originiumShard, "\u6e90\u77f3\u788e\u7247");
            AddResource(resources, iron, "\u94c1");
            AddResource(resources, pureOriginium, "\u81f3\u7eaf\u6e90\u77f3");
            AddResource(resources, goldVoucher, "\u91d1\u5238");
            return resources;
        }

        private static void AddResourceChangeDetails(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId,
            bool includeOtherPlayers)
        {
            if (before == null || after == null)
            {
                return;
            }

            var playerIds = new List<int>(after.Players.Keys);
            playerIds.Sort();
            for (var i = 0; i < playerIds.Count; i++)
            {
                var changedPlayerId = playerIds[i];
                if (!includeOtherPlayers && changedPlayerId != playerId)
                {
                    continue;
                }

                var oldPlayer = before.FindPlayer(changedPlayerId);
                var newPlayer = after.FindPlayer(changedPlayerId);
                if (oldPlayer == null || newPlayer == null)
                {
                    continue;
                }

                var gains = BuildResourceDeltaList(oldPlayer.Resources, newPlayer.Resources, true);
                var losses = BuildResourceDeltaList(oldPlayer.Resources, newPlayer.Resources, false);
                var subject = changedPlayerId == playerId
                    ? string.Empty
                    : after.GetPlayerLabel(changedPlayerId);
                if (gains.Count > 0)
                {
                    details.Add(subject + "\u83b7\u5f97\u4e86" + string.Join("\u3001", gains.ToArray()));
                }

                if (losses.Count > 0)
                {
                    details.Add(subject + "\u6d88\u8017\u4e86" + string.Join("\u3001", losses.ToArray()));
                }
            }
        }

        private static void AddActorResourceLossDetail(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId)
        {
            var oldPlayer = before == null ? null : before.FindPlayer(playerId);
            var newPlayer = after == null ? null : after.FindPlayer(playerId);
            if (oldPlayer == null || newPlayer == null)
            {
                return;
            }

            var losses = BuildResourceDeltaList(oldPlayer.Resources, newPlayer.Resources, false);
            if (losses.Count > 0)
            {
                details.Add("\u652f\u4ed8\u4e86" + string.Join("\u3001", losses.ToArray()));
            }
        }

        private static List<string> BuildResourceDeltaList(
            ResourceSet before,
            ResourceSet after,
            bool positive)
        {
            var resources = new List<string>();
            AddResourceDelta(resources, after.Originium - before.Originium, positive, "\u6e90\u77f3");
            AddResourceDelta(resources, after.OriginiumShard - before.OriginiumShard, positive, "\u6e90\u77f3\u788e\u7247");
            AddResourceDelta(resources, after.Iron - before.Iron, positive, "\u94c1");
            AddResourceDelta(resources, after.PureOriginium - before.PureOriginium, positive, "\u81f3\u7eaf\u6e90\u77f3");
            AddResourceDelta(resources, after.GoldVoucher - before.GoldVoucher, positive, "\u91d1\u5238");
            return resources;
        }

        private static void AddResourceDelta(
            List<string> resources,
            int delta,
            bool positive,
            string resourceName)
        {
            if ((positive && delta > 0) || (!positive && delta < 0))
            {
                resources.Add(resourceName + "\u00d7" + Math.Abs(delta));
            }
        }

        private static void AddScoreChangeDetail(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId)
        {
            var oldPlayer = before == null ? null : before.FindPlayer(playerId);
            var newPlayer = after == null ? null : after.FindPlayer(playerId);
            if (oldPlayer == null || newPlayer == null)
            {
                return;
            }

            var delta = newPlayer.Score - oldPlayer.Score;
            if (delta > 0)
            {
                details.Add("\u83b7\u5f97\u4e86 " + delta + " \u5206");
            }
            else if (delta < 0)
            {
                details.Add("\u5931\u53bb\u4e86 " + Math.Abs(delta) + " \u5206");
            }
        }

        private static void AddCityMoveDetail(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId)
        {
            var oldPlayer = before == null ? null : before.FindPlayer(playerId);
            var newPlayer = after == null ? null : after.FindPlayer(playerId);
            if (oldPlayer == null || newPlayer == null ||
                oldPlayer.CityLocationId == newPlayer.CityLocationId)
            {
                return;
            }

            details.Add("\u5c06\u57ce\u5e02\u4ece\u5730\u70b9 " +
                        ValueOrFallback(oldPlayer.CityLocationId, "?") +
                        " \u79fb\u52a8\u81f3\u5730\u70b9 " +
                        ValueOrFallback(newPlayer.CityLocationId, "?"));
        }

        private static void AddInfluenceChangeDetails(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId)
        {
            if (before == null || after == null)
            {
                return;
            }

            var added = CollectNewInfluenceSlots(before, after, playerId);
            var removedOwn = new List<string>();
            var removedOpponentDetails = new List<string>();
            var replacementDetails = new List<string>();
            var beforeSlots = new List<string>(before.Influences.Keys);
            beforeSlots.Sort(StringComparer.Ordinal);
            for (var i = 0; i < beforeSlots.Count; i++)
            {
                var slotId = beforeSlots[i];
                var oldInfluence = before.Influences[slotId];
                PublicInfluenceActionSnapshot newInfluence;
                if (after.Influences.TryGetValue(slotId, out newInfluence))
                {
                    if (oldInfluence.PlayerId != newInfluence.PlayerId &&
                        newInfluence.PlayerId == playerId)
                    {
                        replacementDetails.Add(
                            "\u66ff\u6362\u4e86" + DescribeInfluenceSlot(slotId) +
                            "\u7684 " + before.GetPlayerLabel(oldInfluence.PlayerId) +
                            " \u5f71\u54cd\u529b");
                    }

                    continue;
                }

                if (oldInfluence.PlayerId == playerId)
                {
                    removedOwn.Add(slotId);
                }
                else
                {
                    removedOpponentDetails.Add(
                        "\u79fb\u9664\u4e86" + DescribeInfluenceSlot(slotId) +
                        "\u7684 " + before.GetPlayerLabel(oldInfluence.PlayerId) +
                        " \u5f71\u54cd\u529b");
                }
            }

            var moveCount = Math.Min(removedOwn.Count, added.Count);
            for (var i = 0; i < moveCount; i++)
            {
                details.Add("\u5c06\u5f71\u54cd\u529b\u4ece" +
                            DescribeInfluenceSlot(removedOwn[i]) +
                            "\u79fb\u52a8\u81f3" +
                            DescribeInfluenceSlot(added[i]));
            }

            if (moveCount > 0)
            {
                removedOwn.RemoveRange(0, moveCount);
                added.RemoveRange(0, moveCount);
            }

            if (added.Count > 0)
            {
                details.Add("\u5728" + JoinTargets(added) + "\u653e\u7f6e\u4e86\u5f71\u54cd\u529b");
            }

            details.AddRange(replacementDetails);
            details.AddRange(removedOpponentDetails);
            for (var i = 0; i < removedOwn.Count; i++)
            {
                details.Add("\u79fb\u9664\u4e86" + DescribeInfluenceSlot(removedOwn[i]) +
                            "\u7684\u5df1\u65b9\u5f71\u54cd\u529b");
            }
        }

        private static List<string> CollectNewInfluenceSlots(
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId)
        {
            var slots = new List<string>();
            if (before == null || after == null)
            {
                return slots;
            }

            var afterSlots = new List<string>(after.Influences.Keys);
            afterSlots.Sort(StringComparer.Ordinal);
            for (var i = 0; i < afterSlots.Count; i++)
            {
                var slotId = afterSlots[i];
                var influence = after.Influences[slotId];
                if (influence.PlayerId == playerId && !before.Influences.ContainsKey(slotId))
                {
                    slots.Add(slotId);
                }
            }

            return slots;
        }

        private static void AddOpponentInfluenceChangeDetails(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId)
        {
            if (before == null || after == null)
            {
                return;
            }

            var slots = new List<string>(before.Influences.Keys);
            slots.Sort(StringComparer.Ordinal);
            for (var i = 0; i < slots.Count; i++)
            {
                var slotId = slots[i];
                var oldInfluence = before.Influences[slotId];
                if (oldInfluence.PlayerId == playerId)
                {
                    continue;
                }

                PublicInfluenceActionSnapshot newInfluence;
                if (after.Influences.TryGetValue(slotId, out newInfluence) &&
                    newInfluence.PlayerId == playerId)
                {
                    details.Add("\u66ff\u6362\u4e86" + DescribeInfluenceSlot(slotId) +
                                "\u7684 " + before.GetPlayerLabel(oldInfluence.PlayerId) +
                                " \u5f71\u54cd\u529b");
                }
                else if (!after.Influences.ContainsKey(slotId))
                {
                    details.Add("\u79fb\u9664\u4e86" + DescribeInfluenceSlot(slotId) +
                                "\u7684 " + before.GetPlayerLabel(oldInfluence.PlayerId) +
                                " \u5f71\u54cd\u529b");
                }
            }
        }

        private static string JoinTargets(List<string> slotIds)
        {
            var targets = new List<string>();
            for (var i = 0; i < slotIds.Count; i++)
            {
                targets.Add(DescribeInfluenceSlot(slotIds[i]));
            }

            if (targets.Count <= 1)
            {
                return targets.Count == 0 ? string.Empty : targets[0];
            }

            return string.Join("\u3001", targets.GetRange(0, targets.Count - 1).ToArray()) +
                   " \u548c" + targets[targets.Count - 1];
        }

        private static void AddRecalledCardDetail(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after,
            int playerId,
            string activatedCardId)
        {
            var oldPlayer = before == null ? null : before.FindPlayer(playerId);
            var newPlayer = after == null ? null : after.FindPlayer(playerId);
            if (oldPlayer == null || newPlayer == null)
            {
                return;
            }

            var recalledNames = new List<string>();
            foreach (var cardId in oldPlayer.DiscardCardIds)
            {
                if (cardId == activatedCardId || newPlayer.DiscardCardIds.Contains(cardId))
                {
                    continue;
                }

                var definition = CharacterCardDatabase.Get(cardId);
                recalledNames.Add("\u201c" + (definition == null ? cardId : definition.Name) + "\u201d");
            }

            recalledNames.Sort(StringComparer.Ordinal);
            if (recalledNames.Count > 0)
            {
                details.Add("\u4ece\u5f03\u724c\u5806\u6536\u56de\u4e86\u89d2\u8272\u724c" +
                            string.Join("\u3001", recalledNames.ToArray()));
            }
        }

        private static void AddFacilitySupplyChangeDetails(
            List<string> details,
            PublicActionStateSnapshot before,
            PublicActionStateSnapshot after)
        {
            if (before == null || after == null)
            {
                return;
            }

            var removed = new List<string>();
            foreach (var facilityId in before.FacilitySupply)
            {
                if (after.FacilitySupply.Contains(facilityId))
                {
                    continue;
                }

                var definition = FacilityCardDatabase.Get(facilityId);
                removed.Add("\u201c" + (definition == null ? facilityId : definition.Name) + "\u201d");
            }

            removed.Sort(StringComparer.Ordinal);
            if (removed.Count > 0)
            {
                details.Add("\u5c06\u4f9b\u5e94\u533a\u7684\u5efa\u7b51" +
                            string.Join("\u3001", removed.ToArray()) +
                            "\u9001\u56de\u724c\u5806");
            }
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
            if (result == null || result.Events == null)
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
            if (result == null || result.Events == null)
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
