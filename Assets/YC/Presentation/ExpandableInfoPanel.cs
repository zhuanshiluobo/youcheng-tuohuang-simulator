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
        [SerializeField] private ExpandableInfoPanelView view;
        [SerializeField] private ExpandableInfoPanelLayoutProfile layoutProfile;
        [SerializeField] private float panelLerpSpeed = 10f;
        [SerializeField] private float panelSnapThreshold = 0.5f;

        private readonly List<InfoModule> modules = new List<InfoModule>();
        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform scrollContent;
        private Button toggleButton;
        private Text toggleButtonText;
        private bool initialized;
        private bool isExpanded;
        private bool isAnimating;
        private bool pendingExpandedState;
        private bool pendingTextRefresh;
        private float targetPanelWidth;
        private CharacterCardPanelViewModel lastCharacterViewModel;
        private System.Action<string, Vector2> beginCharacterCardDrag;
        private System.Action<Vector2> updateCharacterCardDrag;
        private System.Action<string, Vector2> endCharacterCardDrag;
        private ZoomableImageViewerController characterCardImageViewer;
        private Canvas characterCardDragCanvas;
        private RectTransform characterCardDragGhost;
        private CardVisualCatalog cardVisualCatalog;

        public bool IsExpanded => isExpanded;
        public IReadOnlyList<InfoModule> Modules => modules;
        public ExpandableInfoPanelView View => view;
        public ExpandableInfoPanelLayoutProfile LayoutProfile => layoutProfile;

        public bool TryValidateConfiguration(out string reason)
        {
            reason = string.Empty;
            if (layoutProfile == null || !layoutProfile.TryValidateConfiguration(out reason))
            {
                reason = string.IsNullOrEmpty(reason)
                    ? "信息面板缺少显式布局 Profile。"
                    : reason;
                return false;
            }

            if (view == null || !view.TryValidateConfiguration(out reason) || !view.IsBoundTo(this))
            {
                reason = string.IsNullOrEmpty(reason)
                    ? "信息面板固定 View 配置无效。"
                    : reason;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public bool ConfigureCardVisualCatalog(CardVisualCatalog configuredCatalog)
        {
            var reason = "缺少 CardVisualCatalog。";
            if (configuredCatalog == null ||
                !configuredCatalog.TryValidateConfiguration(out reason))
            {
                Debug.LogError("[ExpandableInfoPanel] CardVisualCatalog 配置无效：" + reason, this);
                return false;
            }

            cardVisualCatalog = configuredCatalog;
            return true;
        }

        private void Awake()
        {
            if (!Bind(view)) enabled = false;
        }

        private void OnDestroy()
        {
            if (toggleButton != null) toggleButton.onClick.RemoveListener(Toggle);
            DestroyCharacterCardDragGhost();
        }

        private void Update()
        {
            if (!isAnimating || panelTransform == null) return;
            var nextWidth = Mathf.Lerp(
                panelTransform.rect.width,
                targetPanelWidth,
                Time.deltaTime * panelLerpSpeed);
            var finished = Mathf.Abs(nextWidth - targetPanelWidth) <= panelSnapThreshold;
            if (finished)
            {
                nextWidth = targetPanelWidth;
                isAnimating = false;
            }

            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, nextWidth);
            RebuildLayout();
            if (!finished) return;
            if (pendingExpandedState) RefreshVisibleTextRenderers();
            else contentArea.gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            if (isExpanded && contentArea != null && contentArea.gameObject.activeInHierarchy && pendingTextRefresh)
            {
                RefreshVisibleTextRenderers();
            }
        }

        public bool Bind(ExpandableInfoPanelView configuredView)
        {
            if (initialized) return configuredView == view;
            var reason = string.Empty;
            if (layoutProfile == null ||
                !layoutProfile.TryValidateConfiguration(out reason) ||
                configuredView == null ||
                !configuredView.TryValidateConfiguration(out reason) ||
                !configuredView.IsBoundTo(this))
            {
                Debug.LogError(
                    "[ExpandableInfoPanel] View 配置无效：" +
                    (configuredView == null ? "缺少序列化 View。" : reason),
                    this);
                return false;
            }

            view = configuredView;
            panelTransform = view.PanelTransform;
            contentArea = view.ContentArea;
            scrollContent = view.ScrollContent;
            toggleButton = view.ToggleButton;
            toggleButtonText = view.ToggleButtonText;
            targetPanelWidth = layoutProfile.CollapsedWidth;
            toggleButton.onClick.RemoveListener(Toggle);
            toggleButton.onClick.AddListener(Toggle);
            BuildInitialModules();
            SetExpandedImmediate(false);
            initialized = true;
            return true;
        }

        public void Toggle() => SetExpanded(!isExpanded);
        public void SetExpandedState(bool expand) => SetExpanded(expand);

        public void SetRowValue(string moduleTitle, string label, string value)
        {
            for (var i = 0; i < modules.Count; i++)
            {
                if (modules[i].Title != moduleTitle) continue;
                modules[i].SetRowValue(label, value);
                RebuildLayout();
                return;
            }
        }

        public void SetPlayerDeclarations(IReadOnlyList<PlayerState> players)
        {
            var declarations = FindOrAddModule("玩家宣告");
            declarations.ClearContent();
            if (players == null || players.Count == 0)
            {
                AddTextRow(declarations, "状态", "暂无玩家数据。");
            }
            else
            {
                for (var i = 0; i < players.Count; i++)
                {
                    AddTextRow(declarations, players[i].Name, FormatDeclaredCityStyles(players[i]));
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
                return;
            }

            if (viewModel.CanCover && !string.IsNullOrEmpty(viewModel.InteractionStatus))
            {
                AddTextRow(characterCards, "盖放提示", viewModel.InteractionStatus);
            }

            AddTextRow(characterCards, "手牌区", viewModel.HandCards.Count + " 张");
            if (viewModel.HandCards.Count == 0) AddTextRow(characterCards, "手牌", "暂无手牌");
            else AddCharacterCardStrip(characterCards, viewModel.HandCards, false);

            AddTextRow(characterCards, "弃牌区", viewModel.DiscardCards.Count + " 张");
            if (viewModel.DiscardCards.Count == 0) AddTextRow(characterCards, "弃牌", "暂无弃牌");
            else AddCharacterCardStrip(characterCards, viewModel.DiscardCards, true);
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
            if (lastCharacterViewModel == null || string.IsNullOrEmpty(lastCharacterViewModel.CoveredCardId)) return;
            var cardName = CharacterCardPanelPresenter.ResolveCardDisplayName(lastCharacterViewModel.CoveredCardId);
            OpenCharacterCardViewer(
                "已盖放角色牌（" + cardName + "）",
                GetCharacterCardTexture(lastCharacterViewModel.CoveredCardId));
        }

        public void CloseCharacterCardViewer() => characterCardImageViewer?.Close();

        public InfoModule AddModule(string title)
        {
            var moduleView = InstantiateTemplate(view.ModuleTemplate, scrollContent, "Module: " + title);
            var module = new InfoModule(title, moduleView, layoutProfile);
            modules.Add(module);
            return module;
        }

        public InfoModule AddTextRow(InfoModule module, string label, string value)
        {
            if (module == null) return null;
            var row = InstantiateContentItem(
                module,
                view.RowTemplate,
                "Row: " + label,
                layoutProfile.ModuleContentWidth -
                layoutProfile.ModuleContentLeftPadding -
                layoutProfile.ModuleContentRightPadding,
                layoutProfile.RowHeight,
                layoutProfile.TextRowOffset);
            row.PrimaryText.gameObject.name = "Value: " + label;
            row.PrimaryText.text = FormatRowText(label, value);
            row.PrimaryText.alignment = TextAnchor.MiddleLeft;
            row.Button.enabled = false;
            row.Background.raycastTarget = false;
            RebuildLayout();
            return module;
        }

        public InfoModule AddImagePreview(InfoModule module, string label, Texture2D texture)
        {
            if (module == null) return null;
            if (texture == null) return AddTextRow(module, label, "未找到");
            var aspect = texture.width / (float)texture.height;
            var previewWidth = Mathf.Min(
                layoutProfile.PreviewMaxWidth,
                layoutProfile.PreviewMaxHeight * aspect);
            var previewHeight = previewWidth / aspect;
            var row = InstantiateContentItem(
                module,
                view.ImagePreviewTemplate,
                "Image: " + label,
                layoutProfile.ModuleContentWidth,
                previewHeight + layoutProfile.PreviewRowExtraHeight,
                Vector2.zero);
            row.PreviewFrame.sizeDelta = new Vector2(
                previewWidth + layoutProfile.PreviewFramePadding,
                previewHeight + layoutProfile.PreviewFramePadding);
            row.RawImage.rectTransform.sizeDelta = new Vector2(previewWidth, previewHeight);
            row.RawImage.texture = texture;
            row.RawImage.color = Color.white;
            RebuildLayout();
            return module;
        }

        private InfoModule FindOrAddModule(string title)
        {
            for (var i = 0; i < modules.Count; i++)
            {
                if (modules[i].Title == title) return modules[i];
            }

            return AddModule(title);
        }

        private void SetExpanded(bool expand)
        {
            if (expand && view != null && view.Root != null)
            {
                // 与建设卡区共用 HUD Canvas 时，通过兄弟顺序保证展开面板位于最上层。
                view.Root.SetAsLastSibling();
            }

            pendingExpandedState = expand;
            isExpanded = expand;
            targetPanelWidth = expand ? layoutProfile.ExpandedWidth : layoutProfile.CollapsedWidth;
            isAnimating = true;
            contentArea.gameObject.SetActive(expand);
            pendingTextRefresh = expand;
            toggleButtonText.text = expand ? "◀" : "▶";
        }

        private void SetExpandedImmediate(bool expand)
        {
            pendingExpandedState = expand;
            isExpanded = expand;
            targetPanelWidth = expand ? layoutProfile.ExpandedWidth : layoutProfile.CollapsedWidth;
            isAnimating = false;
            panelTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetPanelWidth);
            contentArea.gameObject.SetActive(expand);
            toggleButtonText.text = expand ? "◀" : "▶";
            RebuildLayout();
        }

        private void AddCharacterCardStrip(
            InfoModule module,
            IReadOnlyList<CharacterCardHandItemViewModel> cards,
            bool discard)
        {
            var strip = InstantiateContentItem(
                module,
                view.CardStripTemplate,
                discard ? "Character Discard Card Strip" : "Character Hand Card Strip",
                layoutProfile.ModuleContentWidth -
                layoutProfile.ModuleContentLeftPadding -
                layoutProfile.ModuleContentRightPadding,
                layoutProfile.CardStripHeight,
                layoutProfile.CardStripOffset);
            var count = Mathf.Max(1, cards == null ? 0 : cards.Count);
            var gap = layoutProfile.CardStripGap;
            var width = strip.Root.sizeDelta.x;
            var cardWidth = Mathf.Min(
                layoutProfile.CardStripMaxCardWidth,
                (width - gap * (count - 1)) / count);
            var startX = (width - (cardWidth * count + gap * (count - 1))) * 0.5f;
            for (var i = 0; cards != null && i < cards.Count; i++)
            {
                var item = cards[i];
                var captured = item;
                var card = CreateCharacterCardThumbnail(
                    strip.ContentRoot,
                    (discard ? "Character Discard Card Image: " : "Character Hand Card Image: ") + i,
                    GetCharacterCardTexture(item.CardId),
                    new Vector2(
                        startX + i * (cardWidth + gap),
                        layoutProfile.CardStripCardY),
                    new Vector2(cardWidth, layoutProfile.CardStripCardHeight),
                    () => OpenCharacterCardViewer(
                        discard ? captured.DisplayName + "（弃牌区）" : captured.DisplayName,
                        GetCharacterCardTexture(captured.CardId)));
                if (discard || !item.CanCover) continue;
                var cardId = item.CardId;
                card.PointerInteraction.ConfigureDrag(
                    () => true,
                    eventData => BeginCharacterCardDrag(card.gameObject, cardId, eventData),
                    UpdateCharacterCardDrag,
                    eventData => EndCharacterCardDrag(cardId, eventData),
                    DestroyCharacterCardDragGhost);
            }
        }

        private ExpandableInfoItemView CreateCharacterCardThumbnail(
            RectTransform parent,
            string objectName,
            Texture2D texture,
            Vector2 position,
            Vector2 size,
            System.Action onClick)
        {
            var card = InstantiateTemplate(view.CardThumbnailTemplate, parent, objectName);
            card.Root.anchorMin = layoutProfile.TopLeftAnchor;
            card.Root.anchorMax = layoutProfile.TopLeftAnchor;
            card.Root.pivot = layoutProfile.TopLeftAnchor;
            card.Root.sizeDelta = size;
            card.Root.anchoredPosition = position;
            card.RawImage.texture = texture;
            card.RawImage.color = texture == null ? UiTheme.DisabledButtonBackground : Color.white;
            card.RawImage.raycastTarget = true;
            card.Button.interactable = texture != null;
            card.PointerInteraction.ConfigureClick(card.Button, onClick, null);
            return card;
        }

        private void BeginCharacterCardDrag(GameObject sourceCard, string cardId, PointerEventData eventData)
        {
            DestroyCharacterCardDragGhost();
            CreateCharacterCardDragGhost(sourceCard, eventData);
            if (eventData != null) beginCharacterCardDrag?.Invoke(cardId, eventData.position);
        }

        private void UpdateCharacterCardDrag(PointerEventData eventData)
        {
            if (eventData == null) return;
            MoveCharacterCardDragGhost(eventData.position, eventData.pressEventCamera);
            updateCharacterCardDrag?.Invoke(eventData.position);
        }

        private void EndCharacterCardDrag(string cardId, PointerEventData eventData)
        {
            if (eventData != null) endCharacterCardDrag?.Invoke(cardId, eventData.position);
            DestroyCharacterCardDragGhost();
        }

        private void CreateCharacterCardDragGhost(GameObject sourceCard, PointerEventData eventData)
        {
            var sourceImage = sourceCard == null ? null : sourceCard.GetComponent<RawImage>();
            var sourceRect = sourceCard == null ? null : sourceCard.GetComponent<RectTransform>();
            var sourceCanvas = sourceCard == null ? null : sourceCard.GetComponentInParent<Canvas>();
            characterCardDragCanvas = sourceCanvas == null ? null : sourceCanvas.rootCanvas;
            if (characterCardDragCanvas == null || sourceImage == null || sourceRect == null) return;
            var ghost = InstantiateTemplate(
                view.DragGhostTemplate,
                characterCardDragCanvas.transform,
                "Character Card Drag Ghost");
            ghost.transform.SetAsLastSibling();
            characterCardDragGhost = ghost.Root;
            characterCardDragGhost.anchorMin = layoutProfile.CenterAnchor;
            characterCardDragGhost.anchorMax = layoutProfile.CenterAnchor;
            characterCardDragGhost.pivot = layoutProfile.CenterAnchor;
            characterCardDragGhost.sizeDelta = sourceRect.rect.size * layoutProfile.DragGhostScale;
            ghost.RawImage.texture = sourceImage.texture;
            ghost.RawImage.uvRect = sourceImage.uvRect;
            ghost.RawImage.color = layoutProfile.DragGhostColor;
            ghost.RawImage.raycastTarget = false;
            ghost.CanvasGroup.interactable = false;
            ghost.CanvasGroup.blocksRaycasts = false;
            if (eventData != null) MoveCharacterCardDragGhost(eventData.position, eventData.pressEventCamera);
        }

        private void MoveCharacterCardDragGhost(Vector2 screenPosition, Camera eventCamera)
        {
            if (characterCardDragCanvas == null || characterCardDragGhost == null) return;
            var canvasRect = characterCardDragCanvas.GetComponent<RectTransform>();
            var camera = characterCardDragCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : (eventCamera == null ? characterCardDragCanvas.worldCamera : eventCamera);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, screenPosition, camera, out var localPoint))
            {
                characterCardDragGhost.anchoredPosition = localPoint;
            }
        }

        private void DestroyCharacterCardDragGhost()
        {
            if (characterCardDragGhost != null) DestroyDynamicObject(characterCardDragGhost.gameObject);
            characterCardDragGhost = null;
            characterCardDragCanvas = null;
        }

        private void OpenCharacterCardViewer(string cardName, Texture2D texture)
        {
            if (texture == null) return;
            if (characterCardImageViewer == null)
            {
                characterCardImageViewer = ZoomableImageViewerController.InstantiateRegistered(
                    transform,
                    "Character Card Image Viewer");
            }

            if (characterCardImageViewer == null) return;
            characterCardImageViewer.DisableReferenceCollapse();
            characterCardImageViewer.Configure("Character Card Image", cardName, 1, _ => texture);
            characterCardImageViewer.ConfigureActions(string.Empty, null, string.Empty, null);
            characterCardImageViewer.Open();
        }

        private void BuildInitialModules()
        {
            var resources = AddModule("资源状态");
            AddTextRow(resources, "源岩", "0");
            AddTextRow(resources, "源石", "0");
            AddTextRow(resources, "异铁", "0");
            AddTextRow(resources, "至纯源石", "0");
            AddTextRow(resources, "金券", "10");
            AddTextRow(AddModule("玩家宣告"), "状态", "暂无玩家数据。");
            AddTextRow(AddModule("角色牌"), "状态", "暂无角色牌数据。");
        }

        private void RebuildLayout()
        {
            if (scrollContent == null) return;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollContent);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelTransform);
        }

        private void RefreshVisibleTextRenderers()
        {
            if (contentArea == null || !contentArea.gameObject.activeInHierarchy) return;
            RebuildLayout();
            var texts = contentArea.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null || !text.gameObject.activeInHierarchy) continue;
                if (text.font != null) text.material = text.font.material;
                text.canvasRenderer.cull = false;
                text.canvasRenderer.SetAlpha(text.color.a);
                text.FontTextureChanged();
                text.SetAllDirty();
            }

            Canvas.ForceUpdateCanvases();
            pendingTextRefresh = false;
        }

        private static ExpandableInfoItemView InstantiateTemplate(
            ExpandableInfoItemView template,
            Transform parent,
            string objectName)
        {
            var instance = Object.Instantiate(template, parent, false);
            instance.gameObject.name = objectName;
            instance.gameObject.SetActive(true);
            return instance;
        }

        private static ExpandableInfoItemView InstantiateContentItem(
            InfoModule module,
            ExpandableInfoItemView template,
            string objectName,
            float width,
            float height,
            Vector2 offset)
        {
            var item = InstantiateTemplate(template, module.ContentRect, objectName);
            var y = module.AppendContentItem(height);
            item.Root.anchorMin = module.LayoutProfile.TopLeftAnchor;
            item.Root.anchorMax = module.LayoutProfile.TopLeftAnchor;
            item.Root.pivot = module.LayoutProfile.TopLeftAnchor;
            item.Root.sizeDelta = new Vector2(width, height);
            item.Root.anchoredPosition = new Vector2(offset.x, y + offset.y);
            item.LayoutElement.minWidth = width;
            item.LayoutElement.preferredWidth = width;
            item.LayoutElement.minHeight = height;
            item.LayoutElement.preferredHeight = height;
            item.LayoutElement.flexibleWidth = 1f;
            return item;
        }

        private Texture2D GetCharacterCardTexture(string cardId)
        {
            return cardVisualCatalog == null ? null : cardVisualCatalog.GetCharacterFront(cardId);
        }

        private static string FormatRowText(string label, string value) =>
            label + "：" + (value ?? string.Empty);

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

        private static void DestroyDynamicObject(GameObject target)
        {
            if (target == null) return;
            if (UnityEngine.Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }

        public sealed class InfoModule
        {
            private readonly string title;
            private readonly RectTransform contentTransform;
            private readonly LayoutElement contentElement;
            private readonly Text titleText;
            private readonly ExpandableInfoPanelLayoutProfile layoutProfile;
            private bool contentVisible = true;
            private float nextContentY;

            public string Title => title;
            public RectTransform ContentRect => contentTransform;
            internal ExpandableInfoPanelLayoutProfile LayoutProfile => layoutProfile;

            internal InfoModule(
                string title,
                ExpandableInfoItemView moduleView,
                ExpandableInfoPanelLayoutProfile configuredLayoutProfile)
            {
                this.title = title;
                layoutProfile = configuredLayoutProfile ??
                    throw new System.ArgumentNullException(nameof(configuredLayoutProfile));
                contentTransform = moduleView.ContentRoot;
                contentElement = moduleView.LayoutElement;
                titleText = moduleView.PrimaryText;
                titleText.text = "▼ " + title;
                moduleView.Button.onClick.RemoveAllListeners();
                moduleView.Button.onClick.AddListener(ToggleContent);
            }

            public void SetContentVisible(bool visible)
            {
                contentVisible = visible;
                contentTransform.gameObject.SetActive(visible);
                titleText.text = (visible ? "▼ " : "▶ ") + title;
            }

            public void ToggleContent() => SetContentVisible(!contentVisible);

            internal float AppendContentItem(float height)
            {
                var y = -(layoutProfile.ModuleContentTopPadding + nextContentY);
                nextContentY += height + layoutProfile.ModuleContentSpacing;
                var contentHeight = layoutProfile.ModuleContentTopPadding + nextContentY;
                contentTransform.sizeDelta = new Vector2(
                    layoutProfile.ModuleContentWidth,
                    contentHeight);
                contentElement.minHeight = contentHeight;
                contentElement.preferredHeight = contentHeight;
                return y;
            }

            public void SetRowValue(string label, string value)
            {
                for (var i = 0; i < contentTransform.childCount; i++)
                {
                    var item = contentTransform.GetChild(i).GetComponent<ExpandableInfoItemView>();
                    var text = item == null ? null : item.PrimaryText;
                    if (text == null || text.gameObject.name != "Value: " + label) continue;
                    text.text = FormatRowText(label, value);
                    text.FontTextureChanged();
                    text.SetAllDirty();
                    return;
                }
            }

            public void ClearContent()
            {
                for (var i = contentTransform.childCount - 1; i >= 0; i--)
                {
                    DestroyDynamicObject(contentTransform.GetChild(i).gameObject);
                }

                nextContentY = 0f;
                contentTransform.sizeDelta = layoutProfile.EmptyModuleContentSize;
                contentElement.minHeight = layoutProfile.ModuleContentTopPadding;
                contentElement.preferredHeight = layoutProfile.ModuleContentTopPadding;
            }
        }
    }
}
