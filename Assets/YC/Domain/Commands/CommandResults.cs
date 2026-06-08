using System.Collections.Generic;
using YC.Domain.Events;
using YC.Domain.Rules;

namespace YC.Domain.Commands
{
    public sealed class ValidationResult
    {
        public static readonly ValidationResult Success = new ValidationResult(true, CommandErrorCode.None, string.Empty);

        public bool IsValid { get; private set; }
        public CommandErrorCode ErrorCode { get; private set; }
        public string Reason { get; private set; }

        public ValidationResult(bool isValid, CommandErrorCode errorCode, string reason)
        {
            IsValid = isValid;
            ErrorCode = errorCode;
            Reason = reason;
        }

        public static ValidationResult Failure(CommandErrorCode errorCode, string reason)
        {
            return new ValidationResult(false, errorCode, reason);
        }
    }

    public sealed class CommandResult
    {
        public bool Succeeded { get; private set; }
        public ValidationResult Validation { get; private set; }
        public List<GameEvent> Events { get; private set; }
        public string LogMessage { get; private set; }

        private CommandResult(bool succeeded, ValidationResult validation, List<GameEvent> events, string logMessage)
        {
            Succeeded = succeeded;
            Validation = validation;
            Events = events;
            LogMessage = logMessage;
        }

        public static CommandResult Invalid(ValidationResult validation)
        {
            return new CommandResult(false, validation, new List<GameEvent>(), validation.Reason);
        }

        public static CommandResult SuccessResult(List<GameEvent> events, string logMessage)
        {
            return new CommandResult(true, ValidationResult.Success, events ?? new List<GameEvent>(), logMessage);
        }
    }
}
