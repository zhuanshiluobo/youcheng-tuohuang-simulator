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
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只读原档与原命令的隔离规则定位，不计正常界面或联机验收。
    public sealed class DiyRecordedEnterpriseDeclineTests
    {
        [TestCase(false)] [TestCase(true)]
        public void RecordedEnterpriseFacilityDeclineKeepsBuiltFacilityAndRuntimeHealthy(bool injectRealFailure)
        {
            string Argument(string key) => Environment.GetCommandLineArgs()
                .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
            string fixturePath = Argument("--yc-decline-fault-fixture"), reportPath = Argument("--yc-decline-fault-report");
            if (fixturePath == null || reportPath == null) Assert.Ignore("仅显式提供隔离原档与报告路径时运行。");
            string results = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            string Safe(string path)
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(results, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("定位路径不在 TestResults。");
                return full;
            }
            string Hash(string path)
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
            fixturePath = Safe(fixturePath); reportPath = Safe(reportPath);
            if (injectRealFailure) reportPath += ".真实故障对照.json";
            Assert.That(File.Exists(reportPath), Is.False);
            var fixture = JObject.Parse(File.ReadAllText(fixturePath));
            string savePath = Safe((string)fixture["sourceSave"]), commandsPath = Safe((string)fixture["successCommands"]), faultPath = Safe((string)fixture["faultCommand"]);
            Assert.That(Hash(savePath), Is.EqualTo((string)fixture["sourceSaveSha256"]));
            Assert.That(Hash(commandsPath), Is.EqualTo((string)fixture["successCommandsSha256"]));
            Assert.That(Hash(faultPath), Is.EqualTo((string)fixture["faultCommandSha256"]));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)JObject.Parse(File.ReadAllText(savePath))["Payload"]);
            Assert.That(save.GameId, Is.EqualTo((string)fixture["gameId"]));
            Assert.That(save.ContentHash, Is.EqualTo((string)fixture["contentHash"]));
            string core = Path.Combine(Directory.GetCurrentDirectory(), "Builds/DIYFinalB8-20261112/tuohuang_Data/StreamingAssets/Content/core");
            var buildManifest = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "TestResults/DIY-award-fix-build-r8-20261112/game-manifest.json")));
            const string scriptPath = "tuohuang_Data/StreamingAssets/Content/core/lua/awards/infected.award.enterprise_special.quick.lua";
            Assert.That(Hash(Path.Combine(core, "lua/awards/infected.award.enterprise_special.quick.lua")),
                Is.EqualTo((string)((JArray)buildManifest["files"]).Single(value => (string)value["path"] == scriptPath)["sha256"]));
            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            runtimeType.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                new object[] { core, Path.Combine(Path.GetDirectoryName(fixturePath), "隔离内容/Presentation") });
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            object restored;
            try { restored = bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException &&
                (error.InnerException.Message.Contains("存档的内容版本与当前配置不一致") ||
                 error.InnerException.Message.Contains("content_version_mismatch")))
            {
                Assert.Ignore("原档属于历史执行合同；当前合同应拒绝恢复，历史原命令重放见已冻结的诊断结果。");
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
            var decline = JObject.Parse(File.ReadAllText(faultPath))["OriginalUiCommand"].ToObject<GameCommand>();
            Assert.That(decline.CommandId, Is.EqualTo((string)fixture["faultCommandId"]));
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)fixture["faultBeforeRevision"]));
            var request = session.State.EffectRuntime.InteractionRequests.Single(item => item.GetStableInteractionId() == decline.Parameters["interactionId"]);
            string owner = request.OwnerEffectId;
            var originalNodes = session.State.EffectRuntime.EffectNodes;
            var payment = originalNodes.Single(node => node.EffectId == owner);
            var condition = originalNodes.Single(node => node.EffectId == payment.ParentEffectId);
            var special = originalNodes.Single(node => node.EffectId == condition.ParentEffectId);
            var entry = originalNodes.Single(node => node.EffectId == special.ParentEffectId);
            var build = originalNodes.Single(node => node.EffectId == entry.ParentEffectId);
            Assert.That(special.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial));
            Assert.That(entry.EffectTypeId, Is.EqualTo(YC.Domain.Facilities.FacilityEntryEffectTypeIds.Activate));
            string facilitiesBefore = JsonConvert.SerializeObject(session.State.Map.Facilities);
            string resourcesBefore = JsonConvert.SerializeObject(session.State.FindPlayer(decline.PlayerId).Resources);
            int scoreBefore = session.State.FindPlayer(decline.PlayerId).Score;
            var work = GameStateCloneService.DeepClone(session.State);
            if (injectRealFailure)
            {
                registry.Register(new EffectRegistration("entry.test.fault", value => EffectStepResult.Failed("real_entry_failure"),
                    EffectExecutorKind.IntrinsicFlow, condition.DefinitionVersion));
                work.EffectRuntime.EffectNodes.Single(node => node.EffectId == condition.EffectId).EffectTypeId = "entry.test.fault";
            }
            OperationWindowService.Synchronize(work);
            Assert.That(OperationWindowService.TryResolveCommandContext(work, decline, out var context, out var validation), Is.True, validation.Reason);
            work.EffectRuntime.CurrentOperationContext = context;
            var result = new AnswerInteractionCommandHandler(registry, new RoundExecutionService(registry)).Handle(work, decline);
            var ids = new HashSet<string>();
            for (var node = work.EffectRuntime.EffectNodes.Single(item => item.EffectId == owner); node != null;
                node = work.EffectRuntime.EffectNodes.FirstOrDefault(item => item.EffectId == node.ParentEffectId)) ids.Add(node.EffectId);
            var chain = work.EffectRuntime.EffectNodes.Where(node => ids.Contains(node.EffectId) || ids.Contains(node.ParentEffectId))
                .Select(node => new { node.EffectId, node.ParentEffectId, node.EffectTypeId, node.Status, node.PendingOutcome,
                    node.FlowStage, node.FailureReason, node.NormalizedArguments, node.NormalizedResult, node.ChildEffectIds }).ToArray();
            using (var stream = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write)))
                stream.Write(JsonConvert.SerializeObject(new { Scope = "隔离原档和命令定位，非正常页面验收", save.GameId,
                    InjectedRealFailure = injectRealFailure, Trace = trace, result.Succeeded, result.Validation, work.EffectRuntime.Status, work.EffectRuntime.LastFaultCode,
                    work.EffectRuntime.LastFaultMessage, Chain = chain }, Formatting.Indented));
            if (injectRealFailure)
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Validation.Reason, Does.Contain("entity_build_child_failed"));
                return;
            }
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(work.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(JsonConvert.SerializeObject(work.Map.Facilities), Is.EqualTo(facilitiesBefore));
            Assert.That(JsonConvert.SerializeObject(work.FindPlayer(decline.PlayerId).Resources), Is.EqualTo(resourcesBefore));
            Assert.That(work.FindPlayer(decline.PlayerId).Score, Is.EqualTo(scoreBefore));
            Assert.That(work.EffectRuntime.EffectNodes.Single(node => node.EffectId == special.EffectId).FailureReason, Is.EqualTo("player_declined"));
            Assert.That(work.EffectRuntime.EffectNodes.Single(node => node.EffectId == entry.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(work.EffectRuntime.EffectNodes.Single(node => node.EffectId == build.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var final = session.Submit(decline);
            Assert.That(final.Succeeded, Is.True, final.Validation?.Reason);
            Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(JsonConvert.SerializeObject(session.State.Map.Facilities), Is.EqualTo(facilitiesBefore));
        }

        [Test]
        public void ChangedExecutionContractRejectsTheOriginalSaveWithoutModifyingIt()
        {
            string fixturePath = Environment.GetCommandLineArgs().FirstOrDefault(value =>
                value.StartsWith("--yc-decline-fault-fixture=", StringComparison.Ordinal))?.Substring("--yc-decline-fault-fixture=".Length);
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
            var runtimeType = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            runtimeType.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] {
                Path.Combine(Directory.GetCurrentDirectory(), "Builds/DIYFinalB8-20261112/tuohuang_Data/StreamingAssets/Content/core"),
                Path.Combine(Path.GetDirectoryName(fixturePath), "隔离内容/Presentation") });
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var error = Assert.Throws<TargetInvocationException>(() =>
                bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save }));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Is.EqualTo("content_version_mismatch"));
            Assert.That(Hash(), Is.EqualTo(before));
        }
    }
}
