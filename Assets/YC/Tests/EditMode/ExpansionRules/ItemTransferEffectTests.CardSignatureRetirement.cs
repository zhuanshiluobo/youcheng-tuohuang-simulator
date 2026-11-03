using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static string RetiredCardType(bool cover)=>cover?CharacterCoverEffectExecutor.EffectTypeId:LuaDomainEffectTypeIds.MoveCharacterCard;
        private static string RetiredCardReason(bool cover)=>cover?CharacterCoverEffectExecutor.RetiredV2Reason:CharacterCardMovement.UnversionedSignatureRetired;
        private static NormalizedValue RetiredCardArgs(bool cover)=>cover?Object("apiVersion",NormalizedValue.CreateInteger(2),"player",NormalizedValue.CreateString("1"),"cardInstanceId",NormalizedValue.CreateString("actual-old-instance"),"coverMode",NormalizedValue.CreateString("additional"),"blockedActionRoundSequence",NormalizedValue.CreateInteger(1)):
            Object("player",NormalizedValue.CreateString("1"),"sourceZone",NormalizedValue.CreateString("discard"),"destinationZone",NormalizedValue.CreateString("hand"));

        [TestCase(true)][TestCase(false)]
        public void RetiredCardSignaturesRejectLuaAliasesManualCompilerAndKernel(bool cover)
        {
            string type=RetiredCardType(cover),reason=RetiredCardReason(cover),name=cover?"CoverCharacterCard":"MoveCharacterCard";
            string args=cover?"apiVersion=2,player=ctx.playerId,cardInstanceId='old',coverMode='additional',blockedActionRoundSequence=1":"player=ctx.playerId,sourceZone='discard',destinationZone='hand'";
            foreach(string body in new[]{"return {Effect."+name+"({"+args+"})}","local old=Effect."+name+";return {old({"+args+"})}","return {Effect.Condition({leftEffects={{kind='effect_spec',effectTypeId='"+type+"',"+args+"}},rightEffects={}})}"})
            {
                var result=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx) "+body+" end"),new LuaInvocationContext("card.retirement","test","p1",0,"1.0.0",2,0,0));
                Assert.That(result.IsSuccess,Is.False);Assert.That(result.Diagnostic,Does.Contain(reason));
            }
            foreach(bool nested in new[]{false,true})
            {
                var leaf=LuaEffectSpec.FromArguments(type,RetiredCardArgs(cover));var spec=nested?LuaEffectSpec.FromEffectGroups(EffectTypeIds.Condition,Object(),new System.Collections.Generic.IReadOnlyList<LuaEffectSpec>[]{new[]{leaf},Array.Empty<LuaEffectSpec>()}):leaf;
                var success=typeof(LuaInvocationResult).GetMethods(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Single(m=>m.Name=="Success"&&m.GetParameters().Length==2);
                var invocation=(LuaInvocationResult)success.Invoke(null,new object[]{ShuffleScript("return function(ctx) return {} end"),new[]{spec}});
                Assert.That(new LuaEffectSpecCompiler(new LuaEffectTypeCatalog()).TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out _,out var fault),Is.False);Assert.That(fault.Diagnostic,Does.Contain(reason));
                var state=State();string before=JsonConvert.SerializeObject(new{state.ItemZones,state.Players});var reg=Registry();CharacterCoverEffectExecutor.Register(reg);var old=EffectSpec.Create(type,RetiredCardArgs(cover),1);if(nested)old=EffectSpec.Condition(new[]{old},Array.Empty<EffectSpec>());
                Assert.That(new EffectTreeExecutor(state,reg).TryCreateRoot(old,"","",out _),Is.False);Assert.That(state.EffectRuntime.LastFaultMessage,Does.Contain(reason));Assert.That(state.EffectRuntime.EffectNodes,Is.Empty);Assert.That(JsonConvert.SerializeObject(new{state.ItemZones,state.Players}),Is.EqualTo(before));
            }
        }

        [TestCase(true,false)][TestCase(true,true)][TestCase(false,false)][TestCase(false,true)]
        public void RetiredCardSignaturesRejectSnapshotJournalAndPendingCarriers(bool cover,bool journal)
        {
            var state=State();var reg=Registry();Run(state,Shuffle(state),reg);var dto=HostRecoveryService.CreateArchive(state);
            if(journal){Run(state,Shuffle(state),reg);HostRecoveryService.AppendJournal(dto,state);}
            string clean=HostRecoveryService.SerializeArchive(dto);Assert.That(HostRecoveryService.RecoverJson(clean,reg).Succeeded,Is.True);
            foreach(string carrier in new[]{"terminal","nested","settlement","deferred","departure_offer","place_mapping"})
            {
                var archive=JObject.Parse(clean);var runtime=(JObject)(journal?archive.SelectTokens("$..StateAfter").OfType<JObject>().Last()["EffectRuntime"]:archive["Snapshot"]["State"]["EffectRuntime"]);
                var old=JObject.FromObject(new EffectNodeRuntimeState{EffectId="old.card.signature",EffectTypeId=RetiredCardType(cover),PlayerId=1,Status=EffectNodeStatus.Completed,NormalizedArguments=RetiredCardArgs(cover)});
                if(carrier=="terminal")((JArray)runtime["EffectNodes"]).Add(old);
                if(carrier=="nested")((JArray)runtime["EffectNodes"][0]["NestedEffects"]).Add(old);
                if(carrier=="settlement")runtime["EffectNodes"][0]["SettlementWindow"]=new JObject{["Entries"]=new JArray(new JObject{["Effects"]=new JArray(old)})};
                if(carrier=="deferred")runtime["DeferredResponses"]=new JArray(new JObject{["PendingBody"]=new JArray(old)});
                if(carrier=="departure_offer")runtime["InfluenceDepartureBatches"]=new JArray(new JObject{["Offers"]=new JArray(new JObject{["Body"]=old})});
                if(carrier=="place_mapping")runtime["InfluencePlaceMappings"]=new JArray(new JObject{["OriginalSpec"]=old});
                string bad=archive.ToString();artifacts.WriteLabeledText("旧盖放或角色移动归档_"+cover+"_"+journal+"_"+carrier,bad);var rejected=HostRecoveryService.RecoverJson(bad,reg);
                Assert.That(rejected.Succeeded,Is.False);Assert.That(rejected.Diagnostic,Is.EqualTo(RetiredCardReason(cover)));Assert.That(archive.ToString(),Is.EqualTo(bad));
            }
        }
    }
}
