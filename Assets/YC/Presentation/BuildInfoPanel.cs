using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class BuildInfoPanel : MonoBehaviour
    {
        private const float ExternalCardFramePadding = 6f;
        private const int ExternalFacilitySlotCount = 6;/*建设卡部分*/
        private const float ExternalCityStyleLeftPadding = 20f;
        private const float ExternalCityStyleCardWidth = 143f;
        private const float ExternalCityStyleCardHeight = 91f;
        private const float ExternalCityStyleCardHorizontalSpacing = 40f;
        private const float ExternalCityStyleCardVerticalSpacing = 20f;/*样式卡部分*/
        private const int ExternalCityStyleColumnCount = 2;
        private static readonly Color OccupiedCityBoardSlotBackground = new Color(0.18f, 0.105f, 0.055f, 0.82f);
        private static readonly Color InvisibleCityBoardSlotColor = new Color(1f, 1f, 1f, 0f);
        private static readonly Color ExternalCardAreaBackground = new Color32(57, 47, 26, 255);
        private static readonly Color ExternalCardBackground = new Color(0.09f, 0.07f, 0.045f, 0.72f);
        private static readonly Color ExternalCardNormalOutline = new Color(0.78f, 0.63f, 0.38f, 0.72f);
        private static readonly Color ExternalCardSelectedOutline = new Color(1f, 0.82f, 0.22f, 1f);
        private static readonly Color FacilityEffectSelectableOutline = new Color(0.35f, 1f, 0.48f, 1f);
        private static readonly Color FacilityEffectUnavailableTint = new Color(0.45f, 0.45f, 0.45f, 0.72f);
        private static readonly Color LegalCityBoardSlotBackground = new Color(0.18f, 0.68f, 0.28f, 0.34f);
        private static readonly Color LegalCityBoardSlotOutline = new Color(0.45f, 1f, 0.42f, 0.98f);
        [SerializeField] private BuildInfoPanelView view;
        [SerializeField] private CardBoardVisualLayout cardBoardVisualLayout;
        private CardVisualCatalog cardVisualCatalog;
        private readonly List<ExternalCardBinding> facilityCardBindings = new List<ExternalCardBinding>();
        private readonly List<ExternalCardBinding> cityStyleCardBindings = new List<ExternalCardBinding>();
        private readonly List<CityBoardSlotBinding> cityBoardSlotBindings = new List<CityBoardSlotBinding>();
        private readonly HashSet<string> draggableFacilityIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> selectableFacilityEffectIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<int> legalCityBoardSlotIndexes = new HashSet<int>();
        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform contentRoot;
        private RectTransform externalFacilityArea;
        private RectTransform externalCityStyleArea;
        private bool initialized;
        private GameState currentState;
        private int currentPlayerId;
        private string selectedFacilityId = string.Empty;
        private ZoomableImageViewerController cardImageViewer;
        private bool buildInteractionActive;
        private RectTransform facilityDragGhost;
        private RectTransform pendingBuildGhost;
        private string pendingBuildGhostFacilityId = string.Empty;
        private int pendingBuildGhostSlotIndex = -1;
        private Text buildAvailabilityText;
        private bool facilityEffectSelectionActive;
        private Action<string> selectFacilityForEffect;
        private Action cancelFacilityEffectSelection;

        public event Action<string> CityStyleClicked;
        public event Action<string> FacilityDragStarted;
        public event Action<string, int> FacilityDropped;

        public bool IsFacilityEffectSelectionActive => facilityEffectSelectionActive;
        public BuildInfoPanelView View => view;
        public CardBoardVisualLayout CardBoardVisualLayout => cardBoardVisualLayout;

        public bool ConfigureCardVisualCatalog(CardVisualCatalog configuredCatalog)
        {
            var reason = "缺少 CardVisualCatalog。";
            if (configuredCatalog == null ||
                !configuredCatalog.TryValidateConfiguration(out reason))
            {
                Debug.LogError("[BuildInfoPanel] CardVisualCatalog 配置无效：" + reason, this);
                return false;
            }

            cardVisualCatalog = configuredCatalog;
            return true;
        }

        private sealed class ExternalCardBinding
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public BuildInfoSlotView Slot;
            public BuildInfoItemView Item;
            public Button Button;
            public Outline Outline;
            public RawImage CardImage;
            public Text FallbackText;
        }

        private sealed class CityBoardSlotBinding
        {
            public int SlotIndex;
            public bool IsEmpty;
            public BuildInfoSlotView Slot;
            public BuildInfoItemView Item;
            public RectTransform Rect;
            public Image Image;
            public Outline Outline;
            public Color DefaultBackground;
            public Color DefaultOutline;
            public Vector2 DefaultOutlineDistance;
        }

        private void Awake()
        {
            if (!Bind(view))
            {
                enabled = false;
            }
        }

        public bool Bind(BuildInfoPanelView configuredView)
        {
            if (initialized)
            {
                return configuredView == view;
            }

            var reason = string.Empty;
            if (cardBoardVisualLayout == null ||
                !cardBoardVisualLayout.TryValidateConfiguration(out reason))
            {
                Debug.LogError(
                    "[BuildInfoPanel] 缺少有效 CardBoardVisualLayout：" +
                    (cardBoardVisualLayout == null ? "引用为空。" : reason),
                    this);
                return false;
            }

            if (configuredView == null ||
                !configuredView.TryValidateConfiguration(out reason) ||
                !configuredView.IsBoundTo(this))
            {
                Debug.LogError(
                    "[BuildInfoPanel] View 配置无效：" +
                    (configuredView == null ? "缺少序列化 View。" : reason),
                    this);
                return false;
            }

            view = configuredView;
            panelTransform = view.PanelTransform;
            contentArea = view.ContentArea;
            contentRoot = view.ContentRoot;
            externalFacilityArea = view.ExternalFacilityArea;
            externalCityStyleArea = view.ExternalCityStyleArea;
            buildAvailabilityText = view.BuildAvailabilityText;
            BindFixedViews();
            initialized = true;
            return true;
        }

        private void BindFixedViews()
        {
            facilityCardBindings.Clear();
            for (var i = 0; i < view.ExternalFacilitySlots.Length; i++)
            {
                var slot = view.ExternalFacilitySlots[i];
                var binding = new ExternalCardBinding
                {
                    Slot = slot,
                    Button = slot.Button,
                    Outline = slot.Outline,
                    FallbackText = slot.EmptyLabel
                };
                facilityCardBindings.Add(binding);
                slot.PointerInteraction.ConfigureClick(slot.Button, () => HandleExternalFacilityClick(binding), null);
                slot.PointerInteraction.ConfigureDrag(
                    () => CanDragExternalFacility(binding),
                    eventData => BeginExternalFacilityDrag(binding, eventData),
                    MoveExternalFacilityDrag,
                    eventData => EndExternalFacilityDrag(binding, eventData),
                    DestroyFacilityDragGhost);
            }

            cityBoardSlotBindings.Clear();
            for (var i = 0; i < view.CityBoardSlots.Length; i++)
            {
                var slot = view.CityBoardSlots[i];
                CityBoardSlotLayout.Apply(slot.Root, cardBoardVisualLayout, i);
                slot.DropTarget.Configure(i);
                cityBoardSlotBindings.Add(new CityBoardSlotBinding
                {
                    SlotIndex = i,
                    Slot = slot,
                    Rect = slot.Root,
                    Image = slot.Background,
                    Outline = slot.Outline
                });
            }
        }

        public void Refresh(GameState state, int playerId)
        {
            currentState = state;
            currentPlayerId = playerId;
            if (!initialized)
            {
                Debug.LogError("[BuildInfoPanel] Refresh 前必须绑定已配置 View。", this);
                return;
            }

            RebuildContent();
        }

        public bool BeginFacilityEffectSelection(
            IEnumerable<string> selectableFacilityIds,
            Action<string> select,
            Action cancel)
        {
            EndFacilityEffectSelection();
            if (selectableFacilityIds == null || select == null || currentState == null || currentState.Decks == null)
            {
                return false;
            }

            var currentSupply = new HashSet<string>(currentState.Decks.FacilitySupply, StringComparer.Ordinal);
            foreach (var facilityId in selectableFacilityIds)
            {
                if (!string.IsNullOrEmpty(facilityId) && currentSupply.Contains(facilityId))
                {
                    selectableFacilityEffectIds.Add(facilityId);
                }
            }

            if (selectableFacilityEffectIds.Count == 0)
            {
                return false;
            }

            facilityEffectSelectionActive = true;
            selectFacilityForEffect = select;
            cancelFacilityEffectSelection = cancel;
            cardImageViewer?.Close();
            DestroyFacilityDragGhost();
            UpdateExternalCardHighlights();
            UpdateExternalFacilityAvailability();
            return true;
        }

        public bool TryCancelFacilityEffectSelection()
        {
            if (!facilityEffectSelectionActive)
            {
                return false;
            }

            var cancel = cancelFacilityEffectSelection;
            EndFacilityEffectSelection();
            cancel?.Invoke();
            return true;
        }

        public void EndFacilityEffectSelection()
        {
            facilityEffectSelectionActive = false;
            selectableFacilityEffectIds.Clear();
            selectFacilityForEffect = null;
            cancelFacilityEffectSelection = null;
            UpdateExternalCardHighlights();
            UpdateExternalFacilityAvailability();
        }

        public void SetBuildInteraction(
            bool active,
            IEnumerable<string> draggableIds,
            IEnumerable<int> legalSlotIndexes,
            string pendingFacilityId)
        {
            buildInteractionActive = active;
            draggableFacilityIds.Clear();
            legalCityBoardSlotIndexes.Clear();

            if (active && draggableIds != null)
            {
                foreach (var facilityId in draggableIds)
                {
                    if (!string.IsNullOrEmpty(facilityId))
                    {
                        draggableFacilityIds.Add(facilityId);
                    }
                }
            }

            if (active && legalSlotIndexes != null)
            {
                foreach (var slotIndex in legalSlotIndexes)
                {
                    if (slotIndex >= 0 && slotIndex < BuildFacilityService.CityBoardSlotCount)
                    {
                        legalCityBoardSlotIndexes.Add(slotIndex);
                    }
                }
            }

            selectedFacilityId = active ? pendingFacilityId ?? string.Empty : string.Empty;
            if (!active)
            {
                DestroyFacilityDragGhost();
            }

            UpdateExternalCardHighlights();
            UpdateExternalFacilityAvailability();
            UpdateCityBoardSlotHighlights();
        }

        public void SetBuildAvailabilityMessage(string message)
        {
            if (buildAvailabilityText == null)
            {
                return;
            }

            buildAvailabilityText.text = message ?? string.Empty;
            buildAvailabilityText.gameObject.SetActive(!string.IsNullOrEmpty(buildAvailabilityText.text));
        }

        public void SetPendingBuildGhost(
            bool visible,
            string facilityId,
            int cityBoardSlotIndex,
            Action beginDrag,
            Action<int> drop)
        {
            if (!visible || string.IsNullOrEmpty(facilityId) || cityBoardSlotIndex < 0)
            {
                DestroyPendingBuildGhost();
                return;
            }

            if (pendingBuildGhost != null &&
                pendingBuildGhostFacilityId == facilityId &&
                pendingBuildGhostSlotIndex == cityBoardSlotIndex)
            {
                return;
            }

            DestroyPendingBuildGhost();
            CityBoardSlotBinding slot = null;
            for (var i = 0; i < cityBoardSlotBindings.Count; i++)
            {
                if (cityBoardSlotBindings[i].SlotIndex == cityBoardSlotIndex)
                {
                    slot = cityBoardSlotBindings[i];
                    break;
                }
            }

            if (slot == null || slot.Rect == null)
            {
                return;
            }

            pendingBuildGhostFacilityId = facilityId;
            pendingBuildGhostSlotIndex = cityBoardSlotIndex;
            var ghost = InstantiateItem(view.PendingBuildGhostTemplate, slot.Rect, "本地建设虚影");
            pendingBuildGhost = ghost.Root;
            pendingBuildGhost.anchorMin = new Vector2(0.08f, 0.08f);
            pendingBuildGhost.anchorMax = new Vector2(0.92f, 0.92f);
            pendingBuildGhost.offsetMin = Vector2.zero;
            pendingBuildGhost.offsetMax = Vector2.zero;

            var rawImage = ghost.RawImage;
            rawImage.texture = TryLoadFacilityCardTexture(facilityId);
            rawImage.color = rawImage.texture == null ? ExternalCardBackground : Color.white;
            rawImage.raycastTarget = true;
            var canvasGroup = ghost.CanvasGroup;
            canvasGroup.alpha = 0.78f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;

            var ghostBinding = new ExternalCardBinding
            {
                Id = facilityId,
                Label = FacilityCardDatabase.Get(facilityId)?.Name ?? facilityId,
                Item = ghost,
                Button = ghost.Button,
                CardImage = rawImage
            };
            ghost.PointerInteraction.ConfigureDrag(
                () => buildInteractionActive,
                eventData =>
                {
                    DestroyFacilityDragGhost();
                    facilityDragGhost = CreateFacilityDragGhost(ghostBinding);
                    MoveExternalFacilityDrag(eventData);
                    beginDrag?.Invoke();
                },
                MoveExternalFacilityDrag,
                eventData =>
                {
                    var targetSlotIndex = ResolveDropCityBoardSlotIndex(eventData);
                    DestroyFacilityDragGhost();
                    drop?.Invoke(targetSlotIndex);
                },
                DestroyFacilityDragGhost);

        }

        private void RebuildContent()
        {
            Canvas.ForceUpdateCanvases();
            panelTransform.ForceUpdateRectTransforms();
            contentArea.ForceUpdateRectTransforms();
            contentRoot.ForceUpdateRectTransforms();
            ClearExternalCityStyleCards();
            AddCityBoardSection();
            RebuildExternalFacilityCards();
            RebuildExternalCityStyleCards();
            RebuildLayout();
        }

        private void ClearExternalCityStyleCards()
        {
            for (var i = cityStyleCardBindings.Count - 1; i >= 0; i--)
            {
                if (cityStyleCardBindings[i] != null && cityStyleCardBindings[i].Item != null)
                {
                    DestroyDynamicObject(cityStyleCardBindings[i].Item.gameObject);
                }
            }

            cityStyleCardBindings.Clear();
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

        private void BindExternalFacilitySlot(
            ExternalCardBinding binding,
            string facilityId,
            string label,
            Texture2D texture)
        {
            if (binding.Item != null)
            {
                DestroyDynamicObject(binding.Item.gameObject);
                binding.Item = null;
            }

            binding.Id = facilityId;
            binding.Label = label;
            binding.Button.interactable = !string.IsNullOrEmpty(facilityId);
            binding.Slot.EmptyLabel.text = "空卡位";
            binding.Slot.EmptyLabel.gameObject.SetActive(string.IsNullOrEmpty(facilityId));
            binding.CardImage = null;
            binding.FallbackText = binding.Slot.EmptyLabel;
            if (!string.IsNullOrEmpty(facilityId))
            {
                binding.Item = InstantiateItem(view.CardContentTemplate, binding.Slot.ContentRoot, "Card Content");
                binding.CardImage = binding.Item.RawImage;
                binding.FallbackText = binding.Item.FallbackText;
                binding.CardImage.texture = texture;
                binding.CardImage.gameObject.SetActive(texture != null);
                binding.FallbackText.text = texture == null ? label : string.Empty;
                binding.FallbackText.gameObject.SetActive(texture == null);
            }

            UpdateExternalFacilityAvailability(binding);
        }

        private void HandleExternalFacilityClick(ExternalCardBinding binding)
        {
            if (binding == null || string.IsNullOrEmpty(binding.Id))
            {
                return;
            }

            if (!facilityEffectSelectionActive)
            {
                OpenCardImage(
                    binding.Label,
                    binding.CardImage == null ? null : binding.CardImage.texture as Texture2D);
                return;
            }

            if (!selectableFacilityEffectIds.Contains(binding.Id))
            {
                return;
            }

            var facilityId = binding.Id;
            var select = selectFacilityForEffect;
            EndFacilityEffectSelection();
            select?.Invoke(facilityId);
        }

        private void UpdateExternalFacilityAvailability()
        {
            for (var i = 0; i < facilityCardBindings.Count; i++)
            {
                UpdateExternalFacilityAvailability(facilityCardBindings[i]);
            }
        }

        private void UpdateExternalFacilityAvailability(ExternalCardBinding binding)
        {
            if (binding == null)
            {
                return;
            }

            var selectableForEffect = facilityEffectSelectionActive &&
                                      !string.IsNullOrEmpty(binding.Id) &&
                                      selectableFacilityEffectIds.Contains(binding.Id);
            var unavailableForEffect = facilityEffectSelectionActive &&
                                       !string.IsNullOrEmpty(binding.Id) &&
                                       !selectableForEffect;
            if (binding.CardImage != null)
            {
                binding.CardImage.color = unavailableForEffect ? FacilityEffectUnavailableTint : Color.white;
            }

            if (binding.FallbackText != null)
            {
                binding.FallbackText.color = unavailableForEffect ? FacilityEffectUnavailableTint : UiTheme.ValueText;
            }

            if (binding.Outline != null)
            {
                if (facilityEffectSelectionActive)
                {
                    binding.Outline.effectColor = selectableForEffect
                        ? FacilityEffectSelectableOutline
                        : ExternalCardNormalOutline;
                    binding.Outline.effectDistance = selectableForEffect
                        ? new Vector2(3f, -3f)
                        : new Vector2(1f, -1f);
                }
                else
                {
                    SetExternalCardOutline(
                        binding.Outline,
                        !string.IsNullOrEmpty(binding.Id) && binding.Id == selectedFacilityId);
                }
            }
        }

        private bool CanDragExternalFacility(ExternalCardBinding binding)
        {
            return !facilityEffectSelectionActive &&
                   buildInteractionActive &&
                   binding != null &&
                   !string.IsNullOrEmpty(binding.Id) &&
                   draggableFacilityIds.Contains(binding.Id);
        }

        private void BeginExternalFacilityDrag(ExternalCardBinding binding, PointerEventData eventData)
        {
            if (!CanDragExternalFacility(binding))
            {
                return;
            }

            DestroyFacilityDragGhost();
            facilityDragGhost = CreateFacilityDragGhost(binding);
            MoveExternalFacilityDrag(eventData);
            FacilityDragStarted?.Invoke(binding.Id);
        }

        private RectTransform CreateFacilityDragGhost(ExternalCardBinding binding)
        {
            var canvas = externalFacilityArea == null ? null : externalFacilityArea.GetComponentInParent<Canvas>();
            if (canvas == null || binding == null || binding.Button == null)
            {
                return null;
            }

            var ghost = InstantiateItem(view.DragGhostTemplate, canvas.transform, "建设卡拖动虚影");
            ghost.transform.SetAsLastSibling();
            ghost.Root.anchorMin = new Vector2(0.5f, 0.5f);
            ghost.Root.anchorMax = new Vector2(0.5f, 0.5f);
            ghost.Root.pivot = new Vector2(0.5f, 0.5f);
            ghost.Root.sizeDelta = binding.Button.GetComponent<RectTransform>().rect.size;
            ghost.Canvas.overrideSorting = true;
            ghost.Canvas.sortingOrder = Mathf.Max(140, canvas.sortingOrder + 1);
            ghost.RawImage.texture = binding.CardImage == null ? null : binding.CardImage.texture;
            ghost.RawImage.color = ghost.RawImage.texture == null ? ExternalCardBackground : Color.white;
            ghost.RawImage.raycastTarget = false;
            ghost.FallbackText.text = ghost.RawImage.texture == null ? binding.Label : string.Empty;
            ghost.FallbackText.gameObject.SetActive(ghost.RawImage.texture == null);
            ghost.CanvasGroup.alpha = 0.82f;
            ghost.CanvasGroup.interactable = false;
            ghost.CanvasGroup.blocksRaycasts = false;
            return ghost.Root;
        }

        private void MoveExternalFacilityDrag(PointerEventData eventData)
        {
            FacilityCardDragUtility.MoveDragGhost(facilityDragGhost, eventData);
        }

        private void EndExternalFacilityDrag(ExternalCardBinding binding, PointerEventData eventData)
        {
            var facilityId = binding == null ? string.Empty : binding.Id;
            var slotIndex = ResolveDropCityBoardSlotIndex(eventData);
            DestroyFacilityDragGhost();

            if (!string.IsNullOrEmpty(facilityId))
            {
                FacilityDropped?.Invoke(facilityId, slotIndex);
            }
        }

        private int ResolveDropCityBoardSlotIndex(PointerEventData eventData)
        {
            return FacilityCardDragUtility.ResolveCityBoardSlotIndex(eventData);
        }

        private void DestroyFacilityDragGhost()
        {
            FacilityCardDragUtility.DestroyDragGhost(ref facilityDragGhost);
        }

        private void DestroyPendingBuildGhost()
        {
            if (pendingBuildGhost != null)
            {
                var ghostObject = pendingBuildGhost.gameObject;
                pendingBuildGhost = null;
                if (UnityEngine.Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(ghostObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(ghostObject);
                }
            }

            pendingBuildGhostFacilityId = string.Empty;
            pendingBuildGhostSlotIndex = -1;
        }

        private void UpdateCityBoardSlotHighlights()
        {
            for (var i = 0; i < cityBoardSlotBindings.Count; i++)
            {
                var binding = cityBoardSlotBindings[i];
                if (binding == null || binding.Image == null || binding.Outline == null)
                {
                    continue;
                }

                var highlighted = buildInteractionActive &&
                                  binding.IsEmpty &&
                                  legalCityBoardSlotIndexes.Contains(binding.SlotIndex);
                binding.Image.color = highlighted ? LegalCityBoardSlotBackground : binding.DefaultBackground;
                binding.Outline.effectColor = highlighted ? LegalCityBoardSlotOutline : binding.DefaultOutline;
                binding.Outline.effectDistance = highlighted
                    ? new Vector2(2f, -2f)
                    : binding.DefaultOutlineDistance;
            }
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
                var item = InstantiateItem(
                    view.CityStyleCardTemplate,
                    externalCityStyleArea,
                    "城市样式 " + (i + 1));
                SetExternalCardRect(
                    item.Root,
                    i,
                    ExternalCityStyleColumnCount,
                    ExternalCityStyleCardWidth,
                    ExternalCityStyleCardHeight,
                    ExternalCityStyleCardHorizontalSpacing,
                    ExternalCityStyleCardVerticalSpacing,
                    ExternalCityStyleLeftPadding);
                var texture = TryLoadCityStyleCardTexture(cityStyleId);
                item.RawImage.texture = texture;
                item.RawImage.gameObject.SetActive(texture != null);
                item.FallbackText.text = texture == null ? label : string.Empty;
                item.FallbackText.gameObject.SetActive(texture == null);
                SetExternalCardOutline(item.Outline, false);
                cityStyleCardBindings.Add(new ExternalCardBinding
                {
                    Id = cityStyleId,
                    Label = label,
                    Item = item,
                    Button = item.Button,
                    Outline = item.Outline,
                    CardImage = item.RawImage,
                    FallbackText = item.FallbackText
                });
                AddCityStyleInfluenceMarkers(item.MarkerRoot, cityStyleId);
                item.PointerInteraction.ConfigureClick(
                    item.Button,
                    () => CityStyleClicked?.Invoke(cityStyleId),
                    null);
            }
        }

        private void AddCityStyleInfluenceMarkers(RectTransform cardRect, string cityStyleId)
        {
            if (cardRect == null || currentState == null || currentState.Players == null)
            {
                return;
            }

            var markerLayout = new CityStyleMarkerLayoutTracker(cardBoardVisualLayout);
            for (var playerIndex = 0; playerIndex < currentState.Players.Count; playerIndex++)
            {
                var player = currentState.Players[playerIndex];
                if (player == null)
                {
                    continue;
                }

                var formalDeclarationCount = 0;
                if (player.DeclaredCityStyles != null)
                {
                    for (var declarationIndex = 0; declarationIndex < player.DeclaredCityStyles.Count; declarationIndex++)
                    {
                        var declaration = player.DeclaredCityStyles[declarationIndex];
                        if (declaration == null || declaration.CityStyleId != cityStyleId)
                        {
                            continue;
                        }

                        formalDeclarationCount += 1;
                        AddCityStyleInfluenceMarker(
                            cardRect,
                            player,
                            cityStyleId,
                            string.IsNullOrEmpty(declaration.MarkerArea)
                                ? CityStyleMarkerAreas.Declared
                                : declaration.MarkerArea,
                            markerLayout);
                    }
                }

                var declaredIdCount = 0;
                if (player.DeclaredCityStyleIds != null)
                {
                    for (var declarationIndex = 0; declarationIndex < player.DeclaredCityStyleIds.Count; declarationIndex++)
                    {
                        if (player.DeclaredCityStyleIds[declarationIndex] == cityStyleId)
                        {
                            declaredIdCount += 1;
                        }
                    }
                }

                for (var legacyIndex = formalDeclarationCount; legacyIndex < declaredIdCount; legacyIndex++)
                {
                    AddCityStyleInfluenceMarker(
                        cardRect,
                        player,
                        cityStyleId,
                        CityStyleMarkerAreas.Declared,
                        markerLayout);
                }
            }
        }

        private void AddCityStyleInfluenceMarker(
            RectTransform cardRect,
            PlayerState player,
            string cityStyleId,
            string markerArea,
            CityStyleMarkerLayoutTracker markerLayout)
        {
            var placement = markerLayout.Next(cityStyleId, markerArea, player.PlayerId);
            var marker = InstantiateItem(
                view.InfluenceMarkerTemplate,
                cardRect,
                "样式影响力 玩家" + player.PlayerId + " 标记" + (placement.PlayerMarkerIndex + 1));
            CityStyleMarkerRenderer.Configure(
                cardBoardVisualLayout,
                marker.Background,
                marker.gameObject.name,
                UiTheme.GetPlayerColor(player.Color, 1f),
                view.InfluenceMarkerTemplate.Background.sprite,
                cardBoardVisualLayout.BuildInfoMarkerSize,
                cityStyleId,
                placement);
        }

        private void OpenCardImage(string cardName, Texture2D texture)
        {
            CardImagePreviewUtility.Open(
                ref cardImageViewer,
                transform,
                "Card Image Viewer",
                "Card Image",
                cardName,
                texture);
        }

        private IReadOnlyList<string> GetCurrentCityStyleSupplyIds()
        {
            if (currentState != null && currentState.Decks != null &&
                currentState.Decks.CityStyleSupply != null &&
                currentState.Decks.CityStyleSupply.Count > 0)
            {
                return currentState.Decks.CityStyleSupply;
            }

            return CityStyleDatabase.PresentationSupplyIds;
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
            UpdateExternalFacilityAvailability();
            UpdateExternalCardHighlights(cityStyleCardBindings, string.Empty);
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

        private void AddCityBoardSection()
        {
            for (var i = 0; i < cityBoardSlotBindings.Count; i++)
            {
                var binding = cityBoardSlotBindings[i];
                if (binding.Item != null)
                {
                    DestroyDynamicObject(binding.Item.gameObject);
                    binding.Item = null;
                }

                var slotIndex = binding.SlotIndex;
                var isUsedForDeclaration = IsCityBoardSlotUsedForDeclaration(slotIndex);
                var facilityId = GetCityBoardSlotFacilityId(slotIndex);
                var label = GetCityBoardSlotLabel(slotIndex);
                var isEmpty = string.IsNullOrEmpty(facilityId);
                binding.IsEmpty = isEmpty;
                binding.Rect.localEulerAngles = isUsedForDeclaration
                    ? new Vector3(0f, 0f, 180f)
                    : Vector3.zero;
                binding.Image.color = isEmpty ? InvisibleCityBoardSlotColor : OccupiedCityBoardSlotBackground;
                binding.Outline.effectColor = isEmpty ? InvisibleCityBoardSlotColor : UiTheme.GoldOutlineThin;
                binding.Outline.effectDistance = isEmpty ? Vector2.zero : new Vector2(1f, -1f);
                binding.DefaultBackground = binding.Image.color;
                binding.DefaultOutline = binding.Outline.effectColor;
                binding.DefaultOutlineDistance = binding.Outline.effectDistance;
                binding.Slot.Button.enabled = !isEmpty;
                binding.Slot.EmptyLabel.text = string.Empty;
                binding.Slot.EmptyLabel.gameObject.SetActive(false);
                if (isEmpty)
                {
                    binding.Slot.PointerInteraction.ConfigureClick(binding.Slot.Button, null, null);
                    continue;
                }

                binding.Item = InstantiateItem(view.CardContentTemplate, binding.Slot.ContentRoot, "设施卡内容");
                var texture = TryLoadFacilityCardTexture(facilityId);
                binding.Item.RawImage.gameObject.name = "设施卡图";
                binding.Item.RawImage.rectTransform.anchorMin = Vector2.zero;
                binding.Item.RawImage.rectTransform.anchorMax = Vector2.one;
                binding.Item.RawImage.rectTransform.offsetMin = Vector2.zero;
                binding.Item.RawImage.rectTransform.offsetMax = Vector2.zero;
                binding.Item.RawImage.texture = texture;
                binding.Item.RawImage.gameObject.SetActive(texture != null);
                binding.Item.FallbackText.text = texture == null ? label : string.Empty;
                binding.Item.FallbackText.gameObject.SetActive(texture == null);
                var detailTitle = isUsedForDeclaration ? label + "（已使用）" : label;
                binding.Slot.PointerInteraction.ConfigureClick(
                    binding.Slot.Button,
                    () => OpenCardImage(detailTitle, TryLoadFacilityCardTexture(facilityId)),
                    null);
            }

            UpdateCityBoardSlotHighlights();
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

        internal Texture2D TryLoadFacilityCardTexture(string facilityId)
        {
            return cardVisualCatalog == null ? null : cardVisualCatalog.GetFacility(facilityId);
        }

        private Texture2D TryLoadCityStyleCardTexture(string cityStyleId)
        {
            return cardVisualCatalog == null ? null : cardVisualCatalog.GetCityStyle(cityStyleId);
        }

        private static BuildInfoItemView InstantiateItem(
            BuildInfoItemView template,
            Transform parent,
            string objectName)
        {
            var instance = UnityEngine.Object.Instantiate(template, parent, false);
            instance.gameObject.name = objectName;
            instance.gameObject.SetActive(true);
            return instance;
        }

        private static void DestroyDynamicObject(GameObject target)
        {
            if (target == null) return;
            if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }

        private void RebuildLayout()
        {
            if (contentRoot == null || panelTransform == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRoot);
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelTransform);
        }

    }
}
