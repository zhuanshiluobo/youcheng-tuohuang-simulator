using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using YC.Application.Sessions;

namespace YC.Infrastructure.Persistence
{
    public enum MatchSaveSizePhase { None, Started, Prepared, Committed, Completed, Failed, Cancelled }

    // 仅含标量；各阶段携带本笔入队序号，旧准备/落盘值不能归到后来的失败或取消。
    public readonly struct MatchSaveSizeStatistics
    {
        public readonly bool Enabled;
        public readonly long LastEnqueuedSequence, LastStartedSequence, LastPreparedSequence, LastCommittedSequence, LastFinishedSequence;
        public readonly MatchSaveSizePhase LastStartedPhase, LastFinishedPhase;
        public readonly int? LastPreparedArchiveChars, LastPreparedContinuationChars;
        public readonly long? LastCommittedFileBytes;
        internal MatchSaveSizeStatistics(bool enabled, long enqueuedSequence, long startedSequence, MatchSaveSizePhase startedPhase,
            long preparedSequence, int? archiveChars, int? continuationChars, long committedSequence, long? fileBytes,
            long finishedSequence, MatchSaveSizePhase finishedPhase)
        {
            Enabled = enabled; LastEnqueuedSequence = enqueuedSequence; LastStartedSequence = startedSequence; LastStartedPhase = startedPhase;
            LastPreparedSequence = preparedSequence; LastPreparedArchiveChars = archiveChars; LastPreparedContinuationChars = continuationChars;
            LastCommittedSequence = committedSequence; LastCommittedFileBytes = fileBytes;
            LastFinishedSequence = finishedSequence; LastFinishedPhase = finishedPhase;
        }
    }

    public sealed class MatchSaveSlot
    {
        public int Index;
        public bool Exists;
        public MatchSaveData Data;
        public string Error;
    }

