using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 原R18冻结数据的隔离回归；无正式槽写入、无预设新局，不计正常界面或整局验收。
    public sealed class QY018JessicaPresentationRecoveryR15Tests
    {
        private static string Root => Directory.GetCurrentDirectory();
        private static string Evidence => Path.Combine(Root, "prompt/企业家内容实施规划-20261019/实施记录/QY-018-JESSICA-PRESENTATION-FAULT-AND-RESTORE-R15");
        private static string Original => Path.Combine(Root, "prompt/企业家内容实施规划-20261019/实施记录/QY-019-FOUR-PLAYER-HOTSEAT-RESUME-R18");
        private const string ExpectedHash = "b68344b9d026498af200548aca9e51cb23dece71b156adac0da7656b75b8fe29";
        private const string UseId = "character_use_8adeed9236de155ed37fb22d5b35a666cd7d3120c2380a93adac7bfadee36703";
        private static string nativeOutput;
        private static void Record(string name, object data)
        {
            if (nativeOutput == null)
            {
                nativeOutput = Environment.GetEnvironmentVariable("YC_QY018_R15_NATIVE_EVIDENCE_ROOT") ??
                    Path.Combine(Evidence, "原生有限验证", "独立批次-" + Guid.NewGuid().ToString("N"));
                nativeOutput = Path.GetFullPath(nativeOutput);
                var allowed = Path.GetFullPath(Path.Combine(Evidence, "原生有限验证")) + Path.DirectorySeparatorChar;
                if (!nativeOutput.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("原生记录只允许写本轮独立验证子目录。");
                Directory.CreateDirectory(nativeOutput);
            }
            File.WriteAllText(Path.Combine(nativeOutput, name), JsonConvert.SerializeObject(data, Formatting.Indented));
        }
        private static NormalizedValue Raw() => JsonConvert.DeserializeObject<NormalizedValue>(File.ReadAllText(Path.Combine(Evidence, "真实续接显示声明归一化输入.json")));

        private static string Sha(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }
        [OneTimeSetUp]
        public void CaptureExactFrozenInputsAndActualEditorAssemblyIdentity()
        {
            var healthy = Path.Combine(Original, "进入第8回合首个自然检查点/auto-3.json");
            var faulted = Path.Combine(Original, "整轮结束各实际槽原样保全/auto-3.json");
            Assert.That(Sha(healthy), Is.EqualTo("722b4a42dd2b77e7c615463dbf8f5b1fa1a225638d6c8f7df406ab14619d00d5"));
            Assert.That(Sha(faulted), Is.EqualTo("19f1e739f855fbf0fe8b6a6b90733ff0b752e563043007f1df9d588cf5fa0d70"));
            var assemblies = new[] { typeof(EffectRegistry).Assembly, typeof(MoonSharpLuaRuntimeHost).Assembly, typeof(GameSession).Assembly, GetType().Assembly };
            Record("原生实际加载身份.json", new { scope = "当前Unity EditMode实际程序集，非新Player身份；冻结R38只供原内容及输入。",
                assemblies = assemblies.Select(a => new { name = a.GetName().Name, a.Location, sha256 = Sha(a.Location), mvid = a.ManifestModule.ModuleVersionId.ToString() }).ToArray(),
                healthySha256 = Sha(healthy), faultedSha256 = Sha(faulted) });
        }

        [Test]
        public void RealP4LabelsRoundtripWithoutSharingOneBusinessValueBudget()
        {
            var raw = Raw();
            Assert.That(raw.TryValidate(out string reason), Is.False);
            Assert.That(reason, Does.Contain("元素数量"));
            var presentation = GameplayPresentationNormalizer.FromStructuredValue<EffectPresentationDefinition>(raw);
            var copy = GameplayPresentationNormalizer.FromStructuredValue<EffectPresentationDefinition>(GameplayPresentationNormalizer.ToStructuredValue(presentation));
            Assert.That(copy.Interaction.Options.Count, Is.EqualTo(10));
            Assert.That(copy.Interaction.Options.Select(o => o.CandidateId), Is.EqualTo(presentation.Interaction.Options.Select(o => o.CandidateId)));
            Assert.That(copy.Interaction.Options.All(o => o.Label.Arguments.Select(a => a.Kind).SequenceEqual(new[] { "item", "integer", "player" })), Is.True);
            Assert.That(NormalizedValue.MaxElements, Is.EqualTo(128));
            Assert.That(NormalizedValue.MaxDepth, Is.EqualTo(8));
        }

        [Test]
        public void StructuredDisplayHonorsItsDeclaredCollectionBound()
        {
            var source = GameplayPresentationNormalizer.FromStructuredValue<EffectPresentationDefinition>(Raw());
            var sample = source.Interaction.Options.First();
            source.Interaction.Options = Enumerable.Range(0, NormalizedValue.MaxElements).Select(i =>
            { var option = GameplayPresentationNormalizer.Clone(sample); option.CandidateId = "candidate:" + i; return option; }).ToList();
            var copy = GameplayPresentationNormalizer.FromStructuredValue<EffectPresentationDefinition>(GameplayPresentationNormalizer.ToStructuredValue(source));
            Assert.That(copy.Interaction.Options.Count, Is.EqualTo(128));
            var last = GameplayPresentationNormalizer.Clone(sample); last.CandidateId = "candidate:overflow"; source.Interaction.Options.Add(last);
            Assert.Throws<ArgumentException>(() => GameplayPresentationNormalizer.ToStructuredValue(source));
        }

        [Test]
        public void StructuredContextDoesNotExtendEmbeddedValueDepth()
        {
            var facts = NormalizedValue.CreateString("leaf");
            for (int i = 0; i < NormalizedValue.MaxDepth; i++)
                facts = NormalizedValue.CreateObject(new[] { new NormalizedValueEntry { Name = "child", Value = facts } });
            Assert.That(facts.IsValid(), Is.True);
            var context = new GameplayPresentationContext { Purpose = "interaction", Facts = facts, StateRevision = 0 };
            Assert.Throws<ArgumentException>(() => GameplayPresentationNormalizer.ToStructuredValue(context));
        }

        [TestCase("duplicate_field")]
        [TestCase("unknown_field")]
        [TestCase("wrong_reference_kind")]
        [TestCase("long_scalar")]
        [TestCase("integer_range")]
        [TestCase("oversized_entry")]
        public void StructuredDisplayRetainsClosedShapeAndPerEntryValidation(string defect)
        {
            var raw = Raw();
            var interaction = raw.Properties.Single(p => p.Name == "interaction").Value;
            var options = interaction.Properties.Single(p => p.Name == "options").Value;
            var option = options.Items.First();
            var label = option.Properties.Single(p => p.Name == "label").Value;
            var args = label.Properties.Single(p => p.Name == "arguments").Value;
            if (defect == "duplicate_field") raw.Properties.Add(raw.Properties.First());
            else if (defect == "unknown_field") raw.Properties.Add(new NormalizedValueEntry { Name = "unknown", Value = NormalizedValue.CreateNull() });
            else if (defect == "wrong_reference_kind") args.Items.First().Properties.Single(p => p.Name == "value").Value.ReferenceType = "player";
            else if (defect == "long_scalar") option.Properties.Single(p => p.Name == "candidateId").Value = new NormalizedValue { Kind = NormalizedValueKind.String, StringValue = new string('x', NormalizedValue.MaxStringBytes + 1) };
            else if (defect == "integer_range") args.Items.Single(a => a.Properties.Single(p => p.Name == "name").Value.StringValue == "level").Properties.Single(p => p.Name == "value").Value.IntegerValue = NormalizedValue.MaxInteger + 1;
            else if (defect == "oversized_entry")
                args.Items = Enumerable.Range(0, 40).Select(i => NormalizedValue.CreateObject(new[] {
                    new NormalizedValueEntry { Name = "kind", Value = NormalizedValue.CreateString("integer") },
                    new NormalizedValueEntry { Name = "name", Value = NormalizedValue.CreateString("arg" + i) },
                    new NormalizedValueEntry { Name = "value", Value = NormalizedValue.CreateInteger(i) } })).ToList();
            Assert.Throws<ArgumentException>(() => GameplayPresentationNormalizer.FromStructuredValue<EffectPresentationDefinition>(raw));
        }

        private static JObject Payload(string relative) => JObject.Parse((string)JObject.Parse(File.ReadAllText(Path.Combine(Original, relative)))["Payload"]);
        private static (EffectRegistry registry, List<IGameCommandHandler> handlers) Configure(JObject payload)
        {
            var state = JObject.Parse((string)payload["ArchiveJson"])["Snapshot"]["State"];
            var pack = ExternalContentPack.Load(Path.Combine(Root, "Builds/QY017-R38-20261024-resume7934/tuohuang_Data/StreamingAssets/Content/core"));
            // 复用本家R13已按正式装配顺序接线的纯服务助手，不改其源码或重复实现游戏规则。
            var configure = typeof(QY018D09SnapshotRecoveryR13Tests).GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Static);
            return ((EffectRegistry, List<IGameCommandHandler>))configure.Invoke(null, new object[] {
                pack, state["RuleConfiguration"].ToObject<GameRuleConfiguration>(), (string)state["MapId"], (int)state["EventDeckSeed"] });
        }

        private static GameSession Session(GameState state, EffectRegistry registry, List<IGameCommandHandler> handlers, JObject payload)
        {
            var session = new GameSession(state); session.ConfigurePaymentSourceQueries(registry);
            foreach (var handler in handlers) session.RegisterHandler(handler);
            var continuation = SimulationConfirmedRecoveryInstaller.ReadContinuation((string)payload["ContinuationJson"]);
            Assert.That(session.TryPrepareLocalRecoveryState(state, continuation, out var prepared, out var fault), Is.True, fault);
            Assert.That(session.TryCommitPreparedRecovery(prepared, out fault), Is.True, fault);
            return session;
        }

        [Test]
        public void Healthy8616OriginalP4BranchOffersAllTenSpecialsAndSettlesFirstRealCandidate()
        {
            var payload = Payload("进入第8回合首个自然检查点/auto-3.json");
            var services = Configure(payload);
            Assert.That(services.registry.ContentPackHash, Is.EqualTo(ExpectedHash));
            var recovered = HostRecoveryService.RecoverJson((string)payload["ArchiveJson"], services.registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var session = Session(recovered.State, services.registry, services.handlers, payload);
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(8616));
            var commands = File.ReadLines(Path.Combine(Original, "a002-R38视口异常后8113正常续局/命令与准确回执.jsonl"))
                .Where(line => !string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).Where(row => (int)row["BeforeRevision"] >= 8616).ToList();
            Assert.That(commands.Count, Is.EqualTo(7));
            foreach (var row in commands)
            {
                Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo((int)row["BeforeRevision"]));
                var result = session.Submit(row["Command"].ToObject<GameCommand>());
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), session.State.EffectRuntime.LastFaultMessage);
            }
            var request = session.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.PromptKey == "core:interaction.enterprise.special.choose");
            Assert.That(request.CandidateIds.Count, Is.EqualTo(10));
            Assert.That(request.Presentation.Options.Select(o => o.CandidateId), Is.EquivalentTo(request.CandidateIds));
            Assert.That(request.Presentation.Options.All(o => o.Label.Arguments.Single(a => a.Name == "player").Value.ReferenceId == "p4"), Is.True);
            var realCandidate = request.CandidateIds.First();
            var decisions = new List<object>();
            for (int step = 0; step < 16 && !session.State.CharacterUses.Single(u => u.UseId == UseId).Finished; step++)
            {
                var next = session.State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
                Assert.That(next, Is.Not.Null, "本策略尚未结束却没有正式待答。");
                var selected = next.CandidateIds.Take(Math.Min(next.CandidateIds.Count, Math.Max(1, next.MinSelections))).ToList();
                var answer = new GameCommand { CommandId = "qy018-r15-isolated-followup-" + step, Kind = GameCommandKind.AnswerInteraction,
                    PlayerId = next.AnsweringPlayerId, OptionIds = selected,
                    Parameters = new Dictionary<string, string> { ["interactionId"] = next.GetStableInteractionId(), ["expectedRevision"] = session.State.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture) } };
                var result = session.Submit(answer);
                decisions.Add(new { next.InteractionTypeId, selected, result.Succeeded, result.LogMessage });
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), session.State.EffectRuntime.LastFaultMessage);
            }
            Assert.That(session.State.CharacterUses.Single(u => u.UseId == UseId).CompletedModes, Does.Contain("strategy"));
            Assert.That(session.State.CharacterUses.Single(u => u.UseId == UseId).Finished, Is.True);
            var activated = session.State.EffectRuntime.RuleEvents.Where(e => e.EventType == "EnterpriseSpecialEffectActivated" &&
                ItemTransferEffectExecutor.Number(e.Payload, "executingPlayerId") == 2 && ItemTransferEffectExecutor.Number(e.Payload, "unlockedByPlayerId") == 4).ToList();
            Assert.That(activated, Is.Not.Empty);
            var again = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(session.State)), services.registry);
            Assert.That(again.Succeeded, Is.True, again.Diagnostic);
            Assert.That(again.State.ContentPackHash, Is.EqualTo(ExpectedHash));
            Assert.That(again.State.EffectRuntime.Diagnostics.Any(d => d.StateRevision == 6677 && d.Code == "presentation_failed"), Is.True);
            Record("原生8616真实分支隔离结果.json", new { scope = "原档只读内存分支，不计正常UI或正式槽", replayedAncestorCommandIds = commands.Select(r => (string)r["Command"]["CommandId"]).ToArray(),
                realCandidate, offeredCandidates = request.CandidateIds, decisions, completedStrategy = true, finalRevision = session.State.EffectRuntime.StateRevision,
                contentHash = again.State.ContentPackHash, diagnostics = again.State.EffectRuntime.Diagnostics });
        }

        [Test]
        public void Fault8687FormalRecoveryKeepsPausedFaultAndDoesNotRetry()
        {
            var payload = Payload("整轮结束各实际槽原样保全/auto-3.json"); var services = Configure(payload);
            var recovered = HostRecoveryService.RecoverJson((string)payload["ArchiveJson"], services.registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(recovered.State.EffectRuntime.StateRevision, Is.EqualTo(8687));
            Assert.That(recovered.State.EffectRuntime.Diagnostics.Any(d => d.Code == "event_dispatch_exception" && d.StateRevision == 8686), Is.True);
            Assert.That(new EffectTreeExecutor(recovered.State, services.registry).RunUntilQuiescent().Faulted, Is.True);
            Assert.That(recovered.State.EffectRuntime.StateRevision, Is.EqualTo(8687));
            Record("原生8687正式恢复边界.json", new { recovered.Succeeded, recovered.State.EffectRuntime.Status, recovered.State.EffectRuntime.StateRevision,
                recovered.State.EffectRuntime.LastFaultCode, recovered.State.EffectRuntime.Diagnostics, implicitRetry = false });
        }
    }
}
