using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CleanupObligationTraversalTests
    {
        private static GameState State()
        {
            var state=new GameState(); var nodes=state.EffectRuntime.EffectNodes;
            for(int i=0;i<128;i++)nodes.Add(new EffectNodeRuntimeState { EffectId="unrelated."+i, ParentEffectId=i==0?"missing":"unrelated."+(i-1), Status=EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId="child.2",ParentEffectId="child.1",Status=EffectNodeStatus.Failed });
            nodes.Add(new EffectNodeRuntimeState { EffectId="root",Status=EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId="child.1",ParentEffectId="root",Status=EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId="cycle.1",ParentEffectId="cycle.2",Status=EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId="cycle.2",ParentEffectId="cycle.1",Status=EffectNodeStatus.Completed });
            return state;
        }
        private static UsageTrackResetProgress Progress() => new UsageTrackResetProgress { RoundId="round.test",RoundNumber=1,OwnerEffectId="owner",RequiredObligationIds=new List<string>{"root"} };
        private static (bool,string,string) Run(GameState state,UsageTrackResetProgress progress,bool original)
        {
            using var mode=original ? UsageTracks.UseFullCleanupObligationTraversalForDiagnostics() : UsageTracks.VerifyCleanupObligationTraversalForDiagnostics();
            try { bool result=UsageTracks.BarrierReady(state,progress,out string reason);return(result,reason,""); }
            catch(Exception e){return(false,"",e.GetType().FullName+"|"+e.Message);}
        }
        [Test]
        public void IndexedParentWalkMatchesOriginalOrderFailuresAndCyclesAndActuallyRuns()
        {
            var state=State();var expected=Progress();var current=Progress();int hits=0;
            var original=Run(state,expected,true);
            using(var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="usage.reset.obligation_index")hits++;}))
                Assert.That(Run(state,current,false),Is.EqualTo(original));
            Assert.That(JsonConvert.SerializeObject(current),Is.EqualTo(JsonConvert.SerializeObject(expected)));
            Assert.That(current.RequiredObligationIds,Is.EqualTo(new[]{"root","child.2","child.1"}));
            Assert.That(hits,Is.EqualTo(1));
        }
        [Test]
        public void SameRevisionAndCountsUseFreshParentAndCompletionFacts()
        {
            var state=State();var expected=Progress();var current=Progress();
            Assert.That(Run(state,current,false),Is.EqualTo(Run(state,expected,true)));
            state.EffectRuntime.EffectNodes[0].ParentEffectId="root";
            state.EffectRuntime.EffectNodes[0].Status=EffectNodeStatus.Running;
            Assert.That(Run(state,current,false),Is.EqualTo(Run(state,expected,true)));
            Assert.That(JsonConvert.SerializeObject(current),Is.EqualTo(JsonConvert.SerializeObject(expected)));
            state.EffectRuntime.EffectNodes[0].Status=EffectNodeStatus.Failed;
            Assert.That(Run(state,current,false),Is.EqualTo(Run(state,expected,true)));
        }
        [TestCase("duplicate")][TestCase("null")][TestCase("empty")][TestCase("missing")]
        public void MalformedOrMissingIdentifiersKeepOriginalResultAndException(string kind)
        {
            var state=State();var expected=Progress();var current=Progress();
            if(kind=="duplicate")state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState{EffectId="root",Status=EffectNodeStatus.Running});
            if(kind=="null")state.EffectRuntime.EffectNodes.Insert(0,null);
            if(kind=="empty")state.EffectRuntime.EffectNodes.Insert(0,new EffectNodeRuntimeState{EffectId=""});
            if(kind=="missing"){expected.RequiredObligationIds.Add("gone");current.RequiredObligationIds.Add("gone");}
            Assert.That(Run(state,current,false),Is.EqualTo(Run(state,expected,true)));
            Assert.That(JsonConvert.SerializeObject(current),Is.EqualTo(JsonConvert.SerializeObject(expected)));
        }
    }
}
