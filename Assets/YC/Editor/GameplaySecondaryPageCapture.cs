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

namespace YC.Presentation.Editor
{
    /// <summary>手动只读的真实场景捕获：不保存资产、不调用任何迁移/生成/Rebuild 入口。</summary>
    public static class GameplaySecondaryPageCapture
    {
        private static Vector2Int[] Sizes =
        {
            new Vector2Int(1920, 1080), new Vector2Int(1024, 768), new Vector2Int(1920, 1080)
        };
        private static int sizeIndex;
        private static int stateIndex;
        private static int stableFrames;
        private static bool stateApplied;
        private static bool baselineOnly;
        private static bool informationOnly;
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
        public static void RunInformationBaseline() { Begin(true, true); }
        public static void RunInformationMatrix() { Begin(false, true); }

        private static void Begin(bool singleSize = false, bool information = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请在未运行游戏时手动启动 Dialogs 局内页面捕获。");
            baselineOnly = singleSize;
            informationOnly = information;
            if (information && !singleSize) Sizes = new[] { new Vector2Int(1920, 1080),
                new Vector2Int(1920, 1200), new Vector2Int(2560, 1080), new Vector2Int(900, 600), new Vector2Int(1920, 1080) };
            evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("YC_UI_CAPTURE_OUTPUT") ?? "prompt/UI换新/执行记录/证据");
            Directory.CreateDirectory(evidence);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            PrepareGameView();
            sizeIndex = stateIndex = stableFrames = 0;
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
                var restored = sizeIndex == Sizes.Length - 1 && sizeIndex > 0 ? "-Restored" : "";
                pendingPath = Path.Combine(evidence, "Dialogs-Secondary-Live-Gameplay" +
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

        private static string StateName => new[] { "Settings", "ActionLog", "CityStyle" }[stateIndex];
        private static int StateCount => informationOnly ? 2 : 3;

        private static bool ApplyState()
        {
            var settings = UnityEngine.Object.FindObjectOfType<GameSettingsMenuController>();
            var frame = UnityEngine.Object.FindObjectOfType<GameplayHudFrame>();
            var controller = UnityEngine.Object.FindObjectOfType<MobileCityInteractionController>();
            if (settings == null || frame == null || controller == null ||
                !frame.TryValidateConfiguration(out waiting)) return false;
            if (stateIndex == 0) settings.Open();
            else if (stateIndex == 1)
            {
                settings.Close();
                var log = UnityEngine.Object.FindObjectOfType<ActionLogViewerController>(true);
                if (log == null) { waiting = "等待正式日志查看器"; return false; }
                log.Open();
            }
            else
            {
                UnityEngine.Object.FindObjectOfType<ActionLogViewerController>(true)?.Close();
                var tabs = UnityEngine.Object.FindObjectOfType<UiMainActionTabs>();
                var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>();
                if (tabs == null || hud == null) { waiting = "等待正式城市样式入口"; return false; }
                tabs.Select(2);
                hud.ActionPanelView.DeclareCityStyleButton.onClick.Invoke();
            }
            stateApplied = true;
            waiting = "等待当前页面素材和布局稳定";
            return true;
        }

        private static bool TryReady(out RectTransform root)
        {
            root = null;
            if (stateIndex == 0) root = Find("Settings Panel");
            else if (stateIndex == 1) root = Find("Action Log Panel");
            else
            {
                var view = UnityEngine.Object.FindObjectOfType<CityStyleDeclarationPreviewView>();
                if (view == null || !view.TryValidateConfiguration(out waiting) ||
                    view.CityStyleCardImage.texture == null || view.CityBoardImage.texture == null) return false;
                root = view.Panel;
            }
            if (root == null || !root.gameObject.activeInHierarchy || root.rect.width < 1 || root.rect.height < 1)
            { waiting = "当前状态的页面根尚未激活或尺寸无效"; return false; }
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
                stateSource = stateIndex == 2
                        ? "真实场景当前公开供应中的城市样式，调用现有正式点击处理入口；未宣告或支付。"
                        : "实际场景与源资产；调用既有页面按钮/控制器，仅查看、不提交命令。",
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
        private static void InvokePrivate(object target, string name, params object[] args)
        {
            var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
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
