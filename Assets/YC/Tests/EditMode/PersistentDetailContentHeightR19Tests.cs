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
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Presentation;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    // R20替代R19增高预期。只在隔离实例测量，不修改字体/布局资产，不计正常对局。
    public sealed class FixedDetailCapacityR20Tests
    {
        private const string HudPath="Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string RowPath="Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab";
        private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private GameObject hudObject,scratch,cameraObject,captionRoot;
        private RenderTexture target;
        private object hud,frame,modules;
        private GameplayPresentationCatalog catalog;
        private bool current=true;
        private int readCount,readDepth;
        private float actualRowWidth,actualCanvasScale;
        private static string Output=>Environment.GetEnvironmentVariable("QY017_R20_OUTPUT")??throw new InvalidOperationException("缺少R20隔离输出目录");
        private static Type Production(string name)=>Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        private static object Read(object value,string name)=>value.GetType().GetField(name,Flags)?.GetValue(value)??value.GetType().GetProperty(name,Flags)?.GetValue(value);
        private static object Call(object value,string name,params object[] args)
        {
            try{return value.GetType().GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
            catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}
        }
        private static string Hash(string path){using var sha=SHA256.Create();using var stream=File.OpenRead(path);return string.Concat(sha.ComputeHash(stream).Select(b=>b.ToString("x2")));}
        private static void Save(string name,object value){using var stream=new FileStream(Path.Combine(Output,name+".json"),FileMode.CreateNew);using var writer=new StreamWriter(stream);writer.Write(JsonConvert.SerializeObject(value,Formatting.Indented));}
        private static object Size(Vector2 value)=>new{x=value.x,y=value.y};
        private sealed class Lease:IDisposable{private Action close;public Lease(Action value){close=value;}public void Dispose(){var c=close;close=null;c?.Invoke();}}
        [SetUp] public void Inputs()
        {
            string[] paths={RowPath,HudPath,"Assets/YC/Presentation/Effects/UiEffectRowView.cs","Assets/YC/Presentation/Text/GameplayTextView.cs","Assets/YC/Tests/EditMode/PersistentDetailContentHeightR19Tests.cs","Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset","Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset","Assets/YC/Application/Presentation/GameplayTextResolver.cs","Assets/YC/Presentation/Text/GameplayTextLayout.cs"};
            Save(TestContext.CurrentContext.Test.Name+"-执行身份",new{files=paths.Select(p=>new{path=p,sha256=Hash(p)}),assemblies=new[]{GetType().Assembly,Production("GameplayTextView").Assembly,typeof(GameStateView).Assembly,typeof(GameplayPresentationCatalog).Assembly}.Select(a=>new{a.Location,mvid=a.ManifestModule.ModuleVersionId,sha256=Hash(a.Location)})});
        }
        [Test] public void ActualScenesUseFixedSourceAndNoHeightOverride()
        {
            var evidence=new List<object>();
            Assert.That(Type.GetType("YC.Presentation.UiPersistentDetailLayoutElement, Assembly-CSharp"),Is.Null);
            foreach(string path in new[]{"Assets/Scenes/SampleScene.unity","Assets/Scenes/ThreePlayerScene.unity"})
            {
                string before=Hash(path);var scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
                try{
                    var views=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren(Production("GameplayInteractionHudView"),true)).ToArray();Assert.That(views,Is.Not.Empty);
                    foreach(var view in views){var source=(Component)Read(Read(view,"DialogRegistry"),"EffectRowPrefab");Assert.That(AssetDatabase.GetAssetPath(source),Is.EqualTo(RowPath));Assert.That(source.GetType().GetField("persistentContentHeight",Flags),Is.Null);
                        evidence.Add(new{scene=path,source=AssetDatabase.GetAssetPath(source),sourceSha256=Hash(RowPath),layouts=source.GetComponentsInChildren<LayoutElement>(true).Select(l=>new{path=l.name,type=l.GetType().FullName,l.minHeight,l.preferredHeight,l.flexibleHeight,l.layoutPriority})});}
                }finally{EditorSceneManager.CloseScene(scene,true);}Assert.That(Hash(path),Is.EqualTo(before));
            }
            Save("实际资产来源与固定布局配置",evidence);
        }
        private void CreateHud()
        {
            catalog=(GameplayPresentationCatalog)typeof(EntrepreneurDetailStabilityR18Tests).GetMethod("PresentationOnly",Flags).Invoke(null,null);
            Production("UiTheme").GetMethod("Initialize",Flags).Invoke(null,new object[]{AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset",Production("UiThemeCatalog"))});
            hudObject=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(HudPath));hud=hudObject.GetComponent(Production("GameplayInteractionHudView"));Call(hud,"ConfigureGameplayPresentation",catalog,new Dictionary<string,string>());
            frame=Read(hud,"Frame");modules=Read(hud,"MainModules");var view=new GameStateView{GameId="R20.component-measurement",ContentPackHash=catalog.ContentHash,ViewerRole=GameStateViewerRole.Player,ViewerPlayerId=1,LaunchPlayerOrder=new List<int>{1,2,3}};
            foreach(int player in view.LaunchPlayerOrder)view.Players.Add(new PlayerStateView{PlayerId=player,Name="隔离玩家"+player,Color=(PlayerColor)(player-1),Resources=new ResourceSet()});Call(frame,"BindGameplaySnapshot",view);Call(modules,"Render",null,view,1,Read(Read(hud,"DialogRegistry"),"CardVisualCatalog"));
        }
        private sealed class Sample{public string Code,Ability,Title,Path,Sha256;public GameplayTextRef Reference;public GameplayTextDocument Document;}
        private Sample[] Samples()
        {
            var samples=new List<Sample>();
            foreach(string code in new[]{"A01","D09"}){
                string path=Path.Combine(UnityEngine.Application.streamingAssetsPath,"Content/core/entrepreneur/entrepreneur.ability."+code+".json");string hash=Hash(path);var json=JObject.Parse(File.ReadAllText(path));
                foreach(var detail in ((JArray)json["data"]["playerDetails"]).OfType<JObject>()){
                    var reference=GameplayPresentationCatalog.ReadDto<GameplayTextRef>(detail["effectTextRef"]);Assert.That(reference,Is.Not.Null);
                    samples.Add(new Sample{Code=code,Ability=(string)detail["abilityId"],Title=(string)detail["displayName"],Path=path,Sha256=hash,Reference=reference,Document=(GameplayTextDocument)Call(frame,"ResolveGameplayText",reference)});
                }Assert.That(Hash(path),Is.EqualTo(hash));
            }
            Save(TestContext.CurrentContext.Test.Name+"-最终实际A与D段落",samples.Select(s=>new{s.Code,s.Ability,s.Title,s.Path,s.Sha256,s.Reference,text=s.Document.ToPlainText(),runs=s.Document.Runs.Select(r=>new{kind=r.GetType().Name,text=r.ToPlainText(),r.Style})}));return samples.ToArray();
        }
        private Component AddRow(RectTransform parent,string id,string title,GameplayTextDocument doc,bool automatic)
        {
            var row=(Component)Call(Read(hud,"DialogRegistry"),"InstantiateEffectRow",parent);row.gameObject.SetActive(true);
            row.GetType().GetProperty("BeginInputRead").SetValue(row,new Func<IDisposable>(()=>{readCount++;readDepth++;return new Lease(()=>readDepth--);}));
            typeof(EntrepreneurDetailStabilityR18Tests).GetMethod("BindPersistentRow",Flags).Invoke(null,new object[]{row,id,title,new Func<string,int,bool>((_,__)=>{Assert.That(readDepth,Is.GreaterThan(0));return current;})});
            Call(frame,"RenderGameplayText",Document(row),doc,false);if(!automatic)((Behaviour)row).enabled=false;return row;
        }
        private static Text Body(Component row)=>(Text)Read(row,"DescriptionText");
        private static Component Document(Component row)=>Body(row).GetComponent(Production("GameplayTextView"));
        private void BindDocument(Component row,GameplayTextDocument doc)=>Call(frame,"RenderGameplayText",Document(row),doc,false);
        private static object Config(Text text)=>new{text.font.name,fontPath=AssetDatabase.GetAssetPath(text.font),fontSize=text.fontSize,text.fontStyle,text.lineSpacing,bestFit=text.resizeTextForBestFit,minSize=text.resizeTextMinSize,maxSize=text.resizeTextMaxSize,text.horizontalOverflow,text.verticalOverflow,text.supportRichText,rect=Size(text.rectTransform.rect.size),pixelsPerUnit=text.pixelsPerUnit,canvasScale=text.canvas==null?0:text.canvas.scaleFactor};
        private static object Measure(Component row)=>new{row=Size(((RectTransform)row.transform).rect.size),title=Config((Text)Read(row,"TitleText")),body=Config(Body(row)),hasDocument=Read(Document(row),"HasDocument"),effectiveSize=Read(Document(row),"EffectiveFontSize"),fits=Call(Document(row),"FitsAtFontSize",Body(row).fontSize),naturalHeight=Read(Document(row),"preferredHeight"),fragments=Read(Document(row),"ActiveFragmentCount"),layouts=Read(Document(row),"LayoutCount"),renders=Read(Document(row),"RenderCount")};
        private RectTransform Host(Canvas canvas,float width,float x=32,float y=70)
        {
            var go=new GameObject("隔离固定源条目宿主",typeof(RectTransform),typeof(VerticalLayoutGroup));var rt=(RectTransform)go.transform;rt.SetParent(canvas.transform,false);rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(0,1);rt.anchoredPosition=new Vector2(x,-y);rt.sizeDelta=new Vector2(width,160);
            var group=go.GetComponent<VerticalLayoutGroup>();group.childControlHeight=group.childControlWidth=true;group.childForceExpandHeight=false;group.childForceExpandWidth=true;return rt;
        }
        private static void Settle(RectTransform parent){Canvas.ForceUpdateCanvases();LayoutRebuilder.ForceRebuildLayoutImmediate(parent);Canvas.ForceUpdateCanvases();}
        private void NewScratch(int width=1600,int height=450,float scale=1)
        {
            scratch=new GameObject("R20固定容量隔离量尺",typeof(RectTransform),typeof(Canvas));StartCapture(scratch.GetComponent<Canvas>(),width,height);scratch.GetComponent<Canvas>().scaleFactor=scale;
        }
        private void StartCapture(Canvas canvas,int width,int height)
        {
            cameraObject=new GameObject("R20原生渲染相机",typeof(Camera));var camera=cameraObject.GetComponent<Camera>();camera.transform.position=new Vector3(0,0,-10);camera.orthographic=true;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.14f,.15f,.18f);
            target=new RenderTexture(width,height,24);target.Create();camera.targetTexture=target;foreach(var layer in canvas.transform.root.GetComponentsInChildren<Canvas>(true)){layer.renderMode=RenderMode.ScreenSpaceCamera;layer.worldCamera=camera;layer.planeDistance=1;}
            captionRoot=new GameObject("R20隔离标识层",typeof(RectTransform),typeof(Canvas));var layerCanvas=captionRoot.GetComponent<Canvas>();layerCanvas.renderMode=RenderMode.ScreenSpaceCamera;layerCanvas.worldCamera=camera;layerCanvas.planeDistance=1;layerCanvas.sortingOrder=32760;
        }
        private Text Caption(string text,float x,float y,float width=1500)
        {
            var node=new GameObject("隔离说明",typeof(RectTransform),typeof(CanvasRenderer),typeof(Text));var t=node.GetComponent<Text>();var rt=(RectTransform)node.transform;rt.SetParent(captionRoot.transform,false);rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(0,1);rt.anchoredPosition=new Vector2(x,-y);rt.sizeDelta=new Vector2(width,50);
            t.font=AssetDatabase.LoadAssetAtPath<GameObject>(RowPath).GetComponentsInChildren<Text>(true).First(v=>v.name=="Row Description").font;t.fontSize=16;t.color=new Color(1,.85f,.35f);t.text=text;t.raycastTarget=false;return t;
        }
        private void Capture(string name)
        {
            Canvas.ForceUpdateCanvases();cameraObject.GetComponent<Camera>().Render();var old=RenderTexture.active;RenderTexture.active=target;var pixels=new Texture2D(target.width,target.height,TextureFormat.RGBA32,false);
            try{pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();File.WriteAllBytes(Path.Combine(Output,name+".png"),pixels.EncodeToPNG());}finally{RenderTexture.active=old;UnityEngine.Object.DestroyImmediate(pixels);}
        }
        private void ReleaseCapture()
        {
            if(captionRoot!=null){UnityEngine.Object.DestroyImmediate(captionRoot);captionRoot=null;}if(cameraObject!=null){cameraObject.GetComponent<Camera>().targetTexture=null;UnityEngine.Object.DestroyImmediate(cameraObject);cameraObject=null;}if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);target=null;}
        }
        private void ReleaseScratch(){ReleaseCapture();if(scratch!=null){UnityEngine.Object.DestroyImmediate(scratch);scratch=null;}}
        private static GameplayTextDocument Probe(string group,int count,IconRun icon)
        {
            if(group=="连续中文")return new GameplayTextDocument(new[]{new TextRun(new string('测',count))});
            if(group=="显式换行")return new GameplayTextDocument(new[]{new TextRun(count==0?"":string.Join("\n",Enumerable.Repeat("测",count)))});
            var runs=new List<GameplayTextRun>();for(int i=0;i<count;i++){
                if(group=="混合文本"){runs.Add(new TextRun("测"));runs.Add(icon);runs.Add(new NumberRun(2));runs.Add(new TextRun("Ab，"));}else runs.Add(icon);
            }return new GameplayTextDocument(runs);
        }
        private bool Fits(Component row,GameplayTextDocument doc,int size){BindDocument(row,doc);return (bool)Call(Document(row),"FitsAtFontSize",size);}
        private sealed class Boundary{public float RowWidth,BodyWidth,BodyHeight,CanvasScale;public string Group;public int Size,LastComplete,FirstOverflow;public int LastUtf16,FirstUtf16;public object LastMeasurement,FirstMeasurement;}
        private Boundary FindBoundary(Component row,string group,int size,IconRun icon)
        {
            int low=0,high=1;while(high<=65536&&Fits(row,Probe(group,high,icon),size)){low=high;high*=2;}Assert.That(high,Is.LessThanOrEqualTo(65536),"未触达边界不能冒报上限");
            while(high-low>1){int mid=low+(high-low)/2;if(Fits(row,Probe(group,mid,icon),size))low=mid;else high=mid;}
            var last=Probe(group,low,icon);var first=Probe(group,high,icon);Assert.That(Fits(row,last,size),Is.True);Assert.That(Fits(row,first,size),Is.False);
            Body(row).fontSize=size;BindDocument(row,last);var lastMeasure=Measure(row);BindDocument(row,first);var firstMeasure=Measure(row);
            return new Boundary{RowWidth=((RectTransform)row.transform).rect.width,BodyWidth=Body(row).rectTransform.rect.width,BodyHeight=Body(row).rectTransform.rect.height,CanvasScale=Body(row).canvas.scaleFactor,Group=group,Size=size,LastComplete=low,FirstOverflow=high,LastUtf16=last.ToPlainText().Length,FirstUtf16=first.ToPlainText().Length,LastMeasurement=lastMeasure,FirstMeasurement=firstMeasure};
        }
        private static string ScaleName(float scale)=>scale.ToString("0.####",System.Globalization.CultureInfo.InvariantCulture);
        private static void BodyBox(Component row)
        {
            var body=Body(row).rectTransform;float w=body.rect.width,h=body.rect.height;
            for(int i=0;i<4;i++){var go=new GameObject("隔离正文固定框边界",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(LayoutElement));var rt=(RectTransform)go.transform;rt.SetParent(body,false);rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(0,1);rt.anchoredPosition=new Vector2(i==1?w-1:0,i==2?1-h:0);rt.sizeDelta=new Vector2(i%2==0?w:1,i%2==0?1:h);go.GetComponent<Image>().color=new Color(1,.3f,.3f,.8f);go.GetComponent<Image>().raycastTarget=false;go.GetComponent<LayoutElement>().ignoreLayout=true;}
        }
        private IEnumerator CaptureBoundaryPair(Boundary b,IconRun icon)
        {
            NewScratch(1300,400,b.CanvasScale);var canvas=scratch.GetComponent<Canvas>();float right=650;
            Caption("隔离组件｜固定容量｜"+b.Group+"｜字号 "+b.Size+"｜缩放 "+ScaleName(b.CanvasScale)+"｜正文 "+b.BodyWidth.ToString("F2")+" × "+b.BodyHeight.ToString("F2"),8,4);
            Caption("最后完整："+b.LastComplete+" 单元",32,42,600);Caption("首次溢出："+b.FirstOverflow+" 单元",right,42,600);
            var leftHost=Host(canvas,b.RowWidth,32/b.CanvasScale,100/b.CanvasScale);var rightHost=Host(canvas,b.RowWidth,right/b.CanvasScale,100/b.CanvasScale);
            var left=AddRow(leftHost,"probe.left","固定源条目",Probe(b.Group,b.LastComplete,icon),false);var r=AddRow(rightHost,"probe.right","固定源条目",Probe(b.Group,b.FirstOverflow,icon),false);Body(left).fontSize=Body(r).fontSize=b.Size;
            Settle(leftHost);Settle(rightHost);BodyBox(left);BodyBox(r);for(int i=0;i<3;i++)yield return null;
            Caption("固定框外文字仍属溢出；字号1仅为技术量尺，不计可读通过。",8,290);
            Capture("隔离-固定容量-"+b.Group+"-字号"+b.Size+"-缩放"+ScaleName(b.CanvasScale));ReleaseScratch();yield return null;
        }
        private IEnumerator CaptureActualParagraph(Sample sample,float scale)
        {
            NewScratch(1300,520,scale);var canvas=scratch.GetComponent<Canvas>();Caption("隔离组件｜最终实际段落｜"+sample.Ability+"｜缩放 "+ScaleName(scale),8,4);Caption("原正常正文字号15（红框为固定正文区域）",32,42,600);Caption("原条目共同拟合（完整文档保留）",650,42,600);
            var lh=Host(canvas,actualRowWidth,32/scale,100/scale);var rh=Host(canvas,actualRowWidth,650/scale,100/scale);var left=AddRow(lh,"actual.normal",sample.Title,sample.Document,false);var right=AddRow(rh,"actual.fit",sample.Title,sample.Document,true);Body(left).fontSize=15;Settle(lh);Settle(rh);for(int i=0;i<4;i++)yield return null;BodyBox(left);BodyBox(right);
            Capture("隔离-最终实际段落-"+sample.Ability+"-缩放"+ScaleName(scale));Save("实际段落图像测量-"+sample.Ability+"-缩放"+ScaleName(scale),new{normal=Measure(left),originalFit=Measure(right),fonts=Body(left).GetComponentsInChildren<Text>().Where(t=>t!=Body(left)&&t.gameObject.activeInHierarchy).Select(t=>t.font.name).Distinct()});ReleaseScratch();yield return null;
        }
        private sealed class ProbeCatalog:IGameplayTextCatalog
        {
            public string Text;public string ContentHash=>"R20.isolated-length-probe";public string Locale=>"zh-CN";
            public bool TryGetTemplate(string key,out GameplayTextTemplateDefinition definition){definition=new GameplayTextTemplateDefinition{Key=key,Template=Text};return true;}
            public bool TryGetIcon(string key,out GameplayIconDefinition definition){definition=null;return false;}
        }
        [UnityTest] public IEnumerator MeasureFixedCapacityAndFinalParagraphs()
        {
            yield return new EnterPlayMode();CreateHud();var samples=Samples();var d=samples.Single(s=>s.Code=="D09");var realHost=(RectTransform)Call(modules,"GetOpponentDetailHost",2);var real=AddRow(realHost,d.Ability,d.Title,d.Document,true);Call(modules,"RefreshDetailAvailability");Click(((Button[])Read(modules,"opponentDetailToggles"))[0]);for(int i=0;i<10;i++)yield return null;
            actualRowWidth=((RectTransform)real.transform).rect.width;actualCanvasScale=((Canvas)Read(hud,"Canvas")).scaleFactor;Save("实际HUD固定条目尺寸与拟合后配置",Measure(real));var asset=AssetDatabase.LoadAssetAtPath<GameObject>(RowPath);Save("源资产原字号与换行拟合配置",asset.GetComponentsInChildren<Text>(true).Where(t=>t.name=="Row Title"||t.name=="Row Description").Select(t=>new{t.name,config=Config(t)}));((Canvas)Read(hud,"Canvas")).enabled=false;
            var icon=d.Document.Runs.OfType<IconRun>().First(v=>!v.NumberValue.HasValue);var boundaries=new List<Boundary>();var actual=new List<object>();
            foreach(float scale in new[]{actualCanvasScale,1f}.Distinct())foreach(float width in new[]{actualRowWidth,260f,360f,900f}.Distinct()){
                NewScratch(scale:scale);var host=Host(scratch.GetComponent<Canvas>(),width);var row=AddRow(host,"capacity.probe","固定源条目",new GameplayTextDocument(new[]{new TextRun("测")}),false);Settle(host);yield return null;
                float fixedRow=((RectTransform)row.transform).rect.height,fixedBody=Body(row).rectTransform.rect.height;
                foreach(string group in new[]{"连续中文","显式换行","图标1.4","混合文本"})foreach(int size in Enumerable.Range(1,15).Reverse())boundaries.Add(FindBoundary(row,group,size,icon));
                foreach(var sample in samples){BindDocument(row,sample.Document);Body(row).fontSize=15;var normal=Measure(row);var fitsBySize=Enumerable.Range(1,15).Select(size=>new{size,fits=Call(Document(row),"FitsAtFontSize",size)}).ToArray();Call(row,"FitText");Canvas.ForceUpdateCanvases();actual.Add(new{width,scale,sample.Code,sample.Ability,sample.Reference,plainLength=sample.Document.ToPlainText().Length,normal,originalAutomatic=Measure(row),fitsBySize});}
                Assert.That(((RectTransform)row.transform).rect.height,Is.EqualTo(fixedRow).Within(.01f));Assert.That(Body(row).rectTransform.rect.height,Is.EqualTo(fixedBody).Within(.01f));ReleaseScratch();yield return null;
            }
            var maximum=boundaries.GroupBy(b=>new{b.RowWidth,b.CanvasScale,b.Group}).Select(g=>new{g.Key,bestAllowed1To15=g.OrderByDescending(b=>b.LastComplete).ThenByDescending(b=>b.Size).First(),bestConfiguredMinimum10To15=g.Where(b=>b.Size>=10).OrderByDescending(b=>b.LastComplete).ThenByDescending(b=>b.Size).First()}).ToArray();
            Save("逐组固定容量边界",new{iconKey=icon.IconKey,unitDefinition="连续中文以测的字数计；显式换行以每行一个测的行数计；图标以实际无叠数图标枚数计；混合以测+图标+2+Ab，重复单元计，另列UTF16长度。",boundaries});Save("原拟合范围逐号技术容量",maximum);Save("最终A与D段落在各固定宽度的结果",actual);
            foreach(var b in boundaries.Where(v=>Mathf.Abs(v.RowWidth-actualRowWidth)<.02f&&(v.Size==15||v.Size==10||v.Size==1||maximum.Any(m=>m.bestAllowed1To15==v))))yield return CaptureBoundaryPair(b,icon);
            foreach(var sample in samples)foreach(float scale in new[]{actualCanvasScale,1f}.Distinct())yield return CaptureActualParagraph(sample,scale);
            var probe=new ProbeCatalog();var translated=new List<object>();foreach(int count in new[]{4096,16384,65536}){probe.Text=new string('测',count);var resolver=new GameplayTextResolver(probe);var doc=resolver.Resolve(new GameplayTextRef("probe"),new GameplayTextContext());Assert.That(doc.ToPlainText(),Is.EqualTo(probe.Text));translated.Add(new{count,plainLength=doc.ToPlainText().Length,runs=doc.Runs.Count,complete=true});}Save("转译长度探针",translated);
            NewScratch();var renderHost=Host(scratch.GetComponent<Canvas>(),actualRowWidth);var renderRow=AddRow(renderHost,"render.length.probe","隔离长文探针",Probe("连续中文",4096,icon),false);Body(renderRow).fontSize=15;Settle(renderHost);for(int i=0;i<4;i++)yield return null;
            var fragments=Body(renderRow).GetComponentsInChildren<Text>().Where(t=>t!=Body(renderRow)&&t.gameObject.activeInHierarchy).ToArray();string rendered=string.Concat(fragments.Select(t=>t.text));Assert.That(rendered,Is.EqualTo(new string('测',4096)));
            Save("实际绘制片段长度探针",new{input=4096,fragmentCount=fragments.Length,fragmentCharacters=rendered.Length,maxFragmentCharacters=fragments.Max(t=>t.text.Length),hasDocument=Read(Document(renderRow),"HasDocument"),fits=Call(Document(renderRow),"FitsAtFontSize",15),scope="完整片段文本仍在；固定矩形外不可计作完整可见，不证明无限渲染上限。"});ReleaseScratch();
        }
        private static void Click(Button button)
        {
            Assert.That(button.gameObject.activeInHierarchy&&button.interactable,Is.True);var events=EventSystem.current??new GameObject("R20隔离指针",typeof(EventSystem)).GetComponent<EventSystem>();var pointer=new PointerEventData(events){button=PointerEventData.InputButton.Left};ExecuteEvents.Execute(button.gameObject,pointer,ExecuteEvents.pointerDownHandler);ExecuteEvents.Execute(button.gameObject,pointer,ExecuteEvents.pointerUpHandler);Assert.That(ExecuteEvents.Execute(button.gameObject,pointer,ExecuteEvents.pointerClickHandler),Is.True);
        }
        [UnityTest] public IEnumerator FixedHudPreservesHiddenReopenedDocumentAndRejectsStaleScope()
        {
            yield return new EnterPlayMode();CreateHud();var samples=Samples();var rows=new List<Component>();
            foreach(var sample in samples){int player=sample.Code=="A01"?1:2;var host=(RectTransform)Call(modules,player==1?"GetLocalDetailHost":"GetOpponentDetailHost",player==1?0:player);rows.Add(AddRow(host,sample.Ability,sample.Title,sample.Document,true));}
            Call(modules,"RefreshDetailAvailability");Click((Button)Read(modules,"localDetailToggle"));var peer=((Button[])Read(modules,"opponentDetailToggles"))[0];Click(peer);for(int i=0;i<12;i++)yield return null;
            var canvas=(Canvas)Read(hud,"Canvas");StartCapture(canvas,Screen.width,Screen.height);Caption("隔离组件｜R20原HUD固定条目｜字号为原自动拟合结果，溢出单列",8,4);var dRow=rows.Single(r=>((string)Read(r,"ItemId")).Contains("D09"));var doc=Document(dRow);
            var evidence=new List<object>();evidence.Add(new{stage="首次稳定显示",value=Measure(dRow)});Capture("隔离-实际HUD-固定D09初次显示");
            int binds=(int)Read(doc,"BindCount");float rowHeight=((RectTransform)dRow.transform).rect.height,bodyHeight=Body(dRow).rectTransform.rect.height;Click(peer);yield return null;Click(peer);for(int i=0;i<12;i++)yield return null;
            Assert.That(Read(doc,"HasDocument"),Is.EqualTo(true));Assert.That(Read(doc,"ActiveFragmentCount"),Is.GreaterThan(0));Assert.That(Read(doc,"BindCount"),Is.EqualTo(binds));Assert.That(((RectTransform)dRow.transform).rect.height,Is.EqualTo(rowHeight));Assert.That(Body(dRow).rectTransform.rect.height,Is.EqualTo(bodyHeight));
            evidence.Add(new{stage="隐藏重开后",value=Measure(dRow)});Capture("隔离-实际HUD-固定D09隐藏重开");int layouts=(int)Read(doc,"LayoutCount"),renders=(int)Read(doc,"RenderCount");for(int i=0;i<20;i++)yield return null;Assert.That(Read(doc,"HasDocument"),Is.EqualTo(true));Assert.That(Read(doc,"LayoutCount"),Is.EqualTo(layouts));Assert.That(Read(doc,"RenderCount"),Is.EqualTo(renders));
            var aRows=rows.Where(r=>((string)Read(r,"ItemId")).Contains("A01")).ToArray();foreach(var row in aRows){Assert.That(Read(Document(row),"HasDocument"),Is.EqualTo(true));Assert.That(Read(Document(row),"ActiveFragmentCount"),Is.GreaterThan(0));}Capture("隔离-实际HUD-固定A01与D09");
            Click(peer);yield return null;current=false;Click(peer);yield return null;Assert.That(Read(doc,"HasDocument"),Is.EqualTo(false));current=true;
            var d=samples.Single(s=>s.Code=="D09");typeof(EntrepreneurDetailStabilityR18Tests).GetMethod("BindPersistentRow",Flags).Invoke(null,new object[]{dRow,d.Ability,d.Title,new Func<string,int,bool>((_,__)=>current)});BindDocument(dRow,d.Document);for(int i=0;i<5;i++)yield return null;Call(frame,"BindGameplaySnapshot",new object[]{null});yield return null;Assert.That(Read(doc,"HasDocument"),Is.EqualTo(false));Assert.That(readCount,Is.GreaterThan(0));Assert.That(readDepth,Is.Zero);
            Save("正文生命周期与固定高度分离验证",new{evidence,bindsBefore=binds,readCount,readDepth,stableFrames=20,expiredHiddenBindingCleared=true,revokedFrameScopeCleared=true,note="文档隐藏重开未消失；超出固定框仍为容量失败，与绑定清除分别报告。"});
        }
        [UnityTearDown] public IEnumerator Finish(){ReleaseScratch();if(hudObject!=null)UnityEngine.Object.Destroy(hudObject);if(UnityEngine.Application.isPlaying)yield return new ExitPlayMode();}
    }
}
