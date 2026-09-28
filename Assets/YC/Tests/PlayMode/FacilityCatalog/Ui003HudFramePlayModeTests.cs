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
    public sealed class Ui003HudFramePlayModeTests
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
            var frame = Get<Component>(hud, "Frame");
            var settings = Get<Button>(frame, "SettingsButton");
            Assert.That(FirstHitAt(settings.transform as RectTransform), Is.SameAs(settings.gameObject),
                "建设临时层遮住顶栏");
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
            frameType.GetMethod("SetRequest").Invoke(frame, new object[] { "ui003-actual-effect", 10 });
            var registry = Get<object>(hud, "DialogRegistry");
            var shell = registry.GetType().GetMethod("InstantiateEffectDialogShell")
                .Invoke(registry, new object[] { content }) as Component;
            Assert.That(shell, Is.Not.Null, "实际 effect 弹窗源未实例化");
            Assert.That(shell.gameObject.activeInHierarchy, Is.True);
            var panel = Get<RectTransform>(shell, "Panel");
            shell.GetType().GetMethod("PrepareForUse").Invoke(shell, new object[]
            {
                "UI003 Effect Probe", "Effect Panel", panel.sizeDelta, panel.anchoredPosition, true
            });
            var originalPosition = panel.anchoredPosition;
            var dragEvent = new PointerEventData(EventSystem.current) { delta = new Vector2(120f, 80f) };
            var dragHandle = Get<Component>(shell, "DragHandle");
            dragHandle.GetType().GetMethod("OnDrag").Invoke(dragHandle, new object[] { dragEvent });
            var collapsible = Get<Component>(shell, "CollapsiblePanel");
            collapsible.GetType().GetMethod("OnDrag").Invoke(collapsible, new object[] { dragEvent });
            Assert.That(panel.anchoredPosition, Is.EqualTo(originalPosition), "拖动弹窗标题或边缘改变了位置");
            Canvas.ForceUpdateCanvases();
            Assert.That(FirstHitAt(Get<Button>(frame, "SettingsButton").transform as RectTransform),
                Is.SameAs(Get<Button>(frame, "SettingsButton").gameObject), "effect 弹窗遮住顶栏");
            Assert.That(FirstHitAt(Get<Button>(frame, "EndActionButton").transform as RectTransform),
                Is.SameAs(Get<Button>(frame, "EndActionButton").gameObject), "effect 弹窗遮住底栏");
            var optionContent = shell.GetType().GetMethod("ConfigureOptionScroll")
                .Invoke(shell, new object[] { "UI003 Options", 80f, 80f }) as RectTransform;
            var optionRow = shell.GetType().GetMethod("CreateOptionRow")
                .Invoke(shell, new object[] { optionContent });
            var selectedOption = Get<Button>(optionRow, "Button");
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
            frameType.GetMethod("SetRequest").Invoke(frame, new object[] { "ui003-actual-effect", 11 });
            Assert.That(shell.gameObject.activeSelf, Is.False, "同一请求刷新后应保持收起");
            fold.onClick.Invoke();
            Assert.That(Get<bool>(log, "IsOpen"), Is.False, "资料 B 控制器应真正关闭");
            Assert.That(shell.gameObject.activeSelf, Is.True, "当前 effect 页应恢复");
            Assert.That(EventSystem.current.currentSelectedGameObject,
                Is.SameAs(selectedOption.gameObject), "恢复 effect 后未返回原选项焦点");
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
            type.GetMethod("SetRequest").Invoke(frame, new object[] { "ui003-request", 7 });
            Assert.That(foldButton.interactable, Is.False, "纯地图请求不能伪造可恢复页面");
            type.GetMethod("ShowPage").Invoke(frame, new object[] { effect, true });
            Assert.That(foldButton.interactable, Is.True);
            type.GetMethod("SuspendEffectForInformation").Invoke(frame, null);
            Assert.That(effect.activeSelf, Is.False);
            type.GetMethod("ShowPage").Invoke(frame, new object[] { informationA, false });
            type.GetMethod("ShowPage").Invoke(frame, new object[] { informationB, false });
            Assert.That(informationA.activeSelf, Is.False);
            Assert.That(informationB.activeSelf, Is.True);
            type.GetMethod("SetRequest").Invoke(frame, new object[] { "ui003-request", 8 });
            Assert.That(effect.activeSelf, Is.False, "同一请求刷新不应自动弹回 effect 页");
            Assert.That(informationB.activeSelf, Is.True);
            foldButton.onClick.Invoke();
            Assert.That(informationB.activeSelf, Is.False);
            Assert.That(effect.activeSelf, Is.True);
            type.GetMethod("SetRequest").Invoke(frame, new object[] { "ui003-next", 9 });
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
                var dragProbe = new GameObject("UI003 Drag Layer Probe", typeof(RectTransform),
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
                Debug.Log("UI003 PLAY OK " + sceneName + " " + Screen.width + "x" + Screen.height);
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
            var point = RectTransformUtility.WorldToScreenPoint(null,
                target.TransformPoint(target.rect.center));
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




    }
}
