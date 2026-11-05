using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using YC.Application.Sessions;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ExecutionTimingCapacityTests
    {
        [Test]
        public void LateScoringCoverageStillRecordsAfterBoundedDetailsFillAndOverflowIsVisible()
        {
            var counters=new ExecutionTimingCounters { Enabled=true };
            for(int i=0;i<256;i++)counters.AddSample("isolated.detail."+i,1);
            counters.AddSample("isolated.overflow",1);
            counters.AddSample("isolated.detail.0",2);
            counters.AddSample("scoring.archive.descendants",3);
            counters.AddSample("cleanup.obligation.descendants",4);
            counters.AddSample("transfer.structural_preview",5);
            counters.AddSample("character.item_world.publish",6);
            var result=counters.Snapshot();
            Assert.That(result.Keys.Count(k=>k.StartsWith("isolated.detail.") && k.EndsWith(".calls")),Is.EqualTo(256));
            Assert.That(result.ContainsKey("isolated.overflow.calls"),Is.False);
            Assert.That(result["diagnostic.detail_dropped.calls"],Is.EqualTo(1));
            Assert.That(result["isolated.detail.0.calls"],Is.EqualTo(2));
            Assert.That(result["isolated.detail.0.ticks"],Is.EqualTo(3));
            Assert.That(result["scoring.archive.descendants.calls"],Is.EqualTo(1));
            Assert.That(result["scoring.archive.descendants.ticks"],Is.EqualTo(3));
            Assert.That(result["cleanup.obligation.descendants.calls"],Is.EqualTo(1));
            Assert.That(result["transfer.structural_preview.calls"],Is.EqualTo(1));
            Assert.That(result["character.item_world.publish.calls"],Is.EqualTo(1));
            Assert.That(result["character.item_world.publish.ticks"],Is.EqualTo(6));
        }
        [Test]
        public void DisabledOrNegativeSamplesDoNotCountAndSaturatedTicksCannotWrap()
        {
            var counters=new ExecutionTimingCounters();
            counters.AddSample("scoring.archive.descendants",1);
            counters.Enabled=true;
            counters.AddSample("scoring.archive.descendants",-1);
            Assert.That(counters.Snapshot()["scoring.archive.descendants.calls"],Is.Zero);
            counters.AddSample("scoring.archive.descendants",long.MaxValue);
            counters.AddSample("scoring.archive.descendants",long.MaxValue);
            var result=counters.Snapshot();
            Assert.That(result["scoring.archive.descendants.calls"],Is.EqualTo(2));
            Assert.That(result["scoring.archive.descendants.ticks"],Is.EqualTo(long.MaxValue));
        }
        [Test]
        public void ConcurrentFixedPhaseSamplesKeepEveryCallAndTick()
        {
            var counters=new ExecutionTimingCounters { Enabled=true };
            Parallel.For(0,8,_=>{for(int i=0;i<1000;i++)counters.AddSample("scoring.archive.descendants",100);});
            var result=counters.Snapshot();
            Assert.That(result["scoring.archive.descendants.calls"],Is.EqualTo(8000));
            Assert.That(result["scoring.archive.descendants.ticks"],Is.EqualTo(800000));
        }
    }
}
