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
        private const float PanelWidth = 365f;
        private const float ExternalCardFramePadding = 6f;
        private const float ExternalCardAreaLeft = 72f;
        private const float ExternalCardAreaWidth = 365f;/*区域部分*/
        private const float ExternalFacilityAreaTop = 17f;
        private const float ExternalFacilityAreaHeight = 350f;
        private const float ExternalFacilityCardWidth = FacilityCardDragUtility.CardWidth;
        private const float ExternalFacilityCardHeight = FacilityCardDragUtility.CardHeight;
        private const float ExternalFacilityCardHorizontalSpacing = 25f;
        private const float ExternalFacilityCardVerticalSpacing = 22f;
        private const int ExternalFacilityColumnCount = 3;
        private const int ExternalFacilitySlotCount = 6;/*建设卡部分*/
        private const float ExternalCityStyleLeftPadding = 20f;
        private const float ExternalCityStyleAreaTop = 367f;
        private const float ExternalCityStyleAreaHeight = 330f;
        private const float ExternalCityStyleCardWidth = 143f;
        private const float ExternalCityStyleCardHeight = 91f;
        private const float ExternalCityStyleCardHorizontalSpacing = 40f;
        private const float ExternalCityStyleCardVerticalSpacing = 20f;/*样式卡部分*/
        private const int ExternalCityStyleColumnCount = 2;
        private const float BuildPanelLeft = ExternalCardAreaLeft;
        private const float BuildPanelTop = ExternalCityStyleAreaTop + ExternalCityStyleAreaHeight;
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
        private readonly List<RectTransform> dynamicItems = new List<RectTransform>();
        private readonly List<ExternalCardBinding> facilityCardBindings = new List<ExternalCardBinding>();
        private readonly List<ExternalCardBinding> cityStyleCardBindings = new List<ExternalCardBinding>();
        private readonly List<CityBoardSlotBinding> cityBoardSlotBindings = new List<CityBoardSlotBinding>();
        private readonly HashSet<string> draggableFacilityIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> selectableFacilityEffectIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<int> legalCityBoardSlotIndexes = new HashSet<int>();
        private static Sprite cityStyleInfluenceMarkerSprite;
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

        private sealed class ExternalCardBinding
        {
            public string Id = string.Empty;
            public string Label = string.Empty;
            public Button Button;
            public Outline Outline;
            public RawImage CardImage;
            public Text FallbackText;
        }

        private sealed class CityBoardSlotBinding
        {
            public int SlotIndex;
            public bool IsEmpty;
            public RectTransform Rect;
            public Image Image;
            public Outline Outline;
            public Color DefaultBackground;
            public Color DefaultOutline;
            public Vector2 DefaultOutlineDistance;
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
            var ghostObject = new GameObject(
                "本地建设虚影",
                typeof(RectTransform),
                typeof(RawImage),
                typeof(Button),
                typeof(CanvasGroup));
            ghostObject.transform.SetParent(slot.Rect, false);
            pendingBuildGhost = ghostObject.GetComponent<RectTransform>();
            pendingBuildGhost.anchorMin = new Vector2(0.08f, 0.08f);
            pendingBuildGhost.anchorMax = new Vector2(0.92f, 0.92f);
            pendingBuildGhost.offsetMin = Vector2.zero;
            pendingBuildGhost.offsetMax = Vector2.zero;

            var rawImage = ghostObject.GetComponent<RawImage>();
            rawImage.texture = TryLoadFacilityCardTexture(facilityId);
            rawImage.color = rawImage.texture == null ? ExternalCardBackground : Color.white;
            rawImage.raycastTarget = true;
            var canvasGroup = ghostObject.GetComponent<CanvasGroup>();
            canvasGroup.alpha = 0.78f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;

            var ghostBinding = new ExternalCardBinding
            {
                Id = facilityId,
                Label = FacilityCardDatabase.Get(facilityId)?.Name ?? facilityId,
                Button = ghostObject.GetComponent<Button>(),
                CardImage = rawImage
            };
            ghostObject.AddComponent<CardPointerInteraction>().ConfigureDrag(
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

        private void BuildPanel(Transform parent)
        {
            var panelObject = new GameObject("Build Sidebar Panel", typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);

            panelTransform = panelObject.GetComponent<RectTransform>();
            panelTransform.anchorMin = new Vector2(0f, 0f);
            panelTransform.anchorMax = new Vector2(0f, 1f);
            panelTransform.pivot = new Vector2(0f, 1f);
            panelTransform.sizeDelta = new Vector2(PanelWidth, -BuildPanelTop);
            panelTransform.anchoredPosition = new Vector2(BuildPanelLeft, -BuildPanelTop);

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
            BuildAvailabilityMessage();
        }

        private void BuildAvailabilityMessage()
        {
            buildAvailabilityText = CreateText(
                externalFacilityArea,
                string.Empty,
                15,
                FontStyle.Bold,
                new Color(1f, 0.22f, 0.18f, 1f),
                TextAnchor.MiddleCenter);
            buildAvailabilityText.gameObject.name = "Build Availability Message";
            var rect = buildAvailabilityText.rectTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(-12f, 32f);
            rect.anchoredPosition = new Vector2(0f, 4f);
            buildAvailabilityText.gameObject.SetActive(false);
        }

        private static RectTransform CreateExternalCardArea(
            Transform parent,
            string name,
            float left,
            float top,
            float width,
            float height)
        {
            var area = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            area.SetParent(parent, false);
            area.anchorMin = new Vector2(0f, 1f);
            area.anchorMax = new Vector2(0f, 1f);
            area.pivot = new Vector2(0f, 1f);
            area.sizeDelta = new Vector2(width, height);
            area.anchoredPosition = new Vector2(left, -top);

            var image = area.GetComponent<Image>();
            image.color = ExternalCardAreaBackground;
            image.raycastTarget = false;

            return area;
        }

        private void BuildContentArea(RectTransform parent)
        {
            contentArea = new GameObject("Content Area", typeof(RectTransform)).GetComponent<RectTransform>();
            contentArea.SetParent(parent, false);
            contentArea.anchorMin = Vector2.zero;
            contentArea.anchorMax = Vector2.one;
            contentArea.offsetMin = Vector2.zero;
            contentArea.offsetMax = Vector2.zero;

            contentRoot = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            contentRoot.SetParent(contentArea, false);
            contentRoot.anchorMin = Vector2.zero;
            contentRoot.anchorMax = Vector2.one;
            contentRoot.offsetMin = Vector2.zero;
            contentRoot.offsetMax = Vector2.zero;

        }

        private void RebuildContent()
        {
            Canvas.ForceUpdateCanvases();
            panelTransform.ForceUpdateRectTransforms();
            contentArea.ForceUpdateRectTransforms();
            contentRoot.ForceUpdateRectTransforms();
            ClearDynamicItems();
            ClearExternalCityStyleCards();
            AddCityBoardSection();
            RebuildExternalFacilityCards();
            RebuildExternalCityStyleCards();
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
                var interaction = binding.Button.gameObject.AddComponent<CardPointerInteraction>();
                interaction.ConfigureClick(
                    binding.Button,
                    () => HandleExternalFacilityClick(binding),
                    null);
                interaction.ConfigureDrag(
                    () => CanDragExternalFacility(binding),
                    eventData => BeginExternalFacilityDrag(binding, eventData),
                    MoveExternalFacilityDrag,
                    eventData => EndExternalFacilityDrag(binding, eventData),
                    DestroyFacilityDragGhost);
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
            imageRect.offsetMin = new Vector2(
                FacilityCardDragUtility.CardImageInset,
                FacilityCardDragUtility.CardImageInset);
            imageRect.offsetMax = new Vector2(
                -FacilityCardDragUtility.CardImageInset,
                -FacilityCardDragUtility.CardImageInset);
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

            return FacilityCardDragUtility.CreateDragGhost(
                canvas.transform as RectTransform,
                binding.Button.GetComponent<RectTransform>(),
                binding.CardImage == null ? null : binding.CardImage.texture,
                binding.Label);
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
                    false,
                    cityStyleCardBindings,
                    cityStyleId);

                AddCityStyleInfluenceMarkers(button.GetComponent<RectTransform>(), cityStyleId);

                button.gameObject.AddComponent<CardPointerInteraction>().ConfigureClick(
                    button,
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

            if (cityStyleInfluenceMarkerSprite == null)
            {
                cityStyleInfluenceMarkerSprite = UguiUtility.CreateFilledSquareSprite(32, 24f);
            }

            var markerLayout = new CityStyleMarkerLayoutTracker();
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

        private static void AddCityStyleInfluenceMarker(
            RectTransform cardRect,
            PlayerState player,
            string cityStyleId,
            string markerArea,
            CityStyleMarkerLayoutTracker markerLayout)
        {
            var placement = markerLayout.Next(cityStyleId, markerArea, player.PlayerId);
            var markerObject = new GameObject(
                "样式影响力 玩家" + player.PlayerId + " 标记" + (placement.PlayerMarkerIndex + 1),
                typeof(RectTransform),
                typeof(Image),
                typeof(Outline));
            markerObject.transform.SetParent(cardRect, false);
            var image = markerObject.GetComponent<Image>();
            CityStyleMarkerRenderer.Configure(
                image,
                markerObject.name,
                UiTheme.GetPlayerColor(player.Color, 1f),
                cityStyleInfluenceMarkerSprite,
                new Vector2(12f, 12f),
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
            cityBoardSlotBindings.Clear();
            var boardImage = TryLoadCityBoardTexture();
            var board = new GameObject("City Board", typeof(RectTransform)).GetComponent<RectTransform>();
            board.SetParent(contentRoot, false);
            board.anchorMin = Vector2.zero;
            board.anchorMax = Vector2.one;
            board.offsetMin = Vector2.zero;
            board.offsetMax = Vector2.zero;
            dynamicItems.Add(board);

            RectTransform slotRoot = board;
            if (boardImage != null)
            {
                var imageObject = new GameObject("城市面板底图", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
                imageObject.transform.SetParent(board, false);
                var rect = imageObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;

                var aspectRatio = imageObject.GetComponent<AspectRatioFitter>();
                aspectRatio.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                aspectRatio.aspectRatio = (float)boardImage.width / boardImage.height;

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
                var button = CreateCityBoardSlotButton(slotRoot, slotIndex, facilityId, label, isEmpty, isUsedForDeclaration);
                var rect = button.GetComponent<RectTransform>();

                var image = button.GetComponent<Image>();
                var outline = button.GetComponent<Outline>();
                cityBoardSlotBindings.Add(new CityBoardSlotBinding
                {
                    SlotIndex = slotIndex,
                    IsEmpty = isEmpty,
                    Rect = rect,
                    Image = image,
                    Outline = outline,
                    DefaultBackground = image.color,
                    DefaultOutline = outline.effectColor,
                    DefaultOutlineDistance = outline.effectDistance
                });

                if (!isEmpty)
                {
                    var detailTitle = isUsedForDeclaration ? label + "（已使用）" : label;
                    button.gameObject.AddComponent<CardPointerInteraction>().ConfigureClick(
                        button,
                        () => OpenCardImage(detailTitle, TryLoadFacilityCardTexture(facilityId)),
                        null);
                }
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

        private static Button CreateCityBoardSlotButton(
            RectTransform parent,
            int slotIndex,
            string facilityId,
            string label,
            bool isEmpty,
            bool isUsedForDeclaration)
        {
            var buttonObject = new GameObject("槽位 " + (slotIndex + 1), typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.AddComponent<CityBoardSlotDropTarget>().Configure(slotIndex);

            var rect = buttonObject.GetComponent<RectTransform>();
            CityBoardSlotLayout.Apply(rect, slotIndex);

            var image = buttonObject.GetComponent<Image>();
            image.color = isEmpty
                ? InvisibleCityBoardSlotColor
                : OccupiedCityBoardSlotBackground;

            var outline = buttonObject.GetComponent<Outline>();
            outline.effectColor = isEmpty
                ? InvisibleCityBoardSlotColor
                : UiTheme.GoldOutlineThin;
            outline.effectDistance = isEmpty ? Vector2.zero : new Vector2(1f, -1f);

            rect.localEulerAngles = isUsedForDeclaration
                ? new Vector3(0f, 0f, 180f)
                : Vector3.zero;

            if (!isEmpty)
            {
                if (!AddFacilityCardImage(rect, facilityId))
                {
                    var fallbackText = CreateText(rect, label, 14, FontStyle.Bold, UiTheme.ValueText, TextAnchor.MiddleCenter);
                    fallbackText.resizeTextForBestFit = true;
                    fallbackText.resizeTextMinSize = 10;
                    fallbackText.resizeTextMaxSize = 14;
                    fallbackText.horizontalOverflow = HorizontalWrapMode.Overflow;
                    fallbackText.raycastTarget = false;
                }
            }

            var button = buttonObject.GetComponent<Button>();
            button.enabled = !isEmpty;
            return button;
        }

        private static bool AddFacilityCardImage(RectTransform parent, string facilityId)
        {
            var texture = TryLoadFacilityCardTexture(facilityId);
            if (texture == null)
            {
                return false;
            }

            var imageObject = new GameObject("设施卡图", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(parent, false);
            var rect = imageObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var rawImage = imageObject.GetComponent<RawImage>();
            rawImage.texture = texture;
            rawImage.color = Color.white;
            rawImage.raycastTarget = false;
            return true;
        }

        internal static Texture2D TryLoadFacilityCardTexture(string facilityId)
        {
            return CardTextureCatalog.LoadFacility(facilityId);
        }

        private static Texture2D TryLoadCityStyleCardTexture(string cityStyleId)
        {
            return CardTextureCatalog.LoadCityStyle(cityStyleId);
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

        private static Texture2D TryLoadCityBoardTexture()
        {
            return CardTextureCatalog.LoadCityBoard();
        }
    }
}
