using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation.Editor
{
    /// <summary>手动打开两张实际场景的 Play 画面并截取多尺寸证据；不保存场景或预制体。</summary>
    public static class Ui003VisualCapture
    {
        private static readonly Vector2Int[] Sizes =
        {
            new Vector2Int(1920, 1080), new Vector2Int(1280, 720),
            new Vector2Int(1024, 768), new Vector2Int(2560, 1080),
            new Vector2Int(900, 600)
        };

        private static int sceneIndex;
        private static int sizeIndex;
        private static int frames;
        private static int stableFrames;
        private static int compactPane;
        private static int finalFrames;
        private static bool finishing;
        private static bool capturePending;
        private static bool modalCapture;
        private static bool diagnosticOnly;
        private static double started;
        private static object gameViewGroup;
        private static EditorWindow gameView;
        private static PropertyInfo selectedSizeIndex;
        private static string evidence;
        private static string lastGeometry;
        private static string readinessIssue;
        private static string lastLoggedIssue;
        private static string capturePrefix = "UI003-LayerFix-";

        public static void Run()
        {
            diagnosticOnly = false;
            capturePrefix = "UI003-LayerFix-";
            Begin();
        }

        public static void RunUi004()
        {
            diagnosticOnly = false;
            capturePrefix = "UI004-";
            Begin();
        }

        public static void RunUi005()
        {
            diagnosticOnly = false;
            capturePrefix = "UI005-";
            Begin();
        }

        public static void RunUi005Actions()
        {
            diagnosticOnly = true;
            capturePrefix = "UI005-Actions-";
            Begin();
        }

        public static void RunUi005ReadyActions()
        {
            diagnosticOnly = true;
            capturePrefix = "UI005-ReadyActionsClean-";
            Begin();
        }

        public static void RunDiagnostic()
        {
            diagnosticOnly = true;
            capturePrefix = "UI003-Diagnostic-LayerFix-";
            Begin();
        }

        private static void Begin()
        {
            evidence = Path.GetFullPath("prompt/UI换新/执行记录/证据");
            Directory.CreateDirectory(evidence);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            PrepareGameView();
            SelectSize(0);
            sceneIndex = 0;
            sizeIndex = 0;
            frames = 0;
            stableFrames = 0;
            compactPane = 1;
            lastGeometry = null;
            readinessIssue = "等待场景与资源加载";
            lastLoggedIssue = null;
            finalFrames = 0;
            finishing = false;
            capturePending = false;
            modalCapture = false;
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void PrepareGameView()
        {
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

        private static void SelectSize(int index)
        {
            var assembly = typeof(EditorApplication).Assembly;
            var sizeType = assembly.GetType("UnityEditor.GameViewSizeType");
            var value = Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"),
                Enum.Parse(sizeType, "FixedResolution"), Sizes[index].x, Sizes[index].y,
                "UI003 " + Sizes[index].x + "x" + Sizes[index].y);
            gameViewGroup.GetType().GetMethod("AddCustomSize").Invoke(gameViewGroup, new[] { value });
            var selected = (int)gameViewGroup.GetType().GetMethod("GetTotalCount").Invoke(gameViewGroup, null) - 1;
            selectedSizeIndex.SetValue(gameView, selected);
            gameView.Repaint();
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup - started > 420)
            {
                Stop(1, "UI003 画面捕获超时：" + readinessIssue);
                return;
            }
            if (!EditorApplication.isPlaying) return;
            if (finishing)
            {
                if (++finalFrames > 60) Stop(0, "UI003 画面捕获完成。");
                return;
            }
            if (++frames < (capturePending ? 12 : 8)) return;
            frames = 0;
            if (capturePending)
            {
                capturePending = false;
                Advance();
                return;
            }
            var scene = sceneIndex == 0 ? "SampleScene" : "ThreePlayerScene";
            var requested = Sizes[sizeIndex];
            if (!TryVisualReadiness(scene, requested, out readinessIssue))
            {
                stableFrames = 0;
                if (readinessIssue != lastLoggedIssue)
                {
                    Debug.Log("UI003 WAIT " + scene + " " + requested + " " + readinessIssue);
                    lastLoggedIssue = readinessIssue;
                }
                return;
            }
            lastLoggedIssue = null;
            if (++stableFrames < 3) return;
            try { VerifyLiveInput(scene, requested); }
            catch (Exception error)
            {
                Debug.LogException(error);
                Stop(1, "UI003 实际尺寸输入核验失败。");
                return;
            }
            var output = Path.Combine(evidence, capturePrefix +
                scene + (modalCapture ? "-Settings" : "") +
                (diagnosticOnly && sizeIndex == 2 && compactPane != 1
                    ? compactPane == 0 ? "-Left" : "-Right" : "") + "-" +
                requested.x + "x" + requested.y + "-actual-" + Screen.width + "x" + Screen.height + ".png");
            ScreenCapture.CaptureScreenshot(output);
            Debug.Log("UI003 VISUAL " + output);
            capturePending = true;
        }

        private static bool TryVisualReadiness(string scene, Vector2Int requested, out string reason)
        {
            reason = string.Empty;
            if (SceneManager.GetActiveScene().name != scene || Screen.width != requested.x ||
                Screen.height != requested.y)
            {
                reason = scene + " 场景或实际渲染尺寸尚未就绪";
                return false;
            }
            var frame = UnityEngine.Object.FindObjectOfType<GameplayHudFrame>();
            var mapDisplay = UnityEngine.Object.FindObjectOfType<MapDisplayController>();
            var camera = Camera.main;
            if (frame == null || mapDisplay == null || camera == null ||
                !frame.TryValidateConfiguration(out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "HUD、地图控制器或相机尚未就绪";
                return false;
            }
            var hud = frame.GetComponentInParent<GameplayInteractionHudView>();
            var surface = hud == null ? null : FindChild(hud.transform, "UI003 Main Surface");
            if (surface == null || !surface.gameObject.activeInHierarchy)
            {
                reason = "新版主界面根节点未激活";
                return false;
            }
            foreach (var name in new[] { "Outer Frame", "Left Ground", "Right Ground",
                         "Lower Ground", "Top Seam", "Map Frame", "Map Backdrop Left",
                         "Map Backdrop Right", "Map Backdrop Top", "Map Backdrop Bottom" })
            {
                var target = FindChild(surface, name);
                var image = target == null ? null : target.GetComponent<Image>();
                if (image == null || image.sprite == null || !target.gameObject.activeInHierarchy)
                {
                    reason = "缺少主界面素材：" + name;
                    return false;
                }
            }
            foreach (var name in new[] { "Brand Mark", "Round Plaque", "Current Action Flag",
                         "Current Action Icon", "Red Zone Plaque", "Red Zone Icon", "Fold Icon",
                         "Settings Icon",
                         "Undo Icon" })
            {
                var target = FindChild(frame.transform, name);
                var image = target == null ? null : target.GetComponent<Image>();
                if (image == null || image.sprite == null || !target.gameObject.activeInHierarchy)
                {
                    reason = "缺少常驻栏素材：" + name;
                    return false;
                }
            }
            foreach (var name in new[] { "Opponent Seat 1", "Opponent Seat 2", "Opponent Seat 3",
                         "City Region", "Map Region", "Self Summary Region", "Entrepreneurs Region",
                         "Cooperation Region", "Discard Region", "Face Down Region",
                         "Action Tabs", "Action Region" })
            {
                var target = FindChild(surface, name);
                if (target == null || !target.gameObject.activeInHierarchy)
                {
                    reason = "缺少主界面区域：" + name;
                    return false;
                }
            }
            // 结算区允许由资产作者放在 HUD 内的任意区域，不约束父节点或位置。
            foreach (var name in new[] { "Resolution Summary Slot", "Resolution Slot Frame" })
            {
                var target = FindChild(hud.transform, name);
                var image = target == null ? null : target.GetComponent<Image>();
                if (image == null || image.sprite == null || !target.gameObject.activeInHierarchy)
                {
                    reason = "结算区显示引用尚未就绪：" + name;
                    return false;
                }
            }
            var hand = hud.CharacterHandPanel;
            if (hand == null || !hand.gameObject.activeInHierarchy ||
                !hand.TryValidateConfiguration(out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "手牌面板引用尚未就绪";
                return false;
            }
            var cards = hand.View.HandCardsRoot.GetComponentsInChildren<CharacterHandCardView>();
            if (cards.Length != hand.OrderedHand.Count)
            {
                reason = "实际手牌数量与当前手牌数据不一致";
                return false;
            }
            var city = FindChild(surface, "City Board Artwork");
            var cityImage = city == null ? null : city.GetComponent<RawImage>();
            if (cityImage == null || cityImage.texture == null || !city.gameObject.activeInHierarchy)
            {
                reason = "城市板贴图尚未加载";
                return false;
            }
            var rendererField = typeof(MapDisplayController).GetField("mapRenderer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var mapRenderer = rendererField.GetValue(mapDisplay) as SpriteRenderer;
            if (mapRenderer == null || mapRenderer.sprite == null)
            {
                reason = "实际地图尚未加载";
                return false;
            }
            var mapRegion = FindChild(surface, "Map Region") as RectTransform;
            var corners = new Vector3[4];
            mapRegion.GetWorldCorners(corners);
            var lowerLeft = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            var upperRight = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            var view = camera.pixelRect;
            if (Mathf.Abs(view.xMin - lowerLeft.x) > 2f ||
                Mathf.Abs(view.xMax - upperRight.x) > 2f ||
                Mathf.Abs(view.yMin - lowerLeft.y) > 2f ||
                Mathf.Abs(view.yMax - upperRight.y) > 2f)
            {
                reason = "地图相机视口与中央区域尚未对齐";
                return false;
            }
            var outerFrame = surface.Find("Outer Frame");
            var mainRegions = surface.Find("Main Regions");
            if (outerFrame == null || mainRegions == null ||
                outerFrame.GetSiblingIndex() >= mainRegions.GetSiblingIndex())
            {
                reason = "主外框绘制在模块边框上方";
                return false;
            }
            foreach (var label in surface.GetComponentsInChildren<Text>(true))
            {
                if (label.gameObject.activeInHierarchy && label.font == null)
                {
                    reason = "缺少字体：" + label.name;
                    return false;
                }
            }
            Canvas.ForceUpdateCanvases();
            var geometry = lowerLeft.ToString("F2") + upperRight.ToString("F2") +
                           view.ToString("F2") + cityImage.rectTransform.rect.ToString("F2");
            if (geometry != lastGeometry)
            {
                lastGeometry = geometry;
                reason = "布局仍在稳定中";
                return false;
            }
            return true;
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindChild(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void VerifyLiveInput(string scene, Vector2Int requested)
        {
            if (Screen.width != requested.x || Screen.height != requested.y)
                throw new InvalidOperationException(scene + " 实际渲染尺寸与请求不符。");
            var frame = UnityEngine.Object.FindObjectOfType<GameplayHudFrame>();
            var eventSystem = EventSystem.current;
            if (frame == null || eventSystem == null ||
                FirstHit(frame.SettingsButton.transform as RectTransform, eventSystem) != frame.SettingsButton.gameObject ||
                FirstHit(frame.EndActionButton.transform as RectTransform, eventSystem) != frame.EndActionButton.gameObject)
                throw new InvalidOperationException(scene + " 常驻栏点击命中失败。");
            var hud = frame.GetComponentInParent<GameplayInteractionHudView>();
            var compactNav = hud == null ? null : FindChild(hud.transform, "Compact Region Navigation");
            if (compactNav != null && compactNav.gameObject.activeInHierarchy)
            {
                for (var i = 0; i < 3; i++)
                {
                    var target = FindChild(compactNav, "Region Button " + i);
                    if (target == null || FirstHit(target as RectTransform, eventSystem) != target.gameObject)
                        throw new InvalidOperationException(scene + " 紧凑分区导航点击命中失败：" + i);
                }
            }
            var top = new Vector3[4];
            var bottom = new Vector3[4];
            var content = new Vector3[4];
            frame.TopBar.GetWorldCorners(top);
            frame.BottomBar.GetWorldCorners(bottom);
            frame.ContentRect.GetWorldCorners(content);
            if (content[1].y <= bottom[2].y || content[2].y >= top[0].y)
                throw new InvalidOperationException(scene + " 内容区与常驻栏边界重叠。");
            var mapDisplay = UnityEngine.Object.FindObjectOfType<MapDisplayController>();
            var camera = Camera.main;
            var zoomField = typeof(MapDisplayController).GetField("currentZoom", BindingFlags.Instance | BindingFlags.NonPublic);
            var framedField = typeof(MapDisplayController).GetField("framedViewportZoom", BindingFlags.Instance | BindingFlags.NonPublic);
            var rendererField = typeof(MapDisplayController).GetField("mapRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
            var mapRenderer = mapDisplay == null ? null : rendererField.GetValue(mapDisplay) as SpriteRenderer;
            var mapBounds = mapRenderer == null || camera == null ? "none" : ProjectedBounds(camera, mapRenderer).ToString();
            Debug.Log("UI003 MAP DIAGNOSTIC controller=" + (mapDisplay != null) +
                      " zoom=" + (mapDisplay == null ? "none" : zoomField.GetValue(mapDisplay).ToString()) +
                      " framed=" + (mapDisplay == null ? "none" : framedField.GetValue(mapDisplay).ToString()) +
                      " cameraRect=" + (camera == null ? "none" : camera.rect.ToString()) +
                      " pixelRect=" + (camera == null ? "none" : camera.pixelRect.ToString()) +
                      " mapBounds=" + mapBounds);
            Debug.Log("UI003 VERIFY " + scene + (modalCapture ? " settings" : "") +
                      " " + Screen.width + "x" + Screen.height + " input=ok viewport=ok layers=ok");
        }

        private static Rect ProjectedBounds(Camera camera, SpriteRenderer renderer)
        {
            var bounds = renderer.sprite.bounds;
            var min = bounds.min;
            var max = bounds.max;
            var xMin = float.MaxValue;
            var yMin = float.MaxValue;
            var xMax = float.MinValue;
            var yMax = float.MinValue;
            foreach (var x in new[] { min.x, max.x })
            foreach (var y in new[] { min.y, max.y })
            {
                var point = camera.WorldToScreenPoint(renderer.transform.TransformPoint(new Vector3(x, y, bounds.center.z)));
                xMin = Mathf.Min(xMin, point.x);
                yMin = Mathf.Min(yMin, point.y);
                xMax = Mathf.Max(xMax, point.x);
                yMax = Mathf.Max(yMax, point.y);
            }
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static GameObject FirstHit(RectTransform rect, EventSystem eventSystem)
        {
            var position = RectTransformUtility.WorldToScreenPoint(null,
                rect.TransformPoint(rect.rect.center));
            var hits = new List<RaycastResult>();
            eventSystem.RaycastAll(new PointerEventData(eventSystem) { position = position }, hits);
            return hits.Count == 0 ? null : hits[0].gameObject;
        }

        private static void Advance()
        {
            stableFrames = 0;
            lastGeometry = null;
            if (diagnosticOnly)
            {
                if (sizeIndex == 0)
                {
                    sizeIndex = 2;
                    SelectSize(sizeIndex);
                }
                else if (sizeIndex == 2)
                {
                    if (compactPane == 1)
                    {
                        SelectCompactPane(0);
                        compactPane = 0;
                    }
                    else if (compactPane == 0)
                    {
                        SelectCompactPane(2);
                        compactPane = 2;
                    }
                    else
                    {
                        compactPane = 1;
                        sizeIndex = 4;
                        SelectSize(sizeIndex);
                    }
                }
                else finishing = true;
                return;
            }
            if (modalCapture)
            {
                modalCapture = false;
                sceneIndex = 1;
                sizeIndex = 0;
                SelectSize(0);
                SceneManager.LoadScene("ThreePlayerScene", LoadSceneMode.Single);
                return;
            }
            sizeIndex++;
            if (sizeIndex < Sizes.Length)
            {
                SelectSize(sizeIndex);
                return;
            }
            if (sceneIndex == 0)
            {
                modalCapture = true;
                sizeIndex = 0;
                SelectSize(0);
                var settings = UnityEngine.Object.FindObjectOfType<GameSettingsMenuController>();
                if (settings != null) settings.Open();
                return;
            }
            finishing = true;
        }

        private static void SelectCompactPane(int index)
        {
            var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>();
            var button = hud == null ? null : FindChild(hud.transform, "Region Button " + index);
            if (button == null) throw new InvalidOperationException("缺少紧凑分区导航按钮 " + index);
            button.GetComponent<Button>().onClick.Invoke();
        }

        private static void Stop(int code, string message)
        {
            EditorApplication.update -= Tick;
            if (code == 0) Debug.Log(message);
            else Debug.LogError(message);
            EditorApplication.Exit(code);
        }
    }
}
