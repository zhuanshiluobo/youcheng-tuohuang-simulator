using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using YC.Application.Sessions;

namespace YC.Infrastructure.Persistence
{
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
            MaxDepth = 128
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
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        public const int ManualCount = 3;
        public const int SlotCount = 6;
        private readonly string directory;
        private readonly object queueLock = new object();
        private Task tail = Task.CompletedTask;
        private readonly HashSet<int> incompatibleSlots = new HashSet<int>();
        private string restoredGameId;
        private int restoredAutomaticSlot = -1;
        private string newGameId;
        public void BeginMatch(string gameId, bool isNewMatch)
        {
            lock (queueLock) newGameId = isNewMatch ? gameId : null;
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
        public MatchSaveStore(string directory) { this.directory = directory; }
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
        public MatchSaveData Read(int slot)
        {
            string text;
            // 槽位列表读取不阻挡后台原子替换；已打开句柄仍读取同一份完整文件。
            using (var stream = new FileStream(PathFor(slot), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            // 在反序列化前检查包装版本；禁用类型名称加载。
            if (!text.Contains("\"Format\"") || !text.Contains("\"Version\""))
                throw new InvalidDataException("不是完整的对局存档文件。");
            var envelope = JsonConvert.DeserializeObject<FileEnvelope>(text, JsonSettings);
            if (envelope == null || envelope.Format != "YC.MatchSave" || envelope.Version != MatchSaveData.CurrentVersion)
                throw new InvalidDataException("不支持此对局存档文件版本。");
            if (string.IsNullOrEmpty(envelope.Payload) || envelope.Checksum != Hash(envelope.Payload))
                throw new InvalidDataException("存档完整性检查失败，无法读取。");
            var data = JsonConvert.DeserializeObject<MatchSaveData>(envelope.Payload, JsonSettings);
            if (data == null) throw new InvalidDataException("对局存档内容为空。");
            data.Validate();
            return data;
        }
        private MatchSaveSlot ReadSlot(int index)
        {
            var row = new MatchSaveSlot { Index = index, Exists = File.Exists(PathFor(index)) };
            if (row.Exists)
            {
                try { row.Data = Read(index); }
                catch (Exception ex) { row.Error = ex.Message; }
            }
            return row;
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
        public void EnsureResumeCapacity(string gameId)
        {
            foreach (var slot in List())
                if (slot.Index >= ManualCount && (!slot.Exists || slot.Data?.GameId == gameId)) return;
            throw new IOException("本对局没有可更新的自动存档，自动槽位已满。请先删除一个不再需要的自动存档，再读取此对局。");
        }
        public Task SaveAsync(MatchSaveData data, int manualSlot = -1)
        {
            if (manualSlot < -1 || manualSlot >= ManualCount) throw new ArgumentOutOfRangeException(nameof(manualSlot));
            data.Validate();
            // 只在调用线程冻结可变 DTO，校验和及外层 JSON 包装交给后台。
            string payload = JsonConvert.SerializeObject(data, JsonSettings);
            // 入队后调用方还会继续修改 DTO；后台只使用本次捕获的标识与文本。
            string gameId = data.GameId, contentHash = data.ContentHash;
            lock (queueLock)
            {
                int preferredSlot = restoredGameId == gameId ? restoredAutomaticSlot : -1;
                var previous = tail;
                tail = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { /* 后续保存仍然可以重试。 */ }
                    // 前一笔写入完成后再查归属，连续提交也只能创建一份同类存档。
                    bool automatic = manualSlot < 0;
                    int first = automatic ? ManualCount : 0;
                    int end = automatic ? SlotCount : ManualCount;
                    var slots = new MatchSaveSlot[SlotCount];
                    for (int i = first; i < end; i++) slots[i] = ReadSlot(i);
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
                    string json = JsonConvert.SerializeObject(new FileEnvelope { Payload = payload, Checksum = Hash(payload) }, JsonSettings);
                    WriteAtomic(PathFor(slot), json);
                    if (automatic) { lock (queueLock) if (newGameId == gameId) newGameId = null; }
                    // 只在新档落盘后清理该对局的旧轮转副本；读取列表和失败写入均保留原档。
                    for (int i = first; i < end; i++)
                    {
                        if (i == slot || slots[i].Data?.GameId != gameId || slots[i].Data.ContentHash != contentHash) continue;
                        if (automatic) { lock (queueLock) if (incompatibleSlots.Contains(i)) continue; }
                        try { File.Delete(PathFor(i)); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // 新档已成功落盘；旧副本清理失败不撤销保存，也不阻止退出。
                            // 保留副本供下一次保存重试，并继续清理其他副本。
                        }
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
                    File.Delete(path);
                    File.Delete(path + ".bak"); // 删除槽位时一并清理旧版本留下的文件。
                    File.Delete(path + ".tmp");
                    lock (queueLock) incompatibleSlots.Remove(slot);
                });
                return tail;
            }
        }
        private static void WriteAtomic(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            // 临时文件和目标位于同一卷；替换失败保留原档，不使用删除再移动的退化路径。
            if (File.Exists(path)) File.Replace(temp, path, null, true);
            else File.Move(temp, path);
        }
    }
}
