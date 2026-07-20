using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class CityStyleDeclarationSlotPointerHandler : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerEnterHandler,
        IPointerClickHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private int slotIndex;
        private Func<bool> isLeftPointerHeld;
        private Action<int, Vector2> leftPointerDown;
        private Action<int, Vector2> leftPointerEnter;
        private Action<Vector2> leftPointerDrag;
        private Action leftPointerUp;
        private Action<int> leftClick;
        private bool draggedDuringCurrentPress;

        public void Configure(
            int configuredSlotIndex,
            Func<bool> configuredIsLeftPointerHeld,
            Action<int, Vector2> configuredLeftPointerDown,
            Action<int, Vector2> configuredLeftPointerEnter,
            Action<Vector2> configuredLeftPointerDrag,
            Action configuredLeftPointerUp,
            Action<int> configuredLeftClick)
        {
            slotIndex = configuredSlotIndex;
            isLeftPointerHeld = configuredIsLeftPointerHeld;
            leftPointerDown = configuredLeftPointerDown;
            leftPointerEnter = configuredLeftPointerEnter;
            leftPointerDrag = configuredLeftPointerDrag;
            leftPointerUp = configuredLeftPointerUp;
            leftClick = configuredLeftClick;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                draggedDuringCurrentPress = false;
                leftPointerDown?.Invoke(slotIndex, eventData.position);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerUp?.Invoke();
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (eventData != null && isLeftPointerHeld != null && isLeftPointerHeld())
            {
                leftPointerEnter?.Invoke(slotIndex, eventData.position);
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return;
            }

            if (eventData.button == PointerEventData.InputButton.Left)
            {
                if (!draggedDuringCurrentPress && !eventData.dragging)
                {
                    leftClick?.Invoke(slotIndex);
                }
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                draggedDuringCurrentPress = true;
                leftPointerDrag?.Invoke(eventData.position);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                draggedDuringCurrentPress = true;
                leftPointerDrag?.Invoke(eventData.position);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerUp?.Invoke();
            }
        }
    }

    internal sealed class CityStyleDeclarationBoardPointerHandler : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private Action<Vector2> leftPointerDown;
        private Action<Vector2> leftPointerDrag;
        private Action leftPointerUp;

        public void Configure(
            Action<Vector2> configuredLeftPointerDown,
            Action<Vector2> configuredLeftPointerDrag,
            Action configuredLeftPointerUp)
        {
            leftPointerDown = configuredLeftPointerDown;
            leftPointerDrag = configuredLeftPointerDrag;
            leftPointerUp = configuredLeftPointerUp;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerDown?.Invoke(eventData.position);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerUp?.Invoke();
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerDrag?.Invoke(eventData.position);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerDrag?.Invoke(eventData.position);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (eventData != null && eventData.button == PointerEventData.InputButton.Left)
            {
                leftPointerUp?.Invoke();
            }
        }
    }

    internal sealed class CityStyleDeclarationPreviewInputHandler : MonoBehaviour
    {
        private static int escapeConsumedFrame = -1;

        private Action clearSelectionRequested;
        private Action closeRequested;
        private Action<int> pageChangeRequested;

        public static bool WasEscapeConsumedThisFrame()
        {
            return escapeConsumedFrame == Time.frameCount;
        }

        public static bool HasOpenDialog()
        {
            var handlers = FindObjectsOfType<CityStyleDeclarationPreviewInputHandler>();
            for (var i = 0; i < handlers.Length; i++)
            {
                if (handlers[i] != null && handlers[i].isActiveAndEnabled)
                {
                    return true;
                }
            }

            return false;
        }

        public void Configure(
            Action configuredClearSelectionRequested,
            Action configuredCloseRequested,
            Action<int> configuredPageChangeRequested)
        {
            clearSelectionRequested = configuredClearSelectionRequested;
            closeRequested = configuredCloseRequested;
            pageChangeRequested = configuredPageChangeRequested;
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(1))
            {
                HandleRightClick(RaycastPointerTarget());
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                escapeConsumedFrame = Time.frameCount;
                closeRequested?.Invoke();
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                pageChangeRequested?.Invoke(-1);
            }
            else if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                pageChangeRequested?.Invoke(1);
            }
        }

        private void HandleRightClick(GameObject pointerTarget)
        {
            if (IsDeclarationSlot(pointerTarget))
            {
                clearSelectionRequested?.Invoke();
                return;
            }

            closeRequested?.Invoke();
        }

        private static GameObject RaycastPointerTarget()
        {
            if (EventSystem.current == null)
            {
                return null;
            }

            var pointer = new PointerEventData(EventSystem.current)
            {
                position = Input.mousePosition
            };
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, results);
            return results.Count > 0 ? results[0].gameObject : null;
        }

        private static bool IsDeclarationSlot(GameObject pointerTarget)
        {
            return pointerTarget != null &&
                   pointerTarget.GetComponentInParent<CityStyleDeclarationSlotPointerHandler>() != null;
        }
    }

    internal sealed class CityStyleDeclarationPreviewDialog
    {
        private const int PreviewCanvasSortingOrder = 130;

        private static readonly Color OverlayColor = new Color(0f, 0f, 0f, 0.82f);
        private static readonly Color SelectableSlotBackground = new Color(0.9f, 0.68f, 0.16f, 0.18f);
        private static readonly Color SelectedSlotBackground = new Color(0.18f, 0.78f, 0.28f, 0.42f);
        private static readonly Color SelectedSlotOutline = new Color(0.48f, 1f, 0.42f, 1f);
        private static readonly Color EmptySlotColor = new Color(1f, 1f, 1f, 0f);
        private static readonly Color ConfirmEnabledColor = new Color(0.2f, 0.62f, 0.18f, 0.98f);
        private static Sprite cityStyleInfluenceMarkerSprite;

        private readonly List<CityBoardSlotBinding> slotBindings = new List<CityBoardSlotBinding>();
        private readonly List<int> selectedSlotIndexes = new List<int>();
        private readonly List<Image> cityStyleInfluenceMarkers = new List<Image>();

        private CityStyleOptionsViewModel model;
        private GameObject previewCanvasObject;
        private GameObject overlayObject;
        private RawImage cityStyleCardImage;
        private RectTransform cityStyleInfluenceMarkerRoot;
        private Text cityStyleCardPlaceholder;
        private Text cityStyleTitleText;
        private Text matchStatusText;
        private Text boardTitleText;
        private RectTransform cityBoardRect;
        private Button previousButton;
        private Button nextButton;
        private Button confirmDeclarationButton;
        private Outline boardOutline;
        private int currentCityStyleIndex;
        private bool selectingFacilities;
        private bool leftPointerHeld;
        private bool hasLastPointerPosition;
        private Vector2 lastPointerPosition;
        private bool selectionCanConfirm;
        private string selectionReason = string.Empty;
        private int selectionRequiredFacilityCount;
        private int selectionSelectedFacilityCount;

        public bool IsShowing
        {
            get { return overlayObject != null && overlayObject.activeSelf; }
        }

        public bool IsSelecting
        {
            get { return selectingFacilities; }
        }

        public string CurrentCityStyleId
        {
            get
            {
                var option = GetCurrentOption();
                return option == null ? string.Empty : option.CityStyleId;
            }
        }

        public IReadOnlyList<int> SelectedSlotIndexes
        {
            get { return selectedSlotIndexes.AsReadOnly(); }
        }

        public bool CanConfirm
        {
            get { return selectionCanConfirm; }
        }

        public string MatchStatus
        {
            get { return matchStatusText == null ? string.Empty : matchStatusText.text; }
        }

        public void Show(RectTransform canvasTransform, CityStyleOptionsViewModel viewModel)
        {
            HideInternal(false);
            if (canvasTransform == null || viewModel == null)
            {
                return;
            }

            model = viewModel;
            currentCityStyleIndex = ResolveInitialCityStyleIndex(viewModel);
            var initialOption = GetCurrentOption();
            selectingFacilities = initialOption != null && initialOption.CanDeclare;
            UguiUtility.EnsureEventSystem();
            BuildUi();
            BuildCityBoard();
            RenderCurrentCityStyle();
            if (initialOption != null)
            {
                ValidateCurrentSelection();
            }
        }

        public void Hide()
        {
            HideInternal(true);
        }

        private void BuildUi()
        {
            var canvas = UguiUtility.CreateCanvas(
                "City Style Declaration Preview Canvas",
                PreviewCanvasSortingOrder);
            previewCanvasObject = canvas.gameObject;
            var canvasRect = canvas.GetComponent<RectTransform>();

            overlayObject = new GameObject(
                "City Style Declaration Preview Overlay",
                typeof(RectTransform),
                typeof(Image));
            overlayObject.transform.SetParent(canvasRect, false);
            var overlayRect = overlayObject.GetComponent<RectTransform>();
            Stretch(overlayRect);
            overlayObject.GetComponent<Image>().color = OverlayColor;
            overlayObject.AddComponent<CityStyleDeclarationPreviewInputHandler>()
                .Configure(ClearCurrentSelection, HandleBackNavigation, ChangeCityStyle);

            var panel = CreatePanel(
                overlayRect,
                "City Style Declaration Preview Panel",
                new Vector2(1520f, 900f),
                Vector2.zero,
                UiTheme.PanelBackground);
            panel.GetComponent<Outline>().effectDistance = new Vector2(4f, -4f);

            CreateText(
                panel,
                "City Style Declaration Preview Title",
                "样式卡预览",
                30,
                FontStyle.Bold,
                UiTheme.GoldText,
                TextAnchor.MiddleCenter,
                new Vector2(680f, 52f),
                new Vector2(0f, 410f));

            UguiUtility.CreateViewerCloseButton(
                panel,
                "Close City Style Declaration Preview Button",
                Hide);

            var leftPanel = CreatePanel(
                panel,
                "City Style Preview Section",
                new Vector2(920f, 800f),
                new Vector2(-280f, -24f),
                new Color(0.04f, 0.035f, 0.028f, 0.72f));
            var rightPanel = CreatePanel(
                panel,
                "City Style Preview Board Section",
                new Vector2(500f, 800f),
                new Vector2(490f, -24f),
                new Color(0.04f, 0.035f, 0.028f, 0.72f));

            cityStyleTitleText = CreateText(
                leftPanel,
                "City Style Preview Name",
                string.Empty,
                24,
                FontStyle.Bold,
                UiTheme.GoldText,
                TextAnchor.MiddleCenter,
                new Vector2(700f, 40f),
                new Vector2(0f, 360f));
            var cardFrame = CreatePanel(
                leftPanel,
                "City Style Preview Card Frame",
                new Vector2(850f, 548f),
                new Vector2(0f, 40f),
                UiTheme.ScrollBackground);
            var cardObject = new GameObject(
                "City Style Preview Card",
                typeof(RectTransform),
                typeof(RawImage));
            cardObject.transform.SetParent(cardFrame, false);
            var cardRect = cardObject.GetComponent<RectTransform>();
            StretchWithInset(cardRect, 6f);
            cityStyleCardImage = cardObject.GetComponent<RawImage>();
            cityStyleCardImage.color = Color.white;
            cityStyleCardImage.raycastTarget = false;

            var markerRootObject = new GameObject(
                "City Style Preview Influence Markers",
                typeof(RectTransform));
            markerRootObject.transform.SetParent(cardRect, false);
            cityStyleInfluenceMarkerRoot = markerRootObject.GetComponent<RectTransform>();
            Stretch(cityStyleInfluenceMarkerRoot);

            cityStyleCardPlaceholder = CreateText(
                cardFrame,
                "City Style Preview Missing Image",
                "样式卡图片暂不可用",
                20,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter,
                new Vector2(760f, 100f),
                Vector2.zero);
            cityStyleCardPlaceholder.gameObject.SetActive(false);

            previousButton = CreateButton(
                leftPanel,
                "Previous City Style",
                "<",
                new Vector2(58f, 118f),
                new Vector2(-425f, 40f),
                34);
            previousButton.onClick.AddListener(() => ChangeCityStyle(-1));
            nextButton = CreateButton(
                leftPanel,
                "Next City Style",
                ">",
                new Vector2(58f, 118f),
                new Vector2(425f, 40f),
                34);
            nextButton.onClick.AddListener(() => ChangeCityStyle(1));

            confirmDeclarationButton = CreateButton(
                leftPanel,
                "Confirm City Style Declaration",
                "确认宣告",
                new Vector2(180f, 48f),
                new Vector2(0f, -310f));
            confirmDeclarationButton.onClick.AddListener(ConfirmDeclaration);

            matchStatusText = CreateText(
                leftPanel,
                "City Style Match Status",
                string.Empty,
                15,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter,
                new Vector2(840f, 54f),
                new Vector2(0f, -375f));
            matchStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            matchStatusText.verticalOverflow = VerticalWrapMode.Truncate;
            matchStatusText.raycastTarget = false;

            boardTitleText = CreateText(
                rightPanel,
                "City Style Preview Board Title",
                "建设面板",
                23,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter,
                new Vector2(430f, 42f),
                new Vector2(0f, 370f));

            var boardObject = new GameObject(
                "City Style Preview Board",
                typeof(RectTransform),
                typeof(RawImage),
                typeof(Outline));
            boardObject.transform.SetParent(rightPanel, false);
            var boardRect = boardObject.GetComponent<RectTransform>();
            boardRect.anchorMin = new Vector2(0.5f, 0.5f);
            boardRect.anchorMax = new Vector2(0.5f, 0.5f);
            boardRect.pivot = new Vector2(0.5f, 0.5f);
            boardRect.sizeDelta = new Vector2(397f, 733f);
            boardRect.anchoredPosition = new Vector2(0f, -20f);
            cityBoardRect = boardRect;
            var boardImage = boardObject.GetComponent<RawImage>();
            boardImage.texture = CardTextureCatalog.LoadCityBoard();
            boardImage.color = boardImage.texture == null ? UiTheme.ScrollBackground : Color.white;
            boardImage.raycastTarget = true;
            boardOutline = boardObject.GetComponent<Outline>();
            boardOutline.effectColor = UiTheme.GoldOutlineThin;
            boardOutline.effectDistance = new Vector2(2f, -2f);
            boardObject.AddComponent<CityStyleDeclarationBoardPointerHandler>()
                .Configure(
                    OnBoardLeftPointerDown,
                    OnLeftPointerDrag,
                    EndLeftPointerGesture);
        }

        private void BuildCityBoard()
        {
            slotBindings.Clear();
            if (cityBoardRect == null)
            {
                return;
            }

            for (var slotIndex = 0; slotIndex < CityBoardSlotLayout.SlotCount; slotIndex++)
            {
                var slotModel = FindCityBoardSlot(slotIndex);
                var facilityId = slotModel == null ? string.Empty : slotModel.FacilityId;
                var occupied = !string.IsNullOrEmpty(facilityId);
                var used = occupied && slotModel.Used;
                var slotObject = new GameObject(
                    "宣告槽位 " + (slotIndex + 1),
                    typeof(RectTransform),
                    typeof(Image),
                    typeof(Button),
                    typeof(Outline));
                slotObject.transform.SetParent(cityBoardRect, false);
                var slotRect = slotObject.GetComponent<RectTransform>();
                SetCityBoardSlotRect(slotRect, slotIndex);
                slotRect.localEulerAngles = used ? new Vector3(0f, 0f, 180f) : Vector3.zero;

                var slotImage = slotObject.GetComponent<Image>();
                slotImage.color = occupied ? SelectableSlotBackground : EmptySlotColor;
                slotImage.raycastTarget = true;
                var slotOutline = slotObject.GetComponent<Outline>();
                slotOutline.effectColor = occupied ? UiTheme.GoldOutlineThin : EmptySlotColor;
                slotOutline.effectDistance = new Vector2(1f, -1f);
                var slotButton = slotObject.GetComponent<Button>();
                slotButton.transition = Selectable.Transition.None;
                slotButton.interactable = false;

                if (occupied)
                {
                    AddFacilityCardImage(slotRect, facilityId);
                }

                if (used)
                {
                    AddUsedBadge(slotRect);
                }

                var binding = new CityBoardSlotBinding
                {
                    SlotIndex = slotIndex,
                    Rect = slotRect,
                    Image = slotImage,
                    Outline = slotOutline,
                    Button = slotButton,
                    Occupied = occupied,
                    Used = used
                };
                slotBindings.Add(binding);
                slotObject.AddComponent<CityStyleDeclarationSlotPointerHandler>().Configure(
                    slotIndex,
                    () => leftPointerHeld,
                    OnSlotLeftPointerDown,
                    OnSlotLeftPointerEnter,
                    OnLeftPointerDrag,
                    EndLeftPointerGesture,
                    OnSlotLeftClick);
            }
        }

        private void RenderCurrentCityStyle()
        {
            var option = GetCurrentOption();
            var optionCount = model == null || model.Options == null ? 0 : model.Options.Count;
            if (option == null)
            {
                cityStyleTitleText.text = "当前没有样式卡";
                cityStyleCardImage.texture = null;
                cityStyleCardImage.gameObject.SetActive(false);
                cityStyleCardPlaceholder.gameObject.SetActive(true);
            }
            else
            {
                cityStyleTitleText.text = option.Name;
                var texture = CardTextureCatalog.LoadCityStyle(option.CityStyleId, option.Name);
                cityStyleCardImage.texture = texture;
                cityStyleCardImage.gameObject.SetActive(texture != null);
                cityStyleCardPlaceholder.gameObject.SetActive(texture == null);
            }

            RenderCityStyleInfluenceMarkers(option == null ? string.Empty : option.CityStyleId);

            SetButtonState(previousButton, currentCityStyleIndex > 0, false);
            SetButtonState(nextButton, currentCityStyleIndex < optionCount - 1, false);
            RenderSelectionState();
        }

        private void RenderCityStyleInfluenceMarkers(string cityStyleId)
        {
            var displayIndex = 0;
            var markerLayout = new CityStyleMarkerLayoutTracker();
            var markers = model == null ? null : model.CityStyleMarkers;
            if (!string.IsNullOrEmpty(cityStyleId) && markers != null)
            {
                for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    var marker = markers[markerIndex];
                    if (marker == null || marker.CityStyleId != cityStyleId)
                    {
                        continue;
                    }

                    AddCityStyleInfluenceMarker(marker, markerLayout, displayIndex++);
                }
            }

            for (var markerIndex = displayIndex;
                 markerIndex < cityStyleInfluenceMarkers.Count;
                 markerIndex++)
            {
                cityStyleInfluenceMarkers[markerIndex].gameObject.SetActive(false);
            }
        }

        private void AddCityStyleInfluenceMarker(
            CityStyleMarkerViewModel markerModel,
            CityStyleMarkerLayoutTracker markerLayout,
            int displayIndex)
        {
            var placement = markerLayout.Next(
                markerModel.CityStyleId,
                markerModel.MarkerArea,
                markerModel.PlayerId);
            var marker = EnsureCityStyleInfluenceMarker(displayIndex);
            if (marker == null)
            {
                return;
            }

            CityStyleMarkerRenderer.Configure(
                marker,
                "样式预览影响力 玩家" + markerModel.PlayerId + " 标记" + (displayIndex + 1),
                UiTheme.GetPlayerColor(markerModel.PlayerColor, 1f),
                cityStyleInfluenceMarkerSprite,
                new Vector2(20f, 20f),
                markerModel.CityStyleId,
                placement);
        }

        private Image EnsureCityStyleInfluenceMarker(int markerIndex)
        {
            if (cityStyleInfluenceMarkerRoot == null)
            {
                return null;
            }

            if (cityStyleInfluenceMarkerSprite == null)
            {
                cityStyleInfluenceMarkerSprite = UguiUtility.CreateFilledSquareSprite(32, 24f);
            }

            while (cityStyleInfluenceMarkers.Count <= markerIndex)
            {
                var markerObject = new GameObject(
                    "样式预览影响力",
                    typeof(RectTransform),
                    typeof(Image),
                    typeof(Outline));
                markerObject.transform.SetParent(cityStyleInfluenceMarkerRoot, false);
                var markerRect = markerObject.GetComponent<RectTransform>();
                markerRect.pivot = new Vector2(0.5f, 0.5f);
                markerRect.sizeDelta = new Vector2(20f, 20f);

                var image = markerObject.GetComponent<Image>();
                image.sprite = cityStyleInfluenceMarkerSprite;
                image.raycastTarget = false;
                var outline = markerObject.GetComponent<Outline>();
                outline.effectColor = Color.white;
                outline.effectDistance = new Vector2(1f, -1f);
                cityStyleInfluenceMarkers.Add(image);
            }

            return cityStyleInfluenceMarkers[markerIndex];
        }

        private void RenderSelectionState()
        {
            var option = GetCurrentOption();
            var hasStyleCard = option != null;
            SetButtonState(confirmDeclarationButton, selectingFacilities && selectionCanConfirm, true);

            if (boardOutline != null)
            {
                boardOutline.effectColor = hasStyleCard
                    ? new Color(1f, 0.82f, 0.24f, 1f)
                    : UiTheme.GoldOutlineThin;
                boardOutline.effectDistance = hasStyleCard
                    ? new Vector2(5f, -5f)
                    : new Vector2(2f, -2f);
            }

            if (boardTitleText != null)
            {
                boardTitleText.color = hasStyleCard ? UiTheme.GoldText : UiTheme.ValueText;
                boardTitleText.text = hasStyleCard ? "建设面板（单击/拖动选择）" : "建设面板";
            }

            for (var i = 0; i < slotBindings.Count; i++)
            {
                var binding = slotBindings[i];
                var selected = selectedSlotIndexes.Contains(binding.SlotIndex);
                binding.Button.interactable = selectingFacilities && binding.Occupied && !binding.Used;
                if (!binding.Occupied)
                {
                    binding.Image.color = EmptySlotColor;
                    binding.Outline.effectColor = EmptySlotColor;
                    binding.Outline.effectDistance = Vector2.zero;
                }
                else if (binding.Used)
                {
                    binding.Image.color = new Color(0.12f, 0.08f, 0.04f, 0.18f);
                    binding.Outline.effectColor = EmptySlotColor;
                    binding.Outline.effectDistance = Vector2.zero;
                }
                else if (selected)
                {
                    binding.Image.color = SelectedSlotBackground;
                    binding.Outline.effectColor = SelectedSlotOutline;
                    binding.Outline.effectDistance = new Vector2(4f, -4f);
                }
                else if (hasStyleCard)
                {
                    binding.Image.color = SelectableSlotBackground;
                    binding.Outline.effectColor = new Color(1f, 0.82f, 0.24f, 0.92f);
                    binding.Outline.effectDistance = new Vector2(2f, -2f);
                }
                else
                {
                    binding.Image.color = new Color(0.12f, 0.08f, 0.04f, 0.18f);
                    binding.Outline.effectColor = UiTheme.GoldOutlineThin;
                    binding.Outline.effectDistance = new Vector2(1f, -1f);
                }
            }

            UpdateMatchStatus(option);
        }

        private void UpdateMatchStatus(CityStyleOptionViewModel option)
        {
            if (matchStatusText == null)
            {
                return;
            }

            if (option == null)
            {
                matchStatusText.text = "样式卡区暂无可预览内容。";
                matchStatusText.color = UiTheme.LabelText;
                return;
            }

            if (!option.CanDeclare)
            {
                matchStatusText.text = "当前不可宣告：" +
                                       (string.IsNullOrEmpty(option.Reason) ? "条件尚未满足。" : option.Reason);
                matchStatusText.color = new Color(1f, 0.48f, 0.32f, 1f);
                return;
            }

            if (!selectingFacilities)
            {
                matchStatusText.text = "当前仅可预览该样式卡。";
                matchStatusText.color = UiTheme.LabelText;
                return;
            }

            var countText = selectionRequiredFacilityCount > 0
                ? "已选 " + selectionSelectedFacilityCount + " / " + selectionRequiredFacilityCount + " 个设施色块。"
                : "已选 " + selectionSelectedFacilityCount + " 个设施色块。";
            if (selectionCanConfirm)
            {
                matchStatusText.text = "满足宣告条件。" + countText;
                matchStatusText.color = new Color(0.48f, 1f, 0.42f, 1f);
            }
            else
            {
                matchStatusText.text = (string.IsNullOrEmpty(selectionReason) ? "尚未满足宣告条件。" : selectionReason) + countText;
                matchStatusText.color = new Color(1f, 0.68f, 0.28f, 1f);
            }
        }

        private void ChangeCityStyle(int offset)
        {
            var count = model == null || model.Options == null ? 0 : model.Options.Count;
            if (count <= 0)
            {
                return;
            }

            var nextIndex = Mathf.Clamp(currentCityStyleIndex + offset, 0, count - 1);
            if (nextIndex == currentCityStyleIndex)
            {
                return;
            }

            currentCityStyleIndex = nextIndex;
            var option = GetCurrentOption();
            selectingFacilities = option != null && option.CanDeclare;
            ResetValidationState();
            EndLeftPointerGesture();
            RenderCurrentCityStyle();
            if (option != null)
            {
                ValidateCurrentSelection();
            }
        }

        private void HandleBackNavigation()
        {
            Hide();
        }

        private void ClearCurrentSelection()
        {
            selectedSlotIndexes.Clear();
            EndLeftPointerGesture();
            ValidateCurrentSelection();
        }

        private void OnBoardLeftPointerDown(Vector2 pointerPosition)
        {
            if (!selectingFacilities)
            {
                return;
            }

            leftPointerHeld = true;
            hasLastPointerPosition = true;
            lastPointerPosition = pointerPosition;
        }

        private void OnSlotLeftPointerDown(int slotIndex, Vector2 pointerPosition)
        {
            if (!selectingFacilities)
            {
                return;
            }

            leftPointerHeld = true;
            hasLastPointerPosition = true;
            lastPointerPosition = pointerPosition;
        }

        private void OnSlotLeftPointerEnter(int slotIndex, Vector2 pointerPosition)
        {
            if (!selectingFacilities || !leftPointerHeld)
            {
                return;
            }

            var changed = false;
            if (hasLastPointerPosition)
            {
                changed = AddSlotsAlongPointerSegment(lastPointerPosition, pointerPosition);
            }

            changed |= AddSelectedSlot(slotIndex, false);
            lastPointerPosition = pointerPosition;
            hasLastPointerPosition = true;
            if (changed)
            {
                ValidateCurrentSelection();
            }
        }

        private void OnLeftPointerDrag(Vector2 pointerPosition)
        {
            if (!selectingFacilities || !leftPointerHeld)
            {
                return;
            }

            var changed = hasLastPointerPosition &&
                          AddSlotsAlongPointerSegment(lastPointerPosition, pointerPosition);
            lastPointerPosition = pointerPosition;
            hasLastPointerPosition = true;
            if (changed)
            {
                ValidateCurrentSelection();
            }
        }

        private void EndLeftPointerGesture()
        {
            leftPointerHeld = false;
            hasLastPointerPosition = false;
        }

        private void OnSlotLeftClick(int slotIndex)
        {
            if (selectingFacilities)
            {
                ToggleSelectedSlot(slotIndex);
            }
        }

        private void ToggleSelectedSlot(int slotIndex)
        {
            var binding = FindSlotBinding(slotIndex);
            if (!selectingFacilities || binding == null || !binding.Occupied || binding.Used)
            {
                return;
            }

            if (!selectedSlotIndexes.Remove(slotIndex))
            {
                selectedSlotIndexes.Add(slotIndex);
                selectedSlotIndexes.Sort();
            }

            ValidateCurrentSelection();
        }

        private bool AddSelectedSlot(int slotIndex, bool validate)
        {
            var binding = FindSlotBinding(slotIndex);
            if (!selectingFacilities || binding == null || !binding.Occupied || binding.Used ||
                selectedSlotIndexes.Contains(slotIndex))
            {
                return false;
            }

            selectedSlotIndexes.Add(slotIndex);
            selectedSlotIndexes.Sort();
            if (validate)
            {
                ValidateCurrentSelection();
            }

            return true;
        }

        private bool AddSlotsAlongPointerSegment(Vector2 start, Vector2 end)
        {
            var changed = false;
            for (var i = 0; i < slotBindings.Count; i++)
            {
                var binding = slotBindings[i];
                if (!binding.Occupied || binding.Used || selectedSlotIndexes.Contains(binding.SlotIndex) ||
                    !SegmentIntersectsRect(start, end, binding.Rect))
                {
                    continue;
                }

                changed |= AddSelectedSlot(binding.SlotIndex, false);
            }

            return changed;
        }

        private void ValidateCurrentSelection()
        {
            ResetValidationState();
            var option = GetCurrentOption();
            if (!selectingFacilities || option == null || model == null || model.ValidateSelection == null)
            {
                selectionReason = option != null && !option.CanDeclare
                    ? option.Reason ?? string.Empty
                    : "暂时无法校验宣告条件。";
                RenderSelectionState();
                return;
            }

            var selection = new List<int>(selectedSlotIndexes).AsReadOnly();
            var result = model.ValidateSelection(option.CityStyleId, selection);
            if (ReferenceEquals(result, null))
            {
                selectionReason = "暂时无法校验宣告条件。";
                RenderSelectionState();
                return;
            }

            selectionCanConfirm = result.CanConfirm && model.ConfirmSelection != null;
            selectionReason = result.Reason ?? string.Empty;
            selectionRequiredFacilityCount = result.RequiredFacilityCount;
            selectionSelectedFacilityCount = result.SelectedFacilityCount;
            RenderSelectionState();
        }

        private void ConfirmDeclaration()
        {
            var option = GetCurrentOption();
            if (!selectingFacilities || !selectionCanConfirm || option == null ||
                model == null || model.ConfirmSelection == null)
            {
                return;
            }

            var styleId = option.CityStyleId;
            var selection = new List<int>(selectedSlotIndexes).AsReadOnly();
            if (model.ConfirmSelection(styleId, selection))
            {
                HideInternal(false);
                return;
            }

            ValidateCurrentSelection();
        }

        private void ResetValidationState()
        {
            selectionCanConfirm = false;
            selectionReason = string.Empty;
            selectionRequiredFacilityCount = 0;
            selectionSelectedFacilityCount = selectedSlotIndexes.Count;
        }

        private CityStyleOptionViewModel GetCurrentOption()
        {
            if (model == null || model.Options == null || model.Options.Count <= 0 ||
                currentCityStyleIndex < 0 || currentCityStyleIndex >= model.Options.Count)
            {
                return null;
            }

            return model.Options[currentCityStyleIndex];
        }

        private static int ResolveInitialCityStyleIndex(CityStyleOptionsViewModel viewModel)
        {
            if (viewModel.Options == null || viewModel.Options.Count <= 0 ||
                string.IsNullOrEmpty(viewModel.InitialCityStyleId))
            {
                return 0;
            }

            for (var i = 0; i < viewModel.Options.Count; i++)
            {
                var option = viewModel.Options[i];
                if (option != null && option.CityStyleId == viewModel.InitialCityStyleId)
                {
                    return i;
                }
            }

            return 0;
        }

        private CityBoardSlotBinding FindSlotBinding(int slotIndex)
        {
            for (var i = 0; i < slotBindings.Count; i++)
            {
                if (slotBindings[i].SlotIndex == slotIndex)
                {
                    return slotBindings[i];
                }
            }

            return null;
        }

        private void HideInternal(bool invokeCancel)
        {
            var cancel = invokeCancel && model != null ? model.Cancel : null;
            model = null;
            selectingFacilities = false;
            selectedSlotIndexes.Clear();
            slotBindings.Clear();
            cityStyleInfluenceMarkers.Clear();
            cityStyleInfluenceMarkerRoot = null;
            ResetValidationState();
            EndLeftPointerGesture();

            if (previewCanvasObject != null)
            {
                var canvasObject = previewCanvasObject;
                previewCanvasObject = null;
                overlayObject = null;
                cityBoardRect = null;
                if (UnityEngine.Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(canvasObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(canvasObject);
                }
            }

            cancel?.Invoke();
        }

        private CityBoardSlotViewModel FindCityBoardSlot(int slotIndex)
        {
            var slots = model == null ? null : model.CityBoardSlots;
            if (slots == null)
            {
                return null;
            }

            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i] != null && slots[i].SlotIndex == slotIndex)
                {
                    return slots[i];
                }
            }

            return null;
        }

        private static void SetCityBoardSlotRect(RectTransform rect, int slotIndex)
        {
            CityBoardSlotLayout.Apply(rect, slotIndex);
        }

        private static void AddFacilityCardImage(RectTransform parent, string facilityId)
        {
            var texture = CardTextureCatalog.LoadFacility(facilityId);
            if (texture == null)
            {
                return;
            }

            var imageObject = new GameObject("设施卡图", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(parent, false);
            Stretch(imageObject.GetComponent<RectTransform>());
            var image = imageObject.GetComponent<RawImage>();
            image.texture = texture;
            image.color = Color.white;
            image.raycastTarget = false;
        }

        private static void AddUsedBadge(RectTransform parent)
        {
            var badgeObject = new GameObject(
                "历史已使用",
                typeof(RectTransform),
                typeof(Image),
                typeof(Outline));
            badgeObject.transform.SetParent(parent, false);
            var badgeRect = badgeObject.GetComponent<RectTransform>();
            badgeRect.anchorMin = new Vector2(0.5f, 0.5f);
            badgeRect.anchorMax = new Vector2(0.5f, 0.5f);
            badgeRect.pivot = new Vector2(0.5f, 0.5f);
            badgeRect.sizeDelta = new Vector2(92f, 28f);
            badgeRect.anchoredPosition = Vector2.zero;
            badgeRect.localEulerAngles = new Vector3(0f, 0f, 180f);
            badgeObject.GetComponent<Image>().color = new Color(0.58f, 0.06f, 0.03f, 0.94f);
            badgeObject.GetComponent<Image>().raycastTarget = false;
            var outline = badgeObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.DarkShadowLight;
            outline.effectDistance = new Vector2(1f, -1f);
            var text = CreateText(
                badgeRect,
                "历史已使用 Text",
                "已使用",
                13,
                FontStyle.Bold,
                Color.white,
                TextAnchor.MiddleCenter,
                badgeRect.sizeDelta,
                Vector2.zero);
            text.raycastTarget = false;
        }

        private static bool SegmentIntersectsRect(Vector2 start, Vector2 end, RectTransform rectTransform)
        {
            if (rectTransform == null)
            {
                return false;
            }

            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            var first = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            var minX = first.x;
            var maxX = first.x;
            var minY = first.y;
            var maxY = first.y;
            for (var i = 1; i < corners.Length; i++)
            {
                var screenPoint = RectTransformUtility.WorldToScreenPoint(null, corners[i]);
                minX = Mathf.Min(minX, screenPoint.x);
                maxX = Mathf.Max(maxX, screenPoint.x);
                minY = Mathf.Min(minY, screenPoint.y);
                maxY = Mathf.Max(maxY, screenPoint.y);
            }

            var direction = end - start;
            var minimumTime = 0f;
            var maximumTime = 1f;
            return ClipSegmentAxis(start.x, direction.x, minX, maxX, ref minimumTime, ref maximumTime) &&
                   ClipSegmentAxis(start.y, direction.y, minY, maxY, ref minimumTime, ref maximumTime);
        }

        private static bool ClipSegmentAxis(
            float start,
            float direction,
            float minimum,
            float maximum,
            ref float minimumTime,
            ref float maximumTime)
        {
            if (Mathf.Abs(direction) <= 0.0001f)
            {
                return start >= minimum && start <= maximum;
            }

            var nearTime = (minimum - start) / direction;
            var farTime = (maximum - start) / direction;
            if (nearTime > farTime)
            {
                var temporary = nearTime;
                nearTime = farTime;
                farTime = temporary;
            }

            minimumTime = Mathf.Max(minimumTime, nearTime);
            maximumTime = Mathf.Min(maximumTime, farTime);
            return minimumTime <= maximumTime;
        }

        private static RectTransform CreatePanel(
            RectTransform parent,
            string name,
            Vector2 size,
            Vector2 position,
            Color color)
        {
            var panelObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Outline));
            panelObject.transform.SetParent(parent, false);
            var rect = panelObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            panelObject.GetComponent<Image>().color = color;
            var outline = panelObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = new Vector2(2f, -2f);
            return rect;
        }

        private static Button CreateButton(
            RectTransform parent,
            string name,
            string label,
            Vector2 size,
            Vector2 position,
            int fontSize = 16)
        {
            var buttonObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            buttonObject.transform.SetParent(parent, false);
            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            buttonObject.GetComponent<Image>().color = UiTheme.ButtonBackground;
            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = new Vector2(1f, -1f);
            var text = CreateText(
                rect,
                name + " Label",
                label,
                fontSize,
                FontStyle.Bold,
                UiTheme.ValueText,
                TextAnchor.MiddleCenter,
                size,
                Vector2.zero);
            text.raycastTarget = false;
            return buttonObject.GetComponent<Button>();
        }

        private static void SetButtonState(Button button, bool interactable, bool emphasizeWhenEnabled)
        {
            if (button == null)
            {
                return;
            }

            button.interactable = interactable;
            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = interactable && emphasizeWhenEnabled
                    ? ConfirmEnabledColor
                    : interactable ? UiTheme.ButtonBackground : UiTheme.DisabledButtonBackground;
            }

            var outline = button.GetComponent<Outline>();
            if (outline != null)
            {
                outline.effectColor = interactable && emphasizeWhenEnabled
                    ? SelectedSlotOutline
                    : interactable ? UiTheme.GoldOutline : UiTheme.GoldOutlineThin;
                outline.effectDistance = interactable && emphasizeWhenEnabled
                    ? new Vector2(3f, -3f)
                    : new Vector2(1f, -1f);
            }

            var label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.color = interactable ? UiTheme.ValueText : UiTheme.LabelText;
            }
        }

        private static Text CreateText(
            RectTransform parent,
            string name,
            string value,
            int fontSize,
            FontStyle fontStyle,
            Color color,
            TextAnchor alignment,
            Vector2 size,
            Vector2 position)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            var text = textObject.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.font = FontUtility.GetCjkFont(fontSize);
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void StretchWithInset(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private sealed class CityBoardSlotBinding
        {
            public int SlotIndex;
            public RectTransform Rect;
            public Image Image;
            public Outline Outline;
            public Button Button;
            public bool Occupied;
            public bool Used;
        }
    }
}
