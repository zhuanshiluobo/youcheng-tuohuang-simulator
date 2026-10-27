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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Presentation;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.CityStyles;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // R21有限后继：复用正式Reader、原HUD/Text/Choice组件及既有隔离Host夹具。
    // 不测容量、不改固定高度，不计正常局或费用/规则全流程验收。
    public sealed class RoomLabelAndFinalDisplayR21Tests
    {
        const string HudPath="Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        const string RowPath="Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab";
        const string MenuPath="Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab";
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        GameObject hudObject,canvasObject,cameraObject;
        RenderTexture target;
        object hud,frame,modules;
        GameStateView current;
        static string Output=>Environment.GetEnvironmentVariable("QY017_R21_OUTPUT")??throw new InvalidOperationException("缺少R21独立输出目录");
        static Type T(string name)=>Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        static object Read(object obj,string name)=>obj.GetType().GetField(name,Flags)?.GetValue(obj)??obj.GetType().GetProperty(name,Flags)?.GetValue(obj);
        static object Call(object obj,string name,params object[] args){try{return obj.GetType().GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(obj,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}}
        static string Hash(string path){using var sha=SHA256.Create();using var stream=File.OpenRead(path);return string.Concat(sha.ComputeHash(stream).Select(b=>b.ToString("x2")));}
        static void Save(string name,object value){using var stream=new FileStream(Path.Combine(Output,name+".json"),FileMode.CreateNew);using var writer=new StreamWriter(stream);writer.Write(JsonConvert.SerializeObject(value,Formatting.Indented));}
        [SetUp] public void RecordIdentity()
        {
            string[] files={MenuPath,HudPath,RowPath,"Assets/Scenes/StartScene.unity","Assets/StreamingAssets/Content/core/pack.json","Assets/StreamingAssets/Content/core/presentation/texts.zh-CN.json","Assets/StreamingAssets/Content/core/presentation/entrepreneurs/D.zh-CN.json","Assets/StreamingAssets/Content/core/lua/entrepreneurs/agnes.lua","Assets/YC/Tests/EditMode/RoomLabelAndFinalDisplayR21Tests.cs"};
            Save(TestContext.CurrentContext.Test.Name+"-实际身份",new{files=files.Select(p=>new{path=p,sha256=Hash(p)}),assemblies=new[]{GetType().Assembly,T("GameplayTextView").Assembly,typeof(GameStateView).Assembly,typeof(GameplayPresentationCatalog).Assembly}.Select(a=>new{a.Location,mvid=a.ManifestModule.ModuleVersionId,sha256=Hash(a.Location)})});
        }
        [Test] public void ActualStartSceneUsesUpdatedSourceLabelAndFormalFourPlayerReadiness()
        {
            var parse=T("Validation.HotseatUiValidationDriver").GetMethod("RequestedLocalPlayerCount",Flags);
            foreach(var sample in new[]{new{entrepreneur=true,value=(string)null,expected=2},new{entrepreneur=false,value=(string)null,expected=4},new{entrepreneur=true,value="2",expected=2},new{entrepreneur=true,value="3",expected=3},new{entrepreneur=true,value="4",expected=4},new{entrepreneur=true,value="1",expected=0},new{entrepreneur=true,value="5",expected=0},new{entrepreneur=true,value="bad",expected=0},new{entrepreneur=true,value="",expected=0}})
                Assert.That(parse.Invoke(null,new object[]{sample.entrepreneur,sample.value}),Is.EqualTo(sample.expected));
            string path="Assets/Scenes/StartScene.unity",before=Hash(path);var scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
            try{
                var menus=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren(T("StartMenuView"),true)).ToArray();Assert.That(menus,Is.Not.Empty);
                var results=new List<object>();foreach(var menu in menus){var room=Read(menu,"RoomPanel");var option=((IEnumerable)Read(room,"ruleModes")).Cast<object>().Single(o=>(string)Read(o,"Id")=="entrepreneur");var button=(Button)Read(option,"Button");var labels=button.GetComponentsInChildren<Text>(true).Select(t=>t.text).ToArray();Assert.That(labels,Does.Contain("企业家"));Assert.That(labels.Any(v=>v.Contains("未实现")),Is.False);
                    var label=button.GetComponentsInChildren<Text>(true).Single(t=>t.text=="企业家");var source=PrefabUtility.GetCorrespondingObjectFromSource(label);Assert.That(AssetDatabase.GetAssetPath(source),Is.EqualTo(MenuPath));results.Add(new{labels,source=AssetDatabase.GetAssetPath(source),implementedFlag=Read(option,"Implemented")});}
                var pack=ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core"));var config=new GameRuleConfiguration{PlayerCount=4,MapId="map-four-players",RuleModeId="entrepreneur",EnabledExpansionIds=new List<string>{"core","enterprise","infected"}};
                var catalog=ActiveContentCatalogBuilder.Build(pack,config);var registry=new EffectRegistry();catalog.Register(registry);RuleModeReadiness.ValidateContent(catalog.ContentDefinitions,catalog.SetupRecipe);
                Save("实际开始场景标签与四人内容就绪",new{results,config,installedHash=pack.ContentHash,activeHash=catalog.ContentPackHash,formalReadiness=true,scope="只读场景来源/原标签及正式四人目录就绪；不改Host拒绝逻辑，不等正常对局"});
            }finally{EditorSceneManager.CloseScene(scene,true);}Assert.That(Hash(path),Is.EqualTo(before));
        }
        void CreateHud(IGameplayTextCatalogSnapshot textCatalog,GameStateView view)
        {
            Assert.That(textCatalog.ContentHash,Is.EqualTo(view.ContentPackHash),"组件必须使用与正式显示投影同版的活动文案目录");
            current=view;T("UiTheme").GetMethod("Initialize",Flags).Invoke(null,new object[]{AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",T("UiThemeCatalog"))});
            hudObject=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HudPath));hud=hudObject.GetComponent(T("GameplayInteractionHudView"));Call(hud,"ConfigureGameplayPresentation",textCatalog,new Dictionary<string,string>());frame=Read(hud,"Frame");modules=Read(hud,"MainModules");Call(frame,"BindGameplaySnapshot",current);Call(modules,"Render",null,current,1,Read(Read(hud,"DialogRegistry"),"CardVisualCatalog"));
        }
        void ReleaseHud(){ReleaseCapture();if(hudObject!=null)UnityEngine.Object.DestroyImmediate(hudObject);hudObject=null;hud=frame=modules=null;current=null;}
        static GameStateView Bare(ExternalContentPack pack)
        {
            var view=new GameStateView{GameId="R21.icons.component-only",ContentPackHash=pack.Presentation.ContentHash,ViewerRole=GameStateViewerRole.Player,ViewerPlayerId=1,LaunchPlayerOrder=new List<int>{1,2}};
            foreach(int player in view.LaunchPlayerOrder)view.Players.Add(new PlayerStateView{PlayerId=player,Name="隔离玩家"+player,Color=(PlayerColor)(player-1),Resources=new ResourceSet()});return view;
        }
        void StartCapture(Canvas canvas,int width,int height)
        {
            cameraObject=new GameObject("R21组件原生相机",typeof(Camera));var camera=cameraObject.GetComponent<Camera>();camera.transform.position=new Vector3(0,0,-10);camera.orthographic=true;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.14f,.15f,.18f);target=new RenderTexture(width,height,24);target.Create();camera.targetTexture=target;
            foreach(var layer in canvas.transform.root.GetComponentsInChildren<Canvas>(true)){layer.renderMode=RenderMode.ScreenSpaceCamera;layer.worldCamera=camera;layer.planeDistance=1;}
        }
        void Capture(string name)
        {
            Canvas.ForceUpdateCanvases();cameraObject.GetComponent<Camera>().Render();var old=RenderTexture.active;RenderTexture.active=target;var pixels=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);try{pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();File.WriteAllBytes(Path.Combine(Output,name+".png"),pixels.EncodeToPNG());}finally{RenderTexture.active=old;UnityEngine.Object.DestroyImmediate(pixels);}
        }
        void ReleaseCapture(){if(cameraObject!=null){cameraObject.GetComponent<Camera>().targetTexture=null;UnityEngine.Object.DestroyImmediate(cameraObject);cameraObject=null;}if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);target=null;}if(canvasObject!=null){UnityEngine.Object.DestroyImmediate(canvasObject);canvasObject=null;}}
        void Caption(string value)
        {
            var go=new GameObject("组件隔离标识",typeof(RectTransform),typeof(CanvasRenderer),typeof(Text));var text=go.GetComponent<Text>();var rt=(RectTransform)go.transform;rt.SetParent(canvasObject.transform,false);rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(0,1);rt.anchoredPosition=new Vector2(8,-4);rt.sizeDelta=new Vector2(1200,50);text.font=AssetDatabase.LoadAssetAtPath<GameObject>(RowPath).GetComponentsInChildren<Text>(true).First(t=>t.name=="Row Description").font;text.fontSize=18;text.color=new Color(1,.85f,.35f);text.raycastTarget=false;text.text=value;
        }
        IEnumerator IconBinding(ExternalContentPack pack)
        {
            CreateHud(pack.Presentation,Bare(pack));((Canvas)Read(hud,"Canvas")).enabled=false;var references=new List<KeyValuePair<string,GameplayTextRef>>();string baseRoot=Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core");
            string definitionPath=Path.Combine(baseRoot,"enterprise/infected.enterprise.stonehoove_security.json");var definition=JObject.Parse(File.ReadAllText(definitionPath));foreach(var level in ((JArray)definition["data"]["levels"]).OfType<JObject>().Where(l=>new[]{1,2,3,5}.Contains((int)l["level"])))references.Add(new KeyValuePair<string,GameplayTextRef>("岩蹄等级"+level["level"],GameplayPresentationCatalog.ReadDto<GameplayTextRef>(level["description"])));
            foreach(string code in new[]{"D09"}){var json=JObject.Parse(File.ReadAllText(Path.Combine(baseRoot,"entrepreneur/entrepreneur.ability."+code+".json")));foreach(var row in ((JArray)json["data"]["playerDetails"]).OfType<JObject>())references.Add(new KeyValuePair<string,GameplayTextRef>((string)row["abilityId"],GameplayPresentationCatalog.ReadDto<GameplayTextRef>(row["effectTextRef"])));}
            var results=new List<object>();var allKeys=new HashSet<string>();
            foreach(var reference in references){var document=(GameplayTextDocument)Call(frame,"ResolveGameplayText",reference.Value);var expected=document.Runs.OfType<IconRun>().ToArray();Assert.That(expected,Is.Not.Empty);foreach(var icon in expected)allKeys.Add(icon.IconKey);
                if(reference.Key.Contains("D09")){Assert.That(document.Runs[0],Is.TypeOf<IconRun>());Assert.That(document.Runs[1],Is.TypeOf<IconRun>());Assert.That(((IconRun)document.Runs[0]).IconKey,Is.EqualTo("core:timing.remaining_game"));Assert.That(((IconRun)document.Runs[1]).IconKey,Is.EqualTo("core:rule.persistent_marker"));}
                canvasObject=new GameObject("R21原正文图标隔离宿主",typeof(RectTransform),typeof(Canvas));StartCapture(canvasObject.GetComponent<Canvas>(),1300,520);Caption("隔离组件｜实际定义typed引用与原正文组件｜"+reference.Key+"｜仅查图标，不验容量");
                var source=AssetDatabase.LoadAssetAtPath<GameObject>(RowPath).GetComponentsInChildren<Text>(true).Single(t=>t.name=="Row Description");var text=UnityEngine.Object.Instantiate(source,canvasObject.transform);var rt=text.rectTransform;rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(0,1);rt.anchoredPosition=new Vector2(24,-80);rt.sizeDelta=new Vector2(1050,source.rectTransform.rect.height);var component=text.GetComponent(T("GameplayTextView"));Call(frame,"RenderGameplayText",component,document,false);for(int i=0;i<4;i++)yield return null;
                var images=text.GetComponentsInChildren<Image>().Where(i=>i.gameObject.activeInHierarchy&&i.gameObject.name=="Gameplay Inline Icon").ToArray();Assert.That(images.Length,Is.EqualTo(expected.Length));Assert.That(images.All(i=>i.sprite!=null),Is.True);Assert.That(Read(component,"HasDocument"),Is.EqualTo(true));Capture("隔离-最终图标绑定-"+reference.Key);
                results.Add(new{reference.Key,reference=reference.Value,text=document.ToPlainText(),keys=expected.Select(i=>i.IconKey).ToArray(),source=RowPath,sprites=images.Select(i=>new{path=AssetDatabase.GetAssetPath(i.sprite),i.sprite.name}).ToArray(),scope="原正文固定高度保留；不据框外渲染宣称长文可读或容量通过"});ReleaseCapture();yield return null;
            }
            Assert.That(allKeys,Does.Contain("core:rule.stonehoove_security_place"));Assert.That(allKeys,Does.Contain("core:rule.stonehoove_security_move"));Save("最终岩蹄与D永续图标实际绑定",new{definitionPath,definitionSha=Hash(definitionPath),results});ReleaseHud();yield return null;
        }
        IEnumerator CompactActualAgnesLabel()
        {
            using var f=new AgnesContentTestFixture();f.Formal();f.State.Round=1;f.State.ActionRound=1;f.State.Phase=GamePhase.ActionRound1;f.State.CurrentPlayerId=1;f.Run(ExpansionSetupFlow.RoundPreparationSpec(1));while(f.Open.InteractionTypeId==ExpansionSetupFlow.SupplyRetirementInteraction)f.Answer(f.Open.CandidateIds.First());Assert.That(f.Open.InteractionTypeId,Is.EqualTo(EntrepreneurFlow.FaceInteraction));f.Answer("entrepreneur.candidate.A09");var request=f.Open;const string candidate="2";Assert.That(request.CandidateIds,Does.Contain(candidate));Assert.That(request.Presentation.CandidateIntegerLabel,Is.Not.Null);Assert.That(request.Presentation.Options,Is.Empty);
            CreateHud(f.Pack.Presentation,GameStateViewProjector.ProjectForPlayer(f.State,1));var registry=Read(hud,"DialogRegistry");var canvas=(Canvas)Read(hud,"Canvas");var dialog=Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"),Flags,null,new object[]{registry,(RectTransform)canvas.transform},null);
            Call(dialog,"BindGameplayText",new Func<GameplayTextRef,GameplayTextDocument>(r=>(GameplayTextDocument)Call(frame,"ResolveGameplayText",r)),new Action<Component,GameplayTextDocument,bool>((c,d,s)=>Call(frame,"RenderGameplayText",c,d,s)));
            var commands=new List<GameCommand>();var renderer=(IInteractionRequestRenderer)Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"),new object[]{new Func<GameState>(()=>f.State),new Func<int>(()=>1),dialog,new Action<IReadOnlyList<WorkflowHighlight>>(_=>{}),new Action(()=>{}),new Action<GameCommand>(c=>commands.Add(c)),new Action<string>(_=>{})});
            string before=JsonConvert.SerializeObject(f.State);var projection=InteractionRequestProjector.ProjectForPlayer(request,1);renderer.Render(projection);for(int i=0;i<5;i++)yield return null;
            var overlay=(GameObject)typeof(CharacterCardEffectInteractionUiCoordinatorTests).GetMethod("GetOverlay",Flags).Invoke(null,new[]{dialog});var row=overlay.GetComponentsInChildren(T("UiEffectRowView"),true).Cast<Component>().Single(r=>(string)Read(r,"ItemId")==candidate);var text=(Text)Read(row,"TitleText");var doc=(GameplayTextDocument)Read(text.GetComponent(T("GameplayTextView")),"document");var expected=GameplayCandidateLabelResolver.Resolve(projection,candidate);var expectedDoc=(GameplayTextDocument)Call(frame,"ResolveGameplayText",expected);Assert.That(doc.ToPlainText(),Is.EqualTo(expectedDoc.ToPlainText()));Assert.That(doc.ToPlainText(),Does.Contain(candidate));Assert.That(commands,Is.Empty);Assert.That(JsonConvert.SerializeObject(f.State),Is.EqualTo(before));
            var scroll=overlay.GetComponentInChildren<ScrollRect>(true);if(scroll!=null)scroll.verticalNormalizedPosition=1;StartCapture(canvas,Screen.width,Screen.height);for(int i=0;i<3;i++)yield return null;Capture("隔离-实际Agnes紧凑数字候选");
            Save("实际Agnes紧凑整数候选UI读取",new{request.CandidateIds,request.Presentation,label=expected,plain=doc.ToPlainText(),moduleSha=Hash(Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core/lua/entrepreneurs/agnes.lua")),scope="既有完整隔离Host实际选A09→真实gainCommand→生产请求/公开投影→通用协调器→原Choice文字；实际1/2候选，未把99规则用例冒为99 UI验收，不计正常局"});renderer.Clear();ReleaseHud();yield return null;
        }
        void ConfigureDetails(IReadOnlyList<YC.Domain.Cards.PlayerDetailDisplayDefinition> definitions)
        {
            var expansion=Read(hud,"ExpansionUi");Call(expansion,"Configure",new Func<GameStateView>(()=>current),new Func<int>(()=>1),null,new Dictionary<string,YC.Domain.Cards.CharacterModeContract>(),new Action<GameCommand>(_=>Assert.Fail("只读详情不应提交")));expansion.GetType().GetProperty("DetailDefinitions").SetValue(expansion,definitions);Call(expansion,"Refresh");
        }
        IEnumerable<PlayerDetailEntry> Entries()=>((IEnumerable<PlayerDetailEntry>)Read(Read(hud,"ExpansionUi"),"DetailEntries"));
        void Publish(GameState state){current=GameStateViewProjector.ProjectForPlayer(state,1);Call(frame,"BindGameplaySnapshot",current);Call(modules,"Render",null,current,1,Read(Read(hud,"DialogRegistry"),"CardVisualCatalog"));Call(Read(hud,"ExpansionUi"),"Refresh");}
        void ExpandLocal()=>typeof(EntrepreneurDetailStabilityR18Tests).GetMethod("PointerClick",Flags).Invoke(null,new[]{Read(modules,"localDetailToggle")});
        object DetailEvidence(PlayerDetailEntry entry)
        {
            var rows=(IDictionary)Read(Read(hud,"ExpansionUi"),"rows");var row=(Component)rows[entry.EntryId];var text=(Text)Read(row,"DescriptionText");var document=text.GetComponent(T("GameplayTextView"));Assert.That(Read(document,"HasDocument"),Is.EqualTo(true));return new{entry,current.PlayerRules,current.PlayerEffects,plain=((GameplayTextDocument)Read(document,"document")).ToPlainText(),source=RowPath};
        }
        IEnumerator RuleAndGrantDetails()
        {
            using(var f=new AgnesContentTestFixture()){
                f.Formal();CreateHud(f.Pack.Presentation,GameStateViewProjector.ProjectForPlayer(f.State,1));ConfigureDetails(PlayerDetailDisplayCatalog.Read(f.Pack.ActiveDefinitions,f.Pack.Presentation));Assert.That(Entries().Any(e=>e.AbilityId=="entrepreneur.A01.cover"&&e.DetailPlayerId==1),Is.False);f.Choose(1,"A09");Publish(f.State);ExpandLocal();for(int i=0;i<5;i++)yield return null;var entry=Entries().Single(e=>e.AbilityId=="entrepreneur.A01.cover"&&e.DetailPlayerId==1);Save("008-owner规则实际选面至原详情读取",DetailEvidence(entry));ReleaseHud();yield return null;
            }
            using(var f=new PaulContentTestFixture()){
                f.Choose(1,"P01");CreateHud(f.Catalog.TextCatalog,GameStateViewProjector.ProjectForPlayer(f.State,1));ConfigureDetails(f.Catalog.PlayerDetailDefinitions);ExpandLocal();for(int i=0;i<5;i++)yield return null;var entry=Entries().Single(e=>e.AbilityId=="entrepreneur.P09.departure"&&e.DetailPlayerId==1);Save("008-grant实际来源至原详情读取",DetailEvidence(entry));
                var copy=GameStateCloneService.DeepClone(f.State);var ability=copy.ItemZones.Items.Single(i=>i.ItemId==f.AbilityItem);ItemTransferEffectExecutor.Value(ability.LocalState,"grantingSourceRefs").Items.Clear();Publish(copy);yield return null;Assert.That(Entries().Any(e=>e.AbilityId=="entrepreneur.P09.departure"&&e.DetailPlayerId==1),Is.False);Save("008-grant撤回后详情消失",new{expectedAbsent=true,note="仅隔离副本移除grant事实；原Host不改，正式投影与通用详情Reader拒绝旧共享段"});ReleaseHud();yield return null;
            }
        }
        [UnityTest] public IEnumerator FinalIconsCompactLabelAndActualRuleGrantUseOriginalComponents()
        {
            yield return new EnterPlayMode();var pack=ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core"));yield return IconBinding(pack);yield return CompactActualAgnesLabel();yield return RuleAndGrantDetails();
        }
        [Test] public void RecordUnchangedCityStyleFixtureValidationReason()
        {
            var pack=ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core"));var type=typeof(CityStyleMiscR7Tests).GetNestedType("Fixture",Flags);var evidence=new List<object>();
            foreach(bool settlement in new[]{false,true}){
                var fixture=Activator.CreateInstance(type,Flags,null,new object[]{pack},null);string style=settlement?"infected.city_style.district_management":"city_style_source_stone_industrial_hub";
                var slots=(List<int>)Call(fixture,"Layout",style,1,settlement);var state=(GameState)Read(fixture,"State");var validation=new DeclareCityStyleService().Validate(state,1,style,slots);
                evidence.Add(new{settlement,style,slots,validation.IsValid,validation.Reason,state.Phase,state.CurrentPlayerId,remaining=state.FindPlayer(1).RemainingMainActionsThisTurn,note="只读检查原R7初始布局Validate；不重跑盖放/绑定，不冒首次完整状态重放或城市样式业务通过"});
            }
            Save("城市样式原夹具宣告失败原因定位",evidence);
        }
        [UnityTearDown] public IEnumerator Finish(){ReleaseHud();if(UnityEngine.Application.isPlaying)yield return new ExitPlayMode();}
    }
}
