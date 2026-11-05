using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只检查续接元数据与实际扩展回调边界；不计作正常界面验收。
    public sealed class ContinuationWorkIsolationTests
    {
        [TestCase(false,false)][TestCase(false,true)][TestCase(true,false)][TestCase(true,true)]
        public void ContinuationMetadataDoesNotExposeWorkButOrdinaryCallbackStillDoes(bool detached,bool nested)
        {
            using var fixture=new PaymentTestFixture();
            const string executor="isolated.cont.executor",parent="isolated.cont.parent",after="isolated.cont.after",handler="isolated.cont.completed";
            string hash=new string('a',64);var held=new List<GameState>();int executions=0,firstRecycling=0,parentRecycling=0,callbackCalls=0;
            Func<EffectExecutionContext,EffectStepResult> body=context=>
            {
                executions++;
                if(executions==1)return EffectStepResult.Continue("second");
                context.State.FindPlayer(1).Score+=3;return EffectStepResult.Completed();
            };
            void Register(string id,Func<EffectExecutionContext,EffectStepResult> fn)=>typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(fixture.Registry,new object[]{id,fn,EffectExecutorKind.IntrinsicFlow,"v1"});
            Register(executor,body);Register(after,c=>{c.State.FindPlayer(1).Score+=7;return EffectStepResult.Completed();});
            EffectSpec Child()
            {
                var spec=EffectSpec.Create(executor,playerId:1);spec.CompletionHandlerId=handler;spec.ContinuationContentId="isolated.content";
                spec.ContinuationContentInstanceId="isolated.instance";spec.ContinuationAbilityId="isolated.ability";spec.ContinuationDefinitionVersion="v1";spec.ContinuationContentHash=hash;return spec;
            }
            Register(parent,c=>c.Node.FlowStage==""?EffectStepResult.Continue("child").AddChild(Child()):EffectStepResult.Completed());
            var registration=new EffectEventHandlerRegistration("isolated.cont.subscription","EffectCompleted","",handler,c=>
                {callbackCalls++;if(!detached)held.Add(c.State);return new[]{EffectSpec.Create(after,playerId:1)};},
                EffectHandlerRole.Continuation,contentInstanceId:"isolated.instance",abilityId:"isolated.ability",definitionVersion:"v1",contentHash:hash,contentId:"isolated.content");
            if(detached)registration.WithNonRetainedStateContract(()=>true);
            fixture.Registry.RegisterEventHandler(registration);
            fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(nested?EffectSpec.Create(parent,playerId:1):Child());
            using var verification=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>
            {
                if(phase=="kernel.recycled_executor."+executor && executions==1)firstRecycling++;
                if(phase=="kernel.recycled_executor."+parent && executions==0)parentRecycling++;
            });
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(firstRecycling,Is.GreaterThan(0),"非终态步骤不应仅因未来存在续接就丢弃独占图");
            if(nested)Assert.That(parentRecycling,Is.GreaterThan(0),"只挂接带续接元数据的子节点没有暴露工作状态");
            Assert.That(callbackCalls,Is.EqualTo(1));Assert.That(held.Count,Is.EqualTo(detached?0:1));
            foreach(var old in held){Assert.That(old.FindPlayer(1).Score,Is.EqualTo(3));old.FindPlayer(1).Score=999;}
            Assert.That(fixture.State.FindPlayer(1).Score,Is.EqualTo(10));
            Assert.That(fixture.State.EffectRuntime.ContinuationBindings.Count,Is.EqualTo(1));
            Assert.That(fixture.State.EffectRuntime.ContinuationBindings[0].Status,Is.EqualTo(ContinuationBindingStatus.Completed));
        }
    }
}
