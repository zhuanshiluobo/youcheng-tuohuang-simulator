using System;
using System.Collections.Generic;
using System.Linq;
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
        private const int ExternalFacilitySlotCount = 6;/*建设卡部分*/
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
        private readonly List<CityStyleRowBinding> cityStyleRows = new List<CityStyleRowBinding>();
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
        private int displayedCityPlayerId = -1;
        private GameStateView cityVisibleView;
        public bool IsOwnCityDisplayed => displayedCityPlayerId < 0 || displayedCityPlayerId == currentPlayerId;
        private int CityDisplayPlayerId => displayedCityPlayerId < 0 ? currentPlayerId : displayedCityPlayerId;

        public void RefreshViewedCity(GameStateView visible, int playerId)
        {
            if (!initialized) return;
            var valid = visible != null && visible.ViewerPlayerId == currentPlayerId && visible.GameId == currentState?.GameId &&
                visible.Players.Exists(p => p.PlayerId == playerId);
            var target = valid ? playerId : currentPlayerId;
            if (target != CityDisplayPlayerId)
            {
                facilityDragActive = false;
                DestroyFacilityDragGhost();
            }
            displayedCityPlayerId = target;
            cityVisibleView = valid ? visible : null;
            RefreshCityBoard();
        }

        private void RefreshCityBoard()
        {
            var facilities = cityVisibleView != null
                ? cityVisibleView.Map.Facilities.Select(p => new FacilityPlacement { PlayerId = p.PlayerId,
                    ContentInstanceId = p.ContentInstanceId, FacilityCardId = p.FacilityCardId,
                    CityBoardSlotIndex = p.CityBoardSlotIndex, LocationId = p.LocationId }).ToArray()
                : currentState == null ? Array.Empty<FacilityPlacement>() : currentState.Map.Facilities.ToArray();
            if (AddCityBoardSection(facilities)) MarkContentLayoutDirty(view.CityBoardRoot);
            if (pendingBuildGhost != null) pendingBuildGhost.gameObject.SetActive(IsOwnCityDisplayed);
            foreach (var slot in cityBoardSlotBindings) slot.Slot.DropTarget.enabled = IsOwnCityDisplayed;
        }
        private string selectedFacilityId = string.Empty;
        private CardViewer cardImageViewer;
        private bool buildInteractionActive;
        private RectTransform facilityDragGhost;
        private RectTransform pendingBuildGhost;
        private string pendingBuildGhostFacilityId = string.Empty;
        private int pendingBuildGhostSlotIndex = -1;
        private Text buildAvailabilityText;
        private bool facilityEffectSelectionActive;
        private Action<string> selectFacilityForEffect;
        private Action cancelFacilityEffectSelection;

        public event Action<string> FacilityDragStarted;
        public event Action<string, int> FacilityDropped;
        public event Action FacilityDragCanceled;
        private readonly Vector3[] dragArtworkCorners = new Vector3[4];
        private Vector2Int dragScreenSize;
        private bool facilityDragActive;

        public bool IsFacilityEffectSelectionActive => facilityEffectSelectionActive;
        public BuildInfoPanelView View => view;
        public CardBoardVisualLayout CardBoardVisualLayout => cardBoardVisualLayout;

        public void SetExternalFacilitySupplyVisible(bool visible) =>
            view?.SetExternalFacilitySupplyVisible(visible);

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
            public string ItemId, ZoneId;
            public string RenderedItemId, RenderedZoneId;
            public BuildInfoItemView Template;
            public string Id = string.Empty;
            public string Label = string.Empty;
            public BuildInfoSlotView Slot;
            public BuildInfoItemView Item;
            public Button Button;
            public Outline Outline;
            public RawImage CardImage;
            public Sprite CardSprite;
            public Text FallbackText;
        }

        private sealed class CityStyleRowBinding
        {
            public string Id;
            public CityStyleStatusRowView Template;
            public CityStyleStatusRowView Row;
        }

        private sealed class CityBoardSlotBinding
        {
            public int SlotIndex;
            public bool IsEmpty;
            public string GameId, ContentInstanceId, FacilityId, DefinitionId, Label, DetailTitle;
            public int PlayerId;
            public BuildInfoItemView Template;
            public Sprite CardSprite;
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
                slot.PointerInteraction.ConfigureClick(slot.Button, () => HandleExternalFacilityClick(binding));
                slot.PointerInteraction.ConfigureDrag(
                    () => CanDragExternalFacility(binding),
                    eventData => BeginExternalFacilityDrag(binding, eventData),
                    MoveExternalFacilityDrag,
                    eventData => EndExternalFacilityDrag(binding, eventData),
                    CancelFacilityDrag);
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
            if (currentPlayerId != playerId || currentState?.GameId != state?.GameId)
            { displayedCityPlayerId = playerId; cityVisibleView = null; }
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
            foreach (var binding in facilityCardBindings)
                if (!string.IsNullOrEmpty(binding.ItemId)) currentSupply.Add(binding.ItemId);
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
            if (!facilityEffectSelectionActive)
            {
                return;
            }

            facilityEffectSelectionActive = false;
            selectableFacilityEffectIds.Clear();
            selectFacilityForEffect = null;
            cancelFacilityEffectSelection = null;
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
            pendingBuildGhost.gameObject.SetActive(IsOwnCityDisplayed);
            pendingBuildGhost.anchorMin = view.CardInteractionLayoutProfile.PendingBuildGhostAnchorMin;
            pendingBuildGhost.anchorMax = view.CardInteractionLayoutProfile.PendingBuildGhostAnchorMax;
            pendingBuildGhost.offsetMin = Vector2.zero;
            pendingBuildGhost.offsetMax = Vector2.zero;

            var rawImage = ghost.RawImage;
            var sprite = TryLoadFacilityCardSprite(facilityId);
            CardArtworkView.Set(rawImage, sprite);
            rawImage.color = sprite == null ? ExternalCardBackground : Color.white;
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
                CardImage = rawImage,
                CardSprite = sprite
            };
            ghost.PointerInteraction.ConfigureDrag(
                () => buildInteractionActive && IsOwnCityDisplayed && !CityBoardInspectionInput.ReturnedThisFrame,
                eventData =>
                {
                    DestroyFacilityDragGhost();
                    CaptureDragGeometry();
                    facilityDragGhost = CreateFacilityDragGhost(ghostBinding);
                    MoveExternalFacilityDrag(eventData);
                    beginDrag?.Invoke();
                },
                MoveExternalFacilityDrag,
                eventData =>
                {
                    var wasDragging = facilityDragActive;
                    var targetSlotIndex = ResolveDropCityBoardSlotIndex(eventData);
                    facilityDragActive = false;
                    DestroyFacilityDragGhost();
                    if (wasDragging && IsOwnCityDisplayed && !CityBoardInspectionInput.ReturnedThisFrame) drop?.Invoke(targetSlotIndex);
                },
                CancelFacilityDrag);

        }

        private void RebuildContent()
        {
            RefreshCityBoard();
            if (RebuildExternalFacilityCards())
                MarkContentLayoutDirty(externalFacilityArea);
            if (RefreshCityStyleRows())
                MarkContentLayoutDirty(externalCityStyleArea);
            // 状态文本由现有 Text 的脏标记传播；普通刷新不强制同步整棵 Canvas。
        }

        private bool RebuildExternalFacilityCards()
        {
            if (externalFacilityArea == null)
            {
                return false;
            }

            var structureChanged = false;
            var supply = currentState == null
                ? Array.Empty<string>()
                : currentState.Decks.FacilitySupply.ToArray();
            var count = Mathf.Min(ExternalFacilitySlotCount, supply.Length);
            for (var i = 0; i < facilityCardBindings.Count; i++)
            {
                var binding = facilityCardBindings[i];
                if (i >= count)
                {
                    binding.ZoneId = string.Empty;
                    binding.ItemId = string.Empty;
                    structureChanged |= BindExternalFacilitySlot(binding, string.Empty, string.Empty, null);
                    continue;
                }

                var facilityId = supply[i];
                var zone = currentState.ItemZones?.Zones.Find(value => value.LocalZoneId == "facility_supply_" + i);
                var item = currentState.ItemZones?.Items.Find(value => value.ZoneId == zone?.ZoneId);
                binding.ZoneId = zone?.ZoneId ?? string.Empty;
                binding.ItemId = item?.ItemId ?? string.Empty;
                var facility = FacilityCardDatabase.Get(currentState, facilityId);
                var label = facility == null ? facilityId : facility.Name;
                structureChanged |= BindExternalFacilitySlot(binding, facilityId, label, TryLoadFacilityCardSprite(facilityId));
            }
            return structureChanged;
        }

        private bool BindExternalFacilitySlot(
            ExternalCardBinding binding,
            string facilityId,
            string label,
            Sprite sprite)
        {
            var empty = string.IsNullOrEmpty(facilityId);
            var identityChanged = binding.Id != facilityId || binding.RenderedItemId != binding.ItemId ||
                                  binding.RenderedZoneId != binding.ZoneId || binding.Template != view.CardContentTemplate;
            var presentationChanged = identityChanged || binding.Label != label || binding.CardSprite != sprite;
            var structureChanged = false;
            if (binding.Item != null && (identityChanged || empty))
            {
                DestroyDynamicObject(binding.Item.gameObject);
                binding.Item = null;
                structureChanged = true;
            }

            binding.Id = facilityId;
            binding.RenderedItemId = binding.ItemId;
            binding.RenderedZoneId = binding.ZoneId;
            binding.Template = view.CardContentTemplate;
            binding.Label = label;
            binding.Button.interactable = !empty;
            binding.Slot.EmptyLabel.gameObject.SetActive(empty);
            binding.CardImage = null;
            binding.CardSprite = sprite;
            binding.FallbackText = binding.Slot.EmptyLabel;
            if (!empty)
            {
                if (binding.Item == null)
                {
                    binding.Item = InstantiateItem(view.CardContentTemplate, binding.Slot.ContentRoot, "Card Content");
                    structureChanged = true;
                    presentationChanged = true;
                }
                binding.CardImage = binding.Item.RawImage;
                binding.FallbackText = binding.Item.FallbackText;
                if (presentationChanged)
                {
                    CardArtworkView.Set(binding.CardImage, sprite);
                    binding.CardImage.gameObject.SetActive(sprite != null);
                    binding.FallbackText.text = sprite == null ? label : string.Empty;
                    binding.FallbackText.gameObject.SetActive(sprite == null);
                }
            }

            UpdateExternalFacilityAvailability(binding);
            return structureChanged;
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
                    binding.CardSprite);
                return;
            }

            var facilityId = selectableFacilityEffectIds.Contains(binding.ItemId) ? binding.ItemId : binding.Id;
            if (!selectableFacilityEffectIds.Contains(facilityId))
            {
                return;
            }

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
                                      (selectableFacilityEffectIds.Contains(binding.ItemId) || selectableFacilityEffectIds.Contains(binding.Id));
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
                    SetOutline(
                        binding.Outline,
                        selectableForEffect ? FacilityEffectSelectableOutline : ExternalCardNormalOutline,
                        selectableForEffect
                            ? view.CardInteractionLayoutProfile.FacilitySelectableOutlineDistance
                            : view.CardInteractionLayoutProfile.NormalOutlineDistance);
                }
                else
                {
                    SetExternalCardOutline(
                        binding.Outline,
                        !string.IsNullOrEmpty(binding.Id) && ExternalFacilityBuildId(binding) == selectedFacilityId);
                }
            }
        }

        private bool CanDragExternalFacility(ExternalCardBinding binding)
        {
            if (!IsOwnCityDisplayed || CityBoardInspectionInput.ReturnedThisFrame) return false;
            return !facilityEffectSelectionActive &&
                   buildInteractionActive &&
                   binding != null &&
                   !string.IsNullOrEmpty(binding.Id) &&
                   draggableFacilityIds.Contains(ExternalFacilityBuildId(binding));
        }

        private static string ExternalFacilityBuildId(ExternalCardBinding binding) =>
            binding == null ? string.Empty : string.IsNullOrEmpty(binding.ItemId) ? binding.Id : binding.ItemId;

        private void BeginExternalFacilityDrag(ExternalCardBinding binding, PointerEventData eventData)
        {
            if (!CanDragExternalFacility(binding))
            {
                return;
            }

            DestroyFacilityDragGhost();
            CaptureDragGeometry();
            facilityDragGhost = CreateFacilityDragGhost(binding);
            MoveExternalFacilityDrag(eventData);
            FacilityDragStarted?.Invoke(ExternalFacilityBuildId(binding));
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
            view.CardInteractionLayoutProfile.DragGhostLayout.RootLayout.ApplyTo(ghost.Root);
            ghost.Root.sizeDelta = binding.Button.GetComponent<RectTransform>().rect.size;
            ghost.Canvas.overrideSorting = true;
            ghost.Canvas.sortingOrder = GameplayUiLayers.ContentDrag;
            CardArtworkView.Set(ghost.RawImage, binding.CardSprite);
            ghost.RawImage.color = binding.CardSprite == null ? ExternalCardBackground : Color.white;
            ghost.RawImage.raycastTarget = false;
            ghost.FallbackText.text = binding.CardSprite == null ? binding.Label : string.Empty;
            ghost.FallbackText.gameObject.SetActive(binding.CardSprite == null);
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
            var wasDragging = facilityDragActive;
            var facilityId = ExternalFacilityBuildId(binding);
            var slotIndex = ResolveDropCityBoardSlotIndex(eventData);
            facilityDragActive = false;
            DestroyFacilityDragGhost();

            if (wasDragging && !string.IsNullOrEmpty(facilityId) && IsOwnCityDisplayed && !CityBoardInspectionInput.ReturnedThisFrame)
            {
                FacilityDropped?.Invoke(facilityId, slotIndex);
            }
        }

        private int ResolveDropCityBoardSlotIndex(PointerEventData eventData)
        {
            if (!facilityDragActive || !IsOwnCityDisplayed || CityBoardInspectionInput.ReturnedThisFrame) return -1;
            RebuildLayout();
            if (!DragGeometryUnchanged()) return -1;
            return FacilityCardDragUtility.ResolveCityBoardSlotIndex(
                eventData, view == null || view.CityBoardImage == null
                    ? null : view.CityBoardImage.rectTransform);
        }

        private void CaptureDragGeometry()
        {
            RebuildLayout();
            facilityDragActive = true;
            dragScreenSize = new Vector2Int(Screen.width, Screen.height);
            if (view != null && view.CityBoardImage != null)
                view.CityBoardImage.rectTransform.GetWorldCorners(dragArtworkCorners);
        }

        private bool DragGeometryUnchanged()
        {
            if (dragScreenSize.x != Screen.width || dragScreenSize.y != Screen.height ||
                view == null || view.CityBoardImage == null) return false;
            var corners = new Vector3[4];
            view.CityBoardImage.rectTransform.GetWorldCorners(corners);
            for (var i = 0; i < corners.Length; i++)
                if ((corners[i] - dragArtworkCorners[i]).sqrMagnitude > 0.0001f) return false;
            return true;
        }

        private void CancelFacilityDrag()
        {
            if (!facilityDragActive) return;
            facilityDragActive = false;
            DestroyFacilityDragGhost();
            FacilityDragCanceled?.Invoke();
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

                var highlighted = IsOwnCityDisplayed && buildInteractionActive &&
                                  binding.IsEmpty &&
                                  legalCityBoardSlotIndexes.Contains(binding.SlotIndex);
                binding.Image.color = highlighted ? LegalCityBoardSlotBackground : binding.DefaultBackground;
                SetOutline(
                    binding.Outline,
                    highlighted ? LegalCityBoardSlotOutline : binding.DefaultOutline,
                    highlighted
                        ? view.CardInteractionLayoutProfile.LegalCityBoardSlotOutlineDistance
                        : binding.DefaultOutlineDistance);
            }
        }

        private bool RefreshCityStyleRows()
        {
            if (externalCityStyleArea == null) return false;
            var ids = GetCurrentCityStyleSupplyIds();
            var rows = new List<KeyValuePair<string, CityStyleDefinition>>(ids.Count);
            foreach (var id in ids)
            {
                var definition = CityStyleDatabase.Get(currentState, id);
                if (definition == null || string.IsNullOrWhiteSpace(definition.Name))
                    throw new InvalidOperationException("本局城市样式定义或名称缺失：" + id);
                rows.Add(new KeyValuePair<string, CityStyleDefinition>(id, definition));
            }

            var remaining = new List<CityStyleRowBinding>(cityStyleRows);
            var next = new List<CityStyleRowBinding>(rows.Count);
            var structureChanged = false;
            // 顺序仍是一级在前、二级在后；组内保持本局供应顺序。
            for (int group = 0; group < 2; group++)
            foreach (var entry in rows)
            {
                var id = entry.Key;
                var definition = entry.Value;
                if ((definition.Level == 2 ? 1 : 0) != group) continue;
                var layout = cardBoardVisualLayout.CityStyleVisuals.Find(id)?.trackLayout;
                var template = view.GetCityStyleRowTemplate(layout);
                if (template == null)
                    throw new InvalidOperationException("城市样式展示模板缺失：" + id + " / " + layout);

                CityStyleRowBinding binding = null;
                for (var i = 0; i < remaining.Count; i++)
                {
                    var existing = remaining[i];
                    if (existing.Id != id || existing.Template != template || existing.Row == null) continue;
                    binding = existing;
                    remaining.RemoveAt(i);
                    break;
                }
                if (binding == null)
                {
                    binding = new CityStyleRowBinding
                    {
                        Id = id,
                        Template = template,
                        Row = Instantiate(template, externalCityStyleArea, false)
                    };
                    binding.Row.name = id;
                    structureChanged = true;
                }
                if (binding.Row.transform.parent != externalCityStyleArea)
                {
                    binding.Row.transform.SetParent(externalCityStyleArea, false);
                    structureChanged = true;
                }
                if (!binding.Row.gameObject.activeSelf)
                {
                    binding.Row.gameObject.SetActive(true);
                    structureChanged = true;
                }
                // 对象复用不跳过数据渲染，包括同 revision 下的标记和玩家状态变化。
                binding.Row.Render(id, definition.Name, currentState);
                next.Add(binding);
            }

            foreach (var retired in remaining)
            {
                if (retired.Row != null) DestroyDynamicObject(retired.Row.gameObject);
                structureChanged = true;
            }
            var orderChanged = next.Count != cityStyleRows.Count;
            for (var i = 0; !orderChanged && i < next.Count; i++)
                orderChanged = next[i] != cityStyleRows[i];
            if (structureChanged || orderChanged)
            {
                // 只移动本组件拥有的动态行；现有模板/静态子对象保留原相对顺序。
                foreach (var binding in next) binding.Row.transform.SetAsLastSibling();
                structureChanged = true;
            }
            cityStyleRows.Clear();
            cityStyleRows.AddRange(next);
            return structureChanged;
        }

        private void OpenCardImage(string cardName, Sprite sprite)
        {
            CardImagePreviewUtility.Open(
                ref cardImageViewer,
                transform,
                sprite);
        }

        private IReadOnlyList<string> GetCurrentCityStyleSupplyIds()
        {
            if (currentState != null && currentState.Decks != null &&
                currentState.Decks.CityStyleSupply != null)
            {
                return currentState.Decks.CityStyleSupply;
            }

            return CityStyleDatabase.PresentationSupplyIds;
        }

        private void SetExternalCardOutline(Outline outline, bool selected)
        {
            SetOutline(
                outline,
                selected ? ExternalCardSelectedOutline : ExternalCardNormalOutline,
                selected
                    ? view.CardInteractionLayoutProfile.FacilitySelectableOutlineDistance
                    : view.CardInteractionLayoutProfile.NormalOutlineDistance);
        }

        private static void SetOutline(Outline outline, Color color, Vector2 distance)
        {
            if (outline.effectColor != color)
            {
                outline.effectColor = color;
            }

            if (outline.effectDistance != distance)
            {
                outline.effectDistance = distance;
            }
        }

        private bool AddCityBoardSection(IReadOnlyList<FacilityPlacement> facilities)
        {
            var structureChanged = false;
            for (var i = 0; i < cityBoardSlotBindings.Count; i++)
            {
                var binding = cityBoardSlotBindings[i];
                var slotIndex = binding.SlotIndex;
                FacilityPlacement placement = null;
                for (var j = 0; j < facilities.Count; j++)
                    if (facilities[j].PlayerId == CityDisplayPlayerId && facilities[j].CityBoardSlotIndex == slotIndex)
                    { placement = facilities[j]; break; }
                var facilityId = placement?.FacilityCardId ?? string.Empty;
                var instanceId = placement?.ContentInstanceId ?? string.Empty;
                var isEmpty = string.IsNullOrEmpty(facilityId);
                var definition = isEmpty ? null : FacilityCardDatabase.Get(currentState, facilityId);
                var definitionId = definition?.FacilityId ?? facilityId;
                var label = isEmpty ? string.Empty : definition?.Name ?? facilityId;
                var sprite = isEmpty ? null : TryLoadFacilityCardSprite(definitionId);
                var isUsedForDeclaration = IsCityBoardSlotUsedForDeclaration(slotIndex);
                var detailTitle = isUsedForDeclaration ? label + "（已使用）" : label;
                var gameId = currentState?.GameId ?? string.Empty;
                var identityChanged = binding.GameId != gameId || binding.PlayerId != CityDisplayPlayerId ||
                    binding.ContentInstanceId != instanceId || binding.FacilityId != facilityId ||
                    binding.DefinitionId != definitionId || binding.Template != view.CardContentTemplate;
                var presentationChanged = identityChanged || binding.Label != label || binding.CardSprite != sprite;
                var clickChanged = identityChanged || binding.DetailTitle != detailTitle || binding.IsEmpty != isEmpty;
                if (binding.Item != null && (identityChanged || isEmpty))
                {
                    DestroyDynamicObject(binding.Item.gameObject);
                    binding.Item = null;
                    structureChanged = true;
                }

                binding.GameId = gameId;
                binding.PlayerId = CityDisplayPlayerId;
                binding.ContentInstanceId = instanceId;
                binding.FacilityId = facilityId;
                binding.DefinitionId = definitionId;
                binding.Template = view.CardContentTemplate;
                binding.Label = label;
                binding.DetailTitle = detailTitle;
                binding.CardSprite = sprite;
                binding.IsEmpty = isEmpty;
                binding.Rect.localEulerAngles = isUsedForDeclaration ? new Vector3(0f, 0f, 180f) : Vector3.zero;
                binding.Image.color = isEmpty ? InvisibleCityBoardSlotColor : OccupiedCityBoardSlotBackground;
                binding.Outline.effectColor = isEmpty ? InvisibleCityBoardSlotColor : UiTheme.GoldOutlineThin;
                binding.Outline.effectDistance = isEmpty ? Vector2.zero : view.CardInteractionLayoutProfile.NormalOutlineDistance;
                binding.DefaultBackground = binding.Image.color;
                binding.DefaultOutline = binding.Outline.effectColor;
                binding.DefaultOutlineDistance = binding.Outline.effectDistance;
                binding.Slot.Button.enabled = !isEmpty;
                binding.Slot.EmptyLabel.text = string.Empty;
                binding.Slot.EmptyLabel.gameObject.SetActive(false);
                if (isEmpty)
                {
                    if (clickChanged) binding.Slot.PointerInteraction.ConfigureClick(binding.Slot.Button, null);
                    continue;
                }

                if (binding.Item == null)
                {
                    binding.Item = InstantiateItem(view.CardContentTemplate, binding.Slot.ContentRoot, "设施卡内容");
                    binding.Item.RawImage.gameObject.name = "设施卡图";
                    binding.Item.RawImage.rectTransform.anchorMin = Vector2.zero;
                    binding.Item.RawImage.rectTransform.anchorMax = Vector2.one;
                    binding.Item.RawImage.rectTransform.offsetMin = Vector2.zero;
                    binding.Item.RawImage.rectTransform.offsetMax = Vector2.zero;
                    structureChanged = true;
                    presentationChanged = true;
                    clickChanged = true;
                }
                if (presentationChanged)
                {
                    CardArtworkView.Set(binding.Item.RawImage, sprite);
                    binding.Item.RawImage.gameObject.SetActive(sprite != null);
                    binding.Item.FallbackText.text = sprite == null ? label : string.Empty;
                    binding.Item.FallbackText.gameObject.SetActive(sprite == null);
                }
                if (clickChanged)
                    binding.Slot.PointerInteraction.ConfigureClick(binding.Slot.Button,
                        () => { if (IsOwnCityDisplayed && !CityBoardInspectionInput.ReturnedThisFrame)
                            OpenCardImage(binding.DetailTitle, binding.CardSprite); });
            }

            UpdateCityBoardSlotHighlights();
            return structureChanged;
        }

        private string GetCityBoardSlotFacilityId(int slotIndex)
        {
            return currentState == null
                ? string.Empty
                : GetCityBoardSlotFacilityId(slotIndex, currentState.Map.Facilities);
        }

        private string GetCityBoardSlotFacilityId(int slotIndex, IReadOnlyList<FacilityPlacement> facilities)
        {
            for (var i = 0; i < facilities.Count; i++)
            {
                var placement = facilities[i];
                if (placement.PlayerId == currentPlayerId && placement.CityBoardSlotIndex == slotIndex)
                {
                    return placement.FacilityCardId;
                }
            }

            return string.Empty;
        }

        private string GetCityBoardSlotLabel(int slotIndex)
        {
            return GetCityBoardSlotLabel(slotIndex, GetCityBoardSlotFacilityId(slotIndex));
        }

        private static string GetCityBoardSlotLabel(int slotIndex, string facilityId)
        {
            if (!string.IsNullOrEmpty(facilityId))
            {
                var facility = FacilityCardDatabase.Get(facilityId);
                return facility == null ? facilityId : facility.Name;
            }

            return "空位 " + (slotIndex + 1);
        }

        private bool IsCityBoardSlotUsedForDeclaration(int slotIndex)
        {
            if (cityVisibleView != null)
                return cityVisibleView.Players.Find(p => p.PlayerId == CityDisplayPlayerId)?.CityStyleUsedSlotIndexes.Contains(slotIndex) == true;
            if (!IsOwnCityDisplayed) return false;
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

        internal Sprite TryLoadFacilityCardSprite(string facilityId)
        {
            return cardVisualCatalog == null ? null : cardVisualCatalog.GetFacility(facilityId);
        }

        private Sprite TryLoadCityStyleCardSprite(string cityStyleId)
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
            // PlayMode 的销毁到帧末才生效，立即隐藏退休内容避免旧行仍参与布局和输入。
            target.SetActive(false);
            if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }

        private static void MarkContentLayoutDirty(RectTransform root)
        {
            if (root != null) LayoutRebuilder.MarkLayoutForRebuild(root);
        }

        // 仅在拖拽开始/落点判定将立即读取世界几何时调用。
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
