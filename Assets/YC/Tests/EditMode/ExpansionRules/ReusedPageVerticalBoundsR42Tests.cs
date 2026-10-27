using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Influence;
using YC.Domain.SpecialActions;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using Object=UnityEngine.Object;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 从真实场景提取带覆盖的原HUD，关闭原场景后仅运行该UI克隆。无Bootstrap/SaveController，无正常对局。
    public sealed class ReusedPageVerticalBoundsR42Tests
    {
        const BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        static Type T(string name)=>Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        static object Read(object o,string n)=>o.GetType().GetField(n,F)?.GetValue(o)??o.GetType().GetProperty(n,F)?.GetValue(o);
        static object Call(object o,string n,params object[] args)=>o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length&&m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(x=>x)).Invoke(o,args);
        string output,sceneName;
        GameObject hud;
        Component frame,registry;
        readonly List<string> failures=new List<string>();
        int sequence;
        object sizeGroup;
        EditorWindow gameView;
        PropertyInfo selectedSize;
        int originalSizeIndex, customSizeIndex=-1;
        [UnityTest,Timeout(240000)] public IEnumerator SampleSceneActualOverrides()=>Run("SampleScene");
        [UnityTest,Timeout(240000)] public IEnumerator ThreePlayerSceneActualOverrides()=>Run("ThreePlayerScene");
        void Record(string stage,object value)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,(++sequence).ToString("D3")+"-"+stage+".json"),JsonConvert.SerializeObject(value,Formatting.Indented));
        }
        static object RectData(RectTransform r)
        {
            if(r==null)return null;
            var corners=new Vector3[4];r.GetWorldCorners(corners);
            var canvases=r.GetComponentsInParent<Canvas>(true);
            return new {name=r.name,instance=r.GetInstanceID(),parent=r.parent?.name,rect=new{r.rect.x,r.rect.y,r.rect.width,r.rect.height},anchorMin=new{r.anchorMin.x,r.anchorMin.y},anchorMax=new{r.anchorMax.x,r.anchorMax.y},position=new{x=r.anchoredPosition.x,y=r.anchoredPosition.y},size=new{x=r.sizeDelta.x,y=r.sizeDelta.y},scale=new{x=r.localScale.x,y=r.localScale.y},
                world=corners.Select(c=>new{x=c.x,y=c.y}).ToArray(),minHeight=LayoutUtility.GetMinHeight(r),preferredHeight=LayoutUtility.GetPreferredHeight(r),
                layout=r.GetComponents<MonoBehaviour>().Where(c=>c is ILayoutElement || c is ILayoutController).Select(c=>new{type=c.GetType().FullName,c.enabled,state=EditorJsonUtility.ToJson(c)}),
                canvases=canvases.Select(c=>new{c.name,c.renderMode,c.isRootCanvas,root=c.rootCanvas.name,c.scaleFactor,pixelRect=new{c.pixelRect.x,c.pixelRect.y,c.pixelRect.width,c.pixelRect.height},
                    scaler=c.GetComponent<CanvasScaler>()==null?null:new{c.GetComponent<CanvasScaler>().uiScaleMode,referenceWidth=c.GetComponent<CanvasScaler>().referenceResolution.x,referenceHeight=c.GetComponent<CanvasScaler>().referenceResolution.y,c.GetComponent<CanvasScaler>().matchWidthOrHeight}}).ToArray()};
        }
        void Geometry(string stage,Component page,RectTransform panel)
        {
            var host=(RectTransform)Read(frame,"ContentRect");var hc=new Vector3[4];host.GetWorldCorners(hc);var pc=new Vector3[4];panel.GetWorldCorners(pc);
            bool within=pc.Min(p=>p.y)>=hc.Min(p=>p.y)-.01f&&pc.Max(p=>p.y)<=hc.Max(p=>p.y)+.01f;
            Record(stage,new{sceneName,frame=Time.frameCount,Screen.width,Screen.height,host=RectData(host),root=RectData((RectTransform)page.transform),panel=RectData(panel),parent=RectData(panel.parent as RectTransform),within,
                chain=panel.GetComponentsInParent<RectTransform>(true).Select(RectData),children=panel.Cast<Transform>().OfType<RectTransform>().Select(RectData),
                topBar=RectData((RectTransform)Read(frame,"topBar")),bottomBar=RectData((RectTransform)Read(frame,"bottomBar")),
                controls=page.GetComponentsInChildren<Button>(false).Where(b=>b==Read(page,"confirm")||b==Read(page,"cancel")||b==Read(page,"back")).Select(b=>new{b.name,interactable=b.IsInteractable(),bounds=RectData((RectTransform)b.transform)})});
            if(stage.EndsWith("stable")&&!within)failures.Add(sceneName+":"+stage);
        }
        static IEnumerator Settle(){for(int i=0;i<6;i++)yield return null;}
        IEnumerator Capture(string stage)
        {
            var path=Path.Combine(output,(++sequence).ToString("D3")+"-"+stage+".png");ScreenCapture.CaptureScreenshot(path);
            var until=Time.realtimeSinceStartup+10;do{yield return null;}while(!File.Exists(path)&&Time.realtimeSinceStartup<until);
            Assert.That(File.Exists(path),Is.True,"隔离GameView截图未写出");
        }
        IEnumerator Size(Vector2Int size)
        {
            if(sizeGroup==null)
            {
                var a=typeof(EditorApplication).Assembly;var t=a.GetType("UnityEditor.GameViewSizes");
                var singleton=typeof(ScriptableSingleton<>).MakeGenericType(t).GetProperty("instance").GetValue(null);
                sizeGroup=t.GetMethod("GetGroup").Invoke(singleton,new[]{Enum.Parse(a.GetType("UnityEditor.GameViewSizeGroupType"),"Standalone")});
                gameView=EditorWindow.GetWindow(a.GetType("UnityEditor.GameView"));selectedSize=gameView.GetType().GetProperty("selectedSizeIndex",F);
                originalSizeIndex=(int)selectedSize.GetValue(gameView);
            }
            RemoveSize();var assembly=typeof(EditorApplication).Assembly;
            var value=Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"),Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType"),"FixedResolution"),size.x,size.y,"QY017 R42 隔离视口");
            sizeGroup.GetType().GetMethod("AddCustomSize").Invoke(sizeGroup,new[]{value});
            customSizeIndex=(int)sizeGroup.GetType().GetMethod("GetTotalCount").Invoke(sizeGroup,null)-1;
            selectedSize.SetValue(gameView,customSizeIndex);gameView.Repaint();
            float until=Time.realtimeSinceStartup+15;
            while((Screen.width!=size.x||Screen.height!=size.y)&&Time.realtimeSinceStartup<until)yield return null;
            Assert.That(new Vector2Int(Screen.width,Screen.height),Is.EqualTo(size));yield return Settle();
        }
        void RemoveSize()
        {
            if(sizeGroup==null||customSizeIndex<0)return;
            selectedSize.SetValue(gameView,originalSizeIndex);
            sizeGroup.GetType().GetMethod("RemoveCustomSize").Invoke(sizeGroup,new object[]{customSizeIndex});customSizeIndex=-1;
        }
        IEnumerator Run(string scene)
        {
            sceneName=scene;sequence=0;failures.Clear();
            output=Path.Combine(Environment.GetEnvironmentVariable("YC_QY017_PAGE_BOUNDS_OUTPUT")??throw new Exception("missing_output"),scene);
            var isolated=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var source=EditorSceneManager.OpenScene("Assets/Scenes/"+scene+".unity",OpenSceneMode.Additive);
            var original=source.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren(T("GameplayInteractionHudView"),true)).Single();
            var mods=PrefabUtility.GetPropertyModifications(original.gameObject)??Array.Empty<PropertyModification>();
            Record("scene-source",new{scene,source=source.path,prefab=PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(original),original=original.GetInstanceID(),
                modifications=mods.Select(m=>new{target=m.target?.name,m.propertyPath,m.value,reference=m.objectReference?.name})});
            hud=Object.Instantiate(original.gameObject);hud.name="QY017-R42-IsolatedSceneHud";hud.SetActive(false);
            SceneManager.MoveGameObjectToScene(hud,isolated);EditorSceneManager.CloseScene(source,true);
            Assert.That(hud.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null).Select(c=>c.GetType().Name),Has.None.EqualTo("MatchSaveController"));
            Assert.That(hud.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null).Select(c=>c.GetType().Name),Has.None.EqualTo("GameSessionBootstrapper"));
            yield return new EnterPlayMode();
            hud=Resources.FindObjectsOfTypeAll<GameObject>().Single(g=>g.name=="QY017-R42-IsolatedSceneHud"&&g.scene.IsValid());
            T("UiTheme").GetMethod("Initialize").Invoke(null,new object[]{AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",T("UiThemeCatalog"))});
            hud.SetActive(true);yield return Settle();
            var view=hud.GetComponent(T("GameplayInteractionHudView"));frame=(Component)Read(view,"Frame");registry=(Component)Read(view,"DialogRegistry");
            foreach(var size in new[]{new Vector2Int(1920,1080),new Vector2Int(1280,720)})
            {
                yield return Size(size);var host=(RectTransform)Read(frame,"ContentRect");
                yield return Markers(size);
                if(size.x==1920){yield return Condition(host,1);yield return Condition(host,2);yield return Players(host);yield return TotalPayment(host);}
                var page=(Component)Call(registry,"InstantiateEnterpriseSelection",host,false);var panel=(RectTransform)Read(page,"Panel");
                var p=new EnterpriseSelectionProjection{RequestId="R42-isolated-"+scene+size,ViewerPlayerId=1,Revision=1,Mode=EnterpriseSelectionMode.Company,SubmissionAvailable=true,CanCancel=true,
                    Boards=Enumerable.Range(0,2).Select(i=>new EnterpriseBoardProjection{Id="b"+i,VisualKey="enterprise-"+i,Label="隔离合作板",PlayerIds=new[]{1,2},PlayerColors=new[]{0,1},PlayerLevels=new[]{0,0}}).ToArray()};
                p.Targets=p.Boards.Select(b=>new EnterpriseSelectionTarget{Id=b.Id,BoardId=b.Id,CompanyId=b.Id,Label=b.Label,Available=true}).ToArray();
                int submits=0;Call(page,"BindSubmission",new Func<EnterpriseSelectionProjection>(()=>p),new Action<EnterpriseSelectionIntent>(_=>submits++),new Action(()=>{}));
                Call(page,"Present",p,false);Geometry(size.x+"-company-first",page,panel);yield return null;Geometry(size.x+"-company-frame1",page,panel);yield return Settle();Geometry(size.x+"-company-stable",page,panel);yield return Capture(size.x+"-company-stable");
                Call(page,"Choose","b0");((Button)Read(page,"confirm")).onClick.Invoke();((Button)Read(page,"confirm")).onClick.Invoke();Assert.That(submits,Is.EqualTo(1));
                p=new EnterpriseSelectionProjection{RequestId="R42-preview-"+scene+size,ViewerPlayerId=1,Revision=2,Mode=EnterpriseSelectionMode.Company,ReadOnly=true,CanCancel=false,MinSelections=0,MaxSelections=0,Boards=p.Boards,Targets=p.Targets};
                bool current=true;int continues=0;Call(page,"BindReadOnlyContinuation",new Func<bool>(()=>current),new Action(()=>{continues++;current=false;}),"继续","合作板预览");
                Call(page,"Present",p,false);Geometry(size.x+"-preview-first",page,panel);yield return null;Geometry(size.x+"-preview-frame1",page,panel);yield return Settle();Geometry(size.x+"-preview-stable",page,panel);yield return Capture(size.x+"-preview-stable");
                var alternate=size.x==1920?new Vector2Int(1280,720):new Vector2Int(1920,1080);
                yield return Size(alternate);Geometry(size.x+"-preview-existing-resize-stable",page,panel);yield return Capture(size.x+"-preview-existing-resize");
                yield return Size(size);Geometry(size.x+"-preview-existing-restore-stable",page,panel);
                // 当前 GameView 原生最小高度为10（a014实测1被夹到10）；不冒充旧Player的144×1截图。
                yield return Size(new Vector2Int(144,10));yield return Size(size);Geometry(size.x+"-preview-after-minimum-restore-stable",page,panel);yield return Capture(size.x+"-preview-after-minimum");
                Call(page,"OpenDetails","b0");yield return Settle();Geometry(size.x+"-details-stable",page,panel);Call(page,"ReturnFromDetails");yield return Settle();
                Call(page,"SuspendForNavigation");page.gameObject.SetActive(false);yield return null;Call(page,"ResumeFromNavigation",p);yield return Settle();Geometry(size.x+"-preview-return-stable",page,panel);
                var confirm=(Button)Read(page,"confirm");confirm.onClick.Invoke();confirm.onClick.Invoke();Assert.That(continues,Is.EqualTo(1));Assert.That(submits,Is.EqualTo(1));
                Record(size.x+"-preview-continuation",new{continues,submits,draft=((EnterpriseSelectionDraft)Read(page,"Draft")).Projection.RequestId});
                Call(page,"ClearPresentation");Object.Destroy(page.gameObject);yield return null;
                yield return Facility(host,size);
            }
            Object.Destroy(hud);yield return null;yield return new ExitPlayMode();
            foreach(var g in Resources.FindObjectsOfTypeAll<GameObject>().Where(g=>g.name=="QY017-R42-IsolatedSceneHud"&&g.scene.IsValid()).ToArray())Object.DestroyImmediate(g);
            Record("finished",new{failures,normalGame=false,saveControllerCreated=false});Assert.That(failures,Is.Empty,"相对宿主纵向越界，原阶段证据保留");
        }
        IEnumerator Facility(RectTransform host,Vector2Int size)
        {
            var fixture=new PageSequenceFixContractTests();fixture.StartMartin();
            var state=(GameState)Call(fixture,"State","entrepreneur.round.M.7",false,null);Call(fixture,"ActivateFace",state,"entrepreneur.round.M.7","M11");
            var lower=(ItemInstanceState)Call(fixture,"Card",state,"building_015");Call(fixture,"Position",state,lower,FacilityLayerQuery.ResolveCell(state,1,0).CellZoneId,"unordered",1);
            var upper=(ItemInstanceState)Call(fixture,"Card",state,"building_016");Call(fixture,"Overlay",state,upper,0);Call(fixture,"Action",state,"M11");Call(fixture,"Answer",state,NormalizedValue.CreateString("continue"));
            var request=state.EffectRuntime.InteractionRequests.Last(r=>r.Status=="open");var visible=GameStateViewProjector.ProjectForPlayer(state,1);var commands=new List<GameCommand>();
            var coordinator=Activator.CreateInstance(T("FacilityPanelSelectionUiCoordinator"),F,null,new object[]{new Func<GameStateView>(()=>visible),new Func<int>(()=>1),registry,host,new Action<GameCommand>(commands.Add),new Func<bool>(()=>true)},null);
            ((IInteractionRequestRenderer)coordinator).Render(InteractionRequestProjector.ProjectForPlayer(request,1));
            var page=(Component)Read(coordinator,"page");var panel=(RectTransform)Read(page,"verticalPagePanel");
            Geometry(size.x+"-facility-first",page,panel);yield return null;Geometry(size.x+"-facility-frame1",page,panel);yield return Settle();Geometry(size.x+"-facility-stable",page,panel);yield return Capture(size.x+"-facility-stable");
            yield return Size(size.x==1920?new Vector2Int(1280,720):new Vector2Int(1920,1080));Geometry(size.x+"-facility-existing-resize-stable",page,panel);yield return Capture(size.x+"-facility-existing-resize");
            yield return Size(size);Geometry(size.x+"-facility-existing-restore-stable",page,panel);
            yield return Size(new Vector2Int(144,10));yield return Size(size);Geometry(size.x+"-facility-after-minimum-restore-stable",page,panel);yield return Capture(size.x+"-facility-after-minimum");
            var slots=(Button[])Read(page,"citySlots");slots[0].onClick.Invoke();yield return Settle();
            page.gameObject.SetActive(false);yield return null;((IInteractionRequestRenderer)coordinator).Render(InteractionRequestProjector.ProjectForPlayer(request,1));yield return Settle();
            Geometry(size.x+"-facility-return-stable",page,panel);var confirm=(Button)Read(page,"confirm");Assert.That(confirm.IsInteractable(),Is.True);confirm.onClick.Invoke();confirm.onClick.Invoke();Assert.That(commands.Count,Is.EqualTo(1));
            Record(size.x+"-facility-original-answer",new{command=commands.Single(),requestId=request.GetStableInteractionId(),request.OwnerEffectId,normalGame=false,runId=fixture.Artifacts.RunId,fixture.Artifacts.ResultsDirectory,fixture.Artifacts.InputsDirectory});
            fixture.FinishMartin();Call(coordinator,"Clear");Object.Destroy(page.gameObject);yield return null;
        }
        IEnumerator Markers(Vector2Int size)
        {
            var modules=Read(hud.GetComponent(T("GameplayInteractionHudView")),"MainModules");
            foreach(var opponent in (GameObject[])Read(modules,"opponentData"))opponent.SetActive(true);
            var markers=((Array)Read(modules,"opponentSupplyMarkers")).Cast<Component>().Concat(new[]{(Component)Read(modules,"localSupplyMarker")}).ToArray();
            foreach(var marker in markers)
            {
                var text=(Text)Read(marker,"NumberText");var face=(Image)Read(marker,"Face");
                foreach(var fixedColor in new[]{false,true})foreach(var value in new[]{10,9,1,0,10})
                {
                    if(fixedColor)Call(marker,"RenderFixed",new Color(160f/255f,32f/255f,240f/255f,1f),value);
                    else Call(marker,"RenderPlayer",PlayerColor.Blue,value);yield return Settle();
                    var drawing=face.GetPixelAdjustedRect();if(face.preserveAspect){var aspect=face.sprite.rect.width/face.sprite.rect.height;if(drawing.width/drawing.height>aspect){var w=drawing.height*aspect;drawing.x+=(drawing.width-w)*.5f;drawing.width=w;}else{var h=drawing.width/aspect;drawing.y+=(drawing.height-h)*.5f;drawing.height=h;}}
                    // 当前 ugui Text.OnPopulateMesh 使用全部 verts；没有旧版的末尾四个占位顶点。
                    var verts=text.cachedTextGenerator.verts;var points=verts.Select(v=>face.rectTransform.InverseTransformPoint(text.rectTransform.TransformPoint(v.position/text.pixelsPerUnit))).ToArray();
                    bool inside=points.Length>0&&points.All(p=>p.x>=drawing.xMin-.1f&&p.x<=drawing.xMax+.1f&&p.y>=drawing.yMin-.1f&&p.y<=drawing.yMax+.1f);
                    Record(size.x+"-marker-"+marker.name+"-"+value,new{value,fixedColor,inside,face=RectData(face.rectTransform),number=RectData(text.rectTransform),text.fontSize,text.resizeTextForBestFit,text.resizeTextMinSize,text.resizeTextMaxSize,text.verticalOverflow,
                        actualFontSize=text.cachedTextGenerator.fontSizeUsedForBestFit,vertices=points.Select(p=>new{x=p.x,y=p.y}),drawing=new{drawing.x,drawing.y,drawing.width,drawing.height},prefab=PrefabUtility.GetCorrespondingObjectFromSource(marker)?.name});
                    if(!inside)failures.Add(marker.name+":"+value+"-数字越界");
                }
            }
            yield return Capture(size.x+"-供应数字实际控件");
        }
        IEnumerator Condition(RectTransform host,int count)
        {
            using var f=new AgnesContentTestFixture();
            var rules=new PageSequenceFixContractTests();rules.StartMartin();
            var recipe=new[]{new ItemRecipeEntry{EntryId="R42-A01",DefinitionId="entrepreneur.ability.A01",TypeId="entrepreneur.ability_card",Quantity=1,PerPlayer=true,InitialZoneRole="entrepreneur_active"}};
            var state=(GameState)Call(rules,"State","",false,recipe);var engineRegistry=(EffectRegistry)Read(rules,"Registry");
            var card=state.ItemZones.Items.Single(i=>i.DefinitionId=="entrepreneur.ability.A01"&&i.ControllerSubject.PlayerId==1);
            var snapshot=AgnesContentTests.Inventory(6,-1,0);
            var json=(string)typeof(AgnesContentTests).GetMethod("ContentJson",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{snapshot});
            snapshot.GlobalContent=AgnesContentTestFixture.Snapshot(Newtonsoft.Json.Linq.JObject.Parse(json.Replace("\"a01\"",JsonConvert.SerializeObject(card.ItemId))));
            var lua=f.Lua("{}","return A.purchaseCommand(ctx,"+count+")",snapshot:snapshot);Assert.That(lua.IsSuccess,Is.True,lua.Diagnostic);
            var pack=(ExternalContentPack)Read(rules,"Pack");
            Assert.That(new LuaEffectSpecCompiler(presentationPack:pack).TryCompile(lua,new LuaEffectCompilationContext{State=state,Registry=engineRegistry,PlayerId=1,SourceId="R42-display-isolated"},out var specs,out var fault),Is.True,fault?.Diagnostic);
            var engine=new EffectTreeExecutor(state,engineRegistry);engine.CreateRoot(specs.Single());var result=engine.RunUntilQuiescent();Assert.That(result.Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            var open=state.EffectRuntime.InteractionRequests.Last(r=>r.Status=="open");var visible=GameStateViewProjector.ProjectForPlayer(state,1);
            Call(frame,"ConfigureGameplayPresentation",pack.Presentation,new Dictionary<string,string>());
            Call(frame,"BindGameplaySnapshot",visible);
            var request=InteractionRequestProjector.ProjectForPlayer(open,1,visible,state.GameId);
            var document=(GameplayTextDocument)T("EffectDialogTextDocuments").GetMethod("ResolvePrompt",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{request,
                new Func<GameplayTextRef,GameplayTextDocument>(r=>(GameplayTextDocument)Call(frame,"ResolveGameplayText",r))});
            Assert.That(request.Presentation.Prompt.Key,Is.EqualTo("core:entrepreneur.A.command.buy"));
            Assert.That(request.Presentation.Prompt.Arguments.Single(a=>a.Name=="count").Value.IntegerValue,Is.EqualTo(count));
            Assert.That(request.Presentation.Prompt.Arguments.Single(a=>a.Name=="cost").Value.IntegerValue,Is.EqualTo(2*count));
            var dialog=Activator.CreateInstance(T("EventChoiceDialog"),F,null,new object[]{registry,new Func<RectTransform>(()=>host)},null);
            bool current=true;int answers=0;
            Call(dialog,"ShowConditionPayment",request,document,new Action<Component,GameplayTextDocument,bool>((c,d,s)=>Call(frame,"RenderGameplayText",c,d,s)),new Func<bool>(()=>current),new Action(()=>{answers++;current=false;}),new Action(()=>{answers++;current=false;}));
            yield return Settle();var window=(Component)Read(dialog,"view");var body=(Text)Read(window,"DescriptionText");var binding=(Component)Read(window,"ConditionPaymentDescriptionDocument");
            var fragments=body.GetComponentsInChildren<Text>(false).Where(t=>t!=body).ToArray();
            Record("条件式正式请求到原正文",new{count,request,document=document.ToPlainText(),hasDocument=Read(binding,"HasDocument"),activeFragments=Read(binding,"ActiveFragmentCount"),body=RectData(body.rectTransform),chain=body.GetComponentsInParent<RectTransform>(true).Select(RectData),fragments=fragments.Select(t=>new{t.text,t.fontSize,color=new{t.color.r,t.color.g,t.color.b,t.color.a},rect=RectData(t.rectTransform)}),normalGame=false,runId=f.Artifacts.RunId,f.Artifacts.ResultsDirectory,f.Artifacts.InputsDirectory});
            if(fragments.Length==0)failures.Add("条件式正文没有可见文字片段");
            yield return Capture("指令购买条件式原窗口");
            var payable=request.PromptParameters.Properties.Single(p=>p.Name=="canPay").Value.BooleanValue;
            var button=(Button)Read(window,"ConditionPaymentExecuteButton");Assert.That(button.IsInteractable(),Is.EqualTo(payable));
            button.onClick.Invoke();button.onClick.Invoke();Assert.That(answers,Is.EqualTo(payable?1:0));
            if(!payable){var decline=(Button)Read(window,"ConditionPaymentDeclineButton");decline.onClick.Invoke();decline.onClick.Invoke();Assert.That(answers,Is.EqualTo(1));}

            Record("条件式原按钮单次回调",new{answers,payable,hostAnswerSubmitted=false,scope="真实Lua条件树产生确认请求；本例不建立来源授权。验证原窗口正文、执行门槛及允许按钮单次回调，不计付款收益结清"});
            Call(dialog,"Hide");rules.FinishMartin();yield return null;
        }
        IEnumerator Players(RectTransform host)
        {
            // R18 结束证据中的真实选择对手合同，隔离重放到 UI 提交边界；不运行杰西卡后续正文。
            var request=new InteractionRequest{
                InteractionId="interaction_2d2e2f59262bdd567d439bb531dbc85457719a9276c706ece05a204c6a9f2c68",
                SourceNodeId="effect_006c3dd1fd852249ae026580022e5f22fac172228d9912c8d4d5a1d17bd848c3",
                OwnerEffectId="effect_006c3dd1fd852249ae026580022e5f22fac172228d9912c8d4d5a1d17bd848c3",
                InteractionTypeId="lua.choice",AnsweringPlayerId=2,Visibility="owner",PromptKey="core:interaction.enterprise.owner.choose",
                CandidateIds=new List<string>{"p1","p3","p4"},MinSelections=1,MaxSelections=1,AllowDecline=false,AnswerSchema="candidate_id",StateRevision=8683,
                Presentation=new GameplayInteractionPresentation{Prompt=new GameplayTextRef("core:interaction.enterprise.owner.choose")}};
            foreach(var id in request.CandidateIds)request.Presentation.Options.Add(new GameplayOptionPresentation{CandidateId=id,
                Label=new GameplayTextRef("core:interaction.player.name",new GameplayTextArgument{Name="player",Kind="player",Value=NormalizedValue.CreateStableReference("player",id)})});
            var rules=new PageSequenceFixContractTests();rules.StartMartin();var pack=(ExternalContentPack)Read(rules,"Pack");
            var map=StaticMapDefinitions.CreateFourPlayerMap();
            var seed=new GameState{GameId="R42-isolated-four-"+Guid.NewGuid().ToString("N"),MapId=map.MapId,ContentPackHash=pack.ContentHash,
                LaunchPlayerOrder=new List<int>{1,2,3,4},CurrentPlayerId=2,StartPlayerId=1,
                Players=Enumerable.Range(1,4).Select(i=>new PlayerState{PlayerId=i,Name="隔离玩家"+i,Color=(PlayerColor)(i-1)}).ToList(),
                RuleConfiguration=new GameRuleConfiguration{MapId=map.MapId,PlayerCount=4,ContentPackHash=pack.ContentHash}};
            var state=new ItemZoneStateFactory().CreateForSetup(seed,map,new[]{new ItemRecipeEntry{EntryId="markers",DefinitionId="core.player_marker",TypeId="core.player_marker",Quantity=29,PerPlayer=true,InitialZoneRole="supply"}});
            state.EffectRuntime.StateRevision=request.StateRevision;state.EffectRuntime.InteractionRequests.Add(request);
            var visible=GameStateViewProjector.ProjectForPlayer(state,2);
            Call(frame,"ConfigureGameplayPresentation",pack.Presentation,new Dictionary<string,string>());Call(frame,"BindGameplaySnapshot",visible);
            var expansion=(Component)Read(hud.GetComponent(T("GameplayInteractionHudView")),"ExpansionUi");
            Call(expansion,"Configure",new Func<GameStateView>(()=>visible),new Func<int>(()=>2),null,null,new Action<GameCommand>(_=>{}));
            var projected=InteractionRequestProjector.ProjectForPlayer(request,2,visible,state.GameId);
            var adapter=T("PlayerSelectionRequestAdapter");var staticFlags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
            object Build(InteractionRequestProjection p)=>adapter.GetMethod("Build",staticFlags).Invoke(null,new object[]{p,state,2,new[]{"p3"}});
            var config=Build(projected);Assert.That(config,Is.Not.Null,"可见正式 typed player 候选必须复用原玩家选择页");
            Assert.That(Read(config,"SelectedIds"),Is.EqualTo(new[]{3}));Assert.That(Read(config,"Min"),Is.EqualTo(1));Assert.That(Read(config,"Max"),Is.EqualTo(1));Assert.That(Read(config,"AllowCancel"),Is.False);
            var cases=new List<object>();
            void Reject(string label,Action<InteractionRequestProjection> edit)
            {var p=JsonConvert.DeserializeObject<InteractionRequestProjection>(JsonConvert.SerializeObject(projected));edit(p);Assert.That(Build(p),Is.Null,label);cases.Add(new{label,rejected=true});}
            Reject("裸p字符串",p=>p.Presentation=null);
            Reject("普通数字",p=>{p.CandidateIds=new List<string>{"1","3"};p.Presentation=null;});
            Reject("声明了非玩家",p=>p.Presentation.Options[0].Label.Arguments[0].Kind="item");
            Reject("候选与引用不一致",p=>p.Presentation.Options[0].Label.Arguments[0].Value.ReferenceId="p4");
            Reject("引用不唯一",p=>p.Presentation.Options[0].Label.Arguments.Add(p.Presentation.Options[0].Label.Arguments[0]));
            Reject("银灰资源与玩家复合候选",p=>{p.CandidateIds[0]="iron|p1";p.Presentation.Options[0].CandidateId="iron|p1";});
            Reject("诗怀雅影响力物品候选",p=>{p.CandidateIds[0]="marker.1";p.Presentation.Options[0].CandidateId="marker.1";});
            Reject("玩家不在可见状态",p=>{p.CandidateIds[0]="p9";p.Presentation.Options[0].CandidateId="p9";p.Presentation.Options[0].Label.Arguments[0].Value.ReferenceId="p9";});
            Reject("两种别名指向同玩家",p=>p.CandidateIds.Add("player:1"));
            var legacy=JsonConvert.DeserializeObject<InteractionRequestProjection>(JsonConvert.SerializeObject(projected));legacy.CandidateIds=new List<string>{"player:1","player:4"};legacy.Presentation=null;
            Assert.That(Build(legacy),Is.Not.Null);
            var legacyIds=(IReadOnlyList<string>)adapter.GetMethod("CandidateIds",staticFlags).Invoke(null,new object[]{legacy,new[]{4}});Assert.That(legacyIds,Is.EqualTo(new[]{"player:4"}));
            var ceobe=JsonConvert.DeserializeObject<InteractionRequestProjection>(JsonConvert.SerializeObject(projected));
            ceobe.CandidateIds=new List<string>{"1","3","4"};for(var i=0;i<3;i++)ceobe.Presentation.Options[i].CandidateId=ceobe.CandidateIds[i];
            Assert.That(Build(ceobe),Is.Not.Null);
            var numericIds=(IReadOnlyList<string>)adapter.GetMethod("CandidateIds",staticFlags).Invoke(null,new object[]{ceobe,new[]{4}});Assert.That(numericIds,Is.EqualTo(new[]{"4"}));
            var multiple=JsonConvert.DeserializeObject<InteractionRequestProjection>(JsonConvert.SerializeObject(projected));multiple.MinSelections=0;multiple.MaxSelections=2;multiple.AllowDecline=true;
            var multipleConfig=Build(multiple);Assert.That(Read(multipleConfig,"Min"),Is.EqualTo(0));Assert.That(Read(multipleConfig,"Max"),Is.EqualTo(2));Assert.That(Read(multipleConfig,"AllowCancel"),Is.True);
            Assert.That(adapter.GetMethod("CandidateIds",staticFlags).Invoke(null,new object[]{multiple,new[]{3,4}}),Is.EqualTo(new[]{"p3","p4"}));
            var dialog=Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"),F,null,new object[]{registry,host},null);
            var commands=new List<GameCommand>();var coordinator=Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"),F,null,new object[]{
                new Func<GameState>(()=>state),new Func<int>(()=>2),dialog,new Action<IReadOnlyList<WorkflowHighlight>>(_=>{}),new Action(()=>{}),new Action<GameCommand>(commands.Add),new Action<string>(_=>{})},null);
            coordinator.GetType().GetProperty("GetVisibleState").SetValue(coordinator,new Func<GameStateView>(()=>visible));
            coordinator.GetType().GetProperty("ExpansionUi").SetValue(coordinator,expansion);
            coordinator.GetType().GetProperty("GetVisibleRequests").SetValue(coordinator,new Func<IReadOnlyList<InteractionRequest>>(()=>new[]{request}));
            Assert.That(((IInteractionRequestRenderer)coordinator).CanRender(InteractionRequestProjector.ProjectForPlayer(request,1)),Is.False,"非回答者不能操作此页");
            ((IInteractionRequestRenderer)coordinator).Render(InteractionRequestProjector.ProjectForPlayer(request,2));yield return Settle();
            var page=(Component)Read(dialog,"players");Assert.That(page,Is.Not.Null);
            Assert.That(((Text)Read(page,"description")).text,Is.EqualTo(((GameplayTextDocument)Call(frame,"ResolveGameplayText",projected.Presentation.Prompt)).ToPlainText()));
            Assert.That(Read(page,"CancelButton") is Button cancel&&!cancel.gameObject.activeSelf,Is.True);
            var options=((IEnumerable)Read(page,"Options")).Cast<Component>().ToArray();
            var self=options.Single(o=>(int)Read(Read(o,"Option"),"Id")==2);Assert.That(((Button)Read(self,"Button")).IsInteractable(),Is.False);
            var target=options.Single(o=>(int)Read(Read(o,"Option"),"Id")==4);((Button)Read(target,"Button")).onClick.Invoke();yield return Settle();
            ((IInteractionRequestRenderer)coordinator).Render(InteractionRequestProjector.ProjectForPlayer(request,2));yield return Settle();
            Assert.That((IReadOnlyList<int>)Read(Read(page,"Draft"),"SelectedIds"),Is.EqualTo(new[]{4}),"同请求刷新保留已选玩家");
            yield return Capture("原玩家页真实候选p4");var confirm=(Button)Read(page,"ConfirmButton");Assert.That(confirm.IsInteractable(),Is.True);confirm.onClick.Invoke();confirm.onClick.Invoke();Assert.That(commands.Count,Is.EqualTo(1));
            var commandJson=JsonConvert.SerializeObject(commands.Single());Assert.That(commandJson,Does.Contain("p4"));Assert.That(commandJson,Does.Not.Contain("player:4"));
            Record("原玩家页请求与单次回答",new{request,projected,command=commands.Single(),cases,legacyIds,numericIds,selected=new[]{4},normalGame=false,hostAnswerSubmitted=false,
                scope="R18已结束请求合同到正式投影、原coordinator及原PlayerSelectionPrefab按钮；只收到回答命令，不执行策略正文"});
            ((IInteractionRequestRenderer)coordinator).Clear();Call(expansion,"Clear");Object.Destroy(page.gameObject);rules.FinishMartin();yield return null;
        }
        IEnumerator TotalPayment(RectTransform host)
        {
            var rules=new PageSequenceFixContractTests();rules.StartMartin();var state=(GameState)Call(rules,"State","",false,null);
            var pack=(ExternalContentPack)Read(rules,"Pack");var effectRegistry=(EffectRegistry)Read(rules,"Registry");
            var source=pack.GetScript("lua/city_styles/composite_power_system.lua");
            var handler=pack.ActiveDefinitions.Single(d=>d.DefinitionId=="city_style_composite_power_system").AbilityHandlers.Single(h=>h.HandlerId=="city-styles.composite");
            var snapshot=LuaFacadeSnapshot.FromInvocation("p1",0,100,3,handler.Version);snapshot.EventPayload=LuaSnapshotValue.Object(Array.Empty<LuaSnapshotEntry>());
            var lua=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("content."+handler.HandlerId,"city_styles.composite",handler.HandlerId,handler.Version,source,LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("R42-composite","AbilityInvoked","p1",state.EffectRuntime.StateRevision,handler.Version,3,0,100,snapshot));
            Assert.That(lua.IsSuccess,Is.True,lua.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler(presentationPack:pack).TryCompile(lua,new LuaEffectCompilationContext{State=state,Registry=effectRegistry,PlayerId=1,SourceId="R42-display-isolated",
                AbilityId="city_styles.composite",CompletionHandlerId=handler.CompletionHandlerId,ContentInstanceId=CityStyleLuaCatalog.ContentInstanceId},out var specs,out var fault),Is.True,fault?.Diagnostic);
            var engine=new EffectTreeExecutor(state,effectRegistry);engine.CreateRoot(specs.Single());var step=engine.RunUntilQuiescent();Assert.That(step.Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            var request=state.EffectRuntime.InteractionRequests.Last(r=>r.Status=="open");Assert.That(request.InteractionTypeId,Is.EqualTo(ResourcePaymentChoiceEffectExecutor.InteractionTypeId));
            var visible=GameStateViewProjector.ProjectForPlayer(state,1);Call(frame,"ConfigureGameplayPresentation",pack.Presentation,new Dictionary<string,string>());Call(frame,"BindGameplaySnapshot",visible);
            var expansion=(Component)Read(hud.GetComponent(T("GameplayInteractionHudView")),"ExpansionUi");
            Call(expansion,"Configure",new Func<GameStateView>(()=>visible),new Func<int>(()=>1),null,null,new Action<GameCommand>(_=>{}));
            var map=new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap());var influence=new InfluenceService(map);
            var options=new SpecialActionOptionQueryService(map,influence,new CityMovementService(map,influence,new TravelCostService(map)),new SpecialActionLifecycleService());
            var commands=new List<GameCommand>();string bottom="";
            var coordinator=Activator.CreateInstance(T("SpecialActionInteractionUiCoordinator"),F,null,new object[]{new Func<GameState>(()=>state),new Func<int>(()=>1),new Func<RectTransform>(()=>host),registry,options,
                new Action<IReadOnlyList<WorkflowHighlight>>(_=>{}),new Action(()=>{}),new Action<GameCommand>(commands.Add),new Action<string>(s=>bottom=s)},null);
            coordinator.GetType().GetProperty("ExpansionUi").SetValue(coordinator,expansion);
            coordinator.GetType().GetProperty("GetVisibleRequests").SetValue(coordinator,new Func<IReadOnlyList<InteractionRequest>>(()=>new[]{request}));
            Call(coordinator,"Synchronize");yield return Settle();
            var dialog=Read(coordinator,"dialog");var shell=Read(dialog,"shell");var page=(Component)Read(shell,"view");var binding=(Component)Read(page,"descriptionDocument");
            var body=binding.GetComponent<Text>();var fragments=body.GetComponentsInChildren<Text>(false).Where(t=>t!=body).Select(t=>t.text).ToArray();
            Assert.That(Read(binding,"HasDocument"),Is.EqualTo(true));Assert.That((int)Read(binding,"ActiveFragmentCount"),Is.GreaterThan(0));
            var expected=T("EffectDialogTextDocuments").GetMethod("ResolvePrompt",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{
                new InteractionRequestProjection{Presentation=request.Presentation},new Func<GameplayTextRef,GameplayTextDocument>(r=>(GameplayTextDocument)Call(frame,"ResolveGameplayText",r))});
            Assert.That(bottom,Does.Contain(((GameplayTextDocument)expected).ToPlainText()));
            Record("合计付款正式请求到原页正文",new{request,sourceCondition=specs.Single().Presentation,bodyText=string.Join("|",fragments),bottom,activeFragments=Read(binding,"ActiveFragmentCount"),
                images=body.GetComponentsInChildren<Image>(false).Count(),sourcePromptRetained=request.Presentation?.Prompt?.Key=="core:condition.city_style.composite_power_system",commands,normalGame=false,hostAnswerSubmitted=false});
            yield return Capture("合计付款原页正文图标");Call(coordinator,"Clear");Call(expansion,"Clear");rules.FinishMartin();yield return null;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            RemoveSize();
            if(hud!=null)Object.DestroyImmediate(hud);
            if(UnityEngine.Application.isPlaying)yield return new ExitPlayMode();
            foreach(var g in Resources.FindObjectsOfTypeAll<GameObject>().Where(g=>g.name=="QY017-R42-IsolatedSceneHud"&&g.scene.IsValid()).ToArray())Object.DestroyImmediate(g);
        }
    }
}
