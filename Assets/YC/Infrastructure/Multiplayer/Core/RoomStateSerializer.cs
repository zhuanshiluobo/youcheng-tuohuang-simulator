using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    /// <summary>TCP 和 Mirror 共享的严格房间协议入口，不迁移旧 pipe 或缺字段消息。</summary>
    public static class RoomStateSerializer
    {
        public static string Serialize(RoomState room)
        {
            if (!TryValidate(room, out var reason)) throw new InvalidOperationException(reason);
            return JsonConvert.SerializeObject(room);
        }

        public static bool TryDeserialize(string json, out RoomState room, out string reasonCode)
        {
            room = null;
            reasonCode = "room_protocol_mismatch";
            try
            {
                if (string.IsNullOrEmpty(json) || json.Length > 262144) return false;
                var root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (root["ProtocolVersion"]?.Type != JTokenType.Integer || (int?)root["ProtocolVersion"] != RoomState.CurrentProtocolVersion || root["RoomId"]?.Type != JTokenType.String ||
                    root["StateRevision"]?.Type != JTokenType.Integer || !(root["Configuration"] is JObject configuration) ||
                    root["HostPlayerId"]?.Type != JTokenType.Integer || root["LocalPlayerId"]?.Type != JTokenType.Integer ||
                    root["HasStarted"]?.Type != JTokenType.Boolean || !(root["PendingSeatSwaps"] is JArray swaps) ||
                    !(root["SeatSlots"] is JArray slots) || slots.Count != 4) return false;
                if (root["Resume"] == null || !(root["WaitingMembers"] is JArray waiting)) return false;
                foreach (var member in waiting)
                    if (!(member is JObject) || member["MemberId"]?.Type != JTokenType.String || member["PlayerName"]?.Type != JTokenType.String) return false;
                if (root["Resume"].Type != JTokenType.Null)
                {
                    if (!(root["Resume"] is JObject resume)) return false;
                    foreach (var field in new[] { "MapId", "GameId", "ContentHash", "Round", "HostPlayerId", "EventDeckSeed", "Configuration", "RandomRuntime", "LaunchPlayerOrder", "Seats", "SimulationEpoch", "ConfirmedGlobalSequence", "MembershipVersion", "CommandPrefixDigest", "CheckpointId", "MetadataDigest" })
                        if (resume[field] == null || resume[field].Type == JTokenType.Null) return false;
                    if (!HasRandomShape(resume["RandomRuntime"])) return false;
                }
                if (root["GameId"]?.Type != JTokenType.String || root["EventDeckSeed"]?.Type != JTokenType.Integer ||
                    root["RandomReady"]?.Type != JTokenType.Boolean || root["RandomAlgorithmVersion"]?.Type != JTokenType.String ||
                    root["RandomRuntime"] == null) return false;
                if ((bool)root["RandomReady"] && !HasRandomShape(root["RandomRuntime"])) return false;
                if (!(root["LaunchPlayerOrder"] is JArray order)) return false;
                foreach (var id in order) if (id.Type != JTokenType.Integer) return false;
                foreach (var field in new[] { "SchemaVersion", "MapId", "RuleModeId", "EnabledExpansionIds", "PlayerCount",
                    "ConfigurationRevision", "ContentPackHash", "RuleContractVersion" })
                    if (configuration[field] == null || configuration[field].Type == JTokenType.Null) return false;
                if (configuration["SchemaVersion"]?.Type != JTokenType.Integer || configuration["MapId"]?.Type != JTokenType.String ||
                    configuration["RuleModeId"]?.Type != JTokenType.String || configuration["EnabledExpansionIds"]?.Type != JTokenType.Array ||
                    configuration["PlayerCount"]?.Type != JTokenType.Integer || configuration["ConfigurationRevision"]?.Type != JTokenType.Integer ||
                    configuration["ContentPackHash"]?.Type != JTokenType.String || configuration["RuleContractVersion"]?.Type != JTokenType.String) return false;
                foreach (var slot in slots)
                    if (!(slot is JObject entry) || entry["SeatIndex"]?.Type != JTokenType.Integer || entry["Status"]?.Type != JTokenType.String ||
                        entry["IsUserReady"]?.Type != JTokenType.Boolean || entry["ReadyConfigurationRevision"]?.Type != JTokenType.Integer ||
                        entry["Occupant"] == null || (entry["Occupant"].Type != JTokenType.Null && !(entry["Occupant"] is JObject))) return false;
                foreach (var swap in swaps)
                {
                    if (!(swap is JObject entry) || entry["SwapRequestId"]?.Type != JTokenType.String) return false;
                    foreach (var field in new[] { "RequestingPlayerId", "TargetPlayerId", "SourceSeatIndex", "TargetSeatIndex", "ConfigurationRevision" })
                        if (entry[field]?.Type != JTokenType.Integer) return false;
                }
                var candidate = root.ToObject<RoomState>();
                if (!TryValidate(candidate, out reasonCode)) return false;
                room = candidate;
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { return false; }
        }

        private static bool HasRandomShape(JToken token)
        {
            if (!(token is JObject runtime) || runtime["GameId"]?.Type != JTokenType.String ||
                runtime["AlgorithmVersion"]?.Type != JTokenType.String || !(runtime["Streams"] is JArray streams)) return false;
            foreach (var stream in streams)
                if (!(stream is JObject item) || item["StreamId"]?.Type != JTokenType.String ||
                    item["State"]?.Type != JTokenType.Integer || item["WordsConsumed"]?.Type != JTokenType.Integer ||
                    item["RequestsConsumed"]?.Type != JTokenType.Integer) return false;
            return true;
        }

        public static bool TryValidate(RoomState room, out string reasonCode)
        {
            reasonCode = "room_protocol_mismatch";
            if (room == null || room.ProtocolVersion != RoomState.CurrentProtocolVersion || string.IsNullOrEmpty(room.RoomId) ||
                room.Configuration == null || room.SeatSlots == null || room.SeatSlots.Count != 4 ||
                room.PendingSeatSwaps == null || room.StateRevision < 0 || room.Configuration.ConfigurationRevision < 0)
                return false;
            if (!room.Configuration.TryValidate(out reasonCode)) return false;
            reasonCode = "room_protocol_mismatch";
            var players = new HashSet<int>();
            for (var i = 0; i < 4; i++)
            {
                var slot = room.SeatSlots[i];
                if (slot == null || slot.SeatIndex != i ||
                    (slot.Status != "open" && slot.Status != "closed" && slot.Status != "occupied") ||
                    (slot.Status == "occupied") != (slot.Occupant != null)) return false;
                if (slot.Occupant != null && (slot.Occupant.PlayerId <= 0 || !players.Add(slot.Occupant.PlayerId) ||
                    slot.Occupant.Color != RoomConfigurationService.GetSeatColor(i))) return false;
                if ((slot.IsUserReady && (slot.Occupant == null || slot.ReadyConfigurationRevision != room.Configuration.ConfigurationRevision)) ||
                    (!slot.IsUserReady && slot.ReadyConfigurationRevision != -1)) return false;
            }
            if (!players.Contains(room.HostPlayerId) || room.Configuration.PlayerCount != room.PlayerCount) return false;
            if (room.WaitingMembers == null || room.WaitingMembers.Exists(m => m == null || string.IsNullOrEmpty(m.MemberId)) ||
                room.WaitingMembers.Select(m => m.MemberId).Distinct().Count() != room.WaitingMembers.Count ||
                room.Resume == null && room.WaitingMembers.Count != 0) return false;
            if (room.Resume != null)
            {
                try { room.Resume.Validate(); } catch (Exception) { return false; }
                if (JsonConvert.SerializeObject(room.Configuration) != JsonConvert.SerializeObject(room.Resume.Configuration) ||
                    room.HostPlayerId != room.Resume.HostPlayerId || room.PendingSeatSwaps.Count != 0 ||
                    room.Resume.Seats.Exists(s => !players.Contains(s.PlayerId) || RoomConfigurationService.FindSlot(room, s.PlayerId).Occupant.Color != s.Color)) return false;
                if (room.HasStarted && (room.GameId != room.Resume.GameId || room.EventDeckSeed != room.Resume.EventDeckSeed ||
                    !room.LaunchPlayerOrder.SequenceEqual(room.Resume.LaunchPlayerOrder) ||
                    JsonConvert.SerializeObject(room.RandomRuntime) != JsonConvert.SerializeObject(room.Resume.RandomRuntime))) return false;
            }
            if (room.LaunchPlayerOrder == null || (room.HasStarted
                ? !TurnOrderService.IsValidPlayerOrder(room.LaunchPlayerOrder, players) : room.LaunchPlayerOrder.Count != 0)) return false;
            if (room.HasStarted != room.RandomReady) return false;
            if (room.RandomReady)
            {
                if (room.RandomAlgorithmVersion != GameplayRandomRuntime.AlgorithmVersion ||
                    !GameplayRandomRuntime.TryValidate(room.RandomRuntime, room.GameId, room.EventDeckSeed, out _)) return false;
            }
            else if (!string.IsNullOrEmpty(room.GameId) || room.EventDeckSeed != 0 || room.RandomRuntime != null ||
                !string.IsNullOrEmpty(room.RandomAlgorithmVersion)) return false;
            var swapIds = new HashSet<string>(StringComparer.Ordinal);
            var requesters = new HashSet<int>();
            foreach (var swap in room.PendingSeatSwaps)
                if (swap == null || string.IsNullOrEmpty(swap.SwapRequestId) || !swapIds.Add(swap.SwapRequestId) || !requesters.Add(swap.RequestingPlayerId) ||
                    swap.SourceSeatIndex < 0 || swap.SourceSeatIndex >= 4 || swap.TargetSeatIndex < 0 || swap.TargetSeatIndex >= 4 ||
                    swap.SourceSeatIndex == swap.TargetSeatIndex || swap.ConfigurationRevision != room.Configuration.ConfigurationRevision ||
                    room.SeatSlots[swap.SourceSeatIndex].Occupant?.PlayerId != swap.RequestingPlayerId ||
                    room.SeatSlots[swap.TargetSeatIndex].Occupant?.PlayerId != swap.TargetPlayerId) return false;
            if (room.HasStarted && (room.PlayerCount < 2 || string.IsNullOrEmpty(room.Configuration.ContentPackHash))) return false;
            reasonCode = string.Empty;
            return true;
        }
    }
}
