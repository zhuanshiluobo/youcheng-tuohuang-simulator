using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    public static class GameStateViewProtocol
    {
        public static bool TryReadInitial(string json, GameRuleConfiguration expected, out InitialGameStateViewDto dto)
            => TryReadInitial(json, expected, null, out dto);

        public static bool TryReadInitial(string json, GameRuleConfiguration expected, IReadOnlyList<int> expectedPlayerOrder,
            out InitialGameStateViewDto dto)
        {
            dto = null;
            if (!TryValidate(json, expected, out var root)) return false;
            try
            {
                var candidate = root.ToObject<InitialGameStateViewDto>();
                if (!ValidateView(candidate?.View, expectedPlayerOrder)) return false;
                dto = candidate; return true;
            }
            catch (JsonException) { return false; }
        }
        public static bool TryReadConfirmed(string json, GameRuleConfiguration expected, out ConfirmedGameStateViewDto dto)
            => TryReadConfirmed(json, expected, null, out dto);

        public static bool TryReadConfirmed(string json, GameRuleConfiguration expected, IReadOnlyList<int> expectedPlayerOrder,
            out ConfirmedGameStateViewDto dto)
        {
            dto = null;
            if (!TryValidate(json, expected, out var root)) return false;
            try
            {
                var candidate = root.ToObject<ConfirmedGameStateViewDto>();
                if (!ValidateView(candidate?.View, expectedPlayerOrder)) return false;
                dto = candidate; return true;
            }
            catch (JsonException) { return false; }
        }
        private static bool ValidateView(GameStateView view, IReadOnlyList<int> expectedOrder) =>
            view != null && view.HasValidPlayerOrderAndSlots() &&
            (expectedOrder == null || expectedOrder.SequenceEqual(view.LaunchPlayerOrder));

        private static bool TryValidate(string json, GameRuleConfiguration expected, out JObject root)
        {
            root = null;
            try
            {
                if (string.IsNullOrEmpty(json) || expected == null) return false;
                root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (root["ProtocolVersion"]?.Type != JTokenType.Integer || (int?)root["ProtocolVersion"] != GameStateView.CurrentSchemaVersion ||
                    !(root["View"] is JObject view) || (int?)view["SchemaVersion"] != GameStateView.CurrentSchemaVersion ||
                    !(view["RuleConfiguration"] is JObject configuration)) return false;
                foreach (var field in new[] { "SchemaVersion", "MapId", "RuleModeId", "EnabledExpansionIds", "PlayerCount", "ConfigurationRevision", "ContentPackHash", "RuleContractVersion" })
                    if (configuration[field] == null || configuration[field].Type == JTokenType.Null) return false;
                var actual = configuration.ToObject<GameRuleConfiguration>();
                if (actual == null || !actual.TryValidate(out _) || actual.MapId != expected.MapId || actual.PlayerCount != expected.PlayerCount ||
                    actual.ConfigurationRevision != expected.ConfigurationRevision || actual.ContentPackHash != expected.ContentPackHash ||
                    actual.RuleModeId != expected.RuleModeId || actual.RuleContractVersion != expected.RuleContractVersion ||
                    actual.EnabledExpansionIds.Count != expected.EnabledExpansionIds.Count ||
                    view["MapId"]?.Value<string>() != expected.MapId || view["ContentPackHash"]?.Value<string>() != expected.ContentPackHash ||
                    !(view["Players"] is JArray players) || players.Count != expected.PlayerCount) return false;
                if (!(view["LaunchPlayerOrder"] is JArray order) || order.Any(id => id.Type != JTokenType.Integer)) return false;
                foreach (var player in players)
                    if (!(player is JObject entry) || entry["PlayerId"]?.Type != JTokenType.Integer ||
                        !(entry["CityStyleUsedSlotIndexes"] is JArray slots) || slots.Any(slot => slot.Type != JTokenType.Integer)) return false;
                foreach (var id in expected.EnabledExpansionIds) if (!actual.EnabledExpansionIds.Contains(id)) return false;
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { return false; }
        }
    }
}
