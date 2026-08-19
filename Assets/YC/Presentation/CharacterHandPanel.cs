using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    public sealed class CharacterHandPanel : MonoBehaviour
    {
        private sealed class HandCardEntry
        {
            public CharacterCardHandItemViewModel Model;
            public CharacterHandCardView View;
        }

        [SerializeField] private CharacterHandPanelView view;
        [SerializeField] private CharacterHandLayoutProfile layoutProfile;

        private readonly Dictionary<int, List<string>> preferredOrders =
            new Dictionary<int, List<string>>();
        private readonly List<HandCardEntry> handEntries = new List<HandCardEntry>();
        private readonly List<CharacterCardHandItemViewModel> orderedHand =
            new List<CharacterCardHandItemViewModel>();
        private readonly List<CharacterCardHandItemViewModel> orderedDiscard =
            new List<CharacterCardHandItemViewModel>();

        private CardVisualCatalog cardVisualCatalog;
        private Action<string, Vector2> beginCoverDrag;
        private Action<Vector2> updateCoverDrag;
        private Action<string, Vector2> endCoverDrag;
        private CharacterCardPanelViewModel currentViewModel;
        private ZoomableImageViewerController cardImageViewer;
        private RectTransform dragGhost;
        private string hoveredCardId = string.Empty;
        private string draggedCardId = string.Empty;
        private int currentPlayerId;
        private List<string> dragInitialPreferredOrder;
        private bool initialized;

        public CharacterHandPanelView View => view;
        public CharacterHandLayoutProfile LayoutProfile => layoutProfile;
        public bool IsDiscardPreviewOpen =>
            initialized && view.DiscardOverlayObject.activeSelf;
        public IReadOnlyList<CharacterCardHandItemViewModel> OrderedHand => orderedHand;

        private void Awake()
        {
            if (!Bind(view))
            {
                enabled = false;
            }
        }

        private void OnDisable()
        {
            CancelActiveDrag();
        }

        private void OnDestroy()
        {
            if (!initialized)
            {
                return;
            }

            view.DiscardButton.onClick.RemoveListener(OpenDiscardPreview);
            view.DiscardCloseButton.onClick.RemoveListener(CloseDiscardPreview);
            DestroyDragGhost();
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (layoutProfile == null)
            {
                reason = "手牌面板缺少有效的显式布局 Profile。";
                return false;
            }

            if (!layoutProfile.TryValidateConfiguration(out reason))
            {
                reason = "手牌面板缺少有效的显式布局 Profile：" + reason;
                return false;
            }

            if (view == null || !view.IsBoundTo(this) || !view.TryValidateConfiguration(out reason))
            {
                reason = "手牌面板固定 View 配置无效：" + reason;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public bool Bind(CharacterHandPanelView configuredView)
        {
            if (initialized)
            {
                return configuredView == view;
            }

            view = configuredView;
            if (!TryValidateConfiguration(out var reason))
            {
                Debug.LogError("[CharacterHandPanel] View 绑定失败：" + reason, this);
                return false;
            }

            view.DiscardButton.onClick.RemoveListener(OpenDiscardPreview);
            view.DiscardButton.onClick.AddListener(OpenDiscardPreview);
            view.DiscardCloseButton.onClick.RemoveListener(CloseDiscardPreview);
            view.DiscardCloseButton.onClick.AddListener(CloseDiscardPreview);
            view.DiscardCloseInputHandler.Configure(CloseDiscardPreview);
            view.DiscardOverlayObject.SetActive(false);
            view.DiscardCountText.text = "0";
            initialized = true;
            return true;
        }

        public bool Configure(
            CardVisualCatalog configuredCatalog,
            Action<string, Vector2> configuredBeginCoverDrag,
            Action<Vector2> configuredUpdateCoverDrag,
            Action<string, Vector2> configuredEndCoverDrag)
        {
            if (!Bind(view))
            {
                return false;
            }

            var reason = string.Empty;
            if (configuredCatalog == null || !configuredCatalog.TryValidateConfiguration(out reason))
            {
                Debug.LogError("[CharacterHandPanel] CardVisualCatalog 配置无效：" + reason, this);
                return false;
            }

            cardVisualCatalog = configuredCatalog;
            beginCoverDrag = configuredBeginCoverDrag;
            updateCoverDrag = configuredUpdateCoverDrag;
            endCoverDrag = configuredEndCoverDrag;
            return true;
        }

        public void Render(int playerId, CharacterCardPanelViewModel viewModel)
        {
            if (!initialized || cardVisualCatalog == null)
            {
                return;
            }

            CancelActiveDrag();
            currentPlayerId = playerId;
            currentViewModel = viewModel;
            ReconcilePreferredOrder(playerId, viewModel);
            BuildOrderedLists(playerId, viewModel);
            RebuildHandCards();
            view.DiscardCountText.text = orderedDiscard.Count.ToString();
            if (IsDiscardPreviewOpen)
            {
                RebuildDiscardPreview();
            }
        }

        public void OpenDiscardPreview()
        {
            if (!initialized)
            {
                return;
            }

            RebuildDiscardPreview();
            view.DiscardCloseInputHandler.Configure(CloseDiscardPreview);
            view.DiscardOverlayObject.SetActive(true);
            view.DiscardOverlayObject.transform.SetAsLastSibling();
        }

        public void CloseDiscardPreview()
        {
            if (!initialized)
            {
                return;
            }

            view.DiscardOverlayObject.SetActive(false);
            ClearChildren(view.OverlayHandContent);
            ClearChildren(view.OverlayDiscardContent);
        }

        public bool TryHandleEscape()
        {
            if (!IsDiscardPreviewOpen)
            {
                return false;
            }

            if (ZoomableImageViewerController.HasOpenViewer() ||
                ZoomableImageViewerController.WasEscapeConsumedThisFrame())
            {
                return true;
            }

            CloseDiscardPreview();
            return true;
        }

        public void OpenCoveredCharacterCardViewer()
        {
            if (currentViewModel == null || string.IsNullOrEmpty(currentViewModel.CoveredCardId))
            {
                return;
            }

            var cardName = CharacterCardPanelPresenter.ResolveCardDisplayName(currentViewModel.CoveredCardId);
            OpenCardViewer(
                "已盖放角色牌（" + cardName + "）",
                currentViewModel.CoveredCardId);
        }

        public void CloseCharacterCardViewer()
        {
            cardImageViewer?.Close();
        }

        private void ReconcilePreferredOrder(int playerId, CharacterCardPanelViewModel model)
        {
            if (playerId <= 0)
            {
                return;
            }

            if (!preferredOrders.TryGetValue(playerId, out var order))
            {
                order = new List<string>();
                preferredOrders.Add(playerId, order);
            }

            if (model == null)
            {
                return;
            }

            AppendUnknown(order, model.HandCards);
            AppendUnknown(order, model.DiscardCards);
            if (!string.IsNullOrEmpty(model.CoveredCardId) && !order.Contains(model.CoveredCardId))
            {
                order.Add(model.CoveredCardId);
            }
        }

        private void BuildOrderedLists(int playerId, CharacterCardPanelViewModel model)
        {
            orderedHand.Clear();
            orderedDiscard.Clear();
            if (model == null)
            {
                return;
            }

            orderedHand.AddRange(model.HandCards);
            orderedDiscard.AddRange(model.DiscardCards);
            if (!preferredOrders.TryGetValue(playerId, out var order))
            {
                return;
            }

            orderedHand.Sort((left, right) => CompareByPreferredOrder(order, left.CardId, right.CardId));
            orderedDiscard.Sort((left, right) => CompareByPreferredOrder(order, left.CardId, right.CardId));
        }

        private void RebuildHandCards()
        {
            ClearHandCards();
            for (var i = 0; i < orderedHand.Count; i++)
            {
                var model = orderedHand[i];
                var entry = new HandCardEntry
                {
                    Model = model,
                    View = InstantiateCard(view.HandCardTemplate, view.HandCardsRoot, "Hand Card: " + model.CardId)
                };
                ConfigureCard(entry.View, model.CardId, false, model.CanCover, entry);
                handEntries.Add(entry);
            }

            ApplyHandLayout();
        }

        private void ConfigureCard(
            CharacterHandCardView card,
            string cardId,
            bool discard,
            bool canCover,
            HandCardEntry handEntry)
        {
            var texture = cardVisualCatalog.GetCharacterFront(cardId);
            card.Image.texture = texture;
            card.Image.color = Color.white;
            card.Image.material = discard ? view.DiscardGrayscaleMaterial : null;
            card.Button.interactable = texture != null;
            card.CanvasGroup.alpha = discard ? layoutProfile.DiscardAlpha : 1f;
            card.PointerInteraction.ConfigureClick(
                card.Button,
                () => OpenCardViewer(
                    discard
                        ? CharacterCardPanelPresenter.ResolveCardDisplayName(cardId) + "（弃牌区）"
                        : CharacterCardPanelPresenter.ResolveCardDisplayName(cardId),
                    cardId),
                null);
            card.PointerInteraction.ConfigureHover(
                discard ? (Action)null : () => SetHoveredCard(cardId),
                discard ? (Action)null : () => ClearHoveredCard(cardId));
            if (handEntry == null)
            {
                card.PointerInteraction.ConfigureDrag(null, null, null, null);
                return;
            }

            card.PointerInteraction.ConfigureDrag(
                () => true,
                eventData => BeginHandDrag(handEntry, canCover, eventData),
                eventData => UpdateHandDrag(handEntry, canCover, eventData),
                eventData => EndHandDrag(handEntry, canCover, eventData),
                CancelActiveDrag);
        }

        private void SetHoveredCard(string cardId)
        {
            if (!string.IsNullOrEmpty(draggedCardId))
            {
                return;
            }

            hoveredCardId = cardId ?? string.Empty;
            ApplyHandLayout();
        }

        private void ClearHoveredCard(string cardId)
        {
            if (hoveredCardId == cardId)
            {
                hoveredCardId = string.Empty;
                ApplyHandLayout();
            }
        }

        private void BeginHandDrag(HandCardEntry entry, bool canCover, PointerEventData eventData)
        {
            if (entry == null || eventData == null || !string.IsNullOrEmpty(draggedCardId))
            {
                return;
            }

            draggedCardId = entry.Model.CardId;
            hoveredCardId = string.Empty;
            if (preferredOrders.TryGetValue(currentPlayerId, out var preferred))
            {
                dragInitialPreferredOrder = new List<string>(preferred);
            }

            CreateDragGhost(entry.View, eventData);
            if (canCover)
            {
                beginCoverDrag?.Invoke(draggedCardId, eventData.position);
            }
            ApplyHandLayout();
        }

        private void UpdateHandDrag(HandCardEntry entry, bool canCover, PointerEventData eventData)
        {
            if (entry == null || eventData == null || draggedCardId != entry.Model.CardId)
            {
                return;
            }

            MoveDragGhost(eventData);
            if (canCover)
            {
                updateCoverDrag?.Invoke(eventData.position);
            }

            if (IsPointerInsideHand(eventData))
            {
                PreviewHandInsertion(eventData);
            }
        }

        private void EndHandDrag(HandCardEntry entry, bool canCover, PointerEventData eventData)
        {
            if (entry == null || eventData == null || draggedCardId != entry.Model.CardId)
            {
                CancelActiveDrag();
                return;
            }

            var droppedInHand = IsPointerInsideHand(eventData);
            if (canCover)
            {
                endCoverDrag?.Invoke(entry.Model.CardId, eventData.position);
            }

            if (droppedInHand)
            {
                CommitCurrentHandOrder();
            }
            else
            {
                RestoreDragInitialOrder();
                BuildOrderedLists(currentPlayerId, currentViewModel);
            }

            draggedCardId = string.Empty;
            dragInitialPreferredOrder = null;
            DestroyDragGhost();
            RebuildHandCards();
        }

        private void CancelActiveDrag()
        {
            if (string.IsNullOrEmpty(draggedCardId) && dragGhost == null)
            {
                return;
            }

            RestoreDragInitialOrder();
            draggedCardId = string.Empty;
            dragInitialPreferredOrder = null;
            DestroyDragGhost();
            BuildOrderedLists(currentPlayerId, currentViewModel);
            ApplyHandLayout();
        }

        private void PreviewHandInsertion(PointerEventData eventData)
        {
            if (orderedHand.Count <= 1 ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    view.HandCardsRoot,
                    eventData.position,
                    eventData.pressEventCamera,
                    out var point))
            {
                return;
            }

            var spacing = currentViewModel != null && currentViewModel.CanCover
                ? layoutProfile.ExpandedSpacing
                : layoutProfile.FanSpacing;
            var rawIndex = (point.x - layoutProfile.FanCenterOffsetX) / spacing +
                           (orderedHand.Count - 1) * 0.5f;
            var targetIndex = Mathf.Clamp(Mathf.RoundToInt(rawIndex), 0, orderedHand.Count - 1);
            var currentIndex = FindCardIndex(orderedHand, draggedCardId);
            if (currentIndex < 0 || currentIndex == targetIndex)
            {
                return;
            }

            var item = orderedHand[currentIndex];
            orderedHand.RemoveAt(currentIndex);
            orderedHand.Insert(targetIndex, item);
            ReorderEntriesToMatchModel();
            ApplyHandLayout();
        }

        private void CommitCurrentHandOrder()
        {
            if (!preferredOrders.TryGetValue(currentPlayerId, out var preferred))
            {
                preferred = new List<string>();
                preferredOrders[currentPlayerId] = preferred;
            }

            var handIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < orderedHand.Count; i++)
            {
                handIds.Add(orderedHand[i].CardId);
            }

            var slots = new List<int>();
            for (var i = 0; i < preferred.Count; i++)
            {
                if (handIds.Contains(preferred[i]))
                {
                    slots.Add(i);
                }
            }

            while (slots.Count < orderedHand.Count)
            {
                preferred.Add(string.Empty);
                slots.Add(preferred.Count - 1);
            }

            for (var i = 0; i < orderedHand.Count; i++)
            {
                preferred[slots[i]] = orderedHand[i].CardId;
            }
        }

        private void RestoreDragInitialOrder()
        {
            if (dragInitialPreferredOrder != null)
            {
                preferredOrders[currentPlayerId] = new List<string>(dragInitialPreferredOrder);
            }
        }

        private bool IsPointerInsideHand(PointerEventData eventData)
        {
            return eventData != null && RectTransformUtility.RectangleContainsScreenPoint(
                view.HandDropArea,
                eventData.position,
                eventData.pressEventCamera);
        }

        private void ApplyHandLayout()
        {
            var expanded = currentViewModel != null && currentViewModel.CanCover;
            var count = handEntries.Count;
            for (var i = 0; i < count; i++)
            {
                var entry = handEntries[i];
                var rect = entry.View.Root;
                rect.anchorMin = Vector2.right * 0.5f;
                rect.anchorMax = rect.anchorMin;
                rect.pivot = Vector2.one * 0.5f;
                var isHovered = entry.Model.CardId == hoveredCardId &&
                                string.IsNullOrEmpty(draggedCardId);
                var isDragged = entry.Model.CardId == draggedCardId;
                var spacing = expanded ? layoutProfile.ExpandedSpacing : layoutProfile.FanSpacing;
                var maxAngle = expanded ? layoutProfile.ExpandedMaxAngle : layoutProfile.FanMaxAngle;
                var normalized = count <= 1 ? 0f : i / (float)(count - 1) * 2f - 1f;
                var x = layoutProfile.FanCenterOffsetX + (i - (count - 1) * 0.5f) * spacing;
                var baseY = expanded
                    ? layoutProfile.CardSize.y * 0.5f + layoutProfile.ExpandedBottom
                    : layoutProfile.CardSize.y * (layoutProfile.PassiveVisibleFraction - 0.5f);
                var y = isHovered
                    ? layoutProfile.CardSize.y * 0.5f + layoutProfile.HoverBottom
                    : baseY;
                rect.sizeDelta = layoutProfile.CardSize;
                rect.anchoredPosition = Vector2.right * x + Vector2.up * y;
                rect.localRotation = Quaternion.Euler(0f, 0f, isHovered ? 0f : -normalized * maxAngle);
                rect.localScale = Vector3.one * (isHovered ? layoutProfile.HoverScale : 1f);
                entry.View.CanvasGroup.alpha = isDragged
                    ? 0.12f
                    : isHovered
                        ? layoutProfile.HoverAlpha
                        : expanded
                            ? 1f
                            : layoutProfile.PassiveAlpha;
                entry.View.CanvasGroup.blocksRaycasts = !isDragged;
                rect.SetSiblingIndex(i);
            }

            if (!string.IsNullOrEmpty(hoveredCardId))
            {
                var hovered = FindEntry(hoveredCardId);
                hovered?.View.Root.SetAsLastSibling();
            }
        }

        private void CreateDragGhost(CharacterHandCardView source, PointerEventData eventData)
        {
            DestroyDragGhost();
            var instance = Instantiate(view.DragGhostTemplate, view.Root, false);
            instance.gameObject.name = "Character Hand Drag Ghost";
            instance.gameObject.SetActive(true);
            instance.SetAsLastSibling();
            instance.sizeDelta = layoutProfile.CardSize;
            instance.GetComponent<RawImage>().texture = source.Image.texture;
            instance.GetComponent<CanvasGroup>().alpha = 0.82f;
            dragGhost = instance;
            MoveDragGhost(eventData);
        }

        private void MoveDragGhost(PointerEventData eventData)
        {
            if (dragGhost == null || eventData == null)
            {
                return;
            }

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    view.Root,
                    eventData.position,
                    eventData.pressEventCamera,
                    out var point))
            {
                dragGhost.anchoredPosition = point;
            }
        }

        private void DestroyDragGhost()
        {
            if (dragGhost == null)
            {
                return;
            }

            DestroyDynamicObject(dragGhost.gameObject);
            dragGhost = null;
        }

        private void RebuildDiscardPreview()
        {
            ClearChildren(view.OverlayHandContent);
            ClearChildren(view.OverlayDiscardContent);
            view.OverlayHandTitle.text = "手牌（" + orderedHand.Count + "）";
            view.OverlayDiscardTitle.text = "弃牌（" + orderedDiscard.Count + "）";
            view.OverlayHandEmptyObject.SetActive(orderedHand.Count == 0);
            view.OverlayDiscardEmptyObject.SetActive(orderedDiscard.Count == 0);
            view.OverlayHandGrid.cellSize = layoutProfile.OverlayCardSize;
            view.OverlayHandGrid.spacing = layoutProfile.OverlaySpacing;
            view.OverlayDiscardGrid.cellSize = layoutProfile.OverlayCardSize;
            view.OverlayDiscardGrid.spacing = layoutProfile.OverlaySpacing;

            for (var i = 0; i < orderedHand.Count; i++)
            {
                var item = orderedHand[i];
                var card = InstantiateCard(
                    view.OverlayCardTemplate,
                    view.OverlayHandContent,
                    "Preview Hand Card: " + item.CardId);
                ConfigureCard(card, item.CardId, false, false, null);
            }

            for (var i = 0; i < orderedDiscard.Count; i++)
            {
                var item = orderedDiscard[i];
                var card = InstantiateCard(
                    view.OverlayCardTemplate,
                    view.OverlayDiscardContent,
                    "Preview Discard Card: " + item.CardId);
                ConfigureCard(card, item.CardId, true, false, null);
            }
        }

        private void OpenCardViewer(string title, string cardId)
        {
            if (cardVisualCatalog == null)
            {
                return;
            }

            var texture = cardVisualCatalog.GetCharacterFront(cardId);
            if (texture == null)
            {
                return;
            }

            if (cardImageViewer == null)
            {
                cardImageViewer = ZoomableImageViewerController.InstantiateRegistered(
                    transform,
                    "Character Card Image Viewer");
            }

            if (cardImageViewer == null)
            {
                return;
            }

            cardImageViewer.DisableReferenceCollapse();
            cardImageViewer.Configure("Character Card Image", title, 1, _ => texture);
            cardImageViewer.ConfigureActions(string.Empty, null, string.Empty, null);
            cardImageViewer.Open();
        }

        private void ClearHandCards()
        {
            for (var i = 0; i < handEntries.Count; i++)
            {
                if (handEntries[i].View != null)
                {
                    DestroyDynamicObject(handEntries[i].View.gameObject);
                }
            }
            handEntries.Clear();
        }

        private void ReorderEntriesToMatchModel()
        {
            handEntries.Sort((left, right) =>
                FindCardIndex(orderedHand, left.Model.CardId).CompareTo(
                    FindCardIndex(orderedHand, right.Model.CardId)));
        }

        private HandCardEntry FindEntry(string cardId)
        {
            for (var i = 0; i < handEntries.Count; i++)
            {
                if (handEntries[i].Model.CardId == cardId)
                {
                    return handEntries[i];
                }
            }

            return null;
        }

        private static CharacterHandCardView InstantiateCard(
            CharacterHandCardView template,
            Transform parent,
            string objectName)
        {
            var instance = Instantiate(template, parent, false);
            instance.gameObject.name = objectName;
            instance.gameObject.SetActive(true);
            return instance;
        }

        private static void AppendUnknown(
            ICollection<string> preferred,
            IReadOnlyList<CharacterCardHandItemViewModel> items)
        {
            if (items == null)
            {
                return;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var cardId = items[i].CardId;
                if (!string.IsNullOrEmpty(cardId) && !preferred.Contains(cardId))
                {
                    preferred.Add(cardId);
                }
            }
        }

        private static int CompareByPreferredOrder(IList<string> order, string left, string right)
        {
            var leftIndex = order.IndexOf(left);
            var rightIndex = order.IndexOf(right);
            if (leftIndex < 0) leftIndex = int.MaxValue;
            if (rightIndex < 0) rightIndex = int.MaxValue;
            return leftIndex == rightIndex
                ? string.CompareOrdinal(left, right)
                : leftIndex.CompareTo(rightIndex);
        }

        private static int FindCardIndex(
            IReadOnlyList<CharacterCardHandItemViewModel> items,
            string cardId)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].CardId == cardId)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void ClearChildren(RectTransform parent)
        {
            if (parent == null)
            {
                return;
            }

            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                DestroyDynamicObject(parent.GetChild(i).gameObject);
            }
        }

        private static void DestroyDynamicObject(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            if (UnityEngine.Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}
