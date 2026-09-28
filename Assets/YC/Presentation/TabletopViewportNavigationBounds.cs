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
        private Rect fixedWorldBounds;
        private bool isInitialized;

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

            NormalizePlaneAxes(ref planeAxisX, ref planeAxisY);
            var spriteBounds = mapRenderer.sprite.bounds;
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (var i = 0; i < 4; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? spriteBounds.min.x : spriteBounds.max.x,
                    (i & 2) == 0 ? spriteBounds.min.y : spriteBounds.max.y,
                    spriteBounds.center.z);
                var offset = mapRenderer.transform.TransformPoint(corner) - origin;
                var point = new Vector2(Vector3.Dot(offset, planeAxisX), Vector3.Dot(offset, planeAxisY));
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            // 边界只取地图自身，不随初始视角、缩放或额外留白扩张。
            fixedWorldBounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
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
                    new Rect(0f, 0f, 1f, 1f),
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
            // 容忍射线投影的浮点误差；某轴视口过大时仅锁定该轴的中心。
            if (firstHorizontalEndpoint > secondHorizontalEndpoint)
            {
                firstHorizontalEndpoint = secondHorizontalEndpoint =
                    (firstHorizontalEndpoint + secondHorizontalEndpoint) * 0.5f;
            }
            if (firstVerticalEndpoint > secondVerticalEndpoint)
            {
                firstVerticalEndpoint = secondVerticalEndpoint =
                    (firstVerticalEndpoint + secondVerticalEndpoint) * 0.5f;
            }

            focusBounds = Rect.MinMaxRect(
                firstHorizontalEndpoint,
                firstVerticalEndpoint,
                secondHorizontalEndpoint,
                secondVerticalEndpoint);
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
