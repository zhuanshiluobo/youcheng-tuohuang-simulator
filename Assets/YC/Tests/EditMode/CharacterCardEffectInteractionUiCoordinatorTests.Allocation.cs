using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed partial class CharacterCardEffectInteractionUiCoordinatorTests
    {
        private Scene allocationScene;
        private bool closeAllocationScene;
        private IsolatedTestArtifacts allocationEvidence;
        private static Type AllocationType(string name)=>Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        private static object AllocationCall(object target,string method,params object[] args)=>target.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==method && m.GetParameters().Length==args.Length).Invoke(target,args);
        private static object AllocationField(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);
        private static GameplayTextDocument AllocationText(GameplayTextRef reference)=>new GameplayTextDocument(new[]{new TextRun(reference.Key)},reference.Key);
        private static void AllocationRender(Component host,GameplayTextDocument doc,bool singleLine)
        {var text=host as Text ?? host.GetComponent<Text>();if(text!=null)text.text=doc?.ToPlainText() ?? "";}
        private object AllocationDialog(string path="Assets/Scenes/ThreePlayerScene.unity")
        {
            allocationEvidence=IsolatedTestArtifacts.BeginCase();allocationScene=SceneManager.GetSceneByPath(path);closeAllocationScene=!allocationScene.IsValid() || !allocationScene.isLoaded;
            if(closeAllocationScene)allocationScene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
            var registry=allocationScene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren(AllocationType("GameplayDialogRegistry"),true)).Single();
            allocationEvidence.WriteLabeledText("实际场景引用来源_隔离组件验收",Newtonsoft.Json.JsonConvert.SerializeObject(new {scene=path,sceneSha256=YC.Infrastructure.Lua.LuaContentHasher.ComputeSha256(System.IO.File.ReadAllText(path)),registry=registry.name,serializedSceneRegistry=UnityEditor.EditorJsonUtility.ToJson(registry),scope="读取实际场景含覆盖的引用，只实例化当前模板；无生成器/保存，不计正常UI或视觉验收"}));
            canvasObject=new GameObject("分配模式隔离画布",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));canvasObject.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            var dialog=Activator.CreateInstance(AllocationType("CharacterCardEffectChoiceDialog"),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{registry,(RectTransform)canvasObject.transform},null);
            AllocationCall(dialog,"BindGameplayText",new Func<GameplayTextRef,GameplayTextDocument>(AllocationText),new Action<Component,GameplayTextDocument,bool>(AllocationRender));return dialog;
        }
        private IInteractionRequestRenderer AllocationRenderer(GameState state,object dialog,List<GameCommand> commands,Action<IReadOnlyList<EffectAllocationDraftEntry>> observe=null)
        {
            var renderer=CreateGenericRenderer(state,dialog,commands);
            renderer.GetType().GetProperty("RenderAllocationQueue").SetValue(renderer,new Action<string,IReadOnlyList<EffectAllocationDraftEntry>,int>((key,entries,rev)=>observe?.Invoke(entries)));
            renderer.GetType().GetProperty("ClearAllocationQueue").SetValue(renderer,new Action<string>(_=>{}));return renderer;
        }
        private static GameState AllocationState(InteractionRequest request)
        {var state=CreateState();state.ContentPackHash="allocation-ui-isolated";state.EffectRuntime.StateRevision=request.StateRevision;state.EffectRuntime.InteractionRequests.Add(request);return state;}
        [TearDown] public void FinishAllocationEvidence()
        {
            if(closeAllocationScene && allocationScene.IsValid() && allocationScene.isLoaded)EditorSceneManager.CloseScene(allocationScene,true);
            closeAllocationScene=false;allocationEvidence?.Finish();allocationEvidence=null;
        }
        [TestCase("Assets/Scenes/ThreePlayerScene.unity")][TestCase("Assets/Scenes/SampleScene.unity")]
        public void AllocationUiUsesCurrentSceneSaleRowsAndKeepsABADraftOnRefresh(string scene)
        {
            var q=EffectAllocationDraftTests.Request();var state=AllocationState(q);var dialog=AllocationDialog(scene);var commands=new List<GameCommand>();string[] queue=Array.Empty<string>();
            var renderer=AllocationRenderer(state,dialog,commands,entries=>queue=entries.Select(e=>e.OptionId).ToArray());renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));
            var overlay=GetOverlay(dialog);ClickButton(overlay,"Effect Allocation Increase 0");ClickButton(overlay,"Effect Allocation Increase 1");ClickButton(overlay,"Effect Allocation Increase 0");Assert.That(queue,Is.EqualTo(new[]{"A","B","A"}));
            ClickButton(overlay,"Effect Allocation Decrease 0");Assert.That(queue,Is.EqualTo(new[]{"A","B"}));
            var input=FindChild(overlay,"Effect Allocation Value 0").GetComponentInParent<InputField>();if(input==null)input=FindChild(overlay,"Effect Allocation Value 0").GetComponent<InputField>();Assert.That(input,Is.Not.Null);
            input.text="2";Assert.That(queue,Is.EqualTo(new[]{"A","B","A"}));input.text="3";input.onEndEdit.Invoke("3");Assert.That(queue,Is.EqualTo(new[]{"A","B","A"}));Assert.That(input.text,Is.EqualTo("2"));
            q.StateRevision++;state.EffectRuntime.StateRevision=q.StateRevision;renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));Assert.That(queue,Is.EqualTo(new[]{"A","B","A"}));
            Assert.That(FindChild(GetOverlay(dialog),"Effect Allocation Increase 2").GetComponent<Button>().interactable,Is.False);Assert.That(commands,Is.Empty);renderer.Clear();
        }
        [TestCase(false)][TestCase(true)] public void AllocationUiConfirmsOrEndsOnceWithTheExactFourFieldQueue(bool end)
        {
            var q=EffectAllocationDraftTests.Request();var state=AllocationState(q);var dialog=AllocationDialog();var commands=new List<GameCommand>();var renderer=AllocationRenderer(state,dialog,commands);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));var overlay=GetOverlay(dialog);
            if(!end){ClickButton(overlay,"Effect Allocation Increase 0");ClickButton(overlay,"Effect Allocation Increase 1");ClickButton(overlay,"Effect Allocation Increase 0");}
            var action=FindChild(overlay,end?"End Effect Allocation":"Confirm Effect Allocation").GetComponent<Button>().onClick;action.Invoke();action.Invoke();
            Assert.That(commands.Count,Is.EqualTo(1));Assert.That(commands[0].Kind,Is.EqualTo(GameCommandKind.AnswerInteraction));Assert.That(commands[0].OptionIds,Is.Empty);
            Assert.That(AllocationSequenceAnswerJson.TryDecode(commands[0].Parameters[AllocationSequenceAnswerJson.Parameter],out var answer),Is.True);Assert.That(AllocationEffects.S(answer,"submitKind"),Is.EqualTo(end?"end_action":"confirm"));
            Assert.That(YC.Domain.Effects.ItemTransferEffectExecutor.Value(answer,"sequence").Items.Select(v=>v.StringValue),Is.EqualTo(end?Array.Empty<string>():new[]{"A","B","A"}));renderer.Clear();
        }
        [Test] public void AllocationUiOldConfirmDoesNotSubmitAfterCandidateReplacementAndClosingDoesNotSubmit()
        {
            var q=EffectAllocationDraftTests.Request();var state=AllocationState(q);var dialog=AllocationDialog();var commands=new List<GameCommand>();var renderer=AllocationRenderer(state,dialog,commands);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));var overlay=GetOverlay(dialog);ClickButton(overlay,"Effect Allocation Increase 0");ClickButton(overlay,"Effect Allocation Increase 0");ClickButton(overlay,"Effect Allocation Increase 0");
            var confirm=FindChild(overlay,"Confirm Effect Allocation").GetComponent<Button>().onClick;q.CandidateSetVersion++;q.StateRevision++;state.EffectRuntime.StateRevision=q.StateRevision;confirm.Invoke();Assert.That(commands,Is.Empty);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));Assert.That(GetText(GetOverlay(dialog),"Effect Allocation Value 0"),Is.EqualTo("0"));AllocationCall(dialog,"Hide");Assert.That(commands,Is.Empty);renderer.Clear();
        }
        [Test] public void AllocationUiEndDisabledAndFormalTypeOutranksOldSaleSemantic()
        {
            var q=EffectAllocationDraftTests.Request(false);q.PromptParameters.Properties.Add(new NormalizedValueEntry{Name="interactionSemantic",Value=NormalizedValue.CreateString(ContentPrimitiveEffectExecutor.ResourceSaleSemantic)});
            var state=AllocationState(q);var dialog=AllocationDialog();var renderer=AllocationRenderer(state,dialog,new List<GameCommand>());renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));
            Assert.That(FindChild(GetOverlay(dialog),"End Effect Allocation"),Is.Null);Assert.That(FindChild(GetOverlay(dialog),"Effect Allocation Increase 0"),Is.Not.Null);Assert.That(FindChild(GetOverlay(dialog),"Character Sale Summary"),Is.Null);
            q.AnswerSchema="candidate_id_array";Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(q,1)),Is.False);renderer.Clear();
        }
        [TestCase(false)][TestCase(true)] public void AllocationUiAcknowledgementDoesNotResubmitOrUnlockAnAcceptedDraft(bool accepted)
        {
            var q=EffectAllocationDraftTests.Request();var state=AllocationState(q);var dialog=AllocationDialog();var commands=new List<GameCommand>();var renderer=AllocationRenderer(state,dialog,commands);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));var overlay=GetOverlay(dialog);ClickButton(overlay,"Effect Allocation Increase 0");ClickButton(overlay,"Effect Allocation Increase 1");ClickButton(overlay,"Effect Allocation Increase 0");ClickButton(overlay,"Confirm Effect Allocation");
            Assert.That(commands.Count,Is.EqualTo(1));AllocationCall(renderer,"NotifyInteractionCommandResolved",state.GameId,commands[0].CommandId,accepted,CommandErrorCode.InvalidTarget);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));overlay=GetOverlay(dialog);Assert.That(commands.Count,Is.EqualTo(1));
            Assert.That(FindChild(overlay,"Effect Allocation Decrease 0").GetComponent<Button>().interactable,Is.EqualTo(!accepted));Assert.That(FindChild(overlay,"End Effect Allocation").GetComponent<Button>().interactable,Is.EqualTo(!accepted));
            if(accepted){ClickButton(overlay,"End Effect Allocation");Assert.That(commands.Count,Is.EqualTo(1));}
            else {Assert.That(GetText(overlay,"Effect Allocation Value 0"),Is.EqualTo("2"));ClickButton(overlay,"Confirm Effect Allocation");Assert.That(commands.Count,Is.EqualTo(2));}
            renderer.Clear();
        }
        [Test] public void AllocationUiActualSceneStripKeepsRealSourcesAndBlocksAllPreviewActions()
        {
            AllocationDialog();var source=allocationScene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren(AllocationType("SettlementEffectStripView"),true)).Single();
            var clone=UnityEngine.Object.Instantiate(source.gameObject,canvasObject.transform,false);try
            {
                var strip=clone.GetComponent(AllocationType("SettlementEffectStripView"));AllocationCall(strip,"SetScope","game/hash",1);
                AllocationCall(strip,"ConfigurePresentation",new Func<GameplayTextRef,GameplayTextDocument>(AllocationText),new Action<Component,GameplayTextDocument,bool>(AllocationRender),new Func<Component,GameplayTextDocument,float>((_,d)=>d.ToPlainText().Length*10f),new Func<string,string,Sprite>((_,__)=>null));
                var option=new YC.Application.Sessions.SettlementOptionView{OptionId="real",WindowId="window",EntryKind="optional_insertion",SourceItemId="actual-source",SourceDefinitionId="actual-definition",AnsweringPlayerId=1,StateRevision=1,IsEnabled=true,CanInspectSource=true,VisibleToViewer=true,Presentation=new SettlementPresentationDefinition{Label=new GameplayTextRef("core:actual"),Description=new GameplayTextRef("core:actual")}};
                int use=0,inspect=0;AllocationCall(strip,"BindWindow","window",new[]{option},new Action<YC.Application.Sessions.SettlementOptionView>(_=>use++),new Action<string>(_=>inspect++));
                var draft=new EffectAllocationDraft();draft.Bind("game","hash",1,InteractionRequestProjector.ProjectForPlayer(EffectAllocationDraftTests.Request(),1));draft.TrySetAmount(0,1);draft.TrySetAmount(1,1);draft.TrySetAmount(0,2);
                AllocationCall(strip,"ShowAllocationQueue","request-a",draft.Entries,1);Assert.That((int)strip.GetType().GetProperty("VisibleOptionCount").GetValue(strip),Is.EqualTo(4));
                var chips=(System.Collections.IDictionary)AllocationField(strip,"allocationChips");Assert.That(chips.Count,Is.EqualTo(3));
                foreach(Component chip in chips.Values)
                {
                    var button=(Button)AllocationField(chip,"hitTarget");Assert.That(button.interactable,Is.False);var pointer=new PointerEventData(null){pointerId=1,button=PointerEventData.InputButton.Left};
                    AllocationCall(chip,"OnPointerDown",pointer);AllocationCall(chip,"OnPointerUp",pointer);AllocationCall(chip,"OnPointerClick",pointer);pointer.button=PointerEventData.InputButton.Right;AllocationCall(chip,"OnPointerDown",pointer);AllocationCall(chip,"OnPointerUp",pointer);AllocationCall(chip,"OnPointerClick",pointer);AllocationCall(chip,"Inspect");AllocationCall(chip,"Update");ExecuteEvents.Execute(chip.gameObject,new BaseEventData(null),ExecuteEvents.submitHandler);
                }
                Assert.That(use,Is.Zero);Assert.That(inspect,Is.Zero);AllocationCall(strip,"ClearAllocationQueue","stale-request");Assert.That(chips.Count,Is.EqualTo(3));
                AllocationCall(strip,"ClearWindow");Assert.That(chips.Count,Is.EqualTo(3));AllocationCall(strip,"BindWindow","window",new[]{option},new Action<YC.Application.Sessions.SettlementOptionView>(_=>use++),new Action<string>(_=>inspect++));
                AllocationCall(strip,"ClearAllocationQueue","request-a");Assert.That((int)strip.GetType().GetProperty("VisibleOptionCount").GetValue(strip),Is.EqualTo(1));
                var realChips=(System.Collections.IDictionary)AllocationField(strip,"chips");var real=(Component)realChips["real"];var realPointer=new PointerEventData(null){pointerId=7,button=PointerEventData.InputButton.Left};
                AllocationCall(real,"OnPointerDown",realPointer);AllocationCall(real,"OnPointerUp",realPointer);AllocationCall(real,"OnPointerClick",realPointer);Assert.That(use,Is.EqualTo(1));
                realPointer.button=PointerEventData.InputButton.Right;AllocationCall(real,"OnPointerDown",realPointer);AllocationCall(real,"OnPointerUp",realPointer);AllocationCall(real,"OnPointerClick",realPointer);Assert.That(inspect,Is.EqualTo(1));
                var longDraft=new EffectAllocationDraft();longDraft.Bind("game","hash",1,InteractionRequestProjector.ProjectForPlayer(EffectAllocationDraftTests.Request(true,99,99,0),1));longDraft.TrySetAmount(0,99);
                AllocationCall(strip,"ShowAllocationQueue","long-request",longDraft.Entries,2);Assert.That(chips.Count,Is.EqualTo(99));AllocationCall(strip,"ClearAllocationQueue","long-request");Assert.That(realChips.Count,Is.EqualTo(1));
            }
            finally{UnityEngine.Object.DestroyImmediate(clone);}
        }
    }
}
