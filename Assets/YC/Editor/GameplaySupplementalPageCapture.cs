using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Editor
{
    /// <summary>只读补充捕获：正式局内资产与明确诊断数据；不保存资产，不调用迁移，不提交命令。</summary>
    public static class GameplaySupplementalPageCapture
    {
        private static Vector2Int[] Sizes;
        private static RulebookViewerController rulebook;
        private static RulebookViewerView rulebookView;
        private static ZoomableImageViewerView rulebookImageView;
        private static ZoomableImageViewerController diagnosticImage;
        private static ZoomableImageViewerView diagnosticImageView;
        private static DispatchDecisionDialogView dispatch;
        private static FinalScoreController round;
        private static FinalScoreView roundView;
        private static bool finalScoreApplied;
        private static string sourceAsset;
        private static SourceProof sourceProof;
        private static ZoomableImageViewerController validatedImagePrefab;
        private static readonly string[] StateNames =
            { "Rulebook", "ZoomExpanded", "ZoomCollapsed", "Dispatch", "FinalSummary", "FinalDetails", "FinalDetailsScrolled" };
        private static bool scrollEndOnly;
        private static bool scrollEndApplied;
        private static int sizeIndex;
        private static int stateIndex;
        private static int stableFrames;
        private static bool stateApplied;
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
        private sealed class SourceProof
        {
            public string phase = "进入Play Mode前，读取实际SampleScene中的序列化引用与Prefab实例来源；不保存资产。";
            public string scene;
            public string settingsAsset;
            public string settingsSceneObject;
            public string rulebookAsset;
            public string rulebookSceneObject;
            public string nestedImageAsset;
            public string registeredImageAsset;
        }
        [Serializable]
        private sealed class CaptureReport
        {
            public string scene;
            public string state;
            public string stateSource;
            public bool componentDiagnostic = true;
            public string sourceAsset;
            public SourceProof validatedSceneSources;
            public bool commandSubmissionEnabled = false;
            public Rect contentScreenRect;
            public string limitation = "真实运行画面及几何记录；脚本打开页面不证明鼠标命中、联机或命令提交通过。";
            public Vector2 requestedSize;
            public Vector2 actualSize;
            public Rect safeArea;
            public List<RectItem> objects = new List<RectItem>();
            public List<ScrollItem> scrolls = new List<ScrollItem>();
        }
        [Serializable]
        private sealed class ScrollItem
        {
            public string path;
            public float normalizedPosition;
            public Rect viewportScreenRect;
            public Rect contentScreenRect;
        }
        [Serializable]
        private sealed class RectItem
        {
            public string path;
            public Rect screenRect;
            public string[] layoutDrivers;
            public bool hasClippingAncestor;
        }

        public static void Run() { Begin(0); }
        public static void RunBaseline() { Begin(1); }
        public static void RunNarrow() { Begin(2); }
        public static void RunIntegrationMatrix() { Begin(3); }
        public static void RunFinalDetailsEnd() { Begin(0, true); }
        public static void RunFinalDetailsEndBaseline() { Begin(1, true); }
        public static void RunFinalDetailsEndNarrow() { Begin(2, true); }

        private static void Begin(int sizeMode, bool onlyScrollEnd = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请在未运行游戏时启动局内补充诊断捕获。");
            Sizes = sizeMode == 3 ? new[] { new Vector2Int(1920, 1080), new Vector2Int(1920, 1200),
                new Vector2Int(2560, 1080), new Vector2Int(900, 600), new Vector2Int(1920, 1080) } :
                sizeMode == 1 ? new[] { new Vector2Int(1920, 1080) } :
                sizeMode == 2 ? new[] { new Vector2Int(900, 600) } :
                new[] { new Vector2Int(1920, 1080), new Vector2Int(900, 600) };
            rulebook = null; rulebookView = null; rulebookImageView = null;
            diagnosticImage = null; diagnosticImageView = null;
            dispatch = null; round = null; roundView = null; finalScoreApplied = false;
            evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("YC_UI_CAPTURE_OUTPUT") ?? "prompt/UI换新/执行记录/证据");
            Directory.CreateDirectory(evidence);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            ValidateSceneSourcesBeforePlay();
            PrepareGameView();
            scrollEndOnly = onlyScrollEnd;
            scrollEndApplied = false;
            sizeIndex = stableFrames = 0;
            stateIndex = scrollEndOnly ? 6 : 0;
            stateApplied = false;
            pendingPath = lastGeometry = null;
            started = EditorApplication.timeSinceStartup;
            SelectSize(Sizes[0]);
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void ValidateSceneSourcesBeforePlay()
        {
            var scene = SceneManager.GetActiveScene();
            var settings = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<GameSettingsMenuController>(true)).Single();
            var sceneRulebook = ReadReference<RulebookViewerController>(settings, "rulebookViewer");
            var sceneRulebookView = ReadReference<RulebookViewerView>(sceneRulebook, "view");
            validatedImagePrefab = ReadReference<ZoomableImageViewerController>(settings, "zoomableImageViewerPrefab");
            sourceProof = new SourceProof
            {
                scene = scene.path,
                settingsAsset = RequireEditorInstanceSource(settings, "InGameSettingsMenu.prefab"),
                settingsSceneObject = GlobalObjectId.GetGlobalObjectIdSlow(settings).ToString(),
                rulebookAsset = RequireEditorInstanceSource(sceneRulebook, "InGameRulebookViewer.prefab"),
                rulebookSceneObject = GlobalObjectId.GetGlobalObjectIdSlow(sceneRulebook).ToString(),
                nestedImageAsset = RequireEditorInstanceSource(sceneRulebookView.ImageViewer, "InGameZoomableImageViewer.prefab"),
                registeredImageAsset = AssetDatabase.GetAssetPath(validatedImagePrefab)
            };
            if (sourceProof.registeredImageAsset != "Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameZoomableImageViewer.prefab")
                throw new InvalidOperationException("场景注册的图片查看器不是局内来源：" + sourceProof.registeredImageAsset);
            File.WriteAllText(Path.Combine(evidence, "Dialogs-Secondary-Supplemental-SourceProof-" +
                Sizes[0].x + "x" + Sizes[0].y + ".json"), JsonUtility.ToJson(sourceProof, true),
                new System.Text.UTF8Encoding(false));
        }

        private static string RequireEditorInstanceSource(Component instance, string expectedFileName)
        {
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance.gameObject);
            var expected = "Assets/YC/Presentation/Prefabs/Gameplay/InGame/" + expectedFileName;
            if (path != expected)
                throw new InvalidOperationException("编辑态场景引用来源不符：" + instance.name + " → " + path + "；预期 " + expected);
            return path;
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
                if (stateIndex == 6 && !scrollEndApplied)
                {
                    ScrollDetailsToEnd();
                    scrollEndApplied = true;
                    stableFrames = 0;
                    lastGeometry = null;
                    return;
                }
                var report = BuildReport(root);
                pendingPath = Path.Combine(evidence, "Dialogs-Secondary-Supplemental-Diagnostic-" + StateName +
                    (sizeIndex == 4 ? "-Restored" : "") +
                    "-" + requested.x + "x" + requested.y + "-actual-" + Screen.width + "x" + Screen.height + ".png");
                File.WriteAllText(Path.ChangeExtension(pendingPath, ".json"), JsonUtility.ToJson(report, true),
                    new System.Text.UTF8Encoding(false));
                captureRequestedUtc = DateTime.UtcNow;
                ScreenCapture.CaptureScreenshot(pendingPath);
                Debug.Log("局内页面真实画面：" + pendingPath + "；状态：" + report.stateSource);
            }
            catch (Exception error) { Debug.LogException(error); Stop(1, "局内页面捕获失败。"); }
        }

        private static string StateName => StateNames[stateIndex];
        private static int StateCount => scrollEndOnly ? 7 : 6;

        private static T ReadReference<T>(UnityEngine.Object owner, string property) where T : UnityEngine.Object
        {
            var data = new SerializedObject(owner);
            var field = data.FindProperty(property);
            if (field == null || !(field.objectReferenceValue is T value))
                throw new InvalidOperationException("缺少诊断所需的正式引用：" + owner.name + "." + property);
            return value;
        }

        private static bool ApplyState()
        {
            var frame = GameplayHudFrame.Active;
            var settings = UnityEngine.Object.FindObjectOfType<GameSettingsMenuController>();
            if (frame == null || settings == null || !frame.TryValidateConfiguration(out waiting)) return false;
            if (sourceProof == null || settings.gameObject.scene.path != sourceProof.scene)
                throw new InvalidOperationException("运行场景没有匹配的进入Play前来源记录。");
            if (rulebook == null)
            {
                rulebook = ReadReference<RulebookViewerController>(settings, "rulebookViewer");
                rulebookView = ReadReference<RulebookViewerView>(rulebook, "view");
                if (!rulebookView.TryValidateConfiguration(out waiting))
                    throw new InvalidOperationException(waiting);
                rulebookImageView = ReadReference<ZoomableImageViewerView>(rulebookView.ImageViewer, "view");
                var imageSource = ReadReference<ZoomableImageViewerController>(settings, "zoomableImageViewerPrefab");
                if (imageSource != validatedImagePrefab)
                    throw new InvalidOperationException("进入Play后场景注册的图片来源发生改变，停止捕获。");
                diagnosticImage = ZoomableImageViewerController.InstantiateRegistered(frame.ContentRect,
                    "Component Diagnostic Image");
                if (diagnosticImage == null) throw new InvalidOperationException("局内图片查看器未注册。");
                diagnosticImageView = ReadReference<ZoomableImageViewerView>(diagnosticImage, "view");
                diagnosticImage.Configure("DialogsDiagnostic", "组件诊断：图片展开与收起", 1, rulebookView.GetPage);
                diagnosticImage.ConfigureActions(string.Empty, null);
                diagnosticImage.ConfigureReferenceCollapse("组件诊断：显示正式规则书第1页素材；无业务命令");
                round = UnityEngine.Object.FindObjectOfType<FinalScoreController>(true);
                if (round == null) throw new InvalidOperationException("实际场景没有 FinalScoreController。");
                roundView = ReadReference<FinalScoreView>(round, "view");
                if (!roundView.TryValidateConfiguration(out waiting))
                    throw new InvalidOperationException(waiting);
            }
            settings.Close();
            rulebook.Close();
            diagnosticImage.Close();
            if (dispatch != null) dispatch.gameObject.SetActive(false);
            roundView.GameOverOverlay.SetActive(false);
            if (stateIndex == 0)
            {
                sourceAsset = sourceProof.rulebookAsset;
                rulebook.Open();
            }
            else if (stateIndex == 1 || stateIndex == 2)
            {
                sourceAsset = sourceProof.registeredImageAsset;
                diagnosticImage.SetCollapsed(false);
                diagnosticImage.Open();
                diagnosticImage.SetCollapsed(stateIndex == 2);
            }
            else if (stateIndex == 3)
            {
                var registry = UnityEngine.Object.FindObjectOfType<GameplayDialogRegistry>();
                if (registry == null) { waiting = "等待场景正式 Registry"; return false; }
                sourceAsset = AssetDatabase.GetAssetPath(registry.DispatchDecisionPrefab);
                if (dispatch == null) dispatch = registry.InstantiateDispatchDecision(frame.ContentRect);
                if (dispatch == null || !dispatch.TryValidateConfiguration(out waiting)) return false;
                dispatch.Bind("组件诊断：调度决策", "这是正式调度窗口的布局诊断。检查正文换行、两项操作及窗口边界；不会派遣单位，也不会提交命令。",
                    "继续调度", null, "结束调度", null);
                dispatch.gameObject.SetActive(true);
                frame.ShowPage(dispatch.gameObject, false);
            }
            else
            {
                sourceAsset = "Assets/YC/Presentation/Prefabs/FinalScore/FinalScore.prefab（SampleScene有效实例）";
                if (!finalScoreApplied)
                {
                    // 独立诊断 State 只传给既有显示控制器，从不替换或修改 session.State。
                    round.RefreshFromState(CreateDiagnosticFinalState());
                    finalScoreApplied = true;
                }
                roundView.GameOverOverlay.SetActive(true);
                frame.ShowPage(roundView.GameOverOverlay, false);
                if (stateIndex == 4) round.CloseFinalScoreDetails();
                else round.OpenFinalScoreDetails();
            }
            stateApplied = true;
            waiting = "等待当前正式资产的纹理与布局稳定";
            return true;
        }

        private static GameState CreateDiagnosticFinalState()
        {
            var state = new GameState
            {
                Phase = GamePhase.FinalScoring,
                FinalScoring = new FinalScoringState
                {
                    IsResolved = true,
                    TiebreakSummary = "组件诊断数据：四名玩家的计分展示；不是实际结算结果。"
                }
            };
            var colors = new[] { PlayerColor.Red, PlayerColor.Blue, PlayerColor.Green, PlayerColor.Yellow };
            for (var id = 1; id <= 4; id++)
            {
                state.Players.Add(new PlayerState { PlayerId = id, Name = "诊断玩家" + id, Color = colors[id - 1] });
                state.FinalScoring.PlayerScores.Add(new FinalPlayerScoreState
                {
                    PlayerId = id, BaseScore = 2 * id + 1, FacilityScore = 2 * id + 2,
                    CityStyleScore = 2, RegionScore = id + 2, ResourceScore = 2 * id - 1,
                    TotalScore = 7 * id + 6
                });
            }
            state.FinalScoring.WinnerPlayerIds.Add(4);
            return state;
        }

        private static void ScrollDetailsToEnd()
        {
            var players = roundView.DetailPlayerRowsContainer.GetComponentInParent<ScrollRect>();
            var formula = roundView.DetailChartsContainer.GetComponentsInChildren<Text>()
                .Single(value => value.name.StartsWith("Final Score Detail Formula P", StringComparison.Ordinal));
            var formulaScroll = formula.GetComponentInParent<ScrollRect>();
            if (players == null || formulaScroll == null)
                throw new InvalidOperationException("终局末端捕获缺少正式玩家或公式滚动区域。");
            foreach (var scroll in new[] { players, formulaScroll })
            {
                var data = new PointerEventData(EventSystem.current)
                {
                    position = ScreenRect(scroll.viewport).center,
                    scrollDelta = new Vector2(0, -10000)
                };
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(data, hits);
                if (hits.Count == 0 || ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, data,
                    ExecuteEvents.scrollHandler) != scroll.gameObject)
                    throw new InvalidOperationException("实际首命中没有路由到目标滚动区域：" + scroll.name);
            }
        }

        private static bool TryReady(out RectTransform root)
        {
            root = stateIndex == 0 ? rulebookImageView.PanelTransform :
                stateIndex <= 2 ? diagnosticImageView.PanelTransform :
                stateIndex == 3 ? dispatch.Panel :
                roundView.GameOverOverlay.GetComponentsInChildren<RectTransform>(true)
                    .Single(value => value.name == "Game Over Dialog");
            if (root == null || !root.gameObject.activeInHierarchy || root.rect.width < 1 || root.rect.height < 1)
            { waiting = "页面未激活或实际尺寸无效"; return false; }
            if (stateIndex <= 2)
            {
                var view = stateIndex == 0 ? rulebookImageView : diagnosticImageView;
                if (view.Image.texture == null || (stateIndex != 2 && !view.ExpandedContentObject.activeInHierarchy))
                { waiting = "等待正式图片素材与展开内容"; return false; }
            }
            if (stateIndex >= 4 && roundView.RankingRowsContainer.GetComponentsInChildren<FinalScoreRankingRowView>().Length < 4)
            {
                // 明细模式的摘要列表会隐藏，仅摘要状态要求活动四行。
                if (stateIndex == 4) { waiting = "等待四个诊断计分行"; return false; }
            }
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
            if (stateIndex == 6)
            {
                var endScrolls = new[]
                {
                    roundView.DetailPlayerRowsContainer.GetComponentInParent<ScrollRect>(),
                    roundView.DetailChartsContainer.GetComponentsInChildren<Text>()
                        .Single(value => value.name.StartsWith("Final Score Detail Formula P", StringComparison.Ordinal))
                        .GetComponentInParent<ScrollRect>()
                };
                if (endScrolls.Any(scroll => scroll == null ||
                    (scroll.content.rect.height > scroll.viewport.rect.height + .1f && scroll.verticalNormalizedPosition > .001f)))
                    throw new InvalidOperationException("终局末端尚未实际滚到末尾，拒绝写入末端证据。");
            }
            var report = new CaptureReport
            {
                scene = SceneManager.GetActiveScene().name, state = StateName,
                sourceAsset = GameplaySupplementalPageCapture.sourceAsset,
                validatedSceneSources = sourceProof,
                stateSource = stateIndex == 0
                    ? "实际局内规则书第1页；正式控制器不支持收起，此状态保持其正常行为。"
                    : stateIndex <= 2
                        ? "局内实际注册图片查看器，使用正式规则书图片素材；展开/收起组件诊断，无业务回调。"
                        : stateIndex == 3
                            ? "正式 Registry 的调度窗口，诊断文案，两个操作回调均为空；未派遣或提交命令。"
                            : stateIndex == 6
                                ? "实际终局View独立四玩家诊断；以EventSystem实际首命中路由滚轮事件到玩家及公式末端，未模拟物理鼠标，未提交命令。"
                                : "实际场景终局View，独立四玩家诊断GameState；未改session、未实际结束游戏、未提交命令。",
                contentScreenRect = ScreenRect(GameplayHudFrame.Active.ContentRect),
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
            foreach (var scroll in root.GetComponentsInChildren<ScrollRect>())
                if (scroll.viewport != null && scroll.content != null)
                    report.scrolls.Add(new ScrollItem
                    {
                        path = PathOf(scroll.transform), normalizedPosition = scroll.verticalNormalizedPosition,
                        viewportScreenRect = ScreenRect(scroll.viewport), contentScreenRect = ScreenRect(scroll.content)
                    });
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
        private static void Advance()
        {
            stableFrames = 0;
            stateApplied = false;
            scrollEndApplied = false;
            lastGeometry = null;
            if (++stateIndex >= StateCount)
            {
                stateIndex = scrollEndOnly ? 6 : 0;
                if (++sizeIndex >= Sizes.Length) { Stop(0, "Dialogs 局内补充诊断捕获完成。"); return; }
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
