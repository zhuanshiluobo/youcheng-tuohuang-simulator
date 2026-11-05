using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class HotseatPerformanceGuardTests
    {
        [Test]
        public void ActionLogSnapshotReadsEachProjectionOnceAndOwnsItsCapturedValues()
        {
            var state = new GameState();
            var influence = new InfluencePlacement { PlayerId = 1, SlotId = "location:A-01:0", LocationId = "A-01" };
            var current = new List<InfluencePlacement> { influence };
            var supply = new List<string> { "facility.first", "" };
            int influenceReads = 0, supplyReads = 0;
            state.Map.Influences.Bind(() => { influenceReads++; return current; });
            state.Decks.FacilitySupply.Bind(() => { supplyReads++; return supply; });
            var type = typeof(GameSession).Assembly.GetType("YC.Application.Sessions.PublicActionStateSnapshot", true);
            var capture = type.GetMethod("Capture");
            var first = capture.Invoke(null, new object[] { state });
            Assert.That(influenceReads, Is.EqualTo(1)); Assert.That(supplyReads, Is.EqualTo(1));
            var captured = (System.Collections.IDictionary)type.GetField("Influences").GetValue(first);
            var before = captured[influence.SlotId];
            influence.PlayerId = 2; current.Clear(); supply.Clear();
            Assert.That((int)before.GetType().GetField("PlayerId").GetValue(before), Is.EqualTo(1));
            Assert.That((HashSet<string>)type.GetField("FacilitySupply").GetValue(first), Does.Contain("facility.first"));
            var next = capture.Invoke(null, new object[] { state });
            Assert.That(((System.Collections.IDictionary)type.GetField("Influences").GetValue(next)).Count, Is.Zero);
            Assert.That(influenceReads, Is.EqualTo(2)); Assert.That(supplyReads, Is.EqualTo(2));
        }

        [Test]
        public void DisplayStringFastBoundKeepsUtf8BudgetAtAsciiCjkEmojiAndReplacementEdges()
        {
            foreach(var text in new[]{new string('a',32768),new string('a',32769),new string('中',10922),
                new string('中',10923),string.Concat(Enumerable.Repeat("😀",8192)),string.Concat(Enumerable.Repeat("😀",8193)),
                new string('\ud800',10922),new string('\ud800',10923)})
                Assert.That(GameplayPresentationNormalizer.TryValidate(text,out _),Is.EqualTo(Encoding.UTF8.GetByteCount(text)<=32768));
        }

        [Test]
        public void RuntimeEmptyCollectionAndInteractionIndexKeepOriginalRejections()
        {
            var state=new GameState{GameId="isolated.runtime.validation"};var registry=new EffectRegistry();
            registry.Register("isolated.noop",context=>EffectStepResult.Completed());
            var engine=new EffectTreeExecutor(state,registry);string id=engine.CreateRoot(EffectSpec.Create("isolated.noop"));
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False);
            var runtime=state.EffectRuntime;
            Assert.That(runtime.TryValidate(out var reason),Is.True,reason);
            var node=engine.GetNode(id);node.ShuffleInputItemIds.AddRange(new[]{"same","same"});
            Assert.That(runtime.TryValidate(out reason),Is.False);Assert.That(reason,Is.EqualTo("洗牌输入集合损坏。"));
            node.ShuffleInputItemIds.Clear();
            var request=new InteractionRequest{RequestId="request",OwnerEffectId=id,Status="broken"};
            runtime.InteractionRequests.Add(request);
            var blocker=new EffectBlockerRuntimeState{BlockerId="blocker",OwnerEffectId=id,InteractionRequestId="missing"};
            runtime.Blockers.Add(blocker);
            Assert.That(runtime.TryValidate(out reason),Is.False);
            Assert.That(reason,Is.EqualTo("Effect blocker 引用了不存在的节点/交互或包含重复 ID。"));
            blocker.InteractionRequestId="request";
            Assert.That(runtime.TryValidate(out reason),Is.False);Assert.That(reason,Is.EqualTo("InteractionRequest 为空或答案非法。"));
        }
    }
}
