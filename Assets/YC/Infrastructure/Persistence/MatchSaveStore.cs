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
        public bool HasBackup;
        public MatchSaveData Data;
        public string Error;
    }

    /// <summary>调用方在主线程序列化；后台队列仅处理不可变文本和磁盘 IO。</summary>
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
        public void ProtectAutomaticSlot(int slot)
        {
            if (slot < ManualCount || slot >= SlotCount) return;
            lock (queueLock) incompatibleSlots.Add(slot);
        }
        public MatchSaveStore(string directory) { this.directory = directory; }
        private string PathFor(int slot)
        {
            if (slot < 0 || slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
            return Path.Combine(directory, (slot < ManualCount ? "manual-" : "auto-") + (slot % 3 + 1) + ".json");
        }
        public MatchSaveData Read(int slot, bool backup = false)
        {
            string text;
            // 槽位列表读取不阻挡后台原子替换；已打开句柄仍读取同一份完整文件。
            using (var stream = new FileStream(PathFor(slot) + (backup ? ".bak" : ""), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();
            // 在反序列化前检查包装版本；禁用类型名称加载。
            if (!text.Contains("\"Format\"") || !text.Contains("\"Version\""))
                throw new InvalidDataException("不是完整的对局存档文件。");
            var envelope = JsonConvert.DeserializeObject<FileEnvelope>(text, JsonSettings);
            if (envelope == null || envelope.Format != "YC.MatchSave" || envelope.Version != MatchSaveData.CurrentVersion)
                throw new InvalidDataException("不支持此对局存档文件版本。");
            if (string.IsNullOrEmpty(envelope.Payload) || envelope.Checksum != Hash(envelope.Payload))
                throw new InvalidDataException("存档完整性检查失败，可尝试读取备份。");
            var data = JsonConvert.DeserializeObject<MatchSaveData>(envelope.Payload, JsonSettings);
            if (data == null) throw new InvalidDataException("对局存档内容为空。");
            data.Validate();
            return data;
        }
        public List<MatchSaveSlot> List()
        {
            var result = new List<MatchSaveSlot>();
            for (int i = 0; i < SlotCount; i++)
            {
                var row = new MatchSaveSlot { Index = i, Exists = File.Exists(PathFor(i)), HasBackup = File.Exists(PathFor(i) + ".bak") };
                if (row.Exists)
                {
                    try { row.Data = Read(i); }
                    catch (Exception ex) { row.Error = ex.Message; }
                }
                result.Add(row);
            }
            return result;
        }
        public Task SaveAsync(MatchSaveData data, int manualSlot = -1)
        {
            if (manualSlot < -1 || manualSlot >= ManualCount) throw new ArgumentOutOfRangeException(nameof(manualSlot));
            data.Validate();
            string payload = JsonConvert.SerializeObject(data, JsonSettings);
            string json = JsonConvert.SerializeObject(new FileEnvelope { Payload = payload, Checksum = Hash(payload) }, JsonSettings);
            var protectedSlots = new HashSet<int>();
            if (manualSlot < 0) foreach (var slot in List())
                if (slot.Exists && (slot.Data == null || slot.Data.ContentHash != data.ContentHash)) protectedSlots.Add(slot.Index);
            lock (queueLock)
            {
                protectedSlots.UnionWith(incompatibleSlots);
                var previous = tail;
                tail = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { /* 后续保存仍然可以重试。 */ }
                    int slot = manualSlot;
                    if (slot < 0)
                    {
                        slot = -1;
                        for (int i = ManualCount; i < SlotCount; i++)
                        {
                            if (protectedSlots.Contains(i)) continue;
                            if (!File.Exists(PathFor(i))) { slot = i; break; }
                            if (slot < 0 || File.GetLastWriteTimeUtc(PathFor(i)) < File.GetLastWriteTimeUtc(PathFor(slot))) slot = i;
                        }
                    }
                    if (slot < 0) throw new IOException("自动槽位均为损坏或不兼容存档，请先备份文件再手动处理。");
                    WriteAtomic(PathFor(slot), json);
                });
                return tail;
            }
        }
        public Task FlushAsync() { lock (queueLock) return tail; }
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
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
            else File.Move(temp, path);
        }
    }
}
