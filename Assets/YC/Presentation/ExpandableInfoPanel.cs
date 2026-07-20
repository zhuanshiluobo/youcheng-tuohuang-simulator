using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.State;
using YC.Presentation.Workflows;

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
        private CharacterCardPanelViewModel lastCharacterViewModel;
        private System.Action<string, Vector2> beginCharacterCardDrag;
        private System.Action<Vector2> updateCharacterCardDrag;
        private System.Action<string, Vector2> endCharacterCardDrag;
        private ZoomableImageViewerController characterCardImageViewer;
        private Canvas characterCardDragCanvas;
        private RectTransform characterCardDragGhost;

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

        public void SetPlayerDeclarations(IReadOnlyList<PlayerState> players)
        {
            InfoModule declarations = null;
            for (var i = 0; i < modules.Count; i++)
            {
                if (modules[i].Title == "玩家宣告")
                {
                    declarations = modules[i];
                    break;
                }
            }

            if (declarations == null)
            {
                declarations = AddModule("玩家宣告");
            }

            declarations.ClearContent();
            if (players == null || players.Count == 0)
            {
                AddTextRow(declarations, "状态", "暂无玩家数据。");
            }
            else
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    AddTextRow(declarations, player.Name, FormatDeclaredCityStyles(player));
                }
            }

            RebuildLayout();
        }

        public void SetCharacterCards(
            CharacterCardPanelViewModel viewModel,
            bool showUseOptions,
            System.Action<string> coverCard,
            System.Action<string, string, IReadOnlyDictionary<string, string>> useCard)
        {
            lastCharacterViewModel = viewModel;
            var characterCards = FindOrAddModule("角色牌");
            characterCards.ClearContent();
            if (viewModel == null)
            {
                AddTextRow(characterCards, "状态", "暂无角色牌数据。");
                RebuildLayout();
                return;
            }

            if (viewModel.CanCover && !string.IsNullOrEmpty(viewModel.InteractionStatus))
            {
                AddTextRow(characterCards, "盖放提示", viewModel.InteractionStatus);
            }

            AddTextRow(characterCards, "手牌区", viewModel.HandCards.Count + " 张");
            if (viewModel.HandCards.Count == 0)
            {
                AddTextRow(characterCards, "手牌", "暂无手牌");
            }
            else
            {
                AddCharacterHandCardStrip(characterCards, viewModel.HandCards);
            }

            AddTextRow(characterCards, "弃牌区", viewModel.DiscardCards.Count + " 张");
            if (viewModel.DiscardCards.Count == 0)
            {
                AddTextRow(characterCards, "弃牌", "暂无弃牌");
            }
            else
            {
                AddCharacterDiscardCardStrip(characterCards, viewModel.DiscardCards);
            }

            RebuildLayout();
        }

        public void ConfigureCharacterCardDragInteraction(
            System.Action<string, Vector2> beginDrag,
            System.Action<Vector2> drag,
            System.Action<string, Vector2> endDrag)
        {
            beginCharacterCardDrag = beginDrag;
            updateCharacterCardDrag = drag;
            endCharacterCardDrag = endDrag;
        }

        public void OpenCoveredCharacterCardViewer()
        {
            if (lastCharacterViewModel != null && !string.IsNullOrEmpty(lastCharacterViewModel.CoveredCardId))
            {
                OpenCoveredCharacterCard(lastCharacterViewModel);
            }
        }

        public void CloseCharacterCardViewer()
        {
            if (characterCardImageViewer != null)
            {
                characterCardImageViewer.Close();
            }
        }

        private InfoModule FindOrAddModule(string title)
        {
            for (var i = 0; i < modules.Count; i++)
            {
                if (modules[i].Title == title)
                {
                    return modules[i];
                }
            }

            return AddModule(title);
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

        public void SetExpandedState(bool expand)
        {
            SetExpanded(expand);
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

        private void AddActionRow(
            InfoModule module,
            string objectName,
            string label,
            bool interactable,
            System.Action onClick)
        {
            var rowRect = CreateContentItem(
                module,
                objectName,
                ModuleContentWidth - ModuleContentLeftPadding - ModuleContentRightPadding,
                30f,
                new Vector2(ModuleContentLeftPadding, 0f));
            var button = rowRect.gameObject.AddComponent<Button>();
            button.interactable = interactable;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            var image = rowRect.GetComponent<Image>();
            image.raycastTarget = true;
            image.color = interactable ? UiTheme.PanelBackgroundLighter : UiTheme.ScrollBackground;
            var text = CreateRowText(rowRect, "Label", label, UiTheme.GoldText, FontStyle.Bold);
            text.alignment = TextAnchor.MiddleCenter;
            PlaceRowText(text, rowRect, RowTextInset, RowTextInset);
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
            if (rect.parent == rowRect)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.offsetMin = new Vector2(left, 0f);
                rect.offsetMax = new Vector2(-right, 0f);
                return;
            }

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

        private static Texture2D TryLoadCardTexture(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            var normalized = relativePath.Replace('\\', '/');
            const string marker = "/Resources/";
            var markerIndex = normalized.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return null;
            var resourcePath = normalized.Substring(markerIndex + marker.Length);
            var extensionIndex = resourcePath.LastIndexOf('.');
            if (extensionIndex > 0) resourcePath = resourcePath.Substring(0, extensionIndex);
            return Resources.Load<Texture2D>(resourcePath);
        }

        private void AddCharacterHandCardStrip(
            InfoModule module,
            IReadOnlyList<CharacterCardHandItemViewModel> cards)
        {
            var rowRect = CreateContentItem(
                module,
                "Character Hand Card Strip",
                ModuleContentWidth - ModuleContentLeftPadding - ModuleContentRightPadding,
                122f,
                new Vector2(ModuleContentLeftPadding, 0f));
            var count = Mathf.Max(1, cards == null ? 0 : cards.Count);
            var gap = 6f;
            var cardWidth = Mathf.Min(74f, (rowRect.sizeDelta.x - gap * (count - 1)) / count);
            var startX = (rowRect.sizeDelta.x - (cardWidth * count + gap * (count - 1))) * 0.5f;
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var item = cards[i];
                var capturedItem = item;
                var cardObject = CreateCharacterCardThumbnail(
                    rowRect,
                    "Character Hand Card Image: " + i,
                    TryLoadCardTexture(item.FrontImageRelativePath),
                    new Vector2(startX + i * (cardWidth + gap), -6f),
                    new Vector2(cardWidth, 110f),
                    () => OpenHandCharacterCard(capturedItem));
                if (item.CanCover && cardObject != null)
                {
                    var cardId = item.CardId;
                    cardObject.GetComponent<CardPointerInteraction>().ConfigureDrag(
                        () => true,
                        eventData => BeginCharacterCardDrag(cardObject, cardId, eventData),
                        UpdateCharacterCardDrag,
                        eventData => EndCharacterCardDrag(cardId, eventData),
                        DestroyCharacterCardDragGhost);
                }
            }
        }

        private void AddCharacterDiscardCardStrip(
            InfoModule module,
            IReadOnlyList<CharacterCardHandItemViewModel> cards)
        {
            var rowRect = CreateContentItem(
                module,
                "Character Discard Card Strip",
                ModuleContentWidth - ModuleContentLeftPadding - ModuleContentRightPadding,
                122f,
                new Vector2(ModuleContentLeftPadding, 0f));
            var count = Mathf.Max(1, cards == null ? 0 : cards.Count);
            var gap = 6f;
            var cardWidth = Mathf.Min(74f, (rowRect.sizeDelta.x - gap * (count - 1)) / count);
            var startX = (rowRect.sizeDelta.x - (cardWidth * count + gap * (count - 1))) * 0.5f;
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var item = cards[i];
                var capturedItem = item;
                CreateCharacterCardThumbnail(
                    rowRect,
                    "Character Discard Card Image: " + i,
                    TryLoadCardTexture(item.FrontImageRelativePath),
                    new Vector2(startX + i * (cardWidth + gap), -6f),
                    new Vector2(cardWidth, 110f),
                    () => OpenDiscardCharacterCard(capturedItem));
            }
        }

        private GameObject CreateCharacterCardThumbnail(
            RectTransform parent,
            string objectName,
            Texture2D texture,
            Vector2 position,
            Vector2 size,
            System.Action onClick)
        {
            var cardObject = new GameObject(objectName, typeof(RectTransform), typeof(RawImage), typeof(Button), typeof(Outline));
            cardObject.transform.SetParent(parent, false);
            var rect = cardObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            var image = cardObject.GetComponent<RawImage>();
            image.texture = texture;
            image.color = texture == null ? UiTheme.DisabledButtonBackground : Color.white;
            image.raycastTarget = true;
            var outline = cardObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(2f, -2f);
            var button = cardObject.GetComponent<Button>();
            button.interactable = texture != null;
            cardObject.AddComponent<CardPointerInteraction>().ConfigureClick(button, onClick, null);
            return cardObject;
        }

        private void BeginCharacterCardDrag(
            GameObject sourceCard,
            string cardId,
            PointerEventData eventData)
        {
            DestroyCharacterCardDragGhost();
            CreateCharacterCardDragGhost(sourceCard, eventData);
            if (eventData != null)
            {
                beginCharacterCardDrag?.Invoke(cardId, eventData.position);
            }
        }

        private void UpdateCharacterCardDrag(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            MoveCharacterCardDragGhost(eventData.position, eventData.pressEventCamera);
            updateCharacterCardDrag?.Invoke(eventData.position);
        }

        private void EndCharacterCardDrag(string cardId, PointerEventData eventData)
        {
            if (eventData != null)
            {
                endCharacterCardDrag?.Invoke(cardId, eventData.position);
            }

            DestroyCharacterCardDragGhost();
        }

        private void CreateCharacterCardDragGhost(GameObject sourceCard, PointerEventData eventData)
        {
            var sourceImage = sourceCard == null ? null : sourceCard.GetComponent<RawImage>();
            var sourceRect = sourceCard == null ? null : sourceCard.GetComponent<RectTransform>();
            var sourceCanvas = sourceCard == null ? null : sourceCard.GetComponentInParent<Canvas>();
            characterCardDragCanvas = sourceCanvas == null ? null : sourceCanvas.rootCanvas;
            if (characterCardDragCanvas == null || sourceImage == null || sourceRect == null)
            {
                return;
            }

            var ghostObject = new GameObject(
                "Character Card Drag Ghost",
                typeof(RectTransform),
                typeof(RawImage),
                typeof(CanvasGroup),
                typeof(Outline));
            ghostObject.transform.SetParent(characterCardDragCanvas.transform, false);
            ghostObject.transform.SetAsLastSibling();

            characterCardDragGhost = ghostObject.GetComponent<RectTransform>();
            characterCardDragGhost.anchorMin = new Vector2(0.5f, 0.5f);
            characterCardDragGhost.anchorMax = new Vector2(0.5f, 0.5f);
            characterCardDragGhost.pivot = new Vector2(0.5f, 0.5f);
            characterCardDragGhost.sizeDelta = sourceRect.rect.size * 1.08f;

            var ghostImage = ghostObject.GetComponent<RawImage>();
            ghostImage.texture = sourceImage.texture;
            ghostImage.uvRect = sourceImage.uvRect;
            ghostImage.color = new Color(1f, 1f, 1f, 0.68f);
            ghostImage.raycastTarget = false;

            var group = ghostObject.GetComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var outline = ghostObject.GetComponent<Outline>();
            outline.effectColor = new Color(1f, 0.82f, 0.35f, 0.85f);
            outline.effectDistance = new Vector2(2f, -2f);
            if (eventData != null)
            {
                MoveCharacterCardDragGhost(eventData.position, eventData.pressEventCamera);
            }
        }

        private void MoveCharacterCardDragGhost(Vector2 screenPosition, Camera eventCamera)
        {
            if (characterCardDragCanvas == null || characterCardDragGhost == null)
            {
                return;
            }

            var canvasRect = characterCardDragCanvas.GetComponent<RectTransform>();
            var camera = characterCardDragCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : (eventCamera == null ? characterCardDragCanvas.worldCamera : eventCamera);
            Vector2 localPoint;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPosition, camera, out localPoint))
            {
                characterCardDragGhost.anchoredPosition = localPoint;
            }
        }

        private void DestroyCharacterCardDragGhost()
        {
            if (characterCardDragGhost != null)
            {
                var ghostObject = characterCardDragGhost.gameObject;
                if (UnityEngine.Application.isPlaying)
                {
                    Destroy(ghostObject);
                }
                else
                {
                    DestroyImmediate(ghostObject);
                }
            }

            characterCardDragGhost = null;
            characterCardDragCanvas = null;
        }

        private void OpenHandCharacterCard(CharacterCardHandItemViewModel item)
        {
            if (item == null)
            {
                return;
            }

            var texture = TryLoadCardTexture(item.FrontImageRelativePath);
            OpenCharacterCardViewer(
                item.DisplayName,
                texture,
                string.Empty,
                null,
                string.Empty,
                null);
        }

        private void OpenDiscardCharacterCard(CharacterCardHandItemViewModel item)
        {
            if (item == null)
            {
                return;
            }

            OpenCharacterCardViewer(
                item.DisplayName + "（弃牌区）",
                TryLoadCardTexture(item.FrontImageRelativePath),
                string.Empty,
                null,
                string.Empty,
                null);
        }

        private void OpenCoveredCharacterCard(CharacterCardPanelViewModel viewModel)
        {
            var frontTexture = TryLoadCardTexture(viewModel.CoveredFrontImageRelativePath);
            var cardName = CharacterCardPanelPresenter.ResolveCardDisplayName(viewModel.CoveredCardId);
            OpenCharacterCardViewer(
                "已盖放角色牌（" + cardName + "）",
                frontTexture,
                string.Empty,
                null,
                string.Empty,
                null);
        }

        private void OpenCharacterCardViewer(
            string cardName,
            Texture2D texture,
            string primaryActionText,
            System.Action primaryAction,
            string secondaryActionText,
            System.Action secondaryAction)
        {
            if (texture == null)
            {
                return;
            }

            EnsureCharacterCardViewer();

            characterCardImageViewer.DisableReferenceCollapse();
            characterCardImageViewer.Configure("Character Card Image", cardName, 1, _ => texture);
            characterCardImageViewer.ConfigureActions(primaryActionText, primaryAction, secondaryActionText, secondaryAction);
            characterCardImageViewer.Open();
        }

        private void EnsureCharacterCardViewer()
        {
            if (characterCardImageViewer != null)
            {
                return;
            }

            var viewerObject = new GameObject("Character Card Image Viewer");
            viewerObject.transform.SetParent(transform, false);
            characterCardImageViewer = viewerObject.AddComponent<ZoomableImageViewerController>();
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
            var resources = AddModule("资源状态");
            AddTextRow(resources, "源岩", "0");
            AddTextRow(resources, "源石", "0");
            AddTextRow(resources, "异铁", "0");
            AddTextRow(resources, "至纯源石", "0");
            AddTextRow(resources, "金券", "10");

            var declarations = AddModule("玩家宣告");
            AddTextRow(declarations, "状态", "暂无玩家数据。");

            var characterCards = AddModule("角色牌");
            AddTextRow(characterCards, "状态", "暂无角色牌数据。");

        }

        private static string FormatDeclaredCityStyles(PlayerState player)
        {
            if (player == null || player.DeclaredCityStyleIds == null || player.DeclaredCityStyleIds.Count == 0)
            {
                return "暂无宣告";
            }

            var names = new List<string>();
            for (var i = 0; i < player.DeclaredCityStyleIds.Count; i++)
            {
                var style = CityStyleDatabase.Get(player.DeclaredCityStyleIds[i]);
                names.Add(style == null ? player.DeclaredCityStyleIds[i] : style.Name);
            }

            return string.Join("、", names.ToArray());
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

            public void ClearContent()
            {
                if (contentTransform == null)
                {
                    return;
                }

                for (var i = contentTransform.childCount - 1; i >= 0; i--)
                {
                    var child = contentTransform.GetChild(i);
                    if (UnityEngine.Application.isPlaying)
                    {
                        Object.Destroy(child.gameObject);
                    }
                    else
                    {
                        Object.DestroyImmediate(child.gameObject);
                    }
                }

                nextContentY = 0f;
                contentTransform.sizeDelta = new Vector2(ModuleContentWidth, ModuleContentTopPadding);
                if (contentElement != null)
                {
                    contentElement.minHeight = ModuleContentTopPadding;
                    contentElement.preferredHeight = ModuleContentTopPadding;
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
