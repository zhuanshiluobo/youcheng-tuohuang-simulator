using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class InformationPagesPlayModeTests
    {
        private static Type T(string n)=>Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        private static Component Find(string n)=>(Component)UnityEngine.Object.FindObjectOfType(T(n));
        private static object P(object o,string n)=>o.GetType().GetProperty(n).GetValue(o);
        private static object F(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);
        private static object Call(object o,string n,params object[] args)=>o.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==n && m.GetParameters().Length==args.Length).Invoke(o,args);
        [UnityTest]
        public IEnumerator ActualSceneButtons_OpenCurrentRegionDataAndConsumeCompleteCloseClick()
        {
            foreach(var scene in new[]{"SampleScene","ThreePlayerScene"})
            {
                yield return Load(scene);
                var controller=Find("MobileCityInteractionController");var state=(GameState)P(controller,"CurrentState");
                var pages=Find("GameplayInformationPages");var map=(IMapQueryService)F(controller,"mapQuery");
                var count=scene=="ThreePlayerScene"?3:4;
                state.Players.Clear();var colors=new[]{PlayerColor.Green,PlayerColor.Yellow,PlayerColor.Blue,PlayerColor.Red};
                for(var i=0;i<count;i++)state.Players.Add(new PlayerState{PlayerId=i+1,Name="玩家 "+(i+1),Color=colors[i],CityLocationId=map.Map.Regions[0].LocationIds[0]});
                var before=GameStateCloneService.DeepClone(state);
                ((Button)P(pages,"RegionButton")).onClick.Invoke();yield return null;yield return null;
                var page=(Component)P(pages,"RegionPage");Assert.That(page.gameObject.activeInHierarchy,Is.True);
                var rows=(IReadOnlyList<RegionInformationRow>)P(page,"Projection");Assert.That(rows.Count,Is.EqualTo(count==3?7:8));
                Assert.That(rows[0].Influence.Count,Is.EqualTo(count));
                var dismiss=page.GetComponent(T("InformationPageDismiss"));
                var data=new PointerEventData(EventSystem.current){pointerId=11,button=PointerEventData.InputButton.Left,position=new Vector2(900,500),pointerCurrentRaycast=new RaycastResult{gameObject=page.gameObject}};
                Call(dismiss,"OnPointerUp",data);Assert.That(page.gameObject.activeSelf,Is.True,"打开按钮的抬起不能关闭新页。");
                Call(dismiss,"OnPointerDown",data);data.position+=Vector2.right*12;Call(dismiss,"OnDrag",data);Call(dismiss,"OnPointerUp",data);
                Assert.That(page.gameObject.activeSelf,Is.True,"拖动不会关闭。");
                yield return Capture(scene+"-区控");
                yield return VerifyPageLayout(page, scene+"-区控");
                Call(dismiss,"OnPointerDown",data);Call(dismiss,"OnPointerUp",data);Assert.That(page.gameObject.activeSelf,Is.False);
                Assert.That(GameStateCloneService.AreEquivalent(before,state),Is.True,"信息查看不得提交任何规则修改。");
            }
        }
        [UnityTest]
        public IEnumerator GameBox_ShowsReturnedComponentsInspectsBackAndRetainsTokenScroll()
        {
            yield return Load("SampleScene");
            var controller=Find("MobileCityInteractionController");var state=(GameState)P(controller,"CurrentState");var viewer=(int)F(controller,"localPlayerId");
            var pages=Find("GameplayInformationPages");var catalog=P(Find("GameplayDialogRegistry"),"CardVisualCatalog");
            var cardId=CharacterCardDatabase.Liskarm;
            var events=EventCardDatabase.GetCardIds(EventColor.Green);
            for(var i=0;i<8;i++)state.GameBox.Cards.Add(new GameBoxCardState{InstanceId="char-"+i,CardId=cardId,Kind=GameBoxCardKind.Character,FaceUp=i%2==0,BackColor=PlayerColor.Blue});
            for(var i=0;i<3;i++)state.GameBox.Cards.Add(new GameBoxCardState{InstanceId="event-"+i,CardId=events[i%events.Count],Kind=GameBoxCardKind.Event});
            state.GameBox.Markers.Add(new GameBoxMarkerState{Color=PlayerColor.Blue,Count=27});
            state.GameBox.Enterprises.Add(new GameBoxEnterpriseState{OwnerPlayerId=viewer,VisualKey="enterprise-0"});
            state.GameBox.Enterprises.Add(new GameBoxEnterpriseState{OwnerPlayerId=viewer+1,VisualKey="enterprise-1"});
            for(var i=0;i<22;i++)state.GameBox.Tokens.Add(new GameBoxTokenState{InstanceId="token-"+i,VisualKey="resource-Originium-0",Count=i+1,State=i%2==0?"used":"unused"});
            var before=GameStateCloneService.DeepClone(state);
            ((Button)P(pages,"GameBoxButton")).onClick.Invoke();for(var i=0;i<5;i++)yield return null;
            var box=(Component)P(pages,"GameBoxPage");Assert.That(box.gameObject.activeInHierarchy,Is.True);
            var boardImage=(RawImage)F(box,"enterpriseImage");
            var board=Call(F(box,"enterprises"),"Find","enterprise-0");
            Assert.That(boardImage.uvRect,Is.EqualTo((Rect)board.GetType().GetField("uv").GetValue(board)),"只显示当前查看者放回的企业板。");
            var cards=(IReadOnlyList<GameBoxCardState>)Call(box,"Cards",GameBoxCardKind.Character);
            var images=(IReadOnlyList<Sprite>)Call(box,"Images",GameBoxCardKind.Character);
            Assert.That(cards.Count,Is.EqualTo(8));Assert.That(images,Is.All.Not.Null);
            var backTexture=(Texture2D)Call(catalog,"GetCharacterBack",PlayerColor.Blue);
            Assert.That(images[7].texture,Is.SameAs(backTexture),"顶牌保持背面，不自动翻面。");
            var scroll=(ScrollRect)P(box,"TokenScroll");Assert.That(scroll.content.rect.height,Is.GreaterThan(scroll.viewport.rect.height));
            scroll.verticalNormalizedPosition=.31f;Canvas.ForceUpdateCanvases();yield return null;
            var position=scroll.verticalNormalizedPosition;
            yield return Capture("SampleScene-游戏盒");
            yield return VerifyPageLayout(box,"SampleScene-游戏盒");
            Assert.That(scroll.verticalNormalizedPosition,Is.EqualTo(position).Within(.02),"尺寸变化后保留 Token 滚动位置。");
            var pile=P(box,"CharacterPile");((Button)P(pile,"InspectButton")).onClick.Invoke();yield return null;
            var picker=(Component)F(pages,"picker");Assert.That(picker.gameObject.activeInHierarchy,Is.True);
            foreach(var node in picker.GetComponentsInChildren<Transform>(true))
                Assert.That(node.name.Any(ch=>ch>='\u4e00'&&ch<='\u9fff'),Is.False,node.name);
            Assert.That(picker.GetComponentsInChildren(T("FacilityEffectCardView")).Length,Is.EqualTo(8));
            var item=picker.GetComponentsInChildren(T("FacilityEffectCardView")).Cast<Component>().Last();
            ExecuteEvents.Execute<IPointerClickHandler>(((Button)P(item,"Button")).gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Right},ExecuteEvents.pointerClickHandler);
            yield return null;
            var cardViewer=(Component)F(pages,"cardViewer");Assert.That(cardViewer,Is.Not.Null);Assert.That((bool)P(cardViewer,"IsShowing"),Is.True);
            Assert.That(P(cardViewer,"Mode").ToString(),Is.EqualTo("Inspect"));
            Assert.That(((Sprite)P(cardViewer,"DisplayedSprite")).texture,Is.SameAs(backTexture));
            Assert.That(((Button)P(cardViewer,"PlotButton")).gameObject.activeInHierarchy,Is.False);
            Call(cardViewer,"Close");yield return null;Assert.That(picker.gameObject.activeInHierarchy,Is.True);
            Call(pages,"ReturnToBox");yield return null;Assert.That(box.gameObject.activeInHierarchy,Is.True);
            Assert.That(scroll.verticalNormalizedPosition,Is.EqualTo(position).Within(.01));
            var dismiss=box.GetComponent(T("InformationPageDismiss"));
            var blank=new PointerEventData(EventSystem.current){pointerId=2,button=PointerEventData.InputButton.Left,position=new Vector2(700,900),pointerCurrentRaycast=new RaycastResult{gameObject=box.gameObject}};
            Call(dismiss,"OnPointerDown",blank);blank.pointerCurrentRaycast=new RaycastResult{gameObject=scroll.viewport.gameObject};Call(dismiss,"OnPointerUp",blank);
            Assert.That(box.gameObject.activeSelf,Is.True,"按下空白、抬起内容区不能关闭。");
            state.GameBox.Cards.Clear();state.GameBox.Enterprises.Clear();state.GameBox.Tokens.Clear();Call(pages,"Refresh");yield return null;
            Assert.That(((IReadOnlyList<GameBoxCardState>)Call(box,"Cards",GameBoxCardKind.Character)).Count,Is.Zero);
            Assert.That(scroll.verticalScrollbar.gameObject.activeInHierarchy,Is.False);
            yield return Capture("SampleScene-游戏盒空盒");
            Assert.That((bool)Call(pages,"TryHandleEscape"),Is.True);
            Assert.That(box.gameObject.activeSelf,Is.False);
            // 前面的查看交互必须保持权威数据；清空是本测试最后的显式准备。
            state.GameBox=before.GameBox;
            Assert.That(GameStateCloneService.AreEquivalent(before,state),Is.True);
            Call(pages,"CloseGameBox");Assert.That(box.gameObject.activeSelf,Is.False);
        }
        private static IEnumerator Load(string scene)
        {
            var launch=Find("GameLaunchContext");if(launch!=null){UnityEngine.Object.Destroy(launch.gameObject);yield return null;}
            yield return SceneManager.LoadSceneAsync(scene,LoadSceneMode.Single);yield return null;yield return null;
#if UNITY_EDITOR
            SetSize(new Vector2Int(1920,1080));
#endif
            for(var i=0;i<8;i++)yield return null;
        }
        private static IEnumerator VerifyPageLayout(Component page, string name)
        {
            var frame = Find("GameplayHudFrame");
            var host = (RectTransform)P(frame,"ContentRect");
            Assert.That(page.transform.parent,Is.SameAs(host),"两个信息页都由实际 PageHost 承载。");
            var window = (RectTransform)P(page,"Window");
            var initial = ScreenRect(window);
            foreach (var size in new[] {new Vector2Int(1920,1200),new Vector2Int(2560,1080),
                new Vector2Int(1280,720),new Vector2Int(900,600),new Vector2Int(1920,1080)})
            {
#if UNITY_EDITOR
                SetSize(size);
#endif
                for(var i=0;i<12;i++)yield return null;
                Canvas.ForceUpdateCanvases();
                var bounds = ScreenRect(host);
                var actual = ScreenRect((RectTransform)page.transform);
                Assert.That(actual.x,Is.EqualTo(bounds.x).Within(.5));
                Assert.That(actual.y,Is.EqualTo(bounds.y).Within(.5));
                Assert.That(actual.width,Is.EqualTo(bounds.width).Within(.5));
                Assert.That(actual.height,Is.EqualTo(bounds.height).Within(.5));
                Contains(bounds,ScreenRect(window),"窗口不得越过 PageHost 边界。");
                var top = (RectTransform)P(frame,"TopBar");
                var point = ScreenRect(top).center;
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=point},hits);
                Assert.That(hits.Count,Is.GreaterThan(0));
                Assert.That(hits[0].gameObject.transform.IsChildOf(top),Is.True,"页面遮罩不得拦截常驻顶栏。");
                foreach(var scroll in page.GetComponentsInChildren<ScrollRect>())
                    Contains(ScreenRect(window),ScreenRect(scroll.viewport),"内部视口留在窗口内。");
                if(page.GetType().Name=="RegionInformationPageView")
                {
                    var rowViews=page.GetComponentsInChildren(T("RegionInformationRowView"));
                    var viewport=page.GetComponentInChildren<ScrollRect>().viewport;
                    foreach(var row in rowViews)Contains(ScreenRect(viewport),ScreenRect((RectTransform)row.transform),"支持尺寸内全部区域行可见。");
                }
                yield return Capture(name+"-"+size.x+"x"+size.y);
            }
            var restored = ScreenRect(window);
            Assert.That(restored.x,Is.EqualTo(initial.x).Within(.5));
            Assert.That(restored.y,Is.EqualTo(initial.y).Within(.5));
            Assert.That(restored.width,Is.EqualTo(initial.width).Within(.5));
            Assert.That(restored.height,Is.EqualTo(initial.height).Within(.5));
        }
        private static void Contains(Rect outer, Rect inner, string reason)
        {
            Assert.That(inner.xMin,Is.GreaterThanOrEqualTo(outer.xMin-.5),reason);
            Assert.That(inner.yMin,Is.GreaterThanOrEqualTo(outer.yMin-.5),reason);
            Assert.That(inner.xMax,Is.LessThanOrEqualTo(outer.xMax+.5),reason);
            Assert.That(inner.yMax,Is.LessThanOrEqualTo(outer.yMax+.5),reason);
        }
