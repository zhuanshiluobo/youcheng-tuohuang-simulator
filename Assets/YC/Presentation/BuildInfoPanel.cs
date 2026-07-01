using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Presentation
{
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
        private const string CityBoardImageRelativePath = "游城拓荒/素材/城市面板.png";
        private static readonly Color UsedCityBoardSlotBackground = new Color(0.42f, 0.12f, 0.055f, 0.98f);
        private static readonly Color UsedCityBoardSlotOutline = new Color(1f, 0.55f, 0.16f, 0.95f);
        private static readonly Color UsedCityBoardSlotBadgeBackground = new Color(0.62f, 0.08f, 0.05f, 0.96f);
        private static readonly Color OccupiedCityBoardSlotBackground = new Color(0.18f, 0.105f, 0.055f, 0.82f);
        private static readonly Color InvisibleCityBoardSlotColor = new Color(1f, 1f, 1f, 0f);
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
        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform scrollContent;
        private Text toggleButtonText;
        private bool initialized;
        private bool isExpanded;
        private GameState currentState;
        private int currentPlayerId;
        private string selectedFacilityId = string.Empty;
        private string selectedCityStyleId = string.Empty;
        private Text statusText;

        public event Action<string> FacilityClicked;
        public event Action<string> CityStyleClicked;
        public event Action<int> CityBoardSlotClicked;

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
            AddStatusRow();
            AddCityBoardSection();
            AddFacilitySupplySection();
            AddCityStyleSection();
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
                var label = GetCityBoardSlotLabel(i);
                var isEmpty = IsCityBoardSlotEmpty(slotIndex);
                var button = CreateCityBoardSlotButton(slotRoot, slotIndex, label, isEmpty, isUsedForDeclaration, boardSize);
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
                    selectedFacilityId = facilityId;
                    SetStatus("已选择设施：" + label);
                    if (FacilityClicked != null)
                    {
                        FacilityClicked(facilityId);
                    }
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
                    selectedCityStyleId = cityStyleId;
                    SetStatus("已选择城市样式：" + label);
                    if (CityStyleClicked != null)
                    {
                        CityStyleClicked(cityStyleId);
                    }
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

        private string GetCityBoardSlotLabel(int slotIndex)
        {
            if (currentState != null)
            {
                for (var i = 0; i < currentState.Map.Facilities.Count; i++)
                {
                    var placement = currentState.Map.Facilities[i];
                    if (placement.PlayerId == currentPlayerId && placement.CityBoardSlotIndex == slotIndex)
                    {
                        var facility = FacilityCardDatabase.Get(placement.FacilityCardId);
                        return facility == null ? placement.FacilityCardId : facility.Name;
                    }
                }
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

            var text = CreateText(rect, isEmpty ? string.Empty : label, 14, FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 10;
            text.resizeTextMaxSize = 14;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.raycastTarget = false;
            text.gameObject.SetActive(!isEmpty);

            return buttonObject.GetComponent<Button>();
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
                Path.Combine(Directory.GetCurrentDirectory(), CityBoardImageRelativePath)
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
