using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 本轮专属夹具。房间、企业／科室与待验页面使用射线输入；其余开局响应通过正式Submit加速。
    // 不重跑八回合，不启动性能采样，不改生产资产。取态方式必须进入证据，不能冒充全流程鼠标测试。
    internal sealed class GameplayPageAcceptanceFixture
    {
        internal static readonly Vector2Int[] Sizes = { new Vector2Int(1920, 1080), new Vector2Int(1366, 768) };
        internal readonly string DirectoryPath = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("YC_PAGE_REWORK_EVIDENCE") ?? "Logs/GameplayUiRework/runtime",
            NUnit.Framework.TestContext.CurrentContext.Test.Name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
        internal Component Gameplay;
        internal GameSession Session;
        private int captureIndex;
        internal GameState State => Session.State;
        internal object Hud => Read(Gameplay, "gameplayInteractionHud");
        internal object Frame => Read(Hud, "Frame");
        internal RectTransform Host => (RectTransform)Read(Frame, "ContentRect");
        internal object Registry => Read(Hud, "DialogRegistry");
        internal InteractionRequest Request => State.EffectRuntime.InteractionRequests.FirstOrDefault(
            r => r.Status == "open" && !r.IsInternalMainlineInteraction());
        internal static object Read(object owner, string name) => HotseatUiInputDriver.Read(owner, name);
        internal static Type T(string name) => HotseatUiInputDriver.RuntimeType("YC.Presentation." + name);
        internal static Component[] Active(string name) => HotseatUiInputDriver.Active("YC.Presentation." + name);
        internal static object Call(object owner, string method, params object[] args) => owner.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length && m.GetParameters()
                .Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(v => v)).Invoke(owner, args);
        internal static GameObject ButtonObject(object owner, string name) => ((Button)Read(owner, name))?.gameObject;
        // 只数现有规则提交标记，不启用全局Profiler，不统计耗时或性能分位数。
        internal sealed class RuleSubmitCounter : IDisposable
        {
            private ProfilerRecorder recorder;
            internal RuleSubmitCounter()
            {
                var handles = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(handles);
                var handle = handles.Single(h => ProfilerRecorderHandle.GetDescription(h).Name == "YC.RuleSubmit");
                recorder = new ProfilerRecorder(handle, 256, ProfilerRecorderOptions.StartImmediately |
                    ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            }
            internal long Calls
            {
                get
                {
                    Assert.That(recorder.Valid && recorder.IsRunning && !recorder.WrappedAround, Is.True,
                        "提交计数无效或溢出，不能补零通过");
                    return Enumerable.Range(0, recorder.Count).Sum(i => recorder.GetSample(i).Count);
                }
            }
            public void Dispose() => recorder.Dispose();
        }
        internal static IEnumerator Wait(Func<bool> condition, string reason, float seconds = 15)
        {
            var limit = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < limit) yield return null;
            Assert.That(condition(), Is.True, reason);
        }
        internal static IEnumerator Settle()
        {
            // 等待正常原生布局，不以测试侧ForceRebuild修补生产入口。
            // 与现有批处理验收入口一样按Update帧等待；截图另外等待非空文件落盘。
            for (var i = 0; i < 8; i++) yield return null;
        }
        internal void Record(string name, object value)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(Path.Combine(DirectoryPath, "observations.jsonl"), JsonConvert.SerializeObject(new
            {
                name, frame = Time.frameCount, scene = SceneManager.GetActiveScene().name,
                width = Screen.width, height = Screen.height,
                state = Session == null ? null : new { State.GameId, phase = State.Phase.ToString(),
                    State.CurrentPlayerId, localPlayer = (int)Read(Gameplay, "localPlayerId"),
                    State.EffectRuntime.StateRevision, requestId = Request?.GetStableInteractionId() }, data = value
            }) + Environment.NewLine);
        }
        internal static Rect ScreenRect(RectTransform rect) => GameplayUiReworkFixture.ScreenRect(rect);
        internal static RaycastResult Hit(Vector2 point)
        {
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            Assert.That(hits, Is.Not.Empty, "实际射线未命中：" + point);
            return hits[0];
        }
        internal IEnumerator Click(GameObject target, string label, Vector2? exactPoint = null,
            PointerEventData.InputButton button = PointerEventData.InputButton.Left)
        {
            Assert.That(target, Is.Not.Null, label + "入口不存在");
            Vector2 point = default;
            if (exactPoint.HasValue) point = exactPoint.Value;
            else
            {
                for (var pass = 0; pass < 32 && !HotseatUiInputDriver.TryPoint(target, out point, out _); pass++)
                {
                    var scroll = target.GetComponentsInParent<ScrollRect>().FirstOrDefault(s => s.isActiveAndEnabled);
                    if (scroll == null) break;
                    var viewport = ScreenRect(scroll.viewport);
                    var rect = ScreenRect((RectTransform)target.transform);
                    var at = Hit(viewport.center);
                    var handler = ExecuteEvents.GetEventHandler<IScrollHandler>(at.gameObject);
                    Assert.That(handler, Is.EqualTo(scroll.gameObject), "滚动视口被其他层阻挡");
                    var delta = scroll.horizontal ? new Vector2(rect.center.x > viewport.center.x ? -1 : 1, 0)
                        : new Vector2(0, rect.center.y < viewport.center.y ? -1 : 1);
                    ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current) { position = viewport.center,
                        scrollDelta = delta, pointerCurrentRaycast = at }, ExecuteEvents.scrollHandler);
                    yield return null; yield return null;
                }
                Assert.That(HotseatUiInputDriver.TryPoint(target, out point, out _), Is.True, label + "不可见／不可点击");
            }
            var hit = Hit(point);
            var expected = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            var actual = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            Record("实际命中", new { label, point = new[] { point.x, point.y },
                target = HotseatUiInputDriver.PathOf(target), hit = HotseatUiInputDriver.PathOf(hit.gameObject),
                handler = HotseatUiInputDriver.PathOf(actual), input = button.ToString() });
            Assert.That(actual, Is.EqualTo(expected), label + "被遮挡或被子控件截获");
            var data = new PointerEventData(EventSystem.current) { pointerId = -1, button = button,
                position = point, pressPosition = point, eligibleForClick = true, clickCount = 1,
                clickTime = Time.unscaledTime, pointerCurrentRaycast = hit, pointerPressRaycast = hit };
            int callbacks = 0;
            var observedButton = actual.GetComponent<Button>();
            UnityEngine.Events.UnityAction observe = () => callbacks++;
            if (observedButton != null && button == PointerEventData.InputButton.Left) observedButton.onClick.AddListener(observe);
            try
            {
                ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerEnterHandler);
                data.pointerPress = ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler) ?? actual;
                yield return null;
                Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(Hit(point).gameObject), Is.EqualTo(actual));
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(actual, data, ExecuteEvents.pointerClickHandler);
                if (observedButton != null && button == PointerEventData.InputButton.Left)
                {
                    yield return Wait(() => callbacks > 0, label + "未产生真实回调", 3);
                    Assert.That(callbacks, Is.EqualTo(1), label + "一次输入重复回调");
                }
                yield return Settle();
            }
            finally { if (observedButton != null) observedButton.onClick.RemoveListener(observe); }
        }
        internal IEnumerator Resolution(Vector2Int size)
        {
#if UNITY_EDITOR
            var capture = HotseatUiInputDriver.RuntimeType("YC.Presentation.Editor.GameplaySupplementalPageCapture");
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { size });
#else
            Screen.SetResolution(size.x, size.y, false);
