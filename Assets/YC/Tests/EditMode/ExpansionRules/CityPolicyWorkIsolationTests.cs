using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Movement;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只验证扩展回调的工作图隔离，不计作正常移城或界面验收。
    public sealed class CityPolicyWorkIsolationTests
    {
        [Test]
        public void OrdinaryDestinationProviderCannotRetainARecycledWorkGraph()
        {
            using var fixture=new PaymentTestFixture();const string handler="isolated.city.provider",ability="isolated.city.ability";
            var declaration=new AbilitySourceDeclaration{DefinitionId="test.payment.source_0",AbilityId=ability,HandlerRef=handler,DeclarationVersion="1.0.0",Kind="ruleQuery",AuthorizationPolicy="controller",
                EffectiveZones=new List<string>{FindZone(fixture.State,"hand",1).TemplateId},QueryKinds=new List<string>{CityDestinationQueryService.QueryKind},PresentationRef="isolated.query"};
            fixture.Registry.RegisterAbilityHandler(handler,"isolated.hash",_=>Array.Empty<EffectSpec>());
            // 正式ruleQuery声明必须有纯处理器；本例的目的地查询不执行此正文。
            fixture.Registry.RegisterAbilityPureHandler(handler,(call,query)=>Object("queryId",NormalizedValue.CreateString(query.QueryId),"basisDigest",NormalizedValue.CreateString(query.FactsDigest),"items",NormalizedValue.CreateArray(new List<NormalizedValue>())));
            var held=new List<GameState>();int executions=0,firstRecycling=0;
            fixture.Registry.RegisterCityDestinationPolicyProvider(handler,"isolated.hash",(state,draft,source)=>{held.Add(state);return Array.Empty<CandidatePatch>();});
            fixture.Registry.FreezeAbilityCatalog(new[]{declaration});fixture.State.ContentDefinitions.AbilitySources=new List<AbilitySourceDeclaration>{declaration};
            var query=typeof(EffectRegistry).GetMethod("QueryCityDestinationExclusions",BindingFlags.Instance|BindingFlags.NonPublic);
            Func<EffectExecutionContext,EffectStepResult> execute=context=>
            {
                executions++;
                if(executions==1)
                {
                    query.Invoke(fixture.Registry,new object[]{context.State,new CandidateSetDraft{CandidateSetId="isolated.city.candidates",CandidateKind=CityDestinationQueryService.QueryKind,ExecutingPlayerId=1,CandidatePoolIds=new List<string>{"harbor"}},"isolated.digest"});
                    return EffectStepResult.Continue("queried");
                }
                context.State.FindPlayer(1).Score+=3;return EffectStepResult.Completed();
            };
            typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(fixture.Registry,new object[]{"isolated.city.executor",execute,EffectExecutorKind.IntrinsicFlow,"1.0.0"});
            fixture.State=GameStateCloneService.DeepClone(fixture.State);var engine=fixture.Engine;engine.CreateRoot(EffectSpec.Create("isolated.city.executor",playerId:1));
            using var verification=GameStateCloneService.VerifySchedulingPublicationCopies();
            using var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.recycled_execution_work" && executions==1)firstRecycling++;});
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,fixture.State.EffectRuntime.LastFaultMessage);
            Assert.That(held.Count,Is.GreaterThan(0),"必须实际调用普通目的地提供者");Assert.That(firstRecycling,Is.Zero);
            foreach(var old in held) { Assert.That(old,Is.Not.SameAs(fixture.State)); Assert.That(old.FindPlayer(1).Score,Is.Zero); }
            // 同一次查询的多个来源可看到同一快照；先全部核对，再修改它们。
            foreach(var old in held)old.FindPlayer(1).Score=999;
            Assert.That(fixture.State.FindPlayer(1).Score,Is.EqualTo(3));
        }
    }
}
