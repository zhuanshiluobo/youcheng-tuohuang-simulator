using System;
using YC.Domain.Commands;
using YC.Domain.Rules;

namespace YC.Presentation.Workflows
{
    public enum SubmitOutcomeKind
    {
        NoResult,
        Rejected,
        WaitingForHost,
        AppliedLocally
    }

    public sealed class SubmitOutcome
    {
        internal SubmitOutcome(
            SubmitOutcomeKind kind,
            WorkflowSubmissionResult submission)
        {
            Kind = kind;
            Submission = submission;
        }

        public SubmitOutcomeKind Kind { get; private set; }

        public WorkflowSubmissionResult Submission { get; private set; }

        public CommandResult CommandResult
        {
            get { return Submission == null ? null : Submission.CommandResult; }
        }
    }

    public sealed class SubmitCallbacks
    {
        public SubmitCallbacks(
            Action<string> showPrompt,
            string waitingForHostPrompt)
        {
            ShowPrompt = showPrompt ?? throw new ArgumentNullException(nameof(showPrompt));
            WaitingForHostPrompt = waitingForHostPrompt ?? string.Empty;
            MissingResultPrompt = CommandGateway.BuildMissingResultPrompt("命令");
        }

        public Action<string> ShowPrompt { get; private set; }

        public string WaitingForHostPrompt { get; private set; }

        public string MissingResultPrompt { get; set; }

        public Action<CommandResult> BeforeRejectedPrompt { get; set; }

        public Action<CommandResult> AfterRejectedPrompt { get; set; }

        public Action<CommandResult> OnAppliedLocally { get; set; }
    }

    public sealed class CommandGateway
    {
        private readonly IGameCommandPort commandPort;

        public CommandGateway(IGameCommandPort commandPort)
        {
            this.commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        }

        public SubmitOutcome Submit(
            GameCommand command,
            SubmitCallbacks callbacks)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (callbacks == null)
            {
                throw new ArgumentNullException(nameof(callbacks));
            }

            var submission = commandPort.Submit(command);
            if (submission == null || submission.CommandResult == null)
            {
                var failure = CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand, callbacks.MissingResultPrompt));
                callbacks.BeforeRejectedPrompt?.Invoke(failure);
                callbacks.ShowPrompt(callbacks.MissingResultPrompt);
                callbacks.AfterRejectedPrompt?.Invoke(failure);
                return new SubmitOutcome(SubmitOutcomeKind.NoResult, submission);
            }

            var result = submission.CommandResult;
            if (!result.Succeeded)
            {
                if (callbacks.BeforeRejectedPrompt != null)
                {
                    callbacks.BeforeRejectedPrompt(result);
                }

                callbacks.ShowPrompt(result.Validation.Reason);
                if (callbacks.AfterRejectedPrompt != null)
                {
                    callbacks.AfterRejectedPrompt(result);
                }

                return new SubmitOutcome(SubmitOutcomeKind.Rejected, submission);
            }

            if (!submission.AppliedLocally)
            {
                callbacks.ShowPrompt(callbacks.WaitingForHostPrompt);
                return new SubmitOutcome(SubmitOutcomeKind.WaitingForHost, submission);
            }

            if (callbacks.OnAppliedLocally != null)
            {
                callbacks.OnAppliedLocally(result);
            }

            return new SubmitOutcome(SubmitOutcomeKind.AppliedLocally, submission);
        }

        public static string BuildWaitingForHostPrompt(string subject)
        {
            return (subject ?? string.Empty) + "已发送给主机，等待确认。";
        }

        public static string BuildMissingResultPrompt(string subject)
        {
            return (subject ?? string.Empty) + "未返回结果。";
        }
    }
}
