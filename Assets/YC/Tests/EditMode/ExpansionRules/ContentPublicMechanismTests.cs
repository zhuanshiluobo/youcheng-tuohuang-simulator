using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Commands;
using System.Globalization;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Application.Gameplay;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ContentPublicMechanismTests
    {
        private const string Definition="entrepreneur.ability.A01",Signal="qy007.public.body",Due="qy007.public.due";
        private const string BodyPath="lua/entrepreneurs/paul.p08.region_value.lua",DuePath="lua/entrepreneurs/paul.p08.final_bonus.lua";
        private static PaulContentTestFixture Fixture(string body,string due="return function(ctx) return {} end",Action<JObject> customize=null,bool fullRound=false,Func<string,Dictionary<string,string>> extraOverrides=null)
        {
            var fixture=new PaulContentTestFixture(primaryEntrepreneur:Definition,sourceOverrides:root=> {
                var j=JObject.Parse(File.ReadAllText(Path.Combine(root,"entrepreneur/"+Definition+".json")));
                ((JArray)j["abilitySources"]).Add(JObject.FromObject(new AbilitySourceDeclaration { DefinitionId=Definition,AbilityId=Signal,Kind="setup",HandlerRef=Signal,
                    DeclarationVersion="1.0.0",ActiveFaceId="qy007.synthetic.active",EffectiveZones=new List<string>{"core.zone.entrepreneur_active"},AuthorizationPolicy="controller",PresentationRef=Definition,
                    AllowedOperations=new List<string>{"cleanup.defer:"+Due} }));
                ((JArray)j["abilityHandlers"]).Add(JObject.FromObject(new ExternalAbilityHandler{HandlerId=Signal,Script=BodyPath,Version="1.0.0"}));
                ((JArray)j["abilityHandlers"]).Add(JObject.FromObject(new ExternalAbilityHandler{HandlerId=Due,Script=DuePath,Version="1.0.0"}));
                customize?.Invoke(j);
                var replacements=new Dictionary<string,string>{["entrepreneur/"+Definition+".json"]=j.ToString(),[BodyPath]=body,[DuePath]=due}; if(extraOverrides!=null) foreach(var pair in extraOverrides(root)) replacements[pair.Key]=pair.Value; return replacements;
            },setupOnly:fullRound,secondaryEntrepreneur:fullRound?"entrepreneur.ability.T09":"entrepreneur.ability.D09");
            var card=fixture.State.ItemZones.Items.Single(i=>i.ItemId==fixture.AbilityItem);card.LocalState.Properties.Add(new NormalizedValueEntry{Name="activeFaceId",Value=NormalizedValue.CreateString("qy007.synthetic.active")});card.LocalState.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            if(!fullRound){fixture.State.Round=1;fixture.State.ActionRound=1;fixture.State.Phase=GamePhase.ActionRound1;}
            return fixture;
        }
        private static string ScheduleBody=>"return function(ctx) return {Effect.ScheduleCleanupObligation({apiVersion=1,player=ctx.playerId,handlerRef='"+Due+"',dueRound=1,context={token='frozen',settlementPresentation={label={key='core:entrepreneur.A.cleanup.obligation.name'},description={key='core:entrepreneur.A.cleanup.obligation.name'},ariaLabel={key='core:entrepreneur.A.cleanup.obligation.name'}}}})} end";
        private static void StartObligation(PaulContentTestFixture f, bool commandBoundary = false)
        {
            var window=SettlementHostProtocol.FindActiveWindow(f.State);
            var option=f.Registry.GetSettlementOptions(f.State,window.Context.PlayerId).First(o=>o.LocalId==CleanupObligation.SettlementLocalId && o.IsEnabled);
            Assert.That(option.CanDeclineWholeEffect,Is.False);
            if(!commandBoundary)
            {
                // 合成来源夹具没有正式OperationWindow；只测通用内核入口，不冒正式命令/UI验收。
                f.Artifacts.WriteLabeledText("通用义务隔离入口边界","本合成来源例从已登记结算选项调用内核；正式GameSession边界另由A08及真实Round例覆盖。");
                Assert.That(f.Engine.TryInvokeCharacterSettlementOption(window.Context.PlayerId,option.OptionId,option.TargetUseId,option.StateRevision,out _,out string why),Is.True,why);f.Pump();return;
            }
            var command=new GameCommand{Kind=GameCommandKind.InvokeSettlementOption,PlayerId=window.Context.PlayerId};
            command.Parameters["optionId"]=option.OptionId;command.Parameters["useId"]=option.TargetUseId;command.Parameters["expectedRevision"]=option.StateRevision.ToString(CultureInfo.InvariantCulture);
            var session=new GameSession(f.State);session.RegisterHandler(new InvokeSettlementOptionCommandHandler(f.Registry));
            var result=session.Submit(command);Assert.That(result.Succeeded,Is.True,result.Validation?.Reason);f.Pump();
        }
        private static void Cleanup(PaulContentTestFixture f)
        {
            f.Registry.Register("qy007.public.cleanup",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType="PlayerCleanupStarted",PlayerId=1,OwnerNodeId=c.Node.EffectId,SourceEffectId=c.Node.EffectId,Payload=Object()}));
            f.Run(EffectSpec.Create("qy007.public.cleanup",Object(),1));
            StartObligation(f);
        }
        [TestCase(false)][TestCase(true)]
        public void FrozenObligationRunsOnceAfterSourceExitAndRestoresItsActualPendingAnswer(bool sameCleanup)
        {
            using var f=Fixture(ScheduleBody,"return function(ctx) assert(ctx.payload.arguments.token=='frozen');return {Effect.Choice({player=ctx.playerId,options={'continue'}}),Effect.GainScore({player=ctx.playerId,amount=1})} end");
            if(sameCleanup) f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy007.schedule.in.cleanup","PlayerCleanupStarted","","qy007.schedule.in.cleanup",c=>new List<EffectSpec>{EffectSpec.Create("qy015.fixture.signal",Object("abilityId",NormalizedValue.CreateString(Signal)),1)}));
            int score=f.State.FindPlayer(1).Score;
            if(sameCleanup) Cleanup(f); else f.Invoke(Signal);
            Assert.That(f.State.EffectRuntime.CleanupObligations.Count,Is.EqualTo(1));
            var record=f.State.EffectRuntime.CleanupObligations.Single();
            f.Restore("义务登记后冻结来源");
            if(!sameCleanup)
            {
                // 明确隔离的来源退出控制；非正常行动证据，保持实例、所有权和其他状态。
                var item=f.State.ItemZones.Items.Single(i=>i.ItemId==record.SourceRef.ItemId);
                item.LocalState.Properties.Single(p=>p.Name=="activeFaceId").Value=NormalizedValue.CreateString("");
                f.Artifacts.WriteLabeledText("合成来源退出隔离控制","撤销本例合成声明的activeFaceId资格；不移动企业家实体或伪造真实卡面离场。");
                Assert.That(f.Registry.QuerySources(f.State,1).Any(s=>s.AbilityId==Signal),Is.False);Cleanup(f);
            }
            var request=f.Open.Single(q=>f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectId==q.OwnerEffectId && n.EffectTypeId==LuaDomainEffectTypeIds.Choice));
            string interaction=request.GetStableInteractionId();f.Restore("义务真实Choice待答");
            f.Answer(f.Open.Single(q=>q.GetStableInteractionId()==interaction),"continue");
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+1));
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status,Is.EqualTo("completed"));
            f.Restore("义务完成收据恢复");f.Pump();
            Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+1));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==CleanupObligation.InvokeType),Is.EqualTo(1));
        }
        [Test] public void TwoAcceptedInvocationsKeepIndependentObligationsAndRecoveryDoesNotReplayEitherBody()
        {
            using var f=Fixture(ScheduleBody,"return function(ctx) return {Effect.Choice({player=ctx.playerId,options={'continue'}}),Effect.GainScore({player=ctx.playerId,amount=1})} end");
            int score=f.State.FindPlayer(1).Score;f.Invoke(Signal);f.Invoke(Signal);
            var records=f.State.EffectRuntime.CleanupObligations;Assert.That(records.Count,Is.EqualTo(2));
            Assert.That(records.Select(r=>r.OriginId).Distinct().Count(),Is.EqualTo(2));Assert.That(records.Select(r=>r.ObligationId).Distinct().Count(),Is.EqualTo(2));
            f.Restore("两次真实调用独立登记后");Cleanup(f);
            for(int answered=0;answered<2;answered++)
            {
                if(!f.Open.Any(request=>f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectId==request.OwnerEffectId && n.EffectTypeId==LuaDomainEffectTypeIds.Choice))) StartObligation(f);
                var q=f.Open.Single(request=>f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectId==request.OwnerEffectId && n.EffectTypeId==LuaDomainEffectTypeIds.Choice));
                string id=q.GetStableInteractionId();f.Restore("多义务第"+answered+"份独立待答");f.Answer(f.Open.Single(request=>request.GetStableInteractionId()==id),"continue");
            }
            Assert.That(f.State.EffectRuntime.CleanupObligations.All(r=>r.Status=="completed"),Is.True);Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+2));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n=>n.EffectTypeId==CleanupObligation.InvokeType),Is.EqualTo(2));
            f.Restore("两份义务独立完成后");f.Pump();Assert.That(f.State.FindPlayer(1).Score,Is.EqualTo(score+2));
        }
        [Test] public void BeforeRegistrationRecoveryConstructsOnlyOneObligationAndEmptyDueBodyCompletesNormally()
        {
            using var f=Fixture(ScheduleBody);
            f.Engine.CreateRoot(EffectSpec.Create("qy015.fixture.signal",Object("abilityId",NormalizedValue.CreateString(Signal)),1));
            for(int i=0;i<30 && !f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==CleanupObligation.ScheduleType);i++) Assert.That(f.Engine.Advance(),Is.True);
            Assert.That(f.State.EffectRuntime.CleanupObligations,Is.Empty);f.Restore("真实登记节点执行前");f.Pump();
            Assert.That(f.State.EffectRuntime.CleanupObligations.Count,Is.EqualTo(1));Cleanup(f);
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status,Is.EqualTo("completed"));
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().BodyEffectIds,Is.Empty);f.Restore("无目标空正文合法履约");
        }
        [Test] public void ScheduleWithoutARealInvocationAncestorCannotCreateAnObligation()
        {
            using var f=Fixture(ScheduleBody);
            f.Engine.CreateRoot(EffectSpec.Create(CleanupObligation.ScheduleType,Object("apiVersion",NormalizedValue.CreateInteger(1),"player",NormalizedValue.CreateString("p1"),"handlerRef",NormalizedValue.CreateString(Due),"dueRound",NormalizedValue.CreateInteger(1),"context",Object()),1));
            Assert.That(f.Engine.RunUntilQuiescent().Faulted,Is.True);Assert.That(f.State.EffectRuntime.LastFaultMessage,Does.Contain("cleanup_obligation_origin_unauthorized"));
            Assert.That(f.State.EffectRuntime.CleanupObligations,Is.Empty);f.Snapshot("无调用祖先首次明确拒绝");
        }
        private static void AdvanceVisible(PaulContentTestFixture f)
        {
            var q=f.Open.LastOrDefault(request=>!request.IsInternalMainlineCompletion());
            if(q!=null)
            {
                if(q.AnswerSchema=="boolean_accept") { Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(),q.AnsweringPlayerId,q.StateRevision,NormalizedValue.CreateBoolean(!q.AllowDecline),out var why),Is.True,why);f.Pump();return; }
                var command=new GameCommand{Kind=GameCommandKind.AnswerInteraction,PlayerId=q.AnsweringPlayerId,OptionIds=q.CandidateIds.Take(Math.Max(1,q.MinSelections)).ToList()};
                if(q.MinSelections==0 && q.AllowDecline) command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter]="false";
                command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter]=q.GetStableInteractionId();command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter]=q.StateRevision.ToString(CultureInfo.InvariantCulture);
                var session=new GameSession(f.State);session.RegisterHandler(new AnswerInteractionCommandHandler(f.Registry,f.Rounds));var result=session.Submit(command);
                Assert.That(result.Succeeded,Is.True,result.Validation?.Reason);f.Pump();return;
            }
            var state=f.State;
            if(!string.IsNullOrEmpty(state.EffectRuntime.ActiveSettlementWindowId))
            {
                if(f.Registry.GetSettlementOptions(state,state.CurrentPlayerId).Any(o=>o.LocalId==CleanupObligation.SettlementLocalId && o.IsEnabled)) { StartObligation(f,true); return; }
                var view=SettlementHostProtocol.GetEndActionView(state,state.CurrentPlayerId);
                Assert.That(view,Is.Not.Null);Assert.That(view.CanCloseCleanupWindow,Is.True);
                var command=new GameCommand{Kind=GameCommandKind.EndAction,PlayerId=state.CurrentPlayerId};
                command.Parameters["windowId"]=view.WindowId;command.Parameters["expectedRevision"]=view.StateRevision.ToString(CultureInfo.InvariantCulture);
                var session=new GameSession(state);session.RegisterHandler(new EndActionCommandHandler(new RoundAdvanceService(f.Rounds),null,f.Registry,f.Rounds));
                var result=session.Submit(command);Assert.That(result.Succeeded,Is.True,result.Validation?.Reason);f.Pump();return;
            }
            if((state.Phase==GamePhase.ActionRound1 || state.Phase==GamePhase.ActionRound2) && !state.FindPlayer(state.CurrentPlayerId).ActedMainActionThisTurn && state.FindPlayer(state.CurrentPlayerId).CompletedMainActionsThisTurn==0)
            {
                var service=new InfluenceService(new MapQueryService(f.Map));int player=state.CurrentPlayerId;
                var slots=f.Map.Locations.SelectMany(l=>Enumerable.Range(0,l.InfluenceSlotCount).Select(i=>InfluenceService.GetLocationSlotId(l.LocationId,i))).Concat(f.Map.Routes.SelectMany(r=>Enumerable.Range(0,r.InfluenceSlotCount).Select(i=>InfluenceService.GetRouteSlotId(r.RouteId,i))));
                var target=slots.First(id=>service.CanPlace(state,player,id).IsValid);
                var session=new GameSession(state);session.RegisterHandler(new DeployInfluenceCommandHandler(service,new RoundAdvanceService(f.Rounds),f.Registry));var action=session.Submit(new GameCommand{Kind=GameCommandKind.DeployInfluence,PlayerId=player,TargetId=target});Assert.That(action.Succeeded,Is.True,action.Validation?.Reason);f.Pump();return;
            }
            var step=state.Phase==GamePhase.ActionRound1 || state.Phase==GamePhase.ActionRound2 || state.Phase==GamePhase.Cleanup ? f.Rounds.EndCurrentPlayerWindow(state,state.CurrentPlayerId) : f.Rounds.Advance(state);
            Assert.That(step.IsValid,Is.True,step.Reason);f.Pump();
        }
        [Test] public void ActualRoundCleanupRemovalAndItsRequiredResponseFinishBeforeTheActualTrackReset()
        {
            using var f=Fixture(ScheduleBody,"return function(ctx) return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false})} end",fullRound:true);
            for(int guard=0;guard<200 && f.State.Phase!=GamePhase.ActionRound1;guard++) AdvanceVisible(f);
            Assert.That(f.State.Phase,Is.EqualTo(GamePhase.ActionRound1));f.Invoke(Signal);f.Place(f.Location,0);
            const string hold="qy007.obligation.actual.response";
            f.Registry.Register(hold,c=>c.GetLatestInteractionAnswer()!=null?EffectStepResult.Completed():EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec{InteractionTypeId=hold,AnsweringPlayerId=1,Visibility="owner",AnswerSchema="candidate_id",MinSelections=1,MaxSelections=1,CandidateIds={"continue"}}),EffectExecutorKind.IntrinsicFlow);
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration(hold,"InfluenceRemoved","*",hold,c=>new List<EffectSpec>{EffectSpec.Create(hold,playerId:1)},EffectHandlerRole.Observer));
            for(int guard=0;guard<200 && !f.Open.Any(q=>q.InteractionTypeId==hold);guard++) AdvanceVisible(f);
            Assert.That(f.Open.Any(q=>q.InteractionTypeId==hold),Is.True);Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status,Is.EqualTo("executing"));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==UsageTracks.ResetTypeId),Is.False);
            var invocation=f.State.EffectRuntime.CleanupObligations.Single();
            var trigger=f.State.EffectRuntime.RuleEvents.Single(e=>e.EventId==invocation.DueEventId);
            Assert.That(f.State.EffectRuntime.MainNodes.Any(m=>m.ExecutionEffectId==trigger.OwnerNodeId && m.NodeTypeId==RoundMainlineNodeTypeIds.PlayerCleanupWindow),Is.True);
            f.Restore("真实主链收尾移除响应未完复位未启动");f.Answer(f.Open.Single(q=>q.InteractionTypeId==hold),"continue");
            for(int guard=0;guard<200 && !f.State.EffectRuntime.UsageTrackResets.Any(r=>r.RoundNumber==1 && r.Phase=="completed");guard++) AdvanceVisible(f);
            Assert.That(f.State.EffectRuntime.UsageTrackResets.Any(r=>r.RoundNumber==1 && r.Phase=="completed"),Is.True);
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status,Is.EqualTo("completed"));f.Restore("真实主链义务及轨道复位完成后");
        }
        [TestCase("ProtocolVersion",false)][TestCase("ProtocolVersion",true)][TestCase("HandlerVersion",true)][TestCase("OriginVersion",false)]
        [TestCase("HandlerHash",false)][TestCase("Context",false)][TestCase("OriginId",true)][TestCase("DueRound",true)]
        public void ObligationRawAndDomainRejectTamperingInSnapshotAndRealStateAfter(string field,bool stateAfter)
        {
            using var f=Fixture(ScheduleBody);f.Invoke(Signal);
            var archive=HostRecoveryService.CreateArchive(f.State);
            if(stateAfter) {f.Run(ResourceEffectSpecFactory.GainScore(1,1));HostRecoveryService.AppendJournal(archive,f.State);}
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            var state=stateAfter ? ((JArray)raw["Journal"]["Entries"]).Last(x=>x["StateAfter"] is JObject)["StateAfter"] : raw["Snapshot"]["State"];
            var record=((JArray)state["EffectRuntime"]["CleanupObligations"]).Single();
            if(field=="Context" || field=="OriginVersion") ((JObject)record).Remove(field);else if(field=="ProtocolVersion") record[field]=99;else if(field=="DueRound") record[field]=999;else record[field]="forged";
            var result=HostRecoveryService.RecoverJson(raw.ToString(),f.Registry);
            f.Artifacts.WriteLabeledText("义务真实归档坏字段拒绝",new{field,stateAfter,result.Succeeded,result.Diagnostic}.ToString());
            Assert.That(result.Succeeded,Is.False);
        }
        [Test]
        public void UnexplainedFailureCannotCompleteAnEstablishedObligation()
        {
            using var f=Fixture(ScheduleBody,"return function(ctx) return {Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=999999}})} end");
            f.Invoke(Signal);
            f.Registry.Register("qy007.public.cleanup",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest{EventType="PlayerCleanupStarted",PlayerId=1,OwnerNodeId=c.Node.EffectId,Payload=Object()}));
            f.Engine.CreateRoot(EffectSpec.Create("qy007.public.cleanup",Object(),1));var run=f.Engine.RunUntilQuiescent();
            Assert.That(run.Faulted,Is.True);Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status,Is.Not.EqualTo("completed"));
            f.Snapshot("义务首次真实故障保留");
        }
    }
}
