using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.SpecialActions;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    internal sealed class CityStyleDeclarationPreviewDialog
    {
        private static readonly Color SelectableSlotBackground = new Color(0.9f, 0.68f, 0.16f, 0.18f);
        private static readonly Color SelectedSlotBackground = new Color(0.18f, 0.78f, 0.28f, 0.42f);
        private static readonly Color SelectedSlotOutline = new Color(0.48f, 1f, 0.42f, 1f);
        private static readonly Color EmptySlotColor = new Color(1f, 1f, 1f, 0f);
        private static readonly Color ConfirmEnabledColor = new Color(0.2f, 0.62f, 0.18f, 0.98f);
        private readonly GameplayDialogRegistry dialogRegistry;
        private readonly Func<RectTransform> getCanvas;
        private readonly List<CityBoardSlotBinding> slotBindings = new List<CityBoardSlotBinding>();
        private readonly List<int> selectedSlotIndexes = new List<int>();
        private readonly List<Image> cityStyleInfluenceMarkers = new List<Image>();
        private readonly List<SpecialActionDropTargetBinding> specialActionDropTargets =
            new List<SpecialActionDropTargetBinding>();
        private readonly SpecialActionChoiceDialog specialActionPaymentDialog;

        private readonly Dictionary<Image, CityStyleMarkerViewModel> markerModels =
            new Dictionary<Image, CityStyleMarkerViewModel>();
        private readonly List<Image> draggedMarkerImages = new List<Image>();

        private CityStyleOptionsViewModel model;
        private CityStyleDeclarationPreviewView view;
        private GameObject previewCanvasObject;
        private GameObject overlayObject;
        private RawImage cityStyleCardImage;
        private RectTransform cityStyleInfluenceMarkerRoot;
        private Text cityStyleCardPlaceholder;
        private Text cityStyleTitleText;
        private Text matchStatusText;
        private Text specialActionHintText;
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
        private RectTransform specialActionDragGhost;
        private CityStyleMarkerViewModel draggedSpecialActionMarker;
        private GameObject specialActionConfirmationObject;
        private CityStyleMarkerViewModel pendingSpecialActionConfirmation;

        internal CityStyleDeclarationPreviewDialog(
            GameplayDialogRegistry dialogRegistry,
            Func<RectTransform> canvasProvider)
        {
            this.dialogRegistry = dialogRegistry ?? throw new ArgumentNullException(nameof(dialogRegistry));
            getCanvas = canvasProvider ?? throw new ArgumentNullException(nameof(canvasProvider));
            specialActionPaymentDialog = new SpecialActionChoiceDialog(
                this.dialogRegistry,
                () => view == null ? null : view.RootRect);
        }

        public bool IsShowing
        {
            get
            {
                return view != null && view.gameObject.activeSelf &&
                       overlayObject != null && overlayObject.activeSelf;
            }
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

        public void Show(CityStyleOptionsViewModel viewModel)
        {
            HideInternal(false);
            var canvasTransform = getCanvas();
            if (canvasTransform == null || viewModel == null)
            {
                return;
            }

            model = viewModel;
            currentCityStyleIndex = ResolveInitialCityStyleIndex(viewModel);
            var initialOption = GetCurrentOption();
            selectingFacilities = initialOption != null && initialOption.CanDeclare;
            BuildUi();
            if (view == null)
            {
                model = null;
                selectingFacilities = false;
                return;
            }

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
            view = dialogRegistry.InstantiateCityStyleDeclarationPreview(getCanvas());
            if (view == null)
            {
                return;
            }

            view.PrepareForUse();
            previewCanvasObject = view.gameObject;
            overlayObject = view.OverlayObject;
            cityStyleCardImage = view.CityStyleCardImage;
            cityStyleInfluenceMarkerRoot = view.CityStyleInfluenceMarkerRoot;
            cityStyleCardPlaceholder = view.CityStyleCardPlaceholder;
            cityStyleTitleText = view.CityStyleTitleText;
            matchStatusText = view.MatchStatusText;
            specialActionHintText = view.SpecialActionHintText;
            boardTitleText = view.BoardTitleText;
            cityBoardRect = view.CityBoardRect;
            previousButton = view.PreviousButton;
            nextButton = view.NextButton;
            confirmDeclarationButton = view.ConfirmDeclarationButton;
            boardOutline = view.BoardOutline;

            view.CloseButton.onClick.AddListener(Hide);
            previousButton.onClick.AddListener(() => ChangeCityStyle(-1));
            nextButton.onClick.AddListener(() => ChangeCityStyle(1));
            confirmDeclarationButton.onClick.AddListener(ConfirmDeclaration);
            view.InputHandler.Configure(ClearCurrentSelection, HandleBackNavigation, ChangeCityStyle);
            view.BoardPointerHandler.Configure(
                OnBoardLeftPointerDown,
                OnLeftPointerDrag,
                EndLeftPointerGesture);

            var boardTexture = dialogRegistry.CardVisualCatalog.GetCityBoard();
            view.CityBoardImage.texture = boardTexture;
            view.CityBoardImage.color = boardTexture == null ? UiTheme.ScrollBackground : Color.white;
        }

        private void BuildCityBoard()
        {
            slotBindings.Clear();
            if (cityBoardRect == null || view == null)
            {
                return;
            }

            for (var slotIndex = 0; slotIndex < CityBoardSlotLayout.SlotCount; slotIndex++)
            {
                var slotModel = FindCityBoardSlot(slotIndex);
                var facilityId = slotModel == null ? string.Empty : slotModel.FacilityId;
                var occupied = !string.IsNullOrEmpty(facilityId);
                var used = occupied && slotModel.Used;
                var slot = view.GetCityBoardSlot(slotIndex);
                var slotRect = slot.Root;
                slotRect.gameObject.name = "宣告槽位 " + (slotIndex + 1);
                SetCityBoardSlotRect(slotRect, slotIndex);
                slotRect.localEulerAngles = used ? new Vector3(0f, 0f, 180f) : Vector3.zero;

                var slotImage = slot.Image;
                slotImage.color = occupied ? SelectableSlotBackground : EmptySlotColor;
                slotImage.raycastTarget = true;
                var slotOutline = slot.Outline;
                slotOutline.effectColor = occupied ? UiTheme.GoldOutlineThin : EmptySlotColor;
                slotOutline.effectDistance = view.CardInteractionLayoutProfile.NormalOutlineDistance;
                var slotButton = slot.Button;
                slotButton.transition = Selectable.Transition.None;
                slotButton.interactable = false;
                var facilityTexture = occupied
                    ? dialogRegistry.CardVisualCatalog.GetFacility(facilityId)
                    : null;
                slot.FacilityImage.texture = facilityTexture;
                slot.FacilityImage.color = Color.white;
                slot.FacilityImage.raycastTarget = false;
                slot.FacilityImage.gameObject.SetActive(facilityTexture != null);
                slot.UsedBadge.SetActive(used);

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
                slot.PointerHandler.Configure(
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
            var texture = dialogRegistry.CardVisualCatalog.GetCityStyle(option.CityStyleId);
                cityStyleCardImage.texture = texture;
                cityStyleCardImage.gameObject.SetActive(texture != null);
                cityStyleCardPlaceholder.gameObject.SetActive(texture == null);
            }

            RenderCityStyleInfluenceMarkers(option == null ? string.Empty : option.CityStyleId);
            RenderSpecialActionDropTargets(option == null ? string.Empty : option.CityStyleId);
            RenderSpecialActionHint(option == null ? string.Empty : option.CityStyleId);

            SetButtonState(previousButton, currentCityStyleIndex > 0, false);
            SetButtonState(nextButton, currentCityStyleIndex < optionCount - 1, false);
            RenderSelectionState();
        }

        private void RenderCityStyleInfluenceMarkers(string cityStyleId)
        {
            CancelSpecialActionDrag();
            markerModels.Clear();
            var displayIndex = 0;
            var markerLayout = new CityStyleMarkerLayoutTracker(view.CardBoardVisualLayout);
            var markers = model == null ? null : model.CityStyleMarkers;
            if (!string.IsNullOrEmpty(cityStyleId) && markers != null)
            {
                foreach (var marker in markers)
                    if (marker != null && marker.CityStyleId == cityStyleId)
                        markerLayout.Register(marker.CityStyleId, marker.MarkerArea, marker.PlayerId);
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
                view.CardBoardVisualLayout,
                marker,
                "样式预览影响力 玩家" + markerModel.PlayerId + " 标记" + (displayIndex + 1),
                UiTheme.GetPlayerColor(markerModel.PlayerColor, 1f),
                marker.sprite,
                view.CardBoardVisualLayout.DeclarationPreviewMarkerSize,
                markerModel.CityStyleId,
                placement);
            markerModels[marker] = markerModel;
            marker.raycastTarget = markerModel.CanDragForSpecialAction;
            var markerButton = marker.GetComponent<Button>();
            markerButton.transition = Selectable.Transition.None;
            markerButton.interactable = markerModel.CanDragForSpecialAction;
            marker.GetComponent<CardPointerInteraction>().ConfigureDrag(
                () => markerModel.CanDragForSpecialAction &&
                      model != null &&
                      model.TryUseSpecialAction != null &&
                      !IsSpecialActionModalOpen(),
                eventData => BeginSpecialActionDrag(markerModel, marker, eventData),
                eventData => FacilityCardDragUtility.MoveDragGhost(specialActionDragGhost, eventData),
                eventData => EndSpecialActionDrag(markerModel, eventData),
                CancelSpecialActionDrag);
        }

        private Image EnsureCityStyleInfluenceMarker(int markerIndex)
        {
            if (cityStyleInfluenceMarkerRoot == null || view == null)
            {
                return null;
            }

            while (cityStyleInfluenceMarkers.Count <= markerIndex)
            {
                var image = view.CreateInfluenceMarker();
                if (image == null)
                {
                    return null;
                }

                cityStyleInfluenceMarkers.Add(image);
            }

            return cityStyleInfluenceMarkers[markerIndex];
        }

        private void RenderSpecialActionDropTargets(string cityStyleId)
        {
            var requiredAreas = new List<string>();
            var markers = model == null ? null : model.CityStyleMarkers;
            if (!string.IsNullOrEmpty(cityStyleId) && markers != null)
            {
                for (var i = 0; i < markers.Count; i++)
                {
                    var marker = markers[i];
                    if (marker != null &&
                        marker.CityStyleId == cityStyleId &&
                        marker.CanDragForSpecialAction &&
                        !string.IsNullOrEmpty(marker.LegalDropArea) &&
                        !requiredAreas.Contains(marker.LegalDropArea))
                    {
                        requiredAreas.Add(marker.LegalDropArea);
                    }
                }
            }

            for (var i = 0; i < requiredAreas.Count; i++)
            {
                var binding = EnsureSpecialActionDropTarget(i);
                if (binding == null)
                {
                    continue;
                }

                var area = requiredAreas[i];
                binding.MarkerArea = area;
                binding.Target.Configure(area);
                var bounds = CityStyleMarkerRenderer.ResolveSpecialActionAreaBounds(
                    view.CardBoardVisualLayout,
                    cityStyleId,
                    area);
                binding.Rect.anchorMin = new Vector2(bounds.xMin, bounds.yMin);
                binding.Rect.anchorMax = new Vector2(bounds.xMax, bounds.yMax);
                binding.Rect.pivot = view.CardInteractionLayoutProfile.DragGhostLayout.RootLayout.Pivot;
                binding.Rect.offsetMin = Vector2.zero;
                binding.Rect.offsetMax = Vector2.zero;
                binding.Rect.gameObject.name = "特殊行动合法落区 " + area;
                binding.Rect.gameObject.SetActive(false);
            }

            for (var i = requiredAreas.Count; i < specialActionDropTargets.Count; i++)
            {
                specialActionDropTargets[i].Rect.gameObject.SetActive(false);
            }
        }

        private SpecialActionDropTargetBinding EnsureSpecialActionDropTarget(int index)
        {
            while (specialActionDropTargets.Count <= index)
            {
                var target = view == null ? null : view.CreateSpecialActionDropTarget();
                if (target == null)
                {
                    return null;
                }

                var targetObject = target.gameObject;
                var image = targetObject.GetComponent<Image>();
                var outline = targetObject.GetComponent<Outline>();
                specialActionDropTargets.Add(new SpecialActionDropTargetBinding
                {
                    Rect = targetObject.GetComponent<RectTransform>(),
                    Image = image,
                    Outline = outline,
                    Target = target
                });
            }

            return specialActionDropTargets[index];
        }

        private void RenderSpecialActionHint(string cityStyleId)
        {
            if (specialActionHintText == null)
            {
                return;
            }

            specialActionHintText.text = string.Empty;
            var markers = model == null ? null : model.CityStyleMarkers;
            if (markers == null)
            {
                return;
            }

            for (var i = 0; i < markers.Count; i++)
            {
                var marker = markers[i];
                if (marker == null || marker.CityStyleId != cityStyleId ||
                    string.IsNullOrEmpty(marker.SpecialActionId))
                {
                    continue;
                }

                if (marker.CanDragForSpecialAction)
                {
                    specialActionHintText.text = "拖动本方可用影响力到绿色高亮的已使用区，发动特殊行动。";
                    specialActionHintText.color = new Color(0.48f, 1f, 0.42f, 1f);
                    return;
                }

                if (!string.IsNullOrEmpty(marker.SpecialActionDisabledReason))
                {
                    specialActionHintText.text = "特殊行动当前不可发动：" + marker.SpecialActionDisabledReason;
                    specialActionHintText.color = new Color(1f, 0.68f, 0.28f, 1f);
                    return;
                }
            }
        }

        private void BeginSpecialActionDrag(
            CityStyleMarkerViewModel markerModel,
            Image markerImage,
            PointerEventData eventData)
        {
            if (IsSpecialActionModalOpen())
            {
                return;
            }

            CancelSpecialActionDrag();
            if (markerModel == null || markerImage == null ||
                !markerModel.CanDragForSpecialAction ||
                string.IsNullOrEmpty(markerModel.LegalDropArea))
            {
                return;
            }

            draggedSpecialActionMarker = markerModel;
            var canvasRect = previewCanvasObject == null
                ? null
                : previewCanvasObject.GetComponent<RectTransform>();
            specialActionDragGhost = FacilityCardDragUtility.CreateDragGhost(
                canvasRect,
                markerImage.rectTransform,
                null,
                string.Empty,
                null,
                view.CardInteractionLayoutProfile.DragGhostLayout);
            if (specialActionDragGhost != null)
            {
                specialActionDragGhost.gameObject.name = "特殊行动影响力拖动虚影";
                specialActionDragGhost.GetComponent<RawImage>().enabled = false;
                foreach (var pair in markerModels)
                {
                    var candidate = pair.Value;
                    var sameGroup = candidate == markerModel ||
                        (markerModel.SpecialActionId == SpecialActionDatabase.MilitaryIndustrialArea &&
                         candidate.SpecialActionId == markerModel.SpecialActionId &&
                         candidate.CityStyleId == markerModel.CityStyleId &&
                         candidate.PlayerId == markerModel.PlayerId &&
                         candidate.CanDragForSpecialAction);
                    if (!sameGroup) continue;
                    var source = pair.Key;
                    var ghostObject = new GameObject("影响力拖动组成员", typeof(RectTransform), typeof(Image));
                    var image = ghostObject.GetComponent<Image>();
                    image.transform.SetParent(specialActionDragGhost, false);
                    image.rectTransform.sizeDelta = source.rectTransform.rect.size;
                    image.rectTransform.anchoredPosition =
                        canvasRect.InverseTransformPoint(source.transform.position) -
                        canvasRect.InverseTransformPoint(markerImage.transform.position);
                    image.sprite = source.sprite;
                    image.color = source.color;
                    image.preserveAspect = true;
                    image.raycastTarget = false;
                    InfluenceModelUiAnchor.Configure(image, view.CardBoardVisualLayout.InfluencePiecePrefab, source.color);
                    source.GetComponent<InfluenceModelUiAnchor>().SetVisible(false);
                    draggedMarkerImages.Add(source);
                }

                FacilityCardDragUtility.MoveDragGhost(specialActionDragGhost, eventData);
            }

            SetSpecialActionDropTargetsVisible(markerModel.LegalDropArea, true);
            if (specialActionHintText != null)
            {
                specialActionHintText.text = "松开到绿色高亮区即可发动；其他区域不会提交命令。";
                specialActionHintText.color = new Color(0.48f, 1f, 0.42f, 1f);
            }
        }

        private void EndSpecialActionDrag(
            CityStyleMarkerViewModel markerModel,
            PointerEventData eventData)
        {
            var dropArea = ResolveSpecialActionDropArea(eventData);
            var legal = markerModel != null &&
                        markerModel == draggedSpecialActionMarker &&
                        !string.IsNullOrEmpty(markerModel.LegalDropArea) &&
                        markerModel.LegalDropArea == dropArea;
            CancelSpecialActionDrag();

            if (!legal)
            {
                if (specialActionHintText != null)
                {
                    specialActionHintText.text = "未落在合法高亮区，特殊行动未发动。";
                    specialActionHintText.color = new Color(1f, 0.48f, 0.32f, 1f);
                }

                return;
            }

            if (!string.IsNullOrEmpty(markerModel.SpecialActionWarning))
            {
                ShowSpecialActionWarningConfirmation(markerModel);
                return;
            }

            ContinueSpecialActionFromMarker(markerModel);
        }

        private void ShowSpecialActionWarningConfirmation(CityStyleMarkerViewModel markerModel)
        {
            HideSpecialActionWarningConfirmation();
            if (markerModel == null || overlayObject == null || view == null)
            {
                return;
            }

            pendingSpecialActionConfirmation = markerModel;
            specialActionConfirmationObject = view.SpecialActionWarningObject;
            view.SpecialActionWarningMessage.text =
                markerModel.SpecialActionWarning + "\n仍要消耗主要行动与本次样式行动次数吗？";
            view.CancelSpecialActionWarningButton.onClick.RemoveAllListeners();
            view.ConfirmSpecialActionWarningButton.onClick.RemoveAllListeners();
            view.CancelSpecialActionWarningButton.onClick.AddListener(HideSpecialActionWarningConfirmation);
            view.ConfirmSpecialActionWarningButton.onClick.AddListener(ConfirmPendingSpecialAction);
            specialActionConfirmationObject.SetActive(true);
            specialActionConfirmationObject.transform.SetAsLastSibling();
        }

        private void ConfirmPendingSpecialAction()
        {
            var marker = pendingSpecialActionConfirmation;
            HideSpecialActionWarningConfirmation();
            if (marker != null)
            {
                ContinueSpecialActionFromMarker(marker);
            }
        }

        private void ContinueSpecialActionFromMarker(CityStyleMarkerViewModel markerModel)
        {
            if (markerModel == null)
            {
                return;
            }

            if (markerModel.SpecialActionId == SpecialActionDatabase.CompositePowerSystem)
            {
                ShowCompositePowerPayment(markerModel);
                return;
            }

            TrySubmitSpecialActionFromMarker(markerModel, 0, 0);
        }

        private void ShowCompositePowerPayment(CityStyleMarkerViewModel markerModel)
        {
            specialActionPaymentDialog.ShowCompositePayment(
                markerModel.MaximumOriginiumPayment,
                markerModel.MaximumIronPayment,
                values =>
                {
                    if (values == null || values.Count < 2)
                    {
                        return;
                    }

                    TrySubmitSpecialActionFromMarker(markerModel, values[0], values[1]);
                },
                () => CancelCompositePowerPayment(markerModel));
            if (specialActionHintText != null)
            {
                specialActionHintText.text = "请选择源岩与异铁的支付组合；取消会返回样式卡预览。";
                specialActionHintText.color = UiTheme.GoldText;
            }
        }

        private void CancelCompositePowerPayment(CityStyleMarkerViewModel markerModel)
        {
            specialActionPaymentDialog.Hide();
            RenderSpecialActionHint(markerModel == null ? CurrentCityStyleId : markerModel.CityStyleId);
            if (specialActionHintText != null)
            {
                specialActionHintText.text = "已取消材料支付，特殊行动未发动。";
                specialActionHintText.color = new Color(1f, 0.68f, 0.28f, 1f);
            }
        }

        private void TrySubmitSpecialActionFromMarker(
            CityStyleMarkerViewModel markerModel,
            int originiumAmount,
            int ironAmount)
        {
            var activeModel = model;
            var attemptedView = view;
            if (activeModel == null || activeModel.TryUseSpecialAction == null || markerModel == null)
            {
                return;
            }

            CancelSpecialActionDrag();
            if (attemptedView != null)
            {
                attemptedView.gameObject.SetActive(false);
            }

            var succeeded = activeModel.TryUseSpecialAction(
                markerModel.SpecialActionId,
                markerModel.MarkerId,
                originiumAmount,
                ironAmount);
            if (succeeded)
            {
                if (ReferenceEquals(view, attemptedView))
                {
                    HideInternal(false);
                }

                return;
            }

            if (ReferenceEquals(view, attemptedView) && attemptedView != null)
            {
                attemptedView.gameObject.SetActive(true);
                RenderSpecialActionHint(markerModel.CityStyleId);
            }
        }

        private void HideSpecialActionWarningConfirmation()
        {
            pendingSpecialActionConfirmation = null;
            if (specialActionConfirmationObject == null)
            {
                return;
            }

            if (view != null)
            {
                view.CancelSpecialActionWarningButton.onClick.RemoveAllListeners();
                view.ConfirmSpecialActionWarningButton.onClick.RemoveAllListeners();
            }

            specialActionConfirmationObject.SetActive(false);
            specialActionConfirmationObject = null;
        }

        private string ResolveSpecialActionDropArea(PointerEventData eventData)
        {
            var hit = eventData == null ? null : eventData.pointerCurrentRaycast.gameObject;
            var current = hit == null ? null : hit.transform;
            while (current != null)
            {
                var target = current.GetComponent<CityStyleSpecialActionDropTarget>();
                if (target != null && target.isActiveAndEnabled)
                {
                    return target.MarkerArea;
                }

                current = current.parent;
            }

            if (eventData == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < specialActionDropTargets.Count; i++)
            {
                var binding = specialActionDropTargets[i];
                if (binding.Rect.gameObject.activeInHierarchy &&
                    RectTransformUtility.RectangleContainsScreenPoint(
                        binding.Rect,
                        eventData.position,
                        eventData.pressEventCamera))
                {
                    return binding.MarkerArea;
                }
            }

            return string.Empty;
        }

        private void SetSpecialActionDropTargetsVisible(string legalArea, bool visible)
        {
            for (var i = 0; i < specialActionDropTargets.Count; i++)
            {
                var binding = specialActionDropTargets[i];
                binding.Rect.gameObject.SetActive(visible && binding.MarkerArea == legalArea);
            }
        }

        private void CancelSpecialActionDrag()
        {
            foreach (var image in draggedMarkerImages)
            {
                if (image != null) image.GetComponent<InfluenceModelUiAnchor>().SetVisible(true);
            }
            draggedMarkerImages.Clear();
            FacilityCardDragUtility.DestroyDragGhost(ref specialActionDragGhost);
            draggedSpecialActionMarker = null;
            SetSpecialActionDropTargetsVisible(string.Empty, false);
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
                    ? view.CardInteractionLayoutProfile.CityStyleBoardActiveOutlineDistance
                    : view.CardInteractionLayoutProfile.CityStyleBoardInactiveOutlineDistance;
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
                    binding.Outline.effectDistance =
                        view.CardInteractionLayoutProfile.CityStyleSelectedSlotOutlineDistance;
                }
                else if (hasStyleCard)
                {
                    binding.Image.color = SelectableSlotBackground;
                    binding.Outline.effectColor = new Color(1f, 0.82f, 0.24f, 0.92f);
                    binding.Outline.effectDistance =
                        view.CardInteractionLayoutProfile.CityStyleSelectableSlotOutlineDistance;
                }
                else
                {
                    binding.Image.color = new Color(0.12f, 0.08f, 0.04f, 0.18f);
                    binding.Outline.effectColor = UiTheme.GoldOutlineThin;
                    binding.Outline.effectDistance = view.CardInteractionLayoutProfile.NormalOutlineDistance;
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
            if (specialActionPaymentDialog.IsShowing || specialActionConfirmationObject != null)
            {
                return;
            }

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
            CancelSpecialActionDrag();
            EndLeftPointerGesture();
            RenderCurrentCityStyle();
            if (option != null)
            {
                ValidateCurrentSelection();
            }
        }

        private void HandleBackNavigation()
        {
            if (specialActionPaymentDialog.IsShowing)
            {
                CancelCompositePowerPayment(null);
                return;
            }

            if (specialActionConfirmationObject != null)
            {
                HideSpecialActionWarningConfirmation();
                return;
            }

            Hide();
        }

        private void ClearCurrentSelection()
        {
            if (IsSpecialActionModalOpen())
            {
                return;
            }

            selectedSlotIndexes.Clear();
            EndLeftPointerGesture();
            ValidateCurrentSelection();
        }

        private void OnBoardLeftPointerDown(Vector2 pointerPosition)
        {
            if (!selectingFacilities || IsSpecialActionModalOpen())
            {
                return;
            }

            leftPointerHeld = true;
            hasLastPointerPosition = true;
            lastPointerPosition = pointerPosition;
        }

        private void OnSlotLeftPointerDown(int slotIndex, Vector2 pointerPosition)
        {
            if (!selectingFacilities || IsSpecialActionModalOpen())
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
            var activeModel = model;
            var attemptedView = view;
            EndLeftPointerGesture();
            if (attemptedView != null)
            {
                attemptedView.gameObject.SetActive(false);
            }

            if (activeModel.ConfirmSelection(styleId, selection))
            {
                if (ReferenceEquals(view, attemptedView))
                {
                    HideInternal(false);
                }

                return;
            }

            if (ReferenceEquals(view, attemptedView) && attemptedView != null)
            {
                attemptedView.gameObject.SetActive(true);
                ValidateCurrentSelection();
            }
        }

        private bool IsSpecialActionModalOpen()
        {
            return specialActionPaymentDialog.IsShowing ||
                   specialActionConfirmationObject != null;
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
            CancelSpecialActionDrag();
            specialActionPaymentDialog.Hide();
            HideSpecialActionWarningConfirmation();
            var oldView = view;
            if (oldView != null)
            {
                oldView.ClearCallbacks();
                oldView.DestroyDynamicInstances();
                oldView.gameObject.SetActive(false);
            }

            model = null;
            selectingFacilities = false;
            selectedSlotIndexes.Clear();
            slotBindings.Clear();
            cityStyleInfluenceMarkers.Clear();
            specialActionDropTargets.Clear();
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

            view = null;
            cityStyleCardImage = null;
            cityStyleCardPlaceholder = null;
            cityStyleTitleText = null;
            matchStatusText = null;
            specialActionHintText = null;
            boardTitleText = null;
            previousButton = null;
            nextButton = null;
            confirmDeclarationButton = null;
            boardOutline = null;

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

        private void SetCityBoardSlotRect(RectTransform rect, int slotIndex)
        {
            view.ApplyCityBoardSlotLayout(rect, slotIndex);
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

        private void SetButtonState(Button button, bool interactable, bool emphasizeWhenEnabled)
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
                    ? view.CardInteractionLayoutProfile.CityStyleEmphasizedButtonOutlineDistance
                    : view.CardInteractionLayoutProfile.NormalOutlineDistance;
            }

            var label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.color = interactable ? UiTheme.ValueText : UiTheme.LabelText;
            }
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

        private sealed class SpecialActionDropTargetBinding
        {
            public string MarkerArea;
            public RectTransform Rect;
            public Image Image;
            public Outline Outline;
            public CityStyleSpecialActionDropTarget Target;
        }
    }
}
