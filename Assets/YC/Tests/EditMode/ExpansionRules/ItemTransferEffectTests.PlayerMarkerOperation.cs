using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
using static YC.Tests.EditMode.ExpansionRules.EnterpriseEffectTests;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static EffectSpec DedicatedMarker(int player,NormalizedValue leg)
        {
            var spec=MarkerSpecV4(player,leg);spec.EffectTypeId=PlayerMarkerOperation.TypeId;
            Value(spec.NormalizedArguments,"apiVersion").IntegerValue=1;spec.NormalizedArguments.Properties.RemoveAll(p=>p.Name=="operationProfileId");return spec;
        }
        [TestCase(1)][TestCase(2)]
        public void PlayerMarkerDedicatedMatchesSharedAndReturnsOriginalSupplyWithoutReset(int carrierPlayer)
        {
            var initial=State();var registry=Registry();PlayerMarkerOperation.Register(registry);
            string card=Ids(initial,"hand",carrierPlayer)[0],marker=Ids(initial,"supply",2)[0],supply=Zone(initial,"supply",2).ZoneId;
            string storage=MarkerStorageV4(initial,card);initial.FindPlayer(1).UsedSpecialActionIdsThisRound.Add("preserve");
            var state=GameStateCloneService.DeepClone(initial);var plain=GameStateCloneService.DeepClone(initial);
            var parent=Run(state,DedicatedMarker(1,MarkerLegV4(marker,supply,storage)),registry);var body=SpecializedItemMovement.Body(state,parent);
            Run(plain,MarkerSpecV4(1,MarkerLegV4(marker,supply,storage)),registry);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(JsonConvert.SerializeObject(plain.ItemZones)));
            Assert.That(parent.PositionChanges,Is.Empty);Assert.That(body.ItemMovementV4.Receipt.Result.MovedItemIds,Is.EqualTo(new[]{marker}));
            Assert.That(state.FindPlayer(1).UsedSpecialActionIdsThisRound,Is.EqualTo(new[]{"preserve"}));
            state=Restore(state,registry);parent=Run(state,DedicatedMarker(1,MarkerLegV4(marker,storage,null)),registry);
            Assert.That(Ids(state,"supply",2),Does.Contain(marker));Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==marker).OwnerSubject.PlayerId,Is.EqualTo(2));
            Assert.That(Number(parent.NormalizedResult,"movedCount"),Is.EqualTo(1));Restore(state,registry);
        }
        [Test]
        public void PlayerMarkerDedicatedLuaHasStrictTypeAndCannotOverrideCommonProfile()
        {
            var state=State();var registry=Registry();PlayerMarkerOperation.Register(registry);
            string marker=Ids(state,"supply",2)[0],storage=MarkerStorageV4(state,Ids(state,"hand",1)[0]);
            var spec=DedicatedMarker(1,MarkerLegV4(marker,Zone(state,"supply",2).ZoneId,storage));
            string source="return function(ctx) return {Effect.OperatePlayerMarker({apiVersion=1,player=ctx.playerId,operation='transfer',selector={mode='explicit',itemIds={'"+marker+"'},expectedSource={zoneId='"+Zone(state,"supply",2).ZoneId+"'}},destination={zoneId='"+storage+"'},insertion={kind='unordered'},requiredCount=1})} end";
            var invocation=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("marker.new","marker.new","marker.new","1",source,LuaContentHasher.ComputeSha256(source)),new LuaInvocationContext("marker.new","test","p1",0,"1",2,0,0));
            Assert.That(invocation.IsSuccess,Is.True,invocation.Diagnostic);Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var fault),Is.True,fault?.Diagnostic);
            Assert.That(Run(state,specs.Single(),registry).Status,Is.EqualTo(EffectNodeStatus.Completed));
            var bad=spec.NormalizedArguments.Clone();bad.Properties.Add(new NormalizedValueEntry{Name="operationProfileId",Value=NormalizedValue.CreateString("core.token_storage")});Assert.That(PlayerMarkerOperation.ValidateArguments(bad),Is.Not.Empty);
            var wrong=DedicatedMarker(1,MarkerLegV4(Ids(state,"hand",1)[0],Zone(state,"hand",1).ZoneId,storage));
            Assert.That(Run(state,wrong,registry).FailureReason,Is.EqualTo("player_marker_type_rejected"));
        }
        [TestCase(false)][TestCase(true)]
        public void PlayerMarkerEmptyDestinationIsSharedAndRecoveryUsesOriginalOccupancy(bool dedicated)
        {
            var state=State();var registry=Registry();PlayerMarkerOperation.Register(registry);
            var ids=Ids(state,"supply",2).Take(2).ToArray();string source=Zone(state,"supply",2).ZoneId,storage=MarkerStorageV4(state,Ids(state,"hand",1)[0]);
            Func<string,bool,EffectSpec> spec=(id,empty)=>
            {
                var leg=MarkerLegV4(id,source,storage);
                if(empty){var dest=Value(leg,"destination");dest.Properties.Add(new NormalizedValueEntry{Name="mustBeEmpty",Value=NormalizedValue.CreateBoolean(true)});dest.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}
                return dedicated?DedicatedMarker(1,leg):MarkerSpecV4(1,leg);
            };
            var moved=Run(state,spec(ids[0],true),registry);Assert.That(moved.Status,Is.EqualTo(EffectNodeStatus.Completed));
            var rejected=Run(state,spec(ids[1],true),registry);Assert.That(rejected.FailureReason,Is.EqualTo("destination_rejected"));Assert.That(rejected.PositionChanges,Is.Empty);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==ids[1]).ZoneId,Is.EqualTo(source));
            Assert.That(Run(state,spec(ids[1],false),registry).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.ItemZones.Items.Count(i=>i.ZoneId==storage),Is.EqualTo(2));Restore(state,registry);
        }
        [TestCase(false)][TestCase(true)]
        public void PlayerMarkerOldCleanupCannotBypassCompilerThroughManualOrNestedSpecs(bool nested)
        {
            var leaf=LuaEffectSpec.FromArguments(PlayerMarkerOperation.TypeId,Object("operation",NormalizedValue.CreateString("round_cleanup"),"executingPlayer",NormalizedValue.CreateString("1")));
            var spec=nested?LuaEffectSpec.FromEffectGroups(EffectTypeIds.Condition,Object(),new IReadOnlyList<LuaEffectSpec>[]{new[]{leaf},Array.Empty<LuaEffectSpec>()}):leaf;
            var success=typeof(LuaInvocationResult).GetMethods(BindingFlags.NonPublic|BindingFlags.Static).Single(m=>m.Name=="Success"&&m.GetParameters().Length==2);
            var invocation=(LuaInvocationResult)success.Invoke(null,new object[]{ShuffleScript("return function(ctx) return {} end"),new[]{spec}});
            var catalog=new LuaEffectTypeCatalog();var entries=(Dictionary<string,LuaEffectTypeRegistration>)typeof(LuaEffectTypeCatalog).GetField("registrations",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(catalog);
            entries[PlayerMarkerOperation.TypeId]=new LuaEffectTypeRegistration(PlayerMarkerOperation.TypeId,null,item=>item.RawArguments.Clone());
            Assert.That(new LuaEffectSpecCompiler(catalog).TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out _,out var fault),Is.False);Assert.That(fault.Diagnostic,Does.Contain(PlayerMarkerOperation.CleanupHostOnlyReason));
            var lua=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx) return {Effect.OperatePlayerMarker({operation='round_cleanup',executingPlayer=ctx.playerId})} end"),new LuaInvocationContext("marker.old","test","p1",0,"1.0.0",2,0,0));
            Assert.That(lua.IsSuccess,Is.False);Assert.That(lua.Diagnostic,Does.Contain(PlayerMarkerOperation.CleanupHostOnlyReason));
            entries[CityStyleSpecialActionEffectTypeIds.RoundCleanup]=new LuaEffectTypeRegistration(CityStyleSpecialActionEffectTypeIds.RoundCleanup,null,item=>item.RawArguments.Clone());
            var systemLeaf=LuaEffectSpec.FromArguments(CityStyleSpecialActionEffectTypeIds.RoundCleanup,Object());
            var systemSpec=nested?LuaEffectSpec.FromEffectGroups(EffectTypeIds.Condition,Object(),new IReadOnlyList<LuaEffectSpec>[]{new[]{systemLeaf},Array.Empty<LuaEffectSpec>()}):systemLeaf;
            var systemInvocation=(LuaInvocationResult)success.Invoke(null,new object[]{ShuffleScript("return function(ctx) return {} end"),new[]{systemSpec}});
            Assert.That(new LuaEffectSpecCompiler(catalog).TryCompile(systemInvocation,new LuaEffectCompilationContext{PlayerId=1},out _,out var systemFault),Is.False);Assert.That(systemFault.Diagnostic,Does.Contain(PlayerMarkerOperation.CleanupHostOnlyReason));
        }
        [TestCase("old",false)][TestCase("old",true)][TestCase("child",false)][TestCase("result",false)]
        public void PlayerMarkerDedicatedRejectsOldAndAlteredRecoveryWithoutRewriting(string fault,bool journal)
        {
            var state=State();var registry=Registry();PlayerMarkerOperation.Register(registry);string marker=Ids(state,"supply",2)[0],storage=MarkerStorageV4(state,Ids(state,"hand",1)[0]);
            var archive=HostRecoveryService.CreateArchive(state);var node=Run(state,DedicatedMarker(1,MarkerLegV4(marker,Zone(state,"supply",2).ZoneId,storage)),registry);
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            var saved=checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId);
            if(fault=="old"){saved["DefinitionVersion"]="1.1.0";saved["NormalizedArguments"]=JToken.FromObject(Object("mode",NormalizedValue.CreateString("round_cleanup")));}
            if(fault=="child")saved["ChildEffectIds"][0]="foreign";
            if(fault=="result")saved["NormalizedResult"]["Properties"].Single(p=>(string)p["Name"]=="movedCount")["Value"]["IntegerValue"]=2;
            string bad=raw.ToString();artifacts.WriteLabeledText("标记职责坏档_"+fault+"_"+journal,bad);Assert.That(HostRecoveryService.RecoverJson(bad,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
    }
}
