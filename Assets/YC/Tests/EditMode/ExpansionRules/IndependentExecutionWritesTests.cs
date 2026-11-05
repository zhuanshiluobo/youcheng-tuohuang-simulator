using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离合同与故障边界测试，不计正常规则候选或界面覆盖。
    public sealed class IndependentExecutionWritesTests
    {
        private static void Register(EffectRegistry registry,string id,Func<EffectExecutionContext,EffectStepResult> execute) =>
            typeof(EffectRegistry).GetMethod("RegisterWithIndependentStateWrites",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(registry,new object[]{id,execute,EffectExecutorKind.IntrinsicFlow,"1.0.0"});

        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void TheNarrowContractStopsAtAnOrdinaryStateCallback(bool exposed,bool verify)
        {
            using var fixture=new PaymentTestFixture();var held=new List<GameState>();int executions=0,firstProofs=0,firstRecycling=0;
            const string handler="isolated.independent.callback";
            Func<AbilityActionCallContext,IList<EffectSpec>> body=call=>{if(exposed)held.Add(call.State);return Array.Empty<EffectSpec>();};
            if(exposed)fixture.Registry.RegisterAbilityHandler(handler,"isolated.hash",body);
            else fixture.Registry.RegisterNonRetainedSourceHandler(handler,"isolated.hash",body);
            var construct=typeof(EffectRegistry).GetMethod("ConstructAbilityBody",BindingFlags.Instance|BindingFlags.NonPublic);
            Register(fixture.Registry,"isolated.independent.executor",context=>
            {
                executions++;
                if(executions==1)
                {
                    construct.Invoke(fixture.Registry,new object[]{handler,new AbilityActionCallContext{State=context.State,OwnerNode=context.Node,SourceRef=new AbilitySourceRef{AbilityId="isolated.ability"}}});
                    return EffectStepResult.Continue("after_callback");
                }
                context.State.FindPlayer(1).Score+=3;return EffectStepResult.Completed();
            });
            fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(EffectSpec.Create("isolated.independent.executor",playerId:1));
            using var verification=verify?EffectTreeExecutor.VerifyIndependentExecutionWritesForDiagnostics():null;
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.independent_execution_writes" && executions==1)firstProofs++;if(phase=="kernel.recycled_execution_work" && executions==1)firstRecycling++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(firstProofs,Is.Zero,"不保留引用的回调也不能承接更强的独立引用写入证明");
            Assert.That(firstRecycling>0,Is.EqualTo(!exposed),"重新检查无别名后仍应复用未向外暴露的工作图");Assert.That(executions,Is.EqualTo(2));
            Assert.That(fixture.State.FindPlayer(1).Score,Is.EqualTo(3));
            foreach(var old in held){Assert.That(old.FindPlayer(1).Score,Is.Zero);old.FindPlayer(1).Score=999;}
            Assert.That(fixture.State.FindPlayer(1).Score,Is.EqualTo(3));
        }

        [Test]
        public void VerificationRejectsAnIncorrectIndependentReferenceDeclaration()
        {
            using var fixture=new PaymentTestFixture();
            Register(fixture.Registry,"isolated.incorrect.unique_contract",context=>
            {context.Node.ChildEffectIds=context.Node.BlockerIds;return EffectStepResult.Continue("incorrect_alias");});
            fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(EffectSpec.Create("isolated.incorrect.unique_contract",playerId:1));
            using var verification=EffectTreeExecutor.VerifyIndependentExecutionWritesForDiagnostics();
            var report=engine.RunUntilQuiescent();
            Assert.That(report.Faulted,Is.True,"带共享列表的错误合同必须触发校验故障");
            Assert.That(fixture.State.EffectRuntime.LastFaultMessage,Does.Contain("独立引用写入合同"));
        }
    }
}
