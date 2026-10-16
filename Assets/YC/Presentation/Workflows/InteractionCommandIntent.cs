using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>绑定实际提交时的对局和玩家身份；Rejected没有GameId时也不从当前新局补写。</summary>
    public sealed class SubmittedInteractionCommands
    {
        private readonly int playerId;
        private readonly bool controlsCurrentPlayerLocally;
        private readonly Dictionary<string, SubmissionIdentity> submissions = new Dictionary<string, SubmissionIdentity>(StringComparer.Ordinal);
        private readonly struct SubmissionIdentity
        {
            public readonly string GameId;
            public readonly int PlayerId;
            public SubmissionIdentity(string gameId, int playerId) { GameId = gameId; PlayerId = playerId; }
        }
        public SubmittedInteractionCommands(int playerId, bool controlsCurrentPlayerLocally = false)
        { this.playerId = playerId; this.controlsCurrentPlayerLocally = controlsCurrentPlayerLocally; }
        public void Record(string gameId, GameCommand command)
        {
            if (!string.IsNullOrEmpty(gameId) && command != null &&
                (controlsCurrentPlayerLocally ? command.PlayerId > 0 : command.PlayerId == playerId) &&
                !string.IsNullOrEmpty(command.CommandId) && !submissions.ContainsKey(command.CommandId))
                submissions.Add(command.CommandId, new SubmissionIdentity(gameId, command.PlayerId));
        }
        public bool TryResolve(string commandId, int answeringPlayerId, string receivedGameId, out string submittedGameId)
        {
            submittedGameId = null;
            if (string.IsNullOrEmpty(commandId) || !submissions.TryGetValue(commandId, out var identity) ||
                answeringPlayerId != identity.PlayerId || receivedGameId != null && receivedGameId != identity.GameId) return false;
            submissions.Remove(commandId); submittedGameId = identity.GameId; return true;
        }
    }

    /// <summary>本地已确认意图；快照更新不是命令结果，只有准确的正式过期拒绝允许有限重建。</summary>
    public sealed class InteractionCommandIntent
    {
        private string gameId, requestId, contract, dependency;
        private int playerId, revision, rebuilds;
        private string[] answers;
        private bool decline, retryAllowed, waiting;
        public string CommandId { get; private set; }
        public bool HasPending => !string.IsNullOrEmpty(CommandId);
        public bool WaitingForRefresh => waiting;
        public bool IsBusy => HasPending || waiting;
        public int RebuildCount => rebuilds;
        public bool BelongsToContext(string currentGameId, int currentPlayerId) => gameId == currentGameId && playerId == currentPlayerId;

        public static bool SameContract(string leftGame, InteractionRequestProjection left,
            string rightGame, InteractionRequestProjection right) =>
            !string.IsNullOrEmpty(leftGame) && leftGame == rightGame && IsOpen(left) && IsOpen(right) &&
            Contract(left) == Contract(right);

        private static bool IsOpen(InteractionRequestProjection request) => request != null &&
            request.VisibleToViewer && request.Status == "open" && !string.IsNullOrEmpty(request.InteractionId);

        public static string ContractKey(InteractionRequestProjection request) => request == null ? null : Contract(request);

        private static string Contract(InteractionRequestProjection request)
        {
            var parameters = request.PromptParameters?.Clone();
            // 正常采集的观察token单独更新；scope、factsDigest、开始预算等全部仍参与合同。
            if (request.InteractionTypeId == "route_resource.selection" && parameters?.Properties != null)
                parameters.Properties.RemoveAll(entry => entry.Name == "requestRevision");
            var fields = new StringBuilder();
            void Text(string name, string value) { value = value ?? string.Empty; fields.Append(name).Append(':').Append(value.Length).Append(':').Append(value); }
            void Number(string name, int value) => Text(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Text("request", request.InteractionId); Text("owner", request.OwnerEffectId); Text("source", request.SourceNodeId);
            Text("type", request.InteractionTypeId); Text("prompt", request.PromptKey); Text("visibility", request.Visibility);
            Text("candidateSet", request.CandidateSetId); Text("schema", request.AnswerSchema);
            Number("player", request.AnsweringPlayerId); Number("candidateVersion", request.CandidateSetVersion);
            Number("minimum", request.MinSelections); Number("maximum", request.MaxSelections);
            Text("decline", request.AllowDecline ? "true" : "false");
            foreach (var candidate in request.CandidateIds ?? new List<string>()) Text("candidate", candidate);
            Text("parameters", parameters?.ToDeterministicString());
            return fields.ToString();
        }

        public bool Begin(string submittedGameId, InteractionRequestProjection request, GameCommand command,
            bool allowRevisionRebuild, string dependencyKey = "")
        {
            if (IsBusy || string.IsNullOrEmpty(submittedGameId) || !IsOpen(request) || command == null ||
                command.Kind != GameCommandKind.AnswerInteraction || command.PlayerId != request.AnsweringPlayerId ||
                !command.Parameters.TryGetValue("interactionId", out var id) || id != request.InteractionId ||
                !command.Parameters.TryGetValue("expectedRevision", out var token) || !int.TryParse(token, out var expected) || expected != request.StateRevision)
                return false;
            var cancelled = command.Parameters.TryGetValue("answerValue", out var value) && value == "false";
            if (!LegalAnswer(request, command.OptionIds, cancelled)) return false;
            gameId = submittedGameId; requestId = request.InteractionId; playerId = command.PlayerId; contract = Contract(request);
            dependency = dependencyKey; answers = command.OptionIds.ToArray(); decline = cancelled;
            revision = request.StateRevision; rebuilds = 0; retryAllowed = allowRevisionRebuild;
            CommandId = command.CommandId; waiting = false;
            return true;
        }

        public bool Resolve(string resolvedGameId, string commandId, bool accepted, CommandErrorCode errorCode,
            bool requiresRefresh = false)
        {
            if (!HasPending || string.IsNullOrEmpty(commandId) || gameId != resolvedGameId || CommandId != commandId) return false;
            CommandId = null;
            waiting = !requiresRefresh && !accepted && errorCode == CommandErrorCode.StateRevisionMismatch && retryAllowed && rebuilds < 3;
            if (!waiting) Reset();
            return true;
        }

        public bool TryRebuild(string currentGameId, InteractionRequestProjection current,
            string dependencyKey, out GameCommand command)
        {
            command = null;
            if (!waiting) return false;
            if (currentGameId == gameId && current?.InteractionId == requestId && current.StateRevision < revision) return false;
            if (currentGameId != gameId || !IsOpen(current) || current.AnsweringPlayerId != playerId ||
                Contract(current) != contract || dependencyKey != dependency || !LegalAnswer(current, answers, decline))
            { Reset(); return false; }
            if (current.StateRevision <= revision) return false;
            command = EffectInteractionCommands.Answer(current, playerId, answers, decline);
            revision = current.StateRevision; rebuilds++; waiting = false; CommandId = command.CommandId;
            return true;
        }

        private static bool LegalAnswer(InteractionRequestProjection request, IReadOnlyList<string> values, bool cancelled) =>
            cancelled ? request.AllowDecline : values != null && values.Count >= request.MinSelections &&
            values.Count <= request.MaxSelections && values.Distinct(StringComparer.Ordinal).Count() == values.Count &&
            values.All(value => request.CandidateIds != null && request.CandidateIds.Contains(value));

        public void Reset()
        {
            gameId = requestId = contract = dependency = CommandId = null; answers = null;
            waiting = retryAllowed = decline = false; rebuilds = 0; revision = -1; playerId = -1;
        }
    }
}
