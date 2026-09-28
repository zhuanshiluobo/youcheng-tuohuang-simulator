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

namespace YC.Tests.PlayMode
{
    public sealed class GameplayHudFramePlayModeTests
    {
        [UnityTest]
        public IEnumerator LegacyBuildSupply_CanReceiveInputAboveNewSurfaceWithoutCoveringBars()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hudType = Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true);
            var hud = UnityEngine.Object.FindObjectOfType(hudType) as Component;
            Assert.That(hud, Is.Not.Null);
            var panel = Get<object>(hud, "BuildInfoPanel");
            var view = Get<object>(panel, "View");
            var root = Get<RectTransform>(view, "Root");
            var canvas = root.GetComponent<Canvas>();
            var raycaster = root.GetComponent<GraphicRaycaster>();
            var group = root.GetComponent<CanvasGroup>();
            Assert.That(canvas, Is.Not.Null);
            Assert.That(raycaster, Is.Not.Null);
            Assert.That(group, Is.Not.Null);
            Assert.That(canvas.sortingOrder, Is.GreaterThan(100));
            Assert.That(group.alpha, Is.EqualTo(0f), "非建设状态不应显示旧建设面板");
            view.GetType().GetMethod("SetLegacyVisible").Invoke(view, new object[] { true });
            Canvas.ForceUpdateCanvases();
            var slots = Get<Array>(view, "ExternalFacilitySlots");
            var firstSlot = slots.GetValue(0);
            var slotRoot = Get<RectTransform>(firstSlot, "Root");
            var position = RectTransformUtility.WorldToScreenPoint(raycaster.eventCamera,
                slotRoot.TransformPoint(slotRoot.rect.center));
            Assert.That(position.x, Is.InRange(0f, Screen.width), "设施供应槽不在屏幕内");
            Assert.That(position.y, Is.InRange(0f, Screen.height), "设施供应槽不在屏幕内");
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, hits);
            Assert.That(hits.Exists(hit => hit.gameObject.transform.IsChildOf(slotRoot)), Is.True,
                "旧设施供应槽无法接收输入");
            Assert.That(slots.Length, Is.EqualTo(6), "正式设施供应必须保留原六槽引用");
            for (var i = 0; i < slots.Length; i++)
            {
                var supplySlot = slots.GetValue(i);
                var supplyRoot = Get<RectTransform>(supplySlot, "Root");
                var supplyButton = Get<Button>(supplySlot, "Button");
                Assert.That(supplyButton, Is.Not.Null);
                Assert.That(supplyRoot.gameObject.activeInHierarchy, Is.True);
                Assert.That(supplyRoot.rect.width, Is.GreaterThan(0f));
                Assert.That(supplyRoot.rect.height, Is.GreaterThan(0f));
                var supplyPoint = RectTransformUtility.WorldToScreenPoint(raycaster.eventCamera,
                    supplyRoot.TransformPoint(supplyRoot.rect.center));
                hits.Clear();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = supplyPoint }, hits);
                Assert.That(hits, Is.Not.Empty, "设施供应槽没有射线命中 #" + i);
                Assert.That(hits[0].gameObject.transform.IsChildOf(supplyRoot), Is.True,
                    "设施供应槽被布局或遮罩挡住 #" + i + "，首个命中：" + hits[0].gameObject.name +
                    "; screen=" + Screen.width + "x" + Screen.height + "; slotPoint=" + supplyPoint +
                    "; slotWorld=" + supplyRoot.position + "; scale=" + supplyRoot.lossyScale +
                    "; rootRect=" + root.rect + "; canvasMode=" + canvas.renderMode +
                    "; eventCamera=" + (raycaster.eventCamera == null ? "null" : raycaster.eventCamera.name));
            }
            var frame = Get<Component>(hud, "Frame");
            var settings = Get<Button>(frame, "SettingsButton");
            Assert.That(FirstHitAt(settings.transform as RectTransform), Is.SameAs(settings.gameObject),
                "建设临时层遮住顶栏");
            var end = Get<Button>(frame, "EndActionButton");
            Assert.That(FirstHitAt(end.transform as RectTransform), Is.SameAs(end.gameObject),
                "建设临时层遮住底栏");
            view.GetType().GetMethod("SetLegacyVisible").Invoke(view, new object[] { false });
        }

        [UnityTest]
        public IEnumerator ActualEffectShell_SettingsThenLog_RestoresAfterClosingLogController()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hudType = Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true);
            var settingsType = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", true);
            var hud = UnityEngine.Object.FindObjectOfType(hudType) as Component;
            var settings = UnityEngine.Object.FindObjectOfType(settingsType) as Component;
            Assert.That(hud, Is.Not.Null);
            Assert.That(settings, Is.Not.Null);
            var frame = Get<Component>(hud, "Frame");
            var frameType = frame.GetType();
            var content = Get<RectTransform>(frame, "ContentRect");
            frameType.GetMethod("SetRequest").Invoke(frame, new object[] { "hud-actual-effect", 10 });
            var registry = Get<object>(hud, "DialogRegistry");
            var shell = registry.GetType().GetMethod("InstantiateEffectDialogShell")
                .Invoke(registry, new object[] { content, true, true, false }) as Component;
            Assert.That(shell, Is.Not.Null, "实际 effect 弹窗源未实例化");
            Assert.That(shell.gameObject.activeInHierarchy, Is.True);
            var panel = Get<RectTransform>(shell, "Panel");
            shell.GetType().GetMethod("PrepareForUse").Invoke(shell, new object[]
            {
                "Effect Probe", "Effect Panel", panel.sizeDelta, panel.anchoredPosition, true
            });
            var originalPosition = panel.anchoredPosition;
            var dragEvent = new PointerEventData(EventSystem.current) { delta = new Vector2(120f, 80f) };
            ExecuteEvents.Execute(panel.gameObject, dragEvent, ExecuteEvents.dragHandler);
            Assert.That(panel.anchoredPosition, Is.EqualTo(originalPosition), "拖动弹窗标题或边缘改变了位置");
            Canvas.ForceUpdateCanvases();
            Assert.That(FirstHitAt(Get<Button>(frame, "SettingsButton").transform as RectTransform),
                Is.SameAs(Get<Button>(frame, "SettingsButton").gameObject), "effect 弹窗遮住顶栏");
            Assert.That(FirstHitAt(Get<Button>(frame, "EndActionButton").transform as RectTransform),
                Is.SameAs(Get<Button>(frame, "EndActionButton").gameObject), "effect 弹窗遮住底栏");
            var optionContent = shell.GetType().GetMethod("ConfigureOptionScroll")
                .Invoke(shell, new object[] { "Options", 80f, 80f }) as RectTransform;
            var optionRow = shell.GetType().GetMethod("CreateOptionRow")
                .Invoke(shell, new object[] { optionContent });
            var selectedOption = Get<Button>(optionRow, "Button");
            optionRow.GetType().GetMethod("SetSelected").Invoke(optionRow, new object[] { true });
            var selectedIndicator = optionRow.GetType().GetField("selectedIndicator",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(optionRow) as Image;
            Assert.That(selectedIndicator, Is.Not.Null, "实际选项模板未配置动态选择状态");
            EventSystem.current.SetSelectedGameObject(selectedOption.gameObject);
            Assert.That(EventSystem.current.currentSelectedGameObject,
                Is.SameAs(selectedOption.gameObject), "effect 选项无法取得焦点");

            var fold = Get<Button>(frame, "FoldButton");
            Assert.That(fold.interactable, Is.True);
            fold.onClick.Invoke();
            Assert.That(shell.gameObject.activeSelf, Is.False);
            settingsType.GetMethod("Open").Invoke(settings, null);
            yield return null;
            var settingsView = settingsType.GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(settings);
            var actionLogButton = Get<Button>(settingsView, "ActionLogButton");
            actionLogButton.onClick.Invoke();
            yield return null;
            var log = settingsType.GetField("actionLogViewer", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(settings);
            Assert.That(Get<bool>(log, "IsOpen"), Is.True);
            frameType.GetMethod("SetRequest").Invoke(frame, new object[] { "hud-actual-effect", 11 });
            Assert.That(shell.gameObject.activeSelf, Is.False, "同一请求刷新后应保持收起");
            fold.onClick.Invoke();
            Assert.That(Get<bool>(log, "IsOpen"), Is.False, "资料 B 控制器应真正关闭");
            Assert.That(shell.gameObject.activeSelf, Is.True, "当前 effect 页应恢复");
            Assert.That(EventSystem.current.currentSelectedGameObject,
                Is.SameAs(selectedOption.gameObject), "恢复 effect 后未返回原选项焦点");
            Assert.That(selectedIndicator.enabled, Is.True, "收起/恢复不能重建并丢失当前选项状态");
            frameType.GetMethod("ResumeEffectPage").Invoke(frame, null);
            Assert.That(shell.gameObject.activeSelf, Is.True, "已恢复页面再次请求恢复不能被收起");
            frameType.GetMethod("ClearRequest").Invoke(frame, null);
            UnityEngine.Object.Destroy(shell.gameObject);
        }

        [UnityTest]
        public IEnumerator EffectPage_CanSuspendBrowseTwoPagesAndRestoreOnlyCurrentRequest()
        {
            yield return ClearLaunchContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hudType = Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true);
            var hud = UnityEngine.Object.FindObjectOfType(hudType) as Component;
            Assert.That(hud, Is.Not.Null);
            var frame = Get<Component>(hud, "Frame");
            var type = frame.GetType();
            var content = Get<RectTransform>(frame, "ContentRect");
            var effect = new GameObject("Effect Probe", typeof(RectTransform));
            var informationA = new GameObject("Information A", typeof(RectTransform));
            var informationB = new GameObject("Information B", typeof(RectTransform));
            effect.transform.SetParent(content, false);
            informationA.transform.SetParent(content, false);
            informationB.transform.SetParent(content, false);
            var foldButton = Get<Button>(frame, "FoldButton");
            Assert.That(foldButton.interactable, Is.False, "没有 effect 页时不能收起主界面");
            type.GetMethod("SetRequest").Invoke(frame, new object[] { "hud-request", 7 });
            Assert.That(foldButton.interactable, Is.False, "纯地图请求不能伪造可恢复页面");
            type.GetMethod("ShowPage").Invoke(frame, new object[] { effect, true });
            Assert.That(foldButton.interactable, Is.True);
            type.GetMethod("SuspendEffectForInformation").Invoke(frame, null);
            Assert.That(effect.activeSelf, Is.False);
            type.GetMethod("ShowPage").Invoke(frame, new object[] { informationA, false });
            type.GetMethod("ShowPage").Invoke(frame, new object[] { informationB, false });
            Assert.That(informationA.activeSelf, Is.False);
            Assert.That(informationB.activeSelf, Is.True);
            type.GetMethod("SetRequest").Invoke(frame, new object[] { "hud-request", 8 });
            Assert.That(effect.activeSelf, Is.False, "同一请求刷新不应自动弹回 effect 页");
            Assert.That(informationB.activeSelf, Is.True);
            foldButton.onClick.Invoke();
            Assert.That(informationB.activeSelf, Is.False);
            Assert.That(effect.activeSelf, Is.True);
            type.GetMethod("SetRequest").Invoke(frame, new object[] { "hud-next", 9 });
            Assert.That(effect.activeSelf, Is.False, "旧请求页面应失效");
            Assert.That(foldButton.interactable, Is.False, "新请求没有页面时不能恢复旧页");
            type.GetMethod("ClearRequest").Invoke(frame, null);
            UnityEngine.Object.Destroy(effect);
            UnityEngine.Object.Destroy(informationA);
            UnityEngine.Object.Destroy(informationB);
        }

        [UnityTest]
        public IEnumerator BothGameplayScenes_KeepBarsClickableAboveSettingsAndContent()
        {
            foreach (var sceneName in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ClearLaunchContext();
                var load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                Assert.That(load, Is.Not.Null);
                yield return load;
                yield return null;
                Canvas.ForceUpdateCanvases();

                var hudType = Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true);
                var settingsType = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", true);
                var hud = UnityEngine.Object.FindObjectOfType(hudType) as Component;
                var settings = UnityEngine.Object.FindObjectOfType(settingsType) as Component;
                Assert.That(hud, Is.Not.Null, sceneName);
                Assert.That(settings, Is.Not.Null, sceneName);
                var frame = Get<Component>(hud, "Frame");
                var validateArgs = new object[] { null };
                Assert.That((bool)frame.GetType().GetMethod("TryValidateConfiguration")
                    .Invoke(frame, validateArgs), Is.True, validateArgs[0] as string);
                Assert.That(UnityEngine.Object.FindObjectsOfType<EventSystem>(), Has.Length.EqualTo(1));

                var end = Get<Button>(frame, "EndActionButton");
                var settingsButton = Get<Button>(frame, "SettingsButton");
                var barCanvas = Get<Canvas>(frame, "BarCanvas");
                var actionPanel = Get<object>(hud, "ActionPanelView");
                Assert.That(end, Is.SameAs(Get<Button>(actionPanel, "EndRoundButton")));
                Assert.That(barCanvas.GetComponent<GraphicRaycaster>(), Is.Not.Null);
                Assert.That(FirstHitAt(settingsButton.transform as RectTransform),
                    Is.SameAs(settingsButton.gameObject), sceneName + " 顶栏设置输入命中错误");
                Assert.That(FirstHitAt(end.transform as RectTransform),
                    Is.SameAs(end.gameObject), sceneName + " 底栏结束行动输入命中错误");
                AssertContentBounds(Get<RectTransform>(frame, "TopBar"),
                    Get<RectTransform>(frame, "BottomBar"),
                    Get<RectTransform>(frame, "ContentRect"), barCanvas, sceneName);
                AssertMainSurfaceLayerOrder(frame, sceneName);

                settingsType.GetMethod("Open").Invoke(settings, null);
                yield return null;
                Canvas.ForceUpdateCanvases();
                var settingsView = settingsType.GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(settings);
                var actionLogButton = Get<Button>(settingsView, "ActionLogButton");
                var settingsOverlay = Get<GameObject>(settingsView, "OverlayObject");
                Assert.That(actionLogButton.transform.IsChildOf(settingsOverlay.transform), Is.True,
                    sceneName + " 日志入口未纳入设置页面");
                Assert.That(actionLogButton.gameObject.activeInHierarchy, Is.True,
                    sceneName + " 日志入口不可达");
                Assert.That(FirstHitAt(settingsButton.transform as RectTransform),
                    Is.SameAs(settingsButton.gameObject), sceneName + " 设置页遮住常驻栏");
                Assert.That(FirstHitAt(end.transform as RectTransform),
                    Is.SameAs(end.gameObject), sceneName + " 设置页遮住底栏");
                EventSystem.current.SetSelectedGameObject(settingsButton.gameObject);
                Assert.That(EventSystem.current.currentSelectedGameObject,
                    Is.SameAs(settingsButton.gameObject), sceneName + " 顶栏不可取得导航焦点");
                actionLogButton.onClick.Invoke();
                yield return null;
                Assert.That(settingsOverlay.activeSelf, Is.False, sceneName + " 切到日志后设置页仍可见");
                var logViewer = settingsType.GetField("actionLogViewer",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(settings);
                Assert.That(Get<bool>(logViewer, "IsOpen"), Is.True, sceneName + " 日志页未打开");
                Assert.That(FirstHitAt(settingsButton.transform as RectTransform),
                    Is.SameAs(settingsButton.gameObject), sceneName + " 日志页遮住常驻栏");
                Assert.That(FirstHitAt(end.transform as RectTransform),
                    Is.SameAs(end.gameObject), sceneName + " 日志页遮住底栏");
                var dragProbe = new GameObject("Drag Layer Probe", typeof(RectTransform),
                    typeof(Canvas), typeof(GraphicRaycaster), typeof(Image));
                var dragRect = dragProbe.transform as RectTransform;
                dragRect.anchorMin = Vector2.zero;
                dragRect.anchorMax = Vector2.one;
                dragRect.offsetMin = Vector2.zero;
                dragRect.offsetMax = Vector2.zero;
                var dragCanvas = dragProbe.GetComponent<Canvas>();
                dragCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                dragCanvas.overrideSorting = true;
                dragCanvas.sortingOrder = 150;
                dragProbe.GetComponent<Image>().color = new Color(1f, 1f, 1f, .01f);
                yield return null;
                Assert.That(FirstHitAt(settingsButton.transform as RectTransform),
                    Is.SameAs(settingsButton.gameObject), sceneName + " 拖拽层遮住顶栏");
                Assert.That(FirstHitAt(end.transform as RectTransform),
                    Is.SameAs(end.gameObject), sceneName + " 拖拽层遮住底栏");
                UnityEngine.Object.Destroy(dragProbe);
                logViewer.GetType().GetMethod("Close").Invoke(logViewer, null);
                settingsType.GetMethod("Close").Invoke(settings, null);
                yield return null;
                Debug.Log("PLAY OK " + sceneName + " " + Screen.width + "x" + Screen.height);
            }
        }

        private static IEnumerator ClearLaunchContext()
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var instance = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null) as Component;
            if (instance == null) yield break;
            UnityEngine.Object.Destroy(instance.gameObject);
            yield return null;
        }

        private static GameObject FirstHitAt(RectTransform target)
        {
            var point = RectTransformUtility.WorldToScreenPoint(EventCamera(target),
                target.TransformPoint(target.rect.center));
            Assert.That(point.x, Is.InRange(0f, Screen.width), target.name + " 位于屏幕外：" + point +
                "; screen=" + Screen.width + "x" + Screen.height);
            Assert.That(point.y, Is.InRange(0f, Screen.height), target.name + " 位于屏幕外：" + point);
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.Count == 0 ? null : hits[0].gameObject;
        }

        private static T Get<T>(object target, string property)
        {
            var info = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(info, Is.Not.Null, property);
            return (T)info.GetValue(target);
        }

        private static void AssertContentBounds(RectTransform topBar, RectTransform bottomBar,
            RectTransform contentRect, Canvas barCanvas, string sceneName)
        {
            // 常驻栏和内容区可能使用不同 Canvas，必须转换到同一屏幕空间后比较实际边界。
            var top = ScreenRect(topBar);
            var bottom = ScreenRect(bottomBar);
            var content = ScreenRect(contentRect);
            Assert.That(content.yMin, Is.GreaterThanOrEqualTo(bottom.yMax - .05f), sceneName + " 内容区压到底栏");
            Assert.That(content.yMax, Is.LessThanOrEqualTo(top.yMin + .05f), sceneName + " 内容区压到顶栏");
            Assert.That(content.width, Is.GreaterThan(0f), sceneName);
            Assert.That(content.height, Is.GreaterThan(0f), sceneName);

            var point = RectTransformUtility.WorldToScreenPoint(EventCamera(contentRect),
                contentRect.TransformPoint(contentRect.rect.center));
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            foreach (var hit in hits)
            {
                Assert.That(hit.gameObject.transform.IsChildOf(barCanvas.transform), Is.False,
                    sceneName + " 最高层透明区域挡住内容区");
            }
        }

        private static Camera EventCamera(RectTransform target)
        {
            var canvas = target.GetComponentInParent<Canvas>();
            var root = canvas == null ? null : canvas.rootCanvas;
            return root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
        }

        private static Rect ScreenRect(RectTransform target)
        {
            var corners = new Vector3[4]; target.GetWorldCorners(corners);
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var camera = EventCamera(target);
            foreach (var corner in corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                min = Vector2.Min(min, point); max = Vector2.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static void AssertMainSurfaceLayerOrder(Component frame, string sceneName)
        {
            var field = frame.GetType().GetField("mainSurface", BindingFlags.Instance | BindingFlags.NonPublic);
            var surface = field == null ? null : field.GetValue(frame) as RectTransform;
            Assert.That(surface, Is.Not.Null, sceneName + " 缺少主界面");
            var outerFrame = surface.Find("Outer Frame");
            var regions = frame.GetType().GetField("mainRegions", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(frame) as RectTransform;
            Assert.That(outerFrame, Is.Not.Null, sceneName + " 缺少外框");
            Assert.That(regions, Is.Not.Null, sceneName + " 缺少主模块");
            Assert.That(outerFrame.GetComponentInParent<Canvas>().sortingOrder,
                Is.LessThan(regions.GetComponentInParent<Canvas>().sortingOrder),
                sceneName + " 外框遮住左右模块边框");
            foreach (var side in new[] { "Left", "Right", "Top", "Bottom" })
            {
                var backdrop = surface.Find("Map Backdrop " + side);
                Assert.That(backdrop, Is.Not.Null, sceneName + " 缺少地图外侧底图 " + side);
                Assert.That(backdrop.GetSiblingIndex(), Is.LessThan(outerFrame.GetSiblingIndex()),
                    sceneName + " 地图外侧底图遮住外框 " + side);
            }
        }
    }
}
