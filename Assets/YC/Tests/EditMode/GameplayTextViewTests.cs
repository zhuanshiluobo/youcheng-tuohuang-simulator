using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Presentation;

namespace YC.Tests.EditMode
{
    public sealed class GameplayTextViewTests
    {
        [Test] public void OverlayBoundsUseExplicitPlacedSizeAndKeepConfiguredNormalizedRegion()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YC.Presentation.GameplayNumberOverlayStyle")).First(t => t != null);
            var overlay = Activator.CreateInstance(type);
            var configured = new Rect(.13f, .21f, .61f, .47f);
            type.GetField("OverlayRect").SetValue(overlay, configured);
            var bounds = (Rect)type.GetMethod("BoundsForIconSize").Invoke(overlay, new object[] { 80f, 40f });
            Assert.That(bounds.width, Is.EqualTo(80 * configured.width).Within(.0001f));
            Assert.That(bounds.height, Is.EqualTo(40 * configured.height).Within(.0001f));
            Assert.That(bounds.x, Is.EqualTo(80 * configured.x).Within(.0001f));
            Assert.That(bounds.y, Is.EqualTo(40 * (1 - configured.y - configured.height)).Within(.0001f));
            Assert.That(type.GetField("OverlayRect").GetValue(overlay), Is.EqualTo(configured));
        }

