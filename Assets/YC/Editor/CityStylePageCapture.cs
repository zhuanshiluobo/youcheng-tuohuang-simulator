using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Domain.State;
using YC.Domain.Rules;
using YC.Presentation.Workflows;

namespace YC.Presentation.Editor
{
    /// <summary>手动只读的真实场景捕获：不保存资产、不调用任何迁移/生成/Rebuild 入口。</summary>
    public static class CityStylePageCapture
    {
        private static readonly Vector2Int[] Sizes =
        {
            new Vector2Int(1920, 1080), new Vector2Int(1920, 1200), new Vector2Int(2560, 1080),
            new Vector2Int(900, 600), new Vector2Int(1920, 1080)
        };
        private static int sizeIndex;
        private static int stateIndex;
        private static int stableFrames;
        private static bool stateApplied;
        private static bool baselineOnly;
        private static string lastGeometry;
        private static string pendingPath;
        private static DateTime captureRequestedUtc;
        private static double started;
        private static string waiting = "等待场景与素材就绪";
        private static string evidence;
        private static EditorWindow gameView;
        private static object gameViewGroup;
        private static PropertyInfo selectedSizeIndex;

        [Serializable]
        private sealed class CaptureReport
        {
            public string scene;
            public string state;
            public string stateSource;
            public string limitation = "真实运行画面及几何记录；脚本打开页面不证明鼠标命中、联机或命令提交通过。";
            public Vector2 requestedSize;
            public Vector2 actualSize;
            public Rect safeArea;
            public List<RectItem> objects = new List<RectItem>();
        }
        [Serializable]
        private sealed class RectItem
        {
            public string path;
            public Rect screenRect;
            public string[] layoutDrivers;
            public bool hasClippingAncestor;
        }

        public static void RunGameplay() { Begin(); }
        public static void RunGameplayBaseline() { Begin(true); }
        public static void RunMarkerRevisionBaseline() { Begin(true, "城市样式标记调整"); }
        public static void RunUnifiedAcceptanceBaseline() { Begin(true, "城市样式统一验收"); }

        private static void Begin(bool singleSize = false, string evidenceFolder = "城市样式构图纠正")
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请在未运行游戏时手动启动 Dialogs 局内页面捕获。");
            baselineOnly = singleSize;
            evidence = Path.GetFullPath("prompt/UI换新/执行记录/" + evidenceFolder);
            Directory.CreateDirectory(evidence);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            PrepareGameView();
            sizeIndex = stableFrames = 0; stateIndex = 0;
            stateApplied = false;
            pendingPath = lastGeometry = null;
            started = EditorApplication.timeSinceStartup;
            SelectSize(Sizes[0]);
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void PrepareGameView()
        {
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            var assembly = typeof(EditorApplication).Assembly;
            var sizesType = assembly.GetType("UnityEditor.GameViewSizes");
            var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            var sizes = singleton.GetProperty("instance").GetValue(null);
            var groupType = assembly.GetType("UnityEditor.GameViewSizeGroupType");
            gameViewGroup = sizesType.GetMethod("GetGroup").Invoke(sizes,
                new[] { Enum.Parse(groupType, "Standalone") });
            gameView = EditorWindow.GetWindow(assembly.GetType("UnityEditor.GameView"));
            selectedSizeIndex = gameView.GetType().GetProperty("selectedSizeIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static void SelectSize(Vector2Int size)
        {
            var assembly = typeof(EditorApplication).Assembly;
            var type = assembly.GetType("UnityEditor.GameViewSizeType");
            var value = Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"),
                Enum.Parse(type, "FixedResolution"), size.x, size.y,
                "局内页面 " + size.x + "x" + size.y);
            gameViewGroup.GetType().GetMethod("AddCustomSize").Invoke(gameViewGroup, new[] { value });
            var index = (int)gameViewGroup.GetType().GetMethod("GetTotalCount").Invoke(gameViewGroup, null) - 1;
            selectedSizeIndex.SetValue(gameView, index);
            gameView.Repaint();
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup - started > 300)
            { Stop(1, "局内页面捕获超时：" + waiting); return; }
            if (!EditorApplication.isPlaying) return;
            try
            {
                if (pendingPath != null)
                {
                    var info = new FileInfo(pendingPath);
                    if (!info.Exists || info.Length < 256 || info.LastWriteTimeUtc < captureRequestedUtc) return;
                    pendingPath = null;
                    Advance();
                    return;
                }
                var requested = Sizes[sizeIndex];
                if (Screen.width != requested.x || Screen.height != requested.y)
                { waiting = "等待实际渲染尺寸 " + requested; return; }
                if (!stateApplied && !ApplyState()) return;
                Canvas.ForceUpdateCanvases();
                if (!TryReady(out var root)) { stableFrames = 0; return; }
                var signature = GeometrySignature(root);
                if (signature != lastGeometry)
                { lastGeometry = signature; stableFrames = 0; return; }
                if (++stableFrames < 4) return;
                var report = BuildReport(root);
                var restored = sizeIndex == Sizes.Length - 1 ? "-Restored" : "";
                pendingPath = Path.Combine(evidence, "CityStyle-Corrected" +
                    (baselineOnly ? "-Baseline" : "") + "-" + StateName + restored +
                    "-" + requested.x + "x" + requested.y + "-actual-" + Screen.width + "x" + Screen.height + ".png");
                File.WriteAllText(Path.ChangeExtension(pendingPath, ".json"), JsonUtility.ToJson(report, true),
                    new System.Text.UTF8Encoding(false));
                captureRequestedUtc = DateTime.UtcNow;
                ScreenCapture.CaptureScreenshot(pendingPath);
                Debug.Log("局内页面真实画面：" + pendingPath + "；状态：" + report.stateSource);
            }
            catch (Exception error) { Debug.LogException(error); Stop(1, "局内页面捕获失败。"); }
        }