    /// <summary>调用方序列化隔离快照；槽位校验、存档包装和原子写盘在串行后台队列完成。</summary>
    public sealed class MatchSaveStore
    {
        private sealed class SaveFieldsResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType)
            {
                // DTO 的 public 字段是唯一存档契约，不序列化领域类型的派生属性或重复别名。
                return new List<MemberInfo>(objectType.GetFields(BindingFlags.Instance | BindingFlags.Public));
            }
        }
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new SaveFieldsResolver(),
            TypeNameHandling = TypeNameHandling.None,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MaxDepth = 1024,
            MissingMemberHandling = MissingMemberHandling.Error
        };
        [Serializable] private sealed class FileEnvelope
        {
            public string Format = "YC.MatchSave";
            public int Version = MatchSaveData.CurrentVersion;
            public string Payload;
            public string Checksum;
        }
        private static string Hash(string value)
        {
            return HashUtf8Bounded(value);
        }
        // 保存与读取使用相同的 UTF-8 和 SHA-256；分块处理避免额外分配整份存档大小的字节数组。
        private static string HashUtf8Bounded(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            using (var writer = new BoundedUtf8Writer(null, true, true))
            {
                writer.Write(value);
                return writer.Complete().Digest;
            }
        }
        // 有行为回调或非内置解析器时完全沿原SerializeObject/WriteAtomic分支。
        // 本快路径要求原有稳定默认设置合同；不新增全局设置并发修改支持。
        private static bool CanStreamEnvelope()
            => JsonConvert.DefaultSettings == null && JsonSettings.ContractResolver is SaveFieldsResolver resolver &&
                resolver.NamingStrategy == null && (JsonSettings.Converters == null || JsonSettings.Converters.Count == 0) &&
                JsonSettings.TraceWriter == null && JsonSettings.Error == null && JsonSettings.ReferenceResolverProvider == null &&
                JsonSettings.EqualityComparer == null && JsonSettings.SerializationBinder == null &&
                JsonSettings.TypeNameHandling == TypeNameHandling.None && JsonSettings.PreserveReferencesHandling == PreserveReferencesHandling.None;
        private sealed class DiscardEnvelopeTextWriter : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;
            public override void Write(char value) { }
            public override void Write(string value) { }
            public override void Write(char[] buffer, int index, int count) { }
        }
        private static void PreflightEnvelope(JsonSerializer serializer, FileEnvelope envelope)
        {
            // 保留“完整外层序列化后、任何.tmp创建前”的原取消点。
            // 内置无回调合同可重复遍历；不保留完整外层正文。额外CPU与内存收益须在实际运行中权衡。
            using (var sink = new DiscardEnvelopeTextWriter())
            using (var writer = new JsonTextWriter(sink) { Formatting = serializer.Formatting })
                serializer.Serialize(writer, envelope);
        }
        // 只编码Json.NET已发出的字符，不重写JSON转义。Dispose不补写/重试失败输出；
        // 调用方仅在成功序列化后Complete排空编码器，再由原子写入口Flush(true)。
        private sealed class BoundedUtf8Writer : TextWriter
        {
            private const int CharacterBlock = 4096;
            private readonly Stream output;
            private readonly Encoding utf8 = new UTF8Encoding(false);
            private readonly Encoder encoder;
            private readonly char[] characters = new char[CharacterBlock];
            private readonly byte[] encoded;
            private readonly bool hashRequired;
            private SHA256 hash;
            private long bytes;
            private int characterCount;
            private bool completed, disposed;
            private FileIdentity identity;
            internal BoundedUtf8Writer(Stream output, bool identify, bool hashRequired = false)
            {
                this.output = output; this.hashRequired = hashRequired;
                encoder = utf8.GetEncoder(); encoded = new byte[utf8.GetMaxByteCount(CharacterBlock)];
                if (identify)
                {
                    if (hashRequired) hash = SHA256.Create() ?? throw new CryptographicException("无法建立存档校验哈希。");
                    else { try { hash = SHA256.Create(); } catch { } }
                }
            }
            public override Encoding Encoding => utf8;
            public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;
            private void EnsureWriting()
            { if (disposed || completed) throw new ObjectDisposedException(nameof(BoundedUtf8Writer)); }
            private void DropOptionalHash()
            {
                var old = hash; hash = null;
                try { old?.Dispose(); } catch { }
            }
            private void WriteEncoded(int count)
            {
                if (count == 0) return;
                output?.Write(encoded, 0, count);
                bytes += count;
                if (hash == null) return;
                if (hashRequired) hash.TransformBlock(encoded, 0, count, encoded, 0);
                else { try { hash.TransformBlock(encoded, 0, count, encoded, 0); } catch { DropOptionalHash(); } }
            }
            // 跨Json.NET的小Write聚合，避免逐引号编码/hash/写流。
            // 使用有界工作区，保留预检/取消点、转义器、编码fallback及原子提交行为。
            private void EncodePending(bool flush)
            {
                WriteEncoded(encoder.GetBytes(characters, 0, characterCount, encoded, 0, flush));
                characterCount = 0;
            }
            public override void Write(char value)
            {
                EnsureWriting(); characters[characterCount++] = value;
                if (characterCount == CharacterBlock) EncodePending(false);
            }
            public override void Write(string value)
            {
                EnsureWriting(); if (value == null) return;
                for (int offset = 0; offset < value.Length;)
                {
                    int count = Math.Min(CharacterBlock - characterCount, value.Length - offset);
                    value.CopyTo(offset, characters, characterCount, count);
                    characterCount += count; offset += count;
                    if (characterCount == CharacterBlock) EncodePending(false);
                }
            }
            public override void Write(char[] buffer, int index, int count)
            {
                EnsureWriting();
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (index < 0 || count < 0 || index > buffer.Length - count) throw new ArgumentOutOfRangeException();
                while (count != 0)
                {
                    int part = Math.Min(CharacterBlock - characterCount, count);
                    Array.Copy(buffer, index, characters, characterCount, part);
                    characterCount += part; index += part; count -= part;
                    if (characterCount == CharacterBlock) EncodePending(false);
                }
            }
            public override void Flush()
            {
                if (disposed) throw new ObjectDisposedException(nameof(BoundedUtf8Writer));
                if (!completed) EncodePending(true);
                // 不提前做底层耐久flush；只有外层原子入口执行一次Flush(true)。
            }
            internal FileIdentity Complete()
            {
                if (completed) return identity;
                Flush();
                if (hash != null)
                {
                    if (hashRequired)
                    {
                        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        identity = new FileIdentity(Convert.ToBase64String(hash.Hash), bytes);
                    }
                    else
                    {
                        try
                        {
                            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            identity = new FileIdentity(Convert.ToBase64String(hash.Hash), bytes);
                        }
                        catch { DropOptionalHash(); }
                    }
                }
                completed = true; return identity;
            }
            protected override void Dispose(bool disposing)
            {
                disposed = true;
                if (disposing && hash != null)
                {
                    if (hashRequired) hash.Dispose();
                    else DropOptionalHash();
                    hash = null;
                }
                base.Dispose(disposing); // 不flush编码器，不关闭调用方的FileStream。
            }
        }
        // 只由串行保存/删除队列访问；Read/List不读写此缓存。
        // 验证器或JSON契约改变时须递增；Store不跨程序集/域重载存活。
        private const int OwnershipValidationContract = 2;
        private readonly ValidatedOwnership[] ownershipCache = new ValidatedOwnership[SlotCount];
        private sealed class SlotOwnershipData
        {
            internal readonly string GameId, ContentHash;
            internal readonly long SavedUtcTicks;
            internal SlotOwnershipData(string gameId, string contentHash, long ticks)
            { GameId = gameId; ContentHash = contentHash; SavedUtcTicks = ticks; }
        }
        private sealed class SlotOwnership
        {
            internal bool Exists;
            internal SlotOwnershipData Data;
            internal string Error;
        }
        internal readonly struct FileIdentity
        {
            internal readonly string Digest;
            internal readonly long Bytes;
            internal FileIdentity(string digest, long bytes) { Digest = digest; Bytes = bytes; }
        }
        // 只记录本次完整校验实际读取的文件摘要，不持有存档正文或可变规则状态。
        public sealed class ResumeReadObservation
        {
            internal readonly MatchSaveStore owner;
            internal readonly int slot;
            internal readonly string gameId;
            internal readonly FileIdentity identity;
            internal ResumeReadObservation(MatchSaveStore owner, int slot, string gameId, FileIdentity identity)
            { this.owner = owner; this.slot = slot; this.gameId = gameId; this.identity = identity; }
        }
        private sealed class ValidatedOwnership
        {
            internal readonly SlotOwnershipData Data;
            internal readonly FileIdentity File;
            internal readonly int FormatVersion = MatchSaveData.CurrentVersion, Contract = OwnershipValidationContract;
            internal ValidatedOwnership(SlotOwnershipData data, FileIdentity file) { Data = data; File = file; }
        }
        private sealed class ValidatedPreview
        {
            internal readonly MatchSaveData Data;
            internal readonly FileIdentity File;
            internal ValidatedPreview(MatchSaveData data, FileIdentity file) { Data = data; File = file; }
        }
        private readonly object previewLock = new object();
        private readonly ValidatedPreview[] previewCache = new ValidatedPreview[SlotCount];
        // 最多保留一份有界的不可变存档正文，避免反复解析同一大档导致 Mono 堆持续扩大。
        // 超出此尺寸只关闭优化，读档能力和合法性不受影响。
        private const long MaxResumeCacheCharacters = 64L * 1024 * 1024;
        private sealed class ValidatedResume
        {
            internal readonly int Slot;
            internal readonly FileIdentity File;
            internal readonly MatchSaveData Data;
            internal ValidatedResume(int slot, FileIdentity file, MatchSaveData data) { Slot = slot; File = file; Data = data; }
        }
        private readonly object resumeLock = new object();
        private ValidatedResume resumeCache;
        private readonly struct PreparedPayload
        {
            internal readonly string Json;
            internal readonly SlotOwnershipData Ownership;
            internal readonly int? ArchiveChars, ContinuationChars;
            internal PreparedPayload(string json, SlotOwnershipData ownership, int? archiveChars, int? continuationChars)
            { Json = json; Ownership = ownership; ArchiveChars = archiveChars; ContinuationChars = continuationChars; }
        }
        private static bool CacheContextAvailable => JsonConvert.DefaultSettings == null;
        private static SlotOwnershipData Project(MatchSaveData data)
            => new SlotOwnershipData(data.GameId, data.ContentHash, data.SavedUtcTicks);
        private static bool IsShort(SlotOwnershipData data) => data != null &&
            data.GameId != null && data.GameId.Length <= 128 && data.ContentHash != null && data.ContentHash.Length <= 128;

        // 只取本次实际序列化输出的有界前缀，确认元数据与完整Validate过的独占DTO一致。
        // 取不到/默认JSON设置非空就不预热，不为预热重新解析整个新档。
        private static SlotOwnershipData ConfirmSerializedOwnership(string payload, SlotOwnershipData validated)
        {
            if (!CacheContextAvailable || !IsShort(validated)) return null;
            try
            {
                // 原写盘UTF8允许替换非法代理字符。发生替换时，校验过的UTF16文本
                // 不能充当落盘内容的证明；保持原写盘行为，只禁用该笔预热。
                new UTF8Encoding(false, true).GetByteCount(payload);
                using (var text = new StringReader(payload.Substring(0, Math.Min(payload.Length, 4096))))
                using (var reader = new JsonTextReader(text))
                {
                    string game = null, content = null; long ticks = 0;
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 1) continue;
                        string name = (string)reader.Value;
                        if (!reader.Read()) return null;
                        if (name == nameof(MatchSaveData.GameId) && reader.TokenType == JsonToken.String) game = (string)reader.Value;
                        else if (name == nameof(MatchSaveData.ContentHash) && reader.TokenType == JsonToken.String) content = (string)reader.Value;
                        else if (name == nameof(MatchSaveData.SavedUtcTicks) && reader.TokenType == JsonToken.Integer) ticks = Convert.ToInt64(reader.Value);
                        else reader.Skip();
                        if (game != null && content != null && ticks != 0)
                            return game == validated.GameId && content == validated.ContentHash && ticks == validated.SavedUtcTicks && CacheContextAvailable ? validated : null;
                    }
                }
            }
            catch { } // 预热失败不改变保存结果。
            return null;
        }
        public const int ManualCount = 3;
        public const int SlotCount = 6;
        private readonly string directory;
        private readonly object queueLock = new object();
        private Task tail = Task.CompletedTask;
        private readonly bool trackStatistics;
        private long enqueued, started, completed, failed, cancelled, pending, active, outstanding, peakOutstanding;
        private long sizeStartedSequence, sizePreparedSequence, sizeCommittedSequence, sizeFinishedSequence;
        private MatchSaveSizePhase sizePhase, sizeFinishedPhase;
        private int? sizePreparedArchiveChars, sizePreparedContinuationChars;
        private long? sizeCommittedFileBytes;
        private readonly HashSet<int> incompatibleSlots = new HashSet<int>();
        private string restoredGameId;
        private int restoredAutomaticSlot = -1;
        private string newGameId;
        public void BeginMatch(string gameId, bool isNewMatch)
        {
            lock (queueLock) newGameId = isNewMatch ? gameId : null;
            if (isNewMatch) lock (resumeLock) resumeCache = null;
        }
        public void SetRestoreSource(int slot, string gameId)
        {
            PathFor(slot);
            lock (queueLock)
            {
                restoredGameId = gameId;
                restoredAutomaticSlot = slot >= ManualCount ? slot : -1;
            }
        }
        public void ProtectAutomaticSlot(int slot)
        {
            if (slot < ManualCount || slot >= SlotCount) return;
            lock (queueLock) incompatibleSlots.Add(slot);
        }
        public MatchSaveStore(string directory) : this(directory, false) { }
        public MatchSaveStore(string directory, bool trackStatistics) { this.directory = directory; this.trackStatistics = trackStatistics; }
        public MatchSaveQueueStatistics ReadQueueStatistics()
        {
            lock (queueLock) return new MatchSaveQueueStatistics(trackStatistics, enqueued, started, completed, failed,
                cancelled, pending, active, outstanding, peakOutstanding);
        }
        // 与队列统计共用构造期开关；只复制纯值，不读取DTO、文件或重新计算身份。
        public MatchSaveSizeStatistics ReadSizeStatistics()
        {
            lock (queueLock) return new MatchSaveSizeStatistics(trackStatistics, enqueued, sizeStartedSequence, sizePhase,
                sizePreparedSequence, sizePreparedArchiveChars, sizePreparedContinuationChars, sizeCommittedSequence,
                sizeCommittedFileBytes, sizeFinishedSequence, sizeFinishedPhase);
        }
        public bool HasSaves()
        {
            for (int i = 0; i < SlotCount; i++)
                if (File.Exists(PathFor(i))) return true;
            return false;
        }
        private string PathFor(int slot)
        {
            if (slot < 0 || slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
            return Path.Combine(directory, (slot < ManualCount ? "manual-" : "auto-") + (slot % 3 + 1) + ".json");
        }
        public MatchSaveData Read(int slot) => ReadValidated(slot, out _);
        public MatchSaveData ReadWithValidation(int slot, MatchSaveData.ValidationScope validation)
        {
            if (validation == null) throw new ArgumentNullException(nameof(validation));
            return ReadValidated(slot, out _, validation);
        }
        public MatchSaveData ReadForResume(int slot, MatchSaveData.ValidationScope validation, out ResumeReadObservation observation)
        {
            var cached = ReadUnchangedResume(slot);
            if (cached != null)
            {
                var copy = cached.Data.CreateDetachedCopy();
                // 每次恢复仍执行原存档校验；缓存只复用经过摘要验证的不可变字符串。
                if (validation == null) copy.Validate(); else validation.Validate(copy);
                observation = new ResumeReadObservation(this, slot, copy.GameId, cached.File);
                return copy;
            }
            lock (resumeLock) resumeCache = null;
            var data = ReadValidated(slot, out _, out var identity, validation);
            long retained = (long)(data.ArchiveJson?.Length ?? 0) + (data.ContinuationJson?.Length ?? 0) +
                (data.SimulationCheckpointJson?.Length ?? 0) + (data.PrivateCheckpointJson?.Length ?? 0);
            var snapshot = identity.Digest != null && retained <= MaxResumeCacheCharacters && CanStreamEnvelope()
                ? new ValidatedResume(slot, identity, data.CreateDetachedCopy()) : null;
            lock (resumeLock) resumeCache = snapshot;
            observation = identity.Digest == null ? null : new ResumeReadObservation(this, slot, data.GameId, identity);
            return data;
        }
        private ValidatedResume ReadUnchangedResume(int slot)
        {
            ValidatedResume cached;
            lock (resumeLock) cached = resumeCache;
            if (cached == null || cached.Slot != slot || !CanStreamEnvelope()) return null;
            try
            {
                using (var stream = OpenOwnershipStream(slot))
                {
                    if (stream == null) return null;
                    var current = ReadFileIdentity(stream);
                    return current.Bytes == cached.File.Bytes && current.Digest == cached.File.Digest && CanStreamEnvelope()
                        ? cached : null;
                }
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
        }
        private void InvalidateResumeCache(int slot)
        {
            lock (resumeLock) if (resumeCache?.Slot == slot) resumeCache = null;
        }
        private MatchSaveData ReadValidated(int slot, out string validatedPayloadChecksum, MatchSaveData.ValidationScope validation = null)
            => ReadValidated(slot, out validatedPayloadChecksum, out _, validation);
        private MatchSaveData ReadValidated(int slot, out string validatedPayloadChecksum, out FileIdentity validatedFile,
            MatchSaveData.ValidationScope validation = null)
        {
            validatedPayloadChecksum = null; validatedFile = default;
            string text = null;
            FileEnvelope envelope = null;
            MatchSaveData canonical = null;
            string canonicalChecksum = null;
            bool streaming = CanStreamEnvelope();
            // 槽位列表读取不阻挡后台原子替换；已打开句柄仍读取同一份完整文件。
            using (var stream = new FileStream(PathFor(slot), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // 正常写出的标准包装先逐块校验，再从同一文件句柄反序列化。
                // 避免先物化整份嵌套 Payload 字符串；非标准格式仍走原解析器。
                if (!streaming || !TryReadCanonicalEnvelope(stream, out canonical, out canonicalChecksum, out validatedFile))
                {
                    stream.Position = 0;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        if (streaming) envelope = ReadEnvelope(reader);
                        else text = reader.ReadToEnd();
                    }
                }
            }
            // 完整读完并关闭同一文件句柄后，仍执行原校验、DTO反序列化和存档校验。
            if (canonical != null)
            {
                if (validation == null) canonical.Validate(); else validation.Validate(canonical);
                validatedPayloadChecksum = canonicalChecksum;
                return canonical;
            }
            return streaming ? ValidateEnvelope(envelope, out validatedPayloadChecksum, validation) :
                ParseValidated(text, out validatedPayloadChecksum, validation);
        }

        // 仅识别本程序无自定义 JSON 设置时写出的标准字段顺序。任何其它合法写法、
        // 损坏或不完整的输入都交回原路径，由原校验决定结果和错误。
        private static bool TryReadCanonicalEnvelope(FileStream stream, out MatchSaveData data, out string checksum, out FileIdentity identity)
        {
            data = null; checksum = null; identity = default;
            try
            {
                if (!ReadCanonicalPass(stream, false, out _, out string first, out _)) return false;
                if (!ReadCanonicalPass(stream, true, out data, out string second, out identity) || first != second || data == null)
                { data = null; identity = default; return false; }
                checksum = second;
                return true;
            }
            catch (Exception error) when (!(error is IOException) && !(error is OutOfMemoryException))
            { data = null; identity = default; return false; }
        }

        private static bool ReadCanonicalPass(FileStream stream, bool deserialize, out MatchSaveData data, out string checksum,
            out FileIdentity identity)
        {
            data = null; checksum = null; identity = default; stream.Position = 0;
            using (var raw = deserialize ? new IdentityReadStream(stream) : null)
            using (var source = new StreamReader(raw ?? (Stream)stream, Encoding.UTF8, true, 8192, true))
            {
                string prefix = "{\"Format\":\"YC.MatchSave\",\"Version\":" +
                    MatchSaveData.CurrentVersion.ToString(CultureInfo.InvariantCulture) + ",\"Payload\":\"";
                if (!ReadExact(source, prefix)) return false;
                using (var payload = new EscapedPayloadReader(source))
                {
                    if (deserialize)
                    {
                        using (var filtered = new CanonicalPayloadFieldReader(payload))
                        using (var reader = new JsonTextReader(filtered) { CloseInput = false, MaxDepth = JsonSettings.MaxDepth })
                        {
                            data = JsonSerializer.CreateDefault(JsonSettings).Deserialize<MatchSaveData>(reader);
                            while (reader.Read()) if (reader.TokenType != JsonToken.Comment) return false;
                            if (data != null) filtered.Apply(data);
                        }
                    }
                    else
                    {
                        var buffer = new char[8192];
                        while (payload.Read(buffer, 0, buffer.Length) != 0) { }
                    }
                    if (!payload.Closed || payload.CharacterCount == 0) return false;
                    string computed = payload.CompleteHash();
                    if (!ReadExact(source, ",\"Checksum\":\"")) return false;
                    var chars = new char[44];
                    for (int i = 0; i < chars.Length; i++)
                    {
                        int ch = source.Read();
                        if (ch < 0) return false;
                        chars[i] = (char)ch;
                    }
                    if (!ReadExact(source, "\"}")) return false;
                    int tail;
                    while ((tail = source.Read()) >= 0)
                        if (tail != ' ' && tail != '\t' && tail != '\r' && tail != '\n') return false;
                    checksum = new string(chars);
                    if (computed != checksum) return false;
                    if (raw != null) identity = raw.Complete();
                    return true;
                }
            }
        }

        // 标准字段以有界块还原大正文，元数据仍由 Json.NET 解析；非标准内容退回原解析器。
        private sealed class CanonicalPayloadFieldReader : TextReader
        {
            private static readonly HashSet<string> LargeFields = new HashSet<string>(StringComparer.Ordinal)
                { "ArchiveJson", "ContinuationJson", "SimulationCheckpointJson", "PrivateCheckpointJson" };
            private static readonly HashSet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
                { "Format", "Version", "SavedUtcTicks", "Mode", "LocalTestNetwork", "LocalPlayerId", "HostSteamId",
                  "MapId", "GameId", "Round", "ContentHash", "Seats", "ArchiveJson", "ContinuationJson",
                  "SimulationCheckpointJson", "PrivateCheckpointJson" };
            private readonly BufferedPayloadCursor source;
            private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            private string pending;
            private readonly char[] single = new char[1];
            private int pendingOffset, depth;
            private bool inString, escaping, expectProperty;
            internal CanonicalPayloadFieldReader(TextReader source) { this.source = new BufferedPayloadCursor(source); }
            internal void Apply(MatchSaveData data)
            {
                if (values.TryGetValue("ArchiveJson", out var archive)) data.ArchiveJson = archive;
                if (values.TryGetValue("ContinuationJson", out var continuation)) data.ContinuationJson = continuation;
                if (values.TryGetValue("SimulationCheckpointJson", out var simulation)) data.SimulationCheckpointJson = simulation;
                if (values.TryGetValue("PrivateCheckpointJson", out var checkpoint)) data.PrivateCheckpointJson = checkpoint;
            }
            public override int Read(char[] buffer, int index, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (index < 0 || count < 0 || index > buffer.Length - count) throw new ArgumentOutOfRangeException();
                int written = 0;
                while (written < count)
                {
                    if (pending != null)
                    {
                        int n = Math.Min(count - written, pending.Length - pendingOffset);
                        pending.CopyTo(pendingOffset, buffer, index + written, n);
                        written += n; pendingOffset += n;
                        if (pendingOffset == pending.Length) { pending = null; pendingOffset = 0; }
                        continue;
                    }
                    int ch = source.Read();
                    if (ch < 0) break;
                    if (!inString && depth == 1 && expectProperty && ch == '"')
                    { ReadProperty(); continue; }
                    Advance(ch);
                    buffer[index + written++] = (char)ch;
                }
                return written;
            }
            public override int Read()
            {
                return Read(single, 0, 1) == 0 ? -1 : single[0];
            }
            private void Advance(int ch)
            {
                if (inString)
                {
                    if (escaping) escaping = false;
                    else if (ch == '\\') escaping = true;
                    else if (ch == '"') inString = false;
                    return;
                }
                if (ch == '/' || ch == '\'' || ch == '(' || ch == ')') throw new FormatException("非标准结构需常规解析。");
                if (depth == 1 && expectProperty && ch != '}' && ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n')
                    throw new FormatException("未引用的属性需常规解析。");
                if (ch == '"') inString = true;
                else if (ch == '{' || ch == '[') { depth++; if (depth == 1 && ch == '{') expectProperty = true; }
                else if (ch == '}' || ch == ']') depth--;
                else if (ch == ',' && depth == 1) expectProperty = true;
            }
            private void ReadProperty()
            {
                var prefix = new StringBuilder("\"");
                var name = new StringBuilder();
                int ch;
                while ((ch = source.Read()) >= 0 && ch != '"')
                {
                    // 标准 DTO 字段名不含转义；其它情况退回完整解析。
                    if (ch == '\\' || ch < 0x20 || name.Length >= 256) throw new FormatException("属性名需常规解析。");
                    prefix.Append((char)ch); name.Append((char)ch);
                }
                if (ch != '"') throw new FormatException("属性名未结束。");
                prefix.Append('"');
                while ((ch = source.Read()) == ' ' || ch == '\r' || ch == '\n' || ch == '\t') prefix.Append((char)ch);
                if (ch != ':') throw new FormatException("属性分隔符无效。");
                prefix.Append(':');
                while ((ch = source.Read()) == ' ' || ch == '\r' || ch == '\n' || ch == '\t') prefix.Append((char)ch);
                if (ch < 0) throw new FormatException("属性值缺失。");
                string key = name.ToString();
                // 原反序列化器接受大小写别名及重复字段；快路径只处理精确且唯一的正式字段。
                if (!KnownFields.Contains(key) || !seen.Add(key)) throw new FormatException("非标准字段需常规解析。");
                expectProperty = false;
                if (LargeFields.Contains(key))
                {
                    if (ch == '"')
                    {
                        var text = new StringBuilder();
                        using (var decoded = new EscapedPayloadReader(source))
                        {
                            var block = new char[8192];
                            int read;
                            while ((read = decoded.Read(block, 0, block.Length)) != 0) text.Append(block, 0, read);
                            if (!decoded.Closed) throw new FormatException("正文未结束。");
                        }
                        values.Add(key, text.ToString());
                        pending = prefix.Append("null").ToString();
                        return;
                    }
                    if (ch != 'n') throw new FormatException("正文类型需常规解析。");
                }
                prefix.Append((char)ch);
                Advance(ch);
                pending = prefix.ToString();
            }
        }
        private sealed class BufferedPayloadCursor : TextReader
        {
            private readonly TextReader source;
            private readonly char[] buffer = new char[8192];
            private int position, length;
            internal BufferedPayloadCursor(TextReader source) { this.source = source; }
            public override int Read()
            {
                if (position == length)
                {
                    length = source.Read(buffer, 0, buffer.Length); position = 0;
                    if (length == 0) return -1;
                }
                return buffer[position++];
            }
        }

        private static bool ReadExact(TextReader source, string expected)
        {
            foreach (char ch in expected) if (source.Read() != ch) return false;
            return true;
        }

        // 只解码外层 JSON 字符串，向内层 Json.NET 提供有界字符流，并按原 UTF-8 合同计算摘要。
        private sealed class EscapedPayloadReader : TextReader
        {
            private readonly TextReader source;
            private readonly BoundedUtf8Writer hash = new BoundedUtf8Writer(null, true, true);
            private readonly char[] single = new char[1];
            internal bool Closed { get; private set; }
            internal long CharacterCount { get; private set; }
            internal EscapedPayloadReader(TextReader source) { this.source = source; }
            internal string CompleteHash() => hash.Complete().Digest;
            public override int Read() => Read(single, 0, 1) == 0 ? -1 : single[0];
            public override int Read(char[] buffer, int index, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (index < 0 || count < 0 || index > buffer.Length - count) throw new ArgumentOutOfRangeException();
                int written = 0;
                while (written < count && !Closed)
                {
                    int ch = source.Read();
                    if (ch < 0) throw new FormatException("存档字符串未结束。");
                    if (ch == '"') { Closed = true; break; }
                    if (ch == '\\')
                    {
                        ch = source.Read();
                        switch (ch)
                        {
                            case '"': case '\\': case '/': break;
                            case 'b': ch = '\b'; break;
                            case 'f': ch = '\f'; break;
                            case 'n': ch = '\n'; break;
                            case 'r': ch = '\r'; break;
                            case 't': ch = '\t'; break;
                            case 'u':
                                ch = 0;
                                for (int i = 0; i < 4; i++)
                                {
                                    int digit = source.Read();
                                    if (digit >= '0' && digit <= '9') digit -= '0';
                                    else if (digit >= 'a' && digit <= 'f') digit -= 'a' - 10;
                                    else if (digit >= 'A' && digit <= 'F') digit -= 'A' - 10;
                                    else throw new FormatException("存档 Unicode 转义无效。");
                                    ch = (ch << 4) | digit;
                                }
                                // Json.NET 对孤立代理项有专门的替换规则；交给原解析器处理。
                                if (ch >= 0xd800 && ch <= 0xdfff) throw new FormatException("存档代理项转义需常规解析。");
                                break;
                            default: throw new FormatException("存档字符串转义无效。");
                        }
                    }
                    else if (ch < 0x20) throw new FormatException("存档字符串包含控制字符。");
                    buffer[index + written++] = (char)ch;
                }
                if (written != 0) { hash.Write(buffer, index, written); CharacterCount += written; }
                return written;
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) hash.Dispose();
                base.Dispose(disposing);
            }
        }
        private static FileEnvelope ReadEnvelope(TextReader source)
        {
            using (var reader = new JsonTextReader(source) { CloseInput = false, MaxDepth = JsonSettings.MaxDepth })
            {
                var root = JObject.Load(reader);
                if (root.Property("Format", StringComparison.Ordinal) == null || root.Property("Version", StringComparison.Ordinal) == null)
                    throw new InvalidDataException("不是完整的对局存档文件。");
                // JsonConvert.DeserializeObject 原有的完整输入检查：尾部注释可以跳过，额外 JSON 值必须拒绝。
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment) throw new JsonSerializationException("存档外层包含额外内容。");
                return root.ToObject<FileEnvelope>(JsonSerializer.CreateDefault(JsonSettings));
            }
        }
        private static MatchSaveData ParseValidated(string text, out string validatedPayloadChecksum, MatchSaveData.ValidationScope validation = null)
        {
            validatedPayloadChecksum = null;
            // 在反序列化前检查包装版本；禁用类型名称加载。
            if (!text.Contains("\"Format\"") || !text.Contains("\"Version\""))
                throw new InvalidDataException("不是完整的对局存档文件。");
            var envelope = JsonConvert.DeserializeObject<FileEnvelope>(text, JsonSettings);
            return ValidateEnvelope(envelope, out validatedPayloadChecksum, validation);
        }
        private static MatchSaveData ValidateEnvelope(FileEnvelope envelope, out string validatedPayloadChecksum, MatchSaveData.ValidationScope validation = null)
        {
            validatedPayloadChecksum = null;
            if (envelope == null || envelope.Format != "YC.MatchSave" || envelope.Version != MatchSaveData.CurrentVersion)
                throw new InvalidDataException("不支持此对局存档文件版本。");
            if (string.IsNullOrEmpty(envelope.Payload) || envelope.Checksum != Hash(envelope.Payload))
                throw new InvalidDataException("存档完整性检查失败，无法读取。");
            var data = JsonConvert.DeserializeObject<MatchSaveData>(envelope.Payload, JsonSettings);
            if (data == null) throw new InvalidDataException("对局存档内容为空。");
            if (validation == null) data.Validate(); else validation.Validate(data);
            validatedPayloadChecksum = envelope.Checksum;
            return data;
        }
        private MatchSaveSlot ReadSlot(int index) => ReadSlotObserved(index, null, 0);
        private MatchSaveSlot ReadSlotObserved(int index, MatchSaveWeakObservation weak, long weakId)
        {
            var row = new MatchSaveSlot { Index = index, Exists = File.Exists(PathFor(index)) };
            if (row.Exists)
            {
                try
                {
                    row.Data = ReadValidated(index, out var checksum);
                    weak?.OldSlotReturned(weakId, index, row.Data, checksum, ReadQueueStatistics());
                }
                catch (Exception ex) { row.Error = ex.Message; }
            }
            return row;
        }
        // Read/List仍用原ReadSlot；仅保存选槽使用短投影，命中不构造旧DTO。
        private SlotOwnership ReadSlotOwnership(int index, MatchSaveWeakObservation weak = null, long weakId = 0)
        {
            var row = new SlotOwnership { Exists = true };
            try
            {
                // 不用File.Exists把权限/分享冲突/其它读错冒成空槽。
                using (var stream = OpenOwnershipStream(index))
                {
                    if (stream == null) { ownershipCache[index] = null; row.Exists = false; return row; }
                    bool cacheable = CacheContextAvailable;
                    var cached = ownershipCache[index];
                    if (cacheable && cached != null && cached.FormatVersion == MatchSaveData.CurrentVersion && cached.Contract == OwnershipValidationContract)
                    {
                        var identity = ReadFileIdentity(stream);
                        if (identity.Bytes == cached.File.Bytes && identity.Digest == cached.File.Digest && CacheContextAvailable)
                        { row.Data = cached.Data; return row; }
                        // 同一打开句柄重置后完整读取；第二遍边读边hash，身份属于实际解析的那批字节。
                        stream.Position = 0;
                    }
                    ownershipCache[index] = null;
                    FileIdentity validatedFile = default;
                    string checksum;
                    MatchSaveData data;
                    if (cacheable)
                    {
                        using (var hashing = new IdentityReadStream(stream))
                        using (var reader = new StreamReader(hashing, Encoding.UTF8, true, 1024, true))
                        {
                            bool streaming = CanStreamEnvelope();
                            FileEnvelope envelope = streaming ? ReadEnvelope(reader) : null;
                            string text = streaming ? null : reader.ReadToEnd();
                            validatedFile = hashing.Complete();
                            // 与原Read一致：全文件字节取得后先关句柄，再做重校验。
                            stream.Close();
                            data = streaming ? ValidateEnvelope(envelope, out checksum) : ParseValidated(text, out checksum);
                        }
                    }
                    else
                    {
                        string text;
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)) text = reader.ReadToEnd();
                        stream.Close();
                        data = ParseValidated(text, out checksum);
                    }
                    row.Data = Project(data);
                    if (cacheable && CacheContextAvailable && IsShort(row.Data))
                        ownershipCache[index] = new ValidatedOwnership(row.Data, validatedFile);
                    weak?.OldSlotReturned(weakId, index, data, checksum, ReadQueueStatistics());
                }
            }
            catch (Exception error) { ownershipCache[index] = null; row.Error = error.Message; }
            return row;
        }
        private FileStream OpenOwnershipStream(int index)
        {
            // 只有打开路径时的NotFound才代表空槽；解析器/设置提供器抛同名异常仍是读错。
            try { return new FileStream(PathFor(index), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        private static FileIdentity ReadFileIdentity(Stream stream)
        {
            using (var hashing = new IdentityReadStream(stream))
            {
                byte[] buffer = new byte[8192];
                while (hashing.Read(buffer, 0, buffer.Length) != 0) { }
                return hashing.Complete();
            }
        }
        // 仅方法内临时使用，绝不放进槽缓存；完整原始字节（含BOM/换行）逐块计入摘要。
        private sealed class IdentityReadStream : Stream
        {
            private readonly Stream inner;
            private readonly SHA256 hash = SHA256.Create();
            private long bytes;
            internal IdentityReadStream(Stream inner) { this.inner = inner; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = inner.Read(buffer, offset, count);
                if (read != 0) { hash.TransformBlock(buffer, offset, read, buffer, offset); bytes += read; }
                return read;
            }
            internal FileIdentity Complete()
            { hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return new FileIdentity(Convert.ToBase64String(hash.Hash), bytes); }
            protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
        public List<MatchSaveSlot> List()
        {
            var result = new List<MatchSaveSlot>();
            for (int i = 0; i < SlotCount; i++)
                result.Add(ReadSlot(i));
            return result;
        }
        // 列表中的文件读取、校验和 DTO 反序列化不访问 Unity，可在后台完成。
        public Task<List<MatchSaveSlot>> ListAsync() => Task.Run(List);
        // UI 只保留验证后的显示信息；槽位确认读取时仍重新读取文件并执行完整恢复校验。
        public List<MatchSaveSlot> ListPreviews()
        {
            var result = new List<MatchSaveSlot>();
            for (int i = 0; i < SlotCount; i++)
            {
                var slot = new MatchSaveSlot { Index = i, Exists = File.Exists(PathFor(i)) };
                if (!slot.Exists)
                {
                    lock (previewLock) previewCache[i] = null;
                }
                else
                {
                    try
                    {
                        slot.Data = ReadUnchangedPreview(i);
                        if (slot.Data == null)
                        {
                            var complete = ReadValidated(i, out _, out var identity);
                            slot.Data = complete.CreateMetadataPreview();
                            var privateCopy = identity.Digest != null && CanStreamEnvelope()
                                ? new ValidatedPreview(slot.Data.CreateMetadataPreview(), identity) : null;
                            lock (previewLock) previewCache[i] = privateCopy;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (previewLock) previewCache[i] = null;
                        slot.Data = null; slot.Error = ex.Message;
                    }
                }
                result.Add(slot);
            }
            return result;
        }
        private MatchSaveData ReadUnchangedPreview(int index)
        {
            ValidatedPreview cached;
            lock (previewLock) cached = previewCache[index];
            if (cached == null || !CanStreamEnvelope()) return null;
            try
            {
                using (var stream = OpenOwnershipStream(index))
                {
                    if (stream == null) return null;
                    var current = ReadFileIdentity(stream);
                    if (current.Bytes != cached.File.Bytes || current.Digest != cached.File.Digest || !CanStreamEnvelope()) return null;
                    return cached.Data.CreateMetadataPreview();
                }
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return null; }
        }
        public Task<List<MatchSaveSlot>> ListPreviewsAsync() => Task.Run(ListPreviews);
        public void EnsureResumeCapacity(string gameId)
            => EnsureResumeCapacityForRead(gameId, null);
        public void EnsureResumeCapacityForRead(string gameId, ResumeReadObservation observation)
        {
            // 只需要自动槽，并在首个可用或属于本局的槽位处结束；手动档及后续大档不应为此重复反序列化。
            for (int index = ManualCount; index < SlotCount; index++)
            {
                // 列表预览只有在完整文件摘要仍一致时才复用，损坏或替换后回到原槽位读取。
                var preview = ReadUnchangedPreview(index);
                if (preview != null)
                {
                    if (preview.GameId == gameId) return;
                    continue;
                }
                if (observation != null && ReferenceEquals(observation.owner, this) && observation.slot == index &&
                    observation.gameId == gameId && CanStreamEnvelope())
                {
                    try
                    {
                        using (var stream = OpenOwnershipStream(index))
                        {
                            if (stream != null)
                            {
                                var current = ReadFileIdentity(stream);
                                if (current.Bytes == observation.identity.Bytes && current.Digest == observation.identity.Digest && CanStreamEnvelope())
                                    return;
                            }
                        }
                    }
                    catch (Exception error) when (!(error is OutOfMemoryException)) { /* 交给原槽位读取处理错误。 */ }
                }
                var slot = ReadSlot(index);
                if (!slot.Exists || slot.Data?.GameId == gameId) return;
            }
            throw new IOException("本对局没有可更新的自动存档，自动槽位已满。请先删除一个不再需要的自动存档，再读取此对局。");
        }
        public Task SaveAsync(MatchSaveData data, int manualSlot = -1)
        {
            if (manualSlot < -1 || manualSlot >= ManualCount) throw new ArgumentOutOfRangeException(nameof(manualSlot));
            bool cacheable = CacheContextAvailable;
            data.Validate();
            cacheable = cacheable && CacheContextAvailable;
            // 只在调用线程冻结可变 DTO，校验和及外层 JSON 包装交给后台。
            var validatedOwnership = Project(data);
            string payload = JsonConvert.SerializeObject(data, JsonSettings);
            cacheable = cacheable && CacheContextAvailable;
            // 入队后调用方还会继续修改 DTO；后台只使用本次捕获的标识与文本。
            string gameId = data.GameId, contentHash = data.ContentHash;
            int? archiveChars = trackStatistics ? data.ArchiveJson?.Length : null;
            int? continuationChars = trackStatistics ? data.ContinuationJson?.Length : null;
            return EnqueueSave(() => Task.FromResult(new PreparedPayload(payload, cacheable ? ConfirmSerializedOwnership(payload, validatedOwnership) : null,
                archiveChars, continuationChars)), gameId, contentHash, manualSlot);
        }

        // 仅接受调用方已经取得的固定恢复点；工厂不得访问活动 Session、Unity 或 Mirror。
        // 与原 SaveAsync 共用唯一写盘队列，Local 原有同步冻结与返回语义保持。
        public Task SavePreparedAsync(Func<Task<MatchSaveData>> prepare, string gameId, string contentHash, int manualSlot = -1,
            CancellationToken cancellation = default)
        {
            if (prepare == null) throw new ArgumentNullException(nameof(prepare));
            return SavePreparedWithValidationAsync(_ => prepare(), gameId, contentHash, manualSlot, cancellation);
        }
        public Task SavePreparedWithValidationAsync(Func<MatchSaveData.ValidationScope, Task<MatchSaveData>> prepare,
            string gameId, string contentHash, int manualSlot = -1, CancellationToken cancellation = default, IMatchSaveTrace trace = null,
            MatchSaveWeakObservation weak = null)
        {
            if (prepare == null) throw new ArgumentNullException(nameof(prepare));
            if (manualSlot < -1 || manualSlot >= ManualCount) throw new ArgumentOutOfRangeException(nameof(manualSlot));
            long weakId = weak?.CandidateId ?? 0;
            return EnqueueSave(async () => {
                bool cacheable = CacheContextAvailable;
                using var validation = new MatchSaveData.ValidationScope(trace);
                MatchSaveTraceScope.Point(trace, "save.prepare.begin");
                MatchSaveData data;
                try { data = await prepare(validation).ConfigureAwait(false) ?? throw new InvalidDataException("确认存档内容为空。"); }
                finally { MatchSaveTraceScope.Point(trace, "save.prepare.end"); }
                if (data.GameId != gameId || data.ContentHash != contentHash)
                    throw new InvalidDataException("确认存档与入队对局身份不一致。");
                weak?.Prepared(weakId, data, ReadQueueStatistics());
                validation.Validate(data);
                // 完整校验结束即释放解析树，外层JSON序列化与旧槽读取不再保留它。
                validation.Dispose();
                var validatedOwnership = Project(data);
                using (MatchSaveTraceScope.Begin(trace, "save.serialize.payload"))
                {
                    string payload = JsonConvert.SerializeObject(data, JsonSettings);
                    return new PreparedPayload(payload, cacheable && CacheContextAvailable ? ConfirmSerializedOwnership(payload, validatedOwnership) : null,
                        trackStatistics ? data.ArchiveJson?.Length : null, trackStatistics ? data.ContinuationJson?.Length : null);
                }
            }, gameId, contentHash, manualSlot, cancellation, trace, weak, weakId);
        }

        private Task EnqueueSave(Func<Task<PreparedPayload>> preparePayload, string gameId, string contentHash, int manualSlot,
            CancellationToken cancellation = default, IMatchSaveTrace trace = null, MatchSaveWeakObservation weak = null, long weakId = 0)
        {
            MatchSaveTraceScope.Point(trace, "save.enqueue");
            weak?.Enqueued(weakId, ReadQueueStatistics());
            lock (queueLock)
            {
                if (trackStatistics) { enqueued++; pending++; outstanding++; peakOutstanding = Math.Max(peakOutstanding, outstanding); }
                long sequence = trackStatistics ? enqueued : 0; // 锁内捕获本笔值，worker不再读取后来增长的enqueued。
                int preferredSlot = restoredGameId == gameId ? restoredAutomaticSlot : -1;
                var previous = tail;
                tail = Task.Run(async () =>
                {
                    bool entered = false;
                    FileEnvelope streamingEnvelope = null;
                    JsonSerializer streamingSerializer = null;
                    try
                    {
                    try { await previous.ConfigureAwait(false); } catch { /* 后续保存仍然可以重试。 */ }
                    finally { previous = null; }
                    if (trackStatistics) lock (queueLock)
                    {
                        pending--; active++; started++;
                        sizeStartedSequence = sequence; sizePhase = MatchSaveSizePhase.Started;
                    }
                    entered = true;
                    MatchSaveTraceScope.Point(trace, "save.start");
                    string payload;
                    SlotOwnershipData writtenOwnership;
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var prepared = await preparePayload().ConfigureAwait(false);
                        payload = prepared.Json; writtenOwnership = prepared.Ownership;
                        if (trackStatistics) lock (queueLock)
                        {
                            sizePreparedSequence = sequence; sizePreparedArchiveChars = prepared.ArchiveChars;
                            sizePreparedContinuationChars = prepared.ContinuationChars; sizePhase = MatchSaveSizePhase.Prepared;
                        }
                        prepared = default;
                    }
                    finally { preparePayload = null; }
                    // 前一笔写入完成后再查归属，连续提交也只能创建一份同类存档。
                    bool automatic = manualSlot < 0;
                    int first = automatic ? ManualCount : 0;
                    int end = automatic ? SlotCount : ManualCount;
                    var slots = new SlotOwnership[SlotCount];
                    using (MatchSaveTraceScope.Begin(trace, "save.readslot"))
                        for (int i = first; i < end; i++) slots[i] = ReadSlotOwnership(i, weak, weakId);
                    int ownedSlot = -1;
                    for (int i = first; i < end; i++)
                    {
                        if (slots[i].Data?.GameId != gameId) continue;
                        if (ownedSlot < 0 || slots[i].Data.SavedUtcTicks > slots[ownedSlot].Data.SavedUtcTicks)
                            ownedSlot = i;
                    }
                    if (automatic && preferredSlot >= first && preferredSlot < end && slots[preferredSlot].Data?.GameId == gameId)
                        ownedSlot = preferredSlot;
                    if (!automatic && ownedSlot >= 0 && slots[manualSlot].Data?.GameId != gameId)
                        throw new IOException($"本对局已有手动存档，请更新手动 {ownedSlot + 1}。");
                    int slot = automatic ? ownedSlot : manualSlot;
                    if (automatic && slot < 0)
                        for (int i = first; i < end; i++)
                            if (!slots[i].Exists) { slot = i; break; }
                    if (automatic && slot < 0)
                    {
                        // 仅新局首次分配可以淘汰最旧兼容档；从手动档续局也不能覆盖别局。
                        lock (queueLock)
                            if (newGameId == gameId)
                                for (int i = first; i < end; i++)
                                {
                                    if (incompatibleSlots.Contains(i) || slots[i].Data == null || slots[i].Data.ContentHash != contentHash) continue;
                                    if (slot < 0 || slots[i].Data.SavedUtcTicks < slots[slot].Data.SavedUtcTicks) slot = i;
                                }
                    }
                    if (slot < 0) throw new IOException("自动存档槽已满，请删除不再需要的自动存档后重试；其他对局不会被自动覆盖。");
                    if (automatic)
                    {
                        bool protectedSlot;
                        lock (queueLock) protectedSlot = incompatibleSlots.Contains(slot);
                        if (protectedSlot || (slots[slot].Exists && (slots[slot].Data == null || slots[slot].Data.ContentHash != contentHash)))
                            throw new IOException("本对局的自动存档损坏或不兼容，无法覆盖，请手动处理对应槽位。");
                    }
                    string json = null;
                    using (MatchSaveTraceScope.Begin(trace, "save.serialize.envelope"))
                    {
                        string checksum = HashUtf8Bounded(payload);
                        weak?.WriteIdentity(weakId, slot, checksum, ReadQueueStatistics());
                        if (CanStreamEnvelope())
                        {
                            streamingEnvelope = new FileEnvelope { Payload = payload, Checksum = checksum };
                            streamingSerializer = JsonSerializer.CreateDefault(JsonSettings);
                            PreflightEnvelope(streamingSerializer, streamingEnvelope);
                        }
                        else json = JsonConvert.SerializeObject(new FileEnvelope { Payload = payload, Checksum = checksum }, JsonSettings);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    ownershipCache[slot] = null;
                    InvalidateResumeCache(slot);
                    FileIdentity writtenFile;
                    using (MatchSaveTraceScope.Begin(trace, "save.write"))
                        writtenFile = streamingEnvelope == null
                            ? WriteAtomic(PathFor(slot), json, writtenOwnership != null && CacheContextAvailable)
                            : WriteAtomicEnvelope(PathFor(slot), streamingEnvelope, streamingSerializer, writtenOwnership != null && CacheContextAvailable);
                    if (trackStatistics) lock (queueLock)
                    {
                        sizeCommittedSequence = sequence;
                        // 原身份未携带实际Bytes时记未知，不为诊断加hash、序列化或文件读取。
                        sizeCommittedFileBytes = writtenFile.Bytes > 0 ? (long?)writtenFile.Bytes : null;
                        sizePhase = MatchSaveSizePhase.Committed;
                    }
                    // 成功原子发布后才预热；下次仍必须完整读取并匹配真实文件摘要。
                    if (writtenOwnership != null && writtenFile.Digest != null && CacheContextAvailable)
                        ownershipCache[slot] = new ValidatedOwnership(writtenOwnership, writtenFile);
                    if (automatic) { lock (queueLock) if (newGameId == gameId) newGameId = null; }
                    // 只在新档落盘后清理该对局的旧轮转副本；读取列表和失败写入均保留原档。
                    for (int i = first; i < end; i++)
                    {
                        if (i == slot || slots[i].Data?.GameId != gameId || slots[i].Data.ContentHash != contentHash) continue;
                        if (automatic) { lock (queueLock) if (incompatibleSlots.Contains(i)) continue; }
                        try { File.Delete(PathFor(i)); ownershipCache[i] = null; }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // 新档已成功落盘；旧副本清理失败不撤销保存，也不阻止退出。
                            // 保留副本供下一次保存重试，并继续清理其他副本。
                        }
                    }
                    if (trackStatistics) lock (queueLock)
                    {
                        completed++; sizeFinishedSequence = sequence;
                        sizePhase = sizeFinishedPhase = MatchSaveSizePhase.Completed;
                    }
                    weak?.QueueFinished(weakId, true, false, ReadQueueStatistics());
                    MatchSaveTraceScope.Point(trace, "save.queue.complete");
                    }
                    catch (OperationCanceledException)
                    {
                        if (trackStatistics) lock (queueLock)
                        {
                            cancelled++; sizeFinishedSequence = sequence;
                            sizePhase = sizeFinishedPhase = MatchSaveSizePhase.Cancelled;
                        }
                        weak?.QueueFinished(weakId, false, true, ReadQueueStatistics());
                        MatchSaveTraceScope.Point(trace, "save.queue.cancel"); throw;
                    }
                    catch
                    {
                        if (trackStatistics) lock (queueLock)
                        {
                            failed++; sizeFinishedSequence = sequence;
                            sizePhase = sizeFinishedPhase = MatchSaveSizePhase.Failed;
                        }
                        weak?.QueueFinished(weakId, false, false, ReadQueueStatistics());
                        MatchSaveTraceScope.Point(trace, "save.queue.fail"); throw;
                    }
                    finally
                    {
                        preparePayload = null; previous = null; trace = null; weak = null;
                        streamingEnvelope = null; streamingSerializer = null;
                        if (trackStatistics) lock (queueLock) { if (entered) active--; else pending--; outstanding--; }
                    }
                });
                return tail;
            }
        }
        public Task FlushAsync() { lock (queueLock) return tail; }
        public Task DeleteAsync(int slot)
        {
            string path = PathFor(slot);
            lock (queueLock)
            {
                var previous = tail;
                tail = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    // 仅处理玩家确认的这个槽位，不枚举或递归删除目录。
                    ownershipCache[slot] = null;
                    InvalidateResumeCache(slot);
                    File.Delete(path);
                    File.Delete(path + ".bak"); // 删除槽位时一并清理旧版本留下的文件。
                    File.Delete(path + ".tmp");
                    lock (queueLock) incompatibleSlots.Remove(slot);
                });
                return tail;
            }
        }
        private static FileIdentity WriteAtomicEnvelope(string path, FileEnvelope envelope, JsonSerializer serializer, bool identify)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            FileIdentity identity;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var text = new BoundedUtf8Writer(stream, identify))
                using (var writer = new JsonTextWriter(text) { Formatting = serializer.Formatting,
                    CloseOutput = false, AutoCompleteOnClose = false })
                {
                    // 内置、无回调的同一有效serializer/同一封装对象第二遍发出真实字节。
                    // 若写失败，Dispose不补写结束标记或重试编码器；原档仍不变。
                    serializer.Serialize(writer, envelope);
                    writer.Flush();
                    identity = text.Complete();
                }
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null, true);
            else File.Move(temp, path);
            return identity;
        }
        private static FileIdentity WriteAtomic(string path, string json, bool identify)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            FileIdentity identity = default;
            if (identify)
            {
                try { using (var sha = SHA256.Create()) identity = new FileIdentity(Convert.ToBase64String(sha.ComputeHash(bytes)), bytes.Length); }
                catch { } // 可选身份无法建立时只取消预热，仍执行原写盘。
            }
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            // 临时文件和目标位于同一卷；替换失败保留原档，不使用删除再移动的退化路径。
            if (File.Exists(path)) File.Replace(temp, path, null, true);
            else File.Move(temp, path);
            return identity;
        }
    }
}
