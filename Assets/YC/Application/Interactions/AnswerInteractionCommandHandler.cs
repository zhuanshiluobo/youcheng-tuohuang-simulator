using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Application.Sessions;

namespace YC.Application.Interactions
{
    /// <summary>
    /// Host 侧唯一的通用交互回答入口。命令只携带稳定交互 ID、稳定候选 ID/选项值和
    /// 提交时看到的 stateRevision；候选正文永远不从客户端进入规则层。
    /// </summary>
    public sealed class AnswerInteractionCommandHandler : IGameCommandHandler
    {
        public const string InteractionIdParameter = "interactionId";
        public const string ExpectedRevisionParameter = "expectedRevision";
        public const string AnswerValueParameter = "answerValue";

        private readonly EffectRegistry registry;
        private readonly RoundExecutionService roundExecutionService;

        public AnswerInteractionCommandHandler(
            EffectRegistry registry,
            RoundExecutionService roundExecutionService = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.roundExecutionService = roundExecutionService;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.AnswerInteraction;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            if (state == null || command == null)
            {
                return Invalid(CommandErrorCode.UnknownCommand, "交互回答命令为空。");
            }

            string interactionId = GetParameter(command, InteractionIdParameter);
            if (string.IsNullOrEmpty(interactionId)) interactionId = command.SourceId;
            if (string.IsNullOrEmpty(interactionId)) interactionId = command.TargetId;

            int expectedRevision;
            if (!int.TryParse(
                    GetParameter(command, ExpectedRevisionParameter),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out expectedRevision) || expectedRevision < 0)
            {
                return Invalid(CommandErrorCode.InvalidTarget, "交互回答缺少有效的 expectedRevision。");
            }

            NormalizedValue answer;
            string answerDiagnostic;
            if (!TryBuildAnswer(command, out answer, out answerDiagnostic))
            {
                return Invalid(CommandErrorCode.InvalidTarget, answerDiagnostic);
            }

            string diagnostic;
            var isCollectionTask = false;
            if (state.EffectRuntime != null && state.EffectRuntime.InteractionRequests != null)
            {
                var request = state.EffectRuntime.InteractionRequests.Find(candidate =>
                    candidate != null && candidate.Status == "open" &&
                    (candidate.RequestId == interactionId || candidate.GetStableInteractionId() == interactionId));
                isCollectionTask = request != null && request.InteractionTypeId == "collection.task";
            }
            var executor = roundExecutionService == null
                ? new EffectTreeExecutor(state, registry)
                : roundExecutionService.CreateCommandExecutor(state);
            if (!executor.TrySubmitInteraction(
                    interactionId,
                    command.PlayerId,
                    expectedRevision,
                    answer,
                    out diagnostic))
            {
                return Invalid(CommandErrorCode.InvalidTarget, diagnostic);
            }

            EffectRunReport report = executor.RunUntilQuiescent();
            if (report.Faulted)
            {
                string faultMessage = state.EffectRuntime == null
                    ? string.Empty
                    : state.EffectRuntime.LastFaultMessage ?? string.Empty;
                return Invalid(
                    CommandErrorCode.UnknownCommand,
                    "交互回答后 Effect 无法继续：" + report.FaultCode +
                    (string.IsNullOrEmpty(faultMessage) ? string.Empty : "（" + faultMessage + "）"));
            }

            if (roundExecutionService != null && !isCollectionTask)
            {
                ValidationResult advance = roundExecutionService.Advance(state);
                if (!advance.IsValid)
                {
                    return Invalid(
                        advance.ErrorCode,
                        "交互回答后回合主链无法继续：" + advance.Reason);
                }
            }

            // 通用回答入口也可能恢复回合主链的子交互。让同一份持久化主链
            // 立即投影回兼容的阶段/当前玩家字段，避免回答成功但外层仍停在旧阶段。
            new RoundExecutionProjector().Project(state);

            return CommandResult.SuccessResult(
                new List<YC.Domain.Events.GameEvent>(),
                "交互回答已提交。");
        }

        private static bool TryBuildAnswer(
            GameCommand command,
            out NormalizedValue answer,
            out string diagnostic)
        {
            answer = null;
            diagnostic = string.Empty;

            string answerValue = GetParameter(command, AnswerValueParameter);
            if (!string.IsNullOrEmpty(answerValue))
            {
                bool booleanValue;
                if (bool.TryParse(answerValue, out booleanValue))
                {
                    answer = NormalizedValue.CreateBoolean(booleanValue);
                }
                else
                {
                    answer = NormalizedValue.CreateString(answerValue);
                }

                return true;
            }

            if (command.OptionIds != null && command.OptionIds.Count > 0)
            {
                var values = new List<NormalizedValue>();
                for (int i = 0; i < command.OptionIds.Count; i++)
                {
                    if (string.IsNullOrEmpty(command.OptionIds[i]))
                    {
                        diagnostic = "交互回答包含空的候选 ID。";
                        return false;
                    }

                    values.Add(NormalizedValue.CreateStableReference("candidate", command.OptionIds[i]));
                }

                answer = NormalizedValue.CreateArray(values);
                return true;
            }

            if (!string.IsNullOrEmpty(command.TargetId))
            {
                answer = NormalizedValue.CreateStableReference("candidate", command.TargetId);
                return true;
            }

            diagnostic = "交互回答必须包含 answerValue、稳定候选 ID 或选项 ID。";
            return false;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command == null || command.Parameters == null) return string.Empty;
            string value;
            return command.Parameters.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
        }

        private static CommandResult Invalid(CommandErrorCode code, string reason)
        {
            return CommandResult.Invalid(ValidationResult.Failure(code, reason));
        }
    }
}
