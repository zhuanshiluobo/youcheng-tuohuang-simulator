using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Presentation
{
    internal sealed class ExternalCardClickHandler : MonoBehaviour, IPointerClickHandler
    {
        private Button button;
        private Action singleClick;
        private Action doubleClick;

        public void Configure(Button configuredButton, Action configuredSingleClick, Action configuredDoubleClick)
        {
            button = configuredButton;
            singleClick = configuredSingleClick;
            doubleClick = configuredDoubleClick;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (button == null || !button.IsInteractable() || eventData == null)
            {
                return;
            }

            if (eventData.clickCount >= 2)
            {
                doubleClick?.Invoke();
                return;
            }

            singleClick?.Invoke();
        }
    }

    public sealed class BuildInfoPanel : MonoBehaviour
    {
        private const float ExpandedWidth = 520f;
        private const float CollapsedWidth = 47f;
        private const float ContentLeftInset = 65f;
        private const float ExpandedHeight = 520f;
        private const float CollapsedHeight = 90f;
        private const float ToggleButtonWidth = 47f;
        private const float SectionTitleHeight = 26f;
        private const float ContentWidth = 432f;
        private const float RowSpacing = 6f;
        private const int ScrollContentHorizontalPadding = 6;
        private const float StatusRowHeight = 62f;
        private const float TextBoxHorizontalPadding = 15f;
        private const float TextBoxVerticalPadding = 4f;
        private const float ScrollSensitivity = 15f;
        private const float CityBoardSourceWidth = 2059f;
        private const float CityBoardSourceHeight = 3801f;
        private const float CityBoardSlotWidthRatio = 0.292f;
        private const float CityBoardSlotHeightRatio = 0.205f;
        private const string CityBoardImageRelativePath = "Assets/YC/Presentation/Resources/CardImages/Boards/city_board.png";
        private const float ExternalCardFramePadding = 6f;
        private const float ExternalCardAreaLeft = 72f;
        private const float ExternalCardAreaWidth = 365f;/*区域部分*/
        private const float ExternalFacilityAreaTop = 17f;
        private const float ExternalFacilityAreaHeight = 323f;
        private const float ExternalFacilityCardWidth = 99f;
        private const float ExternalFacilityCardHeight = 141f;
        private const float ExternalFacilityCardHorizontalSpacing = 25f;
        private const float ExternalFacilityCardVerticalSpacing = 22f;
        private const int ExternalFacilityColumnCount = 3;
        private const int ExternalFacilitySlotCount = 6;/*建设卡部分*/
        private const float ExternalCityStyleLeftPadding = 20f;
        private const float ExternalCityStyleAreaTop = 340f;
        private const float ExternalCityStyleAreaHeight = 330f;
        private const float ExternalCityStyleCardWidth = 143f;
        private const float ExternalCityStyleCardHeight = 91f;
        private const float ExternalCityStyleCardHorizontalSpacing = 40f;
        private const float ExternalCityStyleCardVerticalSpacing = 20f;/*样式卡部分*/
        private const int ExternalCityStyleColumnCount = 2;
        private static readonly Color UsedCityBoardSlotBackground = new Color(0.42f, 0.12f, 0.055f, 0.98f);
        private static readonly Color UsedCityBoardSlotOutline = new Color(1f, 0.55f, 0.16f, 0.95f);
        private static readonly Color UsedCityBoardSlotBadgeBackground = new Color(0.62f, 0.08f, 0.05f, 0.96f);
        private static readonly Color OccupiedCityBoardSlotBackground = new Color(0.18f, 0.105f, 0.055f, 0.82f);
        private static readonly Color InvisibleCityBoardSlotColor = new Color(1f, 1f, 1f, 0f);
        private static readonly Color ExternalCardFrameBackground = new Color(0.08f, 0.06f, 0.035f, 0.42f);
        private static readonly Color ExternalCardFrameOutline = new Color(0.78f, 0.63f, 0.38f, 0.72f);
        private static readonly Color ExternalCardBackground = new Color(0.09f, 0.07f, 0.045f, 0.72f);
        private static readonly Color ExternalCardNormalOutline = new Color(0.78f, 0.63f, 0.38f, 0.72f);
        private static readonly Color ExternalCardSelectedOutline = new Color(1f, 0.82f, 0.22f, 1f);
        private static readonly Vector2[] CityBoardSlotCenters =
        {
            new Vector2(0.176f, 0.162f),
            new Vector2(0.502f, 0.162f),
            new Vector2(0.827f, 0.162f),
            new Vector2(0.176f, 0.381f),
            new Vector2(0.502f, 0.381f),
            new Vector2(0.827f, 0.381f),
            new Vector2(0.176f, 0.600f),
            new Vector2(0.502f, 0.600f),
            new Vector2(0.827f, 0.600f),
            new Vector2(0.176f, 0.819f),
            new Vector2(0.502f, 0.819f),
            new Vector2(0.827f, 0.819f)
        };

        private readonly List<RectTransform> dynamicItems = new List<RectTransform>();
        private readonly List<ExternalCardBinding> facilityCardBindings = new List<ExternalCardBinding>();
        private readonly List<ExternalCardBinding> cityStyleCardBindings = new List<ExternalCardBinding>();
        private static readonly Dictionary<string, Texture2D> FacilityCardTextures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Texture2D> CityStyleCardTextures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Texture2D> TexturePathCache = new Dictionary<string, Texture2D>();
        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform scrollContent;
        private RectTransform externalFacilityArea;
        private RectTransform externalCityStyleArea;
        private Text toggleButtonText;
        private bool initialized;
        private bool isExpanded;
        private GameState currentState;
        private int currentPlayerId;
        private string selectedFacilityId = string.Empty;
        private string selectedCityStyleId = string.Empty;
        private Text statusText;
        private ZoomableImageViewerController cardImageViewer;

        public event Action<string> FacilityClicked;
        public event Action<string> CityStyleClicked;
        public event Action<int> CityBoardSlotClicked;

        private sealed class ExternalCardBinding
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public Button Button;
            public Outline Outline;
            public RawImage CardImage;
            public Text FallbackText;
        }

        private void Awake()
        {
            Initialize(transform);
        }

        public void Initialize(Transform parent)
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            var canvas = UguiUtility.CreateCanvas("Build Info Panel Canvas", 99);
            BuildPanel(canvas.transform);
            SetExpandedImmediate(false);
        }

        public void Refresh(GameState state, int playerId)
        {
            currentState = state;
            currentPlayerId = playerId;
            if (!initialized)
            {
                Initialize(transform);
            }

            RebuildContent();
        }

        public void SetExpanded(bool expand)
        {
            if (!initialized)
            {
                Initialize(transform);
            }

            SetExpandedImmediate(expand);
        }

        private void Toggle()
        {
            SetExpandedImmediate(!isExpanded);
        }

        private void SetExpandedImmediate(bool expand)
        {
            isExpanded = expand;
            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, expand ? ExpandedWidth : CollapsedWidth);
            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, expand ? ExpandedHeight : CollapsedHeight);
            contentArea.gameObject.SetActive(expand);
            toggleButtonText.text = expand ? "▶" : "◀";
            RebuildLayout();
        }

        private void BuildPanel(Transform parent)
        {
            var panelObject = new GameObject("Build Sidebar Panel", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(parent, false);

            panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(1f, 0.5f);
            panelTransform.anchorMax = new Vector2(1f, 0.5f);
            panelTransform.pivot = new Vector2(1f, 0.5f);
            panelTransform.sizeDelta = new Vector2(CollapsedWidth, CollapsedHeight);
            panelTransform.anchoredPosition = Vector2.zero;

            panelObject.GetComponent<Image>().color = UiTheme.PanelBackground;

            BuildToggleButton(panelTransform);
            BuildContentArea(panelTransform);
            BuildExternalCardAreas(parent);
        }

        private void BuildExternalCardAreas(Transform parent)
        {
            externalFacilityArea = CreateExternalCardArea(
                parent,
                "External Facility Supply Area",
                ExternalCardAreaLeft,
                ExternalFacilityAreaTop,
                ExternalCardAreaWidth,
                ExternalFacilityAreaHeight);

            externalCityStyleArea = CreateExternalCardArea(
                parent,
                "External City Style Area",
                ExternalCardAreaLeft,
                ExternalCityStyleAreaTop,
                ExternalCardAreaWidth,
                ExternalCityStyleAreaHeight);

            BuildExternalFacilitySlots();
        }

        private static float CalculateExternalCardAreaWidth(int columnCount, float cardWidth, float horizontalSpacing)
        {
            return ExternalCardFramePadding * 2f +
                   columnCount * cardWidth +
                   Mathf.Max(0, columnCount - 1) * horizontalSpacing;
        }

        private static float CalculateExternalCardAreaHeight(int rowCount, float cardHeight, float verticalSpacing)
        {
            return ExternalCardFramePadding * 2f +
                   rowCount * cardHeight +
                   Mathf.Max(0, rowCount - 1) * verticalSpacing;
        }

        private static RectTransform CreateExternalCardArea(
            Transform parent,
            string name,
            float left,
            float top,
            float width,
            float height)
        {
            var area = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Outline)).GetComponent<RectTransform>();
            area.SetParent(parent, false);
            area.anchorMin = new Vector2(0f, 1f);
            area.anchorMax = new Vector2(0f, 1f);
            area.pivot = new Vector2(0f, 1f);
            area.sizeDelta = new Vector2(width, height);
            area.anchoredPosition = new Vector2(left, -top);

            var image = area.GetComponent<Image>();
            image.color = ExternalCardFrameBackground;
            image.raycastTarget = false;

            var outline = area.GetComponent<Outline>();
            outline.effectColor = ExternalCardFrameOutline;
            outline.effectDistance = new Vector2(1f, -1f);
            return area;
        }

        private void BuildToggleButton(RectTransform parent)
        {
            var buttonObject = new GameObject("Toggle Button", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            var buttonTransform = buttonObject.GetComponent<RectTransform>();
            buttonTransform.anchorMin = new Vector2(0f, 0.5f);
            buttonTransform.anchorMax = new Vector2(0f, 0.5f);
            buttonTransform.pivot = new Vector2(0f, 0.5f);
            buttonTransform.sizeDelta = new Vector2(ToggleButtonWidth, CollapsedHeight);
            buttonTransform.anchoredPosition = Vector2.zero;

            buttonObject.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
            buttonObject.GetComponent<Button>().onClick.AddListener(Toggle);

            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(buttonTransform, false);
            var textTransform = textObject.GetComponent<RectTransform>();
            textTransform.anchorMin = Vector2.zero;
            textTransform.anchorMax = Vector2.one;
            textTransform.offsetMin = Vector2.zero;
            textTransform.offsetMax = Vector2.zero;

            toggleButtonText = textObject.GetComponent<Text>();
            toggleButtonText.text = "◀";
            toggleButtonText.alignment = TextAnchor.MiddleCenter;
            toggleButtonText.color = UiTheme.GoldText;
            toggleButtonText.fontSize = 22;
            toggleButtonText.fontStyle = FontStyle.Bold;
            toggleButtonText.font = FontUtility.GetLatinFont(22);
        }

        private void BuildContentArea(RectTransform parent)
        {
            contentArea = new GameObject("Content Area", typeof(RectTransform)).GetComponent<RectTransform>();
            contentArea.SetParent(parent, false);
            contentArea.anchorMin = Vector2.zero;
            contentArea.anchorMax = Vector2.one;
            contentArea.offsetMin = new Vector2(ContentLeftInset, 11f);
            contentArea.offsetMax = new Vector2(-11f, -11f);

            var titleObject = new GameObject("Header", typeof(RectTransform), typeof(Text), typeof(Outline));
            titleObject.transform.SetParent(contentArea, false);
            var titleTransform = titleObject.GetComponent<RectTransform>();
            titleTransform.anchorMin = new Vector2(0f, 1f);
            titleTransform.anchorMax = new Vector2(1f, 1f);
            titleTransform.pivot = new Vector2(0.5f, 1f);
            titleTransform.sizeDelta = new Vector2(0f, 48f);
            titleTransform.anchoredPosition = Vector2.zero;

            var titleText = titleObject.GetComponent<Text>();
            titleText.text = "建设面板";
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.color = UiTheme.GoldText;
            titleText.fontSize = 30;
            titleText.fontStyle = FontStyle.Bold;
            titleText.font = FontUtility.GetCjkFont(30);

            var scrollObject = new GameObject("Scroll View", typeof(RectTransform), typeof(ScrollRect), typeof(Image));
            scrollObject.transform.SetParent(contentArea, false);
            var scrollTransform = scrollObject.GetComponent<RectTransform>();
            scrollTransform.anchorMin = Vector2.zero;
            scrollTransform.anchorMax = Vector2.one;
            scrollTransform.offsetMin = Vector2.zero;
            scrollTransform.offsetMax = new Vector2(0f, -48f);
            scrollObject.GetComponent<Image>().color = UiTheme.ScrollBackground;

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Mask), typeof(Image)).GetComponent<RectTransform>();
            viewport.SetParent(scrollTransform, false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.02f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;

            scrollContent = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter))
                .GetComponent<RectTransform>();
            scrollContent.SetParent(viewport, false);
            scrollContent.anchorMin = new Vector2(0f, 1f);
            scrollContent.anchorMax = new Vector2(1f, 1f);
            scrollContent.pivot = new Vector2(0.5f, 1f);
            scrollContent.anchoredPosition = Vector2.zero;

            var layout = scrollContent.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = RowSpacing;
            layout.padding = new RectOffset(ScrollContentHorizontalPadding, 6, 6, 6);

            scrollContent.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scrollRect = scrollObject.GetComponent<ScrollRect>();
            scrollRect.viewport = viewport;
            scrollRect.content = scrollContent;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = ScrollSensitivity;
        }

        private void RebuildContent()
        {
            ClearDynamicItems();
            ClearExternalCityStyleCards();
            AddStatusRow();
            AddCityBoardSection();
            RebuildExternalFacilityCards();
            RebuildExternalCityStyleCards();
            AddPlayerDeclarationsSection();
            RebuildLayout();
        }

        private void ClearDynamicItems()
        {
            for (var i = dynamicItems.Count - 1; i >= 0; i--)
            {
                if (dynamicItems[i] != null)
                {
                    if (UnityEngine.Application.isPlaying)
                    {
                        Destroy(dynamicItems[i].gameObject);
                    }
                    else
                    {
                        DestroyImmediate(dynamicItems[i].gameObject);
                    }
                }
            }

            dynamicItems.Clear();
        }

        private void ClearExternalCityStyleCards()
        {
            cityStyleCardBindings.Clear();
            ClearChildren(externalCityStyleArea);
        }

        private static void ClearChildren(RectTransform parent)
        {
            if (parent == null)
            {
                return;
            }

            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (UnityEngine.Application.isPlaying)
                {
                    Destroy(child.gameObject);
                }
                else
                {
                    DestroyImmediate(child.gameObject);
                }
            }
        }

        private void RebuildExternalFacilityCards()
        {
            if (externalFacilityArea == null)
            {
                return;
            }

            var count = currentState == null
                ? 0
                : Mathf.Min(ExternalFacilitySlotCount, currentState.Decks.FacilitySupply.Count);
            for (var i = 0; i < facilityCardBindings.Count; i++)
            {
                var binding = facilityCardBindings[i];
                if (i >= count)
                {
                    BindExternalFacilitySlot(binding, string.Empty, string.Empty, null);
                    continue;
                }

                var facilityId = currentState.Decks.FacilitySupply[i];
                var facility = FacilityCardDatabase.Get(facilityId);
                var label = facility == null ? facilityId : facility.Name;
                BindExternalFacilitySlot(binding, facilityId, label, TryLoadFacilityCardTexture(facilityId));
            }
        }

        private void BuildExternalFacilitySlots()
        {
            facilityCardBindings.Clear();
            for (var i = 0; i < ExternalFacilitySlotCount; i++)
            {
                var binding = CreateExternalFacilitySlot(i);
                facilityCardBindings.Add(binding);
                binding.Button.gameObject.AddComponent<ExternalCardClickHandler>().Configure(
                    binding.Button,
                    () => OnExternalFacilitySlotClicked(binding),
                    () => OpenCardImage(
                        binding.Label,
                        binding.CardImage == null ? null : binding.CardImage.texture as Texture2D));
                BindExternalFacilitySlot(binding, string.Empty, string.Empty, null);
            }
        }

        private ExternalCardBinding CreateExternalFacilitySlot(int index)
        {
            var slotObject = new GameObject(
                "BuildSlot_" + (index + 1),
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            slotObject.transform.SetParent(externalFacilityArea, false);

            var rect = slotObject.GetComponent<RectTransform>();
            SetExternalCardRect(
                rect,
                index,
                ExternalFacilityColumnCount,
                ExternalFacilityCardWidth,
                ExternalFacilityCardHeight,
                ExternalFacilityCardHorizontalSpacing,
                ExternalFacilityCardVerticalSpacing);

            slotObject.GetComponent<Image>().color = ExternalCardBackground;
            var outline = slotObject.GetComponent<Outline>();
            SetExternalCardOutline(outline, false);

            var imageObject = new GameObject("Card Image", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(rect, false);
            var imageRect = imageObject.GetComponent<RectTransform>();
            imageRect.anchorMin = Vector2.zero;
            imageRect.anchorMax = Vector2.one;
            imageRect.offsetMin = new Vector2(3f, 3f);
            imageRect.offsetMax = new Vector2(-3f, -3f);
            var cardImage = imageObject.GetComponent<RawImage>();
            cardImage.color = Color.white;
            cardImage.raycastTarget = false;

            var fallbackText = CreateText(
                rect,
                string.Empty,
                12,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter);
            fallbackText.gameObject.name = "Fallback Label";
            fallbackText.resizeTextForBestFit = true;
            fallbackText.resizeTextMinSize = 8;
            fallbackText.resizeTextMaxSize = 12;
            fallbackText.raycastTarget = false;

            return new ExternalCardBinding
            {
                Button = slotObject.GetComponent<Button>(),
                Outline = outline,
                CardImage = cardImage,
                FallbackText = fallbackText
            };
        }

        private void BindExternalFacilitySlot(
            ExternalCardBinding binding,
            string facilityId,
            string label,
            Texture2D texture)
        {
            binding.Id = facilityId;
            binding.Label = label;
            binding.Button.interactable = !string.IsNullOrEmpty(facilityId);
            binding.CardImage.texture = texture;
            binding.CardImage.gameObject.SetActive(texture != null);
            binding.FallbackText.text = string.IsNullOrEmpty(facilityId) ? "空卡位" : label;
            binding.FallbackText.gameObject.SetActive(texture == null);
            SetExternalCardOutline(
                binding.Outline,
                !string.IsNullOrEmpty(facilityId) && facilityId == selectedFacilityId);
        }

        private void OnExternalFacilitySlotClicked(ExternalCardBinding binding)
        {
            if (binding == null || string.IsNullOrEmpty(binding.Id))
            {
                return;
            }

            ToggleFacilitySelection(binding.Id, binding.Label);
        }

        private void RebuildExternalCityStyleCards()
        {
            if (externalCityStyleArea == null)
            {
                return;
            }

            var styleIds = GetCurrentCityStyleSupplyIds();
            var count = Mathf.Min(6, styleIds.Count);
            for (var i = 0; i < count; i++)
            {
                var cityStyleId = styleIds[i];
                var cityStyle = CityStyleDatabase.Get(cityStyleId);
                var label = cityStyle == null ? cityStyleId : cityStyle.Name;
                var button = CreateExternalCardButton(
                    externalCityStyleArea,
                    "城市样式 " + (i + 1),
                    i,
                    ExternalCityStyleColumnCount,
                    ExternalCityStyleCardWidth,
                    ExternalCityStyleCardHeight,
                    ExternalCityStyleCardHorizontalSpacing,
                    ExternalCityStyleCardVerticalSpacing,
                    ExternalCityStyleLeftPadding,
                    TryLoadCityStyleCardTexture(cityStyleId),
                    label,
                    cityStyleId == selectedCityStyleId,
                    cityStyleCardBindings,
                    cityStyleId);

                button.gameObject.AddComponent<ExternalCardClickHandler>().Configure(
                    button,
                    () => ToggleCityStyleSelection(cityStyleId, label),
                    () => OpenCardImage(label, TryLoadCityStyleCardTexture(cityStyleId)));
            }
        }

        private void OpenCardImage(string cardName, Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            if (cardImageViewer == null)
            {
                var viewerObject = new GameObject("Card Image Viewer");
                viewerObject.transform.SetParent(transform, false);
                cardImageViewer = viewerObject.AddComponent<ZoomableImageViewerController>();
            }

            cardImageViewer.Configure("Card Image", cardName, 1, _ => texture);
            cardImageViewer.Open();
        }

        private IReadOnlyList<string> GetCurrentCityStyleSupplyIds()
        {
            return CityStyleDatabase.PresentationSupplyIds;
        }

        private static Button CreateExternalCardButton(
            RectTransform parent,
            string name,
            int index,
            int columnCount,
            float cardWidth,
            float cardHeight,
            float horizontalSpacing,
            float verticalSpacing,
            float leftPadding,
            Texture2D texture,
            string fallbackLabel,
            bool selected,
            List<ExternalCardBinding> bindings,
            string id)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            SetExternalCardRect(
                rect,
                index,
                columnCount,
                cardWidth,
                cardHeight,
                horizontalSpacing,
                verticalSpacing,
                leftPadding);

            buttonObject.GetComponent<Image>().color = ExternalCardBackground;
            var outline = buttonObject.GetComponent<Outline>();
            SetExternalCardOutline(outline, selected);

            if (texture != null)
            {
                var imageObject = new GameObject("卡图", typeof(RectTransform), typeof(RawImage));
                imageObject.transform.SetParent(rect, false);
                var imageRect = imageObject.GetComponent<RectTransform>();
                imageRect.anchorMin = Vector2.zero;
                imageRect.anchorMax = Vector2.one;
                imageRect.offsetMin = new Vector2(3f, 3f);
                imageRect.offsetMax = new Vector2(-3f, -3f);

                var rawImage = imageObject.GetComponent<RawImage>();
                rawImage.texture = texture;
                rawImage.color = Color.white;
                rawImage.raycastTarget = false;
            }
            else
            {
                var text = CreateText(rect, fallbackLabel, 12, FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter);
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 8;
                text.resizeTextMaxSize = 12;
                text.raycastTarget = false;
            }

            bindings.Add(new ExternalCardBinding
            {
                Id = id,
                Outline = outline
            });

            return buttonObject.GetComponent<Button>();
        }

        private static void SetExternalCardRect(
            RectTransform rect,
            int index,
            int columnCount,
            float cardWidth,
            float cardHeight,
            float horizontalSpacing,
            float verticalSpacing,
            float leftPadding = ExternalCardFramePadding)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(cardWidth, cardHeight);
            rect.anchoredPosition = new Vector2(
                leftPadding + (index % columnCount) * (cardWidth + horizontalSpacing),
                -ExternalCardFramePadding - (index / columnCount) * (cardHeight + verticalSpacing));
        }

        private void UpdateExternalCardHighlights()
        {
            UpdateExternalCardHighlights(facilityCardBindings, selectedFacilityId);
            UpdateExternalCardHighlights(cityStyleCardBindings, selectedCityStyleId);
        }

        private void ToggleFacilitySelection(string facilityId, string label)
        {
            if (selectedFacilityId == facilityId)
            {
                selectedFacilityId = string.Empty;
                SetStatus("已取消设施选择。");
                UpdateExternalCardHighlights();
                if (FacilityClicked != null)
                {
                    FacilityClicked(string.Empty);
                }

                return;
            }

            selectedFacilityId = facilityId;
            SetStatus("已选择设施：" + label);
            UpdateExternalCardHighlights();
            if (FacilityClicked != null)
            {
                FacilityClicked(facilityId);
            }
        }

        private void ToggleCityStyleSelection(string cityStyleId, string label)
        {
            if (selectedCityStyleId == cityStyleId)
            {
                selectedCityStyleId = string.Empty;
                SetStatus("已取消城市样式选择。");
                UpdateExternalCardHighlights();
                if (CityStyleClicked != null)
                {
                    CityStyleClicked(string.Empty);
                }

                return;
            }

            selectedCityStyleId = cityStyleId;
            SetStatus("已选择城市样式：" + label);
            UpdateExternalCardHighlights();
            if (CityStyleClicked != null)
            {
                CityStyleClicked(cityStyleId);
            }
        }

        private static void UpdateExternalCardHighlights(List<ExternalCardBinding> bindings, string selectedId)
        {
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                if (binding == null || binding.Outline == null)
                {
                    continue;
                }

                SetExternalCardOutline(
                    binding.Outline,
                    !string.IsNullOrEmpty(binding.Id) && binding.Id == selectedId);
            }
        }

        private static void SetExternalCardOutline(Outline outline, bool selected)
        {
            outline.effectColor = selected ? ExternalCardSelectedOutline : ExternalCardNormalOutline;
            outline.effectDistance = selected ? new Vector2(3f, -3f) : new Vector2(1f, -1f);
        }

        private void AddStatusRow()
        {
            statusText = AddTextBox(
                "当前选择",
                "当前选择：点击设施、槽位或城市样式方框进行测试。",
                StatusRowHeight,
                FontStyle.Bold);
        }

        private void AddCityBoardSection()
        {
            AddSectionTitle("城市面板");
            var boardImage = TryLoadCityBoardTexture();
            var boardSize = CalculateCityBoardDisplaySize(boardImage);
            var board = AddPanelItem("City Board", boardSize.y);
            RectTransform slotRoot = board;
            if (boardImage != null)
            {
                var imageObject = new GameObject("城市面板底图", typeof(RectTransform), typeof(RawImage));
                imageObject.transform.SetParent(board, false);
                var rect = imageObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = boardSize;
                rect.anchoredPosition = Vector2.zero;

                var rawImage = imageObject.GetComponent<RawImage>();
                rawImage.texture = boardImage;
                rawImage.color = Color.white;
                rawImage.raycastTarget = false;
                slotRoot = rect;
            }

            for (var i = 0; i < BuildFacilityService.CityBoardSlotCount; i++)
            {
                var slotIndex = i;
                var isUsedForDeclaration = IsCityBoardSlotUsedForDeclaration(slotIndex);
                var facilityId = GetCityBoardSlotFacilityId(i);
                var label = GetCityBoardSlotLabel(i);
                var isEmpty = string.IsNullOrEmpty(facilityId);
                var button = CreateCityBoardSlotButton(slotRoot, slotIndex, facilityId, label, isEmpty, isUsedForDeclaration, boardSize);
                var rect = button.GetComponent<RectTransform>();
                if (isUsedForDeclaration)
                {
                    AddUsedCityBoardSlotBadge(slotRoot, slotIndex, rect.anchoredPosition, boardSize);
                }

                button.onClick.AddListener(() =>
                {
                    SetStatus("已选择城市面板槽位：" + (slotIndex + 1));
                    if (CityBoardSlotClicked != null)
                    {
                        CityBoardSlotClicked(slotIndex);
                    }
                });
            }
        }

        private void AddFacilitySupplySection()
        {
            AddSectionTitle("公开可建设设施");
            AddTextBox("设施牌堆", "剩余牌堆：" + GetFacilityDeckCount(), 28f, FontStyle.Bold);

            if (currentState == null || currentState.Decks.FacilitySupply.Count <= 0)
            {
                AddTextBox("设施供应区", "当前没有公开设施。", 36f, FontStyle.Bold);
                return;
            }

            for (var i = 0; i < currentState.Decks.FacilitySupply.Count; i++)
            {
                var facilityId = currentState.Decks.FacilitySupply[i];
                var facility = FacilityCardDatabase.Get(facilityId);
                var label = facility == null ? facilityId : facility.Name + "  分数 " + facility.Score;
                var button = AddButtonBox("设施 " + (i + 1), label, 46f);
                button.onClick.AddListener(() =>
                {
                    ToggleFacilitySelection(facilityId, label);
                });
            }
        }

        private void AddCityStyleSection()
        {
            AddSectionTitle("城市样式");
            var styleIds = currentState != null && currentState.Decks.CityStyleSupply.Count > 0
                ? currentState.Decks.CityStyleSupply
                : CityStyleDatabase.DefaultSupplyIds;

            for (var i = 0; i < styleIds.Count; i++)
            {
                var cityStyleId = styleIds[i];
                var cityStyle = CityStyleDatabase.Get(cityStyleId);
                var label = cityStyle == null
                    ? cityStyleId
                    : cityStyle.Name + "  " + cityStyle.Level + "级  分数 " + cityStyle.Score;
                var button = AddButtonBox("城市样式 " + (i + 1), label, 58f);
                button.onClick.AddListener(() =>
                {
                    ToggleCityStyleSelection(cityStyleId, label);
                });
            }
        }

        private void AddPlayerDeclarationsSection()
        {
            AddSectionTitle("玩家宣告");
            if (currentState == null || currentState.Players.Count <= 0)
            {
                AddTextBox("玩家宣告", "暂无玩家数据。", 36f, FontStyle.Bold);
                return;
            }

            for (var i = 0; i < currentState.Players.Count; i++)
            {
                var player = currentState.Players[i];
                AddTextBox(
                    "玩家 " + player.PlayerId,
                    player.Name + "：" + FormatDeclaredCityStyles(player),
                    36f,
                    FontStyle.Bold);
            }
        }

        private string GetCityBoardSlotFacilityId(int slotIndex)
        {
            if (currentState != null)
            {
                for (var i = 0; i < currentState.Map.Facilities.Count; i++)
                {
                    var placement = currentState.Map.Facilities[i];
                    if (placement.PlayerId == currentPlayerId && placement.CityBoardSlotIndex == slotIndex)
                    {
                        return placement.FacilityCardId;
                    }
                }
            }

            return string.Empty;
        }

        private string GetCityBoardSlotLabel(int slotIndex)
        {
            var facilityId = GetCityBoardSlotFacilityId(slotIndex);
            if (!string.IsNullOrEmpty(facilityId))
            {
                var facility = FacilityCardDatabase.Get(facilityId);
                return facility == null ? facilityId : facility.Name;
            }

            return "空位 " + (slotIndex + 1);
        }

        private bool IsCityBoardSlotUsedForDeclaration(int slotIndex)
        {
            var player = currentState == null ? null : currentState.FindPlayer(currentPlayerId);
            if (player == null || player.DeclaredCityStyles == null)
            {
                return false;
            }

            for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                var declaration = player.DeclaredCityStyles[i];
                if (declaration == null || declaration.UsedCityBoardSlotIndexes == null)
                {
                    continue;
                }

                if (declaration.UsedCityBoardSlotIndexes.Contains(slotIndex))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsCityBoardSlotEmpty(int slotIndex)
        {
            if (currentState == null)
            {
                return true;
            }

            for (var i = 0; i < currentState.Map.Facilities.Count; i++)
            {
                var placement = currentState.Map.Facilities[i];
                if (placement.PlayerId == currentPlayerId && placement.CityBoardSlotIndex == slotIndex)
                {
                    return false;
                }
            }

            return true;
        }

        private static Button CreateCityBoardSlotButton(
            RectTransform parent,
            int slotIndex,
            string facilityId,
            string label,
            bool isEmpty,
            bool isUsedForDeclaration,
            Vector2 boardSize)
        {
            var buttonObject = new GameObject("槽位 " + (slotIndex + 1), typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(
                boardSize.x * CityBoardSlotWidthRatio,
                boardSize.y * CityBoardSlotHeightRatio);
            rect.anchoredPosition = GetCityBoardSlotAnchoredPosition(slotIndex, boardSize);

            var image = buttonObject.GetComponent<Image>();
            image.color = isEmpty
                ? InvisibleCityBoardSlotColor
                : isUsedForDeclaration ? UsedCityBoardSlotBackground : OccupiedCityBoardSlotBackground;

            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = isEmpty
                ? InvisibleCityBoardSlotColor
                : isUsedForDeclaration ? UsedCityBoardSlotOutline : UiTheme.GoldOutlineThin;
            outline.effectDistance = isUsedForDeclaration ? new Vector2(2f, -2f) : new Vector2(1f, -1f);

            rect.localEulerAngles = isUsedForDeclaration
                ? new Vector3(0f, 0f, 180f)
                : Vector3.zero;

            if (!isEmpty)
            {
                AddFacilityCardImage(rect, facilityId);
            }

            var text = CreateText(rect, isEmpty ? string.Empty : label, 14, FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 10;
            text.resizeTextMaxSize = 14;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.raycastTarget = false;
            text.gameObject.SetActive(!isEmpty);

            return buttonObject.GetComponent<Button>();
        }

        private static void AddFacilityCardImage(RectTransform parent, string facilityId)
        {
            var texture = TryLoadFacilityCardTexture(facilityId);
            if (texture == null)
            {
                return;
            }

            var imageObject = new GameObject("设施卡图", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(parent, false);
            var rect = imageObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(4f, 4f);
            rect.offsetMax = new Vector2(-4f, -4f);

            var rawImage = imageObject.GetComponent<RawImage>();
            rawImage.texture = texture;
            rawImage.color = Color.white;
            rawImage.raycastTarget = false;
        }

        private static Vector2 GetCityBoardSlotAnchoredPosition(int slotIndex, Vector2 boardSize)
        {
            var center = CityBoardSlotCenters[Mathf.Clamp(slotIndex, 0, CityBoardSlotCenters.Length - 1)];
            return new Vector2(
                (center.x - 0.5f) * boardSize.x,
                (0.5f - center.y) * boardSize.y);
        }

        private static Vector2 CalculateCityBoardDisplaySize(Texture2D texture)
        {
            var sourceWidth = texture == null ? CityBoardSourceWidth : texture.width;
            var sourceHeight = texture == null ? CityBoardSourceHeight : texture.height;
            var height = ContentWidth * sourceHeight / sourceWidth;
            return new Vector2(ContentWidth, height);
        }

        private static Texture2D TryLoadFacilityCardTexture(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId))
            {
                return null;
            }

            Texture2D cached;
            if (FacilityCardTextures.TryGetValue(facilityId, out cached))
            {
                return cached;
            }

            var facility = FacilityCardDatabase.Get(facilityId);
            string imageRelativePath;
            if (facility == null ||
                !CardImagePathCatalog.TryGetFacilityImageRelativePath(facilityId, out imageRelativePath))
            {
                return null;
            }

            var texture = TryLoadTextureByRelativePath(imageRelativePath, facility.Name);
            if (texture != null)
            {
                FacilityCardTextures[facilityId] = texture;
            }

            return texture;
        }

        private static Texture2D TryLoadCityStyleCardTexture(string cityStyleId)
        {
            if (string.IsNullOrEmpty(cityStyleId))
            {
                return null;
            }

            Texture2D cached;
            if (CityStyleCardTextures.TryGetValue(cityStyleId, out cached))
            {
                return cached;
            }

            var cityStyle = CityStyleDatabase.Get(cityStyleId);
            string imageRelativePath;
            if (cityStyle == null ||
                !CardImagePathCatalog.TryGetCityStyleImageRelativePath(cityStyleId, out imageRelativePath))
            {
                return null;
            }

            var texture = TryLoadTextureByRelativePath(imageRelativePath, cityStyle.Name);
            if (texture != null)
            {
                CityStyleCardTextures[cityStyleId] = texture;
            }

            return texture;
        }

        private static Texture2D TryLoadTextureByRelativePath(string relativePath, string textureName)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            Texture2D cached;
            if (TexturePathCache.TryGetValue(relativePath, out cached))
            {
                return cached;
            }

            var resourcePath = TryGetResourcesPath(relativePath);
            if (!string.IsNullOrEmpty(resourcePath))
            {
                var resourceTexture = Resources.Load<Texture2D>(resourcePath);
                if (resourceTexture != null)
                {
                    TexturePathCache[relativePath] = resourceTexture;
                    return resourceTexture;
                }
            }

            var candidates = new[]
            {
                Path.Combine(UnityEngine.Application.dataPath, "..", relativePath),
                Path.Combine(UnityEngine.Application.dataPath, "..", "..", "..", relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", relativePath)
            };

            for (var i = 0; i < candidates.Length; i++)
            {
                var path = Path.GetFullPath(candidates[i]);
                if (!File.Exists(path))
                {
                    continue;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(File.ReadAllBytes(path)))
                {
                    texture.name = textureName;
                    TexturePathCache[relativePath] = texture;
                    return texture;
                }

                UnityEngine.Object.Destroy(texture);
            }

            return null;
        }

        private static string TryGetResourcesPath(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/');
            var marker = "/Resources/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            var resourcePath = normalized.Substring(markerIndex + marker.Length);
            var extension = Path.GetExtension(resourcePath);
            return string.IsNullOrEmpty(extension)
                ? resourcePath
                : resourcePath.Substring(0, resourcePath.Length - extension.Length);
        }

        private static void AddUsedCityBoardSlotBadge(RectTransform parent, int slotIndex, Vector2 slotCenterPosition, Vector2 boardSize)
        {
            var badgeObject = new GameObject("槽位 " + (slotIndex + 1) + " 已使用标记", typeof(RectTransform), typeof(Image), typeof(Outline));
            badgeObject.transform.SetParent(parent, false);
            var rect = badgeObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(boardSize.x * 0.125f, boardSize.y * 0.022f);
            rect.anchoredPosition = slotCenterPosition + new Vector2(boardSize.x * 0.136f, boardSize.y * 0.082f);

            var image = badgeObject.GetComponent<Image>();
            image.color = UsedCityBoardSlotBadgeBackground;
            image.raycastTarget = false;

            var outline = badgeObject.GetComponent<Outline>();
            outline.effectColor = UsedCityBoardSlotOutline;
            outline.effectDistance = new Vector2(1f, -1f);

            var text = CreateText(rect, "已使用", 12, FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter);
            text.raycastTarget = false;
        }

        private int GetFacilityDeckCount()
        {
            return currentState == null || currentState.Decks == null ? 0 : currentState.Decks.FacilityDeck.Count;
        }

        private static string FormatDeclaredCityStyles(PlayerState player)
        {
            if (player == null || player.DeclaredCityStyleIds.Count <= 0)
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

        private void AddSectionTitle(string title)
        {
            var item = AddPanelItem("Section " + title, SectionTitleHeight);
            item.GetComponent<Image>().color = UiTheme.SectionTitleBackground;
            var text = CreateText(item, title, 15, FontStyle.Bold, UiTheme.GoldText, TextAnchor.MiddleLeft);
            var rect = text.GetComponent<RectTransform>();
            rect.offsetMin = new Vector2(TextBoxHorizontalPadding, 0f);
            rect.offsetMax = new Vector2(-TextBoxHorizontalPadding, 0f);
        }

        private Text AddTextBox(string name, string value, float height, FontStyle style)
        {
            var item = AddPanelItem(name, height);
            var text = CreateText(item, value, 14, style, UiTheme.ValueText, TextAnchor.MiddleLeft);
            var rect = text.GetComponent<RectTransform>();
            rect.offsetMin = new Vector2(TextBoxHorizontalPadding, TextBoxVerticalPadding);
            rect.offsetMax = new Vector2(-TextBoxHorizontalPadding, -TextBoxVerticalPadding);
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private Button AddButtonBox(string name, string label, float height)
        {
            var item = AddPanelItem(name, height);
            return CreateButton(item, name, label);
        }

        private RectTransform AddPanelItem(string name, float height)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Outline), typeof(LayoutElement))
                .GetComponent<RectTransform>();
            item.SetParent(scrollContent, false);
            item.sizeDelta = new Vector2(ContentWidth, height);
            dynamicItems.Add(item);

            var image = item.GetComponent<Image>();
            image.color = UiTheme.ScrollBackground;
            image.raycastTarget = false;

            var outline = item.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            var element = item.GetComponent<LayoutElement>();
            element.minWidth = ContentWidth;
            element.preferredWidth = ContentWidth;
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleWidth = 1f;
            return item;
        }

        private Button CreateButton(RectTransform parent, string name, string label)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(5f, 5f);
            rect.offsetMax = new Vector2(-5f, -5f);

            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);

            var text = CreateText(rect, label, 13, FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 10;
            text.resizeTextMaxSize = 13;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return buttonObject.GetComponent<Button>();
        }

        private static Text CreateText(
            RectTransform parent,
            string value,
            int fontSize,
            FontStyle style,
            Color color,
            TextAnchor alignment)
        {
            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Outline));
            textObject.transform.SetParent(parent, false);
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.maskable = true;

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.DarkShadowLight;
            outline.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private void SetStatus(string value)
        {
            if (statusText != null)
            {
                statusText.text = value;
                statusText.SetAllDirty();
            }
        }

        private void RebuildLayout()
        {
            if (scrollContent == null || panelTransform == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollContent);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelTransform);
        }

        private static Texture2D TryLoadCityBoardTexture()
        {
            var candidates = new[]
            {
                Path.Combine(UnityEngine.Application.dataPath, "..", CityBoardImageRelativePath),
                Path.Combine(UnityEngine.Application.dataPath, "..", "..", "..", CityBoardImageRelativePath),
                Path.Combine(Directory.GetCurrentDirectory(), CityBoardImageRelativePath),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", CityBoardImageRelativePath)
            };

            for (var i = 0; i < candidates.Length; i++)
            {
                var path = Path.GetFullPath(candidates[i]);
                if (!File.Exists(path))
                {
                    continue;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(File.ReadAllBytes(path)))
                {
                    texture.name = "城市面板";
                    return texture;
                }
            }

            return null;
        }
    }
}