#if UNITY_EDITOR
        private static void SetSize(Vector2Int size)
        {
            var capture=Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor",true);
            capture.GetMethod("PrepareGameView",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);
            capture.GetMethod("SelectSize",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{size});
        }
#endif
        private static IEnumerator Capture(string name)
        {
            Canvas.ForceUpdateCanvases();for(var i=0;i<6;i++)yield return null;
            var captureFolder = Environment.GetEnvironmentVariable("YC_INFORMATION_CAPTURE_OUTPUT") ?? "Logs/InformationPages-20261001/visual";
            var directory=Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath,"..",captureFolder));Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,name+".png");ScreenCapture.CaptureScreenshot(path);for(var i=0;i<12;i++)yield return null;
            Assert.That(File.Exists(path),Is.True);
            var pages = Find("GameplayInformationPages");
            var page = (Component)P(pages, name.Contains("区控") ? "RegionPage" : "GameBoxPage");
            var metrics = new PageMetrics { width = Screen.width, height = Screen.height };
            var frame = Find("GameplayHudFrame");
            foreach (var field in new[] { "ContentRect", "TopBar", "BottomBar" })
                metrics.rects.Add(new RectMetric { path = field, rect = ScreenRect((RectTransform)P(frame,field)) });
            foreach (var rect in page.GetComponentsInChildren<RectTransform>())
                metrics.rects.Add(new RectMetric { path = HierarchyPath(rect, page.transform), rect = ScreenRect(rect) });
            File.WriteAllText(Path.Combine(directory,name+"-rects.json"),JsonUtility.ToJson(metrics,true));
        }
        [Serializable] private sealed class PageMetrics
        { public int width, height; public List<RectMetric> rects = new List<RectMetric>(); }
        [Serializable] private sealed class RectMetric { public string path; public Rect rect; }
        private static string HierarchyPath(Transform child, Transform root)
        {
            var path = child.name;
            while(child != root && child.parent != null) { child = child.parent; path = child.name + "/" + path; }
            return path;
        }
        private static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4];rect.GetWorldCorners(corners);
            var low = RectTransformUtility.WorldToScreenPoint(null,corners[0]);
            var high = RectTransformUtility.WorldToScreenPoint(null,corners[2]);
            return Rect.MinMaxRect(low.x,low.y,high.x,high.y);
        }
    }
}
