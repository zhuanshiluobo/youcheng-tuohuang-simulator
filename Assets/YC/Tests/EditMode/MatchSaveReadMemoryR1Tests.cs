using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Rules;

namespace YC.Tests.EditMode
{
    // 使用项目内独立夹具验证存档读取，不访问用户存档，也不替代正常界面测试。
    public sealed class MatchSaveReadMemoryR1Tests
    {
        private static Type StoreType => Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
        private static object Store(string root) => Activator.CreateInstance(StoreType, root);
        private static MatchSaveData Read(object store, int slot) =>
            (MatchSaveData)StoreType.GetMethod("Read").Invoke(store, new object[] { slot });
        private static string Digest(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(bytes));
        }
        private static string FileDigest(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        [Test]
        public void HashMatchesUtf8AcrossBlockAndSurrogateBoundaries()
        {
            var hash = StoreType.GetMethod("Hash", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(hash, Is.Not.Null);
            foreach (string value in new[] { "", "中文与换行\n", new string('a', 4095) + "😀后续",
                new string('x', 4095) + "\ud800后续", new string('z', 4096) + "\udc00结束", new string('中', 32780) })
                Assert.That((string)hash.Invoke(null, new object[] { value }), Is.EqualTo(Digest(Encoding.UTF8.GetBytes(value))));
        }

        [Test]
        public void PrivateCheckpointHashKeepsUtf8IdentityWithoutFullArchiveByteArray()
        {
            var method = typeof(PrivateHostCheckpoint).GetMethod("Hash", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            foreach (string value in new[] { "", "中文与换行\n", new string('a', 4095) + "😀后续",
                new string('x', 4095) + "\ud800后续", new string('z', 4096) + "\udc00结束", new string('中', 32780) })
            {
                using var sha = SHA256.Create();
                string expected = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
                Assert.That((string)method.Invoke(null, new object[] { value }), Is.EqualTo(expected));
            }
            string archive = new string('中', 2_000_000);
            method.Invoke(null, new object[] { "预热" });
            long before = GC.GetAllocatedBytesForCurrentThread();
            method.Invoke(null, new object[] { archive });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.LessThan(archive.Length), "不应再分配整份归档大小的 UTF-8 数组。");
        }

        [Test]
        public void OptionalPrivateSaveValidationComparesProjectedAndFullTreeAllocations()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-ycPrivateMemorySaveFile");
            if (index < 0) Assert.Ignore("仅显式提供隔离联机存档时运行内存对照。");
            string file = Path.GetFullPath(args[index + 1]);
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            Assert.That(file.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), Is.True);
            string beforeHash = FileDigest(file);
            var envelope = JObject.Parse(File.ReadAllText(file));
            var data = JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            envelope = null;
            Assert.That(data.Mode, Is.EqualTo(LaunchMode.Host));
            Assert.That(data.PrivateCheckpointJson, Is.Not.Empty);
            var original = JsonConvert.DefaultSettings;
            object Measure(bool projected)
            {
                JsonConvert.DefaultSettings = projected ? null : () => new JsonSerializerSettings();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long heap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                data.Validate();
                clock.Stop();
                return new { Projected = projected, Seconds = clock.Elapsed.TotalSeconds,
                    AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                    HeapBefore = heap, HeapAfter = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() };
            }
            try
            {
                var full = Measure(false); var compact = Measure(true);
                TestContext.WriteLine("私密房主隔离存档校验对照：" + JsonConvert.SerializeObject(new { full, compact,
                    Scope = "同档同进程先完整树后投影；受控GC，累计分配不等于工作集或玩家实际峰值" }));
                Assert.That(FileDigest(file), Is.EqualTo(beforeHash));
            }
            finally { JsonConvert.DefaultSettings = original; }
        }

