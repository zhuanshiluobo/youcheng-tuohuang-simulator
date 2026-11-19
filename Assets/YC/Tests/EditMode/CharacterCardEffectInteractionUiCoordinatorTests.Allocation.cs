using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Presentation;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;
using YC.Infrastructure.Lua;
using UnityEditor;
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
        [Test] public void FoldedAllocationKeepsItsDocumentForTheSameRequestAndClearsItForAnother()
        {
            var q=EffectAllocationDraftTests.Request();var state=AllocationState(q);var dialog=AllocationDialog();
            var frameType=AllocationType("GameplayHudFrame");
            var frame=allocationScene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren(frameType,true)).Single();
            if(!ReferenceEquals(frameType.GetProperty("Active").GetValue(null),frame))AllocationCall(frame,"Awake");
            AllocationCall(frame,"SetRequest",q.InteractionId,q.StateRevision);
            var richType=AllocationType("GameplayTextView");
            var style=AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset",AllocationType("GameplayTextStyle"));
            var icons=AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset",AllocationType("GameplayIconCatalog"));
            var catalog=ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory,"Assets/StreamingAssets/Content/core")).Presentation;
            Action<Component,GameplayTextDocument,bool> render=(host,doc,_)=>{
                richType.GetMethod("Configure").Invoke(host,new object[]{host.GetComponent<Text>(),style,icons,catalog});
                richType.GetMethod("Bind").Invoke(host,new object[]{doc});
            };
            AllocationCall(dialog,"BindGameplayText",new Func<GameplayTextRef,GameplayTextDocument>(AllocationText),render);
            var commands=new List<GameCommand>();var renderer=AllocationRenderer(state,dialog,commands);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));var overlay=GetOverlay(dialog);
            ClickButton(overlay,"Effect Allocation Increase 0");
            var row=FindChild(overlay,"Resource Allocation Row 0").GetComponent(AllocationType("EffectDialogResourceRowView"));
            var label=FindChild(overlay,"Effect Allocation Label 0");var rich=label.GetComponent(richType);
            Assert.That((bool)richType.GetProperty("HasDocument").GetValue(rich),Is.True);
            AllocationCall(frame,"SuspendEffectForInformation");
            // EditMode不保证启停回调；按真实组件生命周期显式执行。
            AllocationCall(row,"OnDisable");AllocationCall(rich,"OnDisable");
            Assert.That((bool)richType.GetProperty("HasDocument").GetValue(rich),Is.True,"收起同一请求不能清掉数量行正文");
            AllocationCall(frame,"ResumeEffectPage");AllocationCall(rich,"OnEnable");Canvas.ForceUpdateCanvases();
            Assert.That((bool)richType.GetProperty("HasDocument").GetValue(rich),Is.True);
            Assert.That(GetText(overlay,"Effect Allocation Value 0"),Is.EqualTo("1"));Assert.That(commands,Is.Empty);
            AllocationCall(frame,"SetRequest","different-request",q.StateRevision+1);AllocationCall(rich,"OnEnable");
            Assert.That((bool)richType.GetProperty("HasDocument").GetValue(rich),Is.False,"新请求不得恢复旧条目");
            renderer.Clear();
        }
        [Test] public void RealLinStrategyKeepsAllEightTradeRowsAfterFolding()
        {
            var lin=new InfectedCharacterEffectsTests();lin.BeginEvidence();
            try
            {
                // 规则夹具载入未修改的正式林脚本并通过角色盖放进入真实分配请求；不计正常对局验收。
                var state=(GameState)typeof(InfectedCharacterEffectsTests).GetMethod("LinState",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(lin,new object[]{null});
                var q=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open" && r.InteractionTypeId==AllocationEffects.InteractionType);
                Assert.That(q.CandidateIds.Count,Is.EqualTo(8));
                var dialog=AllocationDialog();var frameType=AllocationType("GameplayHudFrame");
                var frame=allocationScene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren(frameType,true)).Single();
                if(!ReferenceEquals(frameType.GetProperty("Active").GetValue(null),frame))AllocationCall(frame,"Awake");
                AllocationCall(frame,"SetRequest",q.GetStableInteractionId(),q.StateRevision);
                var richType=AllocationType("GameplayTextView");
                var style=AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset",AllocationType("GameplayTextStyle"));
                var icons=AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset",AllocationType("GameplayIconCatalog"));
                var catalog=ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory,"Assets/StreamingAssets/Content/core")).Presentation;
                var resolver=new GameplayTextResolver(catalog);
                Func<GameplayTextRef,GameplayTextDocument> resolve=reference=>resolver.Resolve(reference,new GameplayTextContext{Catalog=catalog});
                Action<Component,GameplayTextDocument,bool> render=(host,document,_)=>{
                    richType.GetMethod("Configure").Invoke(host,new object[]{host.GetComponent<Text>(),style,icons,catalog});
                    richType.GetMethod("Bind").Invoke(host,new object[]{document});
                };
                AllocationCall(dialog,"BindGameplayText",resolve,render);
                var commands=new List<GameCommand>();var renderer=AllocationRenderer(state,dialog,commands);
                renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));var overlay=GetOverlay(dialog);
                ClickButton(overlay,"Effect Allocation Increase 0");
                var rows=Enumerable.Range(0,8).Select(i=>FindChild(overlay,"Resource Allocation Row "+i)
                    .GetComponent(AllocationType("EffectDialogResourceRowView"))).ToArray();
                var hosts=Enumerable.Range(0,8).Select(i=>FindChild(overlay,"Effect Allocation Label "+i).GetComponent(richType)).ToArray();
                string[] expected=q.Presentation.QuantityRows.Select(row=>resolve(row.Label).ToPlainText()).ToArray();
                Canvas.ForceUpdateCanvases();Canvas.ForceUpdateCanvases();
                AllocationCall(frame,"SuspendEffectForInformation");
                foreach(var row in rows)AllocationCall(row,"OnDisable");
                foreach(var host in hosts)AllocationCall(host,"OnDisable");
                AllocationCall(frame,"ResumeEffectPage");foreach(var host in hosts)AllocationCall(host,"OnEnable");
                Canvas.ForceUpdateCanvases();Canvas.ForceUpdateCanvases();
                for(int i=0;i<hosts.Length;i++)
                {
                    Assert.That((bool)richType.GetProperty("HasDocument").GetValue(hosts[i]),Is.True);
                    var doc=(GameplayTextDocument)AllocationField(hosts[i],"document");
                    Assert.That(doc.ToPlainText(),Is.EqualTo(expected[i]));
                    Assert.That(doc.Runs.OfType<IconRun>(),Is.Not.Empty,"正式交易文案的图标不能丢失");
                    Assert.That(hosts[i].GetComponentsInChildren<Image>(true).Any(image=>image.gameObject.activeInHierarchy && image.sprite!=null),Is.True,
                        "恢复后必须实际显示正式交易图标");
                }
                Assert.That(GetText(overlay,"Effect Allocation Value 0"),Is.EqualTo("1"));Assert.That(commands,Is.Empty);
                renderer.Clear();
            }
            finally {lin.FinishEvidence();}
        }
        [TestCase(false)][TestCase(true)] public void ReusedAllocationButtonCanCancelAnOptionalAction(bool fold)
        {
            var q=EffectAllocationDraftTests.Request();var state=AllocationState(q);var dialog=AllocationDialog();var commands=new List<GameCommand>();
            var renderer=AllocationRenderer(state,dialog,commands);renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));
            var overlay=GetOverlay(dialog);Assert.That(FindChild(overlay,"Confirm Effect Allocation").GetComponent<Button>().interactable,Is.False);
            if(fold){overlay.SetActive(false);overlay.SetActive(true);}renderer.Clear();q.Status="answered";
            var choice=new InteractionRequest {InteractionId="fixture.special",SourceNodeId="fixture.special.owner",OwnerEffectId="fixture.special.owner",
                InteractionTypeId="effect.choice.execute",AnswerSchema="candidate_id",AnsweringPlayerId=1,Visibility="owner",Status="open",
                CandidateSetId="fixture.special.candidates",CandidateSetVersion=1,StateRevision=2,MinSelections=0,MaxSelections=1,AllowDecline=true,
                CandidateIds=new List<string>{"fixture.special.option"}};
            state.EffectRuntime.StateRevision=2;state.EffectRuntime.InteractionRequests.Add(choice);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(choice,1));overlay=GetOverlay(dialog);
            var cancel=FindChild(overlay,"Cancel Character Effect").GetComponent<Button>();
            Assert.That(cancel.interactable,Is.True,"新请求的取消按钮不能继承旧确认按钮的禁用状态");
            cancel.onClick.Invoke();Assert.That(commands.Count,Is.EqualTo(1));
            Assert.That(commands[0].Parameters[YC.Application.Interactions.AnswerInteractionCommandHandler.AnswerValueParameter],Is.EqualTo("false"));
            renderer.Clear();
        }
        [Test] public void NewlyOpenedOptionalActionCanBeCancelledWithoutFolding()
        {
            var q=new InteractionRequest {InteractionId="fixture.special.first",SourceNodeId="fixture.special.owner",OwnerEffectId="fixture.special.owner",
                InteractionTypeId="effect.choice.execute",AnswerSchema="candidate_id",AnsweringPlayerId=1,Visibility="owner",Status="open",
                CandidateSetId="fixture.special.first.candidates",CandidateSetVersion=1,StateRevision=1,MinSelections=0,MaxSelections=1,AllowDecline=true,
                CandidateIds=new List<string>{"fixture.special.option"}};
            var state=AllocationState(q);var dialog=AllocationDialog();var commands=new List<GameCommand>();var renderer=AllocationRenderer(state,dialog,commands);
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1));
            var cancel=FindChild(GetOverlay(dialog),"Cancel Character Effect").GetComponent<Button>();
            Assert.That(cancel.interactable,Is.True);
            cancel.onClick.Invoke();cancel.onClick.Invoke();
            Assert.That(commands.Count,Is.EqualTo(1));
            Assert.That(commands[0].Parameters[YC.Application.Interactions.AnswerInteractionCommandHandler.AnswerValueParameter],Is.EqualTo("false"));
            renderer.Clear();
        }
        [Test] public void OptionalSpecialChoicesShowNamesAndDescriptionsFromOwnProjectedSources()
        {
            var q=new InteractionRequest {InteractionId="fixture.special",SourceNodeId="fixture.special.owner",OwnerEffectId="fixture.special.owner",
                InteractionTypeId="effect.choice.execute",AnswerSchema="candidate_id",AnsweringPlayerId=1,Visibility="owner",Status="open",
                CandidateSetId="fixture.special.candidates",CandidateSetVersion=1,StateRevision=1,MinSelections=0,MaxSelections=1,AllowDecline=true,
                CandidateIds=new List<string>{"ability-action_fixture-pioneer","ability-action_fixture-enterprise"}};
            var state=AllocationState(q);var dialog=AllocationDialog();var commands=new List<GameCommand>();var renderer=AllocationRenderer(state,dialog,commands);
            var visible=GameStateViewProjector.ProjectForPlayer(state,1);visible.HasAbilityActionCatalog=true;
            var paths=new[]{"city_style_pioneer_command_hub","city_style_large_enterprise_park"};
            var definitions=paths.Select(id=>JObject.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory,"Assets/StreamingAssets/Content/core/city_style",id+".json")))).ToArray();
            for(int i=0;i<paths.Length;i++)visible.AbilityActions.Add(new AbilityActionOption {OptionId=q.CandidateIds[i],EntryKind="special",
                DisplayName=(string)definitions[i]["displayName"],Description=(string)definitions[i]["data"]["specialAction"]["description"],
                SourceRef=new AbilitySourceRef{AuthorizedPlayerId=1,DefinitionId=paths[i]}});
            renderer.GetType().GetProperty("GetVisibleState").SetValue(renderer,new Func<GameStateView>(()=>visible));
            renderer.Render(InteractionRequestProjector.ProjectForPlayer(q,1,visible,state.GameId));var overlay=GetOverlay(dialog);
            for(int i=0;i<paths.Length;i++)
            {
                var row=FindChild(overlay,"Character Effect Option "+i).GetComponent(AllocationType("UiEffectRowView"));
                Assert.That(((Text)row.GetType().GetProperty("TitleText").GetValue(row)).text,Is.EqualTo((string)definitions[i]["displayName"]));
                Assert.That(((Text)row.GetType().GetProperty("DescriptionText").GetValue(row)).text,Is.EqualTo((string)definitions[i]["data"]["specialAction"]["description"]));
            }
            var own=renderer.GetType().GetMethod("OwnProjectedSpecialAction",BindingFlags.Instance|BindingFlags.NonPublic);
            var projected=InteractionRequestProjector.ProjectForPlayer(q,1,visible,state.GameId);
            Assert.That(own.Invoke(renderer,new object[]{projected,q.CandidateIds[0],visible}),Is.Not.Null);
            visible.ViewerPlayerId=2;
            Assert.That(own.Invoke(renderer,new object[]{projected,q.CandidateIds[0],visible}),Is.Null,"不能借他人投影读取来源");
            visible.ViewerPlayerId=1;visible.GameId="different-game";
            Assert.That(own.Invoke(renderer,new object[]{projected,q.CandidateIds[0],visible}),Is.Null,"不能读取其他对局来源");
            Assert.That(commands,Is.Empty);renderer.Clear();
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