        private static string StateName => new[] { "ThirdTab", "List", "Detail", "Returned", "Declare", "DiagnosticOverview", "DiagnosticDetail", "DiagnosticTable", "MainActions" }[stateIndex];
        private static int StateCount => baselineOnly ? 9 : 2;

        private static bool ApplyState()
        {
            var frame = UnityEngine.Object.FindObjectOfType<GameplayHudFrame>();
            var controller = UnityEngine.Object.FindObjectOfType<MobileCityInteractionController>();
            if (frame == null || controller == null || !frame.TryValidateConfiguration(out waiting)) return false;
            var page = UnityEngine.Object.FindObjectOfType<CityStyleDeclarationPreviewView>();
            if (stateIndex == 0 || stateIndex == 7 || stateIndex == 8)
            {
                if (page != null) page.CloseButton.onClick.Invoke();
                var tabs = UnityEngine.Object.FindObjectOfType<UiMainActionTabs>();
                if (tabs == null) return false;
                tabs.Select(stateIndex == 8 ? 0 : 2);
                if (stateIndex == 7)
                {
                    foreach(var row in UnityEngine.Object.FindObjectsOfType<CityStyleStatusRowView>())
                    {
                        var markers = new List<CityStyleMarkerViewModel>();
                        foreach (var area in row.TrackLayout == "active" ? new[]{"unused","used"} : row.TrackLayout == "reward" ? new[]{"declared"} : new[]{"2","used_from_2","1","used_from_1","0"})
                            for (var color=0;color<4;color++) markers.Add(new CityStyleMarkerViewModel(row.Id,color+1,(PlayerColor)color,area));
                        row.RenderMarkers(markers);
                    }
                }
            }
            else if (stateIndex == 1)
            {
                var tabs = UnityEngine.Object.FindObjectOfType<UiMainActionTabs>();
                var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>();
                if (tabs == null || hud == null) { waiting = "等待正式城市样式入口"; return false; }
                tabs.Select(2);
                hud.ActionPanelView.DeclareCityStyleButton.onClick.Invoke();
            }
            else if (stateIndex == 4)
            {
                page.CloseButton.onClick.Invoke();
                var action = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>().ActionPanelView;
                action.DeclareCityStyleButton.onClick.Invoke();
            }
            else if (stateIndex == 5)
            {
                var workflow = Read(controller, "workflowView");
                var dialog = Read(workflow, "cityStyleDeclarationDialog");
                var original = (CityStyleOptionsViewModel)Read(dialog, "model");
                var markers = new List<CityStyleMarkerViewModel>();
                foreach (var option in original.Options)
                {
                    var layout = page.CardBoardVisualLayout.CityStyleVisuals.Find(option.CityStyleId).trackLayout;
                    var areas = layout == "active" ? new[]{"unused","used"} : layout == "reward" ? new[]{"declared"} : new[]{"2","used_from_2","1","used_from_1","0"};
                    foreach (var area in areas)
                        for (var color = 0; color < 4; color++)
                            for (var count = 0; count < 3; count++)
                                markers.Add(new CityStyleMarkerViewModel(option.CityStyleId, color+1, (PlayerColor)color, area));
                }
                var options = original.Options.Select((o,i) => new CityStyleOptionViewModel {
                    CityStyleId=o.CityStyleId, Name=o.Name, Description=o.Description, Score=o.Score,
                    CanDeclare=i%2==0, Reason="诊断：显示不可用状态框" }).ToArray();
                var diagnostic = new CityStyleOptionsViewModel(options, original.CityBoardSlots, markers,
                    options[0].CityStyleId, null, null, null, null, null, true, null, "visual-diagnostic", 0, null, "诊断：四玩家同槽", 12);
                InvokePrivate(dialog, "Show", diagnostic);
            }
            else
            {
                if (page == null) return false;
                if (stateIndex == 2 || stateIndex == 6)
                    page.GetComponentsInChildren<CityStyleCardGesture>().First(g => g.CanOpen)
                        .OnPointerClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                        { button = UnityEngine.EventSystems.PointerEventData.InputButton.Right });
                else page.GetComponentsInChildren<CityStyleCardGesture>().First(g => !g.CanOpen)
                    .OnPointerClick(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                    { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left });
            }
            stateApplied = true;
            waiting = "等待素材和布局稳定";
            return true;
        }

