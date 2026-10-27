using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 私有机制夹具只给定拒绝回执，验证原UI撤回和状态门禁；真实3031规则另跑002指定方法。
    public sealed class KnownSupplyRejectionUiR28Tests
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static Type T(string n)=>Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        static object Read(object o,string n)=>o.GetType().GetField(n,Flags)?.GetValue(o)??o.GetType().GetProperty(n,Flags)?.GetValue(o);
        static void Set(object o,string n,object v){var f=o.GetType().GetField(n,Flags);if(f!=null)f.SetValue(o,v);else o.GetType().GetProperty(n,Flags).SetValue(o,v);}
        static object Call(object o,string n,params object[] args){try{return o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}}
        static void Save(string path,object v){using var s=new FileStream(path,FileMode.CreateNew);using var w=new StreamWriter(s);w.Write(JsonConvert.SerializeObject(v,Formatting.Indented));}
        sealed class Probe{public Exception Error;}
        static IEnumerator Drain(IEnumerator routine,Probe probe)
        {
            var stack=new Stack<IEnumerator>();stack.Push(routine);
            while(stack.Count>0){bool moved=false;object value=null;try{moved=stack.Peek().MoveNext();if(moved)value=stack.Peek().Current;}catch(Exception e){probe.Error=e;yield break;}
                if(!moved){stack.Pop();continue;}if(value is IEnumerator child)stack.Push(child);else if(value is WaitForEndOfFrame){Canvas.ForceUpdateCanvases();yield return null;}else yield return value;}
        }
        object fixture,hud,frame,mobile,driver,actions;GameState state;InfluenceActionPresenter influence;TurnActionPresenter turn;
        GameObject hudRoot,mobileRoot,driverRoot,eventRoot;IsolatedTestArtifacts artifacts;List<object> commands;string output;int deployClicks,dispatchClicks;
        void Prepare()
        {
            deployClicks=dispatchClicks=0;
            artifacts=IsolatedTestArtifacts.BeginCase();string root=Environment.GetEnvironmentVariable("QY017_R28_OUTPUT");Assert.That(Path.IsPathRooted(root??""),Is.True);output=Path.Combine(root,TestContext.CurrentContext.Test.MethodName);Assert.That(Directory.Exists(output),Is.False);Directory.CreateDirectory(output);
            fixture=typeof(MainActionPreselectionUiTests).GetMethod("Production",Flags).Invoke(null,new object[]{false});
            // 复用已有夹具的正式初始设置，放置一个可移动实例，为独立调度提供真实候选；不改任何正常局或3031副本。
            typeof(MainActionPreselectionUiTests).GetMethod("PlaceMovable",Flags).Invoke(null,new[]{fixture});
            state=(GameState)Read(fixture,"State");influence=(InfluenceActionPresenter)Read(fixture,"Presenter");
            var context=(IWritableGameplayContext)Read(fixture,"Context");var port=(IGameCommandPort)Read(fixture,"Port");var view=Read(fixture,"View");var map=(IMapQueryService)Read(fixture,"Map");var flow=new InteractionFlowCoordinator();
            var collection=new ResourceCollectionPresenter(context,port,(IResourceCollectionView)view,map,new ResourceCollectionService(map));
            var explore=new ExplorationEventPresenter(context,port,(IExplorationEventView)view,map,(YC.Domain.Exploration.ExplorationService)Read(fixture,"Exploration"),(InfluenceService)Read(fixture,"Influence"));
            turn=new TurnActionPresenter(context,port,(ITurnActionView)view,flow,map,collection,influence,explore);
            var pack=ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(),"Assets/StreamingAssets/Content/core"));var catalog=ActiveContentCatalogBuilder.Build(pack,state.RuleConfiguration);
            T("UiTheme").GetMethod("Initialize",Flags).Invoke(null,new[]{AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",T("UiThemeCatalog"))});
            hudRoot=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));hud=hudRoot.GetComponent(T("GameplayInteractionHudView"));frame=Read(hud,"Frame");var dialogs=Read(hud,"DialogRegistry");Call(hud,"ConfigureGameplayPresentation",catalog.TextCatalog,new Dictionary<string,string>());Call(frame,"BindGameplaySnapshot",GameStateViewProjector.ProjectForPlayer(state,1));
            mobileRoot=new GameObject("R28未启动控制器");mobileRoot.SetActive(false);mobile=mobileRoot.AddComponent(T("MobileCityInteractionController"));Set(mobile,"localPlayerId",1);Set(mobile,"interactionRequestRouter",new InteractionRequestRouter());Set(mobile,"influenceActionPresenter",influence);Set(mobile,"turnActionPresenter",turn);Set(mobile,"flowCoordinator",flow);Set(mobile,"influenceService",Read(fixture,"Influence"));
            driverRoot=new GameObject("R28拒绝后原UI路径");driverRoot.SetActive(false);driver=driverRoot.AddComponent(T("Validation.HotseatUiValidationDriver"));Set(driver,"mobile",mobile);Set(driver,"session",new GameSession(state));Set(driver,"hud",hud);Set(driver,"output",output);Set(driver,"selectedCase","resource-sales");commands=(List<object>)Read(driver,"commands");
            Action noop=()=>{};actions=T("ActionPanelController").GetMethod("Bind",Flags).Invoke(null,new object[]{Read(hud,"ActionPanelView"),Read(dialogs,"CardVisualCatalog"),new Action<string>(_=>{}),noop,new Action(()=>{deployClicks++;turn.BeginDeployAction();}),new Action(()=>{dispatchClicks++;turn.BeginDispatchAction();}),noop,noop,noop,noop,noop});Call(actions,"ShowMainFace");Call(actions,"SetButtonStates",false,false,true,true,false,false,false,false,false);
            if(EventSystem.current==null)eventRoot=new GameObject("R28私有EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));driverRoot.SetActive(true);
        }
        object Begin()=>Call(driver,"BeginObservedAction",GameCommandKind.DeployInfluence,1,"","location:A-01:1","action:deploy",null);
        void RejectedRecord()
        {
            var command=new GameCommand{Kind=GameCommandKind.DeployInfluence,PlayerId=1,TargetId="location:A-01:1"};
            var result=new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InsufficientInfluence,"insufficientsupply")),false);
            commands.Add(new{Command=command,Result=result,BeforeRevision=state.EffectRuntime.StateRevision,AfterRevision=state.EffectRuntime.StateRevision});
        }
        [UnityTest] public IEnumerator KnownUnchangedSupplyRejectionWithdrawsOriginalDraftAndCanOpenLegalDispatch()
        {
            yield return new EnterPlayMode();Prepare();yield return null;Canvas.ForceUpdateCanvases();yield return null;string digest=RuleStateDigestService.Compute(state);var observed=Begin();var run=new Probe();
            yield return Drain((IEnumerator)Call(driver,"Press",Read(Read(hud,"ActionPanelView"),"DeployButton"),"私有夹具原部署入口"),run);if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();Assert.That(influence.HasUnsubmittedMainActionDraft,Is.True);RejectedRecord();
            run=new Probe();yield return Drain((IEnumerator)Call(driver,"ResolveObservedInfluenceAction",observed),run);if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();
            Assert.That(Read(observed,"KnownSupplyRejectionHandled"),Is.EqualTo(true));Assert.That(turn.HasUnsubmittedMainActionDraft,Is.False);Assert.That(influence.IsActive,Is.False);Assert.That(deployClicks,Is.EqualTo(2));Assert.That(commands.Count,Is.EqualTo(1));Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(digest));
            Assert.That((bool)Call(driver,"ActionUnavailableThisWindow",GameCommandKind.DeployInfluence,1),Is.True);Assert.Throws<InvalidOperationException>(()=>Begin());
            var args=new object[]{null,null};Assert.That((bool)driver.GetType().GetMethod("TryFindDispatch",Flags).Invoke(driver,args),Is.True,"独立行动必须有正式合法源和目标");
            run=new Probe();yield return Drain((IEnumerator)Call(driver,"Press",Read(Read(hud,"ActionPanelView"),"DispatchButton"),"供应拒绝后原合法调度入口"),run);if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();Assert.That(dispatchClicks,Is.EqualTo(1));Assert.That(influence.IsSelectingDispatchSource||influence.IsSelectingDispatchTarget,Is.True);Assert.That(commands.Count,Is.EqualTo(1));Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(digest));
            Assert.That(((Dictionary<string,int>)Read(Read(driver,"coverage"),"Completed")).Count,Is.Zero);Save(Path.Combine(output,"已知拒绝与原UI独立入口.json"),new{deployClicks,dispatchClicks,commands,WindowRetryBlocked=true,RuleStateUnchanged=true,NewCoverage=0,DispatchSource=args[0],DispatchTarget=args[1],Scope="单位回执与原UI生命周期；真实3031typed规则由002指定方法核证，未提交调度完成。"});
        }
        [UnityTest] public IEnumerator KnownReasonWithPendingSubmissionStillStopsAndDoesNotWithdrawDraft()
        {
            yield return new EnterPlayMode();Prepare();yield return null;Canvas.ForceUpdateCanvases();yield return null;var observed=Begin();var run=new Probe();
            yield return Drain((IEnumerator)Call(driver,"Press",Read(Read(hud,"ActionPanelView"),"DeployButton"),"私有夹具原部署入口"),run);if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();RejectedRecord();Set(mobile,"pendingEffectCommandId","r28.private.pending");
            run=new Probe();yield return Drain((IEnumerator)Call(driver,"ResolveObservedInfluenceAction",observed),run);Assert.That(run.Error,Is.TypeOf<InvalidOperationException>());Assert.That(run.Error.Message,Does.Contain("state_or_pending_boundary_changed"));Assert.That(Read(observed,"KnownSupplyRejectionHandled"),Is.EqualTo(false));Assert.That(deployClicks,Is.EqualTo(1));Assert.That(commands.Count,Is.EqualTo(1));Assert.That(influence.HasUnsubmittedMainActionDraft,Is.True);
            Save(Path.Combine(output,"在途门禁不被供应原因吞掉.json"),new{Error=run.Error.Message,deployClicks,commands,ContinuationAllowed=false,ProductionStateCleared=false});
        }
        [UnityTearDown] public IEnumerator Release()
        {
            if(driver!=null)foreach(var writer in ((IDictionary)Read(driver,"appendLogs")).Values.Cast<StreamWriter>())writer.Dispose();
            foreach(var o in new[]{driverRoot,mobileRoot,hudRoot,eventRoot})if(o!=null)UnityEngine.Object.DestroyImmediate(o);artifacts?.Finish();if(UnityEngine.Application.isPlaying)yield return new ExitPlayMode();
        }
    }
}
