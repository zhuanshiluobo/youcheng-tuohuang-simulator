using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;

namespace YC.Tests.EditMode
{
    // 隔离解析回归及可选原档只读诊断；不注入正式对局，不计正常界面验收。
    public sealed class MatchSaveMetadataMemoryTests
    {
        private static MatchSaveData Example() => new MatchSaveData { ArchiveJson = @"{
            'Snapshot':{'SchemaVersion':5,'State':{'GameId':'metadata-test','EventDeckSeed':42,
              'RuleConfiguration':{'MapId':'test-map','PlayerCount':2},
              'RandomRuntime':{'AlgorithmVersion':'test-random'},'LaunchPlayerOrder':[2,1],
              'CurrentPlayerId':2,'ActionRound':3,'Players':[
                {'PlayerId':1,'Color':0,'Name':'本机','Resources':{'Iron':12}},
                {'PlayerId':2,'Color':1,'Name':'行动者','Resources':{'Iron':99}}],
              'EffectRuntime':{'EffectNodes':[{'largeHistory':['a','b','c']}]}}},
            'Journal':{'SchemaVersion':5,'Entries':[{'StateAfter':{'unrelated':123}}]}}" };

        [Test]
        public void MetadataMatchesFullArchiveAndKeepsDisplaySeparateFromHistory()
        {
            var data = Example();
            var full = data.ReadArchiveState();
            var metadata = data.ReadArchiveMetadata();
            foreach (string field in new[] { "GameId", "EventDeckSeed", "RuleConfiguration", "RandomRuntime",
                "LaunchPlayerOrder", "CurrentPlayerId", "ActionRound", "Players" })
                Assert.That(JToken.DeepEquals(full[field], metadata[field]), Is.True, field);
            Assert.That(metadata["EffectRuntime"], Is.Null);
            Assert.That(data.ReadArchiveState()["EffectRuntime"], Is.Not.Null, "完整读取合同保持");
            var launch = data.ReadLaunchMetadata();
            Assert.That(launch.EventDeckSeed, Is.EqualTo(data.EventDeckSeed));
            Assert.That(launch.Configuration.MapId, Is.EqualTo(data.Configuration.MapId));
            Assert.That(launch.LaunchPlayerOrder, Is.EqualTo(data.LaunchPlayerOrder));
            Assert.That(JsonConvert.SerializeObject(launch.RandomState), Is.EqualTo(JsonConvert.SerializeObject(data.RandomState)));
            launch.Configuration.MapId = "changed";
            launch.LaunchPlayerOrder.Clear();
            metadata["EventDeckSeed"] = 99;
            Assert.That(data.Configuration.MapId, Is.EqualTo("test-map"));
            Assert.That(data.LaunchPlayerOrder, Is.EqualTo(new[] { 2, 1 }));
            Assert.That(data.EventDeckSeed, Is.EqualTo(42));
            data.ArchiveJson = data.ArchiveJson.Replace("42", "43");
            Assert.That(data.EventDeckSeed, Is.EqualTo(43), "替换归档不得使用旧元数据");
        }

        [TestCase("{'x':1,'x':2}")]
        [TestCase("[{'x':1,'x':2}]")]
        [TestCase("{'x':[1,2}")]
        public void MetadataStillRejectsInvalidDiscardedHistory(string history)
        {
            var data = Example();
            data.ArchiveJson = data.ArchiveJson.Replace("{'largeHistory':['a','b','c']}", history);
            Assert.Throws<JsonReaderException>(() => data.ReadArchiveState());
            Assert.Throws<JsonReaderException>(() => data.ReadArchiveMetadata());
            Assert.Throws<JsonReaderException>(() => data.ReadLaunchMetadata());
        }

