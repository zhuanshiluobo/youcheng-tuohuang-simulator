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
using YC.Application.Sessions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 通过实际场景中的注册表取得运行时所用源；不保存场景、不覆盖实例、不调用生成器。
    internal static class GameplayUiReworkFixture
    {
        internal const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static Type Type(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("YC.Presentation." + name)).First(t => t != null);
        internal static object Field(object o, string n) => o.GetType().GetField(n, Members).GetValue(o);
        internal static object Property(object o, string n) => o.GetType().GetProperty(n, Members).GetValue(o);
        internal static object Call(object o, string n, params object[] args) => o.GetType().GetMethods(Members)
            .Single(m => m.Name == n && m.GetParameters().Length == args.Length && m.GetParameters()
                .Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(v => v)).Invoke(o, args);
        internal static Component Find(string n) => UnityEngine.Object.FindObjectsOfType(Type(n), true).Cast<Component>().First();
        internal static IEnumerator Load(int count)
        {
            var launch = Type("GameLaunchContext").GetMethod("Ensure").Invoke(null, null);
            var config = new GameRuleConfiguration { PlayerCount = count,
                MapId = count == 3 ? StaticMapDefinitions.ThreePlayerMapId : StaticMapDefinitions.FourPlayerMapId };
            var seats = Enumerable.Range(1, count).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "界面验收" + id }).ToList();
            Call(launch, "Configure", LaunchMode.Local, 1, "", seats, config);
            yield return SceneManager.LoadSceneAsync(count == 3 ? "ThreePlayerScene" : "SampleScene");
            yield return null;
            Assert.That(Find("GameplayInteractionHudView"), Is.Not.Null);
        }
        internal static object Registry => Property(Find("GameplayInteractionHudView"), "DialogRegistry");
        internal static void PresentEnterprise(Component page, EnterpriseSelectionProjection projection, bool fromEffect)
        {
            // 受控组件请求必须与真实Frame的页面请求一致，避免沿用真实开局的挂起状态。
            var frame = Property(Find("GameplayInteractionHudView"), "Frame");
            if (fromEffect) Call(frame, "SetRequest", projection.RequestId, projection.Revision);
            else Call(frame, "ClearRequest");
            Call(page, "Present", projection, fromEffect);
        }
        internal static RectTransform Host => (RectTransform)Property(Property(Find("GameplayInteractionHudView"), "Frame"), "ContentRect");
        internal static void Layout(Component page)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.transform);
            Canvas.ForceUpdateCanvases();
        }
        internal static readonly Vector2Int[] Resolutions = { new Vector2Int(1920,1080), new Vector2Int(1280,720),
            new Vector2Int(1024,768), new Vector2Int(900,600), new Vector2Int(2560,1080) };
        internal static IEnumerator Resolution(Vector2Int size, HotseatUiInputDriver input)
        {
#if UNITY_EDITOR
            var capture = HotseatUiInputDriver.RuntimeType("YC.Presentation.Editor.GameplaySupplementalPageCapture");
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { size });
#else
            Screen.SetResolution(size.x, size.y, false);
