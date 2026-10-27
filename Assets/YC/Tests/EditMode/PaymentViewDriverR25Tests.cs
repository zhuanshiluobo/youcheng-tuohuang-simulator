using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 只在只读2761备份恢复出的隔离会话中调用原HUD与驱动；不启动Player或访问真实槽。
    public sealed class PaymentViewDriverR25Tests
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static string Output=>Environment.GetEnvironmentVariable("QY017_R25_OUTPUT")??throw new InvalidOperationException("缺少R25证据目录");
        static Type T(string n)=>Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        static object Read(object o,string n)=>o.GetType().GetField(n,Flags)?.GetValue(o)??o.GetType().GetProperty(n,Flags)?.GetValue(o);
        static void Set(object o,string n,object v){var f=o.GetType().GetField(n,Flags);if(f!=null)f.SetValue(o,v);else o.GetType().GetProperty(n,Flags).SetValue(o,v);}
        static object Call(object o,string n,params object[] args){try{return o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}}
        static void Save(string n,object v){using var s=new FileStream(Path.Combine(Output,n+".json"),FileMode.CreateNew);using var w=new StreamWriter(s);w.Write(JsonConvert.SerializeObject(v,Formatting.Indented));}
        IsolatedTestArtifacts artifacts;
        GameSession session; GameState State=>session.State;
        EffectRegistry registry; GameStateView current;
        GameObject hudObject,controllerObject,driverObject,eventObject;
        object hud,frame,dialogs,payments,driver,mobile,quantityDialog;
        InteractionRequestRouter router;
        List<object> commands;
        bool sourcesAvailable;
        readonly List<object> queries=new List<object>();
        InteractionRequest Payment=>State.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open"&&r.InteractionTypeId==ResourcePaymentChoiceEffectExecutor.InteractionTypeId);
        InteractionRequestProjection Request=>InteractionRequestProjector.ProjectForPlayer(Payment,4,current,State.GameId);
        Component View=>UnityEngine.Object.FindObjectsOfType<Component>().Single(c=>c.GetType()==T("EffectDialogShellView")&&(Button)Call(driver,"CurrentAllocationConfirm",Request)!=null&&c.gameObject.activeInHierarchy);
        Button Confirm=>(Button)Call(driver,"CurrentAllocationConfirm",Request);
        Component[] Rows=>View.GetComponentsInChildren(T("EffectDialogResourceRowView"),false).Cast<Component>().ToArray();
        static IEnumerator Drain(IEnumerator routine)
        {
            var stack=new Stack<IEnumerator>();stack.Push(routine);
            while(stack.Count>0){bool moved=stack.Peek().MoveNext();if(!moved){stack.Pop();continue;}var value=stack.Peek().Current;
                if(value is IEnumerator child)stack.Push(child);else if(value is WaitForEndOfFrame){Canvas.ForceUpdateCanvases();yield return null;}else yield return value;}
        }
        void Publish()
        {
            current=GameStateViewProjector.ProjectForPlayer(State,4);Call(frame,"BindGameplaySnapshot",current);
            router.GetType().GetProperty("CurrentRequest").SetValue(router,State.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open"&&r.InteractionTypeId==ResourcePaymentChoiceEffectExecutor.InteractionTypeId)?Request:null);
        }
        void Submit(GameCommand command)
        {
            int before=State.EffectRuntime.StateRevision;var result=session.Submit(command);
            commands.Add(new{Command=command,Result=new WorkflowSubmissionResult(result,true),BeforeRevision=before,AfterRevision=State.EffectRuntime.StateRevision});
            Assert.That(result.Succeeded,Is.True,JsonConvert.SerializeObject(result));
            Publish();Call(payments,"NotifyPaymentDraftSettled",command.CommandId,true);Call(payments,"NotifyCommandSettled",command.CommandId);
            Call(payments,"Synchronize");
        }
        PaymentSourceProjection Query(PaymentSourceQuery query)
        {
            if(!sourcesAvailable){queries.Add(new{query,ready=false});return null;}
            var response=session.QueryPaymentSourcePage(4,new PaymentSourcePageRequest{RequestId=PaymentSourcePageProtocol.Id(query),Query=GameStateCloneService.DeepClone(query)});
            queries.Add(new{query,ready=string.IsNullOrEmpty(response.Reason),response.Reason,response.Page});
            Assert.That(response.Reason,Is.Empty);return response.Page;
        }
        void Prepare()
        {
            artifacts=IsolatedTestArtifacts.BeginCase();
            string path=Path.Combine(Directory.GetCurrentDirectory(),"prompt/企业家内容实施规划-20261019/实施记录/QY-019-FOUR-PLAYER-HOTSEAT-RESUME-R10/R24结束正常自动档保全/auto-1.json");
            byte[] bytes=File.ReadAllBytes(path);using var hash=SHA256.Create();Assert.That(string.Concat(hash.ComputeHash(bytes).Select(b=>b.ToString("x2"))),Is.EqualTo("bb66a393aec98cb2374de06072365bcc06d73d5cfddff65a906e5fe6b5712b30"));
            var envelope=JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));string payload=(string)envelope["Payload"];
            Assert.That(Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload))),Is.EqualTo((string)envelope["Checksum"]));
            var data=JsonConvert.DeserializeObject<MatchSaveData>(payload);data.Validate();
            var boot=T("GameSessionBootstrapper").GetMethod("Restore",Flags).Invoke(null,new object[]{null,data});
            session=(GameSession)Read(boot,"Session");registry=(EffectRegistry)Read(boot,"EffectRegistry");
            Assert.That(State.GameId,Is.EqualTo("8cea6a46d03242b7892f6d1013bfee03"));Assert.That(State.EffectRuntime.StateRevision,Is.EqualTo(2761));
            Assert.That(State.EffectRuntime.Status,Is.EqualTo(EffectRuntimeStatus.Active));Assert.That(State.EffectRuntime.Diagnostics,Is.Empty);
            T("UiTheme").GetMethod("Initialize",Flags).Invoke(null,new[]{AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",T("UiThemeCatalog"))});
            hudObject=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            hud=hudObject.GetComponent(T("GameplayInteractionHudView"));frame=Read(hud,"Frame");dialogs=Read(hud,"DialogRegistry");
            Call(hud,"ConfigureGameplayPresentation",Read(boot,"GameplayTextCatalog"),Read(boot,"GameplayDefinitionNames"));
            current=GameStateViewProjector.ProjectForPlayer(State,4);Call(frame,"BindGameplaySnapshot",current);
            Call(Read(hud,"ExpansionUi"),"Configure",new Func<GameStateView>(()=>current),new Func<int>(()=>4),null,Read(boot,"CharacterModes"),new Action<GameCommand>(Submit));
            controllerObject=new GameObject("R25隔离未启动控制器");controllerObject.SetActive(false);mobile=controllerObject.AddComponent(T("MobileCityInteractionController"));
            router=new InteractionRequestRouter();Set(mobile,"interactionRequestRouter",router);Set(mobile,"localPlayerId",4);
            driverObject=new GameObject("R25原指针驱动有限调用");driverObject.SetActive(false);driver=driverObject.AddComponent(T("Validation.HotseatUiValidationDriver"));
            Set(driver,"mobile",mobile);Set(driver,"session",session);Set(driver,"registry",registry);Set(driver,"hud",hud);Set(driver,"output",Output);Set(driver,"selectedCase","normal-flow");commands=(List<object>)Read(driver,"commands");
            payments=Activator.CreateInstance(T("SpecialActionInteractionUiCoordinator"),new object[]{new Func<GameState>(()=>State),new Func<int>(()=>4),new Func<RectTransform>(()=>(RectTransform)Read(frame,"ContentRect")),dialogs,
                new SpecialActionOptionQueryService((MapQueryService)Read(boot,"MapQuery"),(YC.Domain.Influence.InfluenceService)Read(boot,"InfluenceService"),(YC.Domain.Movement.CityMovementService)Read(boot,"MovementService"),new SpecialActionLifecycleService()),new Action<IReadOnlyList<WorkflowHighlight>>(_=>{}),new Action(()=>{}),new Action<GameCommand>(Submit),new Action<string>(_=>{})});
            Set(payments,"ExpansionUi",Read(hud,"ExpansionUi"));Set(payments,"GetVisibleRequests",new Func<IReadOnlyList<InteractionRequest>>(()=>VisibleInteractionRequestSource.Read(State,null,4)));Set(payments,"QueryPaymentSources",new Func<PaymentSourceQuery,PaymentSourceProjection>(Query));
            Set(mobile,"specialActionInteraction",payments);if(EventSystem.current==null)eventObject=new GameObject("R25隔离EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));
            Publish();Call(payments,"Synchronize");driverObject.SetActive(true);
        }
        void CapturePayment()
        {
            var cameraObject=new GameObject("R25隔离画面导出",typeof(Camera));var camera=cameraObject.GetComponent<Camera>();
            var canvases=hudObject.GetComponentsInChildren<Canvas>(true);var modes=canvases.Select(c=>c.renderMode).ToArray();var cameras=canvases.Select(c=>c.worldCamera).ToArray();
            var target=new RenderTexture(1280,720,24);var texture=new Texture2D(1280,720,TextureFormat.RGBA32,false);var previous=RenderTexture.active;
            try{target.Create();camera.transform.position=new Vector3(0,0,-10);camera.orthographic=true;camera.targetTexture=target;
                foreach(var c in canvases){c.renderMode=RenderMode.ScreenSpaceCamera;c.worldCamera=camera;c.planeDistance=1;}
                Canvas.ForceUpdateCanvases();camera.Render();RenderTexture.active=target;texture.ReadPixels(new Rect(0,0,1280,720),0,0);texture.Apply();File.WriteAllBytes(Path.Combine(Output,"原付款隔离视图.png"),texture.EncodeToPNG());}
            finally{for(int i=0;i<canvases.Length;i++){canvases[i].renderMode=modes[i];canvases[i].worldCamera=cameras[i];}RenderTexture.active=previous;camera.targetTexture=null;target.Release();UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(cameraObject);Canvas.ForceUpdateCanvases();}
        }
        [UnityTest]
        public IEnumerator Restored2761OriginalPaymentEditsAndSinglePointerConfirmation()
        {
            yield return new EnterPlayMode();Prepare();yield return null;Canvas.ForceUpdateCanvases();yield return null;
            var original=Request;var before=GameStateCloneService.DeepClone(State.FindPlayer(4).Resources);var owner=State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==original.OwnerEffectId);
            Assert.That(owner.Payment.Demand.TotalCount,Is.EqualTo(2));Assert.That(owner.Payment.IsCommitted,Is.False);
            var layout=Read(dialogs,"EffectDialogLayoutProfile");var labels=Rows.Select(r=>((Text)Read(r,"Label")).text).ToArray();
            string expected=string.Format((string)Read(layout,"PaymentTotalDescriptionFormat"),2,string.Join((string)Read(layout,"PaymentResourceSeparator"),labels.Take(3)));
            Assert.That(((Text)Read(View,"TitleText")).text,Is.EqualTo(Read(layout,"PaymentAllocationTitle")));
            Assert.That(((Text)Read(View,"DescriptionText")).text,Does.Contain(expected));
            Assert.That(UnityEngine.Object.FindObjectsOfType<Component>().Any(c=>c.GetType().Name=="PlayerAutomationId"&&((string)Read(c,"Id")).StartsWith("payment.sources.open.",StringComparison.Ordinal)),Is.False);
            Assert.That((bool)Call(payments,"TryGetPaymentSources",original.InteractionId,null),Is.False);
            sourcesAvailable=true;yield return null;
            Assert.That((bool)Call(payments,"TryGetPaymentSources",original.InteractionId,null),Is.True);
            Save("原付款页无编辑入口且保留受授权只读查询",new{queries,Isolated=true,NormalPlayerRun=false});
            Confirm.interactable=false;Assert.Throws<InvalidOperationException>(()=>{var r=(IEnumerator)Call(driver,"Press",Confirm,"禁用控件不得付款");r.MoveNext();});Assert.That(commands,Is.Empty);Call(payments,"Synchronize");
            var stale=GameStateCloneService.DeepClone(Request);stale.StateRevision--;Assert.That(Call(driver,"CurrentAllocationConfirm",stale),Is.Null);
            Set(mobile,"localPlayerId",3);Assert.That(Call(driver,"CurrentAllocationConfirm",original),Is.Null);Set(mobile,"localPlayerId",4);
            // 只编辑本人可付的原配比：碎片2减到1再恢复2；不把缺少的异铁注入备份。
            yield return Drain((IEnumerator)Call(driver,"Press",Read(Rows[1],"DecreaseButton"),"原付款草稿减少一份碎片"));
            Assert.That(JsonConvert.SerializeObject(State.FindPlayer(4).Resources),Is.EqualTo(JsonConvert.SerializeObject(before)));Assert.That(Confirm.interactable,Is.False);
            var oldConfirm=Confirm;
            yield return Drain((IEnumerator)Call(driver,"Press",Read(Rows[1],"IncreaseButton"),"原付款草稿恢复一份碎片"));
            Assert.That(commands,Is.Empty,"同一已准备配比的本地编辑不重复准备或付款");
            Assert.That(JsonConvert.SerializeObject(State.FindPlayer(4).Resources),Is.EqualTo(JsonConvert.SerializeObject(before)));Assert.That(owner.Payment.IsCommitted,Is.False);
            var draft=Request;Assert.That(Confirm.IsInteractable(),Is.True);Confirm.name="可编辑的确认对象名称";
            Save("原付款草稿与动态显示",new{Request=draft,Title=((Text)Read(View,"TitleText")).text,Description=((Text)Read(View,"DescriptionText")).text,Summary=((Text)Read(View,"ResourceSummaryText")).text,commands,Resources=State.FindPlayer(4).Resources});
            CapturePayment();
            yield return Drain((IEnumerator)Call(driver,"RequiredAllocation"));
            Assert.That(commands.Count,Is.EqualTo(1));Assert.That(owner.Payment.IsCommitted,Is.True);Assert.That(owner.Status,Is.EqualTo(EffectNodeStatus.Completed));
            if(oldConfirm!=null)oldConfirm.onClick.Invoke();Assert.That(commands.Count,Is.EqualTo(1),"已回答的旧控件不得重复提交");
            Assert.That(State.FindPlayer(4).Resources.OriginiumShard,Is.EqualTo(before.OriginiumShard-2));Assert.That(State.FindPlayer(4).Resources.Iron,Is.EqualTo(before.Iron));
            Assert.That(State.FindPlayer(4).Resources.Originium,Is.EqualTo(before.Originium));Assert.That(State.FindPlayer(4).Resources.PureOriginium,Is.EqualTo(before.PureOriginium));Assert.That(State.FindPlayer(4).Resources.GoldVoucher,Is.EqualTo(before.GoldVoucher));
            Assert.That(State.EffectRuntime.LastFaultCode,Is.Null.Or.Empty);Assert.That(State.EffectRuntime.Diagnostics,Is.Empty);
            var invocation=State.EffectRuntime.AbilityActionInvocations.Single(i=>i.InvocationId=="action-invocation_ec9f0aeb57397585615ff0142ecf6f8e6131e23c7b74295e7ac3cbd30e8f6dc4");
            Save("准确付款及真实后继",new{commands,Before=before,After=State.FindPlayer(4).Resources,owner,invocation,NextRequests=VisibleInteractionRequestSource.Read(State,null,4),State.EffectRuntime.StateRevision,Isolated=true,NormalFullGame=false});
            Call(payments,"Clear");
            // 数量页仅验证原视图/原指针回调，使用独立投影，不声称重跑数量规则。
            quantityDialog=Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"),Flags,null,new object[]{dialogs,Read(frame,"ContentRect")},null);
            int count=-1;var quantity=new InteractionRequestProjection{InteractionId="R25.view.quantity",AnsweringPlayerId=4,StateRevision=State.EffectRuntime.StateRevision,VisibleToViewer=true,Status="open",InteractionTypeId=ContentPrimitiveEffectExecutor.QuantityInteractionTypeId};
            router.GetType().GetProperty("CurrentRequest").SetValue(router,quantity);
            Call(quantityDialog,"ShowQuantity",quantity,"",0,1,new[]{0},new Action<IReadOnlyList<int>>(_=>{}),new Func<bool>(()=>true),new Action<int>(n=>count=n));yield return null;
            var q=(Button)Call(driver,"CurrentAllocationConfirm",quantity);Assert.That(q,Is.Not.Null);q.name="数量页可编辑名称";
            yield return Drain((IEnumerator)Call(driver,"Press",q,"原数量页确认回调"));Assert.That(count,Is.Zero);Assert.That(commands.Count,Is.EqualTo(1));
            Save("数量页原引用与指针回调",new{QuantityCallback=count,UnchangedCommandCount=commands.Count,ViewOnly=true,QuantityRulesRetested=false});
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if(driver!=null)foreach(var writer in ((IDictionary)Read(driver,"appendLogs")).Values.Cast<StreamWriter>())writer.Dispose();
            if(payments is IDisposable p)p.Dispose();if(quantityDialog is IDisposable q)q.Dispose();
            foreach(var obj in new[]{driverObject,controllerObject,hudObject,eventObject})if(obj!=null)UnityEngine.Object.DestroyImmediate(obj);
            artifacts?.Finish();if(UnityEngine.Application.isPlaying)yield return new ExitPlayMode();
        }
    }
}
