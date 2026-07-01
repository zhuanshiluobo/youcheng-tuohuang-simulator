using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ExpandableInfoPanel : MonoBehaviour
    {
        private const float ExpandedWidth = 504f;
        private const float CollapsedWidth = 65f;
        private const float SectionTitleHeight = 25f;
        private const float RowHeight = 22f;
        private const float ModuleContentWidth = 420f;
        private const float ModuleContentLeftPadding = 7f;
        private const float ModuleContentRightPadding = 4f;
        private const float ModuleContentTopPadding = 2f;
        private const float ModuleContentSpacing = 2f;
        private const float RowTextInset = 6f;
        private const float HintCardPreviewMaxWidth = 320f;
        private const float HintCardPreviewMaxHeight = 420f;
        private const string HintCardResourcePath = "ProjectAssetLibrary/HintCards/提示卡";
        private const string ExpandedArrow = "◀";
        private const string CollapsedArrow = "▶";

        private readonly List<InfoModule> modules = new List<InfoModule>();

        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform scrollContent;
        private Button toggleButton;
        private Text toggleButtonText;
        private bool isExpanded;
        [SerializeField] private float panelLerpSpeed = 10f;
        [SerializeField] private float panelSnapThreshold = 0.5f;
        private float targetPanelWidth = CollapsedWidth;
        private bool isAnimating;
        private bool pendingExpandedState;
        private bool pendingTextRefresh;

        private bool initialized;
        public bool IsExpanded => isExpanded;
        public IReadOnlyList<InfoModule> Modules => modules;

        private void Awake()
        {
            Initialize(transform);
        }

        private void Update()
        {
            if (!isAnimating || panelTransform == null)
            {
                return;
            }

            var currentWidth = panelTransform.rect.width;
            var nextWidth = Mathf.Lerp(
                currentWidth,
                targetPanelWidth,
                Time.deltaTime * panelLerpSpeed);

            var finishedThisFrame = false;
            if (Mathf.Abs(nextWidth - targetPanelWidth) <= panelSnapThreshold)
            {
                nextWidth = targetPanelWidth;
                isAnimating = false;
                finishedThisFrame = true;
            }

            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, nextWidth);
            RebuildLayout();

            if (!finishedThisFrame)
            {
                return;
            }

            if (pendingExpandedState)
            {
                RefreshVisibleTextRenderers();
            }
            else
            {
                if (contentArea.gameObject.activeSelf)
                {
                    contentArea.gameObject.SetActive(false);
                }

                RebuildLayout();
            }
        }

        private void LateUpdate()
        {
            if (isExpanded &&
                contentArea != null &&
                contentArea.gameObject.activeInHierarchy &&
                pendingTextRefresh)
            {
                RefreshVisibleTextRenderers();
            }
        }

        public void Initialize(Transform parent)
        {
            if (initialized) return;
            initialized = true;

            var canvas = UguiUtility.CreateCanvas("Info Panel Canvas", 100);
            BuildPanel(canvas.transform);
            BuildDemoModules();
            SetExpandedImmediate(false);
        }

        public void SetRowValue(string moduleTitle, string label, string value)
        {
            for (var i = 0; i < modules.Count; i++)
            {
                if (modules[i].Title == moduleTitle)
                {
                    modules[i].SetRowValue(label, value);
                    RebuildLayout();
                    return;
                }
            }
        }

        public InfoModule AddModule(string title)
        {
            var module = new InfoModule(title);
            module.BuildUI(scrollContent);
            modules.Add(module);
            return module;
        }

        public void Toggle()
        {
            SetExpanded(!isExpanded);
        }

        private void SetExpanded(bool expand)
        {
            pendingExpandedState = expand;
            isExpanded = expand;
            targetPanelWidth = expand ? ExpandedWidth : CollapsedWidth;
            isAnimating = true;

            if (expand)
            {
                contentArea.gameObject.SetActive(true);
                pendingTextRefresh = true;
            }
            else
            {
                pendingTextRefresh = false;
                contentArea.gameObject.SetActive(false);
            }

            toggleButtonText.text = expand ? ExpandedArrow : CollapsedArrow;

        }

        private void SetExpandedImmediate(bool expand)
        {
            pendingExpandedState = expand;
            isExpanded = expand;
            targetPanelWidth = expand ? ExpandedWidth : CollapsedWidth;
            isAnimating = false;
            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetPanelWidth);
            contentArea.gameObject.SetActive(expand);
            toggleButtonText.text = expand ? ExpandedArrow : CollapsedArrow;
            RebuildLayout();
            if (expand)
            {
                pendingTextRefresh = true;
                RefreshVisibleTextRenderers();
            }
        }

        private void BuildPanel(Transform parent)
        {
            var panelObject = new GameObject("Sidebar Panel", typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(parent, false);

            panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0f, 0f);
            panelTransform.anchorMax = new Vector2(0f, 1f);
            panelTransform.pivot = new Vector2(0f, 0.5f);
            panelTransform.sizeDelta = new Vector2(CollapsedWidth, 0f);
            panelTransform.anchoredPosition = Vector2.zero;

            var panelImage = panelObject.GetComponent<Image>();
            panelImage.color = UiTheme.PanelBackground;

            var panelOutline = panelObject.GetComponent<Outline>();
            panelOutline.effectColor = UiTheme.GoldOutline;
            panelOutline.effectDistance = new Vector2(2f, 0f);

            BuildToggleButton(panelTransform);
            BuildContentArea(panelTransform);
        }

        private void BuildToggleButton(RectTransform parent)
        {
            var buttonObject = new GameObject("Toggle Button", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            var buttonTransform = buttonObject.GetComponent<RectTransform>();
            buttonTransform.anchorMin = new Vector2(1f, 0.5f);
            buttonTransform.anchorMax = new Vector2(1f, 0.5f);
            buttonTransform.pivot = new Vector2(1f, 0.5f);
            buttonTransform.sizeDelta = new Vector2(47f, 90f);
            buttonTransform.anchoredPosition = Vector2.zero;

            var buttonImage = buttonObject.GetComponent<Image>();
            buttonImage.color = UiTheme.PanelBackgroundLighter;

            toggleButton = buttonObject.GetComponent<Button>();
            toggleButton.onClick.AddListener(Toggle);

            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(buttonTransform, false);

            var textTransform = textObject.GetComponent<RectTransform>();
            textTransform.anchorMin = Vector2.zero;
            textTransform.anchorMax = Vector2.one;
            textTransform.offsetMin = Vector2.zero;
            textTransform.offsetMax = Vector2.zero;

            toggleButtonText = textObject.GetComponent<Text>();
            toggleButtonText.text = CollapsedArrow;
            toggleButtonText.alignment = TextAnchor.MiddleCenter;
            toggleButtonText.color = UiTheme.GoldText;
            toggleButtonText.fontSize = 22;
            toggleButtonText.fontStyle = FontStyle.Bold;
            toggleButtonText.font = FontUtility.GetLatinFont(22);
        }

        private void BuildContentArea(RectTransform parent)
        {
            contentArea = new GameObject("Content Area", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            contentArea.SetParent(parent, false);
            contentArea.anchorMin = new Vector2(0f, 0f);
            contentArea.anchorMax = new Vector2(1f, 1f);
            contentArea.pivot = new Vector2(0.5f, 0.5f);
            contentArea.offsetMin = new Vector2(11f, 11f);
            contentArea.offsetMax = new Vector2(-54f, -11f);
            contentArea.gameObject.SetActive(false);

            BuildHeader(contentArea);
            BuildScrollView(contentArea);
        }

        private void BuildHeader(RectTransform parent)
        {
            var headerObject = new GameObject("Header", typeof(RectTransform), typeof(Text), typeof(Outline));
            headerObject.transform.SetParent(parent, false);

            var headerTransform = headerObject.GetComponent<RectTransform>();
            headerTransform.anchorMin = new Vector2(0f, 1f);
            headerTransform.anchorMax = new Vector2(1f, 1f);
            headerTransform.pivot = new Vector2(0.5f, 1f);
            headerTransform.sizeDelta = new Vector2(0f, 48f);
            headerTransform.anchoredPosition = Vector2.zero;

            var headerText = headerObject.GetComponent<Text>();
            headerText.text = "信息面板";
            headerText.alignment = TextAnchor.MiddleCenter;
            headerText.color = UiTheme.GoldText;
            headerText.fontSize = 30;
            headerText.fontStyle = FontStyle.Bold;
            headerText.font = FontUtility.GetCjkFont(30);

            var headerOutline = headerObject.GetComponent<Outline>();
            headerOutline.effectColor = UiTheme.DarkShadowLight;
            headerOutline.effectDistance = new Vector2(1f, -1f);

            var separatorObject = new GameObject("Header Separator", typeof(RectTransform), typeof(Image));
            separatorObject.transform.SetParent(headerTransform, false);

            var separatorTransform = separatorObject.GetComponent<RectTransform>();
            separatorTransform.anchorMin = new Vector2(0f, 0f);
            separatorTransform.anchorMax = new Vector2(1f, 0f);
            separatorTransform.pivot = new Vector2(0.5f, 0f);
            separatorTransform.sizeDelta = new Vector2(0f, 2f);
            separatorTransform.anchoredPosition = Vector2.zero;

            separatorObject.GetComponent<Image>().color = UiTheme.GoldSeparator;
        }

        private void BuildScrollView(RectTransform parent)
        {
            var scrollObject = new GameObject("Scroll View", typeof(RectTransform), typeof(ScrollRect), typeof(Image));
            scrollObject.transform.SetParent(parent, false);

            var scrollTransform = scrollObject.GetComponent<RectTransform>();
            scrollTransform.anchorMin = new Vector2(0f, 0f);
            scrollTransform.anchorMax = new Vector2(1f, 1f);
            scrollTransform.pivot = new Vector2(0.5f, 0.5f);
            scrollTransform.offsetMin = new Vector2(0f, 0f);
            scrollTransform.offsetMax = new Vector2(0f, -48f);

            scrollObject.GetComponent<Image>().color = UiTheme.ScrollBackground;

            var scrollRect = scrollObject.GetComponent<ScrollRect>();

            var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportObject.transform.SetParent(scrollTransform, false);

            var viewportTransform = viewportObject.GetComponent<RectTransform>();
            viewportTransform.anchorMin = Vector2.zero;
            viewportTransform.anchorMax = Vector2.one;
            viewportTransform.offsetMin = Vector2.zero;
            viewportTransform.offsetMax = Vector2.zero;

            scrollRect.viewport = viewportTransform;

            scrollContent = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter))
                .GetComponent<RectTransform>();
            scrollContent.SetParent(viewportTransform, false);
            scrollContent.anchorMin = new Vector2(0f, 1f);
            scrollContent.anchorMax = new Vector2(1f, 1f);
            scrollContent.pivot = new Vector2(0.5f, 1f);
            scrollContent.sizeDelta = new Vector2(0f, 0f);
            scrollContent.anchoredPosition = Vector2.zero;

            var layoutGroup = scrollContent.GetComponent<VerticalLayoutGroup>();
            layoutGroup.childAlignment = TextAnchor.UpperCenter;
            layoutGroup.childForceExpandWidth = true;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.spacing = 5f;
            layoutGroup.padding = new RectOffset(4, 4, 4, 4);

            var sizeFitter = scrollContent.GetComponent<ContentSizeFitter>();
            sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = scrollContent;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
        }

        public InfoModule AddTextRow(InfoModule module, string label, string value)
        {
            if (module == null)
            {
                return module;
            }

            var rowRect = CreateContentItem(
                module,
                "Row: " + label,
                ModuleContentWidth - ModuleContentLeftPadding - ModuleContentRightPadding,
                RowHeight,
                new Vector2(ModuleContentLeftPadding, 0f));

            var rowText = CreateRowText(
                module.ContentRect,
                "Value: " + label,
                FormatRowText(label, value),
                UiTheme.GoldText,
                FontStyle.Bold);
            PlaceRowText(rowText, rowRect, RowTextInset, RowTextInset);

            RebuildLayout();
            return module;
        }

        private static RectTransform CreateContentItem(
            InfoModule module,
            string name,
            float width,
            float height,
            Vector2 offset)
        {
            var content = module.ContentRect;
            var y = module.AppendContentItem(height);
            var rowObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Outline), typeof(LayoutElement));
            rowObject.transform.SetParent(content, false);

            var rect = rowObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(offset.x, y + offset.y);

            var element = rowObject.GetComponent<LayoutElement>();
            element.minWidth = width;
            element.preferredWidth = width;
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleWidth = 1f;

            var background = rowObject.GetComponent<Image>();
            background.color = UiTheme.ScrollBackground;
            background.raycastTarget = false;

            var outline = rowObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.ScrollBackground;
            outline.effectDistance = new Vector2(1f, -1f);

            return rect;
        }

        private static Text CreateRowText(RectTransform parent, string name, string value, Color color, FontStyle style)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(parent, false);

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = color;
            text.fontSize = 14;
            text.fontStyle = style;
            text.font = FontUtility.GetCjkFont(14);
            text.supportRichText = false;
            text.maskable = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.FontTextureChanged();
            text.SetAllDirty();

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.DarkShadowLight;
            outline.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private static string FormatRowText(string label, string value)
        {
            return label + "：" + (value ?? string.Empty);
        }

        private static void PlaceRowText(Text text, RectTransform rowRect, float left, float right)
        {
            var rect = text.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(
                Mathf.Max(1f, rowRect.sizeDelta.x - left - right),
                rowRect.sizeDelta.y);
            rect.anchoredPosition = rowRect.anchoredPosition + new Vector2(left, 0f);
        }

        public InfoModule AddImagePreview(InfoModule module, string label, Texture2D texture)
        {
            if (module == null)
            {
                return module;
            }

            if (texture == null)
            {
                AddTextRow(module, label, "未找到");
                return module;
            }

            var aspect = texture.width / (float)texture.height;
            var previewWidth = Mathf.Min(HintCardPreviewMaxWidth, HintCardPreviewMaxHeight * aspect);
            var previewHeight = previewWidth / aspect;
            var rowHeight = previewHeight + 12f;

            var rowRect = CreateContentItem(module, "Image: " + label, ModuleContentWidth, rowHeight, Vector2.zero);

            var frameObject = new GameObject(label + " Preview Frame", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(LayoutElement));
            frameObject.transform.SetParent(rowRect, false);

            var frameRect = frameObject.GetComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0.5f, 0.5f);
            frameRect.anchorMax = new Vector2(0.5f, 0.5f);
            frameRect.pivot = new Vector2(0.5f, 0.5f);
            frameRect.sizeDelta = new Vector2(previewWidth + 8f, previewHeight + 8f);
            frameRect.anchoredPosition = Vector2.zero;

            var frameElement = frameObject.GetComponent<LayoutElement>();
            frameElement.minWidth = previewWidth + 8f;
            frameElement.preferredWidth = previewWidth + 8f;
            frameElement.minHeight = previewHeight + 8f;
            frameElement.preferredHeight = previewHeight + 8f;

            frameObject.GetComponent<Image>().color = UiTheme.ScrollBackground;
            var frameOutline = frameObject.GetComponent<Outline>();
            frameOutline.effectColor = UiTheme.GoldOutlineThin;
            frameOutline.effectDistance = new Vector2(1f, -1f);

            var imageObject = new GameObject(label + " Preview", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(frameRect, false);

            var imageRect = imageObject.GetComponent<RectTransform>();
            imageRect.anchorMin = new Vector2(0.5f, 0.5f);
            imageRect.anchorMax = new Vector2(0.5f, 0.5f);
            imageRect.pivot = new Vector2(0.5f, 0.5f);
            imageRect.sizeDelta = new Vector2(previewWidth, previewHeight);
            imageRect.anchoredPosition = Vector2.zero;

            var image = imageObject.GetComponent<RawImage>();
            image.texture = texture;
            image.color = Color.white;

            RebuildLayout();
            return module;
        }

        private void RebuildLayout()
        {
            if (scrollContent == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollContent);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelTransform);
        }

        private void RefreshVisibleTextRenderers()
        {
            if (contentArea == null || !contentArea.gameObject.activeInHierarchy)
            {
                return;
            }

            RebuildLayout();

            var texts = contentArea.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null || !text.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (text.font != null)
                {
                    text.material = text.font.material;
                }

                text.canvasRenderer.cull = false;
                text.canvasRenderer.SetAlpha(text.color.a);
                text.FontTextureChanged();
                text.SetAllDirty();
            }

            Canvas.ForceUpdateCanvases();
            pendingTextRefresh = false;
        }

        private void BuildDemoModules()
        {
            var overview = AddModule("玩家概览");
            AddTextRow(overview, "玩家", "Player 1");
            AddTextRow(overview, "颜色", "蓝色");
            AddTextRow(overview, "剩余影响力", "30");
            AddTextRow(overview, "分数", "0");

            var hintCard = AddModule("提示卡");
            AddImagePreview(hintCard, "提示卡", Resources.Load<Texture2D>(HintCardResourcePath));

            var resources = AddModule("资源状态");
            AddTextRow(resources, "源岩", "0");
            AddTextRow(resources, "源石", "0");
            AddTextRow(resources, "异铁", "0");
            AddTextRow(resources, "至纯源石", "0");
            AddTextRow(resources, "金券", "10");

            var city = AddModule("城市与行动");
            AddTextRow(city, "城市位置", "A-01");
            AddTextRow(city, "本回合", "1 / 8");
            AddTextRow(city, "行动轮", "1");
            AddTextRow(city, "已执行行动", "否");
            AddTextRow(city, "已移动城市", "否");
        }

        public sealed class InfoModule
        {
            private readonly string title;
            private RectTransform sectionTransform;
            private RectTransform contentTransform;
            private LayoutElement contentElement;
            private Text titleText;
            private bool isContentVisible = true;
            private float nextContentY;

            public string Title => title;
            public RectTransform ContentRect => contentTransform;

            internal InfoModule(string title)
            {
                this.title = title;
            }

            internal void BuildUI(RectTransform parent)
            {
                BuildSection(parent, out contentTransform, out titleText, out var button);
                var captured = this;
                button.onClick.AddListener(() => captured.ToggleContent());
            }

            public void SetContentVisible(bool visible)
            {
                isContentVisible = visible;
                contentTransform.gameObject.SetActive(visible);
                titleText.text = (visible ? "▼ " : "▶ ") + title;
            }

            internal float AppendContentItem(float height)
            {
                if (contentTransform == null || contentElement == null)
                {
                    return 0f;
                }

                var y = -(ModuleContentTopPadding + nextContentY);
                nextContentY += height + ModuleContentSpacing;
                var contentHeight = ModuleContentTopPadding + nextContentY;
                contentTransform.sizeDelta = new Vector2(ModuleContentWidth, contentHeight);
                contentElement.minHeight = contentHeight;
                contentElement.preferredHeight = contentHeight;
                return y;
            }

            public void ToggleContent()
            {
                SetContentVisible(!isContentVisible);
            }

            public void SetRowValue(string label, string value)
            {
                if (contentTransform == null)
                {
                    return;
                }

                for (var i = 0; i < contentTransform.childCount; i++)
                {
                    var row = contentTransform.GetChild(i);
                    if (!row.name.StartsWith("Value: " + label))
                    {
                        continue;
                    }

                    var text = row.GetComponent<Text>();
                    if (text != null)
                    {
                        text.text = FormatRowText(label, value);
                        text.FontTextureChanged();
                        text.SetAllDirty();
                    }

                    return;
                }
            }

            private void BuildSection(
                RectTransform parent,
                out RectTransform content,
                out Text titleTextRef,
                out Button titleButtonRef)
            {
                var sectionObject = new GameObject("Module: " + title, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
                sectionObject.transform.SetParent(parent, false);

                sectionTransform = sectionObject.GetComponent<RectTransform>();
                sectionTransform.sizeDelta = new Vector2(0f, 0f);

                var sectionLayout = sectionObject.GetComponent<VerticalLayoutGroup>();
                sectionLayout.childAlignment = TextAnchor.UpperCenter;
                sectionLayout.childForceExpandWidth = true;
                sectionLayout.childForceExpandHeight = false;
                sectionLayout.spacing = 4f;
                sectionLayout.padding = new RectOffset(4, 4, 4, 4);

                var sectionFitter = sectionObject.GetComponent<ContentSizeFitter>();
                sectionFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var titleObject = new GameObject("Section Title", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline), typeof(LayoutElement));
                titleObject.transform.SetParent(sectionTransform, false);

                var titleTransform = titleObject.GetComponent<RectTransform>();
                titleTransform.sizeDelta = new Vector2(0f, SectionTitleHeight);

                var titleElement = titleObject.GetComponent<LayoutElement>();
                titleElement.minHeight = SectionTitleHeight;
                titleElement.preferredHeight = SectionTitleHeight;
                titleElement.flexibleWidth = 1f;

                var titleBg = titleObject.GetComponent<Image>();
                titleBg.color = UiTheme.SectionTitleBackground;

                var titleOutline = titleObject.GetComponent<Outline>();
                titleOutline.effectColor = UiTheme.GoldOutlineThin;
                titleOutline.effectDistance = new Vector2(1f, -1f);

                var textObject = new GameObject("Title Text", typeof(RectTransform), typeof(Text));
                textObject.transform.SetParent(titleTransform, false);

                var textTransform = textObject.GetComponent<RectTransform>();
                textTransform.anchorMin = Vector2.zero;
                textTransform.anchorMax = Vector2.one;
                textTransform.offsetMin = new Vector2(6f, 0f);
                textTransform.offsetMax = new Vector2(-6f, 0f);

                titleTextRef = textObject.GetComponent<Text>();
                titleTextRef.text = "▼ " + title;
                titleTextRef.alignment = TextAnchor.MiddleLeft;
                titleTextRef.color = UiTheme.GoldText;
                titleTextRef.fontSize = 14;
                titleTextRef.fontStyle = FontStyle.Bold;
                titleTextRef.font = FontUtility.GetCjkFont(14);

                titleButtonRef = titleObject.GetComponent<Button>();
                titleText = titleTextRef;

                content = new GameObject("Section Content", typeof(RectTransform), typeof(LayoutElement))
                    .GetComponent<RectTransform>();
                content.SetParent(sectionTransform, false);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(0f, 1f);
                content.pivot = new Vector2(0f, 1f);
                content.sizeDelta = new Vector2(ModuleContentWidth, 0f);

                contentElement = content.GetComponent<LayoutElement>();
                contentElement.minWidth = ModuleContentWidth;
                contentElement.preferredWidth = ModuleContentWidth;
                contentElement.flexibleWidth = 1f;
            }
        }

    }
}
