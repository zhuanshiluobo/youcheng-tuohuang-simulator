using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // I01限定协议回归：真实生产入口，全部私有基线；不计正常游戏或界面验收。
    public sealed partial class ContentPublicMechanismTests
    {
        static JObject ReviewSource(JObject j) => (JObject)((JArray)j["abilitySources"]).Single(s=>(string)s["AbilityId"]==Signal);
        static Dictionary<string,string> ReviewAvailability(string root)
        {
            const string path="lua/entrepreneurs/tiago.lua";
            var text=File.ReadAllText(Path.Combine(root,path)).Replace("function TiagoInvestmentAvailability(ctx)","function OriginalI01Availability(ctx)");
            text=text.Replace("return function(ctx)","function TiagoInvestmentAvailability(ctx) return {enabled=true,disabledReasonKey=''} end\nreturn function(ctx)");
            return new Dictionary<string,string>{{path,text}};
        }
        static PaulContentTestFixture ReviewOriginFixture(string origin)
        {
            return Fixture(ScheduleBody,"return function(ctx) return {Effect.Choice({player=ctx.playerId,options={'continue'}})} end",j=> {
                var s=ReviewSource(j);
                if(origin=="action")
                {
                    s["Kind"]="quick";
                    s["Actions"]=JArray.FromObject(new[]{new AbilityActionDeclaration{ActionId="i01.schedule.action",EntryKind="quick",BodyHandlerRef=Signal,PresentationRef=Definition}});
                }
                else
                {
                    s["Kind"]="cleanup";s["RepeatPolicy"]="unbounded";s["ObservedEvents"]=new JArray("PlayerCleanupStarted");
                    // 私有声明只复用已有正式availability登记，不补pack或读取历史提案。
                    ((JArray)j["abilities"]).Add(new JObject { ["abilityId"]=Signal,["mode"]="cleanup",["skillType"]="normal",["subscriptionId"]=Signal,
                        ["script"]=BodyPath,["version"]="1.0.0",["completionHandlerId"]="",["presentation"]=new JObject{["description"]=new JObject{["key"]="core:presentation.unavailable"}},
                        ["settlement"]=new JObject{["entryKind"]="cleanup_order",["localId"]=Signal+".order",["availabilityHandlerId"]="entrepreneur.D09.investment.availability",["wholeEffectPolicy"]="optional",
                            ["presentation"]=new JObject{["label"]=new JObject{["key"]="core:presentation.unavailable"},["description"]=new JObject{["key"]="core:presentation.unavailable"}}} });
                }
            },fullRound:true,extraOverrides:origin=="cleanup"?ReviewAvailability:null);
        }
        [TestCase("action")][TestCase("cleanup")]
        public void I01Q801ActualActionAndUnboundedCleanupObligationsRecoverAtAllThreeBoundaries(string origin)
        {
            using var f=ReviewOriginFixture(origin);
            for(int guard=0;guard<200 && f.State.Phase!=GamePhase.ActionRound1;guard++) AdvanceVisible(f);
            Assert.That(f.State.Phase,Is.EqualTo(GamePhase.ActionRound1));
            if(origin=="action") f.Run(AbilityActionExecutor.Create(f.Registry.QueryActions(f.State,1).Single(o=>o.ActionId=="i01.schedule.action"),"i01.real.action",Object()));
            else
            {
                Cleanup(f);var option=f.Registry.GetSettlementOptions(f.State,1).Single(o=>o.LocalId==Signal+".order");
                Assert.That(f.Engine.TryInvokeCleanupSource(1,"i01.real.repeated",option.CleanupRequest,out _,out _,out string reason),Is.True,reason);f.Pump();
            }
            var record=f.State.EffectRuntime.CleanupObligations.Single();Assert.That(record.OriginKind,Is.EqualTo(origin));
            f.Artifacts.WriteLabeledText("I01真实祖先冻结版本",JsonConvert.SerializeObject(record));
            f.Restore("I01真实"+origin+"登记后");
            if(origin=="action") Cleanup(f);
            var q=f.Open.Single(q=>f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectId==q.OwnerEffectId && n.EffectTypeId==LuaDomainEffectTypeIds.Choice));
            string id=q.GetStableInteractionId();f.Restore("I01真实"+origin+"履约中");f.Answer(f.Open.Single(x=>x.GetStableInteractionId()==id),"continue");
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status,Is.EqualTo("completed"));f.Restore("I01真实"+origin+"履约后");
        }
        [TestCase("source")][TestCase("action")][TestCase("cleanup")]
        public void I01Q801UnknownOriginExecutorCannotBecomeValidByRehashingItsOwnReceipts(string origin)
        {
            using var f=origin=="source"?Fixture(ScheduleBody):ReviewOriginFixture(origin);
            if(origin!="source")for(int guard=0;guard<200 && f.State.Phase!=GamePhase.ActionRound1;guard++)AdvanceVisible(f);
            if(origin=="source")f.Invoke(Signal);
            else if(origin=="action")f.Run(AbilityActionExecutor.Create(f.Registry.QueryActions(f.State,1).Single(o=>o.ActionId=="i01.schedule.action"),"i01.version.action",Object()));
            else {Cleanup(f);var option=f.Registry.GetSettlementOptions(f.State,1).Single(o=>o.LocalId==Signal+".order");Assert.That(f.Engine.TryInvokeCleanupSource(1,"i01.version.cleanup",option.CleanupRequest,out _,out _,out string why),Is.True,why);f.Pump();}
            var archive=HostRecoveryService.CreateArchive(f.State);var bad=archive.Snapshot.State;var record=bad.EffectRuntime.CleanupObligations.Single();
            record.OriginVersion.ExecutorVersion="999.0.0";
            if(origin=="source")bad.EffectRuntime.AbilitySourceUses.Single(x=>x.ReceiptId==record.OriginId).VersionRef.ExecutorVersion="999.0.0";
            else if(origin=="action")bad.EffectRuntime.AbilityActionInvocations.Single(x=>x.InvocationId==record.OriginId).VersionRef.ExecutorVersion="999.0.0";
            else CleanupSourceService.Invocations(bad).Single(x=>x.InvocationId==record.OriginId).VersionRef.ExecutorVersion="999.0.0";
            // 隔离负例有意重新计算自身收据，不让自然摘要失配代替机制版本门禁。
            string Rehash(string method)=>(string)typeof(CleanupObligation).GetMethod(method,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{record});
            record.OriginAuthorization=Rehash("OriginProof");record.RegistrationDigest=Rehash("RegistrationProof");
            var schedule=bad.EffectRuntime.EffectNodes.Single(x=>x.EffectId==record.ScheduleEffectId);schedule.NormalizedResult.Properties.Single(x=>x.Name=="registrationDigest").Value=NormalizedValue.CreateString(record.RegistrationDigest);
            bool valid=CleanupObligation.TryValidate(bad,f.Registry,out string reason);var result=HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive),f.Registry);
            f.Artifacts.WriteLabeledText("I01自洽收据未知祖先版本隔离负例",JsonConvert.SerializeObject(new{origin,valid,reason,result.Succeeded,result.Diagnostic}));
            Assert.That(valid,Is.False);Assert.That(result.Succeeded,Is.False);
        }
        [TestCase(false,1)][TestCase(true,1)][TestCase(false,2)][TestCase(true,2)]
        public void I01Q802CompletedScheduleMissingRecordIsRejectedByDomainAndSelfConsistentArchive(bool stateAfter,int count)
        {
            using var f=Fixture(ScheduleBody);for(int i=0;i<count;i++)f.Invoke(Signal);
            var archive=HostRecoveryService.CreateArchive(f.State);
            if(stateAfter){f.Run(ResourceEffectSpecFactory.GainScore(1,1));HostRecoveryService.AppendJournal(archive,f.State);}
            var target=stateAfter?archive.Journal.Entries.Last(e=>e.StateAfter!=null).StateAfter:archive.Snapshot.State;
            target.EffectRuntime.CleanupObligations.RemoveAt(0);
            Assert.That(target.EffectRuntime.CleanupObligationProtocolVersion,Is.EqualTo(1));
            bool domain=CleanupObligation.TryValidate(target,f.Registry,out string reason);
            string serialized=HostRecoveryService.SerializeArchive(archive);f.Artifacts.WriteLabeledText("I01漏行隔离负例完整归档",serialized);
            var recovered=HostRecoveryService.RecoverJson(serialized,f.Registry);
            f.Artifacts.WriteLabeledText("I01漏行领域及归档结果",JsonConvert.SerializeObject(new{stateAfter,count,domain,reason,recovered.Succeeded,recovered.Diagnostic,digest=RuleStateDigestService.Compute(target)}));
            Assert.That(domain,Is.False,"协议1仍须反查全部已完成Schedule，不依赖旧摘要失配");Assert.That(recovered.Succeeded,Is.False);
        }
        [TestCase(false)][TestCase(true)]
        public void I01Q803ActualRemovalEventCannotHideAnOuterDueInvocation(bool insideDue)
        {
            const string observer="i01.removal.schedule";
            string due="return function(ctx) return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false})} end";
            using var f=Fixture(ScheduleBody,due,j=>{
                ((JArray)j["abilitySources"]).Add(JObject.FromObject(new AbilitySourceDeclaration{DefinitionId=Definition,AbilityId=observer,Kind="event",HandlerRef=observer,DeclarationVersion="1.0.0",AuthorizationPolicy="controller",PresentationRef=Definition,
                    EffectiveZones=new List<string>{"core.zone.entrepreneur_active"},ObservedEvents=new List<string>{"InfluenceRemoved"},AllowedOperations=new List<string>{"cleanup.defer:"+Due}}));
                ((JArray)j["abilityHandlers"]).Add(JObject.FromObject(new ExternalAbilityHandler{HandlerId=observer,Script="lua/entrepreneurs/paul.p07.resource_profile.lua",Version="1.0.0"}));
            },fullRound:true,extraOverrides:_=>new Dictionary<string,string>{{"lua/entrepreneurs/paul.p07.resource_profile.lua",ScheduleBody}});
            for(int guard=0;guard<200 && f.State.Phase!=GamePhase.ActionRound1;guard++) AdvanceVisible(f);
            f.Place(f.Location,0);
            if(insideDue){f.Invoke(Signal);Cleanup(f);var q=f.Open.Single(request=>f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectId==request.OwnerEffectId && n.EffectTypeId==ContentPrimitiveEffectExecutor.SelectInfluence));Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(),1,q.StateRevision,NormalizedValue.CreateString(q.CandidateIds.First()),out string reason),Is.True,reason);}
            else {string id=f.State.Map.Influences.Single(i=>i.PlayerId==1).InfluenceId;f.Engine.CreateRoot(InfluenceEffectSpecFactory.RemoveInfluence(1,id));}
            var result=f.Engine.RunUntilQuiescent();f.Snapshot(insideDue?"I01到期移除普通事件包装首次现场":"I01义务外同一真实事件来源");
            Assert.That(f.State.EffectRuntime.AbilitySourceUses.Any(u=>u.AbilityId==observer),Is.True);
            Assert.That(result.Faulted,Is.EqualTo(insideDue),f.State.EffectRuntime.LastFaultMessage);
            if(insideDue)Assert.That(f.State.EffectRuntime.LastFaultMessage,Does.Contain("cleanup_obligation_origin_unauthorized"));
            Assert.That(f.State.EffectRuntime.CleanupObligations.Count,Is.EqualTo(1));
        }
        [TestCase(false)][TestCase(true)]
        public void I01RawSourceRefWrongTokenIsRejectedBeforeCoercion(bool stateAfter)
        {
            using var f=Fixture(ScheduleBody);f.Invoke(Signal);var archive=HostRecoveryService.CreateArchive(f.State);
            if(stateAfter){f.Run(ResourceEffectSpecFactory.GainScore(1,1));HostRecoveryService.AppendJournal(archive,f.State);}
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(archive));var state=stateAfter?((JArray)raw["Journal"]["Entries"]).Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            state["EffectRuntime"]["CleanupObligations"][0]["SourceRef"]["AuthorizedPlayerId"]="1";
            f.Artifacts.WriteLabeledText("I01SourceRef数字字符串token隔离挑战",raw.ToString());var result=HostRecoveryService.RecoverJson(raw.ToString(),f.Registry);
            f.Artifacts.WriteLabeledText("I01SourceRef原始token挑战结果",JsonConvert.SerializeObject(new{stateAfter,result.Succeeded,result.Diagnostic}));Assert.That(result.Succeeded,Is.False,"字符串token不能由反序列化强转为合法player整数");
        }
        [Test]
        public void I01Q201IndependentActionHandlerReadsAllSourceApisAndOnlyItsOwnPoliciesAcrossPages()
        {
            const string h0policy="i01.h0.policy";
            string script=@"return function(ctx) local a=SourceData.GetCurrentSources({abilityId='qy007.public.body'});assert(a.totalCount==1 and a.items[1].itemId==ctx.payload.sourceItemId)
                local g=SourceData.GetGrantSources();assert(g.ruleOwnerSourceKey==a.items[1].sourceKey)
                local cursor,n=0,0;repeat local p=SourceData.GetCallableActions({cursor=cursor});for _,o in ipairs(p.items) do assert(o.enabled and o.callPolicyRef~='i01.h0.policy');n=n+1 end;cursor=p.nextCursor until cursor==-1
                assert(n==6,'independent_handler_policy_count');return {Effect.GainScore({player=ctx.playerId,amount=1})} end";
            using var f=Fixture("return function(ctx) return {} end",script,j=>{
                var s=ReviewSource(j);s["Kind"]="quick";var actions=new List<AbilityActionDeclaration>{new AbilityActionDeclaration{ActionId="i01.independent.caller",EntryKind="quick",BodyHandlerRef=Due,PresentationRef=Definition}};
                var policies=new List<AbilityActionCallPolicy>();
                for(int i=0;i<7;i++) {string policy=i==6?h0policy:"i01.h1.policy."+i,action="i01.target."+i;actions.Add(new AbilityActionDeclaration{ActionId=action,EntryKind="quick",BodyHandlerRef=Signal,DirectCallPolicy=policy,PresentationRef=Definition});
                    policies.Add(new AbilityActionCallPolicy{PolicyRef=policy,CallerAbilityId=Signal,CallerHandlerRef=i==6?Signal:Due,TargetActionId=action,OmitEntryCost=true,OmitEntryAfterEffects=true,OmitMainAction=true});((JArray)s["AllowedOperations"]).Add("action.invoke:"+policy);}
                s["Actions"]=JArray.FromObject(actions);j["actionCallPolicies"]=JArray.FromObject(policies);
            },fullRound:true);
            for(int guard=0;guard<200 && f.State.Phase!=GamePhase.ActionRound1;guard++) AdvanceVisible(f);
            int score=f.State.FindPlayer(1).Score;f.Run(AbilityActionExecutor.Create(f.Registry.QueryActions(f.State,1).Single(o=>o.ActionId=="i01.independent.caller"),"i01.independent.action",Object()));
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+1));f.Restore("I01独立H1实际行动三API与分页完成后");
        }
        [Test]
        public void I01Q201WrongHandlerCannotBorrowTheRealIndependentActionExecutionIdentity()
        {
            using var f=Fixture("return function(ctx) return {} end","return function(ctx) return {Effect.Choice({player=ctx.playerId,options={'continue'}})} end",j=>{
                var s=ReviewSource(j);s["Kind"]="quick";s["Actions"]=JArray.FromObject(new[]{new AbilityActionDeclaration{ActionId="i01.wrong.handler.caller",EntryKind="quick",BodyHandlerRef=Due,PresentationRef=Definition}});
            },fullRound:true);
            for(int guard=0;guard<200 && f.State.Phase!=GamePhase.ActionRound1;guard++)AdvanceVisible(f);
            f.Run(AbilityActionExecutor.Create(f.Registry.QueryActions(f.State,1).Single(o=>o.ActionId=="i01.wrong.handler.caller"),"i01.wrong.handler",Object()));
            var action=f.State.EffectRuntime.AbilityActionInvocations.Single(i=>i.ActionId=="i01.wrong.handler.caller");
            Assert.That(action.ActivationCommitted,Is.True);
            var owner=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==action.OwnerEffectId);
            string script="return function(ctx) SourceData.GetCurrentSources();return {} end";
            var handler=new LuaHandlerDefinition("i01.wrong.handler","AbilityInvoked","",new LuaScriptDefinition(Definition,Signal,"i01.wrong.handler","1.0.0",script,LuaContentHasher.ComputeSha256(script)),contentInstanceId:Definition,abilityId:Signal);
            var dispatcher=new LuaEffectEventDispatcher(f.Registry);
            var method=typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            Assert.That(method,Is.Not.Null);
            var context=(LuaInvocationContext)method.Invoke(dispatcher,new object[]{handler,new EffectEventHandlerContext(f.State,new RuleEvent{EventId="i01.wrong.handler.event",EventType="AbilityInvoked",PlayerId=1,OwnerNodeId=owner.EffectId,SourceEffectId=owner.EffectId,Payload=Object("sourceItemId",NormalizedValue.CreateString(action.SourceRef.ItemId))},owner,null)});
            var result=new MoonSharpLuaRuntimeHost().Invoke(handler.Script,context);
            f.Artifacts.WriteLabeledText("I01真实已激活H1下错误处理器Dispatcher挑战",JsonConvert.SerializeObject(new{action.VersionRef,handler.Script.ContentHash,result.Status,result.Diagnostic}));
            Assert.That(result.IsSuccess,Is.False);Assert.That(result.Diagnostic,Does.Contain("source_facts_unavailable"));
        }
    }

    public sealed partial class ContentFacilityContractTests
    {
        private GameState ReviewSelection(bool unique,bool covered,bool consumeMain,out string card,out string storage,bool alreadyBuilt=false)
        {
            WrappedContent(observeAfter:false,consumeMain:consumeMain);var state=State();
            if(covered)
            {
                string low=Definition(state,d=>!d.Unique && !d.HasEntryEffect);
                Put(state,Card(state,low).ItemId,0);Overlay(state,Card(state,low).ItemId,0);
            }
            var carrier=state.ItemZones.Items.Single(i=>i.DefinitionId==Token && i.OwnerSubject.PlayerId==1);
            storage=state.ItemZones.Zones.Single(z=>z.ParentId==carrier.ItemId && z.LocalZoneId=="blueprints").ZoneId;
            card=Card(state,Definition(state,d=>d.Unique==unique && d.GoldVoucherCost>0 && !d.HasEntryEffect)).ItemId;
            Transfer(state,Leg(state,card,storage));
            if(alreadyBuilt)
            {
                string selectedCard=card;string definition=state.ItemZones.Items.Single(i=>i.ItemId==selectedCard).DefinitionId;
                Put(state,Card(state,definition).ItemId,1);
                string alternative=Card(state,Definition(state,d=>!d.Unique && !d.HasEntryEffect)).ItemId;
                Transfer(state,Leg(state,alternative,storage));
            }
            var source=Registry.QuerySources(state,1).Single(s=>s.AbilityId==QueryAbility);
            Registry.Register("i01.build.signal",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType="I01Build",PlayerId=1,SourceEffectId=c.Node.EffectId,OwnerNodeId=c.Node.EffectId,Payload=Object()}));
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration("i01.build.body","I01Build","","i01.build.body",c=>new List<EffectSpec>{AbilitySourceEffectExecutor.Create(source,c.Event,QueryAbility,false,c.State.Round)},EffectHandlerRole.Observer));
            Run(state,EffectSpec.Create("i01.build.signal",Object(),1));return state;
        }
        private YC.Domain.Commands.CommandResult ReviewPrepare(GameState state,string card,string storage)
        {
            var q=Open(state);var command=new GameCommand{Kind=GameCommandKind.PreparePaymentDraft,PlayerId=1,TargetId=card};
            command.Parameters["operation"]="prepare_effect_build";command.Parameters["selectionOwnerEffectId"]=q.OwnerEffectId;
            command.Parameters["selectionInteractionId"]=q.GetStableInteractionId();command.Parameters["selectionCandidateSetId"]=q.CandidateSetId;
            command.Parameters["selectionCandidateSetVersion"]=q.CandidateSetVersion.ToString(CultureInfo.InvariantCulture);
            command.Parameters["expectedRevision"]=state.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture);command.Parameters["paymentMode"]="gold";command.Parameters["expectedSourceZoneId"]=storage;
            var session=new GameSession(state);session.RegisterHandler(new PreparePaymentDraftCommandHandler(Registry));return session.Submit(command);
        }
        [TestCase(false)][TestCase(true)]
        public void I01Q1001FormalUniqueFacilityPaymentQuoteUsesRegistryOverCoveredLayers(bool alreadyBuilt)
        {
            var state=ReviewSelection(true,true,false,out string card,out string storage,alreadyBuilt);
            if(alreadyBuilt)Assert.That(Open(state).CandidateIds,Does.Contain(card),"本例须用当前来源候选进入正式报价唯一组检查，不能仅测陈旧请求");
            int gold=state.FindPlayer(1).Resources.GoldVoucher;var result=ReviewPrepare(state,card,storage);
            Evidence(state,"I01覆盖层唯一设施正式prepare命令结果");
            artifacts.WriteLabeledText("I01唯一设施付款报价",JsonConvert.SerializeObject(new{alreadyBuilt,result.Succeeded,result.Validation}));
            Assert.That(result.Succeeded,Is.EqualTo(!alreadyBuilt),result.Validation?.Reason);
            if(alreadyBuilt)Assert.That(result.Validation.Reason,Is.EqualTo("invalid_build_slot"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));if(!alreadyBuilt) state=Recover(state,"I01唯一设施合法报价后");
        }
        [Test]
        public void I01Q1003UnpaidSourceLossRemainsAnOrdinaryFailureWithoutFallbackOrCharge()
        {
            var state=ReviewSelection(false,false,false,out string card,out string storage);
            var source=Registry.QuerySources(state,1).Single(s=>s.AbilityId==QueryAbility);
            int gold=state.FindPlayer(1).Resources.GoldVoucher;
            Transfer(state,Leg(state,card,FacilityLayerQuery.Supply(state).First().SlotZoneId,"top"));
            var build=Run(state,FacilityBuildEffectSpecFactory.Build(1,card,-1,"free",false,"i01.unpaid",false,storage,"authorized",source.SourceKey));
            Assert.That(build.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(build.FailureReason,Is.EqualTo("source_zone_changed"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(gold));Assert.That(FacilityLayerQuery.IsInCity(state,card,1),Is.False);Evidence(state,"I01未付正常失效仍普通失败");
        }
        [TestCase("move",false)][TestCase("budget",false)][TestCase("move",true)][TestCase("budget",true)]
        public void I01Q1003CommittedPaymentResponseCannotTurnPromotedBuildIntoOrdinaryFailure(string change,bool throughSession)
        {
            var state=ReviewSelection(false,false,change=="budget",out string card,out string storage);
            int gold=state.FindPlayer(1).Resources.GoldVoucher;var prepare=ReviewPrepare(state,card,storage);Assert.That(prepare.Succeeded,Is.True,prepare.Validation?.Reason);
            var q=Open(state);string paymentId=q.OwnerEffectId;
            Registry.Register("i01.real.paid.change",c=> {
                Assert.That(c.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==paymentId).Payment.IsCommitted,Is.True);
                if(change=="move")
                {
                    string destination=FacilityLayerQuery.Supply(c.State).First().SlotZoneId;
                    var leg=Leg(c.State,card,destination,"top");var request=ItemTransferService.CreateBatch(c.State,1,"i01.real.paid.move",new[]{leg});
                    var transfer=new ItemTransferService().RelocateBatch(c,request);return EffectStepResult.Completed(Object("movedCount",NormalizedValue.CreateInteger(transfer.MovedCount)));
                }
                new MainActionBudgetService().SpendCompletedMainAction(c.State,1);return EffectStepResult.Completed();
            });
            Registry.RegisterEventHandler(new EffectEventHandlerRegistration("i01.paid.response","EffectCompleted","*","i01.paid.response",c=>c.Event.SourceEffectId==paymentId?new List<EffectSpec>{EffectSpec.Create("i01.real.paid.change",Object(),1)}:new List<EffectSpec>(),EffectHandlerRole.Observer));
            bool faulted;
            if(throughSession)
            {
                var session=new GameSession(state);session.RegisterHandler(new AnswerInteractionCommandHandler(Registry));
                var command=new GameCommand{Kind=GameCommandKind.AnswerInteraction,PlayerId=1};command.Parameters["interactionId"]=q.GetStableInteractionId();command.Parameters["expectedRevision"]=q.StateRevision.ToString(CultureInfo.InvariantCulture);command.Parameters["answerValue"]="pay";
                var result=session.Submit(command);Assert.That(result.Succeeded,Is.True,result.Validation?.Reason);faulted=state.EffectRuntime.Status==EffectRuntimeStatus.PausedFault;
            }
            else
            {
                var engine=new EffectTreeExecutor(state,Registry);Assert.That(engine.TrySubmitInteraction(q.GetStableInteractionId(),1,q.StateRevision,NormalizedValue.CreateString("pay"),out string reason),Is.True,reason);faulted=engine.RunUntilQuiescent().Faulted;
            }
            Evidence(state,"I01实际付款提交及完成事件响应后首次Build现场");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.LessThan(gold));Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==paymentId).Payment.IsCommitted,Is.True);
            Assert.That(faulted,Is.True,"已付提升Build的首次前提失效必须技术Fault，不能普通Failed释放后续");
            Assert.That(FacilityLayerQuery.IsInCity(state,card,1),Is.False);
            state=Recover(state,"I01已付首次建设前提失效技术故障保留恢复");Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.LessThan(gold));Assert.That(state.EffectRuntime.Status,Is.EqualTo(EffectRuntimeStatus.PausedFault));
        }
        [TestCase(false)][TestCase(true)]
        public void I01Q1002CompletedBindingRemainsHistoricalAfterARealGrantChangeAndRejectsNewOrForgedScopes(bool nested)
        {
            WrappedContent(nestedAfter:nested);var state=State();OwnInfluence(state);
            string low=Definition(state,d=>!d.Unique && !d.HasEntryEffect);Put(state,Card(state,low).ItemId,0);
            string target=Card(state,Definition(state,d=>!d.Unique && d.HasEntryEffect && d.Score>FacilityCardDatabase.Get(state,low).Score && d.OnBuiltReward.Iron>0)).ItemId;
            Run(state,Build(state,target,0));Answer(state,Open(state).CandidateIds.First());Answer(state,"p2");
            for(int guard=0;guard<10 && state.EffectRuntime.InteractionRequests.Any(q=>q.Status=="open" && !q.IsInternalMainlineCompletion());guard++)
            {var q=Open(state);Answer(state,q.CandidateIds.Contains("continue")?"continue":q.CandidateIds.First());}
            Evidence(state,"I01真实嵌套对象scope所有当前请求结清后");
            var binding=state.EffectRuntime.EffectNodes.First(n=>n.ExecutionBinding!=null).ExecutionBinding;
            Assert.That(state.EffectRuntime.EffectNodes.Where(n=>n.ExecutionBinding!=null).All(n=>n.Status==EffectNodeStatus.Completed),Is.True);
            if(nested) Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.NestedEffects.Any(s=>s.ExecutionBinding!=null)),Is.True);
            Registry.Register("i01.real.grant.change",c=> {
                var grant=Registry.QuerySources(c.State,1).Single(s=>s.AbilityId==QueryAbility);
                AbilitySourceSelection.GrantSharedRules(c,binding.SourceRef.ItemId,new[]{grant});return EffectStepResult.Completed();
            });
            Run(state,EffectSpec.Create("i01.real.grant.change",Object(),1));
            Assert.That(Registry.IsCurrentFacilityExecutionSource(state,binding.SourceRef),Is.False);
            Assert.Throws<InvalidOperationException>(()=>EffectExecutionBinding.Create(state,Registry,binding.ParentBuildId,binding.SourceRef,binding.ChoiceInteractionId,2,binding.VersionRef));
            state=Recover(state,"I01完整终态scope在后续真实grant改变后");
            var archive=HostRecoveryService.CreateArchive(state);var bad=archive.Snapshot.State.EffectRuntime.EffectNodes.First(n=>n.ExecutionBinding!=null);bad.ExecutionBinding.AuthorizationRef="forged";
            var restored=HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive),Registry);Assert.That(restored.Succeeded,Is.False);
        }
    }
}
