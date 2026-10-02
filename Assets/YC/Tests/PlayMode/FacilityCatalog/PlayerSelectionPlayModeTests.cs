using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Interactions;
using YC.Domain.Facilities;
using YC.Domain.Commands;

namespace YC.Tests.PlayMode
{
    public sealed class PlayerSelectionPlayModeTests
    {
        private static Type T(string n)=>Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        private static Component Find(string n)=>(Component)UnityEngine.Object.FindObjectOfType(T(n));
        private static object P(object o,string n)=>o.GetType().GetProperty(n).GetValue(o);
        private static object F(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);
        private static void S(object o,string n,object v)=>o.GetType().GetField(n).SetValue(o,v);
        private static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(o,a);
        private static Component[] Options(Component p)=>((IEnumerable)P(p,"Options")).Cast<Component>().Where(c=>c.gameObject.activeSelf).ToArray();
        private static int[] Selected(Component p)=>((IEnumerable)P(P(p,"Draft"),"SelectedIds")).Cast<int>().ToArray();
        private static object Config(int count,bool multi=false,int min=1,int max=1,string request="player-selection")
        {
            var c=Activator.CreateInstance(T("PlayerSelectionConfig"));S(c,"RequestId",request);S(c,"Min",min);S(c,"Max",max);
            S(c,"Mode",Enum.Parse(T("PlayerSelectionMode"),multi?"Multiple":"Single"));
            var players=Array.CreateInstance(T("PlayerSelectionOption"),count);var colors=new[]{PlayerColor.Green,PlayerColor.Yellow,PlayerColor.Blue,PlayerColor.Red};
            for(var i=0;i<count;i++)
            {var p=Activator.CreateInstance(T("PlayerSelectionOption"));S(p,"Id",i+1);S(p,"Name","玩家 "+(i+1));S(p,"Color",colors[i]);S(p,"IsSelf",i==0);players.SetValue(p,i);}
            S(c,"Players",players);return c;
        }
        private static void Present(Component page,object config,Action<int[]> submit,Action cancel,Func<bool> current=null,Delegate latest=null)=>
            Call(page,"Present",config,submit,cancel,current,false,null,latest);
        private static Delegate Provider(Func<object> get)
        {
            var type=typeof(Func<>).MakeGenericType(T("PlayerSelectionConfig"));
            return Expression.Lambda(type,Expression.Convert(Expression.Invoke(Expression.Constant(get)),T("PlayerSelectionConfig"))).Compile();
        }
        private static IEnumerator Load(string scene)
        {
            var launch=Find("GameLaunchContext");if(launch!=null){UnityEngine.Object.Destroy(launch.gameObject);yield return null;}
            yield return SceneManager.LoadSceneAsync(scene,LoadSceneMode.Single);SetSize(new Vector2Int(1920,1080));
            for(var i=0;i<8;i++)yield return null;
        }
        private static Component Create()=> (Component)Call(Find("GameplayDialogRegistry"),"InstantiatePlayerSelection",(RectTransform)P(Find("GameplayHudFrame"),"ContentRect"));
        [UnityTest]
        public IEnumerator Selection_UsesExplicitConfirmationLimitsFreshEligibilityAndDistinctCancel()
        {
            yield return Load("SampleScene");var page=Create();var answers=new List<int[]>();var cancels=0;
            var config=Config(4);Present(page,config,answers.Add,()=>cancels++);yield return null;
            ((Button)P(Options(page)[0],"Button")).onClick.Invoke();Assert.That(answers,Is.Empty);
            Call(page,"Choose",2);Call(page,"Choose",2);CollectionAssert.AreEqual(new[]{2},Selected(page));
            ((Button)P(page,"ConfirmButton")).onClick.Invoke();Call(page,"Confirm");Assert.That(answers.Count,Is.EqualTo(1));CollectionAssert.AreEqual(new[]{2},answers[0]);
            config=Config(4,true,2,2,"multiple");var players=(Array)config.GetType().GetField("Players").GetValue(config);
            S(players.GetValue(3),"Eligible",false);S(players.GetValue(3),"DisabledReason","本次不能选择自己");
            Present(page,config,answers.Add,()=>cancels++);yield return null;Call(page,"Choose",1);Call(page,"Confirm");Assert.That(answers.Count,Is.EqualTo(1));
            Call(page,"Choose",2);Call(page,"Choose",3);Call(page,"Choose",4);CollectionAssert.AreEqual(new[]{1,2},Selected(page));
            Call(page,"Choose",1);Call(page,"Choose",3);CollectionAssert.AreEqual(new[]{2,3},Selected(page));
            var marker=(Image)F(Options(page)[3],"marker");Assert.That(marker.color,Is.EqualTo(Color.white));
            S(players.GetValue(1),"Eligible",false);Call(page,"Refresh",config);CollectionAssert.AreEqual(new[]{3},Selected(page));
            Assert.That(((Button)P(page,"ConfirmButton")).interactable,Is.False);
            Call(page,"TryHandleEscape");Assert.That(cancels,Is.EqualTo(1));Assert.That(answers.Count,Is.EqualTo(1));
            config=Config(0,true,0,2,"optional");Present(page,config,answers.Add,()=>cancels++);Call(page,"Confirm");Assert.That(answers.Last(),Is.Empty);
            config=Config(2,false,1,1,"required");S(config,"AllowCancel",false);Present(page,config,answers.Add,()=>cancels++);
            Assert.That(((Button)P(page,"CancelButton")).gameObject.activeSelf,Is.False);Assert.That(((Button)P(page,"CloseButton")).gameObject.activeSelf,Is.False);
            Call(page,"TryHandleEscape");Assert.That(page.gameObject.activeInHierarchy,Is.True);Assert.That(cancels,Is.EqualTo(1));
            var fresh=Config(2,false,1,1,"fresh");Present(page,fresh,answers.Add,()=>cancels++,null,Provider(()=>fresh));
            Call(page,"Choose",1);S(fresh,"Revision",1);S(((Array)fresh.GetType().GetField("Players").GetValue(fresh)).GetValue(0),"Eligible",false);
            Call(page,"Confirm");Assert.That(answers.Count,Is.EqualTo(2));Assert.That(((Button)P(page,"ConfirmButton")).interactable,Is.False);
            Call(page,"Close");
        }
        [UnityTest]
        public IEnumerator BothScenes_NativeCenteredRowsKeepWindowAndAllOptionsInsidePageHost()
        {
            foreach(var scene in new[]{"SampleScene","ThreePlayerScene"})
            {
                yield return Load(scene);var page=Create();var host=(RectTransform)P(Find("GameplayHudFrame"),"ContentRect");
                Assert.That(page.transform.parent,Is.SameAs(host));
                for(var n=1;n<=4;n++)
                {
                    Present(page,Config(n),_=>{},()=>{});for(var f=0;f<4;f++)yield return null;Canvas.ForceUpdateCanvases();
                    var row=(RectTransform)P(page,"OptionsRow");var options=Options(page);Assert.That(options.Length,Is.EqualTo(n));
                    var first=ScreenRect((RectTransform)options.First().transform);var last=ScreenRect((RectTransform)options.Last().transform);
                    Assert.That((first.xMin+last.xMax)/2,Is.EqualTo(ScreenRect(row).center.x).Within(.5));
                }
                foreach(var size in new[]{new Vector2Int(1920,1080),new Vector2Int(1920,1200),new Vector2Int(2560,1080),new Vector2Int(1280,720),new Vector2Int(900,600),new Vector2Int(1920,1080)})
                {
                    SetSize(size);for(var f=0;f<12;f++)yield return null;Canvas.ForceUpdateCanvases();
                    Contains(ScreenRect(host),ScreenRect((RectTransform)P(page,"Window")));
                    Rect? previous=null;
                    foreach(var option in Options(page))
                    {
                        var rect=ScreenRect((RectTransform)option.transform);Contains(ScreenRect((RectTransform)P(page,"OptionsRow")),rect);
                        if(previous.HasValue)Assert.That(rect.xMin,Is.GreaterThanOrEqualTo(previous.Value.xMax-.5f));previous=rect;
                        foreach(var child in option.GetComponentsInChildren<Graphic>())Contains(rect,ScreenRect(child.rectTransform));
                    }
                    var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=ScreenRect(host).min+Vector2.one*4},hits);
                    Assert.That(hits.First().gameObject.transform.IsChildOf(page.transform),Is.True,"遮罩拦截地图输入，空白不能关闭。");
                    hits.Clear();var confirmButton=(Button)P(page,"ConfirmButton");
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=ScreenRect((RectTransform)confirmButton.transform).center},hits);
                    Assert.That(hits.First().gameObject.transform,Is.SameAs(confirmButton.transform),"页面操作按钮不能被手牌或主界面拦截。");
                    Assert.That(page.gameObject.activeInHierarchy,Is.True);
                    foreach(var node in page.GetComponentsInChildren<Transform>(true))Assert.That(node.name.Any(c=>c>='\u4e00'&&c<='\u9fff'),Is.False,node.name);
                    yield return Capture(scene+"-"+size.x+"x"+size.y);
                }
                var before=Selected(page);var button=(Button)P(Options(page)[1],"Button");
                ExecuteEvents.Execute(button.gameObject,new BaseEventData(EventSystem.current),ExecuteEvents.submitHandler);
                Assert.That(Selected(page),Is.Not.EqualTo(before));Assert.That(EventSystem.current.currentSelectedGameObject.transform.IsChildOf(page.transform),Is.True);
                for(var i=0;i<12;i++)
                {Call(page,"MoveFocus",i%2==0?1:-1);Assert.That(EventSystem.current.currentSelectedGameObject.transform.IsChildOf(page.transform),Is.True);}
                var illustrated=Config(4,true,1,2,"illustrated");var illustrationPlayers=(Array)illustrated.GetType().GetField("Players").GetValue(illustrated);
                S(illustrationPlayers.GetValue(1),"Name","这是一位名字特别长需要缩小或省略的玩家");
                S(illustrationPlayers.GetValue(3),"Eligible",false);S(illustrationPlayers.GetValue(3),"DisabledReason","本次目标不可选择");
                var avatarTexture=new Texture2D(16,32);var pixels=new Color[16*32];
                for(var i=0;i<pixels.Length;i++)pixels[i]=Color.Lerp(new Color(.7f,.8f,.85f),new Color(.3f,.5f,.6f),(float)(i/16)/31);
                avatarTexture.SetPixels(pixels);avatarTexture.Apply();S(illustrationPlayers.GetValue(0),"Avatar",avatarTexture);
                Present(page,illustrated,_=>{},()=>{});Call(page,"Choose",1);Call(page,"Choose",3);
                for(var f=0;f<8;f++)yield return null;
                Assert.That(((RawImage)F(Options(page)[0],"avatar")).uvRect.height,Is.EqualTo(.5f).Within(.001f));
                Assert.That(((Text)F(Options(page)[1],"playerName")).text,Does.EndWith("…"));
                Assert.That(((GameObject)F(Options(page)[3],"disabledState")).activeSelf,Is.True);
                Assert.That(((Image)F(Options(page)[2],"check")).gameObject.activeSelf,Is.True);
                yield return Capture(scene+"-selected-disabled");
                UnityEngine.Object.Destroy(avatarTexture);
                Call(page,"Close");
            }
        }
        [UnityTest]
        public IEnumerator FacilityPlayerRequest_OpensSharedPageAndSubmitsOneAnswerThroughExistingRenderer()
        {
            yield return Load("SampleScene");var registry=Find("GameplayDialogRegistry");var state=new GameState();
            state.Players.Add(new PlayerState{PlayerId=1,Name="甲",Color=PlayerColor.Green});state.Players.Add(new PlayerState{PlayerId=2,Name="乙",Color=PlayerColor.Blue});
            var request=new InteractionRequest{InteractionId="test-player-request",InteractionTypeId=FacilityEntryEffectTypeIds.InteractionType,
                AnsweringPlayerId=1,Visibility="public",Status="open",MinSelections=1,MaxSelections=1,StateRevision=0,
                CandidateIds=new List<string>{"player:2"}};state.EffectRuntime.InteractionRequests.Add(request);
            var type=T("FacilityInteractionUiCoordinator");var ctor=type.GetConstructors().Single();var args=new object[ctor.GetParameters().Length];
            var submitted=new List<GameCommand>();var parameters=ctor.GetParameters();
            for(var i=0;i<args.Length;i++)
            {
                var t=parameters[i].ParameterType;
                if(t==typeof(Func<GameState>))args[i]=(Func<GameState>)(()=>state);
                else if(t==typeof(Func<int>))args[i]=(Func<int>)(()=>1);
                else if(t==typeof(Func<RectTransform>))args[i]=(Func<RectTransform>)(()=>(RectTransform)P(Find("GameplayHudFrame"),"ContentRect"));
                else if(t.IsInstanceOfType(registry))args[i]=registry;
                else if(t==typeof(Action<GameCommand>))args[i]=(Action<GameCommand>)submitted.Add;
                else {var invoke=t.GetMethod("Invoke");args[i]=Expression.Lambda(t,Expression.Empty(),invoke.GetParameters().Select(p=>Expression.Parameter(p.ParameterType)).ToArray()).Compile();}
            }
            var coordinator=ctor.Invoke(args);Call(coordinator,"Render",InteractionRequestProjector.ProjectForPlayer(request,1));yield return null;
            var page=Find("PlayerSelectionPageView");Assert.That(page,Is.Not.Null);Assert.That(Options(page).Length,Is.EqualTo(2));
            Call(page,"Choose",1);Assert.That(Selected(page),Is.Empty);Call(page,"Choose",2);Assert.That(submitted,Is.Empty);
            Call(page,"Confirm");Call(page,"Confirm");Assert.That(submitted.Count,Is.EqualTo(1));
            CollectionAssert.AreEqual(new[]{"player:2"},submitted[0].OptionIds);Call(coordinator,"Dispose");
        }
        private static void SetSize(Vector2Int size)
        {
#if UNITY_EDITOR
            var type=Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor",true);
            type.GetMethod("PrepareGameView",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            type.GetMethod("SelectSize",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{size});
#endif
        }
        private static Rect ScreenRect(RectTransform r)
        {var c=new Vector3[4];r.GetWorldCorners(c);var l=RectTransformUtility.WorldToScreenPoint(null,c[0]);var h=RectTransformUtility.WorldToScreenPoint(null,c[2]);return Rect.MinMaxRect(l.x,l.y,h.x,h.y);}
        private static void Contains(Rect outer,Rect inner)
        {Assert.That(inner.xMin,Is.GreaterThanOrEqualTo(outer.xMin-.5));Assert.That(inner.yMin,Is.GreaterThanOrEqualTo(outer.yMin-.5));Assert.That(inner.xMax,Is.LessThanOrEqualTo(outer.xMax+.5));Assert.That(inner.yMax,Is.LessThanOrEqualTo(outer.yMax+.5));}
        private static IEnumerator Capture(string name)
        {
            var folder=Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath,"../Logs/PlayerSelection-20261002/visual"));Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,name+".png");ScreenCapture.CaptureScreenshot(path);for(var i=0;i<12;i++)yield return null;Assert.That(File.Exists(path),Is.True);
        }
    }
}
