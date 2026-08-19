using System.Collections.Generic;
using UnityEngine;

namespace YC.Presentation
{
    public static class MapCameraGeometry
    {
        private const float MinimumTangent = 0.0001f;
        private const float MinimumDistance = 0.01f;

        public static float CalculatePerspectiveFitDistance(
            IReadOnlyList<Vector3> worldCorners,
            Vector3 focusPoint,
            Quaternion cameraRotation,
            float verticalFieldOfViewDegrees,
            float aspect,
            float padding,
            float nearClipPlane)
        {
            if (worldCorners == null || worldCorners.Count == 0)
            {
                return 0f;
            }

            var safeFieldOfView = Mathf.Clamp(verticalFieldOfViewDegrees, 1f, 179f);
            var verticalTangent = Mathf.Max(
                MinimumTangent,
                Mathf.Tan(safeFieldOfView * Mathf.Deg2Rad * 0.5f));
            var horizontalTangent = Mathf.Max(MinimumTangent, verticalTangent * Mathf.Max(aspect, MinimumTangent));
            var safePadding = Mathf.Max(1f, padding);
            var inverseRotation = Quaternion.Inverse(cameraRotation);
            var requiredDistance = MinimumDistance;

            for (var i = 0; i < worldCorners.Count; i++)
            {
                var cameraLocalOffset = inverseRotation * (worldCorners[i] - focusPoint);
                requiredDistance = Mathf.Max(
                    requiredDistance,
                    Mathf.Abs(cameraLocalOffset.x) * safePadding / horizontalTangent - cameraLocalOffset.z,
                    Mathf.Abs(cameraLocalOffset.y) * safePadding / verticalTangent - cameraLocalOffset.z,
                    Mathf.Max(0f, nearClipPlane) - cameraLocalOffset.z);
            }

            return Mathf.Max(MinimumDistance, requiredDistance);
        }

        public static Vector3 CalculatePlanarCenter(
            IReadOnlyList<Vector3> worldCorners,
            Vector3 planeOrigin,
            Vector3 planeAxisX,
            Vector3 planeAxisY)
        {
            if (worldCorners == null || worldCorners.Count == 0)
            {
                return planeOrigin;
            }

            NormalizePlaneAxes(ref planeAxisX, ref planeAxisY);
            var minX = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;

            for (var i = 0; i < worldCorners.Count; i++)
            {
                var offset = worldCorners[i] - planeOrigin;
                var x = Vector3.Dot(offset, planeAxisX);
                var y = Vector3.Dot(offset, planeAxisY);
                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            return planeOrigin +
                   planeAxisX * ((minX + maxX) * 0.5f) +
                   planeAxisY * ((minY + maxY) * 0.5f);
        }

        public static bool TryScreenPointToPlane(
            Camera camera,
            Vector2 screenPosition,
            Plane plane,
            out Vector3 worldPoint)
        {
            worldPoint = default;
            if (camera == null)
            {
                return false;
            }

            var ray = camera.ScreenPointToRay(screenPosition);
            if (!plane.Raycast(ray, out var distance) || distance < 0f)
            {
                return false;
            }

            worldPoint = ray.GetPoint(distance);
            return true;
        }

        public static bool TryScreenToMapPlane(
            Camera camera,
            Vector2 screenPosition,
            SpriteRenderer mapRenderer,
            out Vector3 worldPoint)
        {
            worldPoint = default;
            if (mapRenderer == null)
            {
                return false;
            }

            var mapCenter = mapRenderer.sprite != null
                ? mapRenderer.transform.TransformPoint(mapRenderer.sprite.bounds.center)
                : mapRenderer.transform.position;
            var plane = new Plane(mapRenderer.transform.forward.normalized, mapCenter);
            return TryScreenPointToPlane(camera, screenPosition, plane, out worldPoint);
        }

        public static Vector3 ApplyPlanarDrag(
            Vector3 focusPoint,
            Vector3 anchorWorldPoint,
            Vector3 currentPointerWorldPoint,
            Vector3 panOrigin,
            Vector3 planeAxisX,
            Vector3 planeAxisY,
            Rect panBounds)
        {
            return ClampPlanarPosition(
                focusPoint + anchorWorldPoint - currentPointerWorldPoint,
                panOrigin,
                planeAxisX,
                planeAxisY,
                panBounds);
        }

        public static Vector3 ClampPlanarPosition(
            Vector3 desiredPosition,
            Vector3 panOrigin,
            Vector3 planeAxisX,
            Vector3 planeAxisY,
            Rect panBounds)
        {
            NormalizePlaneAxes(ref planeAxisX, ref planeAxisY);
            var offset = desiredPosition - panOrigin;
            var x = Mathf.Clamp(Vector3.Dot(offset, planeAxisX), panBounds.xMin, panBounds.xMax);
            var y = Mathf.Clamp(Vector3.Dot(offset, planeAxisY), panBounds.yMin, panBounds.yMax);
            return panOrigin + planeAxisX * x + planeAxisY * y;
        }

        public static Vector3 CalculateCameraPosition(
            Vector3 focusPoint,
            Quaternion cameraRotation,
            float distance)
        {
            return focusPoint - cameraRotation * Vector3.forward * Mathf.Max(MinimumDistance, distance);
        }

        public static float CalculateZoomedDistance(float baseDistance, float zoom)
        {
            return Mathf.Max(MinimumDistance, baseDistance) / Mathf.Max(MinimumTangent, zoom);
        }

        private static void NormalizePlaneAxes(ref Vector3 planeAxisX, ref Vector3 planeAxisY)
        {
            planeAxisX = planeAxisX.sqrMagnitude > 0f ? planeAxisX.normalized : Vector3.right;
            planeAxisY -= planeAxisX * Vector3.Dot(planeAxisY, planeAxisX);
            planeAxisY = planeAxisY.sqrMagnitude > 0f ? planeAxisY.normalized : Vector3.up;
        }

    }
}
