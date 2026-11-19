using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 原故障局的自然存档和真实命令仅用于隔离规则定位，不计可见程序验收。
    public sealed class DiyRecordedEnterpriseSelectionTests
    {
        [Test]
        public void UpdatedContentRejectsOriginalSaveWithoutChangingIt()
        {
            string fixturePath = Environment.GetCommandLineArgs().FirstOrDefault(value =>
                value.StartsWith("--yc-selection-fault-fixture=", StringComparison.Ordinal))?.Substring("--yc-selection-fault-fixture=".Length);
            if (fixturePath == null) Assert.Ignore("仅显式提供隔离原档时运行。");
            string results = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            fixturePath = Path.GetFullPath(fixturePath);
            Assert.That(fixturePath.StartsWith(results, StringComparison.OrdinalIgnoreCase), Is.True);
            var fixture = JObject.Parse(File.ReadAllText(fixturePath));
            string savePath = Path.GetFullPath((string)fixture["sourceSave"]);
            Assert.That(savePath.StartsWith(results, StringComparison.OrdinalIgnoreCase), Is.True);
            string Hash()
            {
                using var stream = File.OpenRead(savePath);
                using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
            string before = Hash();
            Assert.That(before, Is.EqualTo((string)fixture["sourceSaveSha256"]));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(savePath))["Payload"]);
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            runtime.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] {
                Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"),
                Path.Combine(Path.GetDirectoryName(fixturePath), "隔离内容/Presentation") });
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var error = Assert.Throws<TargetInvocationException>(() =>
                bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save }));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain("content_version_mismatch"));
            Assert.That(Hash(), Is.EqualTo(before));
        }

        [Test]
        public void RecordedEnterpriseSpecialSelectionKeepsFacilityRuntimeHealthy()
        {
            string Argument(string key) => Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
            string fixturePath = Argument("--yc-selection-fault-fixture"), reportPath = Argument("--yc-selection-fault-report");
            if (fixturePath == null || reportPath == null) Assert.Ignore("仅显式提供隔离原档和报告路径时运行。");
            string allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            string Safe(string path)
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("定位路径不在 TestResults。");
                return full;
            }
            string Hash(string path)
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
            fixturePath = Safe(fixturePath); reportPath = Safe(reportPath);
            Assert.That(File.Exists(reportPath), Is.False);
            var fixture = JObject.Parse(File.ReadAllText(fixturePath));
            string savePath = Safe((string)fixture["sourceSave"]), commandsPath = Safe((string)fixture["successCommands"]), faultPath = Safe((string)fixture["faultCommand"]);
            Assert.That(Hash(savePath), Is.EqualTo((string)fixture["sourceSaveSha256"]));
            Assert.That(Hash(commandsPath), Is.EqualTo((string)fixture["successCommandsSha256"]));
            Assert.That(Hash(faultPath), Is.EqualTo((string)fixture["faultCommandSha256"]));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(savePath))["Payload"]);
            Assert.That(save.GameId, Is.EqualTo((string)fixture["gameId"]));
            Assert.That(save.ContentHash, Is.EqualTo((string)fixture["contentHash"]));
            string core = Path.Combine(Directory.GetCurrentDirectory(), "Builds/DIYFinalB9-20261112/tuohuang_Data/StreamingAssets/Content/core");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "TestResults/DIY-enterprise-fix-build-r9-20261112/game-manifest.json")));
            const string key = "tuohuang_Data/StreamingAssets/Content/core/pack.json";
            Assert.That(Hash(Path.Combine(core, "pack.json")), Is.EqualTo((string)((JArray)manifest["files"]).Single(value => (string)value["path"] == key)["sha256"]));
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            runtime.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                new object[] { core, Path.Combine(Path.GetDirectoryName(fixturePath), "隔离内容/Presentation") });
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            object restored;
            try { restored = bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException &&
                (error.InnerException.Message.Contains("content_version_mismatch") || error.InnerException.Message.Contains("存档的内容版本与当前配置不一致")))
            {
                Assert.Ignore("原档属于历史执行合同；当前合同应拒绝恢复，原命令诊断以已冻结结果为准。");
                throw;
            }
            var session = (GameSession)restored.GetType().GetField("Session").GetValue(restored);
            var registry = (EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)fixture["initialRevision"]));
            var trace = new List<object>();
            foreach (var original in File.ReadLines(commandsPath).Select(JObject.Parse))
            {
                Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)original["BeforeRevision"]));
                var command = original["Command"].ToObject<GameCommand>();
                var applied = session.Submit(command);
                trace.Add(new { command.CommandId, Before = (int)original["BeforeRevision"], After = session.State.EffectRuntime.StateRevision,
                    applied.Succeeded, Reason = applied.Validation?.Reason });
                Assert.That(applied.Succeeded, Is.True, applied.Validation?.Reason);
                Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)original["AfterRevision"]));
            }
            var selection = JObject.Parse(File.ReadAllText(faultPath))["OriginalUiCommand"].ToObject<GameCommand>();
            Assert.That(selection.CommandId, Is.EqualTo((string)fixture["faultCommandId"]));
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)fixture["faultBeforeRevision"]));
            string candidatePath = Argument("--yc-selection-probe-source");
            if (candidatePath != null)
            {
                candidatePath = Safe(candidatePath);
                const string relativeScript = "lua/enterprises/infected.enterprise.rhodes_island.level_1.special.lua";
                string originalPath = Path.Combine(core, relativeScript);
                var files = (JArray)manifest["files"];
                Assert.That(Hash(originalPath), Is.EqualTo((string)files.Single(value => (string)value["path"] == "tuohuang_Data/StreamingAssets/Content/core/" + relativeScript)["sha256"]));
                var visible = new ItemZoneQuery().Project(session.State, GameStateViewer.Player(selection.PlayerId));
                var instances = visible.Items.Select(item => LuaSnapshotValue.Object(new[] {
                    new LuaSnapshotEntry("id", LuaSnapshotValue.String(item.ItemId)),
                    new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(item.DefinitionId)) })).ToList();
                var builder = typeof(MoonSharpLuaRuntimeHost).Assembly.GetType("YC.Infrastructure.Lua.LuaQuerySnapshotBuilder", true);
                var content = (LuaSnapshotValue)builder.GetMethod("Content", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { session.State, visible, instances });
                var actor = session.State.FindPlayer(selection.PlayerId);
                LuaInvocationResult Invoke(string source)
                {
                    var snapshot = LuaFacadeSnapshot.FromInvocation("p" + selection.PlayerId, actor.Score, actor.Resources.GoldVoucher, session.State.Players.Count, "1.0.0");
                    snapshot.GlobalContent = content;
                    var invocation = new LuaInvocationContext("fixture.enterprise", "AbilityInvoked", "p" + selection.PlayerId,
                        session.State.EffectRuntime.StateRevision, "1.0.0", session.State.Players.Count, actor.Score, actor.Resources.GoldVoucher, snapshot);
                    var script = new LuaScriptDefinition("infected.enterprise.rhodes_island", "infected.enterprise.rhodes_island.level_1.special",
                        "content.infected.enterprise.rhodes_island.level_1.special", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
                    return new MoonSharpLuaRuntimeHost().Invoke(script, invocation);
                }
                var oldResult = Invoke(File.ReadAllText(originalPath));
                var newResult = Invoke(File.ReadAllText(candidatePath));
                Assert.That(oldResult.IsSuccess, Is.True, oldResult.Diagnostic);
                Assert.That(newResult.IsSuccess, Is.True, newResult.Diagnostic);
                string[] Scope(LuaInvocationResult result) => result.Effects.Single().RawArguments.Properties
                    .Single(entry => entry.Name == "enterpriseItemIds").Value.Items.Select(value => value.StringValue).ToArray();
                var active = new EnterpriseStateQuery(registry.EnterpriseDefinitions).GetEnterpriseItemIds(session.State);
                var expected = active.Where(id => session.State.ItemZones.Items.Single(item => item.ItemId == id).DefinitionId != "infected.enterprise.rhodes_island").ToArray();
                var oldScope = Scope(oldResult); var newScope = Scope(newResult);
                using (var stream = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write)))
                    stream.Write(JsonConvert.SerializeObject(new { Scope = "隔离原状态的 Lua 查询探针，不恢复新合同存档、不计正常页面验收",
                        save.GameId, Trace = trace, OriginalScriptSha256 = Hash(originalPath), CandidateScriptSha256 = Hash(candidatePath),
                        ActiveEnterpriseIds = active, Expected = expected, OldScope = oldScope, NewScope = newScope,
                        OldInactive = oldScope.Except(active), OldEffects = oldResult.Effects, NewEffects = newResult.Effects }, Formatting.Indented));
                Assert.That(oldScope.Except(active), Is.Not.Empty, "原现场必须包含不在场候选，才能证明重现根因。");
                Assert.That(newScope, Is.EquivalentTo(expected));
                return;
            }
            var request = session.State.EffectRuntime.InteractionRequests.Single(value => value.GetStableInteractionId() == selection.Parameters["interactionId"]);
            string owner = request.OwnerEffectId;
            string facilitiesBefore = JsonConvert.SerializeObject(session.State.Map.Facilities);
            var work = GameStateCloneService.DeepClone(session.State);
            OperationWindowService.Synchronize(work);
            Assert.That(OperationWindowService.TryResolveCommandContext(work, selection, out var context, out var validation), Is.True, validation.Reason);
            work.EffectRuntime.CurrentOperationContext = context;
            var result = new AnswerInteractionCommandHandler(registry, new RoundExecutionService(registry)).Handle(work, selection);
            var nodes = work.EffectRuntime.EffectNodes;
            var selected = new HashSet<string> { owner };
            for (var node = nodes.Single(value => value.EffectId == owner); node != null;
                node = nodes.FirstOrDefault(value => value.EffectId == node.ParentEffectId)) selected.Add(node.EffectId);
            var descendants = new HashSet<string> { owner };
            bool changed;
            do { changed = false; foreach (var node in nodes.Where(value => descendants.Contains(value.ParentEffectId))) changed |= descendants.Add(node.EffectId); } while (changed);
            var chain = nodes.Where(node => selected.Contains(node.EffectId) || selected.Contains(node.ParentEffectId) || descendants.Contains(node.EffectId))
                .Select(node => new { node.EffectId, node.ParentEffectId, node.EffectTypeId, node.Status, node.PendingOutcome,
                    node.FlowStage, node.FailureReason, node.NormalizedArguments, node.NormalizedResult, node.ChildEffectIds }).ToArray();
            using (var stream = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write)))
                stream.Write(JsonConvert.SerializeObject(new { Scope = "隔离原档和命令定位，非正常页面验收", save.GameId,
                    Trace = trace, result.Succeeded, result.Validation, work.EffectRuntime.Status, work.EffectRuntime.LastFaultCode,
                    work.EffectRuntime.LastFaultMessage, Chain = chain }, Formatting.Indented));
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(work.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(JsonConvert.SerializeObject(work.Map.Facilities), Is.EqualTo(facilitiesBefore));
            var final = session.Submit(selection);
            Assert.That(final.Succeeded, Is.True, final.Validation?.Reason);
            Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
        }
    }
}
