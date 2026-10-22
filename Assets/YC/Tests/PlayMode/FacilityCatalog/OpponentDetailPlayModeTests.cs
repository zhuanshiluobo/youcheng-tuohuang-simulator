using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public IEnumerator BothScenes_SupplyViewportTracksRowsAndFramesWrapWithoutReparenting()
        {
            foreach (var sceneName in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                for (var i = 0; i < 8; i++) yield return null;
                var modules = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayMainModules, Assembly-CSharp", true)) as Component;
                var frame = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true)) as Component;
                var state = new GameState();
                for (var i = 0; i < 4; i++) state.Players.Add(new PlayerState { PlayerId = i + 1, Color = (PlayerColor)i });
                modules.GetType().GetMethod("Render").Invoke(modules, new object[] { state, null, 1, null });
                var hosts = (RectTransform[])Field(modules, "localDetailContents");
                var local = (GameObject)Field(modules, "localDetailPanel");
                var scroll = local.GetComponent<ScrollRect>();
                var localToggle = (Button)Field(modules, "localDetailToggle");
                var main = (RectTransform)Field(frame, "mainRegions");
                var parent = local.transform.parent;
                var frameType = Type.GetType("YC.Presentation.UiModuleDetailFrame, Assembly-CSharp", true);
                var owner = localToggle.transform.parent.parent;
                var enclosingFrame = (RectTransform)Field(owner.GetComponent(frameType), "frame");
                var originalMin = enclosingFrame.offsetMin;
                var originalMax = enclosingFrame.offsetMax;
                var firstLeft = AddDetailRow(hosts[0], "单行左栏样例");
                var firstRight = AddDetailRow(hosts[1], "单行右栏样例");
                yield return null;
                localToggle.onClick.Invoke();
                for (var i = 0; i < 3; i++) yield return null;
                Assert.That(local.transform.parent, Is.SameAs(parent), "视觉包裹不能把详情改成模块的子物体");
                var padding = hosts[0].GetComponent<VerticalLayoutGroup>().padding.vertical;
                var gap = hosts[0].GetComponent<VerticalLayoutGroup>().spacing;
                Assert.That(scroll.viewport.rect.height - padding, Is.EqualTo(76f).Within(.1f),
                    "左右各一条视为一行，完整显示实际条目高度");
                Contains(ScreenRect(scroll.viewport), ScreenRect(firstLeft), "单行左栏完整可见");
                Contains(ScreenRect(scroll.viewport), ScreenRect(firstRight), "单行右栏完整可见");
                AssertCenteredDetailRow(scroll.viewport, firstLeft);
                AssertCenteredDetailRow(scroll.viewport, firstRight);
                AssertDetailFrames(modules);
                var secondRight = AddDetailRow(hosts[1], "较长右栏的第二条");
                for (var i = 0; i < 3; i++) yield return null;
                Assert.That(scroll.viewport.rect.height - padding, Is.EqualTo(76f * 1.2f + gap).Within(.1f),
                    "任意一栏大于一行时，显示一行加下一行的两成");
                AssertDetailFrames(modules);
                firstRight.GetComponent<LayoutElement>().preferredHeight = 114f;
                for (var i = 0; i < 3; i++) yield return null;
                Assert.That(scroll.viewport.rect.height - padding, Is.EqualTo(114f + gap + 76f * .2f).Within(.1f),
                    "条目数量不变而高度改变时，也必须重新测量视口");
                AssertMapCameraInsideCurrentRegion(frame, ScreenRect(local.transform as RectTransform));
                secondRight.gameObject.SetActive(false);
                for (var i = 0; i < 3; i++) yield return null;
                Assert.That(scroll.viewport.rect.height - padding, Is.EqualTo(114f).Within(.1f));
                AssertCenteredDetailRow(scroll.viewport, firstLeft);
                AssertCenteredDetailRow(scroll.viewport, firstRight);
                localToggle.onClick.Invoke();
                for (var i = 0; i < 2; i++) yield return null;
                Assert.That(enclosingFrame.offsetMin, Is.EqualTo(originalMin));
                Assert.That(enclosingFrame.offsetMax, Is.EqualTo(originalMax), "收起恢复预制体原来的边框留白");
                Layout(main);
                AssertMapCameraInsideCurrentRegion(frame, null);
            }
        }

        [UnityTest]
        public IEnumerator BothScenes_ActualEffectRowsRenderInEveryDetailPanel()
        {
            foreach (var sceneName in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                for (var i = 0; i < 8; i++) yield return null;
                var modules = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayMainModules, Assembly-CSharp", true)) as Component;
                var frame = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true)) as Component;
                var registry = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true)) as Component;
                var state = new GameState();
                for (var i = 0; i < 4; i++)
                    state.Players.Add(new PlayerState { PlayerId = i + 1, Color = (PlayerColor)i,
                        InfluenceSupply = 30 - i * 6, Score = 12 + i,
                        Resources = new ResourceSet { Originium = 10, OriginiumShard = 12,
                            Iron = 8, PureOriginium = 6, GoldVoucher = 5 } });
                var visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
                modules.GetType().GetMethod("Render").Invoke(modules, new object[] { state, visible, 1, null });
                var main = (RectTransform)Field(frame, "mainRegions");
                var localHosts = (RectTransform[])Field(modules, "localDetailContents");
                var localToggle = (Button)Field(modules, "localDetailToggle");
                var toggles = (Button[])Field(modules, "opponentDetailToggles");
                var panels = (GameObject[])Field(modules, "opponentDetailPanels");
                var rows = new List<Component>();
                var bodyClicks = 0;
                for (var column = 0; column < 2; column++)
                {
                    rows.Add(AddEffectRow(registry, localHosts[column], "样例：" + (column == 0 ? "资源储备" : "共同补给"),
                        "回合结束时获得 2 金券。", column == 0 ? 8 : 12, column == 0 ? "Unused" : "Used", () => bodyClicks++));
                    rows.Add(AddEffectRow(registry, localHosts[column], "样例：持续效果", "相邻设施触发后获得资源；这是用于检查窄栏换行和标记显示的假想条目。",
                        8, "Unused", () => bodyClicks++));
                }
                for (var i = 0; i < 3; i++)
                {
                    var host = (RectTransform)modules.GetType().GetMethod("GetOpponentDetailHost")
                        .Invoke(modules, new object[] { i + 2 });
                    rows.Add(AddEffectRow(registry, host, "样例：玩家 " + (i + 2) + " 的补给", "回合结束时获得 1 分。",
                        new[] { 4, 1, 2 }[i], "Unused", () => bodyClicks++));
                    rows.Add(AddEffectRow(registry, host, "样例：已用效果", "公开持续效果的说明与使用状态。",
                        new[] { 4, 1, 2 }[i], "Used", () => bodyClicks++));
                }
                yield return null;
                modules.GetType().GetMethod("RefreshDetailAvailability").Invoke(modules, null);
                localToggle.onClick.Invoke();
                foreach (var size in new[] { new Vector2Int(1920,1080), new Vector2Int(900,600) })
                {
#if UNITY_EDITOR
                    SetGameViewSize(size);
#endif
                    for (var i = 0; i < 12; i++) yield return null;
                    for (var opponent = 0; opponent < 3; opponent++)
                    {
                        if (!panels[opponent].activeSelf) toggles[opponent].onClick.Invoke();
                        Layout(main);
                        yield return null;
                        Assert.That(((GameObject)Field(modules, "localDetailPanel")).activeSelf, Is.True);
                        Assert.That(panels[opponent].activeSelf, Is.True);
                        AssertDetailFrames(modules);
                        AssertMapCameraInsideCurrentRegion(frame,
                            ScreenRect((RectTransform)((GameObject)Field(modules, "localDetailPanel")).transform));
                        foreach (var row in rows)
                        {
                            Assert.That((string)row.GetType().GetProperty("ItemId").GetValue(row), Is.Not.Empty);
                            Assert.That(((Button)Field(row, "statusButton")).interactable, Is.False,
                                "公开持续条目的状态图标不得执行行动");
                            if (!row.gameObject.activeInHierarchy) continue;
                            var heading = (Text)Field(row, "title");
                            var description = (Text)Field(row, "description");
                            Assert.That(heading.rectTransform.rect.width, Is.GreaterThan(0));
                            Assert.That(description.rectTransform.rect.height, Is.GreaterThan(0));
                        }
                        yield return CaptureDetails(sceneName + "-effect-details-player" + (opponent + 2) + "-" + size.x + "x" + size.y);
                        // 单条目预览和长列表滚动分别拍摄；全部来自正式条目模板。
                        for (var second = 1; second < rows.Count; second += 2) rows[second].gameObject.SetActive(false);
                        yield return null; Layout(main);
                        AssertDetailFrames(modules);
                        var singleScroll = ((GameObject)Field(modules, "localDetailPanel")).GetComponent<ScrollRect>();
                        singleScroll.verticalNormalizedPosition = 1f;
                        Canvas.ForceUpdateCanvases();
                        Contains(ScreenRect(singleScroll.viewport), ScreenRect(rows[0].transform as RectTransform), "单行左栏正式条目完整可见");
                        Contains(ScreenRect(singleScroll.viewport), ScreenRect(rows[2].transform as RectTransform), "单行右栏正式条目完整可见");
                        AssertCenteredDetailRow(singleScroll.viewport, rows[0].transform as RectTransform);
                        AssertCenteredDetailRow(singleScroll.viewport, rows[2].transform as RectTransform);
                        var singleOpponentScroll = panels[opponent].GetComponent<ScrollRect>();
                        AssertCenteredDetailRow(singleOpponentScroll.viewport, rows[4 + opponent * 2].transform as RectTransform);
                        yield return CaptureDetails(sceneName + "-single-effect-player" + (opponent + 2) + "-" + size.x + "x" + size.y);
                        for (var second = 1; second < rows.Count; second += 2) rows[second].gameObject.SetActive(true);
                        yield return null; Layout(main);
                        if (opponent == 0)
                        {
                            var localScroll = ((GameObject)Field(modules, "localDetailPanel")).GetComponent<ScrollRect>();
                            localScroll.verticalNormalizedPosition = 0f;
                            Canvas.ForceUpdateCanvases();
                            foreach (var host in localHosts)
                                Contains(ScreenRect(localScroll.viewport), ScreenRect((RectTransform)host.GetChild(host.childCount - 1)),
                                    "真实条目的长说明可在共同滚动视口内完整到达");
                            yield return CaptureDetails(sceneName + "-effect-details-scrolled-" + size.x + "x" + size.y);
                            localScroll.verticalNormalizedPosition = 1f;
                        }
                    }
                }
                var body = (Button)Field(rows[0], "bodyButton");
                body.onClick.Invoke();
                Assert.That(bodyClicks, Is.EqualTo(1), "正式条目正文仍可独立打开明细");
            }
