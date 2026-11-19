using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    // 故障前真实存档的只读兼容性探针；源文件和输出都必须在隔离测试目录内。
    public sealed class LiveSaveMigrationProbeTests
    {
        [Test]
        public void RecordedHostSaveMigrationCandidateMustPassUnmodifiedRecovery()
        {
            string Arg(string key) => Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
            string root = Arg("--yc-live-save-probe-root");
            if (root == null) Assert.Ignore("仅显式指定隔离目录时运行。");
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            string Safe(string path)
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("探针路径必须在 TestResults 内。");
                return full;
            }
            root = Safe(root);
            string source = Safe(Path.Combine(Directory.GetCurrentDirectory(), "TestResults/Live-melander-roads-20261112-2055-r1/自动存档auto-2.json"));
            string Hash(string path)
            {
                using var stream = File.OpenRead(path); using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            }
            Directory.CreateDirectory(root);
            string reportPath = Path.Combine(root, "迁移候选诊断.json");
            string candidatePath = Path.Combine(root, "仅供诊断-迁移候选.json");
            Assert.That(File.Exists(reportPath) || File.Exists(candidatePath), Is.False);
            const string expectedHash = "0ed7aec6e0381123add19b53b3fe03c76337118e6eb2b47e18c887ee085b6938";
            Assert.That(Hash(source), Is.EqualTo(expectedHash));
            var sourceEnvelope = JObject.Parse(File.ReadAllText(source));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)sourceEnvelope["Payload"]);
            save.Validate();
            string oldCore = Path.Combine(Directory.GetCurrentDirectory(), "Builds/DIYFinalDistributionCharacterUiR1-20261112/tuohuang_Data/StreamingAssets/Content/core");
            string newCore = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var oldConfiguration = save.ReadLaunchMetadata().Configuration;
            var oldCatalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(oldCore), oldConfiguration.Clone());
            var newConfiguration = oldConfiguration.Clone(); newConfiguration.ContentPackHash = string.Empty;
            var newCatalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(newCore), newConfiguration);
            Assert.That(oldCatalog.ContentPackHash, Is.EqualTo(save.ContentHash));
            var changed = oldCatalog.Scripts.Keys.Union(newCatalog.Scripts.Keys).OrderBy(x => x, StringComparer.Ordinal)
                .Where(path => !oldCatalog.Scripts.TryGetValue(path, out var before) || !newCatalog.Scripts.TryGetValue(path, out var after) || before != after).ToArray();
            Assert.That(changed, Is.EquivalentTo(new[] { "lua/awards/infected.award.roads.acquired.lua", "lua/awards/infected.award.score.acquired.lua", "lua/entrepreneurs/tiago.lua" }));
            var replacements = new Dictionary<string, string> { [oldCatalog.ContentPackHash] = newCatalog.ContentPackHash };
            foreach (string path in changed)
                replacements[LuaContentHasher.ComputeSha256(oldCatalog.Scripts[path])] = LuaContentHasher.ComputeSha256(newCatalog.Scripts[path]);
            var archive = JObject.Parse(save.ArchiveJson);
            var checkpoint = JObject.Parse(save.SimulationCheckpointJson);
            var originalReceipts = checkpoint["Baseline"]["ConfirmedCommands"].DeepClone();
            var replacementsApplied = new List<object>();
            foreach (var value in archive.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String).ToArray())
                if (replacements.TryGetValue((string)value, out var replacement))
                {
                    replacementsApplied.Add(new { Path = value.Path, Before = (string)value, After = replacement });
                    value.Value = replacement;
                }
            var settings = (JsonSerializerSettings)typeof(HostRecoveryService).GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var serializer = JsonSerializer.Create(settings);
            var state = (JObject)archive["Snapshot"]["State"];
            state["ContentDefinitions"] = JToken.FromObject(newCatalog.ContentDefinitions, serializer);
            state["SetupRecipe"] = JToken.FromObject(newCatalog.SetupRecipe, serializer);
            var nodes = ((JArray)state["EffectRuntime"]["EffectNodes"]).OfType<JObject>().ToDictionary(n => (string)n["EffectId"], StringComparer.Ordinal);
            var updatedCommands = new List<object>();
            foreach (var invocation in ((JArray)state["EffectRuntime"]["AbilityActionInvocations"]).OfType<JObject>())
            {
                string id = (string)invocation["OwnerEffectId"];
                string digest = AbilityActionExecutor.Digest(nodes[id]["NormalizedArguments"].ToObject<NormalizedValue>(serializer));
                if ((string)invocation["CommandDigest"] == digest) continue;
                updatedCommands.Add(new { Path = invocation["CommandDigest"].Path, Before = (string)invocation["CommandDigest"], After = digest });
                invocation["CommandDigest"] = digest;
            }
            var typedState = state.ToObject<GameState>(serializer);
            var updatedProofs = new List<object>();
            var makeProof = typeof(FacilityPlacementMovement).GetMethod("MakeProof", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var typedNode in typedState.EffectRuntime.EffectNodes.Where(n => n.CommonItemMovementV4?.FacilityPlacement != null))
            {
                object[] arguments = { typedState, typedNode, null };
                Assert.That((bool)makeProof.Invoke(null, arguments), Is.True, typedNode.EffectId);
                var actual = JObject.FromObject(arguments[2]);
                var saved = JObject.FromObject(typedNode.CommonItemMovementV4.FacilityPlacement);
                string before = (string)saved["BuildFactsDigest"], after = (string)actual["BuildFactsDigest"];
                saved.Remove("BuildFactsDigest"); actual.Remove("BuildFactsDigest");
                Assert.That(JToken.DeepEquals(saved, actual), Is.True, "建设凭据除内容版本摘要外必须保持一致：" + typedNode.EffectId);
                if (before == after) continue;
                var field = nodes[typedNode.EffectId]["CommonItemMovementV4"]["FacilityPlacement"]["BuildFactsDigest"];
                updatedProofs.Add(new { Path = field.Path, Before = before, After = after });
                field.Replace(after);
            }
            typedState = state.ToObject<GameState>(serializer);
            var baseline = SimulationConfirmedRecoveryInstaller.ReadBaseline(save.SimulationCheckpointJson);
            baseline.ContentPackHash = newCatalog.ContentPackHash;
            baseline.RuleStateDigest = RuleStateDigestService.Compute(typedState);
            baseline.MetadataDigest = OperationCommandCanonicalizer.CanonicalRecoveryMetadataDigest(baseline);
            baseline.CheckpointId = OperationCommandCanonicalizer.RecoveryCheckpointId(baseline);
            checkpoint["Baseline"] = JToken.FromObject(baseline, serializer);
            checkpoint["Archive"] = archive.DeepClone();
            save.ContentHash = newCatalog.ContentPackHash;
            save.ArchiveJson = archive.ToString(Formatting.None);
            save.SimulationCheckpointJson = checkpoint.ToString(Formatting.None);
            string error = ""; int revision = -1; bool receiptsPreserved = JToken.DeepEquals(originalReceipts, checkpoint["Baseline"]["ConfirmedCommands"]);
            try
            {
                save.Validate();
                var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
                runtime.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                    new object[] { newCore, Path.Combine(root, "隔离内容/Presentation") });
                var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var result = bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
                var session = (GameSession)result.GetType().GetField("Session").GetValue(result);
                revision = session.State.EffectRuntime.StateRevision;
                OperationCommandCanonicalizer.ValidateRecoveryBaseline(MatchSaveData.OptionsFor(baseline, true, save.LocalPlayerId), baseline, session.State);
            }
            catch (Exception ex) { error = ex.ToString(); }
            var storeType = Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
            var saveSettings = (JsonSerializerSettings)storeType.GetField("JsonSettings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            string payload = JsonConvert.SerializeObject(save, saveSettings);
            string checksum;
            using (var sha = SHA256.Create()) checksum = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            using (var writer = new StreamWriter(new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
                writer.Write(JsonConvert.SerializeObject(new { Format = "YC.MatchSave", Version = MatchSaveData.CurrentVersion, Payload = payload, Checksum = checksum }));
            string slotRoot = Path.Combine(root, "正式槽位格式核验"); Directory.CreateDirectory(slotRoot);
            File.Copy(candidatePath, Path.Combine(slotRoot, "manual-1.json"), false);
            var store = Activator.CreateInstance(storeType, slotRoot);
            var readBack = (MatchSaveData)storeType.GetMethod("Read").Invoke(store, new object[] { 0 });
            Assert.That(readBack.GameId, Is.EqualTo(save.GameId));
            Assert.That(readBack.ContentHash, Is.EqualTo(save.ContentHash));
            using (var writer = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write)))
                writer.Write(JsonConvert.SerializeObject(new { Scope = "仅供隔离定位；未写入真实存档槽位", save.GameId, SourceSha256 = expectedHash,
                    OldHash = oldCatalog.ContentPackHash, NewHash = newCatalog.ContentPackHash, ChangedScripts = changed, Replacements = replacementsApplied,
                    UpdatedCommandDigests = updatedCommands, UpdatedPlacementProofs = updatedProofs,
                    ConfirmedCommandsPreserved = receiptsPreserved, RestoredRevision = revision, Error = error }, Formatting.Indented));
            Assert.That(Hash(source), Is.EqualTo(expectedHash));
            Assert.That(receiptsPreserved, Is.True, "原确认命令凭据必须保留。");
            Assert.That(error, Is.Empty, error);
            Assert.That(revision, Is.EqualTo((int)archive["Snapshot"]["StateRevision"]));
        }

        [Test]
        public void RecordedHostSaveRetainsOriginalContentAndCanBeRestored()
        {
            string Arg(string key) => Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
            string resultRoot = Arg("--yc-live-save-probe-root");
            if (resultRoot == null) Assert.Ignore("仅显式指定隔离目录时运行。");
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            string Safe(string path)
            {
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("探针路径必须在 TestResults 内。");
                return full;
            }
            resultRoot = Safe(resultRoot);
            string source = Safe(Path.Combine(Directory.GetCurrentDirectory(), "TestResults/Live-melander-roads-20261112-2055-r1/自动存档auto-2.json"));
            string Hash(string path)
            {
                using var stream = File.OpenRead(path); using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            }
            Directory.CreateDirectory(resultRoot);
            string report = Path.Combine(resultRoot, "只读兼容性探针.json");
            Assert.That(File.Exists(report), Is.False);
            const string expectedHash = "0ed7aec6e0381123add19b53b3fe03c76337118e6eb2b47e18c887ee085b6938";
            Assert.That(Hash(source), Is.EqualTo(expectedHash));
            var envelope = JObject.Parse(File.ReadAllText(source));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            save.Validate();
            var config = save.ReadLaunchMetadata().Configuration;
            string oldCore = Path.Combine(Directory.GetCurrentDirectory(), "Builds/DIYFinalDistributionCharacterUiR1-20261112/tuohuang_Data/StreamingAssets/Content/core");
            string newCore = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var oldCatalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(oldCore), config.Clone());
            var newConfiguration = config.Clone(); newConfiguration.ContentPackHash = string.Empty;
            var newCatalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(newCore), newConfiguration);
            var archive = JObject.Parse(save.ArchiveJson);
            var state = archive["Snapshot"]["State"];
            bool oldDefinitionsMatch = JToken.DeepEquals(state["ContentDefinitions"], JToken.FromObject(oldCatalog.ContentDefinitions));
            bool newDefinitionsMatch = JToken.DeepEquals(state["ContentDefinitions"], JToken.FromObject(newCatalog.ContentDefinitions));
            bool oldRecipeMatches = JToken.DeepEquals(state["SetupRecipe"], JToken.FromObject(oldCatalog.SetupRecipe));
            bool newRecipeMatches = JToken.DeepEquals(state["SetupRecipe"], JToken.FromObject(newCatalog.SetupRecipe));
            string restoreError = ""; int revision = -1;
            try
            {
                var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
                runtime.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                    new object[] { oldCore, Path.Combine(resultRoot, "隔离内容/Presentation") });
                var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var result = bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
                var session = (GameSession)result.GetType().GetField("Session").GetValue(result);
                revision = session.State.EffectRuntime.StateRevision;
            }
            catch (Exception error) { restoreError = error.ToString(); }
            using (var writer = new StreamWriter(new FileStream(report, FileMode.CreateNew, FileAccess.Write)))
                writer.Write(JsonConvert.SerializeObject(new { save.GameId, save.Round, SourceSha256 = expectedHash,
                    SavedContentHash = save.ContentHash, OldActiveHash = oldCatalog.ContentPackHash, NewActiveHash = newCatalog.ContentPackHash,
                    OldDefinitionsMatch = oldDefinitionsMatch, NewDefinitionsMatch = newDefinitionsMatch,
                    OldRecipeMatches = oldRecipeMatches, NewRecipeMatches = newRecipeMatches, RestoredRevision = revision, RestoreError = restoreError }, Formatting.Indented));
            Assert.That(Hash(source), Is.EqualTo(expectedHash));
            Assert.That(oldCatalog.ContentPackHash, Is.EqualTo(save.ContentHash));
            Assert.That(restoreError, Is.Empty, restoreError);
            Assert.That(revision, Is.EqualTo((int)archive["Snapshot"]["StateRevision"]));
        }
    }
}