#endif
            yield return Wait(() => Screen.width == size.x && Screen.height == size.y, "指定分辨率未生效");
            yield return Settle();
        }
        internal IEnumerator Capture(string label, Component page)
        {
            yield return Settle();
            Directory.CreateDirectory(DirectoryPath);
            Record("运行时矩形与遮罩", new { label, nodes = page.GetComponentsInChildren<RectTransform>(true)
                .Where(r => r.gameObject.activeInHierarchy).Select(Geometry).ToArray() });
            var file = Path.Combine(DirectoryPath, (++captureIndex).ToString("D3") + "-" + label + "-" + Screen.width + "x" + Screen.height + ".png");
            ScreenCapture.CaptureScreenshot(file);
            yield return Wait(() => File.Exists(file) && new FileInfo(file).Length > 0, "截图未落盘");
        }
        internal static object Geometry(RectTransform rect)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var image = rect.GetComponent<Image>(); var raw = rect.GetComponent<RawImage>();
            var canvas = rect.GetComponentInParent<Canvas>();
            string asset = null, guid = null;
#if UNITY_EDITOR
            var resource = image != null ? (Object)image.sprite : raw != null ? raw.texture : null;
            asset = UnityEditor.AssetDatabase.GetAssetPath(resource);
            guid = string.IsNullOrEmpty(asset) ? null : UnityEditor.AssetDatabase.AssetPathToGUID(asset);
