using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>动态玩法正文专用宿主。字形可逐元素测量，物体按行片段和图标池化。</summary>
    [DisallowMultipleComponent]
    public sealed class GameplayTextView : MonoBehaviour, ILayoutElement
    {
        [SerializeField] private Text source;
        [SerializeField] private GameplayTextStyle style;
        [SerializeField] private GameplayIconCatalog icons;
        private IGameplayTextCatalog catalog;
        private GameplayTextDocument document;
        private readonly List<Text> textPool = new List<Text>();
        private readonly List<IconSlot> iconPool = new List<IconSlot>();
        private readonly Dictionary<MetricKey, Metric> metrics = new Dictionary<MetricKey, Metric>();
        private readonly HashSet<string> diagnosticKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly TextGenerator measurer = new TextGenerator();
        private readonly List<Group> groups = new List<Group>();
        private bool dirty, visualsDirty, reparentPool, layoutMarkPending, canvasCallbackRegistered, measurerDisposed;
        private Text hiddenSource;
        private GameplayTextLayout.Result pendingLayout;
        private Rect pendingRect;
        private int pendingSize;
        private float sourceAlpha, lastWidth = -1, lastHeight = -1, lastPixelsPerUnit, lastCanvasScale;
        private Font lastHostFont;
        private Color lastHostColor;
        private int lastSize, lastStyleRevision = -1, lastIconRevision = -1;
        private int lastFontIdentity, lastOptionsIdentity;
        private int? fontSizeOverride;
        private bool singleLineEllipsized;
        private float singleLineMaximumWidth;
        private int metricStyleRevision = -1, metricFontIdentity;
        private Font metricHostFont;
        private float metricPixelsPerUnit;
        private float measuredWidth, measuredHeight;
        private int documentVersion;
        private string boundSourceText;
        private GameplayTextHostStandard hostStandard;
        private UiSemanticLabel legacyLabel;
        private UiContentHeightLayout legacyHeight;
        private bool legacyHeightWasEnabled;
        private bool legacySuspended, legacyWasEnabled;
        private readonly List<LegacyVisual> legacyVisuals = new List<LegacyVisual>();
        public event Action<string> Diagnostic;
        internal event Action LayoutInputChanged;
        public int BindCount { get; private set; }
        public int LayoutCount { get; private set; }
        public int RenderCount { get; private set; }
        public int ActiveFragmentCount { get; private set; }
        public int PooledObjectCount { get { int count = textPool.Count + iconPool.Count; foreach (var icon in iconPool) if (icon.Overlay != null) count++; return count; } }
        public int EffectiveFontSize { get; private set; }
        public bool HasDocument => document != null;
        public string MeasurementCacheKey => document == null ? string.Empty :
            documentVersion + "|" + document.MeasurementIdentity + "|" + document.Identity + "|" + (style != null ? style.Revision : -1) + "|" +
            (style != null ? style.FontConfigurationIdentity : 0) + "|" + (icons != null ? icons.Revision : -1) + "|" +
            (source != null && source.font != null ? source.font.GetInstanceID() : 0) + "|" + CanvasScale + "|" +
            (source != null ? source.horizontalOverflow.ToString() : string.Empty);
        private bool ProvidesLayoutInput => isActiveAndEnabled && document != null;
        public float minWidth => ProvidesLayoutInput ? 0 : -1;
        public float preferredWidth { get { UpdateMeasurement(); return ProvidesLayoutInput ? measuredWidth : -1; } }
        public float flexibleWidth => ProvidesLayoutInput ? 0 : -1;
        public float minHeight => ProvidesLayoutInput ? 0 : -1;
        public float preferredHeight { get { float height; return TryGetPreferredLayoutHeight(out height) ? height : -1; } }
        public float flexibleHeight => ProvidesLayoutInput ? 0 : -1;
        public int layoutPriority => 1;

        /// <summary>只查询同一文档的测量缓存；无活动绑定时让原Text提供布局输入。</summary>
        internal bool TryGetPreferredLayoutHeight(out float height)
        {
            height = 0;
            if (!isActiveAndEnabled) return false;
            UpdateMeasurement();
            if (!ProvidesLayoutInput) return false;
            height = measuredHeight;
            return true;
        }

        public void Configure(Text host, GameplayTextStyle textStyle, GameplayIconCatalog iconCatalog, IGameplayTextCatalog textCatalog)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            ClearBinding(); source = host; style = textStyle; icons = iconCatalog; catalog = textCatalog;
            reparentPool = true;
            metrics.Clear(); metricStyleRevision = -1; diagnosticKeys.Clear(); lastStyleRevision = -1; lastIconRevision = -1;
            QueueVisualApply();
        }
        internal void UseHostStandard(GameplayTextHostStandard standard)
        {
            hostStandard = standard;
            if (source == null) source = standard.Text;
            if (source != standard.Text) throw new InvalidOperationException("条目文字标准与文档宿主不一致。");
            standard.Apply(false);
            if (fontSizeOverride.HasValue) { fontSizeOverride = null; dirty = true; }
        }
        public void SetFontSizeOverride(int? size)
        { if (size.HasValue && size.Value < 1) throw new ArgumentOutOfRangeException(nameof(size)); if (hostStandard != null) size = null; if (fontSizeOverride != size) { fontSizeOverride = size; dirty = true; RequestLayoutUpdate(); } }

        /// <summary>只测当前正式文档；不改待显示分组或Graphic。共同拟合使用与显示相同的字体、图和叠数。</summary>
        public bool FitsAtFontSize(int size)
        {
            if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
            if (document == null) return true;
            ValidateConfiguration(); hostStandard?.Apply(false); RefreshMeasurementCache();
            var rect = source.rectTransform.rect;
            var measuredGroups = new List<Group>();
            var elements = BuildElements(document, size, false, measuredGroups);
            var layout = GameplayTextLayout.Arrange(elements, Math.Max(1, rect.width), size, style.LineSpacing,
                source.horizontalOverflow == HorizontalWrapMode.Wrap);
            if (layout.Width > rect.width + .01f || layout.Height > rect.height + .01f || layout.HasOversizedAtomic) return false;
            foreach (var group in measuredGroups)
            {
                if (group.Icon == null || !group.Icon.NumberValue.HasValue) continue;
                float height = size * style.IconHeightRatio;
                float width = height * group.Sprite.rect.width / Math.Max(1f, group.Sprite.rect.height);
                OverlayMeasurement overlay;
                if (!TryMeasureOverlay(group, size, width, height, out overlay) || !overlay.Fits) return false;
            }
            return true;
        }

        public void Bind(GameplayTextDocument value)
            => BindCore(value, false, 0);

        /// <summary>完整单行实测；不改当前绑定，不取正文换行结果，不生成可视物体。</summary>
        public float MeasureSingleLineWidth(GameplayTextDocument value)
        {
            if (value == null) return 0;
            ValidateConfiguration(); RefreshMeasurementCache();
            var measuredGroups = new List<Group>();
            return GameplayTextLayout.MeasureSingleLineWidth(BuildElements(value, fontSizeOverride ?? source.fontSize, true, measuredGroups));
        }

        /// <summary>按钮专用；最大宽与当前标签框共同限制，保持原字号，硬换行转成空格。</summary>
        public void BindSingleLineEllipsized(GameplayTextDocument value, float maximumWidth)
        {
            if (float.IsNaN(maximumWidth) || float.IsInfinity(maximumWidth) || maximumWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumWidth));
            BindCore(value, true, maximumWidth);
        }

        private void ValidateConfiguration()
        {
            if (source == null || style == null || style.FontRoles == null) throw new InvalidOperationException("玩法文字宿主及字体样式引用尚未配置。");
        }

        private void BindCore(GameplayTextDocument value, bool singleLine, float maximumWidth)
        {
            if (value == null) { ClearBinding(); return; }
            ValidateConfiguration();
            bool sameDocument = ReferenceEquals(document, value) || (document != null && !string.IsNullOrEmpty(value.Identity) && document.Identity == value.Identity);
            if (sameDocument && singleLineEllipsized == singleLine && (!singleLine || singleLineMaximumWidth == maximumWidth)) return;
            singleLineEllipsized = singleLine; singleLineMaximumWidth = maximumWidth;
            document = value; boundSourceText = source.text; documentVersion++; BindCount++; dirty = true;
            EnsureCanvasCallback();
            // 查询阶段只测量；同帧多个 Bind 在安全回调中只应用最终文档。
            RequestLayoutUpdate();
        }
        public void ClearBinding()
        {
            document = null; documentVersion++; dirty = false; singleLineEllipsized = false; singleLineMaximumWidth = 0;
            groups.Clear(); pendingLayout = null; measuredHeight = measuredWidth = 0;
            RequestLayoutUpdate();
            QueueVisualApply();
            ApplyPendingVisuals();
        }
        private void OnEnable() => EnsureCanvasCallback();
        private void OnDisable()
        {
            ClearBinding();
            // 布局循环中停用组件时，保留回调直到池清理能安全完成。
            if (!visualsDirty) RemoveCanvasCallback();
        }
        private void OnDestroy()
        { RemoveCanvasCallback(); RestoreSource(); RestoreLegacy(); DisposeMeasurer(); }
        private void OnRectTransformDimensionsChange() { if (document != null) dirty = true; }
        private void LateUpdate() => ApplyPendingVisuals();
        public void CalculateLayoutInputHorizontal() => UpdateMeasurement();
        public void CalculateLayoutInputVertical() => UpdateMeasurement();

        private static bool CanvasIsRebuilding => CanvasUpdateRegistry.IsRebuildingLayout() || CanvasUpdateRegistry.IsRebuildingGraphics();
        private void EnsureCanvasCallback()
        {
            if (canvasCallbackRegistered) return;
            // 先初始化 UGUI 注册器，使自然 Canvas 回调在其重建完成之后执行。
            CanvasUpdateRegistry.IsRebuildingLayout();
            Canvas.willRenderCanvases += ApplyPendingVisuals; canvasCallbackRegistered = true;
        }
        private void RemoveCanvasCallback()
        { if (canvasCallbackRegistered) { Canvas.willRenderCanvases -= ApplyPendingVisuals; canvasCallbackRegistered = false; } }
        private void QueueVisualApply() { visualsDirty = true; EnsureCanvasCallback(); }
        private void RequestLayoutUpdate()
        {
            layoutMarkPending = source != null;
            FlushLayoutRequest();
            LayoutInputChanged?.Invoke();
        }
        private void FlushLayoutRequest()
        {
            if (!layoutMarkPending || CanvasIsRebuilding) return;
            layoutMarkPending = false;
            if (source != null) LayoutRebuilder.MarkLayoutForRebuild(source.rectTransform);
        }
        private void UpdateMeasurement() { CheckChanges(); if (dirty) Reflow(); }
        private void RestoreSource()
        { if (hiddenSource != null) hiddenSource.canvasRenderer.SetAlpha(sourceAlpha); hiddenSource = null; }
        private void DisposeMeasurer()
        { if (!measurerDisposed) { ((IDisposable)measurer).Dispose(); measurerDisposed = true; } }
        private void ClearVisuals()
        {
            foreach (var text in textPool) if (text != null) text.gameObject.SetActive(false);
            foreach (var icon in iconPool) if (icon.Image != null) { icon.Image.sprite = null; if (icon.Overlay != null) icon.Overlay.text = ""; icon.Image.gameObject.SetActive(false); }
            ActiveFragmentCount = 0;
        }
        private void ApplyPendingVisuals()
        {
            if (CanvasIsRebuilding) return;
            // Canvas 的委托快照可能仍持有已销毁组件；不能再查询其原生属性。
            if (this == null)
            {
                RemoveCanvasCallback(); RestoreSource(); ClearVisuals(); DisposeMeasurer();
                return;
            }
            UpdateMeasurement();
            if (visualsDirty)
            {
                visualsDirty = false;
                if (hiddenSource != source || document == null) RestoreSource();
                if (reparentPool && source != null)
                {
                    reparentPool = false;
                    foreach (var text in textPool) if (text != null) text.transform.SetParent(source.rectTransform, false);
                    foreach (var icon in iconPool) if (icon.Image != null) icon.Image.transform.SetParent(source.rectTransform, false);
                }
                if (document != null && pendingLayout != null)
                {
                    SuspendLegacy();
                    if (hiddenSource == null) { hiddenSource = source; sourceAlpha = source.canvasRenderer.GetAlpha(); }
                    source.canvasRenderer.SetAlpha(0);
                    Render(pendingLayout, pendingSize, pendingRect);
                }
                else
                {
                    ClearVisuals();
                    RestoreLegacy();
                }
                RenderCount++;
            }
            FlushLayoutRequest();
            if (!isActiveAndEnabled && !visualsDirty) RemoveCanvasCallback();
        }

        private void CheckChanges()
        {
            if (document == null || source == null || style == null) return;
            // 现有消费者回退仍通过原Text赋值；条目原文变化表示已经回到纯文本管线。
            if (hostStandard != null && source.text != boundSourceText) { ClearBinding(); return; }
            var rect = source.rectTransform.rect;
            if (lastWidth != rect.width || lastHeight != rect.height || lastSize != source.fontSize || lastHostFont != source.font ||
                lastHostColor != source.color || lastPixelsPerUnit != source.pixelsPerUnit || lastCanvasScale != CanvasScale || lastStyleRevision != style.Revision || lastFontIdentity != style.FontConfigurationIdentity ||
                lastOptionsIdentity != OptionsIdentity() || lastIconRevision != (icons != null ? icons.Revision : -1)) dirty = true;
        }
        private void Reflow()
        {
            if (document == null || source == null || style == null) { dirty = false; return; }
            hostStandard?.Apply(false);
            var rect = source.rectTransform.rect;
            RefreshMeasurementCache();
            int size = fontSizeOverride ?? source.fontSize;
            var layout = singleLineEllipsized ? PrepareSingleLine(size, Math.Min(singleLineMaximumWidth, Math.Max(0, rect.width))) : Prepare(size, Math.Max(1, rect.width));
            if (!singleLineEllipsized && !fontSizeOverride.HasValue && source.resizeTextForBestFit && (layout.Height > rect.height + .01f || layout.HasOversizedAtomic))
            {
                int low = Math.Max(1, source.resizeTextMinSize), high = Math.Max(low, Math.Min(size, source.resizeTextMaxSize));
                int best = low;
                while (low <= high)
                {
                    int mid = low + (high - low) / 2; var candidate = Prepare(mid, Math.Max(1, rect.width));
                    if (candidate.Height <= rect.height + .01f && !candidate.HasOversizedAtomic) { best = mid; low = mid + 1; } else high = mid - 1;
                }
                size = best; layout = Prepare(size, Math.Max(1, rect.width));
            }
            if (layout.HasOversizedAtomic) Report("inline_icon_exceeds_host_width");
            if (!singleLineEllipsized && source.resizeTextForBestFit && layout.Height > rect.height + .01f) Report("text_exceeds_host_at_minimum_font_size");
            // 布局查询必须立即提供尺寸，但不能创建/启停 Graphic 或改父节点。
            pendingLayout = layout; pendingSize = size; pendingRect = rect; QueueVisualApply();
            bool measurementChanged = measuredWidth != layout.Width || measuredHeight != layout.Height;
            measuredWidth = layout.Width; measuredHeight = layout.Height; EffectiveFontSize = size;
            lastWidth = rect.width; lastHeight = rect.height; lastSize = source.fontSize; lastHostFont = source.font; lastHostColor = source.color;
            lastStyleRevision = style.Revision; lastIconRevision = icons != null ? icons.Revision : -1; lastPixelsPerUnit = source.pixelsPerUnit; lastCanvasScale = CanvasScale;
            lastFontIdentity = style.FontConfigurationIdentity; lastOptionsIdentity = OptionsIdentity();
            dirty = false; LayoutCount++;
            if (measurementChanged) LayoutInputChanged?.Invoke();
        }
        private GameplayTextLayout.Result Prepare(int size, float width)
        {
            groups.Clear(); var elements = BuildElements(document, size, false, groups);
            return GameplayTextLayout.Arrange(elements, width, size, style.LineSpacing, source.horizontalOverflow == HorizontalWrapMode.Wrap);
        }

        private GameplayTextLayout.Result PrepareSingleLine(int size, float maximumWidth)
        {
            groups.Clear(); var elements = BuildElements(document, size, true, groups);
            if (GameplayTextLayout.MeasureSingleLineWidth(elements) <= maximumWidth)
                return GameplayTextLayout.Arrange(elements, Math.Max(1, maximumWidth), size, 0, false);
            var marker = new List<GameplayTextLayout.Element>();
            AppendTextElements("…", new GameplayTextRunStyle(), size, true, groups, marker);
            if (marker[0].Width > maximumWidth) Report("single_line_ellipsis_exceeds_maximum_width");
            return GameplayTextLayout.EllipsizeSingleLine(elements, marker[0], maximumWidth, size);
        }

        private List<GameplayTextLayout.Element> BuildElements(GameplayTextDocument value, int size, bool singleLine, List<Group> targetGroups)
        {
            var elements = new List<GameplayTextLayout.Element>();
            foreach (var run in value.Runs)
            {
                if (run is LineBreak)
                {
                    if (singleLine) AppendTextElements(" ", run.Style, size, true, targetGroups, elements);
                    else elements.Add(new GameplayTextLayout.Element { HardBreak = true });
                    continue;
                }
                var icon = run as IconRun; GameplayIconDefinition definition; Sprite sprite;
                if (icon != null && catalog != null && catalog.TryGetIcon(icon.IconKey, out definition) && definition != null && icons != null && icons.TryResolve(definition, GameplayIconTone.Dark, out sprite))
                {
                    var height = size * style.IconHeightRatio; var imageWidth = height * sprite.rect.width / Math.Max(1f, sprite.rect.height);
                    int group = targetGroups.Count; targetGroups.Add(new Group { Icon = icon, Definition = definition, Sprite = sprite });
                    elements.Add(new GameplayTextLayout.Element { Atomic = true, Group = group, Width = imageWidth + style.IconSpacing * 2,
                        Ascent = Math.Max(0, height * .8f + style.IconBaselineOffset), Descent = Math.Max(0, height * .2f - style.IconBaselineOffset) });
                    continue;
                }
                if (icon != null) Report("missing_inline_sprite:" + icon.IconKey);
                var text = run.ToPlainText();
                if (singleLine) text = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
                if (singleLine && icon != null)
                {
                    // 缺图的显式 AltText 仍是一个图示原子，不能截成半个分数。
                    Font font = ResolveTextFont(run.Style, text); var metric = Measure(text, font, size);
                    int group = targetGroups.Count;
                    targetGroups.Add(new Group { Font = font, Color = ColorOf(run.Style.ColorRgba, source.color), Style = run.Style });
                    elements.Add(new GameplayTextLayout.Element { Atomic = true, Text = text, Group = group, Width = metric.Width, Ascent = metric.Ascent, Descent = metric.Descent });
                    continue;
                }
                AppendTextElements(text, run.Style, size, singleLine, targetGroups, elements);
            }
            return elements;
        }

        private void AppendTextElements(string text, GameplayTextRunStyle runStyle, int size, bool singleLine,
            List<Group> targetGroups, List<GameplayTextLayout.Element> elements)
        {
            var iterator = StringInfo.GetTextElementEnumerator(text);
            while (iterator.MoveNext())
            {
                var element = iterator.GetTextElement();
                if (!singleLine && (element == "\r" || element == "\n" || element == "\r\n")) { elements.Add(new GameplayTextLayout.Element { HardBreak = true }); continue; }
                Font font = ResolveTextFont(runStyle, element); var metric = Measure(element, font, size);
                Color color = ColorOf(runStyle.ColorRgba, source.color); int group = targetGroups.Count - 1;
                if (group < 0 || targetGroups[group].Icon != null || targetGroups[group].Font != font || targetGroups[group].Color != color || !targetGroups[group].Style.Equals(runStyle))
                { group = targetGroups.Count; targetGroups.Add(new Group { Font = font, Color = color, Style = runStyle }); }
                elements.Add(new GameplayTextLayout.Element { Text = element, Group = group, Width = metric.Width, Ascent = metric.Ascent, Descent = metric.Descent });
            }
        }

        private Font ResolveTextFont(GameplayTextRunStyle runStyle, string text)
        {
            Font font;
            if (!style.TryResolveFont(runStyle, hostStandard != null ? hostStandard.Font : source.font, out font)) throw new InvalidOperationException("正文真实字体绑定缺失：" + runStyle.SemanticRole + "/" + runStyle.WeightMode);
            var actualFont = style.ResolveGlyphFont(runStyle, font, text);
            if (actualFont == null) { Report("missing_glyph:" + font.name + ":" + string.Join("-", Codepoints(text))); return font; }
            return actualFont;
        }

        private void RefreshMeasurementCache()
        {
            int identity = style.FontConfigurationIdentity;
            if (metricStyleRevision != style.Revision || metricFontIdentity != identity || metricHostFont != source.font || metricPixelsPerUnit != source.pixelsPerUnit)
            {
                metrics.Clear(); metricStyleRevision = style.Revision; metricFontIdentity = identity;
                metricHostFont = source.font; metricPixelsPerUnit = source.pixelsPerUnit;
            }
        }

        private Metric Measure(string text, Font font, int size)
        {
            float pixelsPerUnit = font.dynamic ? CanvasScale : (font.fontSize > 0 ? font.fontSize / (float)size : 1);
            var key = new MetricKey { Text = text, Font = font.GetInstanceID(), Size = size, PixelsPerUnit = pixelsPerUnit }; Metric metric;
            if (metrics.TryGetValue(key, out metric)) return metric;
            var settings = source.GetGenerationSettings(Vector2.zero); settings.font = font; settings.fontSize = size;
            settings.scaleFactor = pixelsPerUnit; settings.textAnchor = TextAnchor.UpperLeft; settings.pivot = new Vector2(0, 1);
            settings.alignByGeometry = false; settings.lineSpacing = 1;
            settings.fontStyle = FontStyle.Normal; settings.richText = false; settings.resizeTextForBestFit = false;
            settings.horizontalOverflow = HorizontalWrapMode.Overflow; settings.verticalOverflow = VerticalWrapMode.Overflow;
            metric.Width = Math.Max(0, measurer.GetPreferredWidth(text, settings) / pixelsPerUnit);
            measurer.Populate(text, settings);
            float baseline = font.ascent * size / (float)Math.Max(1, font.fontSize);
            float lineHeight = measurer.lineCount > 0 ? measurer.lines[0].height / pixelsPerUnit
                : font.lineHeight * size / (float)Math.Max(1, font.fontSize);
            var vertices = measurer.verts;
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            float firstTop = 0; bool hasInk = false;
            for (int i = 0; i + 3 < vertices.Count; i += 4)
            {
                float left = float.PositiveInfinity, bottom = float.PositiveInfinity, right = float.NegativeInfinity, top = float.NegativeInfinity;
                for (int j = i; j < i + 4; j++) { var point = vertices[j].position; left = Math.Min(left, point.x); right = Math.Max(right, point.x); bottom = Math.Min(bottom, point.y); top = Math.Max(top, point.y); }
                if (right <= left || top <= bottom) continue;
                if (!hasInk) firstTop = top;
                hasInk = true; minX = Math.Min(minX, left); maxX = Math.Max(maxX, right); minY = Math.Min(minY, bottom); maxY = Math.Max(maxY, top);
            }
            if (hasInk)
            {
                int pixelSize = font.dynamic ? Math.Max(1, Mathf.RoundToInt(size * pixelsPerUnit)) : font.fontSize;
                font.RequestCharactersInTexture(text, pixelSize, FontStyle.Normal);
                foreach (char character in text)
                {
                    CharacterInfo info;
                    if (!char.IsWhiteSpace(character) && font.GetCharacterInfo(character, out info, pixelSize, FontStyle.Normal) && info.maxY > info.minY && info.maxX > info.minX)
                    { baseline = GameplayTextLayout.BaselineFromGeneratedGlyph(info.maxY, firstTop, pixelsPerUnit); break; }
                }
                metric.Ink = Rect.MinMaxRect(minX / pixelsPerUnit, minY / pixelsPerUnit, maxX / pixelsPerUnit, maxY / pixelsPerUnit);
            }
            metric.BaselineFromTop = baseline;
            metric.Ascent = Math.Max(0, Math.Max(baseline, hasInk ? metric.Ink.yMax + baseline : 0));
            metric.Descent = Math.Max(0, Math.Max(lineHeight - baseline, hasInk ? -metric.Ink.yMin - baseline : 0));
            if (metrics.Count >= 4096) metrics.Clear(); metrics.Add(key, metric); return metric;
        }

        private float CanvasScale => source != null && source.canvas != null ? source.canvas.scaleFactor : 1;

        private void Render(GameplayTextLayout.Result layout, int size, Rect rect)
        {
            int textCount = 0, iconCount = 0; float horizontal = AlignmentX(source.alignment), vertical = AlignmentY(source.alignment);
            foreach (var fragment in layout.Fragments)
            {
                var group = groups[fragment.Group];
                float x = fragment.X + Math.Max(0, rect.width - fragment.LineWidth) * horizontal;
                float y = fragment.Y + Math.Max(0, rect.height - layout.Height) * vertical;
                if (group.Icon == null)
                {
                    var text = GetText(textCount++); text.gameObject.SetActive(true);
                    SetText(text, fragment.Text, group.Font, size, group.Color);
                    var metric = Measure(fragment.Text, group.Font, size);
                    float baseline = y + fragment.Ascent;
                    Place(text.rectTransform, x, GameplayTextLayout.TextTopForBaseline(baseline, metric.BaselineFromTop), fragment.Width + .1f, fragment.Height);
                }
                else
                {
                    var slot = GetIcon(iconCount++); slot.Image.gameObject.SetActive(true);
                    slot.Image.sprite = group.Sprite; slot.Image.color = Color.white; slot.Image.type = Image.Type.Simple; slot.Image.preserveAspect = true; slot.Image.raycastTarget = false;
                    float imageWidth = Math.Max(1, fragment.Width - style.IconSpacing * 2);
                    Place(slot.Image.rectTransform, x + style.IconSpacing, y, imageWidth, fragment.Height);
                    BindOverlay(slot, group, size, imageWidth, fragment.Height);
                }
            }
            for (int i = textCount; i < textPool.Count; i++) textPool[i].gameObject.SetActive(false);
            for (int i = iconCount; i < iconPool.Count; i++) { iconPool[i].Image.sprite = null; if (iconPool[i].Overlay != null) iconPool[i].Overlay.text = ""; iconPool[i].Image.gameObject.SetActive(false); }
            ActiveFragmentCount = textCount + iconCount;
        }
        private void BindOverlay(IconSlot slot, Group group, int size, float imageWidth, float imageHeight)
        {
            if (slot.Overlay != null) { slot.Overlay.text = ""; slot.Overlay.gameObject.SetActive(false); }
            if (!group.Icon.NumberValue.HasValue) return;
            OverlayMeasurement overlay;
            if (!TryMeasureOverlay(group, size, imageWidth, imageHeight, out overlay)) return;
            if (slot.Overlay == null) slot.Overlay = NewText("Gameplay Icon Number", slot.Image.transform);
            var area = overlay.Area;
            var fitted = overlay.Metric;
            slot.Overlay.gameObject.SetActive(true);
            SetText(slot.Overlay, overlay.Value, overlay.Font, overlay.Size, ColorOf(group.Icon.OverlayTextStyle.ColorRgba, overlay.Ink));
            Place(slot.Overlay.rectTransform, area.x + (area.width - fitted.Ink.width) * .5f - fitted.Ink.xMin,
                area.y + (area.height - fitted.Ink.height) * .5f + fitted.Ink.yMax, area.width, area.height);
        }
        private bool TryMeasureOverlay(Group group, int size, float imageWidth, float imageHeight, out OverlayMeasurement result)
        {
            result = default(OverlayMeasurement);
            GameplayNumberOverlayStyle overlay;
            if (!style.TryGetNumberOverlay(group.Definition.NumberOverlayStyleKey, out overlay))
            { Report("missing_number_overlay_style:" + group.Definition.NumberOverlayStyleKey); return false; }
            var area = overlay.BoundsForIconSize(imageWidth, imageHeight);
            var width = area.width; var height = area.height;
            Font font; if (!style.TryResolveFont(group.Icon.OverlayTextStyle, hostStandard != null ? hostStandard.Font : source.font, out font)) throw new InvalidOperationException("分数叠数字体未配置。");
            var value = group.Icon.NumberValue.Value.ToString(CultureInfo.InvariantCulture);
            var actual = style.ResolveGlyphFont(group.Icon.OverlayTextStyle, font, value);
            if (actual == null) Report("missing_number_overlay_glyph:" + font.name); else font = actual;
            int overlaySize = GameplayTextLayout.FitFontSize(Math.Max(1, Mathf.RoundToInt(size * overlay.FontSizeRatio)), width, height,
                candidate => { var measured = Measure(value, font, candidate); return new GameplayTextLayout.Element { Width = Math.Max(measured.Width, measured.Ink.width), Ascent = measured.Ink.height }; });
            var fitted = Measure(value, font, overlaySize);
            result = new OverlayMeasurement { Area = area, Font = font, Value = value, Size = overlaySize, Metric = fitted, Ink = overlay.Ink,
                Fits = Math.Max(fitted.Width, fitted.Ink.width) <= width + .01f && fitted.Ink.height <= height + .01f };
            return true;
        }
        private void SuspendLegacy()
        {
            if (legacySuspended || source == null) return;
            legacyLabel = source.GetComponent<UiSemanticLabel>();
            if (legacyLabel == null) return;
            legacySuspended = true; legacyWasEnabled = legacyLabel.enabled; legacyLabel.enabled = false;
            legacyHeight = source.GetComponent<UiContentHeightLayout>();
            if (legacyHeight != null) { legacyHeightWasEnabled = legacyHeight.enabled; legacyHeight.enabled = false; }
            foreach (var layout in source.GetComponentsInChildren<LayoutGroup>(true))
            {
                if (!(layout is UiInlineTextLayout) && !(layout is UiFlowLayoutGroup)) continue;
                if (layout.gameObject == source.gameObject) continue;
                legacyVisuals.Add(new LegacyVisual { Node = layout.gameObject, Active = layout.gameObject.activeSelf });
                layout.gameObject.SetActive(false);
            }
            RequestLayoutUpdate();
        }
        private void RestoreLegacy()
        {
            if (!legacySuspended) return;
            foreach (var visual in legacyVisuals) if (visual.Node != null) visual.Node.SetActive(visual.Active);
            legacyVisuals.Clear();
            if (legacyLabel != null) legacyLabel.enabled = legacyWasEnabled;
            if (legacyHeight != null) legacyHeight.enabled = legacyHeightWasEnabled;
            legacyHeight = null;
            legacyLabel = null; legacySuspended = false;
            RequestLayoutUpdate();
        }
        private Text GetText(int index)
        { if (index == textPool.Count) textPool.Add(NewText("Gameplay Text Fragment", source.rectTransform)); return textPool[index]; }
        private IconSlot GetIcon(int index)
        {
            if (index == iconPool.Count)
            {
                var node = new GameObject("Gameplay Inline Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
                node.transform.SetParent(source.rectTransform, false); node.GetComponent<LayoutElement>().ignoreLayout = true;
                iconPool.Add(new IconSlot { Image = node.GetComponent<Image>() });
            }
            return iconPool[index];
        }
        private static Text NewText(string name, Transform parent)
        {
            var node = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
            node.transform.SetParent(parent, false); node.GetComponent<LayoutElement>().ignoreLayout = true; return node.GetComponent<Text>();
        }
        private static void SetText(Text text, string value, Font font, int size, Color color)
        {
            text.text = value; text.font = font; text.fontSize = size; text.color = color; text.fontStyle = FontStyle.Normal;
            text.supportRichText = false; text.raycastTarget = false; text.resizeTextForBestFit = false; text.alignment = TextAnchor.UpperLeft;
            text.alignByGeometry = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow; text.verticalOverflow = VerticalWrapMode.Overflow; text.lineSpacing = 1;
        }
        private static void Place(RectTransform rect, float x, float y, float width, float height)
        { rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); rect.localScale = Vector3.one; }
        private static Color ColorOf(uint? rgba, Color inherited)
        { if (!rgba.HasValue) return inherited; uint c = rgba.Value; return new Color32((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c); }
        private static float AlignmentX(TextAnchor anchor) => anchor == TextAnchor.UpperRight || anchor == TextAnchor.MiddleRight || anchor == TextAnchor.LowerRight ? 1 : anchor == TextAnchor.UpperCenter || anchor == TextAnchor.MiddleCenter || anchor == TextAnchor.LowerCenter ? .5f : 0;
        private static float AlignmentY(TextAnchor anchor) => anchor == TextAnchor.LowerLeft || anchor == TextAnchor.LowerCenter || anchor == TextAnchor.LowerRight ? 1 : anchor == TextAnchor.MiddleLeft || anchor == TextAnchor.MiddleCenter || anchor == TextAnchor.MiddleRight ? .5f : 0;
        private int OptionsIdentity() { unchecked { return (int)source.alignment * 397 ^ (int)source.horizontalOverflow * 31 ^ source.resizeTextMinSize * 17 ^ source.resizeTextMaxSize * 13 ^ (source.resizeTextForBestFit ? 1 : 0); } }
        private void Report(string key) { if (diagnosticKeys.Count >= 128) diagnosticKeys.Clear(); if (diagnosticKeys.Add(key)) Diagnostic?.Invoke(key); }
        private static IEnumerable<string> Codepoints(string text) { for (int i = 0; i < text.Length; i++) { if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { yield return "U+" + char.ConvertToUtf32(text, i).ToString("X", CultureInfo.InvariantCulture); i++; } else yield return "U+" + ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture); } }
        private sealed class Group { public Font Font; public Color Color; public GameplayTextRunStyle Style; public IconRun Icon; public GameplayIconDefinition Definition; public Sprite Sprite; }
        private sealed class IconSlot { public Image Image; public Text Overlay; }
        private struct Metric { public float Width, Ascent, Descent, BaselineFromTop; public Rect Ink; }
        private struct OverlayMeasurement { public Rect Area; public Font Font; public string Value; public int Size; public Metric Metric; public Color Ink; public bool Fits; }
        private struct LegacyVisual { public GameObject Node; public bool Active; }
        private struct MetricKey : IEquatable<MetricKey>
        { public string Text; public int Font, Size; public float PixelsPerUnit; public bool Equals(MetricKey other) => Font == other.Font && Size == other.Size && PixelsPerUnit == other.PixelsPerUnit && Text == other.Text; public override bool Equals(object other) => other is MetricKey && Equals((MetricKey)other); public override int GetHashCode() => Text.GetHashCode() ^ Font ^ Size ^ PixelsPerUnit.GetHashCode(); }
    }

    /// <summary>从条目实例的序列化Text捕获标准。来源只改内容；共同拟合允许暂时改字号。</summary>
    internal sealed class GameplayTextHostStandard
    {
        public Text Text { get; }
        public int FontSize { get; }
        public Font Font => font;
        private readonly Font font;
        private readonly FontStyle fontStyle;
        private readonly bool bestFit, richText;
        private readonly int minimum, maximum;
        private readonly float lineSpacing;
        public GameplayTextHostStandard(Text text)
        {
            Text = text; FontSize = text.fontSize; font = text.font; fontStyle = text.fontStyle;
            bestFit = text.resizeTextForBestFit; minimum = text.resizeTextMinSize; maximum = text.resizeTextMaxSize;
            richText = text.supportRichText; lineSpacing = text.lineSpacing;
        }
        public void Apply(bool restoreSize)
        {
            if (Text == null) return;
            if (CanvasUpdateRegistry.IsRebuildingLayout() || CanvasUpdateRegistry.IsRebuildingGraphics()) return;
            if (Text.font != font) Text.font = font;
            if (Text.fontStyle != fontStyle) Text.fontStyle = fontStyle;
            if (restoreSize && Text.fontSize != FontSize) Text.fontSize = FontSize;
            if (Text.resizeTextForBestFit != bestFit) Text.resizeTextForBestFit = bestFit;
            if (Text.resizeTextMinSize != minimum) Text.resizeTextMinSize = minimum;
            if (Text.resizeTextMaxSize != maximum) Text.resizeTextMaxSize = maximum;
            if (Text.supportRichText != richText) Text.supportRichText = richText;
            if (Text.lineSpacing != lineSpacing) Text.lineSpacing = lineSpacing;
        }
        public void AttachDocument()
        { var view = Text == null ? null : Text.GetComponent<GameplayTextView>(); if (view != null) view.UseHostStandard(this); }
    }
}
