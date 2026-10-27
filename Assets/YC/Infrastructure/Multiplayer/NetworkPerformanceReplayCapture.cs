using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    // 开发采样专用；不是网络消息、存档或规则状态。字节预算含初始图、输入、目标证据和manifest。
    internal sealed class NetworkPerformanceReplayCapture
    {
        internal const int MaximumRecords = 128;
        internal const int MaximumBytes = 8 * 1024 * 1024;
        private const int ManifestReserve = 32768;
        private static readonly object activeGate = new object();
        private static readonly List<NetworkPerformanceReplayCapture> active = new List<NetworkPerformanceReplayCapture>(4);
        private static readonly Queue<string> errors = new Queue<string>(4);
        private readonly object gate = new object();
        private readonly List<byte[]> records = new List<byte[]>(MaximumRecords);
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly DeterministicSimulationSessionOptions options;
        private readonly string directory;
        private readonly long target;
        private readonly TaskCompletionSource<object> completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int stopped, finishing;
        private long bytes;
        private string initialJson, initialPending, firstError, stopReason;
        private SimulationCheckpointEnvelope initialEnvelope;
        private DeterministicSimulationCoordinator.PerformanceReplayPoint beforePoint, afterPoint;
        private bool targetApplied, truncated;
        private int exportedPoints;
        internal Task Completion => completion.Task;
        internal string FirstError => Volatile.Read(ref firstError);
        internal bool IsRecording => Volatile.Read(ref stopped) == 0;
        internal long Target => target;

        private NetworkPerformanceReplayCapture(string path, DeterministicSimulationSessionOptions options, long target)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || options == null || target <= 0) throw new ArgumentException("replay_configuration_invalid");
            this.options = options.Snapshot(); this.target = target;
            directory = Path.Combine(Path.GetFullPath(path), "capture-" + Guid.NewGuid().ToString("N"));
        }
        internal static NetworkPerformanceReplayCapture Create(string path, DeterministicSimulationSessionOptions options, long target)
        {
            var capture = new NetworkPerformanceReplayCapture(path, options, target);
            lock (activeGate)
            {
                active.RemoveAll(value => value.Completion.IsCompleted);
                if (active.Count >= 4) throw new InvalidOperationException("replay_pending_session_limit");
                active.Add(capture);
            }
            return capture;
        }
        internal Func<string> WrapInitialFactory(Func<string> original) => () => {
            string json = original();
            // 原工厂只调用一次；此处仅存不可变字符串引用，不序列化/IO/外部回调。
            if (IsRecording && json != null && json.Length <= MaximumBytes) initialPending = json;
            return json;
        };
        private sealed class Installer : ISimulationCheckpointInstaller
        {
            private readonly NetworkPerformanceReplayCapture owner;
            private readonly ISimulationCheckpointInstaller original;
            internal Installer(NetworkPerformanceReplayCapture owner, ISimulationCheckpointInstaller original) { this.owner = owner; this.original = original; }
            public bool TryInstall(string id, string json, out InitialSimulationCheckpointInstallResult result)
            {
                bool installed = original.TryInstall(id, json, out result);
                if (installed && owner.IsRecording && json != null && json.Length <= MaximumBytes) owner.initialPending = json;
                return installed;
            }
        }
        internal ISimulationCheckpointInstaller WrapInstaller(ISimulationCheckpointInstaller original) => new Installer(this, original);
        internal void CaptureInitial(SimulationCheckpointEnvelope envelope)
        {
            if (!IsRecording || initialJson != null) return;
            try
            {
                string pending = initialPending;
                if (pending == null || envelope == null) { Fail("initial_payload_unavailable"); return; }
                if (envelope.GlobalCommandSequence != 0 || envelope.SimulationEpoch != options.SimulationEpoch || envelope.GameId != options.GameId ||
                    envelope.Purpose != SimulationCheckpointEnvelope.InitialSimulation)
                { Fail("requires_formal_initial_baseline_in_original_epoch"); return; }
                long count = Encoding.UTF8.GetByteCount(pending);
                var identity = envelope.Snapshot();
                lock (gate)
                {
                    if (stopped != 0) return;
                    if (bytes + count > MaximumBytes - ManifestReserve) { truncated = true; stopReason = "initial_byte_limit"; Interlocked.Exchange(ref stopped, 1); return; }
                    initialJson = pending; initialEnvelope = identity; bytes += count; initialPending = null;
                }
            }
            catch (Exception error) { Fail(error.Message); }
        }
        internal bool RecordApply(DeterministicSimulationCoordinator.PerformanceReplaySample sample)
        {
            if (!IsRecording) return false;
            try
            {
                if (sample.DiagnosticError != null) { Fail(sample.DiagnosticError); return false; }
                if (sample.Ordered == null || sample.Ordered.GameId != options.GameId || sample.Ordered.SimulationEpoch != options.SimulationEpoch || sample.Ordered.MembershipVersion != options.MembershipVersion)
                { Fail("apply_identity_mismatch"); return false; }
                if (sample.Ordered.GlobalCommandSequence > target) return IsRecording;
                var delta = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var pair in sample.After)
                    if (pair.Key != "clock.frequency" && sample.Before.TryGetValue(pair.Key, out var previous)) delta[pair.Key] = pair.Value - previous;
                // 原始前后累计同时保留；ui/consumer累计可能有主线程并发，不能算本命令规则CPU。
                bool kept = Add("apply:" + sample.Ordered.GlobalCommandSequence + ":" + sample.Ordered.OrderedCommandDigest,
                    new { Kind = "apply", Ordered = sample.Ordered, sample.StartedTick, sample.EndedTick, sample.Frequency, sample.OwnerThreadId,
                        sample.IsHost, sample.AppliedThrough, sample.Before, sample.After, CounterDelta = delta, sample.Result });
                if (kept && sample.Ordered.GlobalCommandSequence == target)
                    lock (gate) { beforePoint = sample.BeforePoint; afterPoint = sample.AfterPoint; }
                return IsRecording;
            }
            catch (Exception error) { Fail(error.Message); return false; }
        }
        internal void RecordProtocol(string purpose, string json, string origin)
        {
            if (!IsRecording) return;
            if (purpose != "ordered" && purpose != "stream-record" && purpose != "stream-grant" && purpose != "stream-seal" && purpose != "stream-confirmed") return;
            try
            {
                if (json == null || Encoding.UTF8.GetByteCount(json) > MaximumBytes - ManifestReserve) { StopLimit("protocol_byte_limit"); return; }
                string digest = Hash(Encoding.UTF8.GetBytes(json));
                Add("protocol:" + purpose + ":" + digest, new { Kind = "protocol", Purpose = purpose, Origin = origin,
                    SourceTick = System.Diagnostics.Stopwatch.GetTimestamp(), Frequency = System.Diagnostics.Stopwatch.Frequency,
                    SourceThreadId = Thread.CurrentThread.ManagedThreadId, Payload = new JRaw(json) });
            }
            catch (Exception error) { Fail(error.Message); }
        }
        private bool Add(string identity, object value)
        {
            if (!IsRecording) return false;
            lock (gate) if (seen.Contains(identity)) return true;
            // 所有序列化/编码均在gate之外；队列只持独立的有界UTF8字节。
            byte[] encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value) + "\n");
            bool limit = false;
            lock (gate)
            {
                if (stopped != 0) return false;
                if (seen.Contains(identity)) return true;
                if (records.Count >= MaximumRecords || bytes + encoded.Length > MaximumBytes - ManifestReserve)
                { truncated = true; stopReason = records.Count >= MaximumRecords ? "record_limit" : "record_byte_limit"; Interlocked.Exchange(ref stopped, 1); limit = true; }
                else
                {
                    records.Add(encoded); seen.Add(identity); bytes += encoded.Length;
                    if (records.Count == MaximumRecords) { truncated = true; stopReason = "record_limit"; Interlocked.Exchange(ref stopped, 1); limit = true; }
                }
            }
            if (limit) CompleteAfter(Task.CompletedTask, stopReason);
            return !limit;
        }
        private void StopLimit(string reason)
        { lock (gate) { truncated = true; stopReason = reason; Interlocked.Exchange(ref stopped, 1); } CompleteAfter(Task.CompletedTask, reason); }
        private void Fail(string error)
        {
            string message = string.IsNullOrEmpty(error) ? "replay_unknown_error" : error.Length > 512 ? error.Substring(0, 512) : error;
            if (Interlocked.CompareExchange(ref firstError, message, null) == null)
                lock (activeGate) { if (errors.Count == 4) errors.Dequeue(); errors.Enqueue(message); }
            Interlocked.Exchange(ref stopped, 1);
        }
        internal void ObserveApplied(long sequence)
        {
            if (sequence != target) return;
            targetApplied = true; CompleteAfter(Task.CompletedTask, "target_applied");
        }
        internal void CompleteAfter(Task executionStopped, string reason)
        {
            if (Interlocked.CompareExchange(ref finishing, 1, 0) != 0) return;
            // 不在Unity同步上下文等退出；在途Apply先交完独立快照，才封闭诊断。
            Task.Run(async () => {
                try { await executionStopped.ConfigureAwait(false); Finish(reason); }
                catch (Exception error) { Fail(error.Message); }
                finally
                {
                    lock (gate) { records.Clear(); seen.Clear(); initialJson = initialPending = null; beforePoint = afterPoint = null; }
                    completion.TrySetResult(null);
                }
            });
        }
        private void Finish(string reason)
        {
            Interlocked.Exchange(ref stopped, 1);
            byte[][] saved; string initial; SimulationCheckpointEnvelope envelope;
            DeterministicSimulationCoordinator.PerformanceReplayPoint before, after;
            lock (gate) { saved = records.ToArray(); initial = initialJson; envelope = initialEnvelope; before = beforePoint; after = afterPoint; stopReason = stopReason ?? reason; }
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (initial != null) files.Add("initial-archive.json", Encoding.UTF8.GetBytes(initial));
            using (var stream = new MemoryStream()) { foreach (var value in saved) stream.Write(value, 0, value.Length); files.Add("inputs.jsonl", stream.ToArray()); }
            long contentBytes = files.Values.Sum(value => (long)value.Length);
            AddPoint(files, "target-before.json", before, ref contentBytes);
            AddPoint(files, "target-after.json", after, ref contentBytes);
            var coverage = InspectPrefix(saved);
            var hashes = files.Select(pair => new { File = pair.Key, Bytes = pair.Value.Length, Sha256 = Hash(pair.Value) }).ToArray();
            byte[] manifest = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
                Schema = "yc.performance-replay.v1", CaptureOnly = true, options, TargetSequence = target, InitialEnvelope = envelope,
                OriginalEpochUnmodified = true, MainMvid = typeof(NetworkPerformanceReplayCapture).Assembly.ManifestModule.ModuleVersionId,
                RecordCount = saved.Length, TargetApplied = targetApplied, InitialPresent = initial != null, ExportedTargetPoints = exportedPoints,
                CompleteOrderedPrefix = coverage.Complete && initial != null, OrderedPrefixCount = coverage.Count, ReplayValidated = false,
                Truncated = truncated, StopReason = stopReason, FirstError = firstError, PayloadBytes = contentBytes, Files = hashes,
                ReplayPath = "formal initial archive in original epoch + complete ordered prefix and stream grant/record/seal/confirmation; never rewrite an epoch2 save",
                CounterMeaning = "owner ApplyOrdered boundary; inclusive existing counters; consumer/ui deltas may contain main-thread concurrency; no CPU/allocation claim"
            }, Formatting.Indented));
            if (manifest.Length > ManifestReserve || contentBytes + manifest.Length > MaximumBytes) { Fail("manifest_byte_limit"); return; }
            Directory.CreateDirectory(directory);
            foreach (var pair in files) WriteNew(Path.Combine(directory, pair.Key), pair.Value);
            WriteNew(Path.Combine(directory, "manifest.json"), manifest);
        }
        private (bool Complete, int Count) InspectPrefix(byte[][] saved)
        {
            var commands = new SortedDictionary<long, OrderedOperationCommandDto>();
            try
            {
                foreach (var line in saved)
                {
                    var row = JObject.Parse(Encoding.UTF8.GetString(line));
                    JToken token = (string)row["Kind"] == "apply" ? row["Ordered"] :
                        (string)row["Purpose"] == "ordered" ? row["Payload"] : (string)row["Purpose"] == "stream-record" ? row["Payload"]?["Ordered"] : null;
                    if (token == null) continue;
                    var command = token.ToObject<OrderedOperationCommandDto>();
                    if (command.GameId != options.GameId || command.SimulationEpoch != options.SimulationEpoch || command.MembershipVersion != options.MembershipVersion ||
                        OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(command) != command.OrderedCommandDigest) { Fail("captured_ordered_identity_or_digest_invalid"); return (false, commands.Count); }
                    if (commands.TryGetValue(command.GlobalCommandSequence, out var old) && old.OrderedCommandDigest != command.OrderedCommandDigest)
                    { Fail("captured_ordered_sequence_conflict"); return (false, commands.Count); }
                    commands[command.GlobalCommandSequence] = command;
                }
                bool complete = target <= MaximumRecords && Enumerable.Range(1, (int)target).All(sequence => commands.ContainsKey(sequence));
                return (complete, commands.Count);
            }
            catch (Exception error) { Fail(error.Message); return (false, commands.Count); }
        }
        private void AddPoint(Dictionary<string, byte[]> files, string name, DeterministicSimulationCoordinator.PerformanceReplayPoint point, ref long contentBytes)
        {
            if (point == null) return;
            try
            {
                var exported = point.Export();
                string digest = RuleStateDigestService.Compute(exported.Archive.Snapshot.State);
                byte[] encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { RuleDigest = digest,
                    Archive = new JRaw(HostRecoveryService.SerializeArchive(exported.Archive)), exported.Continuation }));
                if (contentBytes + encoded.Length > MaximumBytes - ManifestReserve) { truncated = true; stopReason = "target_evidence_byte_limit"; return; }
                files.Add(name, encoded); contentBytes += encoded.Length; exportedPoints++;
            }
            catch (Exception error) { Fail(error.Message); }
        }
        private static void WriteNew(string path, byte[] value)
        { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) stream.Write(value, 0, value.Length); }
        private static string Hash(byte[] value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        internal static string[] FlushPending(int milliseconds)
        {
            NetworkPerformanceReplayCapture[] values;
            lock (activeGate) values = active.ToArray();
            try { Task.WaitAll(values.Select(value => value.Completion).ToArray(), milliseconds); } catch { }
            lock (activeGate)
            {
                var messages = errors.ToArray(); errors.Clear();
                return messages.Concat(values.Where(value => !value.Completion.IsCompleted).Select(value => "replay_finish_not_completed_before_exit")).ToArray();
            }
        }
    }
}
