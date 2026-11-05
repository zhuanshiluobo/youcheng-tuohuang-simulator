using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class SchedulingDependencyDeltaTests
    {
        private static (GameState,int,bool) Run(bool full,bool fail)
        {
            var state=new GameState { GameId="isolated.status.dependencies" };
            state.Players.Add(new PlayerState { PlayerId=1 });
            state.Players.Add(new PlayerState { PlayerId=2 });
            var registry=new EffectRegistry();
            registry.Register("isolated.dependencies.parent",c=>
            {
                if(c.Node.FlowStage=="children")return EffectStepResult.Completed();
                return EffectStepResult.Continue("children")
                    .AddIndependentChildren("parallel",new List<EffectSpec> {
                        EffectSpec.Create("isolated.dependencies.child",playerId:1),
                        EffectSpec.Create("isolated.dependencies.child",playerId:2) })
                    .AddChild(EffectSpec.Create("isolated.dependencies.last",playerId:1));
            });
            registry.Register("isolated.dependencies.child",c=>
            {c.State.FindPlayer(c.Node.PlayerId).Score++;return EffectStepResult.Completed();});
            registry.Register("isolated.dependencies.last",c=>
            {
                if(fail)throw new InvalidOperationException("isolated_dependency_fault");
                c.State.FindPlayer(1).Score+=4;return EffectStepResult.Completed();
            });
            var engine=new EffectTreeExecutor(state,registry);
            engine.CreateRoot(EffectSpec.Create("isolated.dependencies.parent",playerId:1));
            int hits=0;
            using var original=full ? GameStateCloneService.UseFullSchedulingValidationForDiagnostics() : null;
            using var verify=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>
                {if(phase=="kernel.validation.status_dependencies_delta")hits++;});
            var report=engine.RunUntilQuiescent();
            return(state,hits,report.Faulted);
        }
        [TestCase(false)][TestCase(true)]
        public void IndependentChildrenAndLaterSequentialBarrierKeepAllFieldsAndFaultBoundary(bool fail)
        {
            var reference=Run(true,fail);var current=Run(false,fail);
            Assert.That(current.Item3,Is.EqualTo(fail));
            Assert.That(current.Item3,Is.EqualTo(reference.Item3));
            Assert.That(GameStateCloneService.AreEquivalent(current.Item1,reference.Item1),Is.True);
            Assert.That(reference.Item2,Is.Zero);
            Assert.That(current.Item2,Is.GreaterThan(0),"必须实际执行状态依赖增量校验");
            Assert.That(current.Item1.FindPlayer(1).Score,Is.EqualTo(fail?1:5));
            Assert.That(current.Item1.FindPlayer(2).Score,Is.EqualTo(1));
        }
    }
}
