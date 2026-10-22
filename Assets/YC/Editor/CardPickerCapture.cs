using System;
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
    /// <summary>实际场景卡牌窗口的只读组件捕获。只改变运行实例，不生成或保存界面资产，不提交命令。</summary>
    public static class CardPickerCapture
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string PrefabPath = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CardPickerDialog.prefab";
        private static readonly string Output = Path.GetFullPath("prompt/UI换新/执行记录/卡牌选择");
        private static readonly CaptureCase[] Cases =
        {
            new CaptureCase(2, 1920, 1080, false), new CaptureCase(6, 1920, 1080, false),
            new CaptureCase(7, 1920, 1080, false), new CaptureCase(9, 1920, 1080, false),
            new CaptureCase(9, 1920, 1080, true), new CaptureCase(9, 900, 600, true),
            new CaptureCase(7, 1920, 1200, false)
        };

        private static EffectDialogShellView page;
        private static readonly List<FacilityEffectCardView> Cards = new List<FacilityEffectCardView>();
        private static Sprite[] fronts;
        private static SourceProof source;
        private static int step, stable;
        private static bool scrollApplied;
        private static string signature, pending, waiting;
        private static DateTime captureRequestedUtc;
        private static double started;

        public static void Run()
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("请从编辑模式运行卡牌组件捕获。");
                page = null;
                Cards.Clear();
                step = stable = 0;
                signature = pending = null;
                scrollApplied = false;
                waiting = "准备实际场景来源";
                Directory.CreateDirectory(Output);
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                var registry = SceneManager.GetActiveScene().GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<GameplayDialogRegistry>(true)).Single();
                RequireSource(registry);
                source = new SourceProof
                {
                    scene = ScenePath,
                    sceneRegistry = GlobalObjectId.GetGlobalObjectIdSlow(registry).ToString(),
                    prefab = AssetDatabase.GetAssetPath(registry.CardPickerPrefab),
                    prefabGuid = AssetDatabase.AssetPathToGUID(PrefabPath),
                    catalog = AssetDatabase.GetAssetPath(registry.CardVisualCatalog),
                    validatedBeforePlay = true
                };
                File.WriteAllText(Path.Combine(Output, "CardPicker-Component-SourceProof.json"),
                    JsonUtility.ToJson(source, true), new System.Text.UTF8Encoding(false));
                InvokeGameView("PrepareGameView");
                InvokeGameView("SelectSize", Cases[0].Size);
                started = EditorApplication.timeSinceStartup;
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
                EditorApplication.EnterPlaymode();
            }
            catch (Exception exception) { Debug.LogException(exception); Stop(1); }
        }

        private static void RequireSource(GameplayDialogRegistry registry)
        {
            if (registry == null || registry.CardPickerPrefab == null || !registry.CardPickerPrefab.IsCardPicker ||
                AssetDatabase.GetAssetPath(registry.CardPickerPrefab) != PrefabPath || registry.CardVisualCatalog == null)
                throw new InvalidOperationException("场景 Registry 没有引用指定的正式 CardPickerDialog 与卡面目录。");
            if (!registry.CardPickerPrefab.TryValidateConfiguration(out var reason))
                throw new InvalidOperationException("卡牌窗口源引用不完整：" + reason);
        }

        private static void InvokeGameView(string name, params object[] args)
        {
            var method = typeof(GameplaySupplementalPageCapture).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(typeof(GameplaySupplementalPageCapture).Name, name);
            method.Invoke(null, args);
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 300)
                    throw new TimeoutException("卡牌组件捕获超时：" + waiting);
                if (!EditorApplication.isPlaying) return;
                if (pending != null)
                {
                    var file = new FileInfo(pending);
                    if (!file.Exists || file.Length < 256 || file.LastWriteTimeUtc < captureRequestedUtc) return;
                    Debug.Log("卡牌组件捕获完成：" + pending);
                    pending = null;
                    UnityEngine.Object.Destroy(page.gameObject);
                    page = null;
                    Cards.Clear();
                    stable = 0;
                    signature = null;
                    scrollApplied = false;
                    if (++step == Cases.Length) { Stop(0); return; }
                    InvokeGameView("SelectSize", Cases[step].Size);
                    return;
                }

                var captureCase = Cases[step];
                if (Screen.width != captureCase.Size.x || Screen.height != captureCase.Size.y)
                { waiting = "等待实际渲染尺寸 " + captureCase.Size; return; }
                var frame = GameplayHudFrame.Active;
                if (frame == null || frame.ContentRect == null)
                { waiting = "等待局内框架"; return; }
                if (page == null)
                {
                    var registry = UnityEngine.Object.FindObjectOfType<GameplayDialogRegistry>();
                    if (registry == null) { waiting = "等待运行时 Registry"; return; }
                    RequireSource(registry);
                    if (AssetDatabase.GetAssetPath(registry.CardVisualCatalog) != source.catalog)
                        throw new InvalidOperationException("运行时卡面目录与进入 Play 前验证的实际场景引用不一致。");
                    CreateDiagnostic(registry, frame.ContentRect, captureCase);
                    return;
                }

                Canvas.ForceUpdateCanvases();
                if (!Ready()) { stable = 0; signature = null; return; }
                var geometry = string.Join("|", page.GetComponentsInChildren<RectTransform>()
                    .Select(rect => PathOf(rect) + ":" + ScreenRect(rect).ToString("F2")));
                if (signature != geometry) { signature = geometry; stable = 0; waiting = "等待布局稳定"; return; }
                if (++stable < 6) return;
                if (!scrollApplied)
                {
                    page.OptionScroll.StopMovement();
                    page.OptionScroll.horizontalNormalizedPosition = captureCase.End ? 1f : 0f;
                    scrollApplied = true;
                    stable = 0;
                    signature = null;
                    waiting = "等待滚动展示状态稳定";
                    return;
                }
                var report = BuildReport(captureCase, frame);
                pending = Path.Combine(Output, "CardPicker-Component-" + captureCase.Count + "-" +
                    (captureCase.End ? "End" : "Start") + "-" + captureCase.Size.x + "x" + captureCase.Size.y +
                    "-actual-" + Screen.width + "x" + Screen.height + ".png");
                File.WriteAllText(Path.ChangeExtension(pending, ".json"), JsonUtility.ToJson(report, true),
                    new System.Text.UTF8Encoding(false));
                captureRequestedUtc = DateTime.UtcNow;
                ScreenCapture.CaptureScreenshot(pending);
                waiting = "等待本轮 PNG 写入";
            }
            catch (Exception exception) { Debug.LogException(exception); Stop(1); }
        }

        private static void CreateDiagnostic(GameplayDialogRegistry registry, RectTransform parent, CaptureCase captureCase)
        {
            fronts = new[] { "liskarm", "texas", "tin-man", "cannot", "elysium" }
                .Select(registry.CardVisualCatalog.GetCharacterFront).ToArray();
            if (fronts.Any(sprite => sprite == null))
                throw new InvalidOperationException("真实角色正面 Sprite 切片缺失。");
            page = registry.InstantiateEffectDialogShell(parent, false, false, true);
            if (page == null || !page.IsCardPicker) throw new InvalidOperationException("实际实例不是卡牌选择窗口。");
            page.PrepareForUse("Card Picker Component Diagnostic", "Card Picker Window",
                new Vector2(1774, 887), Vector2.zero, true);
            page.ConfigureHeading("卡牌选择 · 组件诊断", "使用真实角色正面素材；展示布局与选中状态，不提交游戏命令。",
                34, "Diagnostic Title", "Diagnostic Description", 40);
            page.ConfigureCardScroll(new Vector2(180, 255), 180);
            for (var index = 0; index < captureCase.Count; index++)
            {
                var card = page.CreateFacilityCard();
                card.name = "Diagnostic Character Card " + index;
                if (!card.TryValidateConfiguration(out var reason)) throw new InvalidOperationException(reason);
                CardArtworkView.Set(card.CardImage, fronts[index % fronts.Length]);
                card.CardImage.gameObject.SetActive(true);
                card.CardImage.color = Color.white;
                card.FallbackLabel.gameObject.SetActive(false);
                card.Outline.enabled = false;
                card.CanvasGroup.alpha = 1f;
                card.Button.onClick.RemoveAllListeners();
                card.Button.interactable = true;
                if (card.DetailsButton != null) card.DetailsButton.onClick.RemoveAllListeners();
                if (card.SelectionImage == null) throw new InvalidOperationException("正式卡牌缺少选中框引用。");
                card.SelectionImage.gameObject.SetActive(true);
                card.SelectionImage.enabled = index == (captureCase.End ? captureCase.Count - 1 : 0);
                Cards.Add(card);
            }
            page.ResourceSummaryText.text = "已选择 1 / 1";
            page.ResourceSummaryText.gameObject.SetActive(true);
            for (var index = 0; index < 2; index++)
            {
                var action = page.AcquireActionButton(null);
                action.Button.onClick.RemoveAllListeners();
                action.Button.interactable = true;
            }
            waiting = "等待字体、卡面与布局就绪";
        }

        private static bool Ready()
        {
            if (!page.gameObject.activeInHierarchy || Cards.Count != Cases[step].Count || page.Panel.rect.width <= 0f ||
                page.OptionScroll.viewport == null || page.OptionScroll.viewport.rect.width <= 0f)
            { waiting = "等待卡牌与视口尺寸"; return false; }
            foreach (var raw in page.GetComponentsInChildren<RawImage>())
                if (raw.enabled && raw.texture == null)
                { waiting = "等待纹理：" + PathOf(raw.transform); return false; }
            foreach (var text in page.GetComponentsInChildren<Text>())
                if (text.enabled && !string.IsNullOrEmpty(text.text) &&
                    (text.font == null || text.font.material == null || text.font.material.mainTexture == null))
                { waiting = "等待字体：" + PathOf(text.transform); return false; }
            return Cards.All(card => card.CardImage.texture != null && card.CardImage.rectTransform.rect.height > 0f);
        }

        private static Report BuildReport(CaptureCase captureCase, GameplayHudFrame frame)
        {
            var viewport = ScreenRect(page.OptionScroll.viewport);
            var row = page.OptionContent.GetComponent<CardPickerRowLayout>();
            return new Report
            {
                source = source, requestedSize = captureCase.Size, actualSize = new Vector2Int(Screen.width, Screen.height),
                count = Cards.Count, distinctTextureCount = Cards.Select(card => card.CardImage.texture).Distinct().Count(),
                selectedIndex = captureCase.End ? captureCase.Count - 1 : 0, requestedEnd = captureCase.End,
                horizontalNormalizedPosition = page.OptionScroll.horizontalNormalizedPosition,
                horizontal = page.OptionScroll.horizontal, vertical = page.OptionScroll.vertical,
                contentRect = ScreenRect(frame.ContentRect), panel = ScreenRect(page.Panel), viewport = viewport,
                cardRow = ScreenRect(page.OptionContent), fitsAvailableHeight = row != null && row.FitsAvailableHeight,
                isOverflowing = row != null && row.IsOverflowing,
                scrollbarVisible = page.OptionScroll.horizontalScrollbar != null &&
                    page.OptionScroll.horizontalScrollbar.gameObject.activeInHierarchy,
                cards = Cards.Select((card, index) => new CardRecord
                {
                    index = index, roleId = fronts[index % fronts.Length].name,
                    textureAsset = card.CardImage.texture.name,
                    itemRect = ScreenRect(card.CardRect), faceRect = ScreenRect(card.CardImage.rectTransform),
                    faceFullyInsideViewport = Contains(viewport, ScreenRect(card.CardImage.rectTransform)),
                    selected = card.SelectionImage.enabled && card.SelectionImage.gameObject.activeInHierarchy,
                    selectionRect = ScreenRect(card.SelectionImage.rectTransform),
                    active = card.gameObject.activeInHierarchy, interactable = card.Button.IsInteractable()
                }).ToArray(),
                entries = page.GetComponentsInChildren<RectTransform>(true).Select(rect => new Entry
                {
                    path = PathOf(rect), screenRect = ScreenRect(rect), activeSelf = rect.gameObject.activeSelf,
                    activeInHierarchy = rect.gameObject.activeInHierarchy,
                    drivers = rect.GetComponents<Component>().Where(component => component is ILayoutController ||
                        component is ILayoutElement).Select(component => component.GetType().Name).ToArray()
                }).ToArray(),
                graphics = page.GetComponentsInChildren<Graphic>(true).Select(graphic => new GraphicRecord
                {
                    path = PathOf(graphic.transform), type = graphic.GetType().Name, enabled = graphic.enabled,
                    active = graphic.gameObject.activeInHierarchy, alpha = graphic.color.a,
                    screenRect = ScreenRect(graphic.rectTransform), textureAsset = TexturePath(graphic),
                    text = graphic is Text label ? label.text : null,
                    fontAsset = graphic is Text fontLabel ? AssetDatabase.GetAssetPath(fontLabel.font) : null
                }).ToArray()
            };
        }

        private static string TexturePath(Graphic graphic)
        {
            if (graphic is RawImage raw) return AssetDatabase.GetAssetPath(raw.texture);
            if (graphic is Image image && image.sprite != null) return AssetDatabase.GetAssetPath(image.sprite);
            return AssetDatabase.GetAssetPath(graphic.mainTexture);
        }

        private static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin - .5f &&
            inner.xMax <= outer.xMax + .5f && inner.yMin >= outer.yMin - .5f && inner.yMax <= outer.yMax + .5f;
        private static string PathOf(Transform target) => target.parent == null ? target.name : PathOf(target.parent) + "/" + target.name;
        private static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var canvas = rect.GetComponentInParent<Canvas>();
            var root = canvas == null ? null : canvas.rootCanvas;
            var camera = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            var points = corners.Select(corner => RectTransformUtility.WorldToScreenPoint(camera, corner)).ToArray();
            return Rect.MinMaxRect(points.Min(point => point.x), points.Min(point => point.y),
                points.Max(point => point.x), points.Max(point => point.y));
        }

        private static void Stop(int code)
        {
            EditorApplication.update -= Tick;
            Debug.Log(code == 0 ? "CardPicker 组件诊断七图完成；未提交业务命令，未保存界面资产。" : "CardPicker 组件诊断失败：" + waiting);
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }

        private sealed class CaptureCase
        {
            public readonly int Count;
            public readonly Vector2Int Size;
            public readonly bool End;
            public CaptureCase(int count, int width, int height, bool end)
            { Count = count; Size = new Vector2Int(width, height); End = end; }
        }
        [Serializable] private sealed class SourceProof
        { public string scene, sceneRegistry, prefab, prefabGuid, catalog; public bool validatedBeforePlay; }
        [Serializable] private sealed class Report
        {
            public bool diagnostic = true, businessVerified = false, commandSubmissionEnabled = false;
            public string diagnosticDescription = "从实际场景 Registry 实例化正式窗口；循环角色正面纹理构成展示样本，不代表合法业务候选。选中框与计数为组件诊断状态，按钮没有提交回调。";
            public string scrollStateSource = "仅设置运行实例 ScrollRect 的展示位置；不是鼠标或手柄输入验收。";
            public SourceProof source;
            public Vector2Int requestedSize, actualSize;
            public int count, distinctTextureCount, selectedIndex;
            public bool requestedEnd, horizontal, vertical, isOverflowing, fitsAvailableHeight, scrollbarVisible;
            public float horizontalNormalizedPosition;
            public Rect contentRect, panel, viewport, cardRow;
            public CardRecord[] cards;
            public Entry[] entries;
            public GraphicRecord[] graphics;
        }
        [Serializable] private sealed class CardRecord
        {
            public int index;
            public string roleId, textureAsset;
            public Rect itemRect, faceRect, selectionRect;
            public bool active, selected, interactable, faceFullyInsideViewport;
        }
        [Serializable] private sealed class Entry
        { public string path; public Rect screenRect; public bool activeSelf, activeInHierarchy; public string[] drivers; }
        [Serializable] private sealed class GraphicRecord
        {
            public string path, type, textureAsset, text, fontAsset;
            public Rect screenRect;
            public bool enabled, active;
            public float alpha;
        }
    }
}
