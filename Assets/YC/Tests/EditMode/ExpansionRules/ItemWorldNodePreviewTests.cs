using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 私有预演隔离合同，非正常游戏页面或性能目标覆盖。
    public sealed class ItemWorldNodePreviewTests
    {
        private static GameState Preview(GameState source,out GameState reference)
        {
            object[] args={source,"preview-node",null};
            var result=(GameState)typeof(GameStateCloneService).GetMethod("CreateItemWorldNodePreview",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);
            reference=(GameState)args[2];return result;
        }
        private static GameState Source(PaymentTestFixture fixture,bool alias=false)
        {
            var source=GameStateCloneService.DeepClone(fixture.State);
            var node=new EffectNodeRuntimeState {EffectId="preview-node",PlayerId=1,Status=EffectNodeStatus.Running};
            if(alias)node.ChildEffectIds=node.BlockerIds;
            source.EffectRuntime.EffectNodes.Add(node);source.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState {EffectId="history",Status=EffectNodeStatus.Completed});return source;
        }
        [TestCase(false)][TestCase(true)]
        public void OnlyCurrentNodeAndWholeItemWorldAreIndependentAndPublicCloneRemainsIndependent(bool full)
        {
            using var fixture=new PaymentTestFixture();var source=Source(fixture);
            using var mode=full?GameStateCloneService.UseFullItemWorldNodePreviewsForDiagnostics():null;
            using var verify=GameStateCloneService.VerifyItemWorldNodePreviewsForDiagnostics();
            int hits=0;using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="transfer.node_preview")hits++;});
            var preview=Preview(source,out var reference);Assert.That(reference,Is.Not.Null);Assert.That(GameStateCloneService.AreEquivalent(preview,reference),Is.True);
            Assert.That(hits,Is.EqualTo(full?0:1));Assert.That(preview.EffectRuntime,Is.Not.SameAs(source.EffectRuntime));
            Assert.That(preview.EffectRuntime.EffectNodes,Is.Not.SameAs(source.EffectRuntime.EffectNodes));
            var node=preview.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="preview-node");node.FlowStage="changed";
            Assert.That(source.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="preview-node").FlowStage,Is.Empty);
            preview.FindPlayer(1).Score+=7;Assert.That(source.FindPlayer(1).Score,Is.EqualTo(reference.FindPlayer(1).Score));
            if(!full)Assert.That(preview.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="history"),Is.SameAs(source.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="history")));
            var publicClone=GameStateCloneService.DeepClone(preview);Assert.That(publicClone.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="history"),Is.Not.SameAs(preview.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="history")));
        }
        [TestCase(false)][TestCase(true)]
        public void FullAndVerificationDiagnosticModesComposeInEitherOrderAndRestoreOnExit(bool fullFirst)
        {
            using var fixture=new PaymentTestFixture();var source=Source(fixture);int hits=0;
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="transfer.node_preview")hits++;});
            using(fullFirst?GameStateCloneService.UseFullItemWorldNodePreviewsForDiagnostics():GameStateCloneService.VerifyItemWorldNodePreviewsForDiagnostics())
            using(fullFirst?GameStateCloneService.VerifyItemWorldNodePreviewsForDiagnostics():GameStateCloneService.UseFullItemWorldNodePreviewsForDiagnostics())
            {
                Preview(source,out var reference);Assert.That(reference,Is.Not.Null);Assert.That(hits,Is.Zero);
            }
            Preview(source,out var restoredReference);Assert.That(restoredReference,Is.Null);Assert.That(hits,Is.EqualTo(1));
        }
        [Test]
        public void AliasedSourceUsesOriginalFullClone()
        {
            using var fixture=new PaymentTestFixture();var source=Source(fixture,true);int hits=0;
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="transfer.node_preview")hits++;});
            var preview=Preview(source,out _);Assert.That(hits,Is.Zero);
            Assert.That(preview.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="history"),Is.Not.SameAs(source.EffectRuntime.EffectNodes.Find(n=>n.EffectId=="history")));
        }
        [Serializable] private sealed class CallbackList<T> : List<T>,IEnumerable<T>
        {
            [NonSerialized] internal Action Callback;
            IEnumerator<T> IEnumerable<T>.GetEnumerator(){Callback?.Invoke();return base.GetEnumerator();}
            IEnumerator IEnumerable.GetEnumerator(){Callback?.Invoke();return base.GetEnumerator();}
        }
        [TestCase(false)][TestCase(true)]
        public void BorrowedDerivedCollectionsIncludingUnusedNormalizedContainersCannotAcquireQualification(bool unused)
        {
            using var fixture=new PaymentTestFixture();var source=Source(fixture);int calls=0;
            if(unused)source.EffectRuntime.EffectNodes[0].NormalizedArguments.Items=new CallbackList<NormalizedValue>{Callback=()=>calls++};
            else source.EffectRuntime.RuleEvents=new CallbackList<RuleEvent>{Callback=()=>calls++};
            var qualify=typeof(GameStateCloneService).GetMethod("HasItemWorldNodePreviewShape",BindingFlags.NonPublic|BindingFlags.Static);
            Assert.That((bool)qualify.Invoke(null,new object[]{source}),Is.False);Assert.That(calls,Is.Zero,"资格预检不能运行扩展枚举。");
        }
    }
}
