using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>
    /// 统一监听本地命令造成的分数变化，并立即请求刷新计分轨表现。
    /// </summary>
    public sealed class ScoreTrackRefreshingCommandPort : IGameCommandPort
    {
        private readonly IGameplayContext context;
        private readonly IGameCommandPort inner;
        private readonly Action refreshScoreTrack;

        public ScoreTrackRefreshingCommandPort(
            IGameplayContext context,
            IGameCommandPort inner,
            Action refreshScoreTrack)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.refreshScoreTrack = refreshScoreTrack ?? throw new ArgumentNullException(nameof(refreshScoreTrack));
        }

        public WorkflowSubmissionResult Submit(GameCommand command)
        {
            var scoresBeforeSubmission = CaptureScores(context.CurrentState);
            var submission = inner.Submit(command);
            if (submission.AppliedLocally &&
                submission.CommandResult.Succeeded &&
                HaveScoresChanged(scoresBeforeSubmission, context.CurrentState))
            {
                refreshScoreTrack();
            }

            return submission;
        }

        private static Dictionary<int, int> CaptureScores(GameState state)
        {
            var scores = new Dictionary<int, int>();
            if (state == null || state.Players == null)
            {
                return scores;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player != null)
                {
                    scores[player.PlayerId] = player.Score;
                }
            }

            return scores;
        }

        private static bool HaveScoresChanged(
            IReadOnlyDictionary<int, int> scoresBeforeSubmission,
            GameState state)
        {
            if (state == null || state.Players == null)
            {
                return scoresBeforeSubmission.Count > 0;
            }

            var playerCount = 0;
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player == null)
                {
                    continue;
                }

                playerCount += 1;
                int previousScore;
                if (!scoresBeforeSubmission.TryGetValue(player.PlayerId, out previousScore) ||
                    previousScore != player.Score)
                {
                    return true;
                }
            }

            return playerCount != scoresBeforeSubmission.Count;
        }
    }
}