#endif
            return new { path = HotseatUiInputDriver.PathOf(rect.gameObject),
                corners = corners.Select(p => new[] { p.x, p.y, p.z }).ToArray(),
                size = new[] { rect.rect.width, rect.rect.height },
                masks = rect.GetComponentsInParent<RectMask2D>().Select(m => HotseatUiInputDriver.PathOf(m.gameObject)).ToArray(),
                stencilMasks = rect.GetComponentsInParent<Mask>().Select(m => HotseatUiInputDriver.PathOf(m.gameObject)).ToArray(),
                asset, guid, imageMultiplier = image == null ? (float?)null : image.pixelsPerUnitMultiplier,
                spritePPU = image?.sprite == null ? (float?)null : image.sprite.pixelsPerUnit,
                border = image?.sprite == null ? null : new[] { image.sprite.border.x, image.sprite.border.y, image.sprite.border.z, image.sprite.border.w },
                referencePPU = canvas == null ? (float?)null : canvas.referencePixelsPerUnit,
                canvasScale = canvas == null ? (float?)null : canvas.scaleFactor };
        }
        internal Component RequestPage() => Active("EnterpriseSelectionDialogView").SingleOrDefault(p =>
            ((EnterpriseSelectionDraft)Read(p, "Draft")).Projection?.RequestId == Request?.GetStableInteractionId());
        private void RecordCandidateInput(Component page, string phase)
        {
            var scroll = (ScrollRect)Read(page, "candidateScroll");
            object Node(GameObject value) => value == null ? null : new
            {
                path = HotseatUiInputDriver.PathOf(value), activeSelf = value.activeSelf,
                activeInHierarchy = value.activeInHierarchy,
                interactable = value.GetComponent<Selectable>()?.IsInteractable(),
                geometry = value.transform is RectTransform rect ? Geometry(rect) : null,
                groups = value.GetComponentsInParent<CanvasGroup>().Select(g => new
                {
                    path = HotseatUiInputDriver.PathOf(g.gameObject), g.alpha, g.interactable, g.blocksRaycasts
                }).ToArray()
            };
            object Ray(Vector2 point)
            {
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                return new { point = new[] { point.x, point.y }, hits = hits.Select(hit => new
                {
                    path = HotseatUiInputDriver.PathOf(hit.gameObject), hit.depth, hit.sortingOrder,
                    clickHandler = HotseatUiInputDriver.PathOf(ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject)),
                    scrollHandler = HotseatUiInputDriver.PathOf(ExecuteEvents.GetEventHandler<IScrollHandler>(hit.gameObject))
                }).ToArray() };
            }
            Record("企业候选输入前诊断", new
            {
                phase, page = Node(page.gameObject), scroll = Node(scroll.gameObject),
                viewport = Node(scroll.viewport.gameObject), content = Node(scroll.content.gameObject),
                viewportCenter = Ray(ScreenRect(scroll.viewport).center),
                targets = Request.CandidateIds.Select(id =>
                {
                    var target = Target(page, id);
                    var rect = target == null ? default : ScreenRect((RectTransform)target.transform);
                    return new { id, node = Node(target), reachable = HotseatUiInputDriver.TryPoint(target, out _, out _),
                        rays = target == null ? null : new[] { .2f, .5f, .8f }.SelectMany(x =>
                            new[] { .2f, .5f, .8f }.Select(y => Ray(new Vector2(
                                Mathf.Lerp(rect.xMin, rect.xMax, x), Mathf.Lerp(rect.yMin, rect.yMax, y))))).ToArray() };
                }).ToArray()
            });
        }
        internal static GameObject Target(Component page, string id)
        {
            foreach (DictionaryEntry board in (IDictionary)Read(page, "items"))
            {
                var targets = (IDictionary)Read(board.Value, "targets");
                if (targets.Contains(id)) return ButtonObject(targets[id], "button");
            }
            return null;
        }
        internal IEnumerator Start(int players, Func<Component, IEnumerator> departmentCheck,
            string preferredEnterpriseDefinition = "enterprise.rhine_lab", string preferredCharacterDefinition = null,
            Func<Component, IEnumerator> firstEnterpriseCheck = null,
            Func<InteractionRequest, IEnumerator> openingRequestCheck = null, string mapId = null)
        {
            Record("验收边界", new { players, scope = "正常房间→实际场景；企业/科室及待验页面走EventSystem；其他开局请求只经正式Submit加速，无全局性能采样" });
            yield return SceneManager.LoadSceneAsync("StartScene"); yield return null;
            yield return Resolution(Sizes[0]);
            var menu = Active("StartMenuController").Single(); var view = Read(menu, "view");
            var room = (Component)Read(view, "RoomPanel");
            yield return Click(ButtonObject(view, "StartGameButton"), "创建本地房间");
            for (var i = 1; i < players; i++)
                yield return Click(ButtonObject(((Array)Read(room, "seats")).GetValue(i), "HotSeatButton"), "加入热座玩家");
            RoomState CurrentRoom() => (RoomState)Read(room, "DisplayedRoom");
            GameObject Option(string group, string id) => ButtonObject(((IEnumerable)Read(room, group)).Cast<object>()
                .Single(o => (string)Read(o, "Id") == id), "Button");
            var map = mapId ?? (players == 3 ? "map-three-players" : "map-four-players");
            if (CurrentRoom().Configuration.MapId != map) yield return Click(Option("maps", map), "选择实际地图");
            if (!CurrentRoom().Configuration.EnabledExpansionIds.Contains("enterprise"))
                yield return Click(Option("expansions", "enterprise"), "启用企业扩展");
            Assert.That(CurrentRoom().Configuration.EnabledExpansionIds, Is.EquivalentTo(new[] { "core", "enterprise" }));
            Record("正式房间配置", CurrentRoom().Configuration);
            yield return Click(ButtonObject(room, "StartButton"), "开始真实新局");
            yield return Wait(() => SceneManager.GetActiveScene().name == (map == "map-three-players" ? "ThreePlayerScene" : "SampleScene")
                && Active("MobileCityInteractionController").Length == 1, "实际游戏场景未就绪", 60);
            // 目标场景激活早于LoadingScene淡出；读取正式状态等待，不能越过黑幕输入。
            var transition = T("SceneTransitionContext").GetProperty("IsTransitionInProgress");
            yield return Wait(() => !(bool)transition.GetValue(null), "正式场景切换尚未完成，禁止在加载遮罩下输入", 30);
            Record("正式场景切换已完成", new { inProgress = (bool)transition.GetValue(null) });
            Gameplay = Active("MobileCityInteractionController").Single(); Session = (GameSession)Read(Gameplay, "session");
            Assert.That(State.Players.Count, Is.EqualTo(players));
            Record("真实对局内容快照", new { State.GameId, State.ContentPackHash, State.RuleConfiguration, State.EventDeckSeed });
            var answerHandler = ((IEnumerable)Read(Session, "commandHandlers")).Cast<object>()
                .OfType<AnswerInteractionCommandHandler>().Single();
            var effects = (EffectRegistry)Read(answerHandler, "registry");
            var enterprises = State.ItemZones.Items.Where(item =>
                effects.EnterpriseDefinitions.FindEnterprise(item.DefinitionId) != null).ToArray();
            Record("正式企业池与科室覆盖前置", new { players, preferredEnterpriseDefinition,
                enterprises = enterprises.Select(item => new { item.ItemId, item.DefinitionId }).ToArray(),
                note = "四人每人2候选覆盖8家企业，按本次专属验证目标点击真实候选；三人不保证出现科室请求" });
            if (players == 4)
            {
                Assert.That(enterprises.Select(item => item.DefinitionId).Distinct().Count(), Is.EqualTo(8));
                Assert.That(enterprises.Length, Is.EqualTo(8));
                Assert.That(enterprises.Any(item => item.DefinitionId == "enterprise.rhine_lab"), Is.True);
            }
            bool departmentSeen = false;
            bool firstEnterpriseCaptured = false;
            for (var step = 0; step < 80; step++)
            {
                if (State.Phase == GamePhase.ActionRound1 && Request == null) break;
                yield return Wait(() => Request != null || State.Phase == GamePhase.ActionRound1, "正式开局没有下一请求");
                if (Request == null) continue;
                var request = Request; var id = request.GetStableInteractionId();
                // 待验开局请求先交给实际 UI 输入；未处理的请求仍沿既有正式 Submit 加速。
                if (openingRequestCheck != null)
                {
                    yield return openingRequestCheck(request);
                    if (request.Status != "open" || Request == null || Request.GetStableInteractionId() != id)
                        continue;
                }
                if (request.InteractionTypeId == "setup.enterprise.choose" || request.InteractionTypeId == "setup.department.choose")
                {
                    yield return Wait(() => RequestPage() != null, "真实企业/科室页面未绑定");
                    var page = RequestPage();
                    if (request.InteractionTypeId == "setup.enterprise.choose" && !firstEnterpriseCaptured)
                    {
                        // 首次呈现先取原始状态，再等Capture内8个正常帧；不重建、不滚动、不改变通过条件。
                        RecordCandidateInput(page, "首次绑定帧");
                        yield return Capture("首次真实企业候选_输入前", page);
                        RecordCandidateInput(page, "自然帧及截图落盘后");
                        firstEnterpriseCaptured = true;
                        if (firstEnterpriseCheck != null) yield return firstEnterpriseCheck(page);
                    }
                    if (request.InteractionTypeId == "setup.department.choose")
                    {
                        departmentSeen = true;
                        Assert.That(request.CandidateIds.Count, Is.EqualTo(3), "正式开局科室需要实际3张候选");
                        yield return departmentCheck(page);
                    }
                    var selected = request.CandidateIds.OrderByDescending(candidate => State.ItemZones.Items.Any(
                        item => item.ItemId == candidate && item.DefinitionId == preferredEnterpriseDefinition))
                        .ThenByDescending(candidate => State.ItemZones.Items.Any(
                            item => item.ItemId == candidate && item.DefinitionId == "enterprise.rhine_lab"))
                        .Take(request.MinSelections).ToArray();
                    foreach (var candidate in selected) yield return Click(Target(page, candidate), "选择正式企业/科室候选");
                    using (var counter = new RuleSubmitCounter())
                    {
                        yield return Click(ButtonObject(page, "confirm"), "确认正式企业/科室");
                        Record("正式企业科室提交计数", new { request.InteractionTypeId, id, calls = counter.Calls });
                        Assert.That(counter.Calls, Is.EqualTo(1), "一次企业/科室确认必须仅正式提交一次");
                    }
                }
                else
                {
                    var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction,
                        PlayerId = request.AnsweringPlayerId };
                    command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = id;
                    command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
                    IEnumerable<string> choices = request.CandidateIds;
                    if (!string.IsNullOrEmpty(preferredCharacterDefinition))
                    {
                        bool IsPreferred(string candidate) => State.ItemZones.Items.Any(item =>
                            item.ItemId == candidate && item.DefinitionId == preferredCharacterDefinition);
                        if (request.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction)
                            choices = choices.Where(candidate => !IsPreferred(candidate));
                        if (request.InteractionTypeId == "character.cover.card")
                        {
                            Assert.That(choices.Any(IsPreferred), Is.True, "正式候选缺少待验角色，禁止补造实例：" + preferredCharacterDefinition);
                            choices = choices.OrderByDescending(IsPreferred);
                        }
                    }
                    command.OptionIds.AddRange(choices.Take(Math.Max(1, request.MinSelections)));
                    var result = Session.Submit(command);
                    Assert.That(result.Succeeded, Is.True, "辅助开局正式响应失败：" + request.InteractionTypeId + " " + result.Validation?.Reason);
                    Record("非待验开局响应通过正式Submit", new { request.InteractionTypeId, id, command.OptionIds });
                    Call(Gameplay, "SynchronizeInteractionFromState");
                }
                yield return Wait(() => Request == null || Request.GetStableInteractionId() != id, "请求未完成：" + request.InteractionTypeId);
            }
            if (players == 4 && preferredEnterpriseDefinition == "enterprise.rhine_lab")
                Assert.That(departmentSeen, Is.True, "四人8企业全覆盖并优先莱茵后必须进入真实科室，不能用组件补证替代");
            else if (!departmentSeen && players == 3)
                Record("三人本局未产生科室选择请求", new { note = "合法随机开局；继续其余页面，不以组件补证声称本局真实科室通过" });
            Assert.That(State.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(Request, Is.Null);
            yield return Settle();
        }
    }
}
