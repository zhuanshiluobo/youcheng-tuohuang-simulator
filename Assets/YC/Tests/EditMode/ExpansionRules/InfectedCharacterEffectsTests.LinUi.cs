using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Presentation;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        private static Type LinUiType(string name)=>Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        private static object LinUiCall(object instance,string method,params object[] args)=>instance.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==method && m.GetParameters().Length==args.Length).Invoke(instance,args);
        private static void LinUiRender(Component component,GameplayTextDocument doc,bool singleLine)
        {var text=component as Text ?? component.GetComponent<Text>();if(text!=null)text.text=doc?.ToPlainText()??"";}
        [TestCase("Assets/Scenes/ThreePlayerScene.unity")][TestCase("Assets/Scenes/SampleScene.unity")]
        public void LinUiActualSceneAllocationQueueAndPurchasePageSubmitTheFrozenRealStrategy(string scenePath)
        {
            var state=LinState(new ResourceSet{Originium=1});var scene=SceneManager.GetSceneByPath(scenePath);bool close=!scene.IsValid() || !scene.isLoaded;if(close)scene=EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Additive);
            GameObject canvas=null,stripObject=null;IInteractionRequestRenderer renderer=null;
            try
            {
                var roots=scene.GetRootGameObjects();var dialogRegistry=roots.SelectMany(r=>r.GetComponentsInChildren(LinUiType("GameplayDialogRegistry"),true)).Single();
                var stripSource=roots.SelectMany(r=>r.GetComponentsInChildren(LinUiType("SettlementEffectStripView"),true)).Single();
                artifacts.WriteLabeledText("林实际场景UI引用_隔离范围",JsonConvert.SerializeObject(new {scenePath,sceneSha256=LuaContentHasher.ComputeSha256(System.IO.File.ReadAllText(scenePath)),registry=EditorJsonUtility.ToJson(dialogRegistry),strip=EditorJsonUtility.ToJson(stripSource),scope="读取实际场景含覆盖引用；模拟组件按钮事件及真实命令/Host，非正常菜单或视觉验收；无保存/生成器"}));
                canvas=new GameObject("林真实协议隔离画布",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
                var dialog=Activator.CreateInstance(LinUiType("CharacterCardEffectChoiceDialog"),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{dialogRegistry,(RectTransform)canvas.transform},null);
                var textResolver=new GameplayTextResolver(pack.Presentation);GameplayTextDocument Resolve(GameplayTextRef text)=>textResolver.Resolve(text,new GameplayTextContext{Catalog=pack.Presentation,ContentHash=pack.ContentHash,Locale="zh-CN",ViewerPlayerId=1,StateRevision=state.EffectRuntime.StateRevision});
                LinUiCall(dialog,"BindGameplayText",new Func<GameplayTextRef,GameplayTextDocument>(Resolve),new Action<Component,GameplayTextDocument,bool>(LinUiRender));
                stripObject=UnityEngine.Object.Instantiate(stripSource.gameObject,canvas.transform,false);var strip=stripObject.GetComponent(LinUiType("SettlementEffectStripView"));LinUiCall(strip,"SetScope",state.GameId+":"+state.ContentPackHash,1);
                LinUiCall(strip,"ConfigurePresentation",new Func<GameplayTextRef,GameplayTextDocument>(Resolve),new Action<Component,GameplayTextDocument,bool>(LinUiRender),new Func<Component,GameplayTextDocument,float>((_,d)=>d.ToPlainText().Length*10f),new Func<string,string,Sprite>((_,__)=>null));
                var commands=new List<GameCommand>();renderer=(IInteractionRequestRenderer)Activator.CreateInstance(LinUiType("CharacterAbilityInteractionUiCoordinator"),new object[]{new Func<GameState>(()=>state),new Func<int>(()=>1),dialog,new Action<IReadOnlyList<WorkflowHighlight>>(_=>{}),new Action(()=>{}),new Action<GameCommand>(commands.Add),new Action<string>(_=>{})});
                renderer.GetType().GetProperty("GetVisibleState").SetValue(renderer,new Func<GameStateView>(()=>View(state)));
                renderer.GetType().GetProperty("RenderAllocationQueue").SetValue(renderer,new Action<string,IReadOnlyList<EffectAllocationDraftEntry>,int>((key,entries,revision)=>LinUiCall(strip,"ShowAllocationQueue",key,entries,revision)));
                renderer.GetType().GetProperty("ClearAllocationQueue").SetValue(renderer,new Action<string>(key=>LinUiCall(strip,"ClearAllocationQueue",key)));
                var q=Open(state);renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));
                Button Button(string name)=>canvas.GetComponentsInChildren<Button>(true).Single(b=>b.name==name && b.gameObject.activeInHierarchy);
                Button("Effect Allocation Increase 0").onClick.Invoke();Button("Effect Allocation Increase 1").onClick.Invoke();Assert.That((int)strip.GetType().GetProperty("AllocationPreviewCount").GetValue(strip),Is.EqualTo(2));
                var rowLabels=q.Presentation.QuantityRows.Select(row=>new {row.RowId,refKey=row.Label.Key,text=Resolve(row.Label).ToPlainText()}).ToArray();
                Button("End Effect Allocation").onClick.Invoke();Assert.That(commands.Count,Is.EqualTo(1));Assert.That(AllocationSequenceAnswerJson.TryDecode(commands[0].Parameters[AllocationSequenceAnswerJson.Parameter],out var answer),Is.True);
                Assert.That(YC.Domain.Effects.ItemTransferEffectExecutor.Value(answer,"sequence").Items.Select(v=>v.StringValue),Is.EqualTo(new[]{"sell_originium","buy_originium"}));
                var submitted=new AnswerInteractionCommandHandler(registry).Handle(state,commands[0]);Assert.That(submitted.Succeeded,Is.True,submitted.LogMessage);renderer.Clear();Assert.That((int)strip.GetType().GetProperty("AllocationPreviewCount").GetValue(strip),Is.Zero);
                q=Open(state);Assert.That(q.InteractionTypeId,Is.EqualTo("action.decline_effect"));var payment=Activator.CreateInstance(LinUiType("EventChoiceDialog"),new object[]{dialogRegistry,new Func<RectTransform>(()=>(RectTransform)canvas.transform)});
                var request=InteractionRequestProjector.ProjectForPlayer(q,1,View(state),state.GameId);var prompt=Resolve(request.Presentation.Prompt);var paymentCommands=new List<GameCommand>();
                LinUiCall(payment,"ShowConditionPayment",request,prompt,new Action<Component,GameplayTextDocument,bool>(LinUiRender),new Func<bool>(()=>q.Status=="open"),new Action(()=>paymentCommands.Add(EffectInteractionCommands.Answer(request,1,new[]{"continue"}))),new Action(()=>paymentCommands.Add(EffectInteractionCommands.Answer(request,1,new[]{"decline"}))));
                var paymentView=payment.GetType().GetProperty("Copy").GetValue(payment);var execute=(Button)paymentView.GetType().GetProperty("ConditionPaymentExecuteButton").GetValue(paymentView);Assert.That(execute.interactable,Is.True);execute.onClick.Invoke();execute.onClick.Invoke();Assert.That(paymentCommands.Count,Is.EqualTo(1));
                submitted=new AnswerInteractionCommandHandler(registry).Handle(state,paymentCommands[0]);Assert.That(submitted.Succeeded,Is.True,submitted.LogMessage);Assert.That(state.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(2));Assert.That(state.FindPlayer(1).Resources.Originium,Is.EqualTo(1));Assert.That(Open(state),Is.Null);
                artifacts.WriteLabeledText("林实际UI组件覆盖及正式回答",JsonConvert.SerializeObject(new {scenePath,rowLabels,paymentPrompt=prompt.ToPlainText(),commands,paymentCommands,result=LinAllocator(state).AllocationV1.Result,scope="当前场景引用与真实冻结正文/协议的组件隔离；不计正常可见独立程序或美术视觉一致"}));
            }
            finally {renderer?.Clear();if(canvas!=null)UnityEngine.Object.DestroyImmediate(canvas);if(close && scene.IsValid() && scene.isLoaded)EditorSceneManager.CloseScene(scene,true);}
        }
    }
}
