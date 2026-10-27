using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YC.Domain.Rules;
using YC.Domain.State;

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
        /// <summary>一次保存准备内复用同一不可变归档文本的解析树；各次语义校验仍完整执行。</summary>
        public sealed class ValidationScope : IDisposable
        {
            private readonly object gate = new object();
            private string archiveJson;
            private JObject archive;
            private bool archiveIsLocalProjection;
            private bool disposed;
            private IMatchSaveTrace trace;
            public ValidationScope() { }
            public ValidationScope(IMatchSaveTrace trace) { this.trace = trace; }
            internal MatchSaveTraceScope BeginStage(string stage) => MatchSaveTraceScope.Begin(trace, stage);
            internal void Point(string stage) => MatchSaveTraceScope.Point(trace, stage);
            internal void Source(int revision) => MatchSaveTraceScope.Source(trace, revision);
            internal JObject ReadArchive(string json, bool localProjection)
            {
                if (!ReferenceEquals(archiveJson, json) || archiveIsLocalProjection != localProjection)
                {
                    var parsed = ReadValidationArchive(json, localProjection);
                    archiveJson = json; archive = parsed; archiveIsLocalProjection = localProjection;
                }
                return archive;
            }
            public void Validate(MatchSaveData data)
            {
                if (data == null) throw new ArgumentNullException(nameof(data));
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException(nameof(ValidationScope));
                    using var marker = BeginStage("save.validate");
                    data.ValidateCore(this);
                }
            }
            public void Dispose()
            {
                lock (gate) { archiveJson = null; archive = null; archiveIsLocalProjection = false; trace = null; disposed = true; }
            }
        }

        /// <summary>热座专用：调用方转交独占的稳定捕获，后台只访问该捕获和复制后的席位头。</summary>
        public static Task<MatchSaveData> CreateFromLocalCaptureAsync(MatchSaveData header, GameSessionRecoveryExport capture)
            => CreateFromLocalCaptureAsync(header, capture, null);
        public static Task<MatchSaveData> CreateFromLocalCaptureAsync(MatchSaveData header, GameSessionRecoveryExport capture, ValidationScope validation)
        {
            if (header == null || capture?.Archive?.Snapshot?.State == null || header.Mode != LaunchMode.Local ||
                header.GameId != capture.Archive.Snapshot.State.GameId || header.ContentHash != capture.Archive.Snapshot.State.ContentPackHash)
                throw new ArgumentException("local_save_identity_invalid");
            var data = new MatchSaveData { Format = header.Format, Version = header.Version, SavedUtcTicks = header.SavedUtcTicks,
                Mode = header.Mode, LocalTestNetwork = header.LocalTestNetwork, LocalPlayerId = header.LocalPlayerId, HostSteamId = header.HostSteamId,
                Seats = header.Seats?.Select(s => s == null ? null : new MatchSaveSeat { PlayerId = s.PlayerId, SteamId = s.SteamId,
                    OperatorId = s.OperatorId, PlayerName = s.PlayerName, Color = s.Color }).ToList() };
            return Task.Run(() => {
                var source = capture.Archive.Snapshot.State;
                validation?.Source(source.EffectRuntime?.StateRevision ?? 0);
                using var marker = validation?.BeginStage("save.prepare.local");
                data.GameId = source.GameId; data.ContentHash = source.ContentPackHash; data.MapId = source.MapId; data.Round = source.Round;
                if (data.SavedUtcTicks <= 0) data.SavedUtcTicks = DateTime.UtcNow.Ticks;
                data.ArchiveJson = HostRecoveryService.SerializeArchive(capture.Archive);
                data.ContinuationJson = SimulationConfirmedRecoveryInstaller.SerializeContinuation(capture.Continuation);
                data.SimulationCheckpointJson = null;
                if (validation == null) data.Validate(); else validation.Validate(data);
                return data;
            });
        }

        public static Task<MatchSaveData> CreateFromWindowSavePointAsync(MatchSaveData header, SimulationWindowSavePoint point)
            => CreateFromWindowSavePointAsync(header, point, null);
        public static async Task<MatchSaveData> CreateFromWindowSavePointAsync(MatchSaveData header, SimulationWindowSavePoint point, ValidationScope validation)
        {
            if (header == null || point == null || header.Mode != LaunchMode.Host || header.LocalPlayerId != point.LocalPlayerId || header.GameId != point.GameId)
                throw new ArgumentException("window_save_identity_invalid");
            var data = new MatchSaveData { Format = header.Format, Version = header.Version, SavedUtcTicks = header.SavedUtcTicks,
                Mode = header.Mode, LocalTestNetwork = header.LocalTestNetwork, LocalPlayerId = header.LocalPlayerId, HostSteamId = header.HostSteamId,
                Seats = header.Seats?.Select(s => new MatchSaveSeat { PlayerId = s.PlayerId, SteamId = s.SteamId, OperatorId = s.OperatorId,
                    PlayerName = s.PlayerName, Color = s.Color }).ToList() };
            validation?.Point("save.checkpoint.begin");
            SimulationRecoveryCheckpointDto checkpoint;
            try { checkpoint = await point.CreateCheckpointAsync().ConfigureAwait(false); }
            finally { validation?.Point("save.checkpoint.end"); }
            return await Task.Run(() => {
                validation?.Source(checkpoint.Archive.Snapshot.State.EffectRuntime?.StateRevision ?? 0);
                using var marker = validation?.BeginStage("save.prepare.window");
                data.GameId = point.GameId; data.ContentHash = point.ContentHash; data.MapId = point.MapId; data.Round = point.Round;
                if (data.SavedUtcTicks <= 0) data.SavedUtcTicks = DateTime.UtcNow.Ticks;
                data.ArchiveJson = HostRecoveryService.SerializeArchive(checkpoint.Archive);
                data.ContinuationJson = SimulationConfirmedRecoveryInstaller.SerializeContinuation(checkpoint.Continuation);
                data.SimulationCheckpointJson = SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint);
                if (validation == null) data.Validate(); else validation.Validate(data);
                return data;
            }).ConfigureAwait(false);
        }
        /// <summary>联机专用：只读已发布固定确认点，序列化与Validate全部在后台完成。</summary>
        public static Task<MatchSaveData> CreateFromPublishedSnapshotAsync(MatchSaveData header, SimulationPublishedSnapshot snapshot)
            => CreateFromPublishedSnapshotAsync(header, snapshot, null);
        public static async Task<MatchSaveData> CreateFromPublishedSnapshotAsync(MatchSaveData header, SimulationPublishedSnapshot snapshot, ValidationScope validation)
        {
            if (header == null || snapshot == null || !snapshot.IsHost || header.Mode != LaunchMode.Host ||
                header.LocalPlayerId != snapshot.LocalPlayerId || !string.IsNullOrEmpty(header.GameId) && header.GameId != snapshot.GameId)
                throw new ArgumentException("published_save_identity_invalid");
            // 入口只复制少量身份元数据，旧存档的大JSON不进入主线程复制路径。
            var data = new MatchSaveData { Format = header.Format, Version = header.Version, SavedUtcTicks = header.SavedUtcTicks,
                Mode = header.Mode, LocalTestNetwork = header.LocalTestNetwork, LocalPlayerId = header.LocalPlayerId, HostSteamId = header.HostSteamId,
                Seats = header.Seats?.Select(seat => seat == null ? null : new MatchSaveSeat { PlayerId = seat.PlayerId, SteamId = seat.SteamId,
                    OperatorId = seat.OperatorId, PlayerName = seat.PlayerName, Color = seat.Color }).ToList() };
            validation?.Point("save.checkpoint.begin");
            SimulationRecoveryCheckpointDto checkpoint;
            try { checkpoint = await snapshot.CreateSaveCheckpointAsync().ConfigureAwait(false); }
            finally { validation?.Point("save.checkpoint.end"); }
            return await Task.Run(() =>
            {
                validation?.Source(checkpoint.Archive.Snapshot.State.EffectRuntime?.StateRevision ?? 0);
                using var marker = validation?.BeginStage("save.prepare.snapshot");
                data.GameId = snapshot.GameId; data.ContentHash = snapshot.ContentHash; data.MapId = snapshot.MapId; data.Round = snapshot.Round;
                if (data.SavedUtcTicks <= 0) data.SavedUtcTicks = DateTime.UtcNow.Ticks;
                data.ArchiveJson = HostRecoveryService.SerializeArchive(checkpoint.Archive);
                data.ContinuationJson = SimulationConfirmedRecoveryInstaller.SerializeContinuation(checkpoint.Continuation);
                data.SimulationCheckpointJson = SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint);
                if (validation == null) data.Validate(); else validation.Validate(data);
                return data;
            }).ConfigureAwait(false);
        }

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

        private static JObject ReadValidationArchive(string json, bool localProjection)
            => localProjection ? LocalArchiveValidationProjection.Read(json) : JObject.Parse(json,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });

        // 仅供本地ValidateCore读取；完整语法和所有对象查重仍由逐token读取验证。
        // 公开ReadArchiveState与Host继续构建完整树；不要把此投影传给恢复或DeepEquals。
        private static class LocalArchiveValidationProjection
        {
            private enum Shape { Discard, Full, Root, Snapshot, State, Players, Player, Journal }

            public static JObject Read(string json)
            {
                var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
                try
                {
                    using var text = new System.IO.StringReader(json);
                    using var reader = new Newtonsoft.Json.JsonTextReader(text);
                    // 不修改默认深度、日期、浮点、注释或多内容策略，与JObject.Parse一致。
                    if (!ReadContent(reader) || reader.TokenType != Newtonsoft.Json.JsonToken.StartObject)
                        throw new Newtonsoft.Json.JsonReaderException("需要完整JSON对象。");
                    var root = (JObject)ReadValue(reader, Shape.Root, settings);
                    while (reader.Read()) { } // 包括尾注释；多值/非法尾内容由原reader拒绝。
                    return root;
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    // 冷错误路径重新走原解析，保原错误类型/位置/先后顺序及宽松语法兼容。
                    return JObject.Parse(json, settings);
                }
            }

            private static bool ReadContent(Newtonsoft.Json.JsonReader reader)
            {
                while (reader.Read())
                    if (reader.TokenType != Newtonsoft.Json.JsonToken.Comment) return true;
                return false;
            }

            private static JToken ReadValue(Newtonsoft.Json.JsonReader reader, Shape shape, JsonLoadSettings settings)
            {
                if (shape == Shape.Full) return JToken.ReadFrom(reader, settings);
                if (reader.TokenType == Newtonsoft.Json.JsonToken.StartObject && shape != Shape.Players)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    var result = shape == Shape.Discard ? null : new JObject();
                    while (ReadContent(reader))
                    {
                        if (reader.TokenType == Newtonsoft.Json.JsonToken.EndObject) return result;
                        if (reader.TokenType != Newtonsoft.Json.JsonToken.PropertyName)
                            throw new Newtonsoft.Json.JsonReaderException("对象属性结构不完整。");
                        string name = (string)reader.Value;
                        if (!names.Add(name)) throw new Newtonsoft.Json.JsonReaderException("对象含重复属性。");
                        var childShape = Select(shape, name);
                        if (!ReadContent(reader)) throw new Newtonsoft.Json.JsonReaderException("属性值缺失。");
                        var child = ReadValue(reader, childShape, settings);
                        if (childShape != Shape.Discard) result.Add(name, child);
                    }
                    throw new Newtonsoft.Json.JsonReaderException("对象未结束。");
                }
                if (reader.TokenType == Newtonsoft.Json.JsonToken.StartArray && (shape == Shape.Players || shape == Shape.Discard))
                {
                    var result = shape == Shape.Players ? new JArray() : null;
                    while (ReadContent(reader))
                    {
                        if (reader.TokenType == Newtonsoft.Json.JsonToken.EndArray) return result;
                        var child = ReadValue(reader, shape == Shape.Players ? Shape.Player : Shape.Discard, settings);
                        if (result != null) result.Add(child);
                    }
                    throw new Newtonsoft.Json.JsonReaderException("数组未结束。");
                }
                // 保留字段遇错误形状时仍完整物化，使原转换/索引校验按原值拒绝。
                if (shape != Shape.Discard) return JToken.ReadFrom(reader, settings);
                if (reader.TokenType == Newtonsoft.Json.JsonToken.StartConstructor)
                {
                    while (ReadContent(reader))
                    {
                        if (reader.TokenType == Newtonsoft.Json.JsonToken.EndConstructor) return null;
                        ReadValue(reader, Shape.Discard, settings);
                    }
                    throw new Newtonsoft.Json.JsonReaderException("构造值未结束。");
                }
                switch (reader.TokenType)
                {
                    case Newtonsoft.Json.JsonToken.Integer:
                    case Newtonsoft.Json.JsonToken.Float:
                    case Newtonsoft.Json.JsonToken.String:
                    case Newtonsoft.Json.JsonToken.Boolean:
                    case Newtonsoft.Json.JsonToken.Null:
                    case Newtonsoft.Json.JsonToken.Undefined:
                    case Newtonsoft.Json.JsonToken.Date:
                    case Newtonsoft.Json.JsonToken.Bytes:
                        return null;
                    default:
                        throw new Newtonsoft.Json.JsonReaderException("JSON值结构不完整。");
                }
            }

            // ValidateCore新增读取归档字段时，必须同步更新此投影读集。
            private static Shape Select(Shape parent, string name)
            {
                switch (parent)
                {
                    case Shape.Root:
                        if (name == "Snapshot") return Shape.Snapshot;
                        if (name == "Journal") return Shape.Journal;
                        break;
                    case Shape.Snapshot:
                        if (name == "State") return Shape.State;
                        if (name == "SchemaVersion" || name == "ContentHash") return Shape.Full;
                        break;
                    case Shape.State:
                        if (name == "Players") return Shape.Players;
                        if (name == "ContentPackHash" || name == "GameId" || name == "MapId" || name == "Round" ||
                            name == "RuleConfiguration" || name == "RandomRuntime" || name == "EventDeckSeed" ||
                            name == "LaunchPlayerOrder") return Shape.Full;
                        break;
                    case Shape.Player:
                        if (name == "PlayerId" || name == "Color") return Shape.Full;
                        break;
                    case Shape.Journal:
                        if (name == "SchemaVersion") return Shape.Full;
                        break;
                }
                return Shape.Discard;
            }
        }

        public void Validate() => ValidateCore(null);
        private void ValidateCore(ValidationScope validation)
        {
            if (Format != "YC.MatchSave" || Version != CurrentVersion)
                throw new InvalidOperationException("不支持此存档版本；需要包含完整规则及联机续接状态的存档。");
            if (Mode != LaunchMode.Local && Mode != LaunchMode.Host || string.IsNullOrEmpty(ArchiveJson) ||
                string.IsNullOrEmpty(ContinuationJson) || SavedUtcTicks <= 0 || SavedUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidOperationException("存档缺少完整的权威状态。");
            bool localProjection = Mode == LaunchMode.Local && Newtonsoft.Json.JsonConvert.DefaultSettings == null;
            var root = validation == null ? ReadValidationArchive(ArchiveJson, localProjection) : validation.ReadArchive(ArchiveJson, localProjection);
            var state = root["Snapshot"]?["State"] as JObject;
            if (state == null || (int?)root["Snapshot"]?["SchemaVersion"] != HostSnapshotDto.CurrentSchemaVersion ||
                (int?)root["Journal"]?["SchemaVersion"] != HostJournalDto.CurrentSchemaVersion ||
                string.IsNullOrEmpty(GameId) || string.IsNullOrEmpty(ContentHash) ||
                (string)root["Snapshot"]?["ContentHash"] != ContentHash || (string)state["ContentPackHash"] != ContentHash ||
                (string)state["GameId"] != GameId || (string)state["MapId"] != MapId || (int?)state["Round"] != Round)
                throw new InvalidOperationException("存档摘要与规则快照不一致。");
            // 完整扫描一次归档；Local仅物化校验读集，Host保留完整树；公开便利属性不可在此重复读取。
            var configuration = state["RuleConfiguration"].ToObject<GameRuleConfiguration>();
            if (configuration == null || !configuration.TryValidate(out _) || configuration.ContentPackHash != ContentHash ||
                configuration.MapId != MapId || !GameplayRandomRuntime.TryValidate(state["RandomRuntime"].ToObject<GameplayRandomState>(),
                    GameId, (int)state["EventDeckSeed"], out _))
                throw new InvalidOperationException("存档的规则配置或随机状态无效。");
            var players = ((JArray)state["Players"]).Select(p => (int)p["PlayerId"]).OrderBy(id => id).ToArray();
            if (Seats == null || Seats.Any(s => s == null || s.PlayerId <= 0) ||
                !Seats.Select(s => s.PlayerId).OrderBy(id => id).SequenceEqual(players) ||
                Seats.Select(s => s.PlayerId).Distinct().Count() != Seats.Count || configuration.PlayerCount != Seats.Count ||
                !players.Contains(LocalPlayerId) || !TurnOrderService.IsValidPlayerOrder(state["LaunchPlayerOrder"].ToObject<List<int>>(), players))
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
