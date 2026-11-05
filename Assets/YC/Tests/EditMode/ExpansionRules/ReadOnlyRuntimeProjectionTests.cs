using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 数据投影隔离，不计正常页面／视觉验收。
    public sealed class ReadOnlyRuntimeProjectionTests
    {
        private sealed class Identity:IEqualityComparer<object>
        {
            internal static readonly Identity Instance=new Identity();
            public new bool Equals(object a,object b)=>ReferenceEquals(a,b);
            public int GetHashCode(object value)=>RuntimeHelpers.GetHashCode(value);
        }
        private static void Visit(object value,HashSet<object> visited,Action<object> found)
        {
            if(value==null)return;
            var type=value.GetType();
            if(type.IsPrimitive || type.IsEnum || value is string || value is decimal)return;
            if(value is Array empty && empty.Length==0)return;
            if(!type.IsValueType)
            {
                if(!visited.Add(value))return;
                found(value);
            }
            if(value is IList list){foreach(var item in list)Visit(item,visited,found);return;}
            for(var owner=type;owner!=null && owner!=typeof(object);owner=owner.BaseType)
                foreach(var field in owner.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                    Visit(field.GetValue(value),visited,found);
        }
        private static string Prepare(PaymentTestFixture fixture)
        {
            const string type="isolated.projection.node";
            fixture.Registry.Register(type,c=>EffectStepResult.Completed(Object("memo",NormalizedValue.CreateString("before"))));
            var engine=fixture.Engine;
            string id=engine.CreateRoot(EffectSpec.Create(type,playerId:1));
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            fixture.State=GameStateCloneService.DeepClone(fixture.State);
            return id;
        }
        [TestCase(false)][TestCase(true)]
        public void BorrowedHistoryMatchesFullCopyAndNoMutableRuntimeObjectEscapesIntoView(bool host)
        {
            using var fixture=new PaymentTestFixture();Prepare(fixture);
            var before=GameStateCloneService.DeepClone(fixture.State);int hits=0;
            GameStateView view;
            using(GameStateViewProjector.VerifyProjectionStateCopiesForDiagnostics())
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="projection.borrowed_runtime")hits++;}))
                view=GameStateViewProjector.Project(fixture.State,host?GameStateViewer.Host:GameStateViewer.Player(1));
            Assert.That(hits,Is.EqualTo(1),"必须实际使用只读历史路径，不能由未命中声称隔离通过");
            var history=new HashSet<object>(Identity.Instance);
            Visit(fixture.State.EffectRuntime,new HashSet<object>(Identity.Instance),value=>history.Add(value));
            Visit(view,new HashSet<object>(Identity.Instance),value=>Assert.That(history.Contains(value),Is.False,"返回视图不能共享可变规则历史："+value.GetType().FullName));
            Assert.That(view.Effects.Count,Is.GreaterThan(0));
            view.Effects[0].NormalizedArguments.StringValue="隔离写入";
            if(view.Events.Count>0)view.Events[0].Payload.StringValue="事件隔离写入";
            Assert.That(GameStateCloneService.AreEquivalent(fixture.State,before),Is.True);
        }
        [Test]
        public void SameRevisionReadsCurrentHistoryWithoutRetainingAnEarlierProjection()
        {
            using var fixture=new PaymentTestFixture();string id=Prepare(fixture);
            var first=GameStateViewProjector.ProjectForPlayer(fixture.State,1);
            int revision=fixture.State.EffectRuntime.StateRevision;
            fixture.State.EffectRuntime.EffectNodes.Find(n=>n.EffectId==id).NormalizedResult=Object("memo",NormalizedValue.CreateString("after"));
            using var verify=GameStateViewProjector.VerifyProjectionStateCopiesForDiagnostics();
            var second=GameStateViewProjector.ProjectForPlayer(fixture.State,1);
            Assert.That(fixture.State.EffectRuntime.StateRevision,Is.EqualTo(revision));
            Assert.That(Text(first.Effects.Find(n=>n.EffectId==id).NormalizedResult,"memo"),Is.EqualTo("before"));
            Assert.That(Text(second.Effects.Find(n=>n.EffectId==id).NormalizedResult,"memo"),Is.EqualTo("after"));
        }
        private sealed class OpaqueOptions:IReadOnlyList<CharacterSettlementOption>
        {
            public int Count=>0;
            public CharacterSettlementOption this[int index]=>throw new IndexOutOfRangeException();
            public IEnumerator<CharacterSettlementOption> GetEnumerator(){yield break;}
            IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        }
        [Test]
        public void OpaqueOptionCollectionsKeepTheOriginalCompleteStateIsolation()
        {
            using var fixture=new PaymentTestFixture();Prepare(fixture);int borrowed=0,full=0;
            using(GameStateCloneService.BeginTimingScope((phase,ticks)=>
                {if(phase=="projection.borrowed_runtime")borrowed++;if(phase=="projection.full_state_snapshot")full++;}))
                GameStateViewProjector.Project(fixture.State,GameStateViewer.Player(1),new OpaqueOptions());
            Assert.That(borrowed,Is.Zero);Assert.That(full,Is.EqualTo(1));
        }
    }
}
