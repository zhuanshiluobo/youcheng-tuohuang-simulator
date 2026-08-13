using UnityEngine;

namespace YC.Presentation
{
    public interface ITabletopNavigationBoundsProvider
    {
        bool Initialize(
            Camera targetCamera,
            Rect tabletopViewport,
            Plane tabletopPlane,
            Vector3 panOrigin,
            Vector3 planeAxisX,
            Vector3 planeAxisY,
            SpriteRenderer mapRenderer,
            out string reason);

        bool TryGetFocusBounds(
            Camera targetCamera,
            Rect tabletopViewport,
            Plane tabletopPlane,
            Vector3 currentFocus,
            Vector3 planeAxisX,
            Vector3 planeAxisY,
            out Rect focusBounds);
    }

    [DisallowMultipleComponent]
    public sealed class TabletopViewportNavigationBounds : MonoBehaviour, ITabletopNavigationBoundsProvider
    {
        [Header("100% View Boundary")]
        [SerializeField, Range(0f, 1f)] private float horizontalMapMarginFraction = 0.15f;
        [SerializeField, Range(0f, 1f)] private float verticalMapMarginFraction = 0.15f;

        private Rect fixedWorldBounds;
        private bool isInitialized;

        public float HorizontalMapMarginFraction => horizontalMapMarginFraction;
        public float VerticalMapMarginFraction => verticalMapMarginFraction;

        public bool Initialize(
            Camera targetCamera,
            Rect tabletopViewport,
            Plane tabletopPlane,
            Vector3 origin,
            Vector3 planeAxisX,
            Vector3 planeAxisY,
            SpriteRenderer mapRenderer,
            out string reason)
        {
            isInitialized = false;
            if (mapRenderer == null || mapRenderer.sprite == null)
            {
                reason = "地图导航边界缺少有效地图 SpriteRenderer。";
                return false;
            }

            if (!TryGetViewportFootprint(
                    targetCamera,
                    tabletopViewport,
                    tabletopPlane,
                    origin,
                    planeAxisX,
                    planeAxisY,
                    out var baselineFootprint))
            {
                reason = "无法取得 100% 缩放时安全视口在桌面平面上的投影。";
                return false;
            }

            var spriteBounds = mapRenderer.sprite.bounds;
            var mapWidth = mapRenderer.transform
                .TransformVector(Vector3.right * spriteBounds.size.x).magnitude;
            var mapHeight = mapRenderer.transform
                .TransformVector(Vector3.up * spriteBounds.size.y).magnitude;
            var horizontalMargin = mapWidth * Mathf.Clamp01(horizontalMapMarginFraction);
            var verticalMargin = mapHeight * Mathf.Clamp01(verticalMapMarginFraction);

            fixedWorldBounds = Rect.MinMaxRect(
                baselineFootprint.xMin - horizontalMargin,
                baselineFootprint.yMin - verticalMargin,
                baselineFootprint.xMax + horizontalMargin,
                baselineFootprint.yMax + verticalMargin);
            isInitialized = true;
            reason = string.Empty;
            return true;
        }

        public bool TryGetFocusBounds(
            Camera targetCamera,
            Rect tabletopViewport,
            Plane tabletopPlane,
            Vector3 currentFocus,
            Vector3 planeAxisX,
            Vector3 planeAxisY,
            out Rect focusBounds)
        {
            focusBounds = default;
            if (!isInitialized ||
                !TryGetViewportFootprint(
                    targetCamera,
                    tabletopViewport,
                    tabletopPlane,
                    currentFocus,
                    planeAxisX,
                    planeAxisY,
                    out var currentFootprint))
            {
                return false;
            }

            var firstHorizontalEndpoint = fixedWorldBounds.xMin - currentFootprint.xMin;
            var secondHorizontalEndpoint = fixedWorldBounds.xMax - currentFootprint.xMax;
            var firstVerticalEndpoint = fixedWorldBounds.yMin - currentFootprint.yMin;
            var secondVerticalEndpoint = fixedWorldBounds.yMax - currentFootprint.yMax;
            focusBounds = Rect.MinMaxRect(
                Mathf.Min(firstHorizontalEndpoint, secondHorizontalEndpoint),
                Mathf.Min(firstVerticalEndpoint, secondVerticalEndpoint),
                Mathf.Max(firstHorizontalEndpoint, secondHorizontalEndpoint),
                Mathf.Max(firstVerticalEndpoint, secondVerticalEndpoint));
            return true;
        }

        private bool TryGetViewportFootprint(
            Camera targetCamera,
            Rect tabletopViewport,
            Plane tabletopPlane,
            Vector3 footprintOrigin,
            Vector3 planeAxisX,
            Vector3 planeAxisY,
            out Rect footprint)
        {
            footprint = default;
            if (targetCamera == null)
            {
                return false;
            }

            NormalizePlaneAxes(ref planeAxisX, ref planeAxisY);
            var minX = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;

            for (var i = 0; i < 4; i++)
            {
                var viewportPoint = new Vector3(
                    (i & 1) == 0 ? tabletopViewport.xMin : tabletopViewport.xMax,
                    (i & 2) == 0 ? tabletopViewport.yMin : tabletopViewport.yMax);
                var ray = targetCamera.ViewportPointToRay(viewportPoint);
                if (!tabletopPlane.Raycast(ray, out var distance) || distance < 0f)
                {
                    return false;
                }

                var offset = ray.GetPoint(distance) - footprintOrigin;
                var x = Vector3.Dot(offset, planeAxisX);
                var y = Vector3.Dot(offset, planeAxisY);
                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            footprint = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private static void NormalizePlaneAxes(ref Vector3 planeAxisX, ref Vector3 planeAxisY)
        {
            planeAxisX = planeAxisX.sqrMagnitude > 0f ? planeAxisX.normalized : Vector3.right;
            planeAxisY -= planeAxisX * Vector3.Dot(planeAxisY, planeAxisX);
            planeAxisY = planeAxisY.sqrMagnitude > 0f ? planeAxisY.normalized : Vector3.up;
        }

    }
}
