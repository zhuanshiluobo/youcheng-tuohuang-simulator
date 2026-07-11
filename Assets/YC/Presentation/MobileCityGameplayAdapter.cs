using System;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>Bridges workflow presenters to the current session and the single command submission entry point.</summary>
    internal sealed class MobileCityGameplayAdapter : IWritableGameplayContext, IGameCommandPort
    {
        private readonly Func<GameState> getState;
        private readonly Func<int> getLocalPlayerId;
        private readonly Func<bool> controlsCurrentPlayerLocally;
        private readonly Action<int> setLocalPlayerId;
        private readonly Func<CommandSubmissionController> getCommandSubmission;

        public MobileCityGameplayAdapter(
            Func<GameState> getState,
            Func<int> getLocalPlayerId,
            Func<bool> controlsCurrentPlayerLocally,
            Action<int> setLocalPlayerId,
            Func<CommandSubmissionController> getCommandSubmission)
        {
            this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
            this.getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
            this.controlsCurrentPlayerLocally = controlsCurrentPlayerLocally ?? throw new ArgumentNullException(nameof(controlsCurrentPlayerLocally));
            this.setLocalPlayerId = setLocalPlayerId ?? throw new ArgumentNullException(nameof(setLocalPlayerId));
            this.getCommandSubmission = getCommandSubmission ?? throw new ArgumentNullException(nameof(getCommandSubmission));
        }

        public GameState CurrentState => getState();

        public int LocalPlayerId => getLocalPlayerId();

        public bool ControlsCurrentPlayerLocally => controlsCurrentPlayerLocally();

        public void SetLocalPlayerId(int playerId)
        {
            if (playerId > 0)
            {
                setLocalPlayerId(playerId);
            }
        }

        public WorkflowSubmissionResult Submit(GameCommand command)
        {
            var submission = getCommandSubmission();
            if (submission == null)
            {
                return new WorkflowSubmissionResult(
                    CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.UnknownCommand,
                        "命令提交器尚未初始化")),
                    false);
            }

            bool appliedLocally;
            var result = submission.Submit(command, out appliedLocally);
            return new WorkflowSubmissionResult(result, appliedLocally);
        }
    }
}
