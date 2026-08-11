using UnityEngine;

namespace YC.Presentation.Maps
{
    [DisallowMultipleComponent]
    public sealed class MapCoordinateSpace : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private MapDisplayLayout layout;
        [SerializeField, HideInInspector] private Transform contentRoot;

        public SpriteRenderer MapRenderer
        {
            get { return mapRenderer; }
        }

        public MapDisplayLayout Layout
        {
            get { return layout; }
        }

        public Transform ContentRoot
        {
            get { return contentRoot; }
        }

        public void Configure(
            SpriteRenderer renderer,
            MapDisplayLayout displayLayout,
            Transform fixedContentRoot = null)
        {
            if (renderer != null)
            {
                mapRenderer = renderer;
            }

            if (displayLayout != null)
            {
                layout = displayLayout;
            }

            if (fixedContentRoot != null)
            {
                contentRoot = fixedContentRoot;
            }
        }

        public bool ValidateBinding(out string error)
        {
            if (mapRenderer == null)
            {
                error = "Map coordinate space requires a SpriteRenderer.";
                return false;
            }

            if (mapRenderer.sprite == null)
            {
                error = "Map coordinate space requires a map sprite.";
                return false;
            }

            if (layout == null)
            {
                error = "Map coordinate space requires a map display layout.";
                return false;
            }

            if (contentRoot == null)
            {
                error = "Map coordinate space requires a fixed content root.";
                return false;
            }

            if (contentRoot != mapRenderer.transform && !contentRoot.IsChildOf(mapRenderer.transform))
            {
                error = "Map coordinate space content root must be the map renderer or one of its children.";
                return false;
            }

            if (layout.MapSprite != null && layout.MapSprite != mapRenderer.sprite)
            {
                error = "Map display layout " + layout.MapId + " is bound to a different map sprite.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        public Vector3 ToWorldPosition(Vector2 normalizedPosition, float localZ)
        {
            if (mapRenderer == null || mapRenderer.sprite == null)
            {
                return transform.position;
            }

            return mapRenderer.transform.TransformPoint(ToRendererLocalPosition(normalizedPosition, localZ));
        }

        public Vector2 ToNormalizedPosition(Vector3 worldPosition)
        {
            if (mapRenderer == null || mapRenderer.sprite == null)
            {
                return Vector2.zero;
            }

            var bounds = mapRenderer.sprite.bounds;
            var local = mapRenderer.transform.InverseTransformPoint(worldPosition);
            var normalizedX = Mathf.InverseLerp(bounds.min.x, bounds.max.x, local.x);
            var normalizedY = Mathf.InverseLerp(bounds.max.y, bounds.min.y, local.y);
            return new Vector2(
                mapRenderer.flipX ? 1f - normalizedX : normalizedX,
                mapRenderer.flipY ? 1f - normalizedY : normalizedY);
        }

        private Vector3 ToRendererLocalPosition(Vector2 normalizedPosition, float localZ)
        {
            var bounds = mapRenderer.sprite.bounds;
            var normalizedX = mapRenderer.flipX ? 1f - normalizedPosition.x : normalizedPosition.x;
            var normalizedY = mapRenderer.flipY ? 1f - normalizedPosition.y : normalizedPosition.y;
            return new Vector3(
                Mathf.Lerp(bounds.min.x, bounds.max.x, normalizedX),
                Mathf.Lerp(bounds.max.y, bounds.min.y, normalizedY),
                localZ);
        }
    }
}
