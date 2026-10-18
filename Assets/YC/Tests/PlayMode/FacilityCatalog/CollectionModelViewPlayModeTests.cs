using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace YC.Tests.PlayMode
{
    public sealed class CollectionModelViewPlayModeTests
    {
        private Component room;
        private Component view;
        private Slider horizontal;
        private Slider vertical;
        private Slider roll;

        [UnitySetUp]
        public IEnumerator LoadCollectionRoom()
        {
            yield return SceneManager.LoadSceneAsync("CollectionRoom", LoadSceneMode.Single);
            yield return null;
            room = UnityEngine.Object.FindObjectOfType(Runtime("CollectionRoomController")) as Component;
            view = UnityEngine.Object.FindObjectOfType(Runtime("CollectionModelViewController")) as Component;
            Assert.That(room, Is.Not.Null, "真实收藏室应保留藏品控制器");
            Assert.That(view, Is.Not.Null, "真实收藏室应绑定模型视角控件");
            horizontal = Field<Slider>(view, "horizontalSlider");
            vertical = Field<Slider>(view, "verticalSlider");
            roll = Field<Slider>(view, "rollSlider");
            Assert.That(horizontal != null && vertical != null && roll != null, Is.True, "旋转与翻转滑块需要完整绑定");
            yield return WaitForSelection(null);
            Canvas.ForceUpdateCanvases();
        }

        [UnityTest]
        public IEnumerator SlidersUseFrontViewAngles_ResetRestoresPreset_AndSwitchRefreshesAngles()
        {
            foreach (var itemId in new[] { "05", "06", "11" })
            {
                var item = FindItem(itemId);
                Call(room, "SelectItem", item);
                yield return WaitForSelection(item);

                var model = Property<GameObject>(room, "CurrentModel");
                var pose = Property<Transform>(view, "PoseTransform");
                var path = Property<string>(item, "ViewPosePath");
                var expectedPose = string.IsNullOrEmpty(path) ? model.transform : model.transform.Find(path);
                Assert.That(pose, Is.SameAs(expectedPose), "应读取当前藏品配置的展示姿态层级");
                var camera = Field<Camera>(room, "previewCamera");
                var frontRotation = Quaternion.Euler(Property<Vector3>(item, "FrontViewEuler"));
                var expectedStandard = camera.transform.rotation * frontRotation;
                Assert.That(Quaternion.Angle(Property<Quaternion>(view, "StandardViewRotation"), expectedStandard),
                    Is.LessThan(.1f), "标准角度应以藏品校准后的正视图为准");
                var presetRotation = pose.rotation;
                var presetViewRotation = Quaternion.Inverse(camera.transform.rotation) * presetRotation * Quaternion.Inverse(frontRotation);
                var presetViewEuler = FrontViewAngles(presetViewRotation);
                var presetAngles = new Vector2(presetViewEuler.y, presetViewEuler.x);
                var presetRoll = presetViewEuler.z;
                AssertAngles(Property<Vector2>(view, "ViewAngles"), presetAngles);
                AssertAngles(new Vector2(horizontal.value, vertical.value), presetAngles);
                AssertRoll(Property<float>(view, "RollAngle"), presetRoll);
                AssertRoll(roll.value, presetRoll);

                horizontal.value = ChangedValue(horizontal, 35f);
                vertical.value = ChangedValue(vertical, 18f);
                var beforeRoll = pose.rotation;
                var beforeRollAngle = Property<float>(view, "RollAngle");
                roll.value = ChangedValue(roll, 28f);
                yield return null;
                var expectedPoseRotation = camera.transform.rotation
                    * Quaternion.AngleAxis(Property<float>(view, "RollAngle"), Vector3.forward)
                    * Quaternion.AngleAxis(horizontal.value, Vector3.up)
                    * Quaternion.AngleAxis(vertical.value, Vector3.right) * frontRotation;
                Assert.That(Quaternion.Angle(pose.rotation, expectedPoseRotation), Is.LessThan(.1f),
                    "模型导入补偿不得把横向旋转变成绕视线滚转");
                Assert.That(Quaternion.Angle(pose.rotation, presetRotation), Is.GreaterThan(5f), "滑块应旋转实际模型");
                Assert.That(Quaternion.Angle(pose.rotation, beforeRoll), Is.GreaterThan(5f), "翻转滑块应绕屏幕正面Z轴改变实际姿态");
                var expectedScreenRoll = Quaternion.AngleAxis(Mathf.DeltaAngle(beforeRollAngle, roll.value),
                    camera.transform.forward) * beforeRoll;
                Assert.That(Quaternion.Angle(pose.rotation, expectedScreenRoll), Is.LessThan(.1f),
                    "斜视姿态中单独翻转应始终绕摄像机视线旋转");
                AssertAngles(Property<Vector2>(view, "ViewAngles"), new Vector2(horizontal.value, vertical.value));
                AssertRoll(Property<float>(view, "RollAngle"), roll.value);
                horizontal.value = vertical.value = roll.value = 0f;
                yield return null;
                Assert.That(Quaternion.Angle(pose.rotation, expectedStandard), Is.LessThan(.1f),
                    "三个角度均为零时应完全正视模型");
                Call(view, "SetZoom", 1.6f);
                Field<Button>(room, "resetViewButton").onClick.Invoke();
                yield return null;
                Assert.That(Quaternion.Angle(pose.rotation, presetRotation), Is.LessThan(.1f), "复位应恢复该藏品展示预设");
                AssertAngles(new Vector2(horizontal.value, vertical.value), presetAngles);
                AssertRoll(Property<float>(view, "RollAngle"), presetRoll);
                AssertRoll(roll.value, presetRoll);
                Assert.That(Property<float>(view, "ZoomMultiplier"), Is.EqualTo(1f).Within(.001f));
                if (itemId == "11") yield return CaptureReview("收藏室_复位验收.png");
            }
        }

        [UnityTest]
        public IEnumerator ItemWithoutModelKeepsDetailsAndDisablesModelControls()
        {
            var item = FindItem("02");
            Assert.That(Property<GameObject>(item, "DisplayPrefab"), Is.Null, "源岩当前只有资料，不能伪造模型引用");
            Call(room, "SelectItem", item);
            var deadline = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < deadline && (Property<bool>(room, "IsSwitching") ||
                !ReferenceEquals(Property<object>(room, "CurrentItem"), item))) yield return null;
            Assert.That(Property<object>(room, "CurrentItem"), Is.SameAs(item));
            Assert.That(Property<bool>(room, "IsSwitching"), Is.False);
            Assert.That(Property<GameObject>(room, "CurrentModel"), Is.Null);
            Assert.That(horizontal.interactable || vertical.interactable || roll.interactable, Is.False);
            Assert.That(Field<Behaviour>(room, "dragController").enabled, Is.False);
            Assert.That(Field<Button>(room, "resetViewButton").interactable, Is.False);
            Assert.That(Field<Text>(room, "nameLabel").text, Is.EqualTo(Property<string>(item, "DisplayName")));
            Assert.That(Field<Text>(room, "previewStatusLabel").gameObject.activeSelf, Is.True);
            Assert.That(Field<Text>(room, "previewStatusLabel").text, Is.Not.Empty);
            var modeled = FindItem("05");
            Call(room, "SelectItem", modeled);
            yield return WaitForSelection(modeled);
            Assert.That(horizontal.interactable && vertical.interactable && roll.interactable, Is.True);
        }

        [UnityTest]
        public IEnumerator WheelCapsZoom_AndTransientHintsFollowHandlesThenFade()
        {
            var item = FindItem("11");
            Call(room, "SelectItem", item);
            yield return WaitForSelection(item);
            var zoomHint = Field<CanvasGroup>(view, "zoomHint");
            var horizontalHint = Field<CanvasGroup>(view, "horizontalHint");
            var verticalHint = Field<CanvasGroup>(view, "verticalHint");
            var rollHint = Field<CanvasGroup>(view, "rollHint");
            Assert.That(IsHidden(zoomHint) && IsHidden(horizontalHint) && IsHidden(verticalHint) && IsHidden(rollHint), Is.True,
                "未操作时提示应隐藏");
            var pivot = Field<Transform>(view, "modelPivot");
            var initialScale = pivot.localScale;
            var wheel = new PointerEventData(EventSystem.current)
            {
                position = Center((RectTransform)view.transform),
                scrollDelta = new Vector2(0f, 100f)
            };
            ExecuteEvents.Execute(view.gameObject, wheel, ExecuteEvents.scrollHandler);
            yield return null;
            Assert.That(Property<float>(view, "ZoomMultiplier"), Is.EqualTo(2f).Within(.001f));
            Assert.That(Vector3.Distance(pivot.localScale, initialScale * 2f), Is.LessThan(.001f), "滚轮应实际放大模型");
            Assert.That(zoomHint.alpha, Is.GreaterThan(.95f));
            StringAssert.Contains("200%", zoomHint.GetComponentInChildren<Text>(true).text);
            yield return new WaitForSecondsRealtime(.15f);
            Assert.That(zoomHint.alpha, Is.GreaterThan(.95f), "停操作0.3秒前应保留倍率提示");
            yield return CaptureReview("收藏室_缩放200百分比.png");
            yield return new WaitForSecondsRealtime(.25f);
            Assert.That(zoomHint.alpha, Is.LessThan(.95f), "停操作0.3秒后应开始淡出");
            yield return WaitForHidden(zoomHint);

            Field<Button>(room, "resetViewButton").onClick.Invoke();
            yield return null;
            horizontal.value = ChangedValue(horizontal, 24f);
            vertical.value = ChangedValue(vertical, 12f);
            roll.value = ChangedValue(roll, 20f);
            yield return null;
            Assert.That(horizontalHint.alpha > .95f && verticalHint.alpha > .95f && rollHint.alpha > .95f, Is.True, "滑动应即时显示角度");
            Assert.That(horizontalHint.transform.IsChildOf(horizontal.handleRect), Is.True, "横向角度应跟随手柄");
            Assert.That(verticalHint.transform.IsChildOf(vertical.handleRect), Is.True, "竖向角度应跟随手柄");
            Assert.That(rollHint.transform.IsChildOf(roll.handleRect), Is.True, "翻转角度应跟随手柄");
            StringAssert.Contains("°", horizontalHint.GetComponentInChildren<Text>(true).text);
            StringAssert.Contains("°", verticalHint.GetComponentInChildren<Text>(true).text);
            StringAssert.Contains("°", rollHint.GetComponentInChildren<Text>(true).text);
            yield return CaptureReview("收藏室_角度交互验收.png");
            yield return new WaitForSecondsRealtime(.15f);
            Assert.That(horizontalHint.alpha > .95f && verticalHint.alpha > .95f && rollHint.alpha > .95f, Is.True);
            yield return new WaitForSecondsRealtime(.25f);
            Assert.That(horizontalHint.alpha < .95f && verticalHint.alpha < .95f && rollHint.alpha < .95f, Is.True);
            yield return WaitForHidden(horizontalHint);
            yield return WaitForHidden(verticalHint);
            yield return WaitForHidden(rollHint);
        }

        [UnityTest]
        public IEnumerator AxesFollowRotation_AndControlsDoNotCompeteWithDragOrSwitching()
        {
            var tips = AxisTips();
            var before = new Vector2[tips.Length];
            for (var index = 0; index < tips.Length; index++) before[index] = tips[index].anchoredPosition;
            horizontal.value = ChangedValue(horizontal, 50f);
            vertical.value = ChangedValue(vertical, 25f);
            roll.value = ChangedValue(roll, 22f);
            yield return null;
            var movement = 0f;
            for (var index = 0; index < tips.Length; index++)
                movement += (tips[index].anchoredPosition - before[index]).sqrMagnitude;
            Assert.That(movement, Is.GreaterThan(1f), "XYZ指示轴应随实际模型姿态更新");

            var drag = Field<Behaviour>(room, "dragController");
            vertical.value = 120f;
            yield return null;
            Assert.That(vertical.value, Is.EqualTo(120f).Within(.1f), "跨过顶视图时滑块不得跳回等价欧拉角");
            var previousAngles = Property<Vector2>(view, "ViewAngles");
            drag.GetType().GetMethod("ApplyRotation", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(drag, new object[] { new Vector2(3f, 2f) });
            yield return null;
            var draggedAngles = Property<Vector2>(view, "ViewAngles");
            Assert.That((draggedAngles - previousAngles).sqrMagnitude, Is.GreaterThan(.1f), "原拖转也应同步滑块读数");
            Assert.That(vertical.value, Is.GreaterThan(90f), "原拖转后应保留最近的俯仰分支");
            AssertRoll(Property<float>(view, "RollAngle"), roll.value);
            var contains = drag.GetType().GetMethod("Contains", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((bool)contains.Invoke(drag, new object[] { Center((RectTransform)horizontal.transform) }), Is.False);
            Assert.That((bool)contains.Invoke(drag, new object[] { Center((RectTransform)vertical.transform) }), Is.False);
            Assert.That((bool)contains.Invoke(drag, new object[] { Center((RectTransform)roll.transform) }), Is.False);
            Assert.That((bool)contains.Invoke(drag, new object[] { Center((RectTransform)Field<Button>(room, "resetViewButton").transform) }), Is.False);
            Assert.That((bool)contains.Invoke(drag, new object[] { Center((RectTransform)view.transform) }), Is.True,
                "模型中心应仍支持拖拽，滑块和复位区域不得同时触发拖拽");

            var target = FindItem("11");
            if (ReferenceEquals(Property<object>(room, "CurrentItem"), target)) target = FindItem("06");
            var pose = Property<Transform>(view, "PoseTransform");
            var rotation = pose.rotation;
            var zoom = Property<float>(view, "ZoomMultiplier");
            Call(room, "SelectItem", target);
            Assert.That(Property<bool>(room, "IsSwitching"), Is.True);
            Assert.That(horizontal.interactable || vertical.interactable || roll.interactable || drag.enabled, Is.False,
                "藏品过渡时应暂时禁用旋转输入");
            horizontal.value = ChangedValue(horizontal, 30f);
            vertical.value = ChangedValue(vertical, 15f);
            roll.value = ChangedValue(roll, 25f);
            Call(view, "ApplyScroll", 10f);
            Assert.That(Quaternion.Angle(pose.rotation, rotation), Is.LessThan(.1f));
            Assert.That(Property<float>(view, "ZoomMultiplier"), Is.EqualTo(zoom).Within(.001f));
            yield return WaitForSelection(target);
            Assert.That(horizontal.interactable && vertical.interactable && roll.interactable && drag.enabled, Is.True,
                "过渡结束应恢复旋转输入");
        }

        private IEnumerator WaitForSelection(object expected)
        {
            var deadline = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < deadline &&
                (Property<GameObject>(room, "CurrentModel") == null || Property<bool>(room, "IsSwitching") ||
                 (expected != null && !ReferenceEquals(Property<object>(room, "CurrentItem"), expected))))
                yield return null;
            Assert.That(Property<GameObject>(room, "CurrentModel"), Is.Not.Null, "藏品应加载完成");
            Assert.That(Property<bool>(room, "IsSwitching"), Is.False, "藏品切换应完成");
            if (expected != null) Assert.That(Property<object>(room, "CurrentItem"), Is.SameAs(expected));
            yield return null;
        }

        private static IEnumerator WaitForHidden(CanvasGroup hint)
        {
            var deadline = Time.realtimeSinceStartup + 2f;
            while (!IsHidden(hint) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(IsHidden(hint), Is.True, "提示应在停止操作后淡出隐藏");
        }

        private static IEnumerator CaptureReview(string fileName)
        {
            // 批处理只检查交互；画面留给可见程序的正常流程验收。
            if (Application.isBatchMode) yield break;
            yield return new WaitForEndOfFrame();
            var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,
                "../Logs/GitMergeValidation/UiMerge-20261018/collection-captures"));
            System.IO.Directory.CreateDirectory(directory);
            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, fileName), screenshot.EncodeToPNG());
            UnityEngine.Object.Destroy(screenshot);
        }

        private object FindItem(string itemId)
        {
            var items = Property<IEnumerable>(Field<object>(room, "catalog"), "Items");
            foreach (var item in items)
                if (Property<string>(item, "ItemId") == itemId) return item;
            Assert.Fail("收藏室目录缺少验收藏品：" + itemId);
            return null;
        }

        private RectTransform[] AxisTips()
        {
            var axes = Field<Array>(view, "axes");
            Assert.That(axes.Length, Is.EqualTo(3), "应完整绑定XYZ三轴");
            var result = new RectTransform[axes.Length];
            for (var index = 0; index < axes.Length; index++) result[index] = Field<RectTransform>(axes.GetValue(index), "tip");
            return result;
        }

        private static Vector3 FrontViewAngles(Quaternion viewRotation)
        {
            var right = viewRotation * Vector3.right;
            var up = viewRotation * Vector3.up;
            var forward = viewRotation * Vector3.forward;
            var yaw = Mathf.Asin(Mathf.Clamp(-right.z, -1f, 1f));
            if (Mathf.Abs(Mathf.Cos(yaw)) < .0001f)
                return new Vector3(0f, yaw * Mathf.Rad2Deg, Mathf.Atan2(-up.x, up.y) * Mathf.Rad2Deg);
            return new Vector3(Mathf.Atan2(up.z, forward.z) * Mathf.Rad2Deg,
                yaw * Mathf.Rad2Deg, Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg);
        }

        private static void AssertAngles(Vector2 actual, Vector2 expected)
        {
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(actual.x, expected.x)), Is.LessThan(.1f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(actual.y, expected.y)), Is.LessThan(.1f));
        }

        private static void AssertRoll(float actual, float expected) =>
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(actual, expected)), Is.LessThan(.1f));

        private static float ChangedValue(Slider slider, float delta) =>
            slider.value + delta <= slider.maxValue ? slider.value + delta : slider.value - delta;
        private static bool IsHidden(CanvasGroup hint) => !hint.gameObject.activeInHierarchy || hint.alpha <= .01f;
        private static Vector2 Center(RectTransform rect)
        {
            var canvas = rect.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        }
        private static Type Runtime(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static T Field<T>(object owner, string name) => (T)owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(owner);
        private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name).GetValue(owner);
        private static object Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name).Invoke(owner, args);
    }
}