        [Test]
        public void OptionalOriginalSaveMemoryComparison()
        {
            var args = Environment.GetCommandLineArgs();
            int fileIndex = Array.IndexOf(args, "-ycMemorySaveFile");
            int reportIndex = Array.IndexOf(args, "-ycMemoryReport");
            if (fileIndex < 0 || reportIndex < 0) Assert.Ignore("仅显式提供原档及报告路径时运行只读诊断。");
            string file = args[fileIndex + 1], report = args[reportIndex + 1];
            string beforeHash = Hash(file);
            var envelope = JObject.Parse(File.ReadAllText(file));
            var data = JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            envelope = null;
            var full = Measure(data, false);
            var metadata = Measure(data, true);
            Assert.That(metadata.Fields, Is.EqualTo(full.Fields));
            Assert.That(metadata.RetainedBytes, Is.LessThan(full.RetainedBytes / 10), "元数据不应保留整个效果历史树");
            Assert.That(Hash(file), Is.EqualTo(beforeHash), "原档必须逐字保持");
            File.WriteAllText(report, JsonConvert.SerializeObject(new { SourceFile = file, SourceSha256 = beforeHash,
                data.GameId, ArchiveChars = data.ArchiveJson.Length, Full = full, Metadata = metadata,
                Scope = "同档只读解析；受控GC后的存活托管大小，不等于正式程序工作集或完整恢复峰值" }, Formatting.Indented));
        }

