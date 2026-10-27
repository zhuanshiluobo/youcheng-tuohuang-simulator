using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // R23只复用R22隔离Host/原HUD；驱动仍通过实际Raycast/Pointer/原回调提交。
    // 不启动独立Player，不读取或恢复任何真实存档。
    public sealed class SupplyDriverAndDisposeR23Tests
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static string Output=>Environment.GetEnvironmentVariable("QY017_R23_OUTPUT")??throw new InvalidOperationException("缺少R23证据目录");
        static Type T(string name)=>Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        static object Read(object o,string n)=>o.GetType().GetField(n,Flags)?.GetValue(o)??o.GetType().GetProperty(n,Flags)?.GetValue(o);
        static void Set(object o,string n,object value){var field=o.GetType().GetField(n,Flags);if(field!=null)field.SetValue(o,value);else o.GetType().GetProperty(n,Flags).SetValue(o,value);}
        static object Call(object o,string n,params object[] args){try{return o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}}
        static object Static(string n,params object[] args){try{return T("Validation.HotseatUiValidationDriver").GetMethod(n,Flags).Invoke(null,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}}
        static void Save(string n,object value){using var s=new FileStream(Path.Combine(Output,n+".json"),FileMode.CreateNew);using var w=new StreamWriter(s);w.Write(JsonConvert.SerializeObject(value,Formatting.Indented));}
        SupplyPromptHostR22Tests fixture;
        GameState state;
        EffectRegistry registry;
        GameObject controllerObject,driverObject,eventObject;
        object driver,mobile,frame,coordinator,dialogs,hud,generic,dialog;
        InteractionRequestRouter router;
        List<object> commands;
        InteractionRequest Open=>state.EffectRuntime.InteractionRequests.Last(r=>r.Status=="open");
        InteractionRequestProjection Request=>InteractionRequestProjector.ProjectForPlayer(Open,1);
        sealed class RoutineProbe { public bool Done; public Exception Error; public int BatchFrameWaits, OmittedCaptures; }
        static IEnumerator Drain(IEnumerator routine,RoutineProbe probe)
        {
            var stack=new Stack<IEnumerator>();stack.Push(routine);
            while(stack.Count>0){object value=null;bool moved=false;try{moved=stack.Peek().MoveNext();if(moved)value=stack.Peek().Current;}catch(Exception e){probe.Error=e;break;}
                if(!moved){stack.Pop();continue;}
                if(value is IEnumerator child){if(child.GetType().Name.StartsWith("<Capture>",StringComparison.Ordinal)){probe.OmittedCaptures++;continue;}stack.Push(child);}
                else if(value is WaitForEndOfFrame){probe.BatchFrameWaits++;Canvas.ForceUpdateCanvases();yield return null;}
                else yield return value;}
            probe.Done=true;
        }
        RoutineProbe RunOriginalRoutine(IEnumerator routine)
        {
            driverObject.SetActive(true);var probe=new RoutineProbe();((MonoBehaviour)driver).StartCoroutine(Drain(routine,probe));return probe;
        }
        void Prepare(bool m05)
        {
            Environment.SetEnvironmentVariable("QY017_R22_OUTPUT",Output);
            fixture=new SupplyPromptHostR22Tests();Call(fixture,"PrepareInput");
            state=(GameState)Call(fixture,"State",m05?"entrepreneur.round.M.1":"",m05,null);Set(fixture,"state",state);
            if(m05){Call(fixture,"ActivateFace",state,"entrepreneur.round.M.1","M05");Call(fixture,"Blueprint",state,"entrepreneur.facility.M03");Call(fixture,"Supply",state,"building_015",0);}
            Call(fixture,"SupplyFour");
            if(m05){Call(fixture,"Action",state,"M05");Call(fixture,"Answer",state,NormalizedValue.CreateString("place_twice"));Call(fixture,"UntilPrompt",state,"core:entrepreneur.M.blueprint.choose_supply");}
            else Call(fixture,"GenericOptionalSupply");
            registry=(EffectRegistry)Read(fixture,"Registry");Call(fixture,"CreateHud");
            hud=Read(fixture,"hud");frame=Read(fixture,"frame");dialogs=Read(fixture,"dialogs");coordinator=Read(fixture,"coordinator");
        }
        void InstallDriver()
        {
            controllerObject=new GameObject("R23隔离控制器身份");controllerObject.SetActive(false);mobile=controllerObject.AddComponent(T("MobileCityInteractionController"));
            router=new InteractionRequestRouter();Set(mobile,"interactionRequestRouter",router);Set(mobile,"localPlayerId",1);
            driverObject=new GameObject("R23驱动有限调用");driverObject.SetActive(false);driver=driverObject.AddComponent(T("Validation.HotseatUiValidationDriver"));
            Set(driver,"mobile",mobile);Set(driver,"session",new GameSession(state));Set(driver,"registry",registry);Set(driver,"hud",hud);
            Set(driver,"output",Output);Set(driver,"selectedCase","normal-flow");commands=(List<object>)Read(driver,"commands");
            Set(coordinator,"submit",new Action<GameCommand>(Submit));
            if(EventSystem.current==null)eventObject=new GameObject("R23隔离EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));
            Publish();
        }
        void Publish()
        {
            var visible=GameStateViewProjector.ProjectForPlayer(state,1);Set(fixture,"current",visible);Call(frame,"BindGameplaySnapshot",visible);
            router.GetType().GetProperty("CurrentRequest").SetValue(router,state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open")?Request:null);
        }
        void Submit(GameCommand command)
        {
            int before=state.EffectRuntime.StateRevision;
            var result=new AnswerInteractionCommandHandler(registry).Handle(state,command);Assert.That(result.Succeeded,Is.True,JsonConvert.SerializeObject(result));
            Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            commands.Add(new{Command=command,Result=new WorkflowSubmissionResult(result,true),BeforeRevision=before,AfterRevision=state.EffectRuntime.StateRevision});
            Publish();
        }
        [UnityTest]
        public IEnumerator ActualSupplyPointerAndBlueprintPointerKeepSeparateStages()
        {
            yield return new EnterPlayMode();Prepare(true);InstallDriver();yield return null;Canvas.ForceUpdateCanvases();yield return null;
            var before=Request;Call(driver,"ObserveRequestStage",before);
            string lower=FacilityLayerQuery.Supply(state).SelectMany(s=>s.ItemIds.Skip(1)).FirstOrDefault();
            Assert.That(lower,Is.Not.Null);Assert.That((bool)Static("SupplyCandidateMatches",before,before,Read(fixture,"current"),1,"supply.select."+lower),Is.False);
            Assert.That((bool)Static("SupplyCandidateMatches",before,before,Read(fixture,"current"),1,"supply.select.confirm"),Is.False);
            var stale=GameStateCloneService.DeepClone((GameStateView)Read(fixture,"current"));stale.StateRevision++;
            Assert.That((bool)Static("SupplyCandidateMatches",before,before,stale,1,"supply.select."+before.CandidateIds[0]),Is.False);
            Assert.That((bool)Static("SupplyCandidateMatches",before,before,Read(fixture,"current"),2,"supply.select."+before.CandidateIds[0]),Is.False);
            var marked=UnityEngine.Object.FindObjectsOfType<Component>().First(c=>c.GetType().Name=="PlayerAutomationId"&&(string)Read(c,"Id")=="supply.select."+before.CandidateIds[0]);
            var button=marked.GetComponent<Button>();button.interactable=false;Assert.That(Call(driver,"CurrentSupplyButton",before,before.CandidateIds[0]),Is.Null);button.interactable=true;
            var hidden=GameStateCloneService.DeepClone(before);hidden.VisibleToViewer=false;
            Assert.That((bool)Static("SupplyCandidateMatches",hidden,before,Read(fixture,"current"),1,"supply.select."+before.CandidateIds[0]),Is.False);
            var run=RunOriginalRoutine((IEnumerator)Call(driver,"NormalSupplySelection",before));float deadline=Time.realtimeSinceStartup+30;while(!run.Done){Assert.That(Time.realtimeSinceStartup,Is.LessThan(deadline),"本家隔离协程不得无限等待");yield return null;}if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();
            Save("供应指针隔离等待边界",new{run.BatchFrameWaits,run.OmittedCaptures,Rule="仅测试宿主适配batch帧末等待并略过整局截图；原Press/Raycast/Pointer/回调/Host及生产等待均未替换"});
            Assert.That(commands.Count,Is.EqualTo(1));Assert.That(Open.InteractionTypeId,Is.EqualTo("lua.choice"));
            var stages=(IDictionary)Read(driver,"continuationStages");var supply=stages[before.InteractionId];
            Assert.That(Read(supply,"Selected"),Is.EqualTo(true));Assert.That(Read(supply,"Confirmed"),Is.EqualTo(true));Assert.That(Read(supply,"EffectSettled"),Is.EqualTo(true));
            Assert.That(stages.Values.Cast<object>().Where(r=>(string)Read(r,"Kind")=="来源行动调用").All(r=>!(bool)Read(r,"EffectSettled")),Is.True);
            Save("供应确认后与整个来源行动分开",new{stages,commands,state.EffectRuntime.StateRevision});
            dialog=Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"),Flags,null,new object[]{dialogs,Read(frame,"ContentRect")},null);
            generic=Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"),new object[]{new Func<GameState>(()=>state),new Func<int>(()=>1),dialog,new Action<IReadOnlyList<WorkflowHighlight>>(_=>{}),new Action(()=>{}),new Action<GameCommand>(Submit),new Action<string>(_=>{})});
            Set(generic,"ExpansionUi",Read(hud,"ExpansionUi"));Set(generic,"GetVisibleState",new Func<GameStateView>(()=>(GameStateView)Read(fixture,"current")));
            var blueprint=Request;((IInteractionRequestRenderer)generic).Render(blueprint);yield return null;yield return null;
            run=RunOriginalRoutine((IEnumerator)Call(driver,"NormalChoice"));deadline=Time.realtimeSinceStartup+30;while(!run.Done){Assert.That(Time.realtimeSinceStartup,Is.LessThan(deadline));yield return null;}if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();
            Assert.That(commands.Count,Is.EqualTo(2));Assert.That(state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open"),Is.False);
            var row=stages[blueprint.InteractionId];Assert.That(Read(row,"Seen"),Is.EqualTo(true));Assert.That(Read(row,"Selected"),Is.EqualTo(true));Assert.That(Read(row,"Confirmed"),Is.EqualTo(true));Assert.That(Read(row,"EffectSettled"),Is.EqualTo(true));
            var source=stages.Values.Cast<object>().Single(r=>(string)Read(r,"Kind")=="来源行动调用");
            Assert.That(Read(source,"EffectSettled"),Is.EqualTo(true));Assert.That(Read(source,"Confirmed"),Is.EqualTo(false),"隔离夹具先前发起的行动不冒签成本轮驱动确认");
            Save("原供应与图纸指针确认及后继分阶段",new{stages,commands,state,Isolated=true,NormalFullGame=false});
        }
        [UnityTest]
        public IEnumerator LiveRepeatedDestroyedPageAndOriginalControllerShutdownAreSafe()
        {
            yield return new EnterPlayMode();Prepare(false);yield return null;
            var page=(Component)Read(coordinator,"page");Call(coordinator,"Clear");Call(coordinator,"Clear");Assert.That(page.gameObject.activeSelf,Is.False);
            ((IInteractionRequestRenderer)coordinator).Render(Request);yield return null;page=(Component)Read(coordinator,"page");Assert.That(page.gameObject.activeSelf,Is.True);
            UnityEngine.Object.DestroyImmediate(page.gameObject);
            Assert.DoesNotThrow(()=>Call(page,"Hide"));Assert.DoesNotThrow(()=>Call(coordinator,"Cancel"));Assert.DoesNotThrow(()=>Call(coordinator,"Clear"));
            Assert.DoesNotThrow(()=>Call(coordinator,"Dispose"));Assert.DoesNotThrow(()=>Call(coordinator,"Dispose"));
            controllerObject=new GameObject("R23原控制器卸载方法隔离");controllerObject.SetActive(false);mobile=controllerObject.AddComponent(T("MobileCityInteractionController"));
            var interactions=new InteractionRouter(_=>{});interactions.Register((IInteraction)coordinator);Set(mobile,"interactionRouter",interactions);
            Assert.DoesNotThrow(()=>Call(mobile,"OnDestroy"));Assert.That(Read(mobile,"interactionRouter"),Is.Null);
            Save("生命周期有限检查",new{liveClear=true,repeatedClear=true,pageDestroyedBeforeCancel=true,repeatedDispose=true,
                originalControllerOnDestroyMethod=true,scope="调用原生产OnDestroy方法和真实Router.CancelAll；没有运行独立程序退出，不计正常整局关闭验收"});
        }
        [Test]
        public void OriginalSaveListPolicyRequiresExactAutoOrManualIdentity()
        {
            const string game="r23.fake.same-game",hash="r23.fake.content";
            var config=new GameRuleConfiguration{PlayerCount=4,MapId="map-four-players",RuleModeId="entrepreneur",ContentPackHash=hash};
            var data=new MatchSaveData{GameId=game,ContentHash=hash,Mode=LaunchMode.Local,Seats=Enumerable.Range(1,4).Select(id=>new MatchSaveSeat{PlayerId=id}).ToList(),
                ArchiveJson=new JObject{["Snapshot"]=new JObject{["State"]=new JObject{["RuleConfiguration"]=JObject.FromObject(config)}}}.ToString()};
            var slotType=Type.GetType("YC.Infrastructure.Persistence.MatchSaveSlot, Assembly-CSharp",true);var slots=Array.CreateInstance(slotType,2);
            for(int n=0;n<2;n++){var slot=Activator.CreateInstance(slotType);Set(slot,"Index",n==0?0:3);Set(slot,"Exists",true);Set(slot,"Data",data);slots.SetValue(slot,n);}
            foreach(string key in new[]{"manual-1","auto-1"}){int i=(int)Static("LocalResumeSlotIndex",key);Assert.That(Static("SelectLocalResumeSlot",slots,i,game,4,"entrepreneur",hash),Is.SameAs(slots.GetValue(i==0?0:1)));}
            foreach(string invalid in new[]{null,"auto-0","auto-4","other-1","manual"})Assert.That(Static("LocalResumeSlotIndex",invalid),Is.EqualTo(-1));
            Assert.That(Static("SelectLocalResumeSlot",slots,3,"other",4,"entrepreneur",hash),Is.Null);
            Assert.That(Static("SelectLocalResumeSlot",slots,3,game,3,"entrepreneur",hash),Is.Null);
            Assert.That(Static("SelectLocalResumeSlot",slots,3,game,4,"standard",hash),Is.Null);
            Assert.That(Static("SelectLocalResumeSlot",slots,3,game,4,"entrepreneur","different"),Is.Null);
            Set(slots.GetValue(1),"Error","不可用");Assert.That(Static("SelectLocalResumeSlot",slots,3,game,4,"entrepreneur",hash),Is.Null);
            Save("恢复入口纯策略核验",new{sameAutoAndManual=true,rejected=new[]{"invalid-slot","wrong-game","wrong-players","wrong-mode","wrong-hash","unavailable"},realSaveRead=false,PlayerStarted=false});
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if(driver!=null){foreach(var writer in ((IDictionary)Read(driver,"appendLogs")).Values.Cast<StreamWriter>())writer.Dispose();}
            if(generic is IInteractionRequestRenderer r)r.Clear();generic=null;
            if(dialog is IDisposable d)d.Dispose();dialog=null;
            if(driverObject!=null)UnityEngine.Object.DestroyImmediate(driverObject);
            if(controllerObject!=null)UnityEngine.Object.DestroyImmediate(controllerObject);
            if(eventObject!=null)UnityEngine.Object.DestroyImmediate(eventObject);
            if(fixture!=null){fixture.FinishMartin();var release=(IEnumerator)Call(fixture,"ReleaseComponents");while(release.MoveNext()){if(release.Current is ExitPlayMode)break;}fixture=null;}
            if(UnityEngine.Application.isPlaying)yield return new ExitPlayMode();
            driverObject=controllerObject=eventObject=null;driver=mobile=frame=coordinator=dialogs=hud=null;commands=null;state=null;registry=null;router=null;
        }
    }
}