        private static bool TryReady(out RectTransform root)
        {
            root = null;
            if (stateIndex == 0 || stateIndex == 7 || stateIndex == 8)
            {
                var frame = UnityEngine.Object.FindObjectOfType<GameplayHudFrame>();
                root = frame == null ? null : frame.transform as RectTransform;
                var build = UnityEngine.Object.FindObjectOfType<BuildInfoPanel>();
                if (build == null || !build.GetComponentsInChildren<RawImage>().Any(r => r.texture != null)) return false;
            }
            else
            {
                var view = UnityEngine.Object.FindObjectOfType<CityStyleDeclarationPreviewView>();
                if (view == null || !view.TryValidateConfiguration(out waiting) ||
                    view.CityStyleCardImage.texture == null || view.CityBoardImage.texture == null) return false;
                root = view.Panel;
            }
            if (root == null || !root.gameObject.activeInHierarchy || root.rect.width < 1 || root.rect.height < 1) return false;
            foreach (var text in root.GetComponentsInChildren<Text>())
                if (text.enabled && text.font == null) { waiting = "缺少字体：" + text.name; return false; }
            return !CanvasUpdateRegistry.IsRebuildingLayout() && !CanvasUpdateRegistry.IsRebuildingGraphics();
        }

        private static string GeometrySignature(RectTransform root)
        {
            return string.Join("|", root.GetComponentsInChildren<RectTransform>()
                .Where(value => value.gameObject.activeInHierarchy)
                .Select(value => value.GetInstanceID() + ":" + ScreenRect(value).ToString("F2")));
        }

        private static CaptureReport BuildReport(RectTransform root)
        {
            var report = new CaptureReport
            {
                scene = SceneManager.GetActiveScene().name, state = StateName,
                stateSource = stateIndex == 7
                    ? "实际Prefab诊断投影：主表四玩家同槽、Ⅱ五槽，每格数量1；不代表正式对局或提交结果。"
                    : stateIndex == 5 || stateIndex == 6
                        ? "实际Prefab诊断投影：四玩家同槽、Ⅱ五槽及合并数量3；不代表正式对局或提交结果。"
                        : "真实场景、正式公开供应及当前页面入口；未提交。",
                requestedSize = Sizes[sizeIndex], actualSize = new Vector2(Screen.width, Screen.height), safeArea = Screen.safeArea
            };
            foreach (var rect in root.GetComponentsInChildren<RectTransform>())
            {
                if (!rect.gameObject.activeInHierarchy) continue;
                report.objects.Add(new RectItem
                {
                    path = PathOf(rect), screenRect = ScreenRect(rect),
                    layoutDrivers = rect.GetComponents<Component>().Where(value =>
                        value is ILayoutController || value is ILayoutElement || value is ScrollRect)
                        .Select(value => value.GetType().Name).ToArray(),
                    hasClippingAncestor = rect.GetComponentInParent<RectMask2D>() != null || rect.GetComponentInParent<Mask>() != null
                });
            }
            return report;
        }

        private static Rect ScreenRect(RectTransform rect)
        {
            var canvas = rect.GetComponentInParent<Canvas>();
            var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
        private static string PathOf(Transform target)
        {
            var path = target.name;
            for (var parent = target.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }
        private static RectTransform Find(string name)
        {
            return UnityEngine.Object.FindObjectsOfType<RectTransform>(true)
                .FirstOrDefault(value => value.gameObject.scene.IsValid() && value.name == name);
        }
        private static object Read(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void InvokePrivate(object target, string name, params object[] args)
        {
            var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new MissingMethodException(target.GetType().Name, name);
            method.Invoke(target, args);
        }
        private static void Advance()
        {
            stableFrames = 0;
            stateApplied = false;
            lastGeometry = null;
            if (++stateIndex >= StateCount)
            {
                stateIndex = 0;
                if (++sizeIndex >= (baselineOnly ? 1 : Sizes.Length)) { Stop(0, "Dialogs 局内页面捕获完成。"); return; }
                SelectSize(Sizes[sizeIndex]);
            }
        }
        private static void Stop(int code, string message)
        {
            EditorApplication.update -= Tick;
            Debug.Log(message);
            EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
