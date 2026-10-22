using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.PlayMode.ExpansionRules
{
    /// <summary>测试侧只读定位和真实 EventSystem 输入；不持有规则写入口。</summary>
    internal sealed class HotseatUiInputDriver
    {
        private readonly string directory;
        private readonly Func<object> observation;
        private readonly HotseatProfilerSampler profiler;
        private int inputIndex;
        private readonly List<object> pendingRecords = new List<object>();
        private static readonly JsonSerializerSettings RecordSettings = new JsonSerializerSettings
            { Converters = { new GeometryJsonConverter() } };
        public long LastReleaseTimestamp { get; private set; }
        public string LastTargetPath { get; private set; }
        internal bool CaptureAllocationMetrics { get; set; }

        public HotseatUiInputDriver(string directory, Func<object> observation, HotseatProfilerSampler profiler)
        {
            this.directory = directory;
            this.observation = observation;
            this.profiler = profiler;
            Directory.CreateDirectory(directory);
        }

        public static Type RuntimeType(string name)
        {
            var found = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
            Assert.That(found, Is.Not.Null, "未加载实际生产类型：" + name);
            return found;
        }

        public static object Read(object owner, string name)
        {
            if (owner == null) return null;
            for (var type = owner.GetType(); type != null; type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var field = type.GetField(name, flags);
                if (field != null) return field.GetValue(owner);
                var property = type.GetProperty(name, flags);
                if (property != null) return property.GetValue(owner);
            }
            Assert.Fail("只读界面定位合同已变化：" + owner.GetType().FullName + "." + name);
            return null;
        }

        public static Component[] Active(string fullName) => Object.FindObjectsOfType(RuntimeType(fullName))
            .Cast<Component>().Where(c => c != null && c.gameObject.activeInHierarchy).ToArray();

        public static GameObject Marker(string id)
        {
            var matches = Active("YC.PlayerJourney.PlayerAutomationId")
                .Where(c => (string)Read(c, "Id") == id).ToArray();
            Assert.That(matches.Length, Is.LessThanOrEqualTo(1), "可见界面标识重复：" + id);
            return matches.Length == 1 ? matches[0].gameObject : null;
        }

        public static string PathOf(GameObject value)
        {
            if (value == null) return "<不存在>";
            var names = new List<string>();
            for (var at = value.transform; at != null; at = at.parent) names.Add(at.name);
            names.Reverse();
            return value.scene.name + "/" + string.Join("/", names);
        }

        public void Record(string kind, object detail)
        {
            // Host测试证据单独存放；不将候选或私密实例写到公开游戏日志。
            pendingRecords.Add(new { kind, frame = Time.frameCount,
                utc = DateTime.UtcNow.ToString("O"), detail, state = observation() });
        }
        public void FlushRecords()
        {
            // Unity几何值含normalized等递归派生属性；证据只保存完整的原始坐标分量。
            var lines = pendingRecords.Select(value => JsonConvert.SerializeObject(value, RecordSettings)).ToArray();
            File.WriteAllLines(System.IO.Path.Combine(directory, "private-host-observation.jsonl"), lines);
        }

        private sealed class GeometryJsonConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Rect);
            public override bool CanRead => false;
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value is Vector2 v2) serializer.Serialize(writer, new { x = v2.x, y = v2.y });
                else if (value is Vector3 v3) serializer.Serialize(writer, new { x = v3.x, y = v3.y, z = v3.z });
                else if (value is Rect rect) serializer.Serialize(writer,
                    new { x = rect.x, y = rect.y, width = rect.width, height = rect.height });
                else throw new JsonSerializationException("不支持的几何证据类型：" + value?.GetType().FullName);
            }
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer) =>
                throw new NotSupportedException("几何证据只写入，不反序列化。");
        }

        public IEnumerator Until(Func<bool> condition, string reason, float seconds = 15)
        {
            var until = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            if (condition()) yield break;
            Record("等待失败", new { reason });
            Assert.Fail(reason + "；实际状态见本用例private-host-observation.jsonl。禁止内部回答兜底。");
        }

        public static bool TryPoint(GameObject target, out Vector2 point, out RaycastResult hit)
        {
            point = default; hit = default;
            if (target == null || !target.activeInHierarchy || EventSystem.current == null) return false;
            if (target.GetComponent<Selectable>() is Selectable selectable && !selectable.IsInteractable()) return false;
            foreach (var group in target.GetComponentsInParent<CanvasGroup>())
                if (group.alpha < .01f || !group.interactable || !group.blocksRaycasts) return false;
            var expected = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            if (expected == null) return false;
            if (target.transform is RectTransform rect)
            {
                var canvas = target.GetComponentInParent<Canvas>();
                if (canvas == null || !canvas.isActiveAndEnabled) return false;
                var root = canvas.rootCanvas;
                var camera = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
                if (root.renderMode != RenderMode.ScreenSpaceOverlay && camera == null) return false;
                foreach (var x in new[] { .5f, .2f, .8f })
                foreach (var y in new[] { .5f, .2f, .8f })
                {
                    var candidate = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(new Vector2(
                        Mathf.Lerp(rect.rect.xMin, rect.rect.xMax, x), Mathf.Lerp(rect.rect.yMin, rect.rect.yMax, y))));
                    if (Matches(candidate, expected, out hit)) { point = candidate; return true; }
                }
                return false;
            }
            var collider = target.GetComponent<Collider2D>();
            var renderer = target.GetComponentInChildren<SpriteRenderer>();
            if (collider == null || !collider.enabled || renderer == null || !renderer.enabled || renderer.color.a < .01f)
                return false;
            foreach (var raycaster in RaycasterManager.GetRaycasters())
            {
                if (!(raycaster is PhysicsRaycaster) || !raycaster.isActiveAndEnabled) continue;
                var camera = raycaster.eventCamera;
                if (camera == null || !camera.isActiveAndEnabled || camera.targetTexture != null) continue;
                foreach (var x in new[] { .5f, .2f, .8f })
                foreach (var y in new[] { .5f, .2f, .8f })
                {
                    var bounds = collider.bounds;
                    var world = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, x),
                        Mathf.Lerp(bounds.min.y, bounds.max.y, y), bounds.center.z);
                    if (!collider.OverlapPoint(world)) continue;
                    var projected = camera.WorldToScreenPoint(world);
                    if (projected.z < camera.nearClipPlane || projected.z > camera.farClipPlane || !camera.pixelRect.Contains(projected))
                        continue;
                    var candidate = new Vector2(projected.x, projected.y);
                    if (Matches(candidate, expected, out hit)) { point = candidate; return true; }
                }
            }
            return false;
        }

        private static bool Matches(Vector2 point, GameObject expected, out RaycastResult hit)
        {
            hit = Raycast(point);
            return hit.gameObject != null && ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject) == expected;
        }

        private static RaycastResult Raycast(Vector2 point)
        {
            if (point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height || EventSystem.current == null)
                return default;
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.Count == 0 ? default : hits[0];
        }

        public IEnumerator Click(Func<GameObject> resolve, string label,
            Func<HotseatProfilerSampler.ReadyTarget> nextReady = null, string operation = null, Func<int?> roomBuildCount = null)
        {
            yield return Until(() => resolve() != null, "没有实际控件：" + label);
            // 只能通过真实滚轮事件滚入视口，不能改ScrollRect位置或激活隐藏物体。
            var scrollAttempts = 0;
            var visibleDeadline = Time.realtimeSinceStartup + 15;
            var initialTarget = resolve();
            if (!TryPoint(initialTarget, out _, out _))
                Record("自动滚动前实际状态", new { label, scroll = ScrollObservation(initialTarget) });
            while (Time.realtimeSinceStartup < visibleDeadline)
            {
                var target = resolve();
                if (TryPoint(target, out _, out _)) break;
                if (scrollAttempts >= 40 || !ScrollToward(target)) yield return null;
                else { scrollAttempts++; yield return null; yield return null; }
            }
            var value = resolve();
            var reachable = TryPoint(value, out var point, out var hit);
            if (!reachable) Record("滚动后仍不可命中", new { label, scrollAttempts, scroll = ScrollObservation(value) });
            Assert.That(reachable, Is.True,
                "实际输入被遮挡、不可见或不可用：" + label + " / " + PathOf(value));
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            LastTargetPath = PathOf(handler);
            var data = new PointerEventData(EventSystem.current)
            {
                pointerId = -1, button = PointerEventData.InputButton.Left,
                position = point, pressPosition = point, eligibleForClick = true,
                clickCount = 1, clickTime = Time.unscaledTime, pointerCurrentRaycast = hit, pointerPressRaycast = hit,
                rawPointerPress = hit.gameObject
            };
            var callbackCount = 0;
            long callbackTimestamp = 0;
            var button = handler.GetComponent<Button>();
            var cardGesture = button != null && button.GetType().FullName == "YC.Presentation.CardPickerCardButton";
            UnityEngine.Events.UnityAction observed = () => { callbackCount++; callbackTimestamp = Stopwatch.GetTimestamp(); };
            if (button != null) button.onClick.AddListener(observed); // 只观察现有回调，不调用UnityEvent。
            var index = ++inputIndex;
            // 保留历史证据字段，当前选牌没有双击等待窗口；不再反射已删除的生产字段。
            profiler.BeginInput(index, label, cardGesture, 0d);
            profiler.PrepareReadyWatch(nextReady, operation ?? label, roomBuildCount);
            Record("指针按下", new { index, label, target = LastTargetPath, x = point.x, y = point.y,
                hit = PathOf(hit.gameObject), handler = handler.GetComponent<MonoBehaviour>()?.GetType().FullName,
                inputKind = cardGesture ? "卡牌选择手势" : "普通按钮或地图指针", configuredGestureWindowMs = 0d });
            profiler.Point("指针按下派发");
            var pressTimestamp = Stopwatch.GetTimestamp();
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerEnterHandler);
            data.pointerPress = ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler) ?? handler;
            try
            {
                profiler.Point("按下后等待抬起帧");
                yield return null;
                var release = Raycast(point);
                Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(release.gameObject), Is.SameAs(handler),
                    "抬起时射线目标改变，不能绕过遮罩点击原控件");
                data.pointerCurrentRaycast = release;
                profiler.Point("指针抬起/点击同步派发");
                var allocatedBefore = CaptureAllocationMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0;
                LastReleaseTimestamp = Stopwatch.GetTimestamp();
                profiler.ReleaseReadyWatch(LastReleaseTimestamp);
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                Assert.That(ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler), Is.True);
                var dispatchReturnedTimestamp = Stopwatch.GetTimestamp();
                var callbacksAtDispatch = callbackCount;
                var allocatedAtDispatch = CaptureAllocationMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0;
                // 测量先等固定终点；后续日志、截图、隐私投影均在此窗口外。
                profiler.ObserveReadyNow();
                if (cardGesture)
                {
                    Assert.That(callbacksAtDispatch, Is.EqualTo(1), "选牌单击必须在本次指针派发返回前回调一次：" + label);
                    Assert.That(callbackTimestamp, Is.InRange(LastReleaseTimestamp, dispatchReturnedTimestamp),
                        "选牌回调必须属于本次同步派发，不能等待后续Update。");
                }
                if (nextReady != null && profiler.Performance)
                {
                    var readyDeadline = Time.realtimeSinceStartup + 15;
                    while (!profiler.ReadyWatchCompleted && Time.realtimeSinceStartup < readyDeadline) yield return null;
                    if (!profiler.ReadyWatchCompleted)
                    {
                        profiler.AbandonReadyWatch("15秒内未观察到合法下一控件和实际渲染帧；不以yield或回调替代");
                        Assert.Fail("端到端判据未满足：" + label + "；详见readiness-samples.jsonl，当前渲染API可能不支持此执行环境。");
                    }
                }
                var allocatedAtReady = CaptureAllocationMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0;
                if (CaptureAllocationMetrics)
                    Record("点击主线程分配", new { index, label,
                        dispatchAllocatedBytes = allocatedAtDispatch - allocatedBefore,
                        throughReadyAllocatedBytes = allocatedAtReady - allocatedBefore,
                        note = "当前主线程累计托管分配差额；同步范围含指针/业务派发和边界标记，至可交互范围另含正常帧及只读采样；非存活内存、非Instantiate次数" });
                profiler.Point(cardGesture ? "卡牌同步回调已核对" : "等待按钮回调",
                    new { pressTimestamp, releaseTimestamp = LastReleaseTimestamp, dispatchReturnedTimestamp });
                // 卡牌选择已在同步派发边界验证；这里只保留其他按钮原有的等待，不直接调用onClick。
                if (button != null && !cardGesture)
                    yield return Until(() => callbackCount > 0, "指针事件未产生Button回调：" + label, 3);
                Assert.That(callbackCount, Is.LessThanOrEqualTo(1), "一次指针操作产生重复Button回调");
                profiler.Point("实际回调已观察", new { callbackTimestamp, callbackCount });
                Record("指针抬起与回调", new { index, label, buttonCallbacks = callbackCount, callbacksAtDispatch,
                    pressTimestamp, releaseTimestamp = LastReleaseTimestamp, dispatchReturnedTimestamp, callbackTimestamp,
                    stopwatchFrequency = Stopwatch.Frequency, configuredGestureWindowMs = 0d,
                    releaseToDispatchReturnedMs = (dispatchReturnedTimestamp - LastReleaseTimestamp) * 1000d / Stopwatch.Frequency,
                    pressToObservedCallbackMs = callbackTimestamp == 0 ? (double?)null :
                        (callbackTimestamp - pressTimestamp) * 1000d / Stopwatch.Frequency,
                    releaseToObservedCallbackMs = callbackTimestamp == 0 ? (double?)null :
                        (callbackTimestamp - LastReleaseTimestamp) * 1000d / Stopwatch.Frequency,
                    observedIncludingFrameWaitMs = ElapsedSinceRelease(),
                    note = "卡牌选择须在同步派发内回调；按下到抬起的自然帧仍计时，确认操作另记，不用于宣告P95达标" });
            }
            finally
            {
                profiler.AbandonReadyWatch("输入未完成");
                if (button != null) button.onClick.RemoveListener(observed);
                if (handler != null) ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerExitHandler);
            }
            profiler.Point("回调后跨帧等待");
            yield return null;
            profiler.Point("无输入帧", new { boundary = "回调后的下一测试观察帧；不等价于最终可交互首帧" });
        }

        public double ElapsedSinceRelease() => LastReleaseTimestamp == 0 ? 0 :
            (Stopwatch.GetTimestamp() - LastReleaseTimestamp) * 1000d / Stopwatch.Frequency;

        private bool ScrollToward(GameObject target)
        {
            if (target == null || !(target.transform is RectTransform rect)) return false;
            var scroll = target.GetComponentInParent<ScrollRect>();
            if (scroll == null || !scroll.isActiveAndEnabled || scroll.viewport == null) return false;
            var canvas = scroll.GetComponentInParent<Canvas>().rootCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var viewport = scroll.viewport;
            var center = RectTransformUtility.WorldToScreenPoint(camera, viewport.TransformPoint(viewport.rect.center));
            var targetPoint = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            // 在真实视口中寻找能够冒泡至该ScrollRect的滚轮命中点。
            foreach (var y in new[] { .5f, .2f, .8f })
            foreach (var x in new[] { .5f, .2f, .8f })
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, viewport.TransformPoint(new Vector2(
                    Mathf.Lerp(viewport.rect.xMin, viewport.rect.xMax, x), Mathf.Lerp(viewport.rect.yMin, viewport.rect.yMax, y))));
                var hit = Raycast(point);
                if (hit.gameObject == null || ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject) != scroll.gameObject) continue;
                // UGUI水平分量直接加到内容位置；右侧目标需要内容向左移动。垂直由UGUI内部反向。
                var delta = scroll.horizontal ? new Vector2(targetPoint.x > center.x ? -3 : 3, 0) :
                    new Vector2(0, targetPoint.y > center.y ? 3 : -3);
                var before = ScrollObservation(target);
                var data = new PointerEventData(EventSystem.current) { position = point, scrollDelta = delta, pointerCurrentRaycast = hit };
                ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.scrollHandler);
                Record("实际滚轮", new { target = PathOf(target), x = delta.x, y = delta.y,
                    before, after = ScrollObservation(target),
                    note = "只派发实际射线命中的滚轮事件；前后几何为只读，未直接赋滚动位置" });
                return true;
            }
            return false;
        }

        private static object ScrollObservation(GameObject target)
        {
            if (target == null || !(target.transform is RectTransform rect)) return null;
            var scroll = target.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.viewport == null || scroll.content == null) return null;
            var canvas = scroll.GetComponentInParent<Canvas>().rootCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var bar = scroll.horizontal ? scroll.horizontalScrollbar : scroll.verticalScrollbar;
            return new { target = PathOf(target), active = target.activeInHierarchy,
                targetScreen = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center)),
                viewportScreen = RectTransformUtility.WorldToScreenPoint(camera, scroll.viewport.TransformPoint(scroll.viewport.rect.center)),
                contentAnchored = scroll.content.anchoredPosition, normalized = scroll.normalizedPosition,
                content = scroll.content.rect, viewport = scroll.viewport.rect, velocity = scroll.velocity,
                horizontal = scroll.horizontal, vertical = scroll.vertical, sensitivity = scroll.scrollSensitivity,
                scrollbar = bar == null ? null : new { path = PathOf(bar.gameObject), active = bar.gameObject.activeInHierarchy,
                    value = bar.value, size = bar.size, direction = bar.direction.ToString(), interactable = bar.IsInteractable() } };
        }

        public IEnumerator Capture(string label, int player)
        {
            if (profiler.Performance) yield break; // 性能运行不在采集中截屏；功能阶段另存实际画面。
            profiler.Point("截图等待", new { label, player });
            var location = System.IO.Path.Combine(directory, "玩家" + player);
            Directory.CreateDirectory(location);
            var safe = string.Concat(label.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = System.IO.Path.Combine(location, inputIndex.ToString("D3") + "-" + safe + ".png");
            yield return null;
            ScreenCapture.CaptureScreenshot(path);
            yield return Until(() => File.Exists(path) && new FileInfo(path).Length > 0, "截图未落盘：" + path, 10);
            Record("实际截图", new { path, width = Screen.width, height = Screen.height });
            profiler.Point("无输入帧");
        }
    }
}
