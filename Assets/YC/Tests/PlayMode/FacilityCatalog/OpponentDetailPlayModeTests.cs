using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class OpponentDetailPlayModeTests
    {
        [UnityTest]
        public IEnumerator BothScenes_DetailScrollAndCityStayInsideLiveContentAcrossResizeAndRestore()
        {
            foreach (var sceneName in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                yield return null;
                var type = Type.GetType("YC.Presentation.GameplayMainModules, Assembly-CSharp", true);
                var modules = UnityEngine.Object.FindObjectOfType(type) as Component;
                var frameType = Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true);
                var frame = UnityEngine.Object.FindObjectOfType(frameType) as Component;
                Assert.That(modules, Is.Not.Null, sceneName);
                Assert.That(frame, Is.Not.Null, sceneName);
                var state = new GameState();
                state.Players.Add(new PlayerState { PlayerId = 1, Color = PlayerColor.Red });
                state.Players.Add(new PlayerState { PlayerId = 2, Color = PlayerColor.Blue });
                state.Players.Add(new PlayerState { PlayerId = 3, Color = PlayerColor.Green });
                var visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
                modules.GetType().GetMethod("Render").Invoke(modules,
                    new object[] { GameStateViewProjector.ToClientState(visible), visible, 1, null });
                var toggles = (Button[])Field(modules, "opponentDetailToggles");
                var panels = (GameObject[])Field(modules, "opponentDetailPanels");
                var city = (RectTransform)Field(modules, "cityRegion");
                var artwork = (RawImage)Field(modules, "cityBoardArtwork");
                var buildViewType = Type.GetType("YC.Presentation.BuildInfoPanelView, Assembly-CSharp", true);
                var buildView = UnityEngine.Object.FindObjectOfType(buildViewType) as Component;
                Assert.That(buildView, Is.Not.Null, sceneName + " 建设面板未加载");
                Assert.That(buildViewType.GetProperty("CityBoardImage").GetValue(buildView), Is.SameAs(artwork),
                    "建设输入必须使用当前可见城市原图");
                var main = (RectTransform)Field(frame, "mainRegions");
                var content = (RectTransform)Field(frame, "contentRect");
                var bottom = (RectTransform)Field(frame, "bottomBar");
                var columns = (Component)Field(frame, "columnsLayout");
                Assert.That(columns, Is.TypeOf<HorizontalLayoutGroup>());
                Layout(main);
                // 三列始终同屏；经过真实渲染帧建立 Graphic depth。
                yield return null;
                var originalCity = ScreenRect(city);
                var artworkRatio = ScreenRect(artwork.rectTransform).width / ScreenRect(artwork.rectTransform).height;
                AssertBounds(city, artwork, content, bottom, artworkRatio, sceneName);
                AssertSlots(buildView, artwork);
                toggles[0].onClick.Invoke();
                Layout(main);
                Assert.That(panels[0].activeSelf, Is.True);
                Assert.That(panels[1].activeSelf, Is.False);
                AssertBounds(city, artwork, content, bottom, artworkRatio, sceneName + " 空明细");
                Assert.That(ScreenRect(city).yMin, Is.EqualTo(originalCity.yMin).Within(.05f));
                var host = (RectTransform)modules.GetType().GetMethod("GetOpponentDetailHost").Invoke(modules, new object[] { 2 });
                RectTransform lastRow = null;
                for (var i = 0; i < 20; i++)
                {
                    var row = new GameObject("测试公开条目 " + i, typeof(RectTransform), typeof(LayoutElement));
                    lastRow = row.GetComponent<RectTransform>(); lastRow.SetParent(host, false);
                    row.GetComponent<LayoutElement>().preferredHeight = 76f;
                }
                modules.GetType().GetMethod("SetOpponentDetailCount").Invoke(modules, new object[] { 2, 20 });
                Layout(main);
                var scroll = panels[0].GetComponent<ScrollRect>();
                Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height), "长内容必须留在内部滚动区");
                scroll.verticalNormalizedPosition = 0f;
                Canvas.ForceUpdateCanvases();
                var viewport = ScreenRect(scroll.viewport);
                var last = ScreenRect(lastRow);
                Assert.That(last.yMin, Is.GreaterThanOrEqualTo(viewport.yMin - .5f), "滚动末项必须可见");
                Assert.That(last.yMax, Is.LessThanOrEqualTo(viewport.yMax + .5f), "滚动末项不能落在视口外");
                AssertBounds(city, artwork, content, bottom, artworkRatio, sceneName + " 长明细");
                AssertSlots(buildView, artwork);

                // 直接改变实际内容容器的可用矩形，验证布局不依赖 Awake 缓存。
                // 应用窗口的真实多尺寸截图由独立采集入口核验，此处不把 Editor SetResolution 当作成功证据。
                var originalMin = main.offsetMin; var originalMax = main.offsetMax;
                try
                {
                    main.offsetMin += new Vector2(0f, 160f); main.offsetMax += new Vector2(-300f, 0f);
                    Layout(main);
                    Assert.That(ScreenRect(city.parent as RectTransform).height,
                        Is.LessThan(ScreenRect(content).height), "测试必须实际缩小玩家列的可用高度");
                    AssertBounds(city, artwork, content, bottom, artworkRatio, sceneName + " 受限列缩小");
                    AssertSlots(buildView, artwork);
                    // 该输入会迫使标题压缩，旧分配方式在此把展开明细分配成零高。
                    main.offsetMin += new Vector2(0f, Mathf.Max(0f, main.rect.height - 400f));
                    Layout(main);
                    Assert.That(scroll.viewport.rect.height, Is.GreaterThanOrEqualTo(lastRow.rect.height),
                        "低高度仍须保留可读的明细视口，不能只保留已激活的零高面板");
                    scroll.verticalNormalizedPosition = 0f;
                    Canvas.ForceUpdateCanvases();
                    Contains(ScreenRect(scroll.viewport), ScreenRect(lastRow), sceneName + " 低高度末项仍可滚动到达");
                    AssertBounds(city, artwork, content, bottom, artworkRatio, sceneName + " 低高度城市仍存在");
                    AssertSlots(buildView, artwork);
                    toggles[1].onClick.Invoke(); Layout(main);
                    Assert.That(panels[0].activeSelf, Is.False); Assert.That(panels[1].activeSelf, Is.True);
                    AssertBounds(city, artwork, content, bottom, artworkRatio, sceneName + " 缩小后切换玩家");
                    toggles[1].onClick.Invoke(); Layout(main);
                    Assert.That(panels[1].activeSelf, Is.False);
                }
                finally
                {
                    main.offsetMin = originalMin; main.offsetMax = originalMax; Layout(main);
                }
                AssertBounds(city, artwork, content, bottom, artworkRatio, sceneName + " 恢复");
                var restored = ScreenRect(city);
                Assert.That(restored.x, Is.EqualTo(originalCity.x).Within(.05f));
                Assert.That(restored.y, Is.EqualTo(originalCity.y).Within(.05f));
                Assert.That(restored.width, Is.EqualTo(originalCity.width).Within(.05f));
                Assert.That(restored.height, Is.EqualTo(originalCity.height).Within(.05f));
            }
        }
        private static void Layout(RectTransform root)
        { Canvas.ForceUpdateCanvases(); LayoutRebuilder.ForceRebuildLayoutImmediate(root); Canvas.ForceUpdateCanvases(); }
        private static void AssertBounds(RectTransform city, RawImage artwork, RectTransform content,
            RectTransform bottom, float aspect, string message)
        {
            var cityRect = ScreenRect(city); var contentRect = ScreenRect(content); var bottomRect = ScreenRect(bottom);
            var columnRect = ScreenRect(city.parent as RectTransform); var image = ScreenRect(artwork.rectTransform);
            Assert.That(cityRect.width, Is.GreaterThan(0f), message); Assert.That(cityRect.height, Is.GreaterThan(0f), message);
            Contains(contentRect, cityRect, message + " 城市/内容区"); Contains(columnRect, cityRect, message + " 城市/玩家列");
            Contains(cityRect, image, message + " 城市原图");
            Assert.That(artwork.gameObject.activeInHierarchy && artwork.enabled, Is.True, message);
            Assert.That(image.width, Is.GreaterThan(0f), message);
            Assert.That(image.height, Is.GreaterThan(0f), message);
            Assert.That(cityRect.yMin, Is.GreaterThanOrEqualTo(bottomRect.yMax - .05f), message + " 城市不得与底栏交叠");
            Assert.That(image.width / image.height, Is.EqualTo(aspect).Within(.001f), message + " 城市原图必须等比");
        }
        private static void Contains(Rect outer, Rect inner, string message)
        {
            Assert.That(inner.xMin, Is.GreaterThanOrEqualTo(outer.xMin - .05f), message);
            Assert.That(inner.xMax, Is.LessThanOrEqualTo(outer.xMax + .05f), message);
            Assert.That(inner.yMin, Is.GreaterThanOrEqualTo(outer.yMin - .05f), message);
            Assert.That(inner.yMax, Is.LessThanOrEqualTo(outer.yMax + .05f), message);
        }
        private static void AssertSlots(Component view, RawImage artwork)
        {
            var slots = (Array)view.GetType().GetProperty("CityBoardSlots").GetValue(view);
            Assert.That(slots.Length, Is.EqualTo(12));
            var resolve = Type.GetType("YC.Presentation.FacilityCardDragUtility, Assembly-CSharp", true)
                .GetMethod("ResolveCityBoardSlotIndex", new[] { typeof(PointerEventData), typeof(RectTransform) });
            var image = ScreenRect(artwork.rectTransform);
            Assert.That(EventSystem.current, Is.Not.Null);
            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots.GetValue(i) as Component;
                Assert.That(slot, Is.Not.Null, "城市格位 #" + i);
                Assert.That(slot.gameObject.activeInHierarchy, Is.True, "城市格位必须实际激活");
                Assert.That(slot.transform.IsChildOf(artwork.transform), Is.True,
                    "显示格位必须属于最终城市原图");
                var rect = ScreenRect(slot.transform as RectTransform);
                Assert.That(rect.width, Is.GreaterThan(0f)); Assert.That(rect.height, Is.GreaterThan(0f));
                Contains(image, rect, "原格位必须位于最终图像内");
                var pointer = new PointerEventData(EventSystem.current) { position = rect.center };
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(pointer, hits);
                Assert.That(hits, Is.Not.Empty, "城市格位中心必须能接收真实输入 #" + i);
                Assert.That(hits[0].gameObject.transform.IsChildOf(slot.transform), Is.True,
                    "城市格位被其他显示层挡住 #" + i + "，首个命中：" + hits[0].gameObject.name +
                    "; slot=" + rect + "; artwork=" + image +
                    "; cull=" + slot.GetComponent<Graphic>().canvasRenderer.cull +
                    "; depth=" + slot.GetComponent<Graphic>().depth + "; ancestors=" + DescribeInputAncestors(slot.transform));
                pointer.pointerCurrentRaycast = hits[0];
                Assert.That((int)resolve.Invoke(null, new object[] { pointer, artwork.rectTransform }), Is.EqualTo(i),
                    "显示格位与输入解析必须一致");
            }
        }
        private static string DescribeInputAncestors(Transform target)
        {
            var items = new List<string>();
            for (var current = target; current != null; current = current.parent)
            {
                var item = current.name + " active=" + current.gameObject.activeInHierarchy;
                var graphic = current.GetComponent<Graphic>();
                if (graphic != null) item += " graphic=" + graphic.enabled + "/raycast=" + graphic.raycastTarget + "/depth=" + graphic.depth;
                var canvas = current.GetComponent<Canvas>();
                if (canvas != null) item += " canvas=" + canvas.enabled + "/" + canvas.renderMode + "/order=" + canvas.sortingOrder;
                var group = current.GetComponent<CanvasGroup>();
                if (group != null) item += " group=" + group.alpha + "/blocks=" + group.blocksRaycasts;
                items.Add(item);
            }
            return string.Join(" > ", items);
        }
        private static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var canvas = rect.GetComponentInParent<Canvas>();
            var root = canvas == null ? null : canvas.rootCanvas;
            var camera = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var corner in corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                min = Vector2.Min(min, point); max = Vector2.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
        private static IEnumerator ClearLaunchContext()
        {
            var type = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var context = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null) as Component;
            if (context == null) yield break;
            UnityEngine.Object.Destroy(context.gameObject);
            yield return null;
        }
        private static object Field(object owner, string name) => owner.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    }
}
