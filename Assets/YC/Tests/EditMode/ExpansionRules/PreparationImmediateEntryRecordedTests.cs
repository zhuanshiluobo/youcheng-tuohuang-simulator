using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>重放正常可见对局的隔离副本，不代替正常界面验收。</summary>
    public sealed class PreparationImmediateEntryRecordedTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RecordedFrameworkSelectionRequiresItsActualPreparationOwner(bool forgedOwner)
        {
            var root = Environment.GetEnvironmentVariable("NOMADCITY_PREPARATION_RECORDED_FIXTURE");
            if (string.IsNullOrEmpty(root)) Assert.Ignore("没有指定准备阶段实录夹具。");
            string savePath = Path.Combine(root, "saves", "auto-1.json");
            string commandPath = Path.Combine(root, "ui-output", "命令与准确回执.jsonl");
            if (!File.Exists(savePath) || !File.Exists(commandPath)) Assert.Ignore("缺少准备阶段实录夹具。");
            var runtime = Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            runtime.GetMethod("ConfigureRegistered", BindingFlags.Public | BindingFlags.Static).Invoke(null,
                new object[] { Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"), Path.Combine(root, "content") });
            var envelope = JObject.Parse(File.ReadAllText(savePath));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var restored = bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
            var session = (GameSession)restored.GetType().GetField("Session").GetValue(restored);
            Assert.That(session.State.GameId, Is.EqualTo("aef5e6fdda284ca59a24e6099aa8c4a9"));
            Assert.That(session.State.EntrepreneurSetup.SetupProtocolVersion, Is.EqualTo(4));
            string lastCommand = null;
            using (var stream = new FileStream(commandPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                while (!reader.EndOfStream) lastCommand = reader.ReadLine();
            var command = JObject.Parse(lastCommand)["Command"].ToObject<GameCommand>();
            Assert.That(command.OptionIds, Is.EqualTo(new[] { "entrepreneur.candidate.M07" }));
            int budget = session.State.FindPlayer(command.PlayerId).RemainingMainActionsThisTurn;
            if (forgedOwner) session.State.EntrepreneurSetup.OwnerEffectId = "forged-preparation-owner";
            var result = session.Submit(command);
            if (forgedOwner)
            {
                Assert.That(result.Succeeded && session.State.EffectRuntime.Status == EffectRuntimeStatus.Active, Is.False,
                    "伪造准备流程来源不能取得即时效果的来源事实。");
                return;
            }
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(session.State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), session.State.EffectRuntime.LastFaultMessage);
            Assert.That(session.State.EffectRuntime.LastFaultMessage, Is.Empty);
            Assert.That(session.State.FindPlayer(command.PlayerId).RemainingMainActionsThisTurn, Is.EqualTo(budget));
            Assert.That(session.State.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == EntrepreneurFlow.EntryTypeId &&
                node.PlayerId == command.PlayerId && node.Status != EffectNodeStatus.Faulted), Is.True);
        }
    }
}
