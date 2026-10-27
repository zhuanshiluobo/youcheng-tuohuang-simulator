using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;

namespace YC.Application.Interactions
{
    public static class CleanupSourceCommand
    {
        public const string Intent = "cleanup_source";
        private static readonly HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal) {
            "intent", "windowId", "ownerEffectId", "optionId", "requestId", "requestGeneration", "candidateSetVersion", "factsDigest", "expectedRevision" };
        public static GameCommand Create(int playerId, string commandId, CleanupSourceRequestRef request, int expectedRevision) => new GameCommand {
            Kind = GameCommandKind.InvokeSettlementOption, PlayerId = playerId, CommandId = commandId,
            Parameters = new Dictionary<string, string> { ["intent"] = Intent, ["windowId"] = request.WindowId, ["optionId"] = request.OptionId,
                ["ownerEffectId"] = request.OwnerEffectId, ["candidateSetVersion"] = request.CandidateSetVersion.ToString(CultureInfo.InvariantCulture),
                ["requestId"] = request.InteractionId, ["requestGeneration"] = request.RequestGeneration.ToString(CultureInfo.InvariantCulture),
                ["factsDigest"] = request.FactsDigest, ["expectedRevision"] = expectedRevision.ToString(CultureInfo.InvariantCulture) } };
        public static bool TryRead(GameCommand command, out CleanupSourceRequestRef request, out string reason)
        {
            request = null; reason = "cleanup_source_command_invalid";
            if (command?.Parameters == null || command.Parameters.Count != Fields.Count || command.Parameters.Keys.Any(k => !Fields.Contains(k)) ||
                Fields.Any(k => !command.Parameters.TryGetValue(k, out var value) || value == null) || command.Parameters["intent"] != Intent ||
                !string.IsNullOrEmpty(command.SourceId) || !string.IsNullOrEmpty(command.TargetId) || command.OptionIds == null || command.OptionIds.Count > 0 || string.IsNullOrWhiteSpace(command.CommandId) ||
                !int.TryParse(command.Parameters["requestGeneration"], NumberStyles.None, CultureInfo.InvariantCulture, out int generation) || generation < 1 ||
                !int.TryParse(command.Parameters["candidateSetVersion"], NumberStyles.None, CultureInfo.InvariantCulture, out int candidates) || candidates < 1 ||
                !int.TryParse(command.Parameters["expectedRevision"], NumberStyles.None, CultureInfo.InvariantCulture, out int revision) || revision < 0 ||
                command.Parameters["factsDigest"].Length != 64 || command.Parameters["factsDigest"].Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) return false;
            request = new CleanupSourceRequestRef { WindowId = command.Parameters["windowId"], OwnerEffectId = command.Parameters["ownerEffectId"], OptionId = command.Parameters["optionId"],
                InteractionId = command.Parameters["requestId"], FactsDigest = command.Parameters["factsDigest"], RequestGeneration = generation, CandidateSetVersion = candidates, AnsweringPlayerId = command.PlayerId };
            if (string.IsNullOrWhiteSpace(request.WindowId) || string.IsNullOrWhiteSpace(request.OwnerEffectId) || string.IsNullOrWhiteSpace(request.OptionId) || string.IsNullOrWhiteSpace(request.InteractionId)) return false;
            reason = ""; return true;
        }
    }
}
