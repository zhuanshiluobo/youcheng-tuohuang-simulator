using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Travel;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class RouteResourceSelectionTests
    {
        [TestCase("common_selection", false)][TestCase("path", false)][TestCase("toll", false)][TestCase("applied", false)]
        [TestCase("common_selection", true)][TestCase("path", true)][TestCase("toll", true)][TestCase("applied", true)]
        public void RetiredExplorationParentCannotResumeExistingSelectionOrHeldEventChildren(string oldStage, bool held)
        {
            Production(out var state, out var registry, out _); string root = StartExploreV3(state, registry, ExploreSelect);
            if (held) { var q = Selection(state); Submit(state, registry, q, SelectionAnswer(q, "A-01", "A1", "A2")); }
            var clean = HostRecoveryService.CreateArchive(state);
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(clean));
            var raw = archive["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].OfType<JObject>().Single(n => (string)n["EffectId"] == root);
            raw["EffectTypeId"] = ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId; raw["FlowStage"] = oldStage;
            string original = archive.ToString(); var rejected = HostRecoveryService.RecoverJson(original, registry);
            Assert.That(rejected.Succeeded, Is.False); Assert.That(rejected.Diagnostic, Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredExplorationSelectionReason));
            Assert.That(archive.ToString(), Is.EqualTo(original));
            exploreArtifacts.WriteLabeledText("旧探索父包装已挂接子链拒绝_" + oldStage + "_" + held, original);

            // 隔离模拟内存中仍持有旧父包装：请求本身仍由合法的共用选路/事件节点拥有。
            var memory = GameStateCloneService.DeepClone(state); var old = ExploreRoot(memory, root);
            old.EffectTypeId = ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId; old.FlowStage = oldStage;
            var request = memory.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
            Assert.That(ExploreRoot(memory, request.OwnerEffectId).EffectTypeId, Is.Not.EqualTo(ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId));
            string before = JsonConvert.SerializeObject(memory);
            var answer = held ? NormalizedValue.CreateArray(request.CandidateIds.Take(Math.Max(1, request.MinSelections)).Select(NormalizedValue.CreateString).ToList())
                : NormalizedValue.CreateArray(SelectionAnswer(request, "A-01", "A1", "A2").Select(NormalizedValue.CreateString).ToList());
            Assert.That(new EffectTreeExecutor(memory, registry).TrySubmitInteraction(request.InteractionId, request.AnsweringPlayerId, request.StateRevision, answer, out string error), Is.False);
            Assert.That(error, Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredExplorationSelectionReason));
            Assert.That(JsonConvert.SerializeObject(memory), Is.EqualTo(before));
            Assert.That(RouteTollEscrowService.QueryBalance(memory, 1).Reserved, Is.EqualTo(held ? 4 : 0));
            ExploreRecover(state, registry, "未退役新探索及托管记录合法对照");
        }
        [Test] public void RetiredExplorationAncestorStopsAlreadyAcceptedChildBeforeReservation()
        {
            Production(out var state, out var registry, out _); string root = StartExploreV3(state, registry, ExploreSelect);
            var q = Selection(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(q.InteractionId, 1, q.StateRevision,
                NormalizedValue.CreateArray(SelectionAnswer(q, "A-01", "A1", "A2").Select(NormalizedValue.CreateString).ToList()), out string error), Is.True, error);
            // 已提交但尚未执行的合法子节点不能借旧父包装继续；故障日志可新增，规则/随机/资金不能变。
            ExploreRoot(state, root).EffectTypeId = ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId;
            string rules = JsonConvert.SerializeObject(new { state.ItemZones, state.Players, state.Map, state.Decks, state.RandomRuntime });
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain(ContentPrimitiveEffectExecutor.RetiredExplorationSelectionReason));
            Assert.That(JsonConvert.SerializeObject(new { state.ItemZones, state.Players, state.Map, state.Decks, state.RandomRuntime }), Is.EqualTo(rules));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
        }
    }
}
