using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Editor
{
    /// <summary>手动打开两张实际场景的 Play 画面并截取多尺寸证据；不保存场景或预制体。</summary>
    public static class GameplayHudCapture
    {
        private static readonly Vector2Int[] Sizes =
        {
            new Vector2Int(1920, 1080), new Vector2Int(1280, 720),
            new Vector2Int(1024, 768), new Vector2Int(2560, 1080),
            new Vector2Int(900, 600)
        };
        private static Vector2Int[] activeSizes = Sizes;
        private static bool actionsMatrix;
        private static int actionsState;
        private static bool actionsStateApplied;
        private static string[] ActionsStates =
            { "Main", "MainEnd", "Quick", "QuickEnd", "City", "CityEnd" };

        private static int sceneIndex;
        private static int sizeIndex;
        private static int frames;
        private static int stableFrames;
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
        private static string capturePrefix = "Hud-LayerFix-";
        private static bool componentOpponent;
        private static bool componentExpanded;
        private static GameState componentState;
        private static GameStateView componentView;
        private static bool dialogsBaseline;
        private static int dialogsComponentMode = -1;
        private static EffectDialogShellView dialogsComponent;
        private static bool dialogsComponentMatrix;
        private static int dialogsComponentStep;
        private static bool dialogsFinalMatrix;
        private static bool dialogsFinalRestored;

        public static void RunFoundationReview()
        {
            dialogsBaseline = true;
            diagnosticOnly = false;
            capturePrefix = "GameplayFoundation-RootFixed-";
            activeSizes = new[] { new Vector2Int(1920, 1080) };
            Begin();
        }

        public static void RunFoundationMatrix()
        {
            RunActionsRightColumnMatrix();
            capturePrefix = "GameplayFoundation-Final-";
            activeSizes = new[] { new Vector2Int(1920, 1080), new Vector2Int(1920, 1200),
                new Vector2Int(2560, 1080), new Vector2Int(1920, 1080), new Vector2Int(900, 600) };
        }

        public static void RunFoundationBaseline()
        {
            dialogsBaseline = true;
            diagnosticOnly = false;
            capturePrefix = "GameplayFoundation-Before-";
            activeSizes = new[] { new Vector2Int(1920, 1080) };
            Begin();
        }

        public static void RunDialogsFinal()
        {
            diagnosticOnly = false;
            dialogsFinalMatrix = true;
            dialogsFinalRestored = false;
            capturePrefix = "Dialogs-Final-";
            activeSizes = Sizes;
            Begin();
        }

        public static void RunActionsRightColumnMatrix()
        {
            actionsMatrix = true;
            actionsState = 0;
            actionsStateApplied = false;
            diagnosticOnly = false;
            dialogsBaseline = true;
            dialogsFinalMatrix = true;
            dialogsFinalRestored = false;
            capturePrefix = "Actions-RightColumn-Verified-";
            activeSizes = new[]
            {
                new Vector2Int(1920, 1080), new Vector2Int(2560, 1080),
                new Vector2Int(1920, 1080), new Vector2Int(1920, 1440),
                new Vector2Int(900, 600)
            };
            Begin();
        }

        public static void RunActionsRightColumnReview()
        {
            ActionsStates = new[] { "Main" };
            RunActionsRightColumnMatrix();
            capturePrefix = "Actions-RightColumn-Corrected-";
            activeSizes = new[] { new Vector2Int(1920, 1080), new Vector2Int(2560, 1080) };
        }


        public static void RunDialogsExpanded()
        {
            componentOpponent = true;
            diagnosticOnly = true;
            capturePrefix = "Dialogs-Expanded-";
            Begin();
        }

        public static void RunDialogsComponentMatrix()
        {
            dialogsComponentMatrix = true;
            RunDialogsComponents();
        }

        public static void RunDialogsComponents()
        {
            dialogsBaseline = true;
            diagnosticOnly = false;
            dialogsComponentMode = 0;
            capturePrefix = "Dialogs-Component-Cards-";
            Begin();
        }

        public static void RunDialogsBaseline()
        {
            dialogsBaseline = true;
            diagnosticOnly = false;
            capturePrefix = "Dialogs-Before-";
            Begin();
        }

        public static void RunDialogsAfter()
        {
            dialogsBaseline = true;
            diagnosticOnly = false;
            capturePrefix = "Dialogs-After-";
            Begin();
        }

        public static void RunDialogsNarrow()
        {
            dialogsBaseline = true;
            diagnosticOnly = false;
            capturePrefix = "Dialogs-Narrow-";
            Begin();
            sizeIndex = 2;
            SelectSize(sizeIndex);
        }

        public static void Run()
        {
            diagnosticOnly = false;
            capturePrefix = "Hud-LayerFix-";
            Begin();
        }

        public static void RunMapViewportReview()
        {
            diagnosticOnly = false;
            capturePrefix = "MapViewport-";
            Begin();
        }

        public static void RunMainHud()
        {
            diagnosticOnly = false;
            capturePrefix = "MainHud-";
            Begin();
        }

        public static void RunEffects()
        {
            diagnosticOnly = false;
            capturePrefix = "Effects-";
            Begin();
        }

        public static void RunEffectsQuick()
        {
            componentOpponent = false;
            diagnosticOnly = true;
            capturePrefix = "Effects-Quick-";
            Begin();
        }

        public static void RunEffectsOpponentComponent()
        {
            componentOpponent = true;
            diagnosticOnly = true;
            capturePrefix = "Effects-OpponentComponent-";
            Begin();
        }

        public static void RunMainHudActions()
        {
            diagnosticOnly = true;
            capturePrefix = "MainHud-Actions-";
            Begin();
        }

        public static void RunMainHudReadyActions()
        {
            diagnosticOnly = true;
            capturePrefix = "MainHud-ReadyActionsClean-";
            Begin();
        }

        public static void RunDiagnostic()
        {
            diagnosticOnly = true;
            capturePrefix = "Hud-Diagnostic-LayerFix-";
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
            lastGeometry = null;
            readinessIssue = "等待场景与资源加载";
            lastLoggedIssue = null;
            finalFrames = 0;
            finishing = false;
            capturePending = false;
            modalCapture = false;
            componentExpanded = false;
            componentState = null;
            componentView = null;
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
                Enum.Parse(sizeType, "FixedResolution"), activeSizes[index].x, activeSizes[index].y,
                "" + activeSizes[index].x + "x" + activeSizes[index].y);
            gameViewGroup.GetType().GetMethod("AddCustomSize").Invoke(gameViewGroup, new[] { value });
            var selected = (int)gameViewGroup.GetType().GetMethod("GetTotalCount").Invoke(gameViewGroup, null) - 1;
            selectedSizeIndex.SetValue(gameView, selected);
            gameView.Repaint();
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup - started > 420)
            {
                Stop(1, "画面捕获超时：" + readinessIssue);
                return;
            }
            if (!EditorApplication.isPlaying) return;
            if (finishing)
            {
                if (++finalFrames > 60) Stop(0, "画面捕获完成。");
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
            var requested = activeSizes[sizeIndex];
            if (actionsMatrix && !PrepareActionsState()) return;
            if (!TryVisualReadiness(scene, requested, out readinessIssue))
            {
                stableFrames = 0;
                if (readinessIssue != lastLoggedIssue)
                {
                    Debug.Log("WAIT " + scene + " " + requested + " " + readinessIssue);
                    lastLoggedIssue = readinessIssue;
                }
                return;
            }
            lastLoggedIssue = null;
            if (++stableFrames < 3) return;
            try
            {
                VerifyLiveInput(scene, requested);
                if (actionsMatrix) VerifyActionsColumnBounds();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Stop(1, "实际尺寸输入核验失败。");
                return;
            }
            var output = Path.Combine(evidence, capturePrefix +
                scene + (actionsMatrix ? "-" + ActionsStates[actionsState] + "-Step" + sizeIndex : "") +
                (modalCapture ? "-Settings" : "") + (dialogsFinalRestored ? "-Restored" : "") +
                "-" +
                requested.x + "x" + requested.y + "-actual-" + Screen.width + "x" + Screen.height + ".png");
            ScreenCapture.CaptureScreenshot(output);
            if (dialogsBaseline || capturePrefix.StartsWith("Dialogs-")) WriteDialogsGeometry(output);
            Debug.Log("VISUAL " + output);
            capturePending = true;
        }

        private static void VerifyActionsColumnBounds()
        {
            var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>();
            var column = FindChild(hud.transform, "Actions Column") as RectTransform;
            var columnCorners = new Vector3[4];
            column.GetWorldCorners(columnCorners);
            foreach (var name in new[] { "Cooperation Region", "Deck Row", "Action Tabs", "Action Region" })
            {
                var rect = FindChild(column, name) as RectTransform;
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                if (corners[0].y < columnCorners[0].y - .5f || corners[2].y > columnCorners[2].y + .5f)
                    throw new InvalidOperationException(name + " 越出右列，不能以整列滚动替代分区适配。");
            }
            var content = FindChild(column, "Actions Scroll Content") as RectTransform;
            if (Mathf.Abs(content.rect.height - column.rect.height) > .5f || column.GetComponent<ScrollRect>() != null)
                throw new InvalidOperationException("右列必须填满当前列高且不能整列滚动。");
        }

        private static bool PrepareActionsState()
        {
            var requested = activeSizes[sizeIndex];
            if (Screen.width != requested.x || Screen.height != requested.y) return false;
            var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>();
            if (hud == null || hud.ActionPanelView == null) return false;
            var tabs = hud.GetComponentInChildren<UiMainActionTabs>(true);
            var stateName = ActionsStates[actionsState];
            var column = FindChild(hud.transform, "Actions Column");
            if (tabs == null || column == null) return false;
            if (!actionsStateApplied)
            {
                var main = hud.ActionPanelView.MainFaceObject;
                tabs.Select(stateName.StartsWith("Quick") ? 1 : stateName.StartsWith("City") ? 2 : 0);
                actionsStateApplied = true;
            }
            Canvas.ForceUpdateCanvases();
            var scroll = hud.ActionPanelView.MainFaceObject.GetComponent<ScrollRect>();
            if (scroll != null)
            {
                scroll.StopMovement();
                scroll.verticalNormalizedPosition = stateName.EndsWith("End") ? 0 : 1;
            }
            return true;
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
            var hud = frame == null ? null : frame.GetComponentInParent<GameplayInteractionHudView>();
            var mapRegion = hud == null ? null : FindChild(hud.transform, "Map Region") as RectTransform;
            if (frame == null || mapDisplay == null || camera == null ||
                !frame.TryValidateConfiguration(out reason))
            {
                if (string.IsNullOrEmpty(reason)) reason = "HUD、地图控制器或相机尚未就绪：HUD=" +
                    (frame != null) + "，地图=" + (mapDisplay != null) + "，主相机=" + (camera != null);
                if (frame != null && camera == null)
                {
                    var region = FindChild(frame.GetComponentInParent<GameplayInteractionHudView>().transform, "Map Region") as RectTransform;
                    if (region != null) reason += "，地图区域=" + region.rect + "，活动=" + region.gameObject.activeInHierarchy +
                        "，缩放=" + region.lossyScale;
                }
                return false;
            }
            var surface = hud == null || hud.MainModules == null ? null : hud.transform;
            if (surface == null || !surface.gameObject.activeInHierarchy)
            {
                reason = "新版主界面未就绪：HUD=" + (hud == null ? "空" : hud.name) +
                    "，Surface=" + (surface == null ? "未找到" : surface.name);
                for (var ancestor = surface; ancestor != null; ancestor = ancestor.parent)
                    reason += "，" + ancestor.name + "=" + ancestor.gameObject.activeSelf;
                return false;
            }
            if (componentOpponent)
            {
                var modules = hud.MainModules;
                if (modules == null)
                {
                    reason = "对手明细宿主未就绪";
                    return false;
                }
                if (componentState == null)
                {
                    componentState = new GameState();
                    componentState.Players.Add(new PlayerState
                        { PlayerId = 1, Name = "本机", Color = PlayerColor.Red });
                    componentState.Players.Add(new PlayerState
                        { PlayerId = 2, Name = "组件测试甲", Color = PlayerColor.Blue });
                    componentState.Players.Add(new PlayerState
                        { PlayerId = 3, Name = "组件测试乙", Color = PlayerColor.Green });
                    componentView = GameStateViewProjector.Project(componentState,
                        GameStateViewer.Player(1));
                }
                modules.Render(GameStateViewProjector.ToClientState(componentView),
                    componentView, 1, null);
                if (!componentExpanded)
                {
                    var toggle = FindChild(surface, "Opponent Seat 1") == null ? null :
                        FindChild(FindChild(surface, "Opponent Seat 1"), "Effect Detail Toggle");
                    if (toggle == null) throw new InvalidOperationException("对手明细入口缺失。");
                    toggle.GetComponent<Button>().onClick.Invoke();
                    componentExpanded = true;
                }
            }
            if (capturePrefix == "Effects-Quick-")
            {
                var tabs = surface.GetComponentInChildren<UiMainActionTabs>(true);
                if (tabs == null)
                {
                    reason = "快速行动页签未就绪";
                    return false;
                }
                if (tabs.SelectedIndex != 1) tabs.Select(1);
            }
            foreach (var name in new[] { "Outer Frame", "Left Ground", "Right Ground",
                         "Lower Ground", "Top Seam", "Map Frame", "Map Backdrop Left",
                         "Map Backdrop Right", "Map Backdrop Top", "Map Backdrop Bottom" })
            {
                var target = FindChild(surface, name);
                var image = target == null ? null : target.GetComponent<Image>();
                if (image == null || image.sprite == null ||
                    !target.gameObject.activeInHierarchy)
                {
                    reason = "缺少主界面素材：" + name;
                    return false;
                }
            }
            foreach (var name in new[] { "Brand Mark", "Round Plaque", "Current Action Flag",
                         "Current Action Icon", "Red Zone Plaque", "Red Zone Icon", "Fold Icon",
                         "Settings Icon", "Resolution Summary Slot", "Resolution Slot Frame",
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
            if (capturePrefix == "Effects-" || capturePrefix == "Effects-Quick-")
            {
                foreach (var name in new[] { "Outer Frame", "Persistent Bottom Bar",
                             "Bottom Frame", "Resolution Slot Frame" })
                {
                    var target = FindChild(frame.transform, name) ?? FindChild(surface, name);
                    var image = target == null ? null : target.GetComponent<Image>();
                    if (image == null || image.sprite == null ||
                        !image.sprite.name.Contains(name == "Persistent Bottom Bar"
                            ? "bottom-bar-clean" : name == "Resolution Slot Frame"
                                ? "chain-slot-frame" : name == "Outer Frame"
                                    ? "outer-frame" : "bottom-frame"))
                    {
                        reason = "3.5 主框未就绪：" + name;
                        return false;
                    }
                }
                var quick = FindChild(surface, "Quick Effect Row");
                if (quick == null || quick.GetComponent<UiEffectRowView>() == null)
                {
                    reason = "3.4 快速行动行未就绪";
                    return false;
                }
            }
            foreach (var name in new[] { "Opponent Seat 1", "Opponent Seat 2", "Opponent Seat 3",
                         "City Region", "Map Region", "Self Summary Region", "Entrepreneurs Region",
                         "Hand Region", "Cooperation Region", "Discard Region", "Face Down Region",
                         "Action Tabs", "Action Region" })
            {
                var target = FindChild(surface, name);
                if (target == null || !target.gameObject.activeInHierarchy)
                {
                    reason = "缺少主界面区域：" + name;
                    return false;
                }
            }
            var city = FindChild(surface, "City Board Artwork");
            var cityImage = city == null ? null : city.GetComponent<RawImage>();
            if (cityImage == null || cityImage.texture == null)
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
            var corners = new Vector3[4];
            mapRegion.GetWorldCorners(corners);
            var lowerLeft = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            var upperRight = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            var view = camera == null ? Rect.zero : camera.pixelRect;
            if ((Mathf.Abs(view.xMin - lowerLeft.x) > 2f ||
                Mathf.Abs(view.xMax - upperRight.x) > 2f ||
                Mathf.Abs(view.yMin - lowerLeft.y) > 2f ||
                Mathf.Abs(view.yMax - upperRight.y) > 2f))
            {
                reason = "地图相机视口与中央区域尚未对齐";
                return false;
            }
            var outerFrame = FindChild(surface, "Outer Frame");
            var mainRegions = FindChild(surface, "Main Regions");
            if (outerFrame == null || mainRegions == null ||
                outerFrame.GetComponentInParent<Canvas>().sortingOrder >=
                mainRegions.GetComponentInParent<Canvas>().sortingOrder)
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
            if (dialogsComponentMode >= 0 && dialogsComponent == null)
            {
                BuildDialogsComponent(hud);
                reason = "等待通用选择组件布局测量";
                return false;
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

        private static void BuildDialogsComponent(GameplayInteractionHudView hud)
        {
            var registry = hud.GetComponentInChildren<GameplayDialogRegistry>(true);
            dialogsComponent = registry.InstantiateEffectDialogShell(GameplayHudFrame.Active.ContentRect);
            var profile = dialogsComponent.LayoutProfile;
            var cards = dialogsComponentMode == 0;
            dialogsComponent.PrepareForUse("Component Diagnostic", "Diagnostic Panel",
                cards ? profile.SelectionPanelSize : profile.SalePanelSize, Vector2.zero, true);
            GameplayHudFrame.Active.ShowPage(dialogsComponent.gameObject, true);
            dialogsComponent.ConfigureHeading(cards ? "卡牌选择 · 组件诊断" : dialogsComponentMode == 1 ? "资源数量 · 组件诊断" : "选项 · 组件诊断",
                "仅检查实际资产、布局及字体；此画面使用诊断数据，不表示正式规则结算。", 56, "Title", "Description", 28);
            if (cards)
            {
                dialogsComponent.ConfigureCardScroll(profile.SelectionCardSize, profile.SelectionMinimumCardWidth);
                var facilityIds = ExternalContentRuntime.Pack.ActiveDefinitions
                    .Where(definition => definition.ContentType == "facility")
                    .Select(definition => definition.RuntimeId).ToArray();
                if (facilityIds.Length == 0)
                    throw new InvalidOperationException("外部内容包没有可用于诊断的设施卡。");
                for (int i = 0; i < 7; i++)
                {
                    var card = dialogsComponent.CreateFacilityCard();
                    card.CardImage.texture = registry.CardVisualCatalog.GetFacility(facilityIds[i % facilityIds.Length]);
                    card.FallbackLabel.gameObject.SetActive(false);
                    card.SelectionImage.enabled = i < 2;
                    card.DetailsButton.onClick.RemoveAllListeners();
                }
                dialogsComponent.ResourceSummaryText.text = string.Format(profile.SelectionSummaryFormat, 2, 2, 2);
            }
            else if (dialogsComponentMode == 1)
            {
                var names = new[] { "源岩", "源石", "异铁", "至纯源石" };
                for (int i = 0; i < 4; i++)
                {
                    var row = dialogsComponent.CreateResourceRow();
                    row.Label.text = names[i] + "（诊断库存 12）";
                    row.ValueText.text = "0";
                }
                dialogsComponent.ResourceSummaryText.text = "诊断草稿：尚未成交";
            }
            else
            {
                dialogsComponent.ConfigureOptionScroll("Options", 0, 0);
                for (int i = 0; i < 9; i++)
                {
                    var row = dialogsComponent.CreateOptionRow(null);
                    row.Label.text = "诊断选项 " + (i + 1) + "：正文换行和独立确认";
                    row.SetSelected(i == 1);
                }
                dialogsComponent.ResourceSummaryText.text = string.Format(profile.SelectionSummaryFormat, 1, 1, 1);
            }
            dialogsComponent.ResourceSummaryText.gameObject.SetActive(true);
            var action = dialogsComponent.AcquireActionButton(null);
            action.Label.text = cards || dialogsComponentMode == 2 ? profile.SelectionConfirmLabel : profile.SaleConfirmLabel;
            action.Button.interactable = dialogsComponentMode != 1;
            var secondary = dialogsComponent.AcquireActionButton(null);
            secondary.Label.text = cards || dialogsComponentMode == 2 ? profile.SelectionCancelLabel : profile.SaleFinishLabel;
            Canvas.ForceUpdateCanvases();
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
            var top = new Vector3[4];
            var bottom = new Vector3[4];
            var content = new Vector3[4];
            frame.TopBar.GetWorldCorners(top);
            frame.BottomBar.GetWorldCorners(bottom);
            frame.ContentRect.GetWorldCorners(content);
            if (content[0].y < bottom[2].y - .01f || content[2].y > top[0].y + .01f)
                throw new InvalidOperationException(scene + " 内容区与常驻栏边界重叠。");
            var mapDisplay = UnityEngine.Object.FindObjectOfType<MapDisplayController>();
            var camera = Camera.main;
            var zoomField = typeof(MapDisplayController).GetField("currentZoom", BindingFlags.Instance | BindingFlags.NonPublic);
            var framedField = typeof(MapDisplayController).GetField("framedViewportZoom", BindingFlags.Instance | BindingFlags.NonPublic);
            var rendererField = typeof(MapDisplayController).GetField("mapRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
            var mapRenderer = mapDisplay == null ? null : rendererField.GetValue(mapDisplay) as SpriteRenderer;
            var mapBounds = mapRenderer == null || camera == null ? "none" : ProjectedBounds(camera, mapRenderer).ToString();
            Debug.Log("MAP DIAGNOSTIC controller=" + (mapDisplay != null) +
                      " zoom=" + (mapDisplay == null ? "none" : zoomField.GetValue(mapDisplay).ToString()) +
                      " framed=" + (mapDisplay == null ? "none" : framedField.GetValue(mapDisplay).ToString()) +
                      " cameraRect=" + (camera == null ? "none" : camera.rect.ToString()) +
                      " pixelRect=" + (camera == null ? "none" : camera.pixelRect.ToString()) +
                      " mapBounds=" + mapBounds);
            Debug.Log("VERIFY " + scene + (modalCapture ? " settings" : "") +
                      " " + Screen.width + "x" + Screen.height + " input=ok viewport=" +
                      (camera == null ? "hidden" : "ok") + " layers=ok");
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
            if (actionsMatrix)
            {
                actionsStateApplied = false;
                if (++actionsState < ActionsStates.Length) return;
                actionsState = 0;
            }
            if (dialogsFinalMatrix)
            {
                if (!dialogsFinalRestored && sizeIndex + 1 < activeSizes.Length)
                { SelectSize(++sizeIndex); return; }
                if (!dialogsFinalRestored)
                { dialogsFinalRestored = true; sizeIndex = 0; SelectSize(0); return; }
                if (sceneIndex == 0)
                {
                    sceneIndex = 1;
                    dialogsFinalRestored = false;
                    SceneManager.LoadScene("ThreePlayerScene", LoadSceneMode.Single);
                }
                else finishing = true;
                return;
            }
            if (dialogsBaseline)
            {
                if (dialogsComponentMode >= 0)
                {
                    if (dialogsComponentMatrix)
                    {
                        dialogsComponentStep++;
                        if (dialogsComponentStep == 1) { sizeIndex = 2; SelectSize(2); return; }
                        if (dialogsComponentStep == 2) { sizeIndex = 4; SelectSize(4); return; }
                        if (dialogsComponentStep == 3)
                        {
                            capturePrefix += "End-";
                            dialogsComponent.RestoreScrollPosition(0);
                            return;
                        }
                        if (dialogsComponentStep == 4)
                        {
                            capturePrefix = capturePrefix.Replace("End-", "Restored-");
                            sizeIndex = 0; SelectSize(0);
                            dialogsComponent.RestoreScrollPosition(1);
                            return;
                        }
                        dialogsComponentStep = 0;
                    }
                    if (++dialogsComponentMode >= 3) { finishing = true; return; }
                    UnityEngine.Object.Destroy(dialogsComponent.gameObject);
                    dialogsComponent = null;
                    capturePrefix = dialogsComponentMode == 1 ? "Dialogs-Component-Resources-" : "Dialogs-Component-Options-";
                    return;
                }
                if (capturePrefix == "Dialogs-Narrow-")
                {
                    if (sizeIndex == 2) { sizeIndex = 0; SelectSize(0); }
                    else finishing = true;
                    return;
                }
                if (sceneIndex == 0)
                {
                    sceneIndex = 1;
                    SceneManager.LoadScene("ThreePlayerScene", LoadSceneMode.Single);
                }
                else finishing = true;
                return;
            }
            if (diagnosticOnly)
            {
                if (++sizeIndex < activeSizes.Length) SelectSize(sizeIndex);
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
            if (sizeIndex < activeSizes.Length)
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

        [Serializable] private sealed class GeometryReport
        {
            public int width, height;
            public Vector2 canvasLogicalSize;
            public float canvasScaleFactor;
            public Rect safeArea;
            public List<GeometryEntry> entries = new List<GeometryEntry>();
        }
        [Serializable] private sealed class GeometryEntry
        {
            public string path;
            public Rect screenRect;
            public bool active;
        }
        private static void WriteDialogsGeometry(string imagePath)
        {
            var report = new GeometryReport { width = Screen.width, height = Screen.height,
                safeArea = Screen.safeArea };
            var hud = UnityEngine.Object.FindObjectOfType<GameplayInteractionHudView>();
            var surfaceCanvas = hud.MainModules.GetComponent<Canvas>();
            report.canvasLogicalSize = ((RectTransform)hud.MainModules.transform).rect.size;
            report.canvasScaleFactor = surfaceCanvas == null ? 0 : surfaceCanvas.scaleFactor;
            foreach (var rect in hud.GetComponentsInChildren<RectTransform>(true))
            {
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                var canvas = rect.GetComponentInParent<Canvas>();
                var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera : null;
                var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
                var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                var path = rect.name;
                for (var parent = rect.parent; parent != null && parent != hud.transform; parent = parent.parent)
                    path = parent.name + "/" + path;
                report.entries.Add(new GeometryEntry { path = path,
                    screenRect = Rect.MinMaxRect(min.x, min.y, max.x, max.y), active = rect.gameObject.activeInHierarchy });
            }
            File.WriteAllText(Path.ChangeExtension(imagePath, ".json"), JsonUtility.ToJson(report, true));
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
