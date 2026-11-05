using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离回调引用生命周期检查，不计正常局或界面性能。
    public sealed class CallbackWorkIsolationTests
    {
        [TestCase(false)][TestCase(true)]
        public void DefaultPureQueryMayRetainItsOldWorkWhileExplicitDetachedQueryCanRecycle(bool detached)
        {
            using var f=new PaymentTestFixture();const string queryKind="isolated.callback.query",handler="isolated.query.handler";
            var declaration=new AbilitySourceDeclaration { DefinitionId="test.payment.source_0",AbilityId="isolated.query.ability",HandlerRef=handler,DeclarationVersion="1.0.0",Kind="ruleQuery",AuthorizationPolicy="controller",EffectiveZones=new List<string>{FindZone(f.State,"hand",1).TemplateId},QueryKinds=new List<string>{queryKind},PresentationRef="isolated.query" };
            f.Registry.RegisterAbilityHandler(handler,"isolated.query.hash",_=>Array.Empty<EffectSpec>());
            var held=new List<GameState>();int queries=0;
            Func<AbilityActionCallContext,AbilityQueryContext,NormalizedValue> query=(call,facts)=> {
                queries++;if(!detached)held.Add(call.State);
                return Object("queryId",NormalizedValue.CreateString(facts.QueryId),"basisDigest",NormalizedValue.CreateString(facts.FactsDigest),"items",NormalizedValue.CreateArray(new List<NormalizedValue>()));
            };
            if(detached)f.Registry.RegisterNonRetainedAbilityPureHandler(handler,query);else f.Registry.RegisterAbilityPureHandler(handler,query);
            f.Registry.RegisterAbilityQueryContract(queryKind,(facts,source,items)=>"");
            f.Registry.FreezeAbilityCatalog(new[]{declaration});f.State.ContentDefinitions.AbilitySources=new List<AbilitySourceDeclaration>{declaration};
            f.State=GameStateCloneService.DeepClone(f.State);int executions=0,recycledAfterQuery=0;
            Func<EffectExecutionContext,EffectStepResult> body=context=> {
                executions++;
                if(executions==1)
                {
                    var source=context.Registry.QuerySources(context.State,1,"ruleQuery",queryKind:queryKind).Single();
                    var result=context.Registry.QueryRules(context.State,new AbilityQueryContext { QueryKind=queryKind,QueryId="isolated.query",FactsDigest="isolated.facts",ContentVersion=context.State.ContentPackHash,MapId=context.State.MapId,StateRevision=context.State.EffectRuntime.StateRevision,SubjectPlayerId=1,SourceRef=source,Facts=Object() });
                    Assert.That(result.Count,Is.EqualTo(1));return EffectStepResult.Continue("queried");
                }
                context.State.FindPlayer(1).Score+=2;return EffectStepResult.Completed();
            };
            typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f.Registry,new object[]{"isolated.callback.executor",body,EffectExecutorKind.IntrinsicFlow,"v1"});
            var engine=f.Engine;engine.CreateRoot(EffectSpec.Create("isolated.callback.executor",playerId:1));
            using(var verify=GameStateCloneService.VerifySchedulingPublicationCopies())
            using(var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.recycled_execution_work" && executions==1)recycledAfterQuery++;}))
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,f.State.EffectRuntime.LastFaultMessage);
            Assert.That(queries,Is.GreaterThan(0));Assert.That(executions,Is.EqualTo(2));Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(2));
            Assert.That(recycledAfterQuery>0,Is.EqualTo(detached));
            foreach(var old in held)old.FindPlayer(1).Score=999;
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(2),"普通回调持有的旧私有图不能改写已发布结果");
        }
        [TestCase(false)][TestCase(true)]
        public void BuiltInInfluenceCanRecycleAndCustomPlacementStillGetsAnIsolatedOldWork(bool custom)
        {
            var map=new GameMapDefinition { MapId="test.transfer_map",MinPlayers=2,MaxPlayers=4,
                Regions=new List<MapRegionDefinition>{new MapRegionDefinition{RegionId="harbor_region"}},
                Locations=new List<MapLocationDefinition>{new MapLocationDefinition{LocationId="harbor",RegionId="harbor_region",CanDockCity=true,InfluenceSlotCount=2},new MapLocationDefinition{LocationId="dock",RegionId="harbor_region",CanDockCity=true,InfluenceSlotCount=2}},
                Routes=new List<MapRouteDefinition>{new MapRouteDefinition{RouteId="lane",RegionId="harbor_region",FromLocationId="harbor",ToLocationId="dock",InfluenceSlotCount=2,BaseCost=2}} };
            using var f=new PaymentTestFixture(mapOverride:map);f.State=GameStateCloneService.DeepClone(f.State);
            var service=new InfluenceService(f.State,new MapQueryService(map));var held=new List<GameState>();
            if(custom)service.PlacementAllowed=(state,slot)=>{held.Add(state);return true;};
            InfluenceEffectExecutor.Register(f.Registry,service);int recycled=0;
            var engine=f.Engine;string root=engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1,InfluenceService.GetRouteSlotId("lane",0)));
            using(var verify=GameStateCloneService.VerifySchedulingPublicationCopies())
            using(var timing=GameStateCloneService.BeginTimingScope((phase,ticks)=>{if(phase=="kernel.recycled_executor."+InfluenceEffectTypeIds.PlaceInfluence)recycled++;}))
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,f.State.EffectRuntime.LastFaultMessage);
            var completed=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root);
            Assert.That(completed.Status,Is.EqualTo(EffectNodeStatus.Completed),completed.FailureReason);
            Assert.That(recycled>0,Is.EqualTo(!custom));if(custom)Assert.That(held.Count,Is.GreaterThan(0));
            int score=f.State.FindPlayer(1).Score;foreach(var old in held)old.FindPlayer(1).Score=999;
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score));
        }
    }
}
