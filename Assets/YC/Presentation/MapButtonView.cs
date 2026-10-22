using UnityEngine;
using UnityEngine.EventSystems;
using YC.Presentation.Maps;
using YC.Presentation.Workflows;

namespace YC.Presentation
{
    /// <summary>固定在地图预制体中的轮廓按钮；显示状态与输入资格分别控制。</summary>
    [DisallowMultipleComponent]
    public sealed class MapButtonView : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private WorkflowHighlightTargetKind targetKind;
        [SerializeField] private string targetId;
        [SerializeField] private string shapeId;
        [SerializeField] private SpriteRenderer image;
        [SerializeField] private PolygonCollider2D hitArea;
        private MapHighlightShape shape;
        private MobileCityInteractionController controller;

        public WorkflowHighlightTargetKind TargetKind => targetKind;
        public string TargetId => targetId;
        public SpriteRenderer Image => image;
        public PolygonCollider2D HitArea => hitArea;
        public bool IsInteractive => hitArea != null && hitArea.enabled && gameObject.activeInHierarchy;
        public bool IsSelected { get; private set; }

        public void Bind(MobileCityInteractionController owner, MapCoordinateSpace space,
            MapHighlightCatalog catalog, Vector2 normalizedPosition)
        {
            controller = owner;
            shape = catalog.GetShape(shapeId);
            transform.position = space.ToWorldPosition(normalizedPosition, 0f);
            // 按钮父级是地图 SpriteRenderer；只继承一次整图变换。
            var mapSize = (Vector2)space.MapRenderer.sprite.bounds.size;
            var size = shape.GetLocalCanvasSize(mapSize, catalog.SourceMapSize);
            image.sprite = shape.Available;
            image.transform.localScale = new Vector3(
                size.x / shape.Available.bounds.size.x,
                size.y / shape.Available.bounds.size.y, 1f);
            hitArea.SetPath(0, shape.GetLocalPolygon(mapSize, catalog.SourceMapSize));
            SetState(false, false, false);
        }

        public void SetState(bool visible, bool selected, bool interactive)
        {
            IsSelected = visible && selected;
            if (shape != null) image.sprite = IsSelected ? shape.Selected : shape.Available;
            image.color = Color.white;
            image.enabled = visible;
            hitArea.enabled = visible && interactive;
            YC.PlayerJourney.PlayerAutomationId.Attach(gameObject,
                "map." + (targetKind == WorkflowHighlightTargetKind.Location ? "location." :
                    targetKind == WorkflowHighlightTargetKind.Route ? "route." : "influence_slot.") + targetId,
                hitArea.enabled);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!IsInteractive || controller == null || eventData == null || MapDisplayController.SuppressMapClick ||
                eventData.button != PointerEventData.InputButton.Left || eventData.dragging) return;
            var threshold = EventSystem.current == null ? 5 : EventSystem.current.pixelDragThreshold;
            if ((eventData.position - eventData.pressPosition).sqrMagnitude > threshold * threshold) return;
            if (TabletopPointerClassifier.CanRouteMapPointer(eventData.position))
                controller.OnMapButtonClicked(targetKind, targetId);
        }
    }
}
