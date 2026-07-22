using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class UseSpecialActionCommandHandler : IGameCommandHandler
    {
        public const string SpecialActionIdParameter = "specialActionId";
        public const string DeclarationMarkerIdParameter = "declarationMarkerId";
        public const string SessionIdParameter = "specialActionSessionId";
        public const string InfluenceSlotIdsParameter = "influenceSlotIds";
        public const string TargetInfluenceSlotIdParameter = "targetInfluenceSlotId";
        public const string OriginiumAmountParameter = "originiumAmount";
        public const string IronAmountParameter = "ironAmount";
        public const string TargetLocationIdParameter = "targetLocationId";
        public const string RouteInfluenceSlotIdParameter = "routeInfluenceSlotId";

        private readonly SpecialActionService specialActionService;
        private readonly SpecialActionOptionQueryService optionQuery;
        private readonly MoveCityCommandHandler moveCityCommandHandler;

        public UseSpecialActionCommandHandler(
            SpecialActionService specialActionService,
            SpecialActionOptionQueryService optionQuery,
            MoveCityCommandHandler moveCityCommandHandler)
        {
            this.specialActionService = specialActionService ?? throw new ArgumentNullException(nameof(specialActionService));
            this.optionQuery = optionQuery ?? throw new ArgumentNullException(nameof(optionQuery));
            this.moveCityCommandHandler = moveCityCommandHandler ?? throw new ArgumentNullException(nameof(moveCityCommandHandler));
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null &&
                   (command.Kind == GameCommandKind.UseSpecialAction ||
                    command.Kind == GameCommandKind.ResolvePendingChoice);
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

            return command.Kind == GameCommandKind.UseSpecialAction
                ? HandleBegin(state, command)
                : HandleResolve(state, command);
        }

        private CommandResult HandleBegin(GameState state, GameCommand command)
        {
            var specialActionId = GetParameter(command, SpecialActionIdParameter);
            if (string.IsNullOrEmpty(specialActionId))
            {
                specialActionId = command.TargetId;
            }

            var markerId = GetParameter(command, DeclarationMarkerIdParameter);
            if (string.IsNullOrEmpty(markerId))
            {
                markerId = command.SourceId;
            }

            var originiumAmount = -1;
            var ironAmount = -1;
            var definition = SpecialActionDatabase.Get(specialActionId);
            if (definition != null && definition.EffectKind == SpecialActionEffectKind.CompositePowerMove &&
                (!TryGetNonNegativeInt(command, OriginiumAmountParameter, out originiumAmount) ||
                 !TryGetNonNegativeInt(command, IronAmountParameter, out ironAmount)))
            {
                return Invalid(
                    CommandErrorCode.InvalidTarget,
                    "复合动力系统必须在首条命令中明确提交非负的源岩与异铁支付数量。");
            }

            var result = specialActionService.Begin(
                state,
                command.PlayerId,
                specialActionId,
                markerId,
                command.CommandId,
                originiumAmount,
                ironAmount);
            return ToCommandResult(state, command.PlayerId, string.Empty, result, null);
        }

        private CommandResult HandleResolve(GameState state, GameCommand command)
        {
            var pending = state.PendingSpecialAction;
            if (pending == null || !pending.IsValid())
            {
                return Invalid(CommandErrorCode.PendingChoiceRequired, "当前没有待处理的特殊行动会话。");
            }

            if (pending.PlayerId != command.PlayerId)
            {
                return Invalid(CommandErrorCode.NotCurrentPlayer, "只能由发动特殊行动的玩家继续结算。");
            }

            var suppliedSessionId = GetParameter(command, SessionIdParameter);
            if (string.IsNullOrEmpty(suppliedSessionId) && command.SourceId == pending.SessionId)
            {
                suppliedSessionId = command.SourceId;
            }

            if (string.IsNullOrEmpty(suppliedSessionId))
            {
                return Invalid(CommandErrorCode.InvalidSource, "继续结算特殊行动时必须携带会话 ID。");
            }

            if (!string.IsNullOrEmpty(suppliedSessionId) && suppliedSessionId != pending.SessionId)
            {
                return Invalid(CommandErrorCode.InvalidSource, "特殊行动会话已经过期，请按最新状态重新选择。");
            }

            var previousStep = pending.Step;
            switch (previousStep)
            {
                case SpecialActionPendingSteps.AwaitMilitaryTargets:
                    return ToCommandResult(
                        state,
                        command.PlayerId,
                        previousStep,
                        specialActionService.ResolveMilitaryTargets(
                            state,
                            command.PlayerId,
                            ResolveIds(command, InfluenceSlotIdsParameter)),
                        null);

                case SpecialActionPendingSteps.AwaitMobilizationTarget:
                    return ToCommandResult(
                        state,
                        command.PlayerId,
                        previousStep,
                        specialActionService.ResolveMobilizationTarget(
                            state,
                            command.PlayerId,
                            ResolveSingleId(command, TargetInfluenceSlotIdParameter)),
                        null);

                case SpecialActionPendingSteps.AwaitCompositePayment:
                    return HandleCompositePayment(state, command, previousStep);

                case SpecialActionPendingSteps.AwaitFreeMoveTarget:
                    return HandleFreeMove(state, command, previousStep);

                case SpecialActionPendingSteps.AwaitMoveEvent:
                    return HandleMoveEvent(state, command, previousStep);

                case SpecialActionPendingSteps.AwaitRouteInfluence:
                    return ToCommandResult(
                        state,
                        command.PlayerId,
                        previousStep,
                        specialActionService.ResolveRouteInfluence(
                            state,
                            command.PlayerId,
                            ResolveSingleId(command, RouteInfluenceSlotIdParameter)),
                        null);

                default:
                    return Invalid(CommandErrorCode.InvalidTarget, "未知的特殊行动待处理步骤。");
            }
        }

        private CommandResult HandleCompositePayment(GameState state, GameCommand command, string previousStep)
        {
            int originiumAmount;
            int ironAmount;
            if (!TryGetNonNegativeInt(command, OriginiumAmountParameter, out originiumAmount) ||
                !TryGetNonNegativeInt(command, IronAmountParameter, out ironAmount))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "必须明确提交非负的源岩与异铁支付数量。");
            }

            var result = specialActionService.ResolveCompositePayment(
                state,
                command.PlayerId,
                originiumAmount,
                ironAmount);
            return ToCommandResult(state, command.PlayerId, previousStep, result, null);
        }

        private CommandResult HandleFreeMove(GameState state, GameCommand command, string previousStep)
        {
            if (optionQuery.GetLegalFreeMoveTargetIds(state, command.PlayerId).Count == 0)
            {
                return ToCommandResult(
                    state,
                    command.PlayerId,
                    previousStep,
                    specialActionService.SkipFreeMoveWhenNoTarget(state, command.PlayerId),
                    null);
            }

            var targetLocationId = ResolveSingleId(command, TargetLocationIdParameter);
            if (string.IsNullOrEmpty(targetLocationId))
            {
                return Invalid(CommandErrorCode.InvalidTarget, "必须选择免费移动的目标地点。");
            }

            CityMovementResult movementResult;
            var moveCommandResult = moveCityCommandHandler.HandleGrantedMove(
                state,
                command,
                targetLocationId,
                out movementResult);
            if (!moveCommandResult.Succeeded)
            {
                return moveCommandResult;
            }

            var awaitingMoveEvent = state.PendingCardSession != null && state.PendingCardSession.IsValid();
            var specialResult = specialActionService.RecordFreeMoveCompleted(
                state,
                command.PlayerId,
                movementResult.RouteId,
                awaitingMoveEvent);
            return ToCommandResult(
                state,
                command.PlayerId,
                previousStep,
                specialResult,
                moveCommandResult.Events);
        }

        private CommandResult HandleMoveEvent(GameState state, GameCommand command, string previousStep)
        {
            var moveCommandResult = moveCityCommandHandler.Handle(state, command);
            if (!moveCommandResult.Succeeded)
            {
                return moveCommandResult;
            }

            var specialResult = specialActionService.ResumeAfterMoveEvent(state, command.PlayerId);
            return ToCommandResult(
                state,
                command.PlayerId,
                previousStep,
                specialResult,
                moveCommandResult.Events);
        }

        private static CommandResult ToCommandResult(
            GameState state,
            int playerId,
            string previousStep,
            SpecialActionOperationResult result,
            IReadOnlyList<GameEvent> precedingEvents)
        {
            if (result == null)
            {
                return Invalid(CommandErrorCode.UnknownCommand, "特殊行动未返回结算结果。");
            }

            if (!result.Succeeded)
            {
                return CommandResult.Invalid(result.Validation);
            }

            var events = precedingEvents == null
                ? new List<GameEvent>()
                : new List<GameEvent>(precedingEvents);
            var subjectId = result.Definition == null ? string.Empty : result.Definition.SpecialActionId;
            if (!string.IsNullOrEmpty(previousStep))
            {
                events.Add(new GameEvent
                {
                    Kind = GameEventKind.ChoiceResolved,
                    PlayerId = playerId,
                    SubjectId = subjectId,
                    Message = "特殊行动步骤已完成。",
                    Data =
                    {
                        { "specialActionStep", previousStep }
                    }
                });
            }

            var pending = state.PendingSpecialAction;
            if (!result.Completed && pending != null && pending.IsValid())
            {
                events.Add(new GameEvent
                {
                    Kind = GameEventKind.ChoiceOpened,
                    PlayerId = playerId,
                    SubjectId = pending.SpecialActionId,
                    Message = result.Summary,
                    Data =
                    {
                        { "specialActionSessionId", pending.SessionId },
                        { "specialActionStep", pending.Step },
                        { "declarationMarkerId", pending.DeclarationMarkerId },
                        { "remainingRepetitions", pending.RemainingRepetitions.ToString(CultureInfo.InvariantCulture) },
                        { "traversedRouteId", pending.TraversedRouteId ?? string.Empty }
                    }
                });
            }
            else
            {
                events.Add(new GameEvent
                {
                    Kind = GameEventKind.LogOnly,
                    PlayerId = playerId,
                    SubjectId = subjectId,
                    Message = result.Summary
                });
            }

            return CommandResult.SuccessResult(events, result.Summary);
        }

        private static List<string> ResolveIds(GameCommand command, string parameterName)
        {
            var result = SplitIds(GetParameter(command, parameterName));
            if (result.Count == 0 && command.OptionIds != null)
            {
                for (var i = 0; i < command.OptionIds.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(command.OptionIds[i]))
                    {
                        result.Add(command.OptionIds[i].Trim());
                    }
                }
            }

            return result;
        }

        private static string ResolveSingleId(GameCommand command, string parameterName)
        {
            var value = GetParameter(command, parameterName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(command.TargetId))
            {
                return command.TargetId.Trim();
            }

            return command.OptionIds != null && command.OptionIds.Count > 0
                ? command.OptionIds[0]
                : string.Empty;
        }

        private static bool TryGetNonNegativeInt(GameCommand command, string parameterName, out int value)
        {
            return int.TryParse(
                       GetParameter(command, parameterName),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out value) &&
                   value >= 0;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command == null || command.Parameters == null)
            {
                return string.Empty;
            }

            string value;
            return command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static List<string> SplitIds(string encoded)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return result;
            }

            var parts = encoded.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var value = parts[i].Trim();
                if (value.Length > 0)
                {
                    result.Add(value);
                }
            }

            return result;
        }

        private static CommandResult Invalid(CommandErrorCode code, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(code, reason));
        }
    }
}