        [Test]
        public void OptionalPrivateHostFixtureReadPreservesCheckpointAndSource()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-ycPrivateMemorySaveFile");
            if (index < 0) Assert.Ignore("仅显式提供项目内隔离联机存档时执行读取对照。");
            string file = Path.GetFullPath(args[index + 1]);
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            Assert.That(file.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(Path.GetFileName(file), Is.EqualTo("auto-1.json"));
            string before = FileDigest(file);
            var store = Store(Path.GetDirectoryName(file));
            var current = Read(store, 3);
            Assert.That(current.Mode, Is.EqualTo(LaunchMode.Host));
            Assert.That(current.PrivateCheckpointJson, Is.Not.Empty);
            var previous = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => new JsonSerializerSettings();
                var baseline = Read(store, 3);
                Assert.That(current.GameId, Is.EqualTo(baseline.GameId));
                Assert.That(current.ContentHash, Is.EqualTo(baseline.ContentHash));
                Assert.That(current.ArchiveJson, Is.EqualTo(baseline.ArchiveJson));
                Assert.That(current.ContinuationJson, Is.EqualTo(baseline.ContinuationJson));
                Assert.That(current.PrivateCheckpointJson, Is.EqualTo(baseline.PrivateCheckpointJson));
            }
            finally { JsonConvert.DefaultSettings = previous; }
            Assert.That(FileDigest(file), Is.EqualTo(before), "原隔离存档不可改变。");
        }

        [Test]
        public void ReadKeepsIntegrityChecksAndRejectsDamagedEnvelope()
        {
            var save = CreateCurrentPrivateHostSave();
            var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "MemoryReadFixture-r1", "integrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = Store(root);
            ((Task)StoreType.GetMethod("SaveAsync").Invoke(store, new object[] { save, 0 })).GetAwaiter().GetResult();
            string path = Path.Combine(root, "manual-1.json");
            string original = File.ReadAllText(path);
            Assert.That(Read(store, 0).GameId, Is.EqualTo(save.GameId));
            File.WriteAllText(path, original + " \r\n");
            Assert.That(Read(store, 0).GameId, Is.EqualTo(save.GameId));
            File.WriteAllText(path, "/*存档注释*/" + original + " /*尾部注释*/");
            Assert.That(Read(store, 0).GameId, Is.EqualTo(save.GameId));
            File.WriteAllText(path, original + "{}");
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            var damaged = JObject.Parse(original); damaged["Checksum"] = "not-a-valid-checksum";
            File.WriteAllText(path, damaged.ToString(Formatting.None));
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            damaged = JObject.Parse(original); damaged.Remove("Format");
            File.WriteAllText(path, damaged.ToString(Formatting.None));
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            damaged = JObject.Parse(original); damaged.Remove("Version");
            File.WriteAllText(path, damaged.ToString(Formatting.None));
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            damaged = JObject.Parse(original); damaged["Unrecognized"] = true;
            File.WriteAllText(path, damaged.ToString(Formatting.None));
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            File.WriteAllText(path, original.Substring(0, original.Length - 1));
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            File.WriteAllText(path, original);
            Assert.That(Read(store, 0).GameId, Is.EqualTo(save.GameId));

            // 自定义 Json.NET 默认设置仍使用原读取路径，并恢复进程全局设置。
            var previousSettings = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => new JsonSerializerSettings();
                Assert.That(Read(store, 0).GameId, Is.EqualTo(save.GameId));
                File.WriteAllText(path, original + "{}");
                Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            }
            finally
            {
                JsonConvert.DefaultSettings = previousSettings;
                File.WriteAllText(path, original);
            }

            // 新建存储实例令所有权缓存为空，下一次保存必须重新读取、验证同一存档。
            var coldStore = Store(root);
            ((Task)StoreType.GetMethod("SaveAsync").Invoke(coldStore, new object[] { save, 0 })).GetAwaiter().GetResult();
            Assert.That(Read(coldStore, 0).GameId, Is.EqualTo(save.GameId));
        }

        [Test]
        public void CanonicalStreamingReadMatchesFullParserAndFallsBackForOtherFormats()
        {
            var save = CreateCurrentPrivateHostSave();
            save.Seats[0].PlayerName = new string('中', 4095) + "😀\"\\\t玩家 {\"ArchiveJson\":\"假\"}";
            var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "MemoryReadFixture-r1", "canonical-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = Store(root);
            ((Task)StoreType.GetMethod("SaveAsync").Invoke(store, new object[] { save, 0 })).GetAwaiter().GetResult();
            string path = Path.Combine(root, "manual-1.json");
            string original = File.ReadAllText(path);
            var fast = StoreType.GetMethod("TryReadCanonicalEnvelope", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(fast, Is.Not.Null);
            bool AcceptsFast()
            {
                using var stream = File.OpenRead(path);
                return (bool)fast.Invoke(null, new object[] { stream, null, null, null });
            }
            string ReadWithSettings(bool baseline)
            {
                var previous = JsonConvert.DefaultSettings;
                try
                {
                    JsonConvert.DefaultSettings = baseline ? () => new JsonSerializerSettings() : null;
                    return JsonConvert.SerializeObject(Read(store, 0));
                }
                finally { JsonConvert.DefaultSettings = previous; }
            }
            void AssertSameRead(bool expectFast)
            {
                Assert.That(AcceptsFast(), Is.EqualTo(expectFast));
                Assert.That(ReadWithSettings(false), Is.EqualTo(ReadWithSettings(true)));
            }

            AssertSameRead(true);
            File.WriteAllText(path, original + " \r\n");
            AssertSameRead(true);
            File.WriteAllText(path, original.Replace("中", "\\u4e2d"));
            AssertSameRead(true);
            File.WriteAllText(path, original.Replace("😀", "\\ud83d\\ude00"));
            AssertSameRead(false); // 代理项转义交由 Json.NET 的原有规则处理。
            File.WriteAllText(path, "/*前置注释*/" + original + " /*尾部注释*/");
            AssertSameRead(false);
            var commented = JObject.Parse(original);
            string commentedPayload = (string)commented["Payload"];
            commentedPayload = "{/*正文注释*/" + commentedPayload.Substring(1);
            commented["Payload"] = commentedPayload;
            commented["Checksum"] = Digest(Encoding.UTF8.GetBytes(commentedPayload));
            File.WriteAllText(path, commented.ToString(Formatting.None));
            AssertSameRead(false);
            var aliased = JObject.Parse(original);
            string aliasedPayload = (string)aliased["Payload"];
            aliasedPayload = aliasedPayload.Substring(0, aliasedPayload.Length - 1) + ",\"archivejson\":\"invalid\"}";
            aliased["Payload"] = aliasedPayload;
            aliased["Checksum"] = Digest(Encoding.UTF8.GetBytes(aliasedPayload));
            File.WriteAllText(path, aliased.ToString(Formatting.None));
            Assert.That(AcceptsFast(), Is.False, "大小写别名必须交给原解析器按原顺序处理。");
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            aliasedPayload = (string)JObject.Parse(original)["Payload"];
            aliasedPayload = aliasedPayload.Substring(0, aliasedPayload.Length - 1) + ",archivejson:'invalid'}";
            aliased["Payload"] = aliasedPayload;
            aliased["Checksum"] = Digest(Encoding.UTF8.GetBytes(aliasedPayload));
            File.WriteAllText(path, aliased.ToString(Formatting.None));
            Assert.That(AcceptsFast(), Is.False, "未引用的属性和单引号必须交给原解析器处理。");
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));

            // 未通过校验的正文和尾部额外内容仍被拒绝，不得通过快路径绕过完整性检查。
            var damaged = JObject.Parse(original); damaged["Checksum"] = "invalid";
            File.WriteAllText(path, damaged.ToString(Formatting.None));
            Assert.That(AcceptsFast(), Is.False);
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
            File.WriteAllText(path, original + "{}");
            Assert.That(AcceptsFast(), Is.False);
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
        }

        [Test]
        public void PreviewListKeepsDisplayAndValidationWithoutRetainingArchiveOrChangingRead()
        {
            var save = CreateCurrentPrivateHostSave();
            var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "MemoryReadFixture-r1", "preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = Store(root);
            ((Task)StoreType.GetMethod("SaveAsync").Invoke(store, new object[] { save, 0 })).GetAwaiter().GetResult();
            object FirstPreview() => ((System.Collections.IEnumerable)StoreType.GetMethod("ListPreviews").Invoke(store, null)).Cast<object>().First();
            var slot = FirstPreview();
            var preview = (MatchSaveData)slot.GetType().GetField("Data").GetValue(slot);
            Assert.That(preview, Is.Not.Null);
            Assert.That(preview.GameId, Is.EqualTo(save.GameId));
            Assert.That(preview.ContentHash, Is.EqualTo(save.ContentHash));
            Assert.That(preview.ArchiveJson, Is.Null);
            Assert.That(preview.ContinuationJson, Is.Null);
            Assert.That(preview.PrivateCheckpointJson, Is.Null);
            Assert.That(JToken.DeepEquals(preview.ReadArchiveMetadata(), save.ReadArchiveMetadata()), Is.True);
            preview.ReadArchiveMetadata()["CurrentPlayerId"] = -123;
            Assert.That(JToken.DeepEquals(preview.ReadArchiveMetadata(), save.ReadArchiveMetadata()), Is.True,
                "调用方更改展示副本不得污染后续读取。");
            preview.Seats[0].PlayerName = "修改预览";
            Assert.That(preview.Seats[0].PlayerName, Is.Not.EqualTo(save.Seats[0].PlayerName));
            var second = FirstPreview();
            var secondPreview = (MatchSaveData)second.GetType().GetField("Data").GetValue(second);
            Assert.That(secondPreview.Seats[0].PlayerName, Is.EqualTo(save.Seats[0].PlayerName),
                "缓存的展示数据不能被调用方修改。");
            Assert.Throws<InvalidOperationException>(() => preview.Validate(), "展示副本不能作为完整存档恢复。");
            Assert.That(Read(store, 0).ArchiveJson, Is.EqualTo(save.ArchiveJson));
            var file = Path.Combine(root, "manual-1.json");
            var bytes = File.ReadAllBytes(file);
            var marker = Encoding.UTF8.GetBytes("\"Checksum\":\"");
            int at = -1;
            for (int i = 0; i <= bytes.Length - marker.Length; i++)
            {
                bool matches = true;
                for (int j = 0; j < marker.Length; j++) if (bytes[i + j] != marker[j]) { matches = false; break; }
                if (matches) { at = i + marker.Length; break; }
            }
            Assert.That(at, Is.GreaterThanOrEqualTo(0));
            bytes[at] = bytes[at] == (byte)'A' ? (byte)'B' : (byte)'A';
            File.WriteAllBytes(file, bytes); // 长度不变，仍需发现内容变化。
            slot = FirstPreview();
            Assert.That(slot.GetType().GetField("Data").GetValue(slot), Is.Null);
            Assert.That((string)slot.GetType().GetField("Error").GetValue(slot), Is.Not.Empty);
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
        }

        [Test]
        public void OptionalMiddleGameFixtureReadMeasuresAllocationWithoutChangingSource()
        {
            const string option = "--yc-memory-read-fixture=";
            string argument = Array.Find(Environment.GetCommandLineArgs(), a => a.StartsWith(option, StringComparison.Ordinal));
            if (argument == null) Assert.Ignore("仅显式提供项目内隔离存档目录时执行分配测量。");
            string root = Path.GetFullPath(argument.Substring(option.Length));
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            Assert.That(root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), Is.True);
            string path = Path.Combine(root, "auto-1.json");
            string sourceHash = FileDigest(path);
            var store = Store(root);
            var warm = Read(store, 3);
            Assert.That(warm.GameId, Is.EqualTo("fbc71c69c76a444f86630e5fcd045694"));
            warm = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long probeBefore = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[8192]; GC.KeepAlive(probe);
            bool counterAvailable = GC.GetAllocatedBytesForCurrentThread() - probeBefore >= probe.Length;
            long managedBefore = GC.GetTotalMemory(false);
            long heapBefore = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
            long start = GC.GetAllocatedBytesForCurrentThread();
            var current = Read(store, 3);
            long? allocated = counterAvailable ? GC.GetAllocatedBytesForCurrentThread() - start : (long?)null;
            long managedAfter = GC.GetTotalMemory(false);
            long heapAfter = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
            Assert.That(current.GameId, Is.EqualTo("fbc71c69c76a444f86630e5fcd045694"));
            Assert.That(current.Round, Is.EqualTo(5));
            Assert.That(FileDigest(path), Is.EqualTo(sourceHash), "隔离原档不得改变");
            TestContext.WriteLine("隔离第5回合单次读取: " + JsonConvert.SerializeObject(new { AllocatedBytes = allocated,
                CounterAvailable = counterAvailable, ManagedBefore = managedBefore, ManagedAfter = managedAfter,
                MonoReservedBefore = heapBefore, MonoReservedAfter = heapAfter,
                Limitation = "读取前受控回收，读取内可能发生自动回收；托管前后差值不是累计分配" }));
        }

        [Test]
        public void ResumeCapacityChecksOnlyNecessaryAutomaticSlotsAndStillRejectsFullOrBrokenSlots()
        {
            var save = CreateCurrentPrivateHostSave();
            var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "MemoryReadFixture-r1", "capacity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = Store(root);
            ((Task)StoreType.GetMethod("SaveAsync").Invoke(store, new object[] { save, 0 })).GetAwaiter().GetResult();
            string manual = Path.Combine(root, "manual-1.json");
            string first = Path.Combine(root, "auto-1.json");
            string second = Path.Combine(root, "auto-2.json");
            string third = Path.Combine(root, "auto-3.json");
            File.Copy(manual, second); File.Copy(manual, third);
            var ensure = StoreType.GetMethod("EnsureResumeCapacity");
            int defaultSettingsCalls = 0;
            var previousSettings = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => { defaultSettingsCalls++; return new JsonSerializerSettings(); };
                ensure.Invoke(store, new object[] { "another-game" });
                Assert.That(defaultSettingsCalls, Is.Zero, "首个自动槽为空时，无需读取手动档或后续自动档。");
            }
            finally { JsonConvert.DefaultSettings = previousSettings; }
            File.Copy(manual, first);
            StoreType.GetMethod("ListPreviews").Invoke(store, null); // 已验证的预览只在文件摘要未变化时可复用。
            ensure.Invoke(store, new object[] { save.GameId });
            var full = Assert.Throws<TargetInvocationException>(() => ensure.Invoke(store, new object[] { "another-game" }));
            Assert.That(full.InnerException, Is.TypeOf<IOException>());
            File.WriteAllText(first, "{broken");
            ensure.Invoke(store, new object[] { save.GameId });
            full = Assert.Throws<TargetInvocationException>(() => ensure.Invoke(store, new object[] { "another-game" }));
            Assert.That(full.InnerException, Is.TypeOf<IOException>());
        }

        [Test]
        public void ResumeObservationNeverTrustsChangedFileOrAnotherStore()
        {
            var save = CreateCurrentPrivateHostSave();
            var root = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "MemoryReadFixture-r1", "observation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = Store(root);
            ((Task)StoreType.GetMethod("SaveAsync").Invoke(store, new object[] { save, 0 })).GetAwaiter().GetResult();
            string manual = Path.Combine(root, "manual-1.json");
            string first = Path.Combine(root, "auto-1.json");
            File.Copy(manual, first);
            var args = new object[] { 3, null, null };
            var read = (MatchSaveData)StoreType.GetMethod("ReadForResume").Invoke(store, args);
            Assert.That(read.GameId, Is.EqualTo(save.GameId));
            Assert.That(args[2], Is.Not.Null);
            var ensure = StoreType.GetMethod("EnsureResumeCapacityForRead");
            ensure.Invoke(store, new[] { save.GameId, args[2] });
            read.Seats[0].PlayerName = "外部修改";
            read.ArchiveJson = "外部修改";
            var repeatedArgs = new object[] { 3, null, null };
            var repeated = (MatchSaveData)StoreType.GetMethod("ReadForResume").Invoke(store, repeatedArgs);
            Assert.That(repeated.Seats[0].PlayerName, Is.EqualTo(save.Seats[0].PlayerName));
            Assert.That(repeated.ArchiveJson, Is.EqualTo(save.ArchiveJson), "已验证的正文不能被调用方覆盖。");
            Assert.That(repeatedArgs[2], Is.Not.Null);

            File.WriteAllText(Path.Combine(root, "auto-2.json"), "{broken");
            File.WriteAllText(Path.Combine(root, "auto-3.json"), "{broken");
            var bytes = File.ReadAllBytes(first);
            bytes[bytes.Length - 3] = bytes[bytes.Length - 3] == (byte)'A' ? (byte)'B' : (byte)'A';
            File.WriteAllBytes(first, bytes);
            var rejected = Assert.Throws<TargetInvocationException>(() => ensure.Invoke(store, new[] { save.GameId, args[2] }));
            Assert.That(rejected.InnerException, Is.TypeOf<IOException>(), "已变化的槽位不能借用原摘要。");
            Assert.Throws<TargetInvocationException>(() => StoreType.GetMethod("ReadForResume").Invoke(store, new object[] { 3, null, null }),
                "同长度篡改后不能返回缓存正文。");
            Assert.That(StoreType.GetField("resumeCache", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(store), Is.Null,
                "文件已变化且重读失败后不能继续持有旧正文。");

            var otherRoot = Path.Combine(root, "other");
            Directory.CreateDirectory(otherRoot);
            for (int i = 1; i <= 3; i++) File.WriteAllText(Path.Combine(otherRoot, "auto-" + i + ".json"), "{broken");
            var otherStore = Store(otherRoot);
            rejected = Assert.Throws<TargetInvocationException>(() => ensure.Invoke(otherStore, new[] { save.GameId, args[2] }));
            Assert.That(rejected.InnerException, Is.TypeOf<IOException>(), "摘要不能跨存储目录使用。");
        }

        // 使用当前私密房主合同构造隔离存档，避免旧模拟检查点与私密随机状态混搭。
        private static MatchSaveData CreateCurrentPrivateHostSave()
        {
            var session = DeterministicSimulationCoordinatorTests.CreateFormalSessions(out _, true)[0];
            var state = session.State;
            var capture = session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture());
            var header = new MatchSaveData { Mode = LaunchMode.Host, LocalPlayerId = 3, HostSteamId = 103,
                GameId = state.GameId, ContentHash = state.ContentPackHash,
                Seats = state.Players.Select(player => new MatchSaveSeat { PlayerId = player.PlayerId,
                    PlayerName = player.Name, Color = player.Color, SteamId = (ulong)(100 + player.PlayerId),
                    OperatorId = "operator-" + player.PlayerId }).ToList() };
            return MatchSaveData.CreateFromPrivateCaptureAsync(header, capture, 1, 1).GetAwaiter().GetResult();
        }
    }
}
