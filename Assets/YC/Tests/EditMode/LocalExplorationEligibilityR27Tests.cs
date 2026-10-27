using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 新建私有夹具，复用已有正式主行动机制和原HUD；不读取2997/真实存档或运行中Player。
    public sealed class LocalExplorationEligibilityR27Tests
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static string Output=>Environment.GetEnvironmentVariable("QY017_R27_OUTPUT")??throw new InvalidOperationException("缺少R27输出目录");
        static Type T(string n)=>Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        static object Read(object o,string n)=>o.GetType().GetField(n,Flags)?.GetValue(o)??o.GetType().GetProperty(n,Flags)?.GetValue(o);
        static void Set(object o,string n,object v){var f=o.GetType().GetField(n,Flags);if(f!=null)f.SetValue(o,v);else o.GetType().GetProperty(n,Flags).SetValue(o,v);}
        static object Call(object o,string n,params object[] args){try{return o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}}
        static void Save(string n,object v){using var s=new FileStream(Path.Combine(Output,n+".json"),FileMode.CreateNew);using var w=new StreamWriter(s);w.Write(JsonConvert.SerializeObject(v,Formatting.Indented));}
        static string Snapshot(object v)=>JsonConvert.SerializeObject(v);
        sealed class Probe{public Exception Error;}
        static IEnumerator Drain(IEnumerator routine,Probe probe)
        {
            var stack=new Stack<IEnumerator>();stack.Push(routine);
            while(stack.Count>0){bool moved=false;object value=null;try{moved=stack.Peek().MoveNext();if(moved)value=stack.Peek().Current;}catch(Exception e){probe.Error=e;yield break;}
                if(!moved){stack.Pop();continue;}if(value is IEnumerator child)stack.Push(child);else if(value is WaitForEndOfFrame){Canvas.ForceUpdateCanvases();yield return null;}else yield return value;}
        }
        GameObject hudRoot,mobileRoot,driverRoot,eventRoot;
        object fixture,hud,frame,dialogs,mobile,driver,route,dialog,adapter,actions;
        GameState state;GameStateView visible;IMapQueryService map;
        InfluenceActionPresenter influence;int exploreClicks,deployClicks;
        IsolatedTestArtifacts artifacts;
        void BindSnapshot(GameStateView view){visible=view;Call(frame,"BindGameplaySnapshot",view);}
        void Prepare(bool emptyDecks)
        {
            fixture=typeof(MainActionPreselectionUiTests).GetMethod("Production",Flags).Invoke(null,new object[]{false});
            state=(GameState)Read(fixture,"State");map=(IMapQueryService)Read(fixture,"Map");influence=(InfluenceActionPresenter)Read(fixture,"Presenter");
            var pack=ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(),"Assets/StreamingAssets/Content/core"));var catalog=ActiveContentCatalogBuilder.Build(pack,state.RuleConfiguration);
            T("UiTheme").GetMethod("Initialize",Flags).Invoke(null,new[]{AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",T("UiThemeCatalog"))});
            hudRoot=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));hud=hudRoot.GetComponent(T("GameplayInteractionHudView"));frame=Read(hud,"Frame");dialogs=Read(hud,"DialogRegistry");
            Call(hud,"ConfigureGameplayPresentation",catalog.TextCatalog,new Dictionary<string,string>());BindSnapshot(GameStateViewProjector.ProjectForPlayer(state,1));
            // 客户端预检夹具只定义公开输入。净化状态由正式ToClientState从同一投影生成，
            // 不清实体牌堆、不修改权威夹具，更不读取或修改任何正常局。
            if(emptyDecks)visible.Decks.EventDeckGreenCount=visible.Decks.EventDeckYellowCount=visible.Decks.EventDeckRedCount=0;
            mobileRoot=new GameObject("R27隔离未启动控制器");mobileRoot.SetActive(false);mobile=mobileRoot.AddComponent(T("MobileCityInteractionController"));Set(mobile,"localPlayerId",1);Set(mobile,"interactionRequestRouter",new InteractionRequestRouter());
            driverRoot=new GameObject("R27原指针有限调用");driverRoot.SetActive(false);driver=driverRoot.AddComponent(T("Validation.HotseatUiValidationDriver"));
            Set(driver,"mobile",mobile);Set(driver,"session",new GameSession(state));Set(driver,"registry",Read(fixture,"Registry"));Set(driver,"hud",hud);Set(driver,"output",Output);Set(driver,"selectedCase","full-game");
            var canvas=(RectTransform)Read(frame,"ContentRect");dialog=Activator.CreateInstance(T("EventChoiceDialog"),new object[]{dialogs,new Func<RectTransform>(()=>canvas)});
            var mapType=T("MapViewPresenter");var mapReader=Expression.Lambda(typeof(Func<>).MakeGenericType(mapType),Expression.Constant(null,mapType)).Compile();Action noop=()=>{};
            adapter=Activator.CreateInstance(T("MobileCityWorkflowViewAdapter"),new object[]{new Func<GameState>(()=>state),new Func<int>(()=>1),new Func<RectTransform>(()=>canvas),dialogs,mapReader,map,dialog,new Action<string>(_=>{}),noop,noop,noop,noop,noop,new Action<string>(_=>{}),new Func<int,string>(n=>n.ToString())});
            route=Activator.CreateInstance(T("RouteResourceSelectionInteractionUiCoordinator"),new object[]{new Func<GameState>(()=>state),new Func<GameStateView>(()=>visible),new Func<int>(()=>1),new Func<IReadOnlyList<InteractionRequest>>(()=>Array.Empty<InteractionRequest>()),new RouteSelectionSessionAuthority(map),new RouteResourceSelectionService(map),dialog,adapter,new Action<GameCommand>(_=>Assert.Fail("本例仅原草稿，不允许绕过控件提交命令")),noop});
            Set(route,"CanSubmit",new Func<bool>(()=>true));Set(mobile,"routeResourceSelection",route);
            actions=T("ActionPanelController").GetMethod("Bind",Flags).Invoke(null,new object[]{Read(hud,"ActionPanelView"),Read(dialogs,"CardVisualCatalog"),new Action<string>(_=>{}),noop,
                new Action(()=>{deployClicks++;influence.BeginDeploy();}),noop,new Action(()=>{exploreClicks++;Call(route,"BeginLocalExploration");}),noop,noop,noop,noop});
            Call(actions,"ShowMainFace");Call(actions,"SetButtonStates",false,false,true,false,true,false,false,false,false);
            if(EventSystem.current==null)eventRoot=new GameObject("R27隔离EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));driverRoot.SetActive(true);
            exploreClicks=deployClicks=0;
        }
        void DisposeScene()
        {
            if(driver!=null)foreach(var writer in ((IDictionary)Read(driver,"appendLogs")).Values.Cast<StreamWriter>())writer.Dispose();
            if(route is IInteractionRequestRenderer r)r.Clear();if(dialog is IDisposable d)d.Dispose();
            foreach(var o in new[]{driverRoot,mobileRoot,hudRoot,eventRoot})if(o!=null)UnityEngine.Object.DestroyImmediate(o);
            driverRoot=mobileRoot=hudRoot=eventRoot=null;driver=mobile=hud=frame=route=dialog=adapter=actions=null;
        }
        [UnityTest]
        public IEnumerator LocalEmptyLegalAndInvalidProjectionKeepOriginalControlsAndFailureBoundaries()
        {
            yield return new EnterPlayMode();artifacts=IsolatedTestArtifacts.BeginCase();Prepare(true);yield return null;Canvas.ForceUpdateCanvases();yield return null;
            string before=Snapshot(state);Assert.That(new RouteResourceSelectionService(map).TryCreateExplorationPreview(GameStateViewProjector.ToClientState(visible),visible,1,out _,out _,out _).Reason,Is.EqualTo("route_selection_target_unavailable"));
            Assert.That(Call(driver,"EligibleCampaignMainAction","explore",1),Is.EqualTo("build"));Assert.That(exploreClicks,Is.Zero);Assert.That(((IList)Read(driver,"commands")).Count,Is.Zero);Assert.That(Snapshot(state),Is.EqualTo(before));
            Assert.That(((InfluenceService)Read(fixture,"Influence")).GetLegalPlacementSlotIds(state,1),Is.Not.Empty);
            var run=new Probe();yield return Drain((IEnumerator)Call(driver,"Press",Read(Read(hud,"ActionPanelView"),"DeployButton"),"无探索目标后原部署入口"),run);if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();
            Assert.That(deployClicks,Is.EqualTo(1));Assert.That(exploreClicks,Is.Zero);Assert.That(influence.IsSelectingDeployTarget,Is.True);Assert.That(Snapshot(state),Is.EqualTo(before));
            Save("本地无目标转原合法部署草稿",new{FormalReason="route_selection_target_unavailable",exploreClicks,deployClicks,OriginalDeployDraft=influence.IsSelectingDeployTarget,Commands=((IList)Read(driver,"commands")).Count,AuthorityUnchanged=true,NormalGameCoverage=false});DisposeScene();
            Prepare(false);yield return null;Canvas.ForceUpdateCanvases();yield return null;
            before=Snapshot(state);var original=visible;
            foreach(string variant in new[]{"revision","viewer","phase"})
            {
                var bad=GameStateCloneService.DeepClone(original);if(variant=="revision")bad.StateRevision--;if(variant=="viewer")bad.ViewerPlayerId=2;if(variant=="phase")bad.Phase=GamePhase.Cleanup;
                BindSnapshot(bad);Assert.Throws<InvalidOperationException>(()=>Call(driver,"EligibleCampaignMainAction","explore",1),variant);Assert.That(exploreClicks,Is.Zero);Assert.That(Snapshot(state),Is.EqualTo(before));
            }
            BindSnapshot(original);Assert.That(Call(driver,"EligibleCampaignMainAction","explore",1),Is.EqualTo("explore"));
            var progress=Call(driver,"PlayProgress",1);run=new Probe();yield return Drain((IEnumerator)Call(driver,"BeginCampaignExploration",progress),run);if(run.Error!=null)ExceptionDispatchInfo.Capture(run.Error).Throw();
            var presenter=(RouteResourceSelectionPresenter)Read(route,"Presenter");Assert.That(presenter.IsActive,Is.True);Assert.That(presenter.Session.SelectableLocationUpperBound,Is.Not.Empty);Assert.That(exploreClicks,Is.EqualTo(1));Assert.That(Snapshot(state),Is.EqualTo(before));
            Assert.That(((IList)Read(driver,"commands")).Count,Is.Zero);Assert.That(Read(progress,"Explorations"),Is.EqualTo(0));
            Save("合法公开预览原指针打开草稿",new{presenter.Session,presenter.Projection,exploreClicks,AuthorityUnchanged=true,Commands=0,NewExplorationCompletion=0,InvalidVariantsRejected=new[]{"revision","viewer","phase"}});
            Call(route,"Clear");Assert.That(presenter.IsActive,Is.False);
            var button=(Button)Read(Read(hud,"ActionPanelView"),"ExploreButton");button.onClick.RemoveAllListeners();button.onClick.AddListener(()=>exploreClicks++);
            Assert.That(Call(driver,"EligibleCampaignMainAction","explore",1),Is.EqualTo("explore"));run=new Probe();yield return Drain((IEnumerator)Call(driver,"BeginCampaignExploration",progress),run);
            Assert.That(run.Error,Is.TypeOf<InvalidOperationException>());Assert.That(run.Error.Message,Does.Contain("正常探索入口未形成实际地图草稿"));Assert.That(Snapshot(state),Is.EqualTo(before));
            Save("合法预览但受控回调未开草稿仍失败",new{Error=run.Error.Message,FallbackSelected=false,Commands=((IList)Read(driver,"commands")).Count,AuthorityUnchanged=true,NormalProductionFaultClaimed=false});
        }
        [UnityTearDown]
        public IEnumerator Cleanup(){DisposeScene();artifacts?.Finish();if(UnityEngine.Application.isPlaying)yield return new ExitPlayMode();}
    }
}
