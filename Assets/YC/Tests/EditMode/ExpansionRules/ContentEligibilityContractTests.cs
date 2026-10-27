using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ContentEligibilityContractTests
    {
        const string Def="entrepreneur.ability.A01",Caller="qy007.eligibility.cleanup",Boot="qy007.eligibility.load";
        const string CallerPath="lua/entrepreneurs/paul.p08.region_value.lua",BootPath="lua/entrepreneurs/paul.p07.resource_profile.lua";
        static PaulContentTestFixture Fixture(bool targets,bool internalShortage=false)
        {
            var f=new PaulContentTestFixture(primaryEntrepreneur:Def,sourceOverrides:root=> {
                var j=JObject.Parse(File.ReadAllText(Path.Combine(root,"entrepreneur/"+Def+".json")));
                var cleanup=new AbilitySourceDeclaration {DefinitionId=Def,AbilityId=Caller,Kind="cleanup",HandlerRef=Caller,DeclarationVersion="1.0.0",AuthorizationPolicy="controller",PresentationRef=Def,
                    EffectiveZones=new List<string>{"core.zone.entrepreneur_active"},ObservedEvents=new List<string>{"PlayerCleanupStarted"},CleanupInjectionPolicy="when_available",
                    AllowedOperations=new List<string>{"carrier.transfer.original_supply:"+Def+":commands:core.player_marker"}};
                var target=new AbilitySourceDeclaration {DefinitionId=Def,AbilityId="qy007.eligibility.targets",Kind="quick",HandlerRef="qy007.target.body",DeclarationVersion="1.0.0",AuthorizationPolicy="controller",PresentationRef=Def,
                    EffectiveZones=new List<string>{targets?"core.zone.entrepreneur_active":"core.zone.box_reserve"}};
                var policies=new List<AbilityActionCallPolicy>();
                for(int n=0;n<6;n++) {
                    string action="qy007.target."+n,policy="qy007.policy."+n;
                    target.Actions.Add(new AbilityActionDeclaration {ActionId=action,EntryKind=n==0?"special":"quick",BodyHandlerRef="qy007.target.body",EntryCostRef="qy007.target.cost",EntryAfterEffectsRef="qy007.target.after",DirectCallPolicy=policy,PresentationRef=Def});
                    policies.Add(new AbilityActionCallPolicy{PolicyRef=policy,CallerAbilityId=Caller,CallerHandlerRef=Caller,TargetActionId=action,OmitEntryCost=true,OmitEntryAfterEffects=true,OmitMainAction=true});cleanup.AllowedOperations.Add("action.invoke:"+policy);
                }
                ((JArray)j["abilitySources"]).Add(JObject.FromObject(cleanup));((JArray)j["abilitySources"]).Add(JObject.FromObject(target));
                ((JArray)j["abilitySources"]).Add(JObject.FromObject(new AbilitySourceDeclaration{DefinitionId=Def,AbilityId=Boot,Kind="setup",HandlerRef=Boot,DeclarationVersion="1.0.0",AuthorizationPolicy="controller",PresentationRef=Def,EffectiveZones=cleanup.EffectiveZones,AllowedOperations=new List<string>{"carrier.load:"+Def+":commands:core.player_marker"}}));
                var template=JObject.Parse(File.ReadAllText(Path.Combine(root,"entrepreneur/entrepreneur.round.A.5.json")))["abilities"][0].DeepClone();
                template["abilityId"]=Caller;template["subscriptionId"]=Caller;template["script"]=CallerPath;template["completionHandlerId"]=Caller+".continued";template["version"]="1.0.0";
                template["settlement"]["localId"]=Caller+".order";template["settlement"]["availabilityHandlerId"]="entrepreneur.D09.investment.availability";
                ((JArray)j["abilities"]).Add(template);
                foreach(var h in new[]{new ExternalAbilityHandler{HandlerId=Caller,Script=CallerPath,Version="1.0.0",CompletionHandlerId=Caller+".continued"},new ExternalAbilityHandler{HandlerId=Boot,Script=BootPath,Version="1.0.0"},
                    new ExternalAbilityHandler{HandlerId="qy007.target.body",Script="lua/entrepreneurs/paul.p02.quick.lua",Version="1.0.0"},new ExternalAbilityHandler{HandlerId="qy007.target.cost",Script="lua/entrepreneurs/paul.p03.quick.lua",Version="1.0.0"},new ExternalAbilityHandler{HandlerId="qy007.target.after",Script="lua/entrepreneurs/paul.p06.immediate.lua",Version="1.0.0"}})
                    ((JArray)j["abilityHandlers"]).Add(JObject.FromObject(h));
                j["actionCallPolicies"]=JArray.FromObject(policies);
                string caller=@"return function(ctx) local p=ctx.payload
                    if ctx.eventType=='EffectCompleted' and not(p.continuationContext and p.continuationContext.stage=='call') then return {} end
                    local o=SourceData.GetCallableActions({actionId='qy007.target.0'}).items[1];assert(o and o.enabled)
                    if p.continuationContext and p.continuationContext.stage=='call' then
                        local v=p.normalizedResult;if type(v)=='table' then if v.value~=nil then v=v.value elseif #v==1 then v=v[1] else return {} end end
                        if v~='use' then return {} end
                        return {Effect.InvokeAction({apiVersion=1,player=ctx.playerId,itemId=o.itemId,actionId=o.actionId,callPolicyRef=o.callPolicyRef})}
                    end
                    local card=Global.Content.FindInstances({definitionId='entrepreneur.ability.A01',controller=ctx.playerId})[1]
                    local token=SourceData.GetTransferableTokens({carrierItemId=card.id,localZoneId='commands',typeId='core.player_marker'}).items[1];assert(token)
                    return {Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,leftEffects={Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds={token.itemId},expectedSource={zoneId=token.zoneId},destination={kind='original_supply'},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1})},rightEffects={Effect.WithContext(Effect.Choice({player=ctx.playerId,options={'use'},minSelections=0,maxSelections=1}),{stage='call'})}})} end";
                string bootstrap=@"return function(ctx) local card=Global.Content.FindInstances({definitionId='entrepreneur.ability.A01',controller=ctx.playerId})[1];local supply=Global.Content.FindZones({templateId='core.zone.player_supply',owner=ctx.playerId})[1];local target=Global.Content.FindZones({parentId=card.id,localZoneId='commands'})[1];local item=Global.Content.FindInstances({zoneId=supply.id,typeId='core.player_marker'})[1];return {Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds={item.id},expectedSource={zoneId=supply.id},destination={zoneId=target.id},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1})} end";
                string tiagoPath="lua/entrepreneurs/tiago.lua";string tiago=File.ReadAllText(Path.Combine(root,tiagoPath)).Replace("function TiagoInvestmentAvailability(ctx)","function OriginalTiagoInvestmentAvailability(ctx)");
                const string probe=@"function TiagoInvestmentAvailability(ctx) assert(Global==nil and Effect==nil);local p=SourceData.GetCallableActions();local stock=SourceData.GetTransferableTokens({carrierItemId=p.ruleOwnerItemId,localZoneId='commands',typeId='core.player_marker'});local yes=stock.totalCount>=1 and p.enabledCount>0;return {enabled=yes,disabledReasonKey=yes and '' or 'core:presentation.unavailable'} end ";
                tiago=tiago.Replace("return function(ctx)",probe+"return function(ctx)");
                return new Dictionary<string,string>{["entrepreneur/"+Def+".json"]=j.ToString(),[CallerPath]=caller,[BootPath]=bootstrap,[tiagoPath]=tiago,
                    ["lua/entrepreneurs/paul.p02.quick.lua"]=internalShortage ? "return function(ctx) return {Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,leftEffects={Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=99999}})},rightEffects={Effect.GainScore({player=ctx.playerId,amount=1})}})} end" : "return function(ctx) return {Effect.GainScore({player=ctx.playerId,amount=1})} end",
                    ["lua/entrepreneurs/paul.p03.quick.lua"]="return function(ctx) return {Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=99999}})} end",
                    ["lua/entrepreneurs/paul.p06.immediate.lua"]="return function(ctx) return {Effect.GainResource({recipient=ctx.playerId,resourceType='gold_voucher',amount=10})} end"};
            });
            f.State.Round=1;f.State.ActionRound=1;f.State.Phase=GamePhase.Cleanup;return f;
        }
        static void Cleanup(PaulContentTestFixture f,string type)
        {
            f.Registry.Register(type,c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType="PlayerCleanupStarted",PlayerId=1,SourceEffectId=c.Node.EffectId,OwnerNodeId=c.Node.EffectId,Payload=Object()}));f.Run(EffectSpec.Create(type,Object(),1));
        }
        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)]
        public void OnceInjectionNeedsPayableInventoryAndCurrentDirectCandidatesThenPaysBeforeOptionalChoice(bool decline,bool internalShortage)
        {
            using var f=Fixture(true,internalShortage);Cleanup(f,"qy007.eligibility.empty");
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n=>n.Settlement?.AbilityId==Caller),Is.False,"库存0时不得注入排序段");
            f.Invoke(Boot);var source=f.Registry.QuerySources(f.State,1).Single(s=>s.AbilityId==Caller);
            var snapshot=SourceFactsLuaCatalog.Create(f.State,f.Registry,source);
            string script=@"return function(ctx) local a=SourceData.GetCallableActions();assert(a.totalCount==6 and a.enabledCount==6 and a.transferableTokenCount==1 and #a.items==4 and a.nextCursor==4);local b=SourceData.GetCallableActions({cursor=a.nextCursor});assert(#b.items==2 and b.nextCursor==-1);return {} end";
            var context=new LuaInvocationContext("qy007.paged","Query","p1",f.State.EffectRuntime.StateRevision,"1.0.0",1,0,0){SourceFacts=snapshot};
            var result=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Def,Caller,Caller,"1.0.0",script,LuaContentHasher.ComputeSha256(script)),context);Assert.That(result.IsSuccess,Is.True,result.Diagnostic);
            Cleanup(f,"qy007.eligibility.full");var option=f.Registry.GetSettlementOptions(f.State,1).Single(o=>o.LocalId==Caller+".order");
            Assert.That(f.Engine.TryInvokeCharacterSettlementOption(1,option.OptionId,"",f.State.EffectRuntime.StateRevision,out _,out string why),Is.True,why);f.Pump();
            foreach(var confirm in f.Open.Where(r=>r.InteractionTypeId=="action.decline_effect").ToList()) f.Answer(confirm,"continue");
            var q=f.Open.Single(r=>f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectId==r.OwnerEffectId && n.EffectTypeId==LuaDomainEffectTypeIds.Choice));
            var zone=f.State.ItemZones.Zones.Single(z=>z.ParentId==source.ItemId && z.LocalZoneId=="commands");Assert.That(f.State.ItemZones.Items.Count(i=>i.ZoneId==zone.ZoneId),Is.Zero,"必须先pay1，再出现可放弃Choice");
            int score=f.State.FindPlayer(1).Score,gold=f.State.FindPlayer(1).Resources.GoldVoucher,budget=f.State.FindPlayer(1).RemainingMainActionsThisTurn;
            f.Restore("once已付费可放弃战术待答");q=f.Open.Single(r=>r.GetStableInteractionId()==q.GetStableInteractionId());
            if(decline) f.Decline(q);else f.Answer(q,"use");
            for(int i=0;i<4 && f.Open.Any(r=>r.InteractionTypeId=="action.decline_effect");i++) f.Answer(f.Open.Last(r=>r.InteractionTypeId=="action.decline_effect"),"continue");
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+(decline || internalShortage?0:1)));Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn,Is.EqualTo(budget));
            Assert.That(f.Registry.GetSettlementOptions(f.State,1).Any(o=>o.LocalId==Caller+".order"),Is.False);f.Restore("once正常终态不重复");
        }
        [Test] public void NoCurrentTacticDoesNotInjectEvenWithRealPayableInventory()
        {
            using var f=Fixture(false);f.Invoke(Boot);Cleanup(f,"qy007.eligibility.no.target");
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n=>n.Settlement?.AbilityId==Caller),Is.False);
        }
    }
}