        private static string Hash(string file)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(file);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        [Test]
        public void OptionalPreparedLocalRestoreIsSingleUseAndInvalidatesChangedInputs()
        {
            var args = Environment.GetCommandLineArgs();
            int fileIndex = Array.IndexOf(args, "-ycMemorySaveFile");
            if (fileIndex < 0) Assert.Ignore("仅显式提供原档时运行交接隔离回归。");
            var envelope = JObject.Parse(File.ReadAllText(args[fileIndex + 1]));
            var data = JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            var type = Type.GetType("YC.Presentation.MatchSaveController, Assembly-CSharp", true);
            var root = new UnityEngine.GameObject("存档交接隔离回归");
            root.SetActive(false);
            try
            {
                var controller = root.AddComponent(type);
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var resultField = type.GetField("preparedLocalResult", flags);
                var result = Activator.CreateInstance(resultField.FieldType);
                resultField.FieldType.GetField("LocalPlayerId").SetValue(result, data.LocalPlayerId);
                var take = type.GetMethod("TakePreparedLocalRestore", flags);
                void Seed()
                {
                    type.GetField("preparedLocalSource", flags).SetValue(controller, data);
                    type.GetField("preparedLocalArchive", flags).SetValue(controller, data.ArchiveJson);
                    type.GetField("preparedLocalContinuation", flags).SetValue(controller, data.ContinuationJson);
                    type.GetField("preparedLocalContentHash", flags).SetValue(controller, "installed-test-hash");
                    var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
                    type.GetField("preparedLocalContentGeneration", flags).SetValue(controller,
                        runtime.GetProperty("ContentGeneration", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
                    resultField.SetValue(controller, result);
                }
                object Take(MatchSaveData source, string hash = "installed-test-hash") => take.Invoke(controller, new object[] { source, hash });
                Seed(); Assert.That(Take(data), Is.SameAs(result));
                Assert.That(Take(data), Is.Null, "只能交接一次");
                Seed(); Assert.That(Take(data, "changed-content"), Is.Null);
                Seed(); Assert.That(Take(new MatchSaveData()), Is.Null);
                string archive = data.ArchiveJson, continuation = data.ContinuationJson;
                Seed(); data.ArchiveJson = archive + " "; Assert.That(Take(data), Is.Null); data.ArchiveJson = archive;
                Seed(); data.ContinuationJson = continuation + " "; Assert.That(Take(data), Is.Null); data.ContinuationJson = continuation;
                int player = data.LocalPlayerId;
                Seed(); data.LocalPlayerId++; Assert.That(Take(data), Is.Null); data.LocalPlayerId = player;
                Seed(); data.Mode = LaunchMode.Host; Assert.That(Take(data), Is.Null); data.Mode = LaunchMode.Local;
                int round = data.Round;
                Seed(); data.Round++;
                Assert.That(Assert.Throws<TargetInvocationException>(() => Take(data)).InnerException, Is.TypeOf<InvalidOperationException>());
                data.Round = round;
                Assert.That(Take(data), Is.Null, "校验失败也必须释放交接对象");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void PreparedRestoreFromEarlierContentGenerationCannotBeReused()
        {
            var type = Type.GetType("YC.Presentation.MatchSaveController, Assembly-CSharp", true);
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
            var root = new UnityEngine.GameObject("内容代次隔离夹具"); root.SetActive(false);
            try
            {
                var controller = root.AddComponent(type);
                var data = new MatchSaveData { Mode = LaunchMode.Local, LocalPlayerId = 1, ArchiveJson = "隔离归档", ContinuationJson = "隔离续接" };
                var resultField = type.GetField("preparedLocalResult", instance); var result = Activator.CreateInstance(resultField.FieldType);
                resultField.FieldType.GetField("LocalPlayerId").SetValue(result, 1);
                long current = (long)runtime.GetProperty("ContentGeneration", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                void Seed(long generation)
                {
                    type.GetField("preparedLocalSource", instance).SetValue(controller, data);
                    type.GetField("preparedLocalArchive", instance).SetValue(controller, data.ArchiveJson);
                    type.GetField("preparedLocalContinuation", instance).SetValue(controller, data.ContinuationJson);
                    type.GetField("preparedLocalContentHash", instance).SetValue(controller, "相同 core 哈希");
                    type.GetField("preparedLocalContentGeneration", instance).SetValue(controller, generation);
                    resultField.SetValue(controller, result);
                }
                var take = type.GetMethod("TakePreparedLocalRestore", instance);
                Seed(current - 1);
                Assert.That(take.Invoke(controller, new object[] { data, "相同 core 哈希" }), Is.Null);
                Assert.That(resultField.GetValue(controller), Is.Null, "过期对象不得留待下次复用");
                // 当前代次通过缓存门槛后仍执行原有存档校验；这里的刻意无效归档必须被拒绝。
                Seed(current);
                Assert.Throws<TargetInvocationException>(() => take.Invoke(controller, new object[] { data, "相同 core 哈希" }));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [Test]
        public void LeavingGameplayReleasesUnconsumedLocalRestoreWithoutBreakingNormalHandoff()
        {
            var type = Type.GetType("YC.Presentation.MatchSaveController, Assembly-CSharp", true);
            var launch = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
            var singleton = type.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            var original = singleton.GetValue(null);
            var root = new UnityEngine.GameObject("离局释放隔离夹具"); root.SetActive(false);
            try
            {
                var controller = root.AddComponent(type);
                singleton.SetValue(null, controller);
                var data = new MatchSaveData { ArchiveJson = "隔离归档", ContinuationJson = "隔离续接" };
                var resultField = type.GetField("preparedLocalResult", instance);
                var result = Activator.CreateInstance(resultField.FieldType);
                type.GetField("preparedLocalSource", instance).SetValue(controller, data);
                resultField.SetValue(controller, result);
                type.GetMethod("Unbind").Invoke(controller, null);
                Assert.That(resultField.GetValue(controller), Is.SameAs(result), "正常配置交接仍须保留准备好的恢复结果。");
                launch.GetMethod("ShutdownOnlineSession").Invoke(null, null);
                Assert.That(resultField.GetValue(controller), Is.Null, "离局后不能继续持有未交接的完整对局。");
                Assert.That(type.GetField("preparedLocalSource", instance).GetValue(controller), Is.Null);
            }
            finally { singleton.SetValue(null, original); UnityEngine.Object.DestroyImmediate(root); }
        }

        private sealed class Measurement
        {
            public double Seconds;
            public long RetainedBytes;
            public string Fields;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Measurement Measure(MatchSaveData data, bool projected)
        {
            long before = GC.GetTotalMemory(true);
            var timer = Stopwatch.StartNew();
            var state = projected ? data.ReadArchiveMetadata() : data.ReadArchiveState();
            timer.Stop();
            long after = GC.GetTotalMemory(true);
            var fields = new JObject();
            foreach (string field in new[] { "RuleConfiguration", "EventDeckSeed", "RandomRuntime", "LaunchPlayerOrder",
                "CurrentPlayerId", "ActionRound" }) fields[field] = state[field]?.DeepClone();
            fields["Players"] = new JArray(((JArray)state["Players"]).Select(p => new JObject {
                ["PlayerId"] = p["PlayerId"]?.DeepClone(), ["Color"] = p["Color"]?.DeepClone(),
                ["Name"] = p["Name"]?.DeepClone(), ["Resources"] = p["Resources"]?.DeepClone() }));
            GC.KeepAlive(state);
            return new Measurement { Seconds = timer.Elapsed.TotalSeconds, RetainedBytes = after - before,
                Fields = fields.ToString(Formatting.None) };
        }
    }
}
