using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public sealed class PreparedSimulationRecovery
    {
        internal readonly SimulationConfirmedRecoveryInstaller Owner;
        internal readonly PreparedGameSessionRecovery Session;
        internal readonly string BundleDigest;
        internal readonly bool ResumeAllowed;
        internal readonly SimulationRecoveryBaselineDto Baseline;
        public DeterministicSimulationCoordinator Coordinator { get; }
        public string CheckpointId => Baseline.CheckpointId;
        internal PreparedSimulationRecovery(SimulationConfirmedRecoveryInstaller owner, PreparedGameSessionRecovery session,
            DeterministicSimulationCoordinator coordinator, SimulationRecoveryBaselineDto baseline, string digest, bool resumeAllowed)
        { Owner = owner; Session = session; Coordinator = coordinator; Baseline = baseline; BundleDigest = digest; ResumeAllowed = resumeAllowed; }
    }

    /// <summary>运行中确认前缀专用。全部预检先于根/投影/私有日志与协议上下文发布。</summary>
    public sealed class SimulationConfirmedRecoveryInstaller
    {
        private readonly GameSession session;
        private readonly EffectRegistry registry;
        private readonly GameStateViewer viewer;
        private readonly DeterministicSimulationSessionOptions originalOptions;
        private readonly GameState startRandomFacts;
        private readonly string mapId, configuration;
        private readonly int[] launchOrder;
        private PreparedSimulationRecovery prepared;
        private string installedId, installedDigest;
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            ContractResolver = new ContinuationFieldResolver(),
            TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error,
            ObjectCreationHandling = ObjectCreationHandling.Replace, MaxDepth = 1024 };

        // 续接 DTO/定义上下文只保存公开字段；Archive 仍完全交给正式档案序列化器。
        private sealed class ContinuationFieldResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override System.Collections.Generic.List<System.Reflection.MemberInfo> GetSerializableMembers(Type type)
                => type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(field => !field.IsNotSerialized).Cast<System.Reflection.MemberInfo>().ToList();
        }

        public SimulationConfirmedRecoveryInstaller(GameSession session, EffectRegistry registry, GameStateViewer viewer,
            DeterministicSimulationSessionOptions options)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.viewer = viewer; originalOptions = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
            mapId = session.State.MapId; launchOrder = session.State.LaunchPlayerOrder?.ToArray();
            configuration = JsonConvert.SerializeObject(session.State.RuleConfiguration);
            startRandomFacts = GameStateCloneService.DeepClone(new GameState {
                GameId = session.State.GameId, EventDeckSeed = session.State.EventDeckSeed, RandomRuntime = session.State.RandomRuntime });
        }

        public bool IsBoundTo(GameSession live, DeterministicSimulationSessionOptions options) => ReferenceEquals(session, live) && options != null &&
            originalOptions.GameId == options.GameId && originalOptions.ContentPackHash == options.ContentPackHash && originalOptions.IsHost == options.IsHost &&
            originalOptions.LocalPlayerId == options.LocalPlayerId && originalOptions.MembershipVersion == options.MembershipVersion &&
            originalOptions.FrozenPlayerIds.SequenceEqual(options.FrozenPlayerIds) && originalOptions.RequiredReplicaIds.SequenceEqual(options.RequiredReplicaIds);

        public static string SerializeCheckpoint(SimulationRecoveryCheckpointDto checkpoint)
        {
            if (checkpoint?.Baseline == null || checkpoint.Archive == null || checkpoint.Continuation == null) throw new ArgumentException("recovery_package_incomplete");
            var serializer = JsonSerializer.Create(Settings);
            return new JObject { ["Baseline"] = JToken.FromObject(checkpoint.Baseline, serializer),
                ["Archive"] = JObject.Parse(HostRecoveryService.SerializeArchive(checkpoint.Archive)),
                ["Continuation"] = JToken.FromObject(checkpoint.Continuation, serializer) }.ToString(Formatting.None);
        }

        public static string SerializeContinuation(GameSessionLogContinuationDto continuation)
            => JsonConvert.SerializeObject(continuation, Settings);

        public static GameSessionLogContinuationDto ReadContinuation(string json)
        {
            var raw = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if ((int?)raw["SchemaVersion"] != 1 || raw["HasPendingActionLog"]?.Type != JTokenType.Boolean)
                throw new ArgumentException("recovery_continuation_fields_missing");
            return raw.ToObject<GameSessionLogContinuationDto>(JsonSerializer.Create(Settings));
        }

        public static SimulationRecoveryBaselineDto ReadBaseline(string json)
        {
            var root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (root.Count != 3 || !(root["Baseline"] is JObject) || root["Archive"] == null || root["Continuation"] == null)
                throw new ArgumentException("recovery_package_incomplete");
            return root["Baseline"].ToObject<SimulationRecoveryBaselineDto>(JsonSerializer.Create(Settings));
        }

        public bool TryPrepare(SimulationRecoveryNoticeDto expected, string bundleJson,
            out PreparedSimulationRecovery candidate, out SimulationRecoveryResultDto result)
        {
            candidate = null; result = new SimulationRecoveryResultDto { CheckpointId = expected?.CheckpointId ?? string.Empty };
            try
            {
                if (expected == null || expected.Phase != SimulationRecoveryPhase.Prepare || expected.ProtocolVersion != OperationCommandProtocol.Version ||
                    expected.GameId != originalOptions.GameId || expected.MembershipVersion != originalOptions.MembershipVersion ||
                    expected.SourceSimulationEpoch <= 0 || expected.SourceSimulationEpoch >= long.MaxValue || expected.SimulationEpoch != expected.SourceSimulationEpoch + 1 ||
                    string.IsNullOrEmpty(bundleJson) || new UTF8Encoding(false, true).GetByteCount(bundleJson) > 64 * 1024 * 1024)
                    throw new ArgumentException("recovery_prepare_identity_invalid");
                string digest;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    digest = BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(bundleJson))).Replace("-", "").ToLowerInvariant();
                if (prepared?.CheckpointId == expected.CheckpointId)
                {
                    if (prepared.BundleDigest != digest || prepared.ResumeAllowed != expected.ResumeAllowed) throw new ArgumentException("recovery_prepare_conflict");
                    candidate = prepared; result.Succeeded = true; return true;
                }
                var root = JObject.Parse(bundleJson, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (root.Count != 3 || root["Baseline"] == null || root["Archive"] == null || root["Continuation"] == null) throw new ArgumentException("recovery_package_incomplete");
                var serializer = JsonSerializer.Create(Settings);
                if (!(root["Continuation"] is JObject rawContinuation) || rawContinuation["SchemaVersion"]?.Type != JTokenType.Integer ||
                    (int)rawContinuation["SchemaVersion"] != 1 || rawContinuation["HasPendingActionLog"]?.Type != JTokenType.Boolean)
                    throw new ArgumentException("recovery_continuation_fields_missing");
                var baseline = root["Baseline"].ToObject<SimulationRecoveryBaselineDto>(serializer);
                if (baseline.GameId != expected.GameId || baseline.SimulationEpoch != expected.SimulationEpoch || baseline.SourceSimulationEpoch != expected.SourceSimulationEpoch ||
                    baseline.MembershipVersion != expected.MembershipVersion || baseline.CheckpointId != expected.CheckpointId ||
                    baseline.ConfirmedGlobalSequence != expected.ConfirmedGlobalSequence || baseline.MetadataDigest != expected.MetadataDigest)
                    throw new ArgumentException("recovery_notice_mismatch");
                var target = originalOptions.Snapshot(); target.SimulationEpoch = baseline.SimulationEpoch;
                target.CheckpointGlobalSequence = baseline.ConfirmedGlobalSequence; target.CheckpointPrefixDigest = baseline.CommandPrefixDigest;
                OperationCommandCanonicalizer.ValidateRecoveryBaseline(target, baseline);
                var recovered = HostRecoveryService.RecoverJson(root["Archive"].ToString(Formatting.None), registry);
                if (!recovered.Succeeded) throw new ArgumentException(recovered.FaultCode + ":" + recovered.Diagnostic);
                var state = recovered.State;
                if (state.EventDeckSeed != startRandomFacts.EventDeckSeed || state.MapId != mapId || launchOrder == null || state.LaunchPlayerOrder == null ||
                    !launchOrder.SequenceEqual(state.LaunchPlayerOrder) || configuration != JsonConvert.SerializeObject(state.RuleConfiguration) ||
                    !GameplayRandomRuntime.IsContinuation(startRandomFacts.RandomRuntime, state.RandomRuntime, startRandomFacts.EventDeckSeed))
                    throw new ArgumentException("recovery_start_facts_mismatch");
                OperationCommandCanonicalizer.ValidateRecoveryBaseline(target, baseline, state);
                var continuation = root["Continuation"].ToObject<GameSessionLogContinuationDto>(serializer);
                if (!session.TryPrepareConfirmedRecoveryState(state, continuation, viewer, out var sessionPrepared, out var fault)) throw new ArgumentException(fault);
                var coordinator = DeterministicSimulationCoordinator.FromConfirmedRecovery(session, target, baseline, state);
                candidate = prepared = new PreparedSimulationRecovery(this, sessionPrepared, coordinator,
                    OperationCommandCanonicalizer.SnapshotRecoveryBaseline(baseline), digest, expected.ResumeAllowed);
                result.Succeeded = true; return true;
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            { result.ErrorCode = "recovery_prepare_failed"; result.Reason = exception.Message; return false; }
        }

        public bool TryCommit(PreparedSimulationRecovery candidate, out SimulationRecoveryResultDto result)
        {
            result = new SimulationRecoveryResultDto { CheckpointId = candidate?.CheckpointId ?? string.Empty };
            if (candidate == null || candidate.Owner != this || candidate != prepared)
            { result.ErrorCode = "recovery_prepared_mismatch"; return false; }
            if (installedId == candidate.CheckpointId)
            {
                result.Succeeded = installedDigest == candidate.BundleDigest;
                if (!result.Succeeded) result.ErrorCode = "recovery_install_conflict";
                return result.Succeeded;
            }
            if (!session.TryCommitPreparedRecovery(candidate.Session, out var fault))
            { result.ErrorCode = "recovery_commit_failed"; result.Reason = fault; return false; }
            installedId = candidate.CheckpointId; installedDigest = candidate.BundleDigest;
            result.Succeeded = true; return true;
        }
    }
}