#endif
            yield return input.Until(() => Screen.width == size.x && Screen.height == size.y, "指定画面分辨率未生效");
            yield return null;
        }
        internal static Rect ScreenRect(RectTransform rect)
        {
            var canvas = rect.GetComponentInParent<Canvas>().rootCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(low.x, low.y, high.x, high.y);
        }
        internal static RaycastResult Raycast(Vector2 point)
        {
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            Assert.That(hits, Is.Not.Empty, "指针未命中实际界面"); return hits[0];
        }
        internal static IEnumerator Wheel(ScrollRect scroll, Vector2 delta, HotseatUiInputDriver input)
        {
            var point = ScreenRect(scroll.viewport).center; var hit = Raycast(point);
            Assert.That(ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject), Is.EqualTo(scroll.gameObject));
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, new PointerEventData(EventSystem.current)
                { position = point, scrollDelta = delta, pointerCurrentRaycast = hit }, ExecuteEvents.scrollHandler);
            input.Record("布局滚轮", new { delta, viewport = ScreenRect(scroll.viewport) });
            yield return null; yield return null;
        }
        internal static IEnumerator DragScrollbar(ScrollRect scroll, bool toEnd, HotseatUiInputDriver input)
        {
            var bar = scroll.horizontal ? scroll.horizontalScrollbar : scroll.verticalScrollbar;
            Assert.That(bar, Is.Not.Null); Assert.That(bar.gameObject.activeInHierarchy, Is.True);
            var start = ScreenRect(bar.handleRect).center; var bounds = ScreenRect((RectTransform)bar.transform);
            var finish = scroll.horizontal ? new Vector2(toEnd ? bounds.xMax - 1 : bounds.xMin + 1, start.y)
                : new Vector2(start.x, toEnd ? bounds.yMin + 1 : bounds.yMax - 1);
            var hit = Raycast(start); var handler = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
            Assert.That(handler, Is.EqualTo(bar.gameObject), "滑块被遮挡，不能绕过射线输入");
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, position = start, pressPosition = start,
                button = PointerEventData.InputButton.Left, pointerCurrentRaycast = hit, pointerPressRaycast = hit };
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.initializePotentialDrag);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.beginDragHandler);
            for (int i = 1; i <= 6; i++)
            {
                var next = Vector2.Lerp(start, finish, i / 6f); data.delta = next - data.position; data.position = next;
                ExecuteEvents.Execute(handler, data, ExecuteEvents.dragHandler); yield return null;
            }
            ExecuteEvents.Execute(handler, data, ExecuteEvents.endDragHandler);
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerUpHandler);
            input.Record("布局滑块拖动", new { start, finish, horizontal = scroll.horizontal }); yield return null;
        }
        internal static string EvidenceDirectory(string kind, int players) => Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath,
            "../Logs/ExpansionValidation/GameplayIssueAcceptance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"),
            "ui", kind + "-" + players + "人"));
    }

    public sealed class EnterprisePickerLayoutUiTests
    {
        private Component page;
        private HotseatProfilerSampler profiler;
        private HotseatUiInputDriver evidence;
        private bool completed;
        [UnityTearDown] public IEnumerator Cleanup()
        {
            evidence?.FlushRecords(); evidence = null;
            if (page != null) { GameplayUiReworkFixture.Call(page, "ClearPresentation"); UnityEngine.Object.Destroy(page.gameObject); }
            if (profiler != null) { profiler.Finish(completed); UnityEngine.Object.Destroy(profiler.gameObject); }
            yield return null;
        }
        [UnityTest] public IEnumerator ThreePlayerScene_BrowseAndMultiSelectUseActualRegistry() => Verify(3);
        [UnityTest] public IEnumerator SampleScene_BrowseAndMultiSelectUseActualRegistry() => Verify(4);
        [UnityTest] public IEnumerator ThreePlayerScene_EnterpriseModeResolutionMatrix() => Matrix(3);
        [UnityTest] public IEnumerator SampleScene_EnterpriseModeResolutionMatrix() => Matrix(4);
        [UnityTest] public IEnumerator ThreePlayerScene_BoardGesturesAndScreenReturnPreserveDraft() => Gestures(3);
        [UnityTest] public IEnumerator SampleScene_BoardGesturesAndScreenReturnPreserveDraft() => Gestures(4);

        private IEnumerator Gestures(int players)
        {
            completed = false; yield return GameplayUiReworkFixture.Load(players);
            var directory = GameplayUiReworkFixture.EvidenceDirectory("企业详情手势", players);
            profiler = HotseatProfilerSampler.Create(directory, false, 0, 0);
            var input = evidence = new HotseatUiInputDriver(directory, () => new { scene = SceneManager.GetActiveScene().path }, profiler);
            yield return GameplayUiReworkFixture.Resolution(new Vector2Int(1920, 1080), input);
            page = (Component)GameplayUiReworkFixture.Call(GameplayUiReworkFixture.Registry,
                "InstantiateEnterpriseSelection", GameplayUiReworkFixture.Host, false);
            var detail = (GameObject)GameplayUiReworkFixture.Field(page, "detailPage");
            var surface = (GameObject)GameplayUiReworkFixture.Field(page, "detailsReturnSurface");
            for (int mode = 0; mode < 4; mode++)
            {
                var p = new EnterpriseSelectionProjection { RequestId = "gestures-" + mode, ViewerPlayerId = 1, Revision = 1,
                    CanCancel = true, ReadOnly = mode == 0, SubmissionAvailable = mode != 0,
                    Mode = mode == 2 ? EnterpriseSelectionMode.Effect : EnterpriseSelectionMode.Company,
                    Boards = Enumerable.Range(0, 5).Select(i => new EnterpriseBoardProjection { Id = "b" + i,
                        VisualKey = "enterprise-" + i, Label = "详情手势企业" + i }).ToArray() };
                var area = (Rect)GameplayUiReworkFixture.Call(GameplayUiReworkFixture.Find("GameplayExpansionUi"), "LevelArea", 2);
                p.Targets = p.Boards.Select((b, i) => new EnterpriseSelectionTarget { Id = b.Id, BoardId = b.Id,
                    CompanyId = b.Id, EffectId = "受控详情特效-" + i, Tier = 2, Label = b.Label,
                    Available = mode != 0 && (mode != 3 || i != 4), Description = "手势组件验收",
                    X = area.x, Y = area.y, Width = area.width, Height = area.height }).ToArray();
                int submissions = 0;
                GameplayUiReworkFixture.Call(page, "BindSubmission", (Func<EnterpriseSelectionProjection>)(() => p),
                    (Action<EnterpriseSelectionIntent>)(_ => submissions++), (Action)(() => { }));
                GameplayUiReworkFixture.PresentEnterprise(page, p, mode != 0);
                yield return null; GameplayUiReworkFixture.Layout(page); yield return null;
                var draft = (EnterpriseSelectionDraft)GameplayUiReworkFixture.Property(page, "Draft");
                var targets = page.GetComponentsInChildren(GameplayUiReworkFixture.Type("EnterpriseTargetView"), false).Cast<Component>().ToArray();
                var first = ((Button)GameplayUiReworkFixture.Field(targets.First(), "button")).gameObject;
                var last = ((Button)GameplayUiReworkFixture.Field(targets.Last(), "button")).gameObject;
                if (mode != 0) yield return input.Click(() => first, "手势前选择首板");
                var scroll = (ScrollRect)GameplayUiReworkFixture.Property(page, "CandidateScroll");
                yield return DragToLastCandidate(scroll, last, input);
                var selected = draft.SelectedIds.ToArray(); var inspected = draft.InspectedId;
                var offset = scroll.content.anchoredPosition;
                foreach (var gesture in new[] { "长按", "右键" })
                {
                    yield return PreviewGesture(last, gesture, detail, input);
                    Assert.That(draft.SelectedIds.ToArray(), Is.EqualTo(selected));
                    Assert.That(draft.InspectedId, Is.EqualTo(inspected));
                    Assert.That(submissions, Is.Zero);
                    yield return input.Capture("模式" + mode + "-" + gesture + "-详情", 1);
                    var point = gesture == "右键" ? GameplayUiReworkFixture.ScreenRect((RectTransform)surface.transform).min + new Vector2(8, 8)
                        : GameplayUiReworkFixture.ScreenRect(((RawImage)GameplayUiReworkFixture.Field(page, "enlargedImage")).rectTransform).center;
                    yield return ReturnAt(point, surface, input);
                    Assert.That(detail.activeSelf, Is.False);
                    Assert.That(draft.SelectedIds.ToArray(), Is.EqualTo(selected));
                    Assert.That(draft.InspectedId, Is.EqualTo(inspected));
                    Assert.That(Vector2.Distance(scroll.content.anchoredPosition, offset), Is.LessThan(1));
                    Assert.That(submissions, Is.Zero, "详情及返回均不能提交或穿透到底层候选");
                }
                // 原图的非特效正文区域也复用同一手势组件；单击不制造候选。
                if (mode == 2)
                {
                    var boards = page.GetComponentsInChildren(GameplayUiReworkFixture.Type("EnterpriseBoardItemView"), false).Cast<Component>().ToArray();
                    var background = (Component)GameplayUiReworkFixture.Field(boards.Last(), "boardPreviewButton");
                    foreach (var gesture in new[] { "长按", "右键" })
                    {
                        yield return PreviewGesture(background.gameObject, gesture, detail, input);
                        yield return ReturnAt(GameplayUiReworkFixture.ScreenRect((RectTransform)surface.transform).min + new Vector2(8, 8), surface, input);
                        Assert.That(draft.SelectedIds.ToArray(), Is.EqualTo(selected));
                        Assert.That(draft.InspectedId, Is.EqualTo(inspected));
                    }
                }
                Assert.That(HotseatUiInputDriver.TryPoint(last, out var start, out var hit), Is.True);
                var data = Pointer(start, hit, PointerEventData.InputButton.Left);
                ExecuteEvents.Execute(last, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(last, data, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(last, data, ExecuteEvents.beginDragHandler);
                data.delta = new Vector2(30, 0); data.position += data.delta;
                ExecuteEvents.Execute(last, data, ExecuteEvents.dragHandler); yield return null;
                ExecuteEvents.Execute(last, data, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(last, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(last, data, ExecuteEvents.pointerClickHandler);
                yield return new WaitForSecondsRealtime(.4f);
                Assert.That(detail.activeSelf, Is.False);
                Assert.That(draft.SelectedIds.ToArray(), Is.EqualTo(selected), "拖动后的松开不能误选");
                Assert.That(submissions, Is.Zero);
                input.Record("企业详情手势完成", new { mode, selected, inspected, longPress = .5f, singleClickWait = 0f });
            }
            completed = true;
        }

        private static PointerEventData Pointer(Vector2 point, RaycastResult hit, PointerEventData.InputButton button) =>
            new PointerEventData(EventSystem.current) { pointerId = button == PointerEventData.InputButton.Right ? -2 : -1,
                button = button, position = point, pressPosition = point, pointerCurrentRaycast = hit, pointerPressRaycast = hit,
                eligibleForClick = true, clickCount = 1, clickTime = Time.unscaledTime };

        private static IEnumerator PreviewGesture(GameObject target, string gesture, GameObject detail, HotseatUiInputDriver input)
        {
            Assert.That(HotseatUiInputDriver.TryPoint(target, out var point, out var hit), Is.True, "详情手势必须命中实际企业板");
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            Assert.That(handler, Is.EqualTo(target));
            var component = handler.GetComponent(GameplayUiReworkFixture.Type("CardPickerCardButton"));
            Assert.That(component, Is.Not.Null, "必须复用角色牌手势组件");
            var data = Pointer(point, hit, gesture == "右键" ? PointerEventData.InputButton.Right : PointerEventData.InputButton.Left);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
            if (gesture == "长按")
            {
                yield return new WaitForSecondsRealtime((float)GameplayUiReworkFixture.Field(component, "longPressSeconds") + .1f);
                Assert.That(detail.activeSelf, Is.True, "长按应在松开前打开详情");
            }
            else yield return null;
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            yield return null;
            Assert.That(detail.activeSelf, Is.True, gesture + "必须打开企业详情");
            input.Record("企业详情实际指针", new { gesture, point, target = HotseatUiInputDriver.PathOf(target) });
        }

        private IEnumerator ReturnAt(Vector2 point, GameObject surface, HotseatUiInputDriver input)
        {
            var hit = GameplayUiReworkFixture.Raycast(point);
            Assert.That(hit.gameObject, Is.EqualTo(surface), "返回接收层必须阻挡底层企业");
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            Assert.That(handler, Is.EqualTo(page.gameObject));
            var data = Pointer(point, hit, PointerEventData.InputButton.Left);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            Assert.That(data.used, Is.True, "返回点击必须消费");
            input.Record("企业详情单击屏幕返回", new { point }); yield return null;
        }
        private static IEnumerator DragToLastCandidate(ScrollRect scroll, GameObject last, HotseatUiInputDriver input)
        {
            var before = scroll.content.anchoredPosition;
            var normalizedBefore = scroll.horizontalNormalizedPosition;
            yield return GameplayUiReworkFixture.DragScrollbar(scroll, true, input);
            input.Record("企业滑块独立结果", new { before, after = scroll.content.anchoredPosition,
                normalizedBefore, normalizedAfter = scroll.horizontalNormalizedPosition,
                barValue = scroll.horizontalScrollbar.value });
            Assert.That(scroll.content.anchoredPosition.x, Is.LessThan(before.x - .1f), "拖滑块必须实际移动内容");
            Assert.That(scroll.horizontalNormalizedPosition, Is.GreaterThan(normalizedBefore + .01f));
            Assert.That(scroll.horizontalNormalizedPosition, Is.EqualTo(1f).Within(.01f), "滑块必须独立把内容带到末端");
            Assert.That(HotseatUiInputDriver.TryPoint(last, out _, out _), Is.True,
                "拖动后末板必须已可射线命中，不能依赖Click的自动滚轮补偿");
        }

        private static IEnumerator VerifyFittingCandidates(ScrollRect scroll, HotseatUiInputDriver input)
        {
            Assert.That(scroll.horizontalScrollbar.gameObject.activeInHierarchy, Is.False);
            Assert.That(scroll.content.rect.width, Is.LessThanOrEqualTo(scroll.viewport.rect.width + .1f));
            var before = scroll.content.anchoredPosition;
            foreach (float direction in new[] { -1f, 1f })
            {
                yield return GameplayUiReworkFixture.Wheel(scroll, new Vector2(direction * 3, 0), input);
                Assert.That(Vector2.Distance(scroll.content.anchoredPosition, before), Is.LessThan(.1f),
                    "内容未溢出时，滚轮不能使企业板偏移");
                var bounds = GameplayUiReworkFixture.ScreenRect(scroll.viewport);
                var start = bounds.center;
                var hit = GameplayUiReworkFixture.Raycast(start);
                var handler = ExecuteEvents.GetEventHandler<IDragHandler>(hit.gameObject);
                Assert.That(handler, Is.Not.Null);
                Assert.That(handler.GetComponentInParent<ScrollRect>(), Is.SameAs(scroll), "拖动必须来自实际候选区命中");
                var data = Pointer(start, hit, PointerEventData.InputButton.Left);
                data.pointerPress = ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler);
                data.pointerDrag = handler;
                ExecuteEvents.Execute(handler, data, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(handler, data, ExecuteEvents.beginDragHandler);
                data.dragging = true; data.eligibleForClick = false;
                for (int i = 1; i <= 6; i++)
                {
                    var next = start + new Vector2(direction * bounds.width * .25f * i / 6f, 0);
                    data.delta = next - data.position; data.position = next;
                    ExecuteEvents.Execute(handler, data, ExecuteEvents.dragHandler); yield return null;
                    Assert.That(Vector2.Distance(scroll.content.anchoredPosition, before), Is.LessThan(.1f),
                        "内容未溢出时，拖动不能使企业板偏移");
                }
                ExecuteEvents.Execute(handler, data, ExecuteEvents.endDragHandler);
                if (data.pointerPress != null) ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                yield return null;
                Assert.That(Vector2.Distance(scroll.content.anchoredPosition, before), Is.LessThan(.1f));
                input.Record("企业未溢出拖动结果", new { direction, before, after = scroll.content.anchoredPosition });
            }
        }

        private IEnumerator Matrix(int players)
        {
            completed = false; yield return GameplayUiReworkFixture.Load(players);
            var directory = GameplayUiReworkFixture.EvidenceDirectory("企业模式矩阵", players);
            profiler = HotseatProfilerSampler.Create(directory, false, 0, 0);
            var input = evidence = new HotseatUiInputDriver(directory, () => new { scene = SceneManager.GetActiveScene().path }, profiler);
            page = (Component)GameplayUiReworkFixture.Call(GameplayUiReworkFixture.Registry,
                "InstantiateEnterpriseSelection", GameplayUiReworkFixture.Host, false);
            int states = 0;
            foreach (var resolution in GameplayUiReworkFixture.Resolutions)
            {
                yield return GameplayUiReworkFixture.Resolution(resolution, input);
                // 同一实例依次经历2/4板详情打开、返回，再转8板；覆盖轴开关造成的监听丢失。
                foreach (int count in new[] { 2, 4, 8 })
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    int[] tiers = { 2, 2, 1, 3, 3, 2, 2, 2 };
                    var p = new EnterpriseSelectionProjection { RequestId = "matrix-" + states++, ViewerPlayerId = 1, Revision = 1,
                        CanCancel = true, ReadOnly = mode == 0, SubmissionAvailable = mode != 0,
                        Mode = mode == 2 ? EnterpriseSelectionMode.Effect : EnterpriseSelectionMode.Company,
                        Boards = Enumerable.Range(0, count).Select(i => new EnterpriseBoardProjection { Id = "b" + i,
                            VisualKey = "enterprise-" + i, Label = "企业组件 " + i,
                            PlayerIds = new[] { 1, 2, 3, 4 }.Take(players).ToArray(),
                            PlayerColors = new[] { 0, 1, 3, 2 }.Take(players).ToArray(),
                            PlayerLevels = new[] { 2, 3, 4, 1 }.Take(players).ToArray() }).ToArray() };
                    p.Targets = p.Boards.Select((b, i) =>
                    {
                        var area = (Rect)GameplayUiReworkFixture.Call(GameplayUiReworkFixture.Find("GameplayExpansionUi"), "LevelArea", tiers[i]);
                        return new EnterpriseSelectionTarget { Id = b.Id, BoardId = b.Id, CompanyId = b.Id,
                            EffectId = "受控特效" + i, Tier = tiers[i], Available = mode != 0 && i > 0,
                            Label = b.Label + " · " + tiers[i] + "级", Description = "组件压力输入；实际规则由正式宿主测试覆盖。",
                            UnavailableReason = i == 0 && mode != 0 ? "受控不可选候选" : null,
                            X = area.x, Y = area.y, Width = area.width, Height = area.height };
                    }).ToArray();
                    int submissions = 0;
                    GameplayUiReworkFixture.Call(page, "BindSubmission", mode == 0 ? null : (Func<EnterpriseSelectionProjection>)(() => p),
                        mode == 0 ? null : (Action<EnterpriseSelectionIntent>)(_ => submissions++), (Action)(() => { }));
                    GameplayUiReworkFixture.PresentEnterprise(page, p, mode != 0);
                    yield return null; GameplayUiReworkFixture.Layout(page); yield return null;
                    var draft = (EnterpriseSelectionDraft)GameplayUiReworkFixture.Property(page, "Draft");
                    var targets = page.GetComponentsInChildren(GameplayUiReworkFixture.Type("EnterpriseTargetView"), false).Cast<Component>().ToArray();
                    Assert.That(targets.Length, Is.EqualTo(count), "只为提供的特效注册目标，不能给普通奖励额外加框");
                    var scroll = (ScrollRect)GameplayUiReworkFixture.Property(page, "CandidateScroll");
                    Assert.That(scroll.horizontalScrollbar, Is.Not.Null);
                    Assert.That(scroll.horizontalScrollbar.gameObject.activeInHierarchy, Is.EqualTo(count > 4));
                    string label = (mode == 0 ? "只读" : mode == 1 ? "公司" : "特效") + "-" + count + "板-" + resolution.x + "x" + resolution.y;
                    yield return input.Capture(label + "-首屏", 1);
                    if (count > 4)
                    {
                        yield return DragToLastCandidate(scroll,
                            ((Button)GameplayUiReworkFixture.Field(targets.Last(), "button")).gameObject, input);
                    }
                    else yield return VerifyFittingCandidates(scroll, input);
                    yield return input.Click(() => ((Button)GameplayUiReworkFixture.Field(targets.Last(), "button")).gameObject, label + "末板");
                    Assert.That(draft.InspectedId, Is.EqualTo("b" + (count - 1)));
                    Assert.That(draft.SelectedIds.Count, Is.EqualTo(mode == 0 ? 0 : 1));
                    var excerpt = (RawImage)GameplayUiReworkFixture.Field(page, "detailImage");
                    Assert.That(excerpt.texture, Is.Not.Null);
                    Assert.That(excerpt.uvRect.height, Is.LessThan(.5f));
                    var boards = page.GetComponentsInChildren(GameplayUiReworkFixture.Type("EnterpriseBoardItemView"), false).Cast<Component>().ToArray();
                    var original = (RawImage)GameplayUiReworkFixture.Property(boards.Last(), "BoardImage");
                    var screen = GameplayUiReworkFixture.ScreenRect(original.rectTransform);
                    Assert.That(screen.width / screen.height, Is.EqualTo(13f / 37).Within(.002f));
                    input.Record("企业状态矩形", new { label, board = screen, excerpt = GameplayUiReworkFixture.ScreenRect(excerpt.rectTransform),
                        logical = original.rectTransform.rect, uv = excerpt.uvRect, content = scroll.content.rect, viewport = scroll.viewport.rect,
                        canvasScale = original.GetComponentInParent<Canvas>().rootCanvas.scaleFactor });
                    yield return input.Capture(label + "-末板摘录", 1);
                    var before = scroll.horizontalNormalizedPosition;
                    yield return input.Click(() => ((Button)GameplayUiReworkFixture.Field(page, "excerptButton")).gameObject, "摘录打开原图");
                    Assert.That(((GameObject)GameplayUiReworkFixture.Field(page, "detailPage")).activeSelf, Is.True);
                    yield return input.Capture(label + "-原板", 1);
                    yield return input.Click(() => (GameObject)GameplayUiReworkFixture.Field(page, "detailsReturnSurface"), "单击详情返回");
                    Assert.That(((GameObject)GameplayUiReworkFixture.Field(page, "detailPage")).activeSelf, Is.False);
                    Assert.That(scroll.horizontalNormalizedPosition, Is.EqualTo(before).Within(.01f));
                    Assert.That(submissions, Is.Zero);
                    if (mode != 0)
                    {
                        yield return input.Click(() => ((Button)GameplayUiReworkFixture.Field(targets.First(), "button")).gameObject, "不可选候选");
                        Assert.That(draft.SelectedIds, Is.Empty);
                        Assert.That(((Button)GameplayUiReworkFixture.Field(page, "confirm")).interactable, Is.False);
                        yield return input.Capture(label + "-不可选原因", 1);
                    }
                }
            }
            Assert.That(states, Is.EqualTo(45)); completed = true;
        }
        private IEnumerator Verify(int players)
        {
            completed = false; yield return GameplayUiReworkFixture.Load(players);
            var directory = GameplayUiReworkFixture.EvidenceDirectory("企业组件", players);
            profiler = HotseatProfilerSampler.Create(directory, false, 0, 0);
            var input = evidence = new HotseatUiInputDriver(directory, () => new { scene = SceneManager.GetActiveScene().path }, profiler);
            page = (Component)GameplayUiReworkFixture.Call(GameplayUiReworkFixture.Registry,
                "InstantiateEnterpriseSelection", GameplayUiReworkFixture.Host, false);
            var p = new EnterpriseSelectionProjection { RequestId = "layout-components", ViewerPlayerId = 1, Revision = 1,
                ReadOnly = true, CanCancel = true, Mode = EnterpriseSelectionMode.Company,
                Boards = Enumerable.Range(0, 4).Select(i => new EnterpriseBoardProjection { Id = "b" + i,
                    VisualKey = "enterprise-" + i, Label = "验收企业" + i, PlayerIds = new[] { 2, 1 },
                    PlayerColors = new[] { 1, 0 }, PlayerLevels = new[] { 4, 2 } }).ToArray() };
            p.Targets = p.Boards.Select(b => new EnterpriseSelectionTarget { Id = b.Id, BoardId = b.Id,
                CompanyId = b.Id, Label = b.Label, Description = "组件验收正文", Available = false }).ToArray();
            GameplayUiReworkFixture.PresentEnterprise(page, p, false);
            yield return null; GameplayUiReworkFixture.Layout(page);
            var draft = (EnterpriseSelectionDraft)GameplayUiReworkFixture.Property(page, "Draft");
            var confirm = (Button)GameplayUiReworkFixture.Field(page, "confirm");
            Assert.That(confirm.gameObject.activeSelf, Is.False);
            Assert.That(draft.Inspected, Is.Not.Null);
            GameplayUiReworkFixture.Call(page, "Choose", "b1");
            Assert.That(draft.SelectedIds, Is.Empty);
            Assert.That(draft.InspectedId, Is.EqualTo("b1"));
            foreach (var target in page.GetComponentsInChildren(GameplayUiReworkFixture.Type("EnterpriseTargetView"), true).Cast<Component>())
                Assert.That(((GameObject)GameplayUiReworkFixture.Field(target, "stateFrame")).activeSelf, Is.False);
            var image = (RawImage)GameplayUiReworkFixture.Field(page, "detailImage");
            Assert.That(image.texture, Is.Not.Null);
            Assert.That(image.uvRect.height, Is.LessThan(.5f), "详情必须为原文摘录，不能仍是微缩整板");
            GameplayUiReworkFixture.Call(page, "OpenDetails", "b1");
            Assert.That(((GameObject)GameplayUiReworkFixture.Field(page, "detailPage")).activeSelf, Is.True);
            GameplayUiReworkFixture.Call(page, "ReturnFromDetails");
            Assert.That(draft.InspectedId, Is.EqualTo("b1"));
            // 返回所在帧禁止穿透；下一次独立选择在下一帧开始，不绕过生产守卫。
            yield return null;
            p.ReadOnly = false; p.SubmissionAvailable = true; p.MinSelections = p.MaxSelections = 2; p.Revision++;
            foreach (var target in p.Targets) target.Available = true;
            int submissions = 0;
            GameplayUiReworkFixture.Call(page, "BindSubmission", (Func<EnterpriseSelectionProjection>)(() => p),
                (Action<EnterpriseSelectionIntent>)(_ => submissions++), (Action)(() => { }));
            GameplayUiReworkFixture.PresentEnterprise(page, p, true);
            GameplayUiReworkFixture.Call(page, "Choose", "b0");
            Assert.That(confirm.interactable, Is.False);
            GameplayUiReworkFixture.Call(page, "Choose", "b2");
            Assert.That(confirm.interactable, Is.True);
            Assert.That(draft.SelectedIds, Is.EquivalentTo(new[] { "b0", "b2" }));
            GameplayUiReworkFixture.Call(page, "OpenDetails", "b2");
            GameplayUiReworkFixture.Call(page, "ReturnFromDetails");
            Assert.That(draft.SelectedIds.Count, Is.EqualTo(2));
            Assert.That(submissions, Is.Zero, "查看原图不能提交业务选择");
            var scroll = (ScrollRect)GameplayUiReworkFixture.Property(page, "CandidateScroll");
            yield return null; // 等待原生ScrollRect按实际溢出量更新AutoHide。
            yield return VerifyFittingCandidates(scroll, input);
            var levels = page.GetComponentsInChildren(GameplayUiReworkFixture.Type("EnterpriseBoardItemView"), true).Cast<Component>()
                .Select(b => (Text[])GameplayUiReworkFixture.Field(b, "playerLevels")).First();
            Assert.That(levels[0].text, Is.EqualTo("2")); Assert.That(levels[1].text, Is.EqualTo("4"));
            Assert.That(levels[2].transform.parent.gameObject.activeSelf, Is.False);
            p.Boards = p.Boards.Concat(new[] { new EnterpriseBoardProjection { Id = "b4", VisualKey = "enterprise-4" } }).ToArray();
            p.Targets = p.Targets.Concat(new[] { new EnterpriseSelectionTarget { Id = "b4", BoardId = "b4", CompanyId = "b4", Available = true } }).ToArray();
            p.Revision++; GameplayUiReworkFixture.PresentEnterprise(page, p, true);
            yield return null; GameplayUiReworkFixture.Layout(page);
            Assert.That(scroll.horizontal, Is.True);
            Assert.That(scroll.content.rect.width, Is.GreaterThan(scroll.viewport.rect.width));
            p.ViewerPlayerId = 2; GameplayUiReworkFixture.PresentEnterprise(page, p, true);
            Assert.That(draft.SelectedIds, Is.Empty, "切换观察者后不能继承上一人的草稿");
            completed = true;
        }
    }
}
