using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Travel
{
    public sealed class RouteSelectionAnswerCodec : IRouteSelectionAnswerCodec
    {
        public ValidationResult TryParse(InteractionRequest request, IReadOnlyList<string> selectedCandidateIds, out RouteSelectionAnswer answer)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (selectedCandidateIds == null) throw new ArgumentNullException(nameof(selectedCandidateIds));
            answer = null;
            if (request.InteractionTypeId != "route_resource.selection" || request.AnswerSchema != "candidate_ids") return Invalid("route_selection_request_invalid");
            string sessionId = RouteSelectionSessionAuthority.Text(request.PromptParameters, "sessionId");
            string scope = RouteSelectionSessionAuthority.Text(request.PromptParameters, "scopeKey");
            if (sessionId.Length == 0 || scope.Length == 0 || selectedCandidateIds.Count < 1 || selectedCandidateIds.Count > 127) return Invalid("route_selection_answer_count");
            var allowed = new HashSet<string>(request.CandidateIds, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var routes = new List<string>();
            var routeSet = new HashSet<string>(StringComparer.Ordinal);
            var locations = new List<string>();
            var choices = new List<RouteRecipientChoice>();
            int commits = 0, cancels = 0;
            foreach (string id in selectedCandidateIds)
            {
                RouteSelectionCandidate candidate;
                if (id == null || !allowed.Contains(id) || !seen.Add(id) || !TryDecode(id, scope, out candidate)) return Invalid("route_selection_candidate_invalid");
                switch (candidate.Kind)
                {
                    case RouteSelectionCandidateKind.Commit: commits++; break;
                    case RouteSelectionCandidateKind.Cancel: cancels++; break;
                    case RouteSelectionCandidateKind.Location: locations.Add(candidate.LocationId); break;
                    case RouteSelectionCandidateKind.RouteAndRecipient:
                        if (!routeSet.Add(candidate.RouteId)) return Invalid("route_selection_multiple_recipients");
                        routes.Add(candidate.RouteId);
                        choices.Add(new RouteRecipientChoice(candidate.RouteId, candidate.ReceiverSubjectKey));
                        break;
                }
            }
            if (cancels == 1 && selectedCandidateIds.Count == 1)
            {
                answer = new RouteSelectionAnswer { IsCancellation = true, ScopeKey = scope };
                return ValidationResult.Success;
            }
            if (cancels != 0 || commits != 1) return Invalid("route_selection_commit_required");
            answer = new RouteSelectionAnswer { ScopeKey = scope, Draft = new RouteSelectionDraft(sessionId, 0, routes, locations, choices, "", locations.Count == 0) };
            return ValidationResult.Success;
        }

        public static string Encode(string scope, RouteSelectionCandidateKind kind, string entityId = "", string subject = "")
        {
            string prefix = "rs1." + scope + ".";
            switch (kind)
            {
                case RouteSelectionCandidateKind.Commit: return prefix + "commit";
                case RouteSelectionCandidateKind.Cancel: return prefix + "cancel";
                case RouteSelectionCandidateKind.Location: return prefix + "location." + EncodeField(entityId);
                case RouteSelectionCandidateKind.RouteAndRecipient: return prefix + "route." + EncodeField(entityId) + "." + EncodeField(subject);
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        internal static bool TryDecode(string id, string scope, out RouteSelectionCandidate candidate)
        {
            candidate = null;
            if (string.IsNullOrEmpty(id) || Encoding.UTF8.GetByteCount(id) > NormalizedValue.MaxStringBytes) return false;
            string[] parts = id.Split('.');
            if (parts.Length < 3 || parts[0] != "rs1" || parts[1] != scope) return false;
            var value = new RouteSelectionCandidate { CandidateId = id };
            if (parts.Length == 3 && parts[2] == "commit") value.Kind = RouteSelectionCandidateKind.Commit;
            else if (parts.Length == 3 && parts[2] == "cancel") value.Kind = RouteSelectionCandidateKind.Cancel;
            else if (parts.Length == 4 && parts[2] == "location")
            {
                string location;
                if (!TryDecodeField(parts[3], out location)) return false;
                value.Kind = RouteSelectionCandidateKind.Location; value.LocationId = location;
            }
            else if (parts.Length == 5 && parts[2] == "route")
            {
                string route, subject;
                if (!TryDecodeField(parts[3], out route) || !TryDecodeField(parts[4], out subject)) return false;
                int player;
                if (subject != "bank" && (!subject.StartsWith("player:", StringComparison.Ordinal) || !int.TryParse(subject.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out player) || player <= 0 || subject != "player:" + player.ToString(CultureInfo.InvariantCulture))) return false;
                value.Kind = RouteSelectionCandidateKind.RouteAndRecipient; value.RouteId = route; value.ReceiverSubjectKey = subject;
            }
            else return false;
            candidate = value;
            return true;
        }

        internal static string EncodeField(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        internal static bool TryDecodeField(string encoded, out string value)
        {
            value = "";
            try
            {
                string input = encoded.Replace('-', '+').Replace('_', '/');
                input = input.PadRight(input.Length + ((4 - input.Length % 4) % 4), '=');
                value = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(input));
                return value.Length > 0 && EncodeField(value) == encoded;
            }
            catch (FormatException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }
        private static ValidationResult Invalid(string reason) { return ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason); }
    }
}
