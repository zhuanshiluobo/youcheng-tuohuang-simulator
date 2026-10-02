using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation.Editor
{
    /// <summary>只读组件诊断：真实场景 Registry 和 Show 方法，不提交规则命令、不保存或生成资产。</summary>
    public static class GameplayEventDialogCapture
    {
        private enum Case { EventArtwork, EventText, ExplorePath, ExplorePayment, ResourceRecipients,
            ResourceBank, BuildFocus, BuildConfirmation, CharacterSecondEffect }
        private static Vector2Int[] sizes;
        private static Case[] cases;
        private static int sizeIndex, caseIndex, stage, stableFrames, callbackCount;
        private static bool applied;
        private static string signature, pendingPath, evidence, waiting;
        private static DateTime requestedUtc;
        private static double started;
        private static object dialog, gameViewGroup;
        private static EditorWindow gameView;
        private static PropertyInfo selectedSizeIndex;
        private static EventChoiceDialogView view;
        private static Report report;
        private static readonly Vector3[] Corners = new Vector3[4];

        [Serializable] private sealed class Report
        {
            public string mode;
            public string source = "组件诊断：SampleScene 当前 Registry 与实际 EventChoiceDialog Prefab；调用既有 Show 方法。路线、收款人和文字样本为诊断数据，卡图来自正式目录。回调仅计数，不提交命令；不代表规则结算或联机验收。";
            public Vector2 requestedSize, actualSize;
            public Rect panel, availableContent;
            public int diagnosticCallbacks;
            public List<string> issues = new List<string>();
            public List<Geometry> objects = new List<Geometry>();
            public List<ScrollCheck> scrolls = new List<ScrollCheck>();
        }
        [Serializable] private sealed class Geometry
        {
            public string path;
            public Rect rect;
            public string[] layoutDrivers;
        }
        [Serializable] private sealed class ScrollCheck
        {
            public string path, lastButton;
            public float contentHeight, viewportHeight;
            public bool contentOverflows, lastButtonVisibleAtTop, lastButtonReachesViewportAtEnd;
            public Rect viewportAtEnd, lastButtonAtEnd;
        }

        public static void RunBaseline() => Begin(new[] { new Vector2Int(1920, 1080) },
            (Case[])Enum.GetValues(typeof(Case)));
        public static void RunIntegrationMatrix() => Begin(new[] { new Vector2Int(1920, 1080),
            new Vector2Int(1920, 1200), new Vector2Int(2560, 1080), new Vector2Int(900, 600),
            new Vector2Int(1920, 1080) }, (Case[])Enum.GetValues(typeof(Case)));

        public static void RunNarrow() => Begin(new[] { new Vector2Int(1024, 768), new Vector2Int(900, 600) },
            new[] { Case.EventArtwork, Case.ExplorePayment, Case.ResourceRecipients, Case.BuildFocus });

        public static void RunFoundationFinal() => Begin(new[] { new Vector2Int(1920, 1080), new Vector2Int(900, 600) },
            (Case[])Enum.GetValues(typeof(Case)));

        private static void Begin(Vector2Int[] requestedSizes, Case[] requestedCases)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请在非运行状态启动事件窗口只读捕获。");
            sizes = requestedSizes; cases = requestedCases;
            sizeIndex = caseIndex = stage = stableFrames = callbackCount = 0;
            applied = false; signature = pendingPath = null; dialog = null; view = null;
            evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("YC_UI_CAPTURE_OUTPUT") ?? "prompt/UI换新/执行记录/证据");
            Directory.CreateDirectory(evidence);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            PrepareGameView(); SelectSize(sizes[0]);
            started = EditorApplication.timeSinceStartup;
            waiting = "等待正式场景初始化";
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup - started > 240) { Stop(1, "事件捕获超时：" + waiting); return; }
            if (!EditorApplication.isPlaying) return;
            try
            {
                if (pendingPath != null)
                {
                    var image = new FileInfo(pendingPath);
                    if (!image.Exists || image.Length < 256 || image.LastWriteTimeUtc < requestedUtc) return;
                    pendingPath = null; Advance(); return;
                }
                var size = sizes[sizeIndex];
                if (Screen.width != size.x || Screen.height != size.y) { waiting = "等待实际尺寸 " + size; return; }
                if (!applied && !Apply()) return;
                Canvas.ForceUpdateCanvases();
                if (!Ready()) { stableFrames = 0; return; }
                var next = string.Join("|", view.Panel.GetComponentsInChildren<RectTransform>()
                    .Where(r => r.gameObject.activeInHierarchy).Select(r => r.GetInstanceID() + ":" + ScreenRect(r).ToString("F2")));
                if (next != signature) { signature = next; stableFrames = 0; return; }
                if (++stableFrames < 5) return;
                if (stage == 0)
                {
                    report = BuildReport();
                    SetScroll(0); stage = 1; ResetStability(); return;
                }
                if (stage == 1)
                {
                    InspectScrollEnds();
                    SetScroll(1); stage = 2; ResetStability(); return;
                }
                report.diagnosticCallbacks = callbackCount;
                if (callbackCount != 0) report.issues.Add("诊断期间意外触发选择回调。");
                pendingPath = Path.Combine(evidence, "Dialogs-EventDiagnostic-" + cases[caseIndex] +
                    (sizeIndex == 4 ? "-Restored" : "") + "-" + size.x + "x" + size.y + ".png");
                File.WriteAllText(Path.ChangeExtension(pendingPath, ".json"), JsonUtility.ToJson(report, true), new System.Text.UTF8Encoding(false));
                requestedUtc = DateTime.UtcNow;
                ScreenCapture.CaptureScreenshot(pendingPath);
                Debug.Log("Event组件诊断：" + pendingPath + "；几何问题=" + report.issues.Count);
            }
            catch (Exception error) { Debug.LogException(error); Stop(1, "事件组件捕获失败。"); }
        }

        private static bool Apply()
        {
            var controller = UnityEngine.Object.FindObjectOfType<MobileCityInteractionController>(true);
            var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>(true);
            if (controller == null || controller.CurrentState == null || hud == null || hud.DialogRegistry == null ||
                !FacilityCardDatabase.IsInitialized || !EventCardDatabase.IsInitialized) return false;
            if (!hud.DialogRegistry.TryValidateConfiguration(out waiting)) return false;
            // 仅暂停此内存实例的用户控制，防止演示页面被游戏刷新覆盖；不更改规则状态。
            controller.enabled = false;
            var type = typeof(EventChoiceDialogView).Assembly.GetType("YC.Presentation.EventChoiceDialog", true);
            if (dialog == null)
                dialog = Activator.CreateInstance(type, new object[] { hud.DialogRegistry,
                    new Func<RectTransform>(() => hud.Canvas.transform as RectTransform) });
            Action clicked = () => callbackCount++;
            Action<int> selected = _ => callbackCount++;
            Action<string, int> recipient = (_, __) => callbackCount++;
            Func<int, string> playerName = id => "诊断玩家" + id;
            var routes = Enumerable.Range(1, 6).Select(i => new ExplorePaymentChoice("诊断航道-" + i,
                new List<int> { 2, 3, 4 })).ToList();
            var recipients = routes.ToDictionary(route => route.RouteId, _ => 2);
            switch (cases[caseIndex])
            {
                case Case.EventArtwork:
                    var actualCard = EventCardDatabase.GreenCardIds.Concat(EventCardDatabase.RedCardIds).Concat(EventCardDatabase.YellowCardIds)
                        .Select(EventCardDatabase.Get).FirstOrDefault(card => card != null && card.ChoiceRewards.Count > 0 &&
                            hud.DialogRegistry.CardVisualCatalog.GetEvent(card.CardId) != null);
                    if (actualCard == null) { waiting = "正式事件卡图尚未就绪"; return false; }
                    Invoke("ShowEventCardOptions", actualCard, "组件诊断 · 源岩资源点", null, null, playerName, selected, recipient);
                    break;
                case Case.EventText:
                    var sample = new EventCardDefinition { CardId = "dialogs.diagnostic.event-text", Name = "文字事件组件诊断",
                        Description = "此页使用诊断候选检查长文本、付款对象和滚动布局。选择不会触发游戏规则。" };
                    for (var i = 0; i < 10; i++)
                    { sample.ChoiceDescriptions.Add("诊断选项 " + (i + 1) + "：检查多行说明在实际内容宽度下的换行和完整显示。"); sample.ChoiceRewards.Add(new ResourceSet()); }
                    Invoke("ShowEventCardOptions", sample, "组件诊断", routes.Take(2).ToList(), recipients, playerName, selected, recipient);
                    break;
                case Case.ExplorePath:
                    Invoke("ShowExplorePathOptions", Enumerable.Range(1, 10).Select(i => new ExplorePathChoice(null,
                        "诊断路线 " + i + "：城市 → 相邻航道 → 目标资源点；检查列表末项可达。")).ToList(), selected);
                    break;
                case Case.ExplorePayment:
                    Invoke("ShowExplorePaymentOptions", routes, recipients, playerName, recipient, clicked);
                    break;
                case Case.ResourceRecipients:
                    Invoke("ShowResourceCollectionPaymentOptions", "诊断航道", 3, Enumerable.Range(2, 8).ToArray(),
                        playerName, selected, clicked, clicked);
                    break;
                case Case.ResourceBank:
                    Invoke("ShowResourceCollectionPaymentOptions", "诊断航道", 3, new int[0], playerName, selected, clicked, clicked);
                    break;
                case Case.BuildFocus:
                case Case.BuildConfirmation:
                    var facility = FacilityCardDatabase.All.FirstOrDefault(card => hud.DialogRegistry.CardVisualCatalog.GetFacility(card.FacilityId) != null);
                    if (facility == null) { waiting = "正式设施卡图尚未就绪"; return false; }
                    var query = new BuildFacilityOptionQueryService(BuildFacilityService.CreateForEffectTree())
                        .Query(controller.CurrentState, controller.CurrentState.CurrentPlayerId, facility.FacilityId);
                    var model = new BuildFacilityDraftViewModel(cases[caseIndex] == Case.BuildFocus ? BuildFacilityDraftPhase.Focused : BuildFacilityDraftPhase.Confirming,
                        new[] { query }, query, facility, 0, BuildFacilityService.PaymentModeResources, string.Empty,
                        new[] { 0 }, _ => callbackCount++);
                    Invoke(cases[caseIndex] == Case.BuildFocus ? "ShowBuildFacilityFocus" : "ShowBuildFacilityConfirmation", model);
                    break;
                default:
                    Invoke("ShowCharacterSecondEffectDecision", "诊断角色", "计谋", clicked, clicked);
                    break;
            }
            view = (EventChoiceDialogView)type.GetField("view", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
            if (view == null) { waiting = "正式Show入口未创建视图"; return false; }
            applied = true; stage = 0; callbackCount = 0; ResetStability();
            waiting = "等待字体、卡图及布局稳定";
            return true;
        }

        private static void Invoke(string method, params object[] args)
        {
            var target = dialog.GetType().GetMethod(method);
            var values = Enumerable.Repeat<object>(Type.Missing, target.GetParameters().Length).ToArray();
            Array.Copy(args, values, args.Length);
            target.Invoke(dialog, values);
        }

        private static bool Ready()
        {
            if (view == null || !view.gameObject.activeInHierarchy || view.Panel.rect.width <= 1 || !view.TryValidateConfiguration(out waiting)) return false;
            foreach (var text in view.GetComponentsInChildren<Text>())
                if (text.enabled && (text.font == null || text.font.material == null || text.font.material.mainTexture == null))
                { waiting = "字体图集未就绪：" + text.name; return false; }
            if (cases[caseIndex] == Case.EventArtwork && view.EventCardArtworkImage.texture == null) return false;
            if (cases[caseIndex] == Case.BuildFocus && view.FacilityPreviewImage.texture == null) return false;
            return !CanvasUpdateRegistry.IsRebuildingLayout() && !CanvasUpdateRegistry.IsRebuildingGraphics();
        }

        private static Report BuildReport()
        {
            var result = new Report { mode = cases[caseIndex].ToString(), requestedSize = sizes[sizeIndex],
                actualSize = new Vector2(Screen.width, Screen.height), panel = ScreenRect(view.Panel),
                availableContent = GameplayHudFrame.Active == null ? Screen.safeArea : ScreenRect(GameplayHudFrame.Active.ContentRect) };
            if (!Contains(result.availableContent, result.panel)) result.issues.Add("窗口超出实际内容区。");
            foreach (var rect in view.Panel.GetComponentsInChildren<RectTransform>())
            {
                if (!rect.gameObject.activeInHierarchy) continue;
                result.objects.Add(new Geometry { path = PathOf(rect), rect = ScreenRect(rect), layoutDrivers = rect.GetComponents<Component>()
                    .Where(c => c is ILayoutController || c is ILayoutElement || c is ScrollRect).Select(c => c.GetType().Name).ToArray() });
            }
            foreach (var group in view.Panel.GetComponentsInChildren<Button>().Where(b => b.gameObject.activeInHierarchy).GroupBy(b => b.transform.parent))
            {
                var buttons = group.ToArray();
                for (var i = 0; i < buttons.Length; i++) for (var j = i + 1; j < buttons.Length; j++)
                {
                    var first = ScreenRect((RectTransform)buttons[i].transform);
                    var second = ScreenRect((RectTransform)buttons[j].transform);
                    // 共边热点经 Canvas 缩放会有亚像素舍入，不等于实际点击区重叠。
                    if (Mathf.Min(first.xMax, second.xMax) - Mathf.Max(first.xMin, second.xMin) > .5f &&
                        Mathf.Min(first.yMax, second.yMax) - Mathf.Max(first.yMin, second.yMin) > .5f)
                        result.issues.Add("同级按钮重叠：" + PathOf(buttons[i].transform) + " / " + buttons[j].name);
                }
            }
            if (cases[caseIndex] == Case.EventArtwork)
            {
                var image = ScreenRect(view.EventCardArtworkImage.rectTransform);
                var texture = view.EventCardArtworkImage.texture;
                if (Mathf.Abs(image.width / image.height - (float)texture.width / texture.height) > .01f)
                    result.issues.Add("原图显示矩形与源图比例不一致。");
                foreach (var button in view.ArtworkChoiceHost.GetComponentsInChildren<Button>())
                    if (!Contains(image, ScreenRect((RectTransform)button.transform))) result.issues.Add("热点超出最终图像矩形：" + button.name);
            }
            return result;
        }

        private static void SetScroll(float position)
        {
            foreach (var scroll in view.GetComponentsInChildren<ScrollRect>())
            { if (!scroll.gameObject.activeInHierarchy) continue; scroll.StopMovement(); scroll.verticalNormalizedPosition = position; }
        }

        private static void InspectScrollEnds()
        {
            foreach (var scroll in view.GetComponentsInChildren<ScrollRect>())
            {
                if (!scroll.gameObject.activeInHierarchy || scroll.content == null || scroll.viewport == null) continue;
                var buttons = scroll.content.GetComponentsInChildren<Button>().Where(b => b.gameObject.activeInHierarchy).ToArray();
                var last = buttons.LastOrDefault();
                var viewport = ScreenRect(scroll.viewport);
                var lastRect = last == null ? new Rect() : ScreenRect((RectTransform)last.transform);
                var overflow = scroll.content.rect.height > scroll.viewport.rect.height + 1;
                var reaches = last == null || (lastRect.yMax > viewport.yMin && lastRect.yMin >= viewport.yMin - 2 && lastRect.yMin < viewport.yMax);
                var initialButton = last == null ? null : report.objects.Find(item => item.path == PathOf(last.transform));
                var initialViewport = report.objects.Find(item => item.path == PathOf(scroll.viewport));
                var visibleAtTop = initialButton != null && initialViewport != null && Contains(initialViewport.rect, initialButton.rect);
                report.scrolls.Add(new ScrollCheck { path = PathOf(scroll.transform), lastButton = last == null ? "无交互条目" : PathOf(last.transform),
                    contentHeight = scroll.content.rect.height, viewportHeight = scroll.viewport.rect.height, contentOverflows = overflow,
                    lastButtonVisibleAtTop = visibleAtTop, lastButtonReachesViewportAtEnd = reaches, viewportAtEnd = viewport, lastButtonAtEnd = lastRect });
                if (!reaches && !visibleAtTop) report.issues.Add("首尾位置均未显示末项，需要人工检查：" + PathOf(scroll.transform));
            }
        }

        private static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin - 2 && inner.xMax <= outer.xMax + 2 &&
            inner.yMin >= outer.yMin - 2 && inner.yMax <= outer.yMax + 2;
        private static Rect ScreenRect(RectTransform rect)
        {
            var canvas = rect.GetComponentInParent<Canvas>();
            var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            rect.GetWorldCorners(Corners);
            var min = RectTransformUtility.WorldToScreenPoint(camera, Corners[0]);
            var max = RectTransformUtility.WorldToScreenPoint(camera, Corners[2]);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
        private static string PathOf(Transform value)
        { var path = value.name; for (var parent = value.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path; return path; }
        private static void ResetStability() { stableFrames = 0; signature = null; }
        private static void Advance()
        {
            applied = false; ResetStability();
            if (++caseIndex < cases.Length) return;
            caseIndex = 0;
            if (++sizeIndex >= sizes.Length) { Stop(0, "事件窗口组件诊断捕获完成；须阅读几何JSON及实际PNG，不能替代规则验收。"); return; }
            SelectSize(sizes[sizeIndex]);
        }
        private static void Stop(int code, string message)
        {
            EditorApplication.update -= Tick;
            Debug.Log(message);
            EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
        private static void PrepareGameView()
        {
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            var assembly = typeof(EditorApplication).Assembly;
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes");
            var sizesObject = typeof(ScriptableSingleton<>).MakeGenericType(sizesType).GetProperty("instance").GetValue(null);
            gameViewGroup = sizesType.GetMethod("GetGroup").Invoke(sizesObject,
                new[] { Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeGroupType"), "Standalone") });
            gameView = EditorWindow.GetWindow(assembly.GetType("UnityEditor.GameView"));
            selectedSizeIndex = gameView.GetType().GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        private static void SelectSize(Vector2Int size)
        {
            var assembly = typeof(EditorApplication).Assembly;
            var value = Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"),
                Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType"), "FixedResolution"), size.x, size.y, "事件诊断 " + size.x + "x" + size.y);
            gameViewGroup.GetType().GetMethod("AddCustomSize").Invoke(gameViewGroup, new[] { value });
            selectedSizeIndex.SetValue(gameView, (int)gameViewGroup.GetType().GetMethod("GetTotalCount").Invoke(gameViewGroup, null) - 1);
            gameView.Repaint();
        }
    }
}