        [Test] public void NativeFontBaselinesAndScoreInkStayAlignedAcrossCanvasScalesAndReuse()
        {
            var viewType = Type.GetType("YC.Presentation.GameplayTextView, Assembly-CSharp");
            var styleType = Type.GetType("YC.Presentation.GameplayTextStyle, Assembly-CSharp");
            var iconType = Type.GetType("YC.Presentation.GameplayIconCatalog, Assembly-CSharp");
            var style = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset", styleType);
            var icons = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset", iconType);
            var roles = styleType.GetProperty("FontRoles").GetValue(style);
            var canvasNode = new GameObject("字体基线及图内叠数窄验", typeof(RectTransform), typeof(Canvas));
            var canvas = canvasNode.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var hostNode = new GameObject("正文", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            hostNode.transform.SetParent(canvasNode.transform, false);
            var host = hostNode.GetComponent<Text>(); host.font = (Font)roles.GetType().GetProperty("Regular").GetValue(roles);
            host.fontSize = 32; host.alignment = TextAnchor.UpperLeft; host.rectTransform.sizeDelta = new Vector2(800, 240);
            var view = hostNode.AddComponent(viewType);
            var document = new GameplayTextDocument(new GameplayTextRun[] { new TextRun("Hg"),
                new TextRun("Hg", new GameplayTextRunStyle("Regular", "Bold")), new TextRun("策略", new GameplayTextRunStyle("SpecialWord")),
                new NumberRun(123), new IconRun("core:resource.score", "123 分", 123) }, "基线与分数");
            try
            {
                viewType.GetMethod("Configure").Invoke(view, new object[] { host, style, icons, new ScoreLayoutCatalog() });
                foreach (float scale in new[] { 1f, 2f, .75f })
                {
                    canvas.scaleFactor = scale;
                    viewType.GetMethod("Bind").Invoke(view, new object[] { document }); FlushCanvas();
                    var fragments = host.GetComponentsInChildren<Text>().Where(t => t != host && t.transform.parent == host.transform).ToArray();
                    var baselines = fragments.Select(ActualBaseline).ToArray();
                    Assert.That(baselines.Max() - baselines.Min(), Is.LessThanOrEqualTo(2 / scale));
                    Assert.That(fragments.Any(t => t.font == (Font)roles.GetType().GetProperty("Emphasis").GetValue(roles)), Is.True);
                    var image = host.GetComponentsInChildren<Image>().Single();
                    float iconBaselineOffset = (float)styleType.GetProperty("IconBaselineOffset").GetValue(style);
                    float iconBaseline = -image.rectTransform.anchoredPosition.y + Math.Max(0, image.rectTransform.sizeDelta.y * .8f + iconBaselineOffset);
                    Assert.That(iconBaseline, Is.EqualTo(baselines[0]).Within(2 / scale));
                    var overlay = image.GetComponentsInChildren<Text>().Single();
                    object[] arguments = { "core:score_number_overlay", null };
                    styleType.GetMethod("TryGetNumberOverlay").Invoke(style, arguments);
                    var area = (Rect)arguments[1].GetType().GetMethod("BoundsForIconSize").Invoke(arguments[1], new object[] { image.rectTransform.sizeDelta.x, image.rectTransform.sizeDelta.y });
                    LogScoreMetrics(view, viewType, host, image, overlay, arguments[1], area, scale);
                    Assert.That(overlay.fontSize, Is.GreaterThan(1));
                    Assert.That(overlay.font, Is.SameAs(roles.GetType().GetProperty("EffectNumber").GetValue(roles)));
                    var ink = ActualInk(overlay); float tolerance = 2 / scale;
                    Assert.That(ink.width, Is.LessThanOrEqualTo(area.width + tolerance));
                    Assert.That(ink.height, Is.LessThanOrEqualTo(area.height + tolerance));
                    Assert.That(ink.center.x, Is.EqualTo(area.center.x).Within(tolerance));
                    Assert.That(-ink.center.y, Is.EqualTo(area.center.y).Within(tolerance));
                    var ids = host.GetComponentsInChildren<Graphic>(true).Select(g => g.GetInstanceID()).ToArray();
                    viewType.GetMethod("ClearBinding").Invoke(view, null); FlushCanvas();
                    viewType.GetMethod("Bind").Invoke(view, new object[] { document }); FlushCanvas();
                    Assert.That(host.GetComponentsInChildren<Graphic>(true).Select(g => g.GetInstanceID()), Is.EqualTo(ids));
                    int layoutCount = (int)viewType.GetProperty("LayoutCount").GetValue(view);
                    int renderCount = (int)viewType.GetProperty("RenderCount").GetValue(view);
                    viewType.GetMethod("Bind").Invoke(view, new object[] { document }); FlushCanvas();
                    Assert.That((int)viewType.GetProperty("LayoutCount").GetValue(view), Is.EqualTo(layoutCount));
                    Assert.That((int)viewType.GetProperty("RenderCount").GetValue(view), Is.EqualTo(renderCount));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(canvasNode); }
        }

        private static void LogScoreMetrics(object view, Type viewType, Text host, Image image, Text overlay, object configuredOverlay, Rect area, float scale)
        {
            float ratio = (float)configuredOverlay.GetType().GetField("FontSizeRatio").GetValue(configuredOverlay);
            var normalized = (Rect)configuredOverlay.GetType().GetField("OverlayRect").GetValue(configuredOverlay);
            Debug.Log($"叠数原生采样：scale={scale:R}, hostFont={host.font.name}, hostSize={host.fontSize}, hostPpu={host.pixelsPerUnit:R}, font={overlay.font.name}, fontDynamic={overlay.font.dynamic}, fontDefaultSize={overlay.font.fontSize}, fontAscent={overlay.font.ascent}, fontLineHeight={overlay.font.lineHeight}, overlaySize={overlay.fontSize}, overlayPpu={overlay.pixelsPerUnit:R}, iconPlaced={image.rectTransform.sizeDelta.ToString("R")}, iconRect={image.rectTransform.rect.ToString("R")}, configured={normalized.ToString("R")}, area={area.ToString("R")}, ratio={ratio:R}");
            var measure = viewType.GetMethod("Measure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            int preferred = Math.Max(1, Mathf.RoundToInt(host.fontSize * ratio));
            foreach (int size in new[] { preferred, Math.Max(2, preferred / 2), 2, 1 }.Distinct())
            {
                var metric = measure.Invoke(view, new object[] { overlay.text, overlay.font, size });
                var type = metric.GetType();
                var ink = (Rect)type.GetField("Ink").GetValue(metric);
                Debug.Log($"叠数候选实测：size={size}, width={type.GetField("Width").GetValue(metric)}, ascent={type.GetField("Ascent").GetValue(metric)}, descent={type.GetField("Descent").GetValue(metric)}, baseline={type.GetField("BaselineFromTop").GetValue(metric)}, ink={ink.ToString("R")}");
            }
            Debug.Log($"叠数已放置网格：ink={ActualInk(overlay).ToString("R")}, textRect={overlay.rectTransform.rect.ToString("R")}, topLeft={overlay.rectTransform.anchoredPosition.ToString("R")}");
        }

        private static float ActualBaseline(Text text)
        {
            text.cachedTextGenerator.Populate(text.text, text.GetGenerationSettings(text.rectTransform.rect.size));
            float top = text.cachedTextGenerator.verts.Take(4).Max(v => v.position.y);
            text.font.RequestCharactersInTexture(text.text, Mathf.RoundToInt(text.fontSize * text.pixelsPerUnit), FontStyle.Normal);
            CharacterInfo info;
            Assert.That(text.font.GetCharacterInfo(text.text[0], out info, Mathf.RoundToInt(text.fontSize * text.pixelsPerUnit), FontStyle.Normal), Is.True);
            return -text.rectTransform.anchoredPosition.y + (info.maxY - top) / text.pixelsPerUnit;
        }
        private static Rect ActualInk(Text text)
        {
            text.cachedTextGenerator.Populate(text.text, text.GetGenerationSettings(text.rectTransform.rect.size));
            var points = text.cachedTextGenerator.verts.Take(text.text.Length * 4).Select(v => new Vector2(v.position.x / text.pixelsPerUnit, v.position.y / text.pixelsPerUnit) + text.rectTransform.anchoredPosition).ToArray();
            return Rect.MinMaxRect(points.Min(p => p.x), points.Min(p => p.y), points.Max(p => p.x), points.Max(p => p.y));
        }

        [Test] public void NativeCanvasLayoutQueriesDeferGraphicsAndApplyLatestBinding()
        {
            var viewType = Type.GetType("YC.Presentation.GameplayTextView, Assembly-CSharp");
            var styleType = Type.GetType("YC.Presentation.GameplayTextStyle, Assembly-CSharp");
            var iconType = Type.GetType("YC.Presentation.GameplayIconCatalog, Assembly-CSharp");
            Assert.That(viewType, Is.Not.Null); Assert.That(styleType, Is.Not.Null); Assert.That(iconType, Is.Not.Null);
            var style = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayTextStyle.asset", styleType);
            var icons = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Text/Content/GameplayIconCatalog.asset", iconType);
            Assert.That(style, Is.Not.Null); Assert.That(icons, Is.Not.Null);
            var roles = styleType.GetProperty("FontRoles").GetValue(style);
            Assert.That(roles, Is.Not.Null);
            var rolesType = roles.GetType();
            var canvas = new GameObject("文字布局生命周期验证", typeof(RectTransform), typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            Text CreateHost(string name)
            {
                var node = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                node.transform.SetParent(canvas.transform, false);
                var host = node.GetComponent<Text>();
                host.font = (Font)rolesType.GetProperty("Regular").GetValue(roles); host.fontSize = 24;
                host.horizontalOverflow = HorizontalWrapMode.Wrap; host.rectTransform.sizeDelta = new Vector2(240, 500);
                return host;
            }
            var source = CreateHost("原正文宿主");
            var view = source.gameObject.AddComponent(viewType);
            var scoreCatalog = new ScoreLayoutCatalog();
            int Counter(string name) => (int)viewType.GetProperty(name).GetValue(view);
            Text[] Fragments() => source.GetComponentsInChildren<Text>().Where(t => t != source && t.transform.parent == source.transform).ToArray();
            string Body() => string.Concat(Fragments().Select(t => t.text));
            void Bind(GameplayTextDocument value) => viewType.GetMethod("Bind").Invoke(view, new object[] { value });
            void Query()
            {
                viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                viewType.GetMethod("CalculateLayoutInputVertical").Invoke(view, null);
            }
            void InNativeLayout(Action action)
            {
                var probe = new LayoutLifecycleProbe(source.transform, action);
                Assert.That(CanvasUpdateRegistry.TryRegisterCanvasElementForLayoutRebuild(probe), Is.True);
                Canvas.ForceUpdateCanvases();
                Assert.That(probe.Calls, Is.EqualTo(1), "必须实际进入 UGUI 布局循环");
                Assert.That(probe.Error, Is.Null, probe.Error?.ToString());
                FlushCanvas();
            }
            var rich = new GameplayTextDocument(new GameplayTextRun[] {
                new TextRun(new string('文', 48), new GameplayTextRunStyle("Regular", "Bold", 0xD95745FFu)),
                new LineBreak(), new TextRun("策略", new GameplayTextRunStyle("SpecialWord")),
                new NumberRun(123), new IconRun("core:resource.score", "-123 分", -123),
                new LineBreak(), new TextRun("玩家 <b>字面</b>")
            }, "生命周期富文本");
            var brief = new GameplayTextDocument(new[] { new TextRun("最终正文") }, "生命周期短正文");
            var superseded = new GameplayTextDocument(new[] { new TextRun("不应显示的中间绑定") }, "生命周期中间正文");
            try
            {
                viewType.GetMethod("Configure").Invoke(view, new object[] { source, style, icons, scoreCatalog });
                Bind(rich); Query();
                Assert.That(Counter("PooledObjectCount"), Is.Zero, "布局查询只能提供尺寸");
                Assert.That((float)viewType.GetProperty("preferredHeight").GetValue(view), Is.GreaterThan(0));
                FlushCanvas();
                Assert.That(Body(), Does.Contain("玩家 <b>字面</b>"));
                Assert.That(Fragments().All(t => !t.supportRichText && t.fontStyle == FontStyle.Normal), Is.True);
                Assert.That(Fragments().Any(t => t.font == (Font)rolesType.GetProperty("Emphasis").GetValue(roles)), Is.True);
                Assert.That(Fragments().Any(t => t.font == (Font)rolesType.GetProperty("SpecialWord").GetValue(roles)), Is.True);
                Assert.That(Fragments().Any(t => t.font == (Font)rolesType.GetProperty("EffectNumber").GetValue(roles)), Is.True);
                var image = source.GetComponentsInChildren<Image>().Single();
                Assert.That(image.sprite, Is.Not.Null);
                Assert.That(image.GetComponentsInChildren<Text>().Single().text, Is.EqualTo("-123"));
                int richPool = Counter("PooledObjectCount"), richFragments = Counter("ActiveFragmentCount");
                var poolIds = source.GetComponentsInChildren<Graphic>(true).Select(g => g.GetInstanceID()).ToArray();
                int beforeRender = Counter("RenderCount");
                InNativeLayout(() => {
                    Assert.That(CanvasUpdateRegistry.IsRebuildingLayout(), Is.True);
                    viewType.GetMethod("ClearBinding").Invoke(view, null);
                    Bind(superseded); Bind(brief); Query();
                    Assert.That(Counter("RenderCount"), Is.EqualTo(beforeRender));
                    Assert.That(Counter("PooledObjectCount"), Is.EqualTo(richPool));
                    Assert.That(Counter("ActiveFragmentCount"), Is.EqualTo(richFragments));
                    Assert.That(image.gameObject.activeSelf, Is.True, "不能在布局阶段停用原图示");
                });
                Assert.That(Body(), Is.EqualTo("最终正文"));
                Assert.That(Counter("RenderCount"), Is.EqualTo(beforeRender + 1), "同轮多次绑定只应用最后一份");
                Assert.That(Counter("PooledObjectCount"), Is.EqualTo(richPool));
                Assert.That(image.gameObject.activeSelf, Is.False);
                InNativeLayout(() => { Bind(rich); Query(); });
                Assert.That(Counter("PooledObjectCount"), Is.EqualTo(richPool));
                Assert.That(source.GetComponentsInChildren<Graphic>(true).Select(g => g.GetInstanceID()), Is.EquivalentTo(poolIds));
                Assert.That(image.GetComponentsInChildren<Text>().Single().text, Is.EqualTo("-123"));
                float wideHeight = (float)viewType.GetProperty("preferredHeight").GetValue(view);
                InNativeLayout(() => {
                    source.rectTransform.sizeDelta = new Vector2(120, 500); Query();
                    Assert.That(Counter("PooledObjectCount"), Is.EqualTo(richPool));
                    Assert.That((float)viewType.GetProperty("preferredHeight").GetValue(view), Is.GreaterThan(wideHeight));
                });
                Assert.That(Counter("ActiveFragmentCount"), Is.GreaterThan(richFragments));
                int grownPool = Counter("PooledObjectCount");
                InNativeLayout(() => { source.rectTransform.sizeDelta = new Vector2(480, 500); Query(); });
                Assert.That(Counter("PooledObjectCount"), Is.EqualTo(grownPool));
                Assert.That(Body(), Does.Contain("玩家 <b>字面</b>"));
                var replacement = CreateHost("替换正文宿主");
                InNativeLayout(() => {
                    viewType.GetMethod("Configure").Invoke(view, new object[] { replacement, style, icons, scoreCatalog });
                    Bind(brief); Query();
                    Assert.That(image.transform.parent, Is.SameAs(source.transform), "重挂父节点也必须延后");
                });
                Assert.That(source.canvasRenderer.GetAlpha(), Is.EqualTo(1));
                source = replacement;
                Assert.That(Body(), Is.EqualTo("最终正文"));
                Assert.That(Counter("PooledObjectCount"), Is.EqualTo(grownPool));
                Bind(rich); FlushCanvas();
                Assert.That(Body(), Does.Contain("玩家 <b>字面</b>"));
                int stableLayouts = Counter("LayoutCount"), stableRenders = Counter("RenderCount");
                for (int i = 0; i < 6; i++) { Bind(rich); Query(); Canvas.ForceUpdateCanvases(); }
                Assert.That(Counter("LayoutCount"), Is.EqualTo(stableLayouts));
                Assert.That(Counter("RenderCount"), Is.EqualTo(stableRenders));
                Assert.That(Counter("PooledObjectCount"), Is.EqualTo(grownPool));
                Debug.Log($"文字布局原生检查：布局回调延后应用、最终绑定、池复用、分数叠数通过；稳定布局={stableLayouts}，稳定应用={stableRenders}，池对象={grownPool}。");
            }
            finally { UnityEngine.Object.DestroyImmediate(canvas); }
        }

        private static void FlushCanvas()
        {
            // 只在此有限原生检查中驱动 Canvas；生产代码没有 ForceUpdateCanvases。
            Canvas.ForceUpdateCanvases(); Canvas.ForceUpdateCanvases();
        }
        private sealed class LayoutLifecycleProbe : ICanvasElement
        {
            public Transform transform { get; }
            private readonly Action action;
            public int Calls { get; private set; }
            public Exception Error { get; private set; }
            public LayoutLifecycleProbe(Transform target, Action work) { transform = target; action = work; }
            public void Rebuild(CanvasUpdate executing)
            {
                if (executing != CanvasUpdate.Layout || Calls != 0) return;
                Calls++;
                try { action(); } catch (Exception error) { Error = error; }
            }
            public void LayoutComplete() { }
            public void GraphicUpdateComplete() { }
            public bool IsDestroyed() => transform == null;
        }
        private sealed class ScoreLayoutCatalog : IGameplayTextCatalog
        {
            public string ContentHash => "native-layout-fixture";
            public string Locale => "zh-CN";
            public bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition) { definition = null; return false; }
            public bool TryGetIcon(string key, out GameplayIconDefinition definition)
            {
                definition = key == "core:resource.score" ? new GameplayIconDefinition {
                    Id = key, Usage = "inline_rule", AssetKey = "core:resource.score.asset", RenderMode = "number_overlay",
                    ValueRequired = true, NumberOverlayStyleKey = "core:score_number_overlay"
                } : null;
                return definition != null;
            }
        }

        [Test] public void SingleLineMeasuresWithoutBindingEllipsizesWithoutBestFitAndCanReturnToBodyMode()
        {
            Type viewType = Type.GetType("YC.Presentation.GameplayTextView, Assembly-CSharp");
            Type styleType = Type.GetType("YC.Presentation.GameplayTextStyle, Assembly-CSharp");
            Type rolesType = Type.GetType("YC.Presentation.UiFontRoles, Assembly-CSharp");
            var roles = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/CommonUi/Content/UiFontRoles.asset", rolesType);
            Assert.That(roles, Is.Not.Null); var style = ScriptableObject.CreateInstance(styleType);
            styleType.GetMethod("Configure").Invoke(style, new[] { roles });
            var root = new GameObject("单行文字验证", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var text = root.GetComponent<Text>(); text.font = (Font)rolesType.GetProperty("Regular").GetValue(roles);
            text.fontSize = 24; text.rectTransform.sizeDelta = new Vector2(90, 10); text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 8; text.resizeTextMaxSize = 24;
            var view = root.AddComponent(viewType);
            try
            {
                viewType.GetMethod("Configure").Invoke(view, new object[] { text, style, null, null });
                var document = new GameplayTextDocument(new GameplayTextRun[] { new TextRun(new string('文', 30), new GameplayTextRunStyle("Regular", "Bold")), new LineBreak(), new NumberRun(-123) }, "单行验证");
                float fullWidth = (float)viewType.GetMethod("MeasureSingleLineWidth").Invoke(view, new object[] { document });
                Assert.That(fullWidth, Is.GreaterThan(text.rectTransform.rect.width));
                Assert.That((int)viewType.GetProperty("PooledObjectCount").GetValue(view), Is.EqualTo(0));
                Assert.That((int)viewType.GetProperty("BindCount").GetValue(view), Is.EqualTo(0));
                viewType.GetMethod("BindSingleLineEllipsized").Invoke(view, new object[] { document, float.MaxValue });
                viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                FlushCanvas();
                var fragments = root.GetComponentsInChildren<Text>().Where(t => t != text).ToArray();
                Assert.That(string.Concat(fragments.Select(t => t.text)), Does.EndWith("…"));
                Assert.That(fragments.All(t => t.fontSize == 24), Is.True);
                Assert.That((float)viewType.GetProperty("preferredWidth").GetValue(view), Is.LessThanOrEqualTo(90));
                int layouts = (int)viewType.GetProperty("LayoutCount").GetValue(view);
                Assert.That((float)viewType.GetMethod("MeasureSingleLineWidth").Invoke(view, new object[] { new GameplayTextDocument(new[] { new TextRun("另一个待测标签") }) }), Is.GreaterThan(0));
                Assert.That((int)viewType.GetProperty("LayoutCount").GetValue(view), Is.EqualTo(layouts));
                text.rectTransform.sizeDelta = new Vector2(fullWidth + 1, 100);
                viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                FlushCanvas();
                Assert.That(string.Concat(root.GetComponentsInChildren<Text>().Where(t => t != text).Select(t => t.text)), Is.EqualTo(new string('文', 30) + " -123"));
                text.resizeTextForBestFit = false; text.rectTransform.sizeDelta = new Vector2(90, 1000);
                viewType.GetMethod("Bind").Invoke(view, new object[] { document }); viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                FlushCanvas();
                Assert.That(root.GetComponentsInChildren<Text>().Where(t => t != text).Any(t => t.text.Contains("…")), Is.False);
                Assert.That((float)viewType.GetProperty("preferredHeight").GetValue(view), Is.GreaterThan(24));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(style); }
        }
        [Test] public void ActualFontsPoolingAndSameDocumentBindingKeepTheirRoles()
        {
            Type viewType = Type.GetType("YC.Presentation.GameplayTextView, Assembly-CSharp");
            Type styleType = Type.GetType("YC.Presentation.GameplayTextStyle, Assembly-CSharp");
            Type rolesType = Type.GetType("YC.Presentation.UiFontRoles, Assembly-CSharp");
            Assert.That(viewType, Is.Not.Null); Assert.That(styleType, Is.Not.Null);
            var roles = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/CommonUi/Content/UiFontRoles.asset", rolesType);
            Assert.That(roles, Is.Not.Null);
            var style = ScriptableObject.CreateInstance(styleType);
            styleType.GetMethod("Configure").Invoke(style, new[] { roles });
            var root = new GameObject("Gameplay text validation", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var text = root.GetComponent<Text>(); text.font = (Font)rolesType.GetProperty("Regular").GetValue(roles);
            text.fontSize = 24; text.color = Color.green; text.rectTransform.sizeDelta = new Vector2(120, 120); text.horizontalOverflow = HorizontalWrapMode.Wrap;
            var view = root.AddComponent(viewType);
            try
            {
                viewType.GetMethod("Configure").Invoke(view, new object[] { text, style, null, null });
                var document = new GameplayTextDocument(new GameplayTextRun[] { new TextRun(new string('文', 200), new GameplayTextRunStyle("Regular", "Bold", 0xD95745FFu)), new TextRun("策略", new GameplayTextRunStyle("SpecialWord")), new NumberRun(123) }, "first");
                viewType.GetMethod("Bind").Invoke(view, new object[] { document }); viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                FlushCanvas();
                var children = root.GetComponentsInChildren<Text>().Where(t => t != text).ToArray();
                Assert.That(children.Length, Is.LessThan(100)); Assert.That(children.All(t => t.fontStyle == FontStyle.Normal && !t.supportRichText), Is.True);
                Assert.That(children.Any(t => t.font == (Font)rolesType.GetProperty("Emphasis").GetValue(roles)), Is.True);
                Assert.That(children.Any(t => t.font == (Font)rolesType.GetProperty("SpecialWord").GetValue(roles)), Is.True);
                Assert.That(children.Any(t => t.font == (Font)rolesType.GetProperty("EffectNumber").GetValue(roles)), Is.True);
                int count = (int)viewType.GetProperty("PooledObjectCount").GetValue(view);
                var plain = new GameplayTextDocument(new[] { new TextRun("普通") }, "second");
                viewType.GetMethod("Bind").Invoke(view, new object[] { plain }); viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                FlushCanvas();
                var active = root.GetComponentsInChildren<Text>().Single(t => t != text);
                Assert.That(active.color, Is.EqualTo(Color.green)); Assert.That(active.font, Is.SameAs(text.font));
                Assert.That((int)viewType.GetProperty("PooledObjectCount").GetValue(view), Is.EqualTo(count));
                int bound = (int)viewType.GetProperty("BindCount").GetValue(view), arranged = (int)viewType.GetProperty("LayoutCount").GetValue(view);
                viewType.GetMethod("Bind").Invoke(view, new object[] { plain }); viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                FlushCanvas();
                Assert.That((int)viewType.GetProperty("BindCount").GetValue(view), Is.EqualTo(bound)); Assert.That((int)viewType.GetProperty("LayoutCount").GetValue(view), Is.EqualTo(arranged));
                var replacementNode = new GameObject("Replacement text host", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                replacementNode.transform.SetParent(root.transform, false);
                var replacement = replacementNode.GetComponent<Text>(); replacement.font = text.font; replacement.fontSize = text.fontSize;
                replacement.rectTransform.sizeDelta = text.rectTransform.sizeDelta;
                viewType.GetMethod("Configure").Invoke(view, new object[] { replacement, style, null, null });
                viewType.GetMethod("Bind").Invoke(view, new object[] { plain }); viewType.GetMethod("CalculateLayoutInputHorizontal").Invoke(view, null);
                FlushCanvas();
                Assert.That(root.GetComponentsInChildren<Text>(true).Where(t => t != text && t != replacement).All(t => t.transform.parent == replacement.transform), Is.True);
                Assert.That((int)viewType.GetProperty("PooledObjectCount").GetValue(view), Is.EqualTo(count));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(style); }
        }
    }
}
