using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>
    /// 公共建设卡与特殊建设卡共用的尺寸、拖拽虚影移动和城市槽位解析。
    /// </summary>
    internal static class FacilityCardDragUtility
    {
        public const float CardWidth = 99f;
        public const float CardHeight = 141f;
        public const float CardImageInset = 3f;

        private static readonly Color CardBackground = new Color(0.09f, 0.07f, 0.045f, 0.72f);
        private const int MinimumDragSortingOrder = 140;

        public static RectTransform CreateDragGhost(
            RectTransform canvas,
            RectTransform source,
            Texture texture,
            string fallbackLabel,
            Font fallbackFont)
        {
            if (canvas == null || source == null)
            {
                return null;
            }

            var ghostObject = new GameObject(
                "建设卡拖动虚影",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(RawImage),
                typeof(CanvasGroup));
            ghostObject.transform.SetParent(canvas, false);
            ghostObject.transform.SetAsLastSibling();

            var dragCanvas = ghostObject.GetComponent<Canvas>();
            dragCanvas.overrideSorting = true;
            dragCanvas.sortingOrder = ResolveDragSortingOrder();

            var ghost = ghostObject.GetComponent<RectTransform>();
            ghost.anchorMin = new Vector2(0.5f, 0.5f);
            ghost.anchorMax = new Vector2(0.5f, 0.5f);
            ghost.pivot = new Vector2(0.5f, 0.5f);
            ghost.sizeDelta = source.rect.size;

            var image = ghostObject.GetComponent<RawImage>();
            image.texture = texture;
            image.color = texture == null ? CardBackground : Color.white;
            image.raycastTarget = false;

            var canvasGroup = ghostObject.GetComponent<CanvasGroup>();
            canvasGroup.alpha = 0.82f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            if (texture == null && !string.IsNullOrEmpty(fallbackLabel))
            {
                AddFallbackLabel(ghost, fallbackLabel, fallbackFont);
            }

            return ghost;
        }

        public static void MoveDragGhost(RectTransform ghost, PointerEventData eventData)
        {
            if (ghost == null || eventData == null)
            {
                return;
            }

            var canvasRect = ghost.parent as RectTransform;
            if (canvasRect == null)
            {
                return;
            }

            Vector2 localPoint;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect,
                    eventData.position,
                    eventData.pressEventCamera,
                    out localPoint))
            {
                ghost.anchoredPosition = localPoint;
            }
        }

        public static int ResolveCityBoardSlotIndex(PointerEventData eventData)
        {
            if (eventData == null)
            {
                return -1;
            }

            var hit = eventData.pointerCurrentRaycast.gameObject;
            var current = hit == null ? null : hit.transform;
            while (current != null)
            {
                var target = current.GetComponent<CityBoardSlotDropTarget>();
                if (target != null && target.SlotIndex >= 0)
                {
                    return target.SlotIndex;
                }

                current = current.parent;
            }

            var targets = UnityEngine.Object.FindObjectsOfType<CityBoardSlotDropTarget>();
            for (var i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                var rect = target == null ? null : target.transform as RectTransform;
                if (target != null && target.isActiveAndEnabled && target.SlotIndex >= 0 && rect != null &&
                    RectTransformUtility.RectangleContainsScreenPoint(
                        rect,
                        eventData.position,
                        eventData.pressEventCamera))
                {
                    return target.SlotIndex;
                }
            }

            return -1;
        }

        public static void DestroyDragGhost(ref RectTransform ghost)
        {
            if (ghost == null)
            {
                return;
            }

            var ghostObject = ghost.gameObject;
            ghost = null;
            if (UnityEngine.Application.isPlaying)
            {
                UnityEngine.Object.Destroy(ghostObject);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(ghostObject);
            }
        }

        private static void AddFallbackLabel(RectTransform parent, string value, Font font)
        {
            var textObject = new GameObject(
                "Fallback",
                typeof(RectTransform),
                typeof(Text),
                typeof(Outline));
            textObject.transform.SetParent(parent, false);
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(8f, 8f);
            rect.offsetMax = new Vector2(-8f, -8f);

            var text = textObject.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.font = font;
            text.fontSize = 12;
            text.fontStyle = FontStyle.Bold;
            text.color = UiTheme.ValueText;
            text.alignment = TextAnchor.MiddleCenter;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 8;
            text.resizeTextMaxSize = 12;
            text.raycastTarget = false;

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = UiTheme.DarkShadowLight;
            outline.effectDistance = new Vector2(1f, -1f);
        }

        private static int ResolveDragSortingOrder()
        {
            var sortingOrder = MinimumDragSortingOrder;
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            for (var i = 0; i < canvases.Length; i++)
            {
                if (canvases[i] != null)
                {
                    sortingOrder = Mathf.Max(sortingOrder, canvases[i].sortingOrder + 1);
                }
            }

            return Mathf.Min(sortingOrder, short.MaxValue);
        }
    }

    /// <summary>
    /// Reusable pointer interaction for cards. It owns click classification,
    /// drag lifecycle validation, and suppression of the click emitted after a drag.
    /// Card-specific visuals and gameplay behavior stay in the configured callbacks.
    /// </summary>
    public sealed class CardPointerInteraction : MonoBehaviour,
        IPointerDownHandler,
        IPointerClickHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        private Button button;
        private Action singleClick;
        private Action doubleClick;
        private Func<bool> canBeginDrag;
        private Action<PointerEventData> beginDrag;
        private Action<PointerEventData> drag;
        private Action<PointerEventData> endDrag;
        private Action cancelDrag;
        private bool dragging;
        private bool suppressClickForCurrentPress;

        public void ConfigureClick(
            Button configuredButton,
            Action configuredSingleClick,
            Action configuredDoubleClick)
        {
            button = configuredButton;
            singleClick = configuredSingleClick;
            doubleClick = configuredDoubleClick;
        }

        public void ConfigureDrag(
            Func<bool> configuredCanBeginDrag,
            Action<PointerEventData> configuredBeginDrag,
            Action<PointerEventData> configuredDrag,
            Action<PointerEventData> configuredEndDrag,
            Action configuredCancelDrag = null)
        {
            if (button == null)
            {
                button = GetComponent<Button>();
            }

            canBeginDrag = configuredCanBeginDrag;
            beginDrag = configuredBeginDrag;
            drag = configuredDrag;
            endDrag = configuredEndDrag;
            cancelDrag = configuredCancelDrag;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (IsLeftPointer(eventData))
            {
                suppressClickForCurrentPress = false;
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (button == null || !button.IsInteractable() || !IsLeftPointer(eventData))
            {
                return;
            }

            if (suppressClickForCurrentPress)
            {
                suppressClickForCurrentPress = false;
                return;
            }

            if (eventData.clickCount >= 2)
            {
                doubleClick?.Invoke();
                return;
            }

            singleClick?.Invoke();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = button != null &&
                       button.IsInteractable() &&
                       IsLeftPointer(eventData) &&
                       canBeginDrag != null &&
                       canBeginDrag();
            if (!dragging)
            {
                return;
            }

            suppressClickForCurrentPress = true;
            beginDrag?.Invoke(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (dragging && eventData != null)
            {
                drag?.Invoke(eventData);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            endDrag?.Invoke(eventData);
        }

        private void OnDisable()
        {
            if (dragging)
            {
                dragging = false;
                cancelDrag?.Invoke();
            }
        }

        private static bool IsLeftPointer(PointerEventData eventData)
        {
            return eventData != null && eventData.button == PointerEventData.InputButton.Left;
        }
    }
}
