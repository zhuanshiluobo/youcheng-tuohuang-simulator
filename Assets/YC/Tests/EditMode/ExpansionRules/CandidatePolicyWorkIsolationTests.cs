using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离候选回调生命周期，不计正常对局或界面验收。
    public sealed class CandidatePolicyWorkIsolationTests
    {
        [Test]
        public void APlainCandidatePolicyCanKeepItsOldStateWithoutSeeingFutureExecutionOrChangingPublication()
        {
            var state=new GameState { GameId="isolated.candidate.callback" };
            state.Players.Add(new PlayerState{PlayerId=1});var registry=new EffectRegistry();
            var policies=new CandidatePolicyRegistry();GameState held=null;int callbacks=0,executions=0;
            policies.Register(new CandidatePolicyRegistration("isolated.policy","isolated",context=> {
                callbacks++;held=context.State;return Array.Empty<CandidatePatch>();
            }));
            Func<EffectExecutionContext,EffectStepResult> body=context=> {
                executions++;
                if(executions==1)
                {
                    var draft=new CandidateSetDraft { CandidateSetId="isolated.set",Version=1,CandidateKind="isolated",
                        CandidatePoolIds=new List<string>{"choice"},DefaultCandidateIds=new List<string>{"choice"},CurrentIds=new List<string>{"choice"},
                        Context=NormalizedValue.CreateObject(new List<NormalizedValueEntry>()) };
                    var result=new CandidateSetResolver().Resolve(context.State,draft,policies,context.State.EffectRuntime.StateRevision);
                    Assert.That(result.Succeeded,Is.True,result.Diagnostic);
                    Assert.That(result.FinalCandidateIds,Is.EqualTo(new[]{"choice"}));
                    return EffectStepResult.Continue("queried");
                }
                context.State.Players[0].Score=5;return EffectStepResult.Completed();
            };
            typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(registry,new object[]{"isolated.candidate.executor",body,EffectExecutorKind.IntrinsicFlow,"v1"});
            var engine=new EffectTreeExecutor(state,registry);engine.CreateRoot(EffectSpec.Create("isolated.candidate.executor",playerId:1));
            using(GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(callbacks,Is.EqualTo(1));Assert.That(executions,Is.EqualTo(2));Assert.That(held,Is.Not.Null);
            Assert.That(held.Players[0].Score,Is.Zero,"普通候选回调持有的旧图不能被后续执行继续改写");
            held.Players[0].Score=999;Assert.That(state.Players[0].Score,Is.EqualTo(5));
        }
    }
}
