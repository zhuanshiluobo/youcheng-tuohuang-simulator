using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 正式Bootstrapper、实际请求和原消费者/按钮的隔离CPU测量；不计正常菜单局、帧或视觉验收。
    public sealed class ChoicePageProductionTimingTests
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object o, string n) => o.GetType().GetField(n,F)?.GetValue(o) ?? o.GetType().GetProperty(n,F)?.GetValue(o);
        private static void Set(object o, string n, object v)
        { var field=o.GetType().GetField(n,F); if(field!=null)field.SetValue(o,v);else o.GetType().GetProperty(n,F).SetValue(o,v); }
        private static object Call(object o,string n,params object[] args)
        {
            try { return o.GetType().GetMethods(F).Single(m=>m.Name==n && m.GetParameters().Length==args.Length).Invoke(o,args); }
            catch(TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        private IsolatedTestArtifacts artifacts;
        private SceneRuntimeCatalogScope catalogs;
        private GameObject launchRoot,controllerRoot,hud,eventRoot;
        private object controller,adapter,entrepreneur,enterprise,frame;
        private GameSession session;
        private EffectRegistry registry;
        private int player=1;
        private string phase,scenario;
        private readonly List<object> samples=new List<object>();
        private readonly List<GameCommand> preparation=new List<GameCommand>(),commands=new List<GameCommand>();
        private readonly List<CommandResult> results=new List<CommandResult>();
        private readonly List<object> pages=new List<object>();
        private readonly HashSet<Component> initializedEnterprisePages=new HashSet<Component>();
        private bool successorInteractive;
        private int activityCatalogBuilds, duplicateDetailBuilds;
        private int buttonMeasurementDepth;
        private bool cpuListeners;
        private struct CpuRow { public string phase,handler;public long ticks; }
        private readonly List<CpuRow> cpuRows=new List<CpuRow>(256);
        private Action<string,string,long> luaTrace;
        private Action<long> validationTrace;
        private Dictionary<string,long> targetExecutionBefore,targetExecutionAfter;
        private GameState State=>session.State;
        private GameStateView Visible()=>(GameStateView)Call(controller,"VisibleSaleState");
        private IReadOnlyList<InteractionRequest> Requests()=>VisibleInteractionRequestSource.Read(null,Visible(),player);
        private void Record(UiRegressionMetrics.Sample s)
        {
            samples.Add(new {phase,insideButtonMeasurement=buttonMeasurementDepth>0,sample=s});
            if(s.Phase=="assembly.active_catalog")activityCatalogBuilds++;
            if(s.Phase=="enterprise.detail.active_snapshot")duplicateDetailBuilds++;
        }
        private InteractionRequest Open=>State.EffectRuntime.InteractionRequests.FirstOrDefault(r=>r.Status=="open" && !r.IsInternalMainlineInteraction());
        private void Click(Button b,string name)
        {
            phase=name;Assert.That(b!=null && b.IsActive() && b.IsInteractable(),Is.True,name);
            buttonMeasurementDepth++;
            try
            {
                using(UiRegressionMetrics.Measure("choice.native_button_total"))
                {
                    var p=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
                    ExecuteEvents.Execute(b.gameObject,p,ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(b.gameObject,p,ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(b.gameObject,p,ExecuteEvents.pointerClickHandler);
                    using(UiRegressionMetrics.Measure("choice.layout"))Canvas.ForceUpdateCanvases();
                }
            }
            finally {buttonMeasurementDepth--;}
        }
        private Button Card(string id)=>hud.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(c=>c!=null && c.gameObject.activeInHierarchy && c.GetType().FullName=="YC.PlayerJourney.PlayerAutomationId" &&
                (string)c.GetType().GetField("Id").GetValue(c)=="selection.card."+id).Select(c=>c.GetComponent<Button>()).Single();
        private Component EnterprisePage()=>hud.GetComponentsInChildren(T("EnterpriseSelectionDialogView"),true)
            .Cast<Component>().Single(c=>c.gameObject.activeInHierarchy);
        private Button EnterpriseTarget(string id)
        {
            var page=EnterprisePage();var draft=(EnterpriseSelectionDraft)Read(page,"Draft");
            var target=draft.Projection.Targets.Single(t=>t.Id==id);
            var items=(IDictionary)Read(page,"items");var board=items[target.BoardId];
            var targets=(IDictionary)Read(board,"targets");return (Button)Read(targets[id],"button");
        }
        private Button Confirm()=>Open.InteractionTypeId==EntrepreneurFlow.ChooseInteraction || Open.InteractionTypeId==EntrepreneurFlow.FaceInteraction
            ? hud.GetComponentsInChildren<Button>(true).Single(b=>b.gameObject.activeInHierarchy && b.name=="Confirm Selection")
            : (Button)Read(EnterprisePage(),"confirm");
        private static Action CaptureCallbacks(Button button)
        {
            // 保存当次委托；保留UnityEvent对象会随原按钮重绑而指向下一请求的新委托。
            var calls=((IEnumerable)typeof(UnityEngine.Events.UnityEventBase).GetMethod("PrepareInvoke",F)
                .Invoke(button.onClick,null)).Cast<object>().ToArray();
            return ()=> { foreach(var call in calls)call.GetType().GetMethod("Invoke",new[]{typeof(object[])})
                .Invoke(call,new object[]{Array.Empty<object>()}); };
        }
        private void RenderCurrent()
        {
            var request=Open;Assert.That(request,Is.Not.Null,"正式后继必须有可交互请求");
            player=request.AnsweringPlayerId;Set(controller,"localPlayerId",player);
            InteractionRequestProjection projection;
            using(UiRegressionMetrics.Measure("choice.current_projection"))
                projection=InteractionRequestProjector.ProjectForPlayer(Requests().Single(r=>r.GetStableInteractionId()==request.GetStableInteractionId()),player);
            object renderer;
            if((bool)Call(entrepreneur,"CanRender",projection))renderer=entrepreneur;
            else { Assert.That((bool)Call(enterprise,"CanRender",projection),Is.True,request.InteractionTypeId);renderer=enterprise; }
            using(UiRegressionMetrics.Measure("choice.current_render"))Call(renderer,"Render",projection);
            // EditMode直接实例化Prefab不会自动运行Awake；正常程序的同一原方法负责确认按钮接线。
            if(!UnityEngine.Application.isPlaying && ReferenceEquals(renderer,enterprise))
            { var page=EnterprisePage();if(initializedEnterprisePages.Add(page))Call(page,"Awake"); }
            using(UiRegressionMetrics.Measure("choice.layout"))Canvas.ForceUpdateCanvases();
            var button=Confirm();Assert.That(button.IsActive(),Is.True,"后继原页面已显示");
            pages.Add(new {phase,State.GameId,State.ContentPackHash,State.Phase,State.Round,player,request=request.GetStableInteractionId(),
                request.InteractionTypeId,request.CandidateIds,request.MinSelections,request.MaxSelections,
                renderer=renderer.GetType().FullName,confirmInteractable=button.IsInteractable(),
                cardCount=hud.GetComponentsInChildren<MonoBehaviour>(true).Count(c=>c!=null && c.gameObject.activeInHierarchy && c.GetType().Name=="CardPickerCardButton"),
                assemblies=new[]{typeof(GameSession).Assembly,renderer.GetType().Assembly}.Select(a=>new {a.FullName,mvid=a.ManifestModule.ModuleVersionId})});
        }
        private void Submit(GameCommand command)
        {
            commands.Add(command);CommandResult result;
            var oldLua=LuaEffectEventDispatcher.CpuTimingSample;var oldValidation=YC.Domain.Items.ItemZoneQuery.ValidationTimingSample;
            try
            {
                LuaEffectEventDispatcher.CpuTimingSample=cpuListeners?luaTrace:null;
                YC.Domain.Items.ItemZoneQuery.ValidationTimingSample=cpuListeners?validationTrace:null;
                using(UiRegressionMetrics.Measure("choice.authoritative_submit"))result=session.Submit(command);
            }
            finally {LuaEffectEventDispatcher.CpuTimingSample=oldLua;YC.Domain.Items.ItemZoneQuery.ValidationTimingSample=oldValidation;}
            results.Add(result);Assert.That(result.Succeeded,Is.True,result.Validation?.Reason);
            Call(entrepreneur,"NotifyCommandSettled",command.CommandId);
            Call(enterprise,"NotifyInteractionCommandResolved",State.GameId,command.CommandId,result.Succeeded,result.Validation?.ErrorCode??CommandErrorCode.None);
            RenderCurrent();
        }
        [TestCase("preview")] [TestCase("ability")] [TestCase("enterprise")] [TestCase("face")]
        public void FormalChoicePagesMeasureOriginalSelectionAndConfirmation(string target)
            =>RunScenario(target,false);
        [TestCase("preview")] [TestCase("ability")] [TestCase("enterprise")] [TestCase("face")]
        public void FormalChoicePageConfirmCpuBreakdown(string target)=>RunScenario(target,true);
        private void RunScenario(string target,bool listen)
        {
            samples.Clear();pages.Clear();preparation.Clear();commands.Clear();results.Clear();initializedEnterprisePages.Clear();
            activityCatalogBuilds=duplicateDetailBuilds=0;successorInteractive=false;
            buttonMeasurementDepth=0;
            targetExecutionBefore=targetExecutionAfter=null;
            cpuListeners=listen;cpuRows.Clear();
            luaTrace=(name,handler,ticks)=>cpuRows.Add(new CpuRow{phase=name,handler=handler,ticks=ticks});
            validationTrace=ticks=>cpuRows.Add(new CpuRow{phase="item_zone.validate",handler="",ticks=ticks});
            scenario=target;artifacts=IsolatedTestArtifacts.BeginCase();catalogs=new SceneRuntimeCatalogScope();
            UiRegressionMetrics.Recorded+=Record;UiRegressionMetrics.Enabled=true;phase="正式装配与原资产初始化";
            launchRoot=new GameObject("QY017 R10正式选择隔离上下文");var launch=launchRoot.AddComponent(T("GameLaunchContext"));
            var configuration=new GameRuleConfiguration {PlayerCount=2,MapId="map-three-players",RuleModeId="entrepreneur",
                EnabledExpansionIds=new List<string>{"core","enterprise","infected"}};
            T("GameLaunchContext").GetMethod("ConfigureNewLocal").Invoke(launch,new object[]{1,"",new List<PlayerSeat>{
                new PlayerSeat{PlayerId=1,PlayerName="隔离甲"},new PlayerSeat{PlayerId=2,PlayerName="隔离乙"}},configuration,null,null});
            object assembled;
            using(UiRegressionMetrics.Measure("choice.bootstrap_total"))
                assembled=T("GameSessionBootstrapper").GetMethod("Build",new[]{T("GameLaunchContext"),typeof(bool),typeof(bool)}).Invoke(null,new object[]{launch,false,false});
            session=(GameSession)Read(assembled,"Session");registry=(EffectRegistry)Read(assembled,"EffectRegistry");session.ExecutionTimings.Enabled=true;
            string wanted=target=="preview"?EntrepreneurFlow.PreviewInteraction:target=="ability"?EntrepreneurFlow.ChooseInteraction:
                target=="enterprise"?"setup.enterprise.choose":EntrepreneurFlow.FaceInteraction;
            phase="准备实际请求（不计点击样本）";
            for(int step=0;step<100 && Open?.InteractionTypeId!=wanted;step++)
            {
                var r=Open;Assert.That(r,Is.Not.Null,"合法开局请求未到目标："+wanted);
                var ids=r.CandidateIds.Take(Math.Max(1,r.MinSelections)).ToArray();
                var command=EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(r,r.AnsweringPlayerId),r.AnsweringPlayerId,ids);
                preparation.Add(command);var result=session.Submit(command);Assert.That(result.Succeeded,Is.True,r.InteractionTypeId+":"+result.Validation?.Reason);
            }
            Assert.That(Open?.InteractionTypeId,Is.EqualTo(wanted));player=Open.AnsweringPlayerId;
            controllerRoot=new GameObject("QY017 R10正式投影");controllerRoot.SetActive(false);
            controller=controllerRoot.AddComponent(T("MobileCityInteractionController"));Set(controller,"session",session);Set(controller,"localPlayerId",player);
            phase="原HUD资产首次加载";
            var theme=AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",T("UiThemeCatalog"));T("UiTheme").GetMethod("Initialize").Invoke(null,new[]{theme});
            hud=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            if(EventSystem.current==null)eventRoot=new GameObject("QY017 R10事件",typeof(EventSystem),typeof(StandaloneInputModule));
            var hudView=hud.GetComponent(T("GameplayInteractionHudView"));frame=Read(hudView,"Frame");Call(frame,"Awake");
            var uiRegistry=Read(hudView,"DialogRegistry");Set(uiRegistry,"BeginInputRead",new Func<IDisposable>(()=>(IDisposable)Call(controller,"BeginVisibleRead")));
            Call(frame,"ConfigureGameplayPresentation",Read(assembled,"GameplayTextCatalog"),Read(assembled,"GameplayDefinitionNames"));
            adapter=hud.GetComponent(T("GameplayExpansionUi"));
            Call(adapter,"Configure",new Func<GameStateView>(Visible),new Func<int>(()=>player),registry.EnterpriseDefinitions,registry.CharacterAbilityContracts,new Action<GameCommand>(Submit));
            Call(adapter,"BindSessionPresentation",Read(assembled,"ActivePresentation"),new Func<GameSession>(()=>session));
            entrepreneur=Activator.CreateInstance(T("EntrepreneurSelectionUiCoordinator"),F,null,new object[]{adapter,uiRegistry,Read(frame,"ContentRect"),new Func<int>(()=>player),new Action<GameCommand>(Submit),new Func<bool>(()=>true)},null);
            enterprise=Activator.CreateInstance(T("EnterpriseInteractionUiCoordinator"),F,null,new object[]{adapter,hudView,new Func<IReadOnlyList<InteractionRequest>>(Requests),new Func<int>(()=>player),new Action<GameCommand>(Submit)},null);
            Set(enterprise,"CanSubmit",new Func<bool>(()=>true));Set(enterprise,"IsNetworkSession",new Func<bool>(()=>false));
            phase="目标页面首次投影与显示";RenderCurrent();
            var request=Open;var idsNow=target=="preview"?((EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft")).Projection.Targets.Select(t=>t.Id).ToArray():request.CandidateIds.ToArray();
            Assert.That(idsNow.Length,Is.GreaterThan(1));
            artifacts.WriteLabeledText("首次选择前实际Host",HostRecoveryService.SerializeArchive(session.CreateHostArchive(State.ContentPackHash)));
            string before=JsonConvert.SerializeObject(State);
            Action oldRow=null;int[] previousCardRows=null;
            for(int i=0;i<3;i++)
            {
                if(target=="enterprise" && ((EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft")).SelectedIds.Contains(idsNow[0]))
                    Click(EnterpriseTarget(idsNow[0]),"准备再次选中-"+i);
                var a=target=="ability"||target=="face"?Card(idsNow[0]):EnterpriseTarget(idsNow[0]);
                if(i==0)
                {
                    oldRow=CaptureCallbacks(a);
                    if(target=="ability"||target=="face")previousCardRows=idsNow.Select(id=>Card(id).GetInstanceID()).ToArray();
                }
                Click(a,i==0?"首次选中或预览":"重复选中或预览-"+i);
                if(target=="enterprise")
                {
                    Click(EnterpriseTarget(idsNow[0]),"取消已选-"+i);
                    Assert.That(((EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft")).SelectedIds,Does.Not.Contain(idsNow[0]));
                    if(((EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft")).SelectedIds.Contains(idsNow[1]))
                        Click(EnterpriseTarget(idsNow[1]),"准备再次改选-"+i);
                }
                Click(target=="ability"||target=="face"?Card(idsNow[1]):EnterpriseTarget(idsNow[1]),"改选或改看-"+i);
                if(previousCardRows!=null)Assert.That(idsNow.Select(id=>Card(id).GetInstanceID()),Is.EqualTo(previousCardRows),"同请求本地选择应复用原候选按钮");
            }
            if(target=="enterprise")
            {
                var draft=(EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft");
                foreach(var id in idsNow.Where(id=>!draft.SelectedIds.Contains(id)))
                { if(draft.SelectedIds.Count>=request.MinSelections)break;Click(EnterpriseTarget(id),"按真实最少数量补选"); }
                Assert.That(draft.SelectedIds.Count,Is.InRange(request.MinSelections,request.MaxSelections));
            }
            Assert.That(JsonConvert.SerializeObject(State),Is.EqualTo(before),"本地选中/改选不能提交或修改规则状态");
            if(target=="ability")
            {
                string selected=(string)Read(entrepreneur,"selected");
                for(int open=0;open<2;open++)
                {
                    Click(hud.GetComponentsInChildren<Button>(true).Single(b=>b.gameObject.activeInHierarchy&&b.name=="Inspect Assigned Enterprises"),
                        open==0?"已选后查看企业候选":"已选后再次查看企业候选");
                    phase=open==0?"首次查看后核实际候选数量（按钮计时外）":"再次查看后核实际候选数量（按钮计时外）";
                    Assert.That(((EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft")).Projection.Targets.Length,
                        Is.EqualTo(Visible().Entrepreneurs.OwnEnterpriseCandidateItemIds.Count));
                    Click((Button)Read(EnterprisePage(),"ReadOnlyContinueButton"),open==0?"返回卡牌并保草稿":"再次返回卡牌并保草稿");
                    Assert.That((string)Read(entrepreneur,"selected"),Is.EqualTo(selected));
                }
            }
            if(target=="preview")Assert.That(((EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft")).SelectedIds,Is.Empty,"U02仅预览，不伪造选择");
            var confirm=Confirm();var old=CaptureCallbacks(confirm);int previous=commands.Count;
            // 真实R12现场；归档/序列化/I/O及计数快照均在整个原按钮计时之外。
            artifacts.WriteLabeledText("R12目标确认前正式Host",HostRecoveryService.SerializeArchive(session.CreateHostArchive(State.ContentPackHash)));
            targetExecutionBefore=session.ExecutionTimings.Snapshot();
            Click(confirm,"确认至真实提交与后继布局");
            targetExecutionAfter=session.ExecutionTimings.Snapshot();
            artifacts.WriteLabeledText("R12目标确认后正式Host",HostRecoveryService.SerializeArchive(session.CreateHostArchive(State.ContentPackHash)));
            Assert.That(commands.Count,Is.EqualTo(previous+1));
            var answered=commands[previous];
            artifacts.WriteLabeledText("R12实际确认命令",JsonConvert.SerializeObject(answered));
            // 后继页须有可操作的原候选，不能只以窗口已激活充交互完成。
            var next=Open;
            if(next.InteractionTypeId==EntrepreneurFlow.PreviewInteraction)
                Assert.That(Confirm().IsActive() && Confirm().IsInteractable(),Is.True);
            else if(next.InteractionTypeId==EntrepreneurFlow.ChooseInteraction || next.InteractionTypeId==EntrepreneurFlow.FaceInteraction)
            {
                Assert.That((string)Read(entrepreneur,"selected"),Is.Empty,"后继不能沿用前请求草稿");
                Click(Card(next.CandidateIds[0]),"后继原候选实际可交互");
                Assert.That((string)Read(entrepreneur,"selected"),Is.EqualTo(next.CandidateIds[0]));
            }
            else
            {
                var draft=(EnterpriseSelectionDraft)Read(EnterprisePage(),"Draft");Assert.That(draft.SelectedIds,Is.Empty);
                Click(EnterpriseTarget(next.CandidateIds[0]),"后继原候选实际可交互");
                Assert.That(draft.SelectedIds,Does.Contain(next.CandidateIds[0]));
            }
            successorInteractive=true;Assert.That(commands.Count,Is.EqualTo(previous+1));
            phase="旧确认回调拒绝";old();Assert.That(commands.Count,Is.EqualTo(previous+1));
            string settled=JsonConvert.SerializeObject(State);var selection=(string)Read(entrepreneur,"selected");
            oldRow();Assert.That(commands.Count,Is.EqualTo(previous+1));
            Assert.That((string)Read(entrepreneur,"selected"),Is.EqualTo(selection),"旧请求候选回调不能修改后继卡牌草稿");
            Assert.That(JsonConvert.SerializeObject(State),Is.EqualTo(settled));
            // 新命令ID重放旧答案，不能只靠旧按钮的本地拒绝证明权威请求已失效。
            var stale=new GameCommand {Kind=answered.Kind,PlayerId=answered.PlayerId,SourceId=answered.SourceId,TargetId=answered.TargetId,
                OptionIds=new List<string>(answered.OptionIds),Parameters=new Dictionary<string,string>(answered.Parameters)};
            var staleResult=session.Submit(stale);Assert.That(staleResult.Succeeded,Is.False);
            Assert.That(JsonConvert.SerializeObject(State),Is.EqualTo(settled),"旧答案拒绝不能发布状态或Journal变化");
            artifacts.WriteLabeledText("R12旧答案权威拒绝",JsonConvert.SerializeObject(new {stale,staleResult}));
            Assert.That(State.EffectRuntime.InteractionRequests.Single(r=>r.GetStableInteractionId()==request.GetStableInteractionId()).Status,Is.EqualTo("answered"));
            Assert.That(activityCatalogBuilds,Is.EqualTo(1),"正式装配只做一次，包含展示出口创建成本");
            Assert.That(duplicateDetailBuilds,Is.Zero,"详情消费不能重新装配活动内容");
        }
        [TearDown]public void Finish()
        {
            UiRegressionMetrics.Recorded-=Record;UiRegressionMetrics.Enabled=false;
            if(artifacts!=null)
            {
                if(session!=null)artifacts.WriteLabeledText("采样结束正式Host",HostRecoveryService.SerializeArchive(session.CreateHostArchive(State.ContentPackHash)));
                artifacts.WriteLabeledText("企业与卡牌页全部实测",JsonConvert.SerializeObject(new {scenario,phase,samples,pages,preparation,commands,results,
                    cpuListeners,cpuRows,cpuClockFrequency=System.Diagnostics.Stopwatch.Frequency,successorInteractive,
                    activityCatalogBuilds,duplicateDetailBuilds,
                    samplingWindow="insideButtonMeasurement=true才属于原按钮总段；按钮返回后的实际候选数量断言会另读当前投影，不能把同phase或相邻样本直接加进点击成本。",
                    targetExecutionBefore,targetExecutionAfter,
                    targetExecutionDelta=targetExecutionBefore==null||targetExecutionAfter==null?null:
                        targetExecutionAfter.ToDictionary(p=>p.Key,p=>p.Key=="clock.frequency"?p.Value:p.Value-targetExecutionBefore[p.Key]),
                    state=session?.State,execution=session?.ExecutionTimings.Snapshot(),installedHash=ExternalContentPack.Current?.ContentHash,
                    device=new {SystemInfo.graphicsDeviceType,SystemInfo.graphicsDeviceName,SystemInfo.maxTextureSize,Screen.width,Screen.height},
                    assetState="原HUD/企业图集已加载后才计目标首次Render；动态卡图首次解码及索引仍按samples所属phase完整记录；不把HUD加载算作页面打开",
                    scope="当前Bootstrapper真实开局请求，原企业/卡牌消费者与原资产按钮；隔离准备通过合法答案推进，非菜单正常局。CPU同步回调及强制布局；不计GPU、下一帧或正常多局。U02仅浏览；能力/回合面无取消选择语义。所有样本保留；文件和Host序列化不在计时内。"},Formatting.Indented));
            }
            if(enterprise!=null)
            {
                if(UnityEngine.Application.isPlaying)Call(enterprise,"Dispose");
                else {Call(enterprise,"Clear");foreach(var name in new[]{"enterprise","department"})
                    if(Read(enterprise,name) is Component page && page!=null)UnityEngine.Object.DestroyImmediate(page.gameObject);}
            }
            if(entrepreneur!=null)Call(entrepreneur,"Clear");
            if(adapter!=null)Call(adapter,"BeginShutdown");
            foreach(var go in new[]{hud,eventRoot,controllerRoot,launchRoot})if(go!=null)UnityEngine.Object.DestroyImmediate(go);
            catalogs?.Dispose();artifacts?.Finish();
            session=null;adapter=entrepreneur=enterprise=controller=frame=null;
            launchRoot=controllerRoot=hud=eventRoot=null;catalogs=null;artifacts=null;
        }
    }
}
