using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;

namespace YC.Application.Sessions
{
    [Serializable]
    public sealed class MatchSaveSeat
    {
        public int PlayerId;
        public ulong SteamId;
        public string OperatorId;
        public string PlayerName;
        public PlayerColor Color;
        public PlayerSeat ToSeat() => new PlayerSeat { PlayerId = PlayerId, SteamId = SteamId,
            OperatorId = OperatorId, PlayerName = PlayerName, Color = Color };
    }

    /// <summary>本机存档。规则原包使用正式 Host 序列化器；联机续接原包不得进入房间广播。</summary>
    [Serializable]
    public sealed class MatchSaveData
    {
        public const int CurrentVersion = 2;
        public string Format = "YC.MatchSave";
        public int Version = CurrentVersion;
        public long SavedUtcTicks;
        public LaunchMode Mode;
        public bool LocalTestNetwork;
        public int LocalPlayerId;
        public ulong HostSteamId;
        public string MapId;
        public string GameId;
        public int Round;
        public string ContentHash;
        public List<MatchSaveSeat> Seats = new List<MatchSaveSeat>();
        public string ArchiveJson;
        public string ContinuationJson;
        public string SimulationCheckpointJson;

        public JObject ReadArchiveState() => (JObject)JObject.Parse(ArchiveJson,
            new Newtonsoft.Json.Linq.JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error })["Snapshot"]?["State"];
        public GameRuleConfiguration Configuration => ReadArchiveState()["RuleConfiguration"].ToObject<GameRuleConfiguration>();
        public int EventDeckSeed => (int)ReadArchiveState()["EventDeckSeed"];
        public GameplayRandomState RandomState => ReadArchiveState()["RandomRuntime"].ToObject<GameplayRandomState>();
        public List<int> LaunchPlayerOrder => ReadArchiveState()["LaunchPlayerOrder"].ToObject<List<int>>();

        public void Validate()
        {
            if (Format != "YC.MatchSave" || Version != CurrentVersion)
                throw new InvalidOperationException("不支持此存档版本；需要包含完整规则及联机续接状态的存档。");
            if (Mode != LaunchMode.Local && Mode != LaunchMode.Host || string.IsNullOrEmpty(ArchiveJson) ||
                string.IsNullOrEmpty(ContinuationJson) || SavedUtcTicks <= 0 || SavedUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidOperationException("存档缺少完整的权威状态。");
            var root = JObject.Parse(ArchiveJson, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var state = root["Snapshot"]?["State"] as JObject;
            if (state == null || (int?)root["Snapshot"]?["SchemaVersion"] != HostSnapshotDto.CurrentSchemaVersion ||
                (int?)root["Journal"]?["SchemaVersion"] != HostJournalDto.CurrentSchemaVersion ||
                string.IsNullOrEmpty(GameId) || string.IsNullOrEmpty(ContentHash) ||
                (string)root["Snapshot"]?["ContentHash"] != ContentHash || (string)state["ContentPackHash"] != ContentHash ||
                (string)state["GameId"] != GameId || (string)state["MapId"] != MapId || (int?)state["Round"] != Round)
                throw new InvalidOperationException("存档摘要与规则快照不一致。");
            var configuration = Configuration;
            if (configuration == null || !configuration.TryValidate(out _) || configuration.ContentPackHash != ContentHash ||
                configuration.MapId != MapId || !GameplayRandomRuntime.TryValidate(RandomState, GameId, EventDeckSeed, out _))
                throw new InvalidOperationException("存档的规则配置或随机状态无效。");
            var players = ((JArray)state["Players"]).Select(p => (int)p["PlayerId"]).OrderBy(id => id).ToArray();
            if (Seats == null || Seats.Any(s => s == null || s.PlayerId <= 0) ||
                !Seats.Select(s => s.PlayerId).OrderBy(id => id).SequenceEqual(players) ||
                Seats.Select(s => s.PlayerId).Distinct().Count() != Seats.Count || configuration.PlayerCount != Seats.Count ||
                !players.Contains(LocalPlayerId) || !TurnOrderService.IsValidPlayerOrder(LaunchPlayerOrder, players))
                throw new InvalidOperationException("存档席位与规则玩家不一致。");
            if (Seats.Select(s => s.Color).Distinct().Count() != Seats.Count ||
                Seats.Any(s => (int?)((JArray)state["Players"]).Single(p => (int)p["PlayerId"] == s.PlayerId)["Color"] != (int)s.Color))
                throw new InvalidOperationException("存档席位颜色与规则玩家不一致。");
            SimulationConfirmedRecoveryInstaller.ReadContinuation(ContinuationJson);
            if (Mode == LaunchMode.Host)
            {
                if (string.IsNullOrEmpty(SimulationCheckpointJson) || Seats.Any(s => s.SteamId == 0) ||
                    Seats.Select(s => s.SteamId).Distinct().Count() != Seats.Count ||
                    !Seats.Any(s => s.PlayerId == LocalPlayerId && s.SteamId == HostSteamId))
                    throw new InvalidOperationException("联机存档缺少确认边界或操作者身份。");
                if (LocalTestNetwork && (Seats.Any(s => string.IsNullOrEmpty(s.OperatorId)) || Seats.Select(s => s.OperatorId).Distinct().Count() != Seats.Count))
                    throw new InvalidOperationException("本地联机存档缺少原操作者身份。");
                var checkpoint = JObject.Parse(SimulationCheckpointJson);
                var baseline = SimulationConfirmedRecoveryInstaller.ReadBaseline(SimulationCheckpointJson);
                if (baseline.GameId != GameId || baseline.ContentPackHash != ContentHash ||
                    !JToken.DeepEquals(checkpoint["Archive"], root) ||
                    !JToken.DeepEquals(checkpoint["Continuation"], JObject.Parse(ContinuationJson)))
                    throw new InvalidOperationException("规则快照与联机确认边界不是同一保存点。");
                var options = OptionsFor(baseline, true, LocalPlayerId);
                OperationCommandCanonicalizer.ValidateRecoveryBaseline(options, baseline);
            }
            else if (!string.IsNullOrEmpty(SimulationCheckpointJson))
                throw new InvalidOperationException("本地存档包含不适用的联机上下文。");
        }

        public static DeterministicSimulationSessionOptions OptionsFor(SimulationRecoveryBaselineDto baseline, bool host, int playerId)
            => new DeterministicSimulationSessionOptions { IsHost = host, LocalPlayerId = playerId,
                GameId = baseline.GameId, ContentPackHash = baseline.ContentPackHash,
                SimulationEpoch = baseline.SimulationEpoch, MembershipVersion = baseline.MembershipVersion,
                CheckpointGlobalSequence = baseline.ConfirmedGlobalSequence, CheckpointPrefixDigest = baseline.CommandPrefixDigest,
                FrozenPlayerIds = new List<int>(baseline.FrozenPlayerIds), RequiredReplicaIds = new List<string>(baseline.RequiredReplicaIds) };
    }
}
