using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ContentFacilityContractTests : FacilityLayerTestFixture
    {
        private const string Response="qy007.facility.response",After="qy007.facility.after";
        private const string AfterPath="lua/entrepreneurs/paul.p08.final_bonus.lua";
        private void WrappedContent(bool wrongChoice=false,bool observeAfter=true,bool directBuild=false,bool consumeMain=false,bool nestedAfter=false)
        {
            var baseline=artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(),"Assets/StreamingAssets/Content/core"),"设施新增机制完整未修改基线");
            var token=JObject.Parse(File.ReadAllText(Path.Combine(baseline,"component/"+Token+".json")));
            var query=new AbilitySourceDeclaration { DefinitionId=Token,AbilityId=QueryAbility,Kind="ruleQuery",HandlerRef=QueryAbility,DeclarationVersion="1.0.0",AuthorizationPolicy="owner",PresentationRef=Token,
                EffectiveZones=new List<string>{"core.zone.box_retired"},QueryKinds=new List<string>{FacilityBuildQueryService.BuiltKind,FacilityBuildQueryService.SlotKind,FacilityBuildQueryService.SourceKind} };
            var response=new AbilitySourceDeclaration {DefinitionId=Token,AbilityId=Response,Kind="setup",HandlerRef=Response,DeclarationVersion="1.0.0",AuthorizationPolicy="owner",PresentationRef=Token,EffectiveZones=query.EffectiveZones};
            var after=new AbilitySourceDeclaration {DefinitionId=Token,AbilityId=After,Kind="event",HandlerRef=After,DeclarationVersion="1.0.0",AuthorizationPolicy="owner",PresentationRef=Token,EffectiveZones=query.EffectiveZones,
                ObservedEvents=new List<string>{"EffectCompleted"},CompletedEffectTypes=new List<string>{FacilityEntryEffectTypeIds.Build},AllowedOperations=new List<string>{"execution.bind"}};
            if(!observeAfter){after.Kind="setup";after.ObservedEvents.Clear();after.CompletedEffectTypes.Clear();}
            token["itemTypeId"]="core.board_card";
            token["cardZones"]=JArray.Parse(@"[{""localZoneId"":""blueprints"",""zoneTemplateId"":""core.zone.card_facility_storage"",""acceptedItemKinds"":[""Card""],""acceptedTypeIds"":[""core.facility_card""],""capacityPolicy"":""unbounded"",""orderingKind"":""Unordered"",""visibilityPolicyId"":""core.public_items"",""entryPolicyId"":""core.explicit_transfer"",""exitPolicyId"":""core.explicit_transfer"",""carrierExitPolicyId"":""core.preserve_contents"",""slots"":[]}]");
            token["abilitySources"]=JArray.FromObject(new[]{query,response,after});
            token["abilityHandlers"]=JArray.FromObject(new[]{new ExternalAbilityHandler{HandlerId=QueryAbility,Script="lua/qy007_facility_query.lua",Version="1.0.0"},
                new ExternalAbilityHandler{HandlerId=Response,Script="lua/qy007_facility_response.lua",Version="1.0.0"},new ExternalAbilityHandler{HandlerId=After,Script=AfterPath,Version="1.0.0",CompletionHandlerId=After+".continue"}});
            string build=directBuild ? "local card=Global.Content.FindInstances({zoneId=z.id,contentType='facility'})[1]; return {Effect.Build({player=ctx.playerId,facilityId=card.id,sourceKind='authorized',sourceKey=source.sourceKey,expectedSourceZoneId=z.id,consumeMainAction=false,paymentMode='free'})}" : "return {Effect.ChooseBuild({player=ctx.playerId,sourceZone='authorized',sourceKey=source.sourceKey,expectedSourceZoneId=z.id,consumeMainAction=false,paymentMode='gold'})}";
            if(consumeMain) build=build.Replace("consumeMainAction=false","consumeMainAction=true");
            string queryLua=@"return function(ctx) local p=ctx.payload; if not p.queryKind then local source=SourceData.GetCurrentSources({abilityId='qy010.fixture.query'}).items[1];assert(source,'actual_source_fact_missing');local z=Global.Content.FindZones({parentId=source.itemId,localZoneId='blueprints'})[1];assert(z,'authorized_source_zone_not_visible'); BUILD end;local f=p.facts;local items={}
                if p.queryKind=='built_facilities.v1' then items=f.allCityItemIds
                elseif p.queryKind=='facility.build.slot.v1' then for _,c in ipairs(f.cells) do if c.topItemId~='' and f.newScore>c.topScore then
                    items[#items+1]={slotIndex=c.slotIndex,expectedTopItemId=c.topItemId,orderDigest=c.orderDigest,requiredResponseAbilityId='qy007.facility.response',removalPrecondition={operation='remove',ownerFilter='self',requiredCount=1}} end end
                elseif p.queryKind=='facility.build.sources.v1' and f.requestedSource=='authorized' then items=f.candidatePoolIds end
                return {queryId=p.queryId,basisDigest=p.basisDigest,items=items} end".Replace("BUILD",build);
            const string responseLua=@"return function(ctx) assert(ctx.payload.buildFacts.overlay); return {Effect.WithContext(Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false}),{requiredOutcome={protocolVersion=1,resultField='completedCount',expectedValue=1}})} end";
            string afterLua=@"return function(ctx) local p=ctx.payload; local c=p.continuationContext
                if c and c.stage=='give' then assert(p.interactionId~=nil);return {Effect.WithContext(Effect.GainScore({player='p2',amount=1}),{executionBinding={protocolVersion=1,parentBuildId=c.buildId,choiceInteractionId=CHOICE,playerId=2}})} end
                if p.buildFacts and p.buildFacts.builderId==tonumber(string.sub(ctx.playerId,2)) then return {Effect.WithContext(Effect.Choice({player=ctx.playerId,options={'p2'}}),{stage='give',buildId=p.buildFacts.buildId})} end return {} end".Replace("CHOICE",wrongChoice?"p.effectId":"p.interactionId");
            if(nestedAfter) afterLua=afterLua.Replace("Effect.GainScore({player='p2',amount=1})","Effect.Condition({decisionPlayer='p2',ruleCondition=true,leftEffects={Effect.GainScore({player='p2',amount=1})},rightEffects={Effect.GainScore({player='p2',amount=1})}})");
            var manifest=JObject.Parse(File.ReadAllText(Path.Combine(baseline,"pack.json")));manifest["enabledExpansionIds"]=new JArray("core","enterprise","infected");
            var root=artifacts.CopyContent(baseline,"设施公共合成消费者隔离输入",new Dictionary<string,string>{["pack.json"]=manifest.ToString(),["component/"+Token+".json"]=token.ToString(),["lua/qy007_facility_query.lua"]=queryLua,["lua/qy007_facility_response.lua"]=responseLua,[AfterPath]=afterLua});
            Pack=ExternalContentPack.Load(root);artifacts.RecordContent("设施公共机制实际输入",root,Pack.ContentHash);
        }
        [TestCase(false)][TestCase(true)]
        public void WrappedMandatoryRemovalAndRealBuildCompletionAuthorizeOnlyTheAnsweredChoice(bool forgedChoice)
        {
            WrappedContent(forgedChoice);var state=State();OwnInfluence(state);
            const string hold="qy007.facility.removal.response";
            Registry.Register(hold,c=>c.GetLatestInteractionAnswer()!=null ? EffectStepResult.Completed() : EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec{InteractionTypeId=hold,AnsweringPlayerId=1,Visibility="owner",AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1,CandidateIds={"continue"}}),EffectExecutorKind.IntrinsicFlow);
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration(hold,"InfluenceRemoved","*",hold,c=>new List<EffectSpec>{EffectSpec.Create(hold,playerId:1)},EffectHandlerRole.Observer));
            string low=Definition(state,d=>!d.Unique && !d.HasEntryEffect);Put(state,Card(state,low).ItemId,0);
            int old=FacilityCardDatabase.Get(state,low).Score;
            string target=Card(state,Definition(state,d=>!d.Unique && d.Score>old && d.HasEntryEffect && d.OnBuiltReward.Iron>0)).ItemId;
            var build=Run(state,Build(state,target,0));var facts=FacilityBuildJournal.Read(build);int iron=state.FindPlayer(1).Resources.Iron;int otherScore=state.FindPlayer(2).Score;
            var root=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.ObligationEffectId);
            Assert.That(root.EffectTypeId,Is.EqualTo(AbilitySourceEffectExecutor.TypeId));Assert.That(root.RequiredOutcome,Is.Not.Null);
            Assert.That(state.EffectRuntime.AbilitySourceUses.Any(r=>r.AbilityId==After),Is.False);
            state=Recover(state,"SourceUse包装必做移除待答");Answer(state,Open(state).CandidateIds.First());
            Assert.That(Open(state).InteractionTypeId,Is.EqualTo(hold));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.EntryEffectId).FlowStage,Is.Empty);
            Assert.That(state.EffectRuntime.AbilitySourceUses.Any(r=>r.AbilityId==After),Is.False);
            state=Recover(state,"SourceUse必做移除真实响应待答仍阻止原入场");Answer(state,"continue");
            var choice=Open(state);Assert.That(choice.CandidateIds,Is.EqualTo(new[]{"p2"}));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==facts.EntryEffectId).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.Iron,Is.GreaterThan(iron));
            Assert.That(state.EffectRuntime.AbilitySourceUses.Count(r=>r.AbilityId==After && r.PlayerId==1),Is.EqualTo(1));
            state=Recover(state,"原Build本人入场完成后真实对象Choice待答");choice=Open(state);string choiceId=choice.GetStableInteractionId();
            var engine=new EffectTreeExecutor(state,Registry);Assert.That(engine.TrySubmitInteraction(choiceId,1,choice.StateRevision,NormalizedValue.CreateString("p2"),out var reason),Is.True,reason);
            var report=engine.RunUntilQuiescent();Assert.That(report.Faulted,Is.EqualTo(forgedChoice),state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(2).Score,Is.EqualTo(otherScore+(forgedChoice?0:1)));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==build.EffectId).PlayerId,Is.EqualTo(1));
            Evidence(state,forgedChoice?"effectId冒充interactionId首次拒绝":"真实Choice续接新授权scope");
            if(!forgedChoice){state=Recover(state,"真实Build完成后对象执行结清");Assert.That(state.FindPlayer(2).Score,Is.EqualTo(otherScore+1));}
        }
        [TestCase(false)][TestCase(true)]
        public void LuaAuthorizedBuildKeepsItsOwnSourceKeyZoneAndBudgetThroughPaymentAndRecovery(bool direct)
        {
            WrappedContent(observeAfter:false,directBuild:direct);var state=State();
            var carrier=state.ItemZones.Items.Single(i=>i.DefinitionId==Token && i.OwnerSubject.PlayerId==1);
            var storage=state.ItemZones.Zones.Single(z=>z.ParentId==carrier.ItemId && z.LocalZoneId=="blueprints");
            string card=Card(state,Definition(state,d=>!d.Unique && !d.HasEntryEffect && d.GoldVoucherCost>0)).ItemId;
            Transfer(state,Leg(state,card,storage.ZoneId));var source=Registry.QuerySources(state,1).Single(x=>x.AbilityId==QueryAbility);
            Registry.Register("qy007.build.signal",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType="QY007AuthorizedBuild",PlayerId=1,Payload=Object()}));
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy007.build.body","QY007AuthorizedBuild","","qy007.build.body",c=>new List<EffectSpec>{AbilitySourceEffectExecutor.Create(source,c.Event,QueryAbility,false,c.State.Round)},EffectHandlerRole.Observer));
            int gold=state.FindPlayer(1).Resources.GoldVoucher,budget=state.FindPlayer(1).RemainingMainActionsThisTurn;
            var root=Run(state,EffectSpec.Create("qy007.build.signal",Object(),1));
            if(!direct)
            {
                var q=Open(state);Assert.That(q.InteractionTypeId,Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));Assert.That(q.CandidateIds,Does.Contain(card));
                var engine=new EffectTreeExecutor(state,Registry);
                Assert.That(engine.TryPrepareEffectBuildPayment(1,q.OwnerEffectId,q.GetStableInteractionId(),q.CandidateSetId,q.CandidateSetVersion,card,Object("paymentMode",NormalizedValue.CreateString("gold"),"expectedSourceZoneId",NormalizedValue.CreateString(storage.ZoneId)),state.EffectRuntime.StateRevision,Guid.NewGuid().ToString("N"),out string reason),Is.True,reason);
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
                state=Recover(state,"Lua授权ChooseBuild真实付款准备待答");Answer(state,"pay");
            }
            for(int guard=0;guard<20 && state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root.EffectId).Status!=EffectNodeStatus.Completed;guard++)
            {var q=Open(state);Answer(state,q.CandidateIds.First());}
            var buildNode=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==FacilityEntryEffectTypeIds.Build);
            Assert.That(buildNode.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(Text(buildNode.NormalizedArguments,"sourceKind"),Is.EqualTo("authorized"));
            Assert.That(Text(buildNode.NormalizedArguments,"sourceKey"),Is.EqualTo(source.SourceKey));Assert.That(Text(buildNode.NormalizedArguments,"expectedSourceZoneId"),Is.EqualTo(storage.ZoneId));
            Assert.That(Flag(buildNode.NormalizedArguments,"consumeMainAction"),Is.False);Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn,Is.EqualTo(budget));
            if(direct) Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));else Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.LessThan(gold));
            state=Recover(state,"Lua授权来源原Build完成后");Assert.That(FacilityBuildJournal.Read(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==buildNode.EffectId)).ItemId,Is.EqualTo(card));
        }
        [TestCase(false)][TestCase(true)]
        public void LuaTwoLegBatchUsesActualDefinitionAndOrderFactsAndCommitsAtomically(bool wrongSecondDestination)
        {
            var state=State();var cards=state.ItemZones.Items.Where(i=>FacilityLayerQuery.IsFacility(i)).Take(2).ToArray();
            var supply=FacilityLayerQuery.Supply(state).Take(2).ToArray();
            Transfer(state,Leg(state,cards[0].ItemId,supply[0].SlotZoneId,"top"),Leg(state,cards[1].ItemId,supply[1].SlotZoneId,"top"));
            string lua=@"return function(ctx) local s=Global.Facilities.GetSupplyLayers();local a=s[1];local b=s[2]
                local d=Global.Content.GetDefinition(Global.Content.GetInstance(a.topItemId).definitionId)
                assert(type(d.color)=='string' and type(d.unique)=='boolean' and type(d.uniqueGroupId)=='string' and type(d.hasEntryEffect)=='boolean' and type(d.reserveOnly)=='boolean')
                local function leg(id,from,to) return {itemRefs={{itemId=id}},expectedSource={zoneId=from},destination={zoneId=to},insertion={kind='top'},operationProfileId='core.facility_card_transfer',requiredCount=1,carrierPolicy='preserve'} end
                local function order(x) return {zoneId=x.slotZoneId,digest=x.orderDigest,orderRevision=x.orderRevision,expectedTopItemId=x.topItemId} end
                return {Effect.MoveItem({apiVersion=2,player=ctx.playerId,operationId='qy007.actual.batch',transfers={leg(a.topItemId,a.slotZoneId,b.slotZoneId),leg(b.topItemId,b.slotZoneId,DEST)},expectedOrders={order(a),order(b)}})} end".Replace("DEST",wrongSecondDestination?"'missing.zone'":"a.slotZoneId");
            new LuaEffectEventDispatcher(Registry).Register(new LuaHandlerDefinition("qy007.batch","QY007Batch","",new LuaScriptDefinition(Token,QueryAbility,"qy007.batch","1.0.0",lua,LuaContentHasher.ComputeSha256(lua)),contentInstanceId:Token,abilityId:QueryAbility));
            Registry.Register("qy007.batch.signal",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType="QY007Batch",PlayerId=1,Payload=Object()}));
            var engine=new EffectTreeExecutor(state,Registry);engine.CreateRoot(EffectSpec.Create("qy007.batch.signal",Object(),1));var result=engine.RunUntilQuiescent();
            Assert.That(result.Faulted,Is.EqualTo(wrongSecondDestination),state.EffectRuntime.LastFaultMessage);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==cards[0].ItemId).ZoneId,Is.EqualTo(wrongSecondDestination?supply[0].SlotZoneId:supply[1].SlotZoneId));
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==cards[1].ItemId).ZoneId,Is.EqualTo(wrongSecondDestination?supply[1].SlotZoneId:supply[0].SlotZoneId));
            Evidence(state,"Lua真实双腿提交结果");if(!wrongSecondDestination) state=Recover(state,"Lua双腿原子提交后恢复");
        }
    }
}