#if UNITY_EDITOR
            SetGameViewSize(new Vector2Int(1920,1080));
#endif
        }

        private static Component AddEffectRow(Component registry, RectTransform host, string title,
            string description, int mask, string status, Action showBody)
        {
            var row = (Component)registry.GetType().GetMethod("InstantiateEffectRow").Invoke(registry, new object[] { host });
            row.gameObject.name = "Sample Effect Row";
            var rowType = row.GetType();
            var markerType = rowType.GetNestedType("Marker");
            var markers = Array.CreateInstance(markerType, 1);
            var marker = Activator.CreateInstance(markerType);
            markerType.GetField("Mask").SetValue(marker, mask);
            markerType.GetField("Count").SetValue(marker, 12);
            markerType.GetField("Description").SetValue(marker, mask == 12 ? "红蓝合作标记" : "玩家标记");
            markers.SetValue(marker, 0);
            var bind = Array.Find(rowType.GetMethods(), m => m.Name == "Bind" && m.GetParameters().Length == 13);
            bind.Invoke(row, new object[] { Guid.NewGuid().ToString(), "visual-fixture", 1,
                Enum.Parse(rowType.GetNestedType("Mode"), "PersistentDetail"),
                Enum.Parse(rowType.GetNestedType("Status"), status), false, false, title, description, markers,
                new Func<string,int,bool>((id,revision) => true), new Action<string>(id => showBody()), null });
            row.gameObject.SetActive(true);
            return row;
        }

        [UnityTest]
        public IEnumerator BothScenes_DetailScrollAndCityStayInsideLiveContentAcrossResizeAndRestore()
        {
            foreach (var sceneName in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
#if UNITY_EDITOR
                SetGameViewSize(new Vector2Int(1920,1080));
#endif
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
                Assert.That(toggles[0].gameObject.activeSelf, Is.False, "没有实际内容时不提供空明细入口");
                toggles[0].onClick.Invoke();
                Assert.That(panels[0].activeSelf, Is.False);
                var host = (RectTransform)modules.GetType().GetMethod("GetOpponentDetailHost").Invoke(modules, new object[] { 2 });
                RectTransform lastRow = null;
                for (var i = 0; i < 20; i++)
                {
                    lastRow = AddDetailRow(host, "测试公开条目 " + i);
                }
                modules.GetType().GetMethod("SetOpponentDetailCount").Invoke(modules, new object[] { 2, 20 });
                var secondHost = (RectTransform)modules.GetType().GetMethod("GetOpponentDetailHost").Invoke(modules, new object[] { 3 });
                AddDetailRow(secondHost, "另一位玩家的公开详情");
                modules.GetType().GetMethod("RefreshDetailAvailability").Invoke(modules, null);
                toggles[0].onClick.Invoke();
                Layout(main);
                Assert.That(panels[0].activeSelf, Is.True);
                Assert.That(panels[1].activeSelf, Is.False);
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

        [UnityTest]
        public IEnumerator BothScenes_FourDetailButtonsFollowRealContentAndLocalDetailsOpenAboveInTwoColumns()
        {
            foreach (var sceneName in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                for (var i = 0; i < 8; i++) yield return null;
                var modules = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayMainModules, Assembly-CSharp", true)) as Component;
                var frame = UnityEngine.Object.FindObjectOfType(
                    Type.GetType("YC.Presentation.GameplayHudFrame, Assembly-CSharp", true)) as Component;
                var state = new GameState();
                var colors = new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green, PlayerColor.Yellow };
                for (var i = 0; i < 4; i++)
                    state.Players.Add(new PlayerState { PlayerId = i + 1, Name = "玩家 " + (i + 1),
                        Color = colors[i], InfluenceSupply = 30 - i * 6, Score = 12 + i,
                        Resources = new ResourceSet { Originium = 101, OriginiumShard = 202, Iron = 303,
                            PureOriginium = 404, GoldVoucher = 505 } });
                Action render = () => {
                    var visible = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
                    modules.GetType().GetMethod("Render").Invoke(modules, new object[] { state, visible, 1, null });
                };
                render();
                var toggles = (Button[])Field(modules, "opponentDetailToggles");
                var panels = (GameObject[])Field(modules, "opponentDetailPanels");
                var localToggle = (Button)Field(modules, "localDetailToggle");
                var localPanel = (GameObject)Field(modules, "localDetailPanel");
                var hosts = (RectTransform[])Field(modules, "localDetailContents");
                var main = (RectTransform)Field(frame, "mainRegions");
                var localMarker = (Component)Field(modules, "localSupplyMarker");
                var metrics = (RectTransform)localMarker.transform.parent.parent;
                var summary = (RectTransform)localToggle.transform.parent;
                Assert.That(metrics.childCount, Is.EqualTo(7));
                Assert.That(metrics.GetComponent<HorizontalLayoutGroup>(), Is.Not.Null);
                Assert.That(localToggle.transform.parent, Is.SameAs(metrics.parent),
                    "详情按钮与七项数据是外层布局中的独立同级项。");
                Assert.That(localPanel.GetComponent<ScrollRect>(), Is.Not.Null);
                Assert.That(hosts.Length, Is.EqualTo(2));
                Assert.That(hosts[0], Is.Not.SameAs(hosts[1]));
                Assert.That(hosts[0].parent, Is.SameAs(hosts[1].parent));
                Assert.That(hosts[0].parent.GetComponent<HorizontalLayoutGroup>(), Is.Not.Null);
                Assert.That(localPanel.GetComponentsInChildren<Scrollbar>(true), Is.Empty);
                foreach (var panel in panels) Assert.That(panel.GetComponentsInChildren<Scrollbar>(true), Is.Empty);
                foreach (var toggle in toggles) Assert.That(toggle.gameObject.activeSelf, Is.False);
                Assert.That(localToggle.gameObject.activeSelf, Is.False);
                Assert.That(localPanel.activeSelf, Is.False);
                modules.GetType().GetMethod("SetOpponentDetailCount").Invoke(modules, new object[] { 2, 99 });
                Assert.That(toggles[0].gameObject.activeSelf, Is.False, "只有上报数量不等于已填充详情");
                var blank = AddDetailRow(hosts[0], "");
                yield return null;
                Assert.That(localToggle.gameObject.activeSelf, Is.False, "空文案和空容器不显示按钮");
                blank.gameObject.SetActive(false);
                Layout(main);
                var fullWidth = metrics.rect.width;
                RectTransform lastLeft = null, lastRight = null;
                for (var i = 0; i < 12; i++)
                {
                    lastLeft = AddDetailRow(hosts[0], "公开详情条目 " + (i + 1));
                    lastRight = AddDetailRow(hosts[1], "玩家供应堆详情 " + (i + 1));
                }
                for (var i = 0; i < 3; i++)
                {
                    var host = (RectTransform)modules.GetType().GetMethod("GetOpponentDetailHost")
                        .Invoke(modules, new object[] { i + 2 });
                    for (var row = 0; row < 4; row++)
                        AddDetailRow(host, "玩家 " + (i + 2) + " 的公开详情 " + (row + 1));
                }
                yield return null; // 无数量通知也必须能观察到真实填充。
                Layout(main);
                foreach (var toggle in toggles) Assert.That(toggle.gameObject.activeSelf, Is.True);
                Assert.That(localToggle.gameObject.activeSelf, Is.True);
                foreach (var toggle in toggles)
                {
                    AssertMatchingDetailButton(localToggle, toggle);
                    Assert.That(localToggle.GetComponent<Image>().sprite, Is.SameAs(toggle.GetComponent<Image>().sprite),
                        "相同收起状态使用同款按钮底图");
                }
                var commonBackdrop = localPanel.GetComponent<ScrollRect>().viewport.GetComponent<Image>();
                Assert.That(commonBackdrop, Is.Not.Null, "双栏展开区应有与对手详情区相同的整体底板");
                AssertMatchingDetailImage(commonBackdrop, panels[0].GetComponent<ScrollRect>().viewport.GetComponent<Image>());
                Assert.That(metrics.rect.width, Is.LessThan(fullWidth), "仅可见按钮占用外层行宽度");
                foreach (var size in new[] { new Vector2Int(1920,1080), new Vector2Int(1920,1200),
                    new Vector2Int(2560,1080), new Vector2Int(1280,720), new Vector2Int(900,600) })
                {
#if UNITY_EDITOR
                    SetGameViewSize(size);
#endif
                    for (var i = 0; i < 12; i++) yield return null;
                    if (panels[0].activeSelf) toggles[0].onClick.Invoke();
                    if (!localPanel.activeSelf) localToggle.onClick.Invoke();
                    Layout(main);
                    foreach (var toggle in toggles) AssertMatchingDetailButton(localToggle, toggle);
                    Assert.That(localPanel.activeSelf, Is.True);
                    foreach (var panel in panels) Assert.That(panel.activeSelf, Is.False);
                    var localRect = ScreenRect((RectTransform)localPanel.transform);
                    AssertMapCameraInsideCurrentRegion(frame, localRect);
                    Assert.That(localRect.yMin, Is.GreaterThanOrEqualTo(ScreenRect(summary).yMax - .5f),
                        "本机详情区必须在玩家供应堆上方。");
                    Contains(ScreenRect((RectTransform)localPanel.transform.parent), localRect, sceneName + " 双栏详情/中列");
                    var scrolls = localPanel.GetComponentsInChildren<ScrollRect>();
                    Assert.That(scrolls.Length, Is.EqualTo(1), "双栏共用一个滚动视口");
                    var sharedScroll = scrolls[0];
                    var first = ScreenRect(hosts[0]);
                    var second = ScreenRect(hosts[1]);
                    Assert.That(second.xMin, Is.GreaterThanOrEqualTo(first.xMax - .5f), "两栏不能重叠");
                    Assert.That(first.xMin, Is.GreaterThanOrEqualTo(localRect.xMin));
                    Assert.That(second.xMax, Is.LessThanOrEqualTo(localRect.xMax));
                    Assert.That(second.width, Is.EqualTo(first.width).Within(.5f));
                    Assert.That(sharedScroll.content.rect.height, Is.GreaterThan(sharedScroll.viewport.rect.height));
                    sharedScroll.verticalNormalizedPosition = 0f;
                    Canvas.ForceUpdateCanvases();
                    Contains(ScreenRect(sharedScroll.viewport), ScreenRect(lastLeft), "左栏末项可滚动到达");
                    Contains(ScreenRect(sharedScroll.viewport), ScreenRect(lastRight), "右栏末项可滚动到达");
                    sharedScroll.verticalNormalizedPosition = 1f;
                    Canvas.ForceUpdateCanvases();
                    var leftBefore = ScreenRect(lastLeft).y;
                    var rightBefore = ScreenRect(lastRight).y;
                    var wheel = new PointerEventData(EventSystem.current) { scrollDelta = new Vector2(0f,-6f) };
                    ExecuteEvents.Execute(localPanel, wheel, ExecuteEvents.scrollHandler);
                    Canvas.ForceUpdateCanvases();
                    var leftMove = ScreenRect(lastLeft).y - leftBefore;
                    var rightMove = ScreenRect(lastRight).y - rightBefore;
                    Assert.That(Mathf.Abs(leftMove), Is.GreaterThan(.01f), "滚轮实际移动双栏内容");
                    Assert.That(rightMove, Is.EqualTo(leftMove).Within(.01f), "双栏始终以相同距离同步滚动");
                    sharedScroll.verticalNormalizedPosition = 1f;
                    foreach (RectTransform metric in metrics)
                        Contains(ScreenRect(metrics), ScreenRect(metric), "七项数据不能溢出");
                    Contains(ScreenRect(summary), ScreenRect((RectTransform)localToggle.transform), "独立详情按钮在供应堆行内");
                    toggles[0].onClick.Invoke(); Layout(main);
                    Assert.That(localPanel.activeSelf, Is.True); Assert.That(panels[0].activeSelf, Is.True,
                        "本机和一名对手的详情必须能同时展开");
                    Assert.That(panels[1].activeSelf || panels[2].activeSelf, Is.False);
                    AssertMapCameraInsideCurrentRegion(frame, ScreenRect((RectTransform)localPanel.transform));
                    var city = (RectTransform)Field(modules, "cityRegion");
                    Contains(ScreenRect((RectTransform)city.parent), ScreenRect(city), "双区域展开后的城市仍在玩家列内");
                    var opponentScroll = panels[0].GetComponent<ScrollRect>();
                    Assert.That(opponentScroll.viewport.rect.height, Is.GreaterThan(0f));
                    Assert.That(opponentScroll.content.rect.height, Is.GreaterThan(opponentScroll.viewport.rect.height));
                    yield return CaptureDetails(sceneName + "-combined-details-" + size.x + "x" + size.y);
                    var expandedOpponentFace = toggles[0].GetComponent<Image>().sprite;
                    var expandedOpponentArrow = toggles[0].transform.Find("Toggle Icon").GetComponent<Image>().sprite;
                    localToggle.onClick.Invoke(); Layout(main);
                    Assert.That(localPanel.activeSelf, Is.False); Assert.That(panels[0].activeSelf, Is.True,
                        "收起本机详情不能关闭对手详情");
                    AssertMapCameraInsideCurrentRegion(frame, null);
                    localToggle.onClick.Invoke(); Layout(main);
                    Assert.That(localPanel.activeSelf, Is.True); Assert.That(panels[0].activeSelf, Is.True);
                    Assert.That(localToggle.GetComponent<Image>().sprite, Is.SameAs(expandedOpponentFace),
                        "相同展开状态使用同款按钮底图");
                    Assert.That(localToggle.transform.Find("Toggle Icon").GetComponent<Image>().sprite,
                        Is.Not.SameAs(expandedOpponentArrow), "本机与对手的展开方向相反");
                }
                // 两栏条目数不等时，用较长一栏确定共同滚动范围。
                var synchronizedScroll = localPanel.GetComponent<ScrollRect>();
                var previousHeight = synchronizedScroll.content.rect.height;
                for (var i = 0; i < 3; i++) lastRight = AddDetailRow(hosts[1], "右栏追加的公开详情 " + (i + 1));
                yield return null; Layout(main);
                Assert.That(synchronizedScroll.content.rect.height, Is.GreaterThan(previousHeight));
                synchronizedScroll.verticalNormalizedPosition = 0f; Canvas.ForceUpdateCanvases();
                Contains(ScreenRect(synchronizedScroll.viewport), ScreenRect(lastRight), "较长一栏的末项可滚动到达");
                synchronizedScroll.verticalNormalizedPosition = 1f; Canvas.ForceUpdateCanvases();
                var drag = new PointerEventData(EventSystem.current) {
                    button = PointerEventData.InputButton.Left,
                    position = ScreenRect(synchronizedScroll.viewport).center
                };
                ExecuteEvents.Execute(localPanel, drag, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(localPanel, drag, ExecuteEvents.beginDragHandler);
                var leftBeforeDrag = ScreenRect(lastLeft).y;
                var rightBeforeDrag = ScreenRect(lastRight).y;
                drag.position += new Vector2(0f,25f);
                ExecuteEvents.Execute(localPanel, drag, ExecuteEvents.dragHandler);
                Canvas.ForceUpdateCanvases();
                Assert.That(Mathf.Abs(ScreenRect(lastLeft).y-leftBeforeDrag), Is.GreaterThan(.01f));
                Assert.That(ScreenRect(lastRight).y-rightBeforeDrag,
                    Is.EqualTo(ScreenRect(lastLeft).y-leftBeforeDrag).Within(.01f), "拖动也必须同时移动两栏");
                ExecuteEvents.Execute(localPanel, drag, ExecuteEvents.endDragHandler);
                synchronizedScroll.StopMovement();
                foreach (var host in hosts)
                    foreach (Transform child in host) child.gameObject.SetActive(false);
                yield return null;
                Layout(main);
                Assert.That(localToggle.gameObject.activeSelf, Is.False);
                Assert.That(localPanel.activeSelf, Is.False, "展开内容清空后必须自动收起");
                foreach (var playerId in new[] { 2, 3, 4 })
                {
                    var host = (RectTransform)modules.GetType().GetMethod("GetOpponentDetailHost").Invoke(modules, new object[] { playerId });
                    toggles[playerId - 2].onClick.Invoke();
                    foreach (Transform child in host) child.gameObject.SetActive(false);
                    yield return null;
                    Assert.That(toggles[playerId - 2].gameObject.activeSelf, Is.False);
                    Assert.That(panels[playerId - 2].activeSelf, Is.False);
                }
                Layout(main);
                var recoveredWidth = metrics.rect.width;
                AddDetailRow(hosts[1], "实际详情再次出现");
                yield return null; Layout(main);
                Assert.That(localToggle.gameObject.activeSelf, Is.True);
                Assert.That(metrics.rect.width, Is.LessThan(recoveredWidth));
                // 身份切换后不能展示上一位玩家的供应堆详情。
                modules.GetType().GetMethod("Render").Invoke(modules, new object[] { state, null, 2, null });
                yield return null;
                Assert.That(localToggle.gameObject.activeSelf, Is.False);
                Assert.That(localPanel.activeSelf, Is.False);
            }
#if UNITY_EDITOR
            SetGameViewSize(new Vector2Int(1920,1080));
#endif
        }

        private static void AssertMapCameraInsideCurrentRegion(Component frame, Rect? detail)
        {
            var display = UnityEngine.Object.FindObjectOfType(
                Type.GetType("YC.Presentation.MapDisplayController, Assembly-CSharp", true)) as Component;
            var camera = (Camera)Field(display, "targetCamera");
            var mapRegion = (RectTransform)Field(frame, "mapRegion");
            var opening = (RectTransform)Field(frame, "mapVisibleBounds");
            Contains(ScreenRect(mapRegion), camera.pixelRect, "展开同帧的实际地图相机视口必须缩入地图区域");
            var visible = ScreenRect(opening);
            Assert.That(camera.pixelRect.xMin, Is.EqualTo(visible.xMin).Within(.5f));
            Assert.That(camera.pixelRect.xMax, Is.EqualTo(visible.xMax).Within(.5f));
            Assert.That(camera.pixelRect.yMin, Is.EqualTo(visible.yMin).Within(.5f));
            Assert.That(camera.pixelRect.yMax, Is.EqualTo(visible.yMax).Within(.5f));
            if (detail.HasValue)
                Assert.That(camera.pixelRect.yMin, Is.GreaterThanOrEqualTo(detail.Value.yMax - .5f),
                    "地图实际显示不能伸入供应堆明细区，不能靠底板遮挡");
        }

        private static void AssertMatchingDetailButton(Button local, Button opponent)
        {
            Assert.That(local.transition, Is.EqualTo(opponent.transition));
            Assert.That(local.colors, Is.EqualTo(opponent.colors));
            Assert.That(local.spriteState, Is.EqualTo(opponent.spriteState), "悬停、按下与选择状态使用同套素材");
            var localRect = (RectTransform)local.transform;
            var otherRect = (RectTransform)opponent.transform;
            Assert.That(localRect.rect.width, Is.EqualTo(otherRect.rect.width).Within(.01f));
            Assert.That(localRect.rect.height, Is.EqualTo(otherRect.rect.height).Within(.01f),
                "供应堆布局不能额外拉伸明细按钮");
            var localText = local.GetComponentInChildren<Text>();
            var otherText = opponent.GetComponentInChildren<Text>();
            Assert.That(localText.font, Is.SameAs(otherText.font));
            Assert.That(localText.fontSize, Is.EqualTo(otherText.fontSize));
            Assert.That(localText.fontStyle, Is.EqualTo(otherText.fontStyle));
            Assert.That(localText.color, Is.EqualTo(otherText.color));
            var localFace = local.GetComponent<Image>();
            var otherFace = opponent.GetComponent<Image>();
            Assert.That(localFace.type, Is.EqualTo(otherFace.type));
            Assert.That(localFace.color, Is.EqualTo(otherFace.color));
            Assert.That(localFace.material, Is.SameAs(otherFace.material));
        }

        private static void AssertMatchingDetailImage(Image local, Image opponent)
        {
            Assert.That(local.sprite, Is.SameAs(opponent.sprite));
            Assert.That(local.type, Is.EqualTo(opponent.type));
            Assert.That(local.color, Is.EqualTo(opponent.color));
            Assert.That(local.material, Is.SameAs(opponent.material));
            Assert.That(local.pixelsPerUnitMultiplier, Is.EqualTo(opponent.pixelsPerUnitMultiplier));
        }

        private static void AssertDetailFrames(Component modules)
        {
            var type = Type.GetType("YC.Presentation.UiModuleDetailFrame, Assembly-CSharp", true);
            var seats = (RectTransform[])Field(modules, "opponentSeats");
            var panels = (GameObject[])Field(modules, "opponentDetailPanels");
            for (var i = 0; i < seats.Length; i++) AssertDetailFrame(seats[i], panels[i], type);
            var toggle = (Button)Field(modules, "localDetailToggle");
            AssertDetailFrame(toggle.transform.parent.parent as RectTransform,
                (GameObject)Field(modules, "localDetailPanel"), type);
        }

        private static void AssertCenteredDetailRow(RectTransform backdrop, RectTransform row)
        {
            var background = ScreenRect(backdrop);
            var item = ScreenRect(row);
            var top = background.yMax - item.yMax;
            var bottom = item.yMin - background.yMin;
            Assert.That(top, Is.GreaterThan(0f), "条目边框与详情整体底板之间仍有上内边距");
            Assert.That(bottom, Is.GreaterThan(0f), "条目边框与详情整体底板之间仍有下内边距");
            Assert.That(top, Is.EqualTo(bottom).Within(.1f), "单条目在详情整体底板中垂直居中");
        }

        private static void AssertDetailFrame(RectTransform owner, GameObject panel, Type type)
        {
            var binding = owner.GetComponent(type);
            Assert.That(binding, Is.Not.Null, "正式场景实际使用的模块需配置明细边框");
            var frame = (RectTransform)Field(binding, "frame");
            Assert.That(frame.GetComponent<Image>().raycastTarget, Is.False, "扩展装饰边框不能拦截地图或条目输入");
            Assert.That(panel.transform.parent, Is.SameAs(owner.parent), "条目区仍是原生布局中的同级项");
            Contains(ScreenRect(frame), ScreenRect(owner), "Module Frame 包住原模块");
            if (panel.activeInHierarchy)
            {
                Contains(ScreenRect(frame), ScreenRect(panel.transform as RectTransform), "Module Frame 视觉包住展开条目区");
                var background = (RectTransform)Field(binding, "joinedContentBackground");
                if (background == null) background = owner.Find("Module Background") as RectTransform;
                Assert.That(background, Is.Not.Null, "接合位置应使用当前模块实际显示的背景");
                var initial = ScreenRect(background);
                var detailBackground = ScreenRect(panel.GetComponent<ScrollRect>().viewport.GetComponent<Image>().rectTransform);
                var below = ScreenRect(panel.transform as RectTransform).center.y < ScreenRect(owner).center.y;
                Assert.That(below ? initial.yMin - detailBackground.yMax : detailBackground.yMin - initial.yMax,
                    Is.EqualTo(0f).Within(.05f), "展开条目整体背景与模块原有内容背景的间距必须为 0");
            }
        }

        private static RectTransform AddDetailRow(RectTransform host, string title)
        {
            var template = host.GetChild(0).gameObject;
            var row = UnityEngine.Object.Instantiate(template, host, false);
            row.name = "Detail Test Row";
            row.GetComponent<Text>().text = title;
            row.GetComponent<LayoutElement>().preferredHeight = 76f;
            row.SetActive(true);
            return row.transform as RectTransform;
        }

#if UNITY_EDITOR
        private static void SetGameViewSize(Vector2Int size)
        {
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { size });
        }
#endif
        private static IEnumerator CaptureDetails(string name)
        {
            var directory = Environment.GetEnvironmentVariable("YC_HUD_SUPPLY_CAPTURE_OUTPUT");
            if (string.IsNullOrWhiteSpace(directory)) yield break;
            var absolute = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "../", directory));
            Directory.CreateDirectory(absolute);
            var file = Path.Combine(absolute, name + ".png");
            ScreenCapture.CaptureScreenshot(file);
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(File.Exists(file), Is.True);
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
