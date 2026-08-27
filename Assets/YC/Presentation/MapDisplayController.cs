using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace YC.Presentation
{
    [ExecuteAlways]
    public sealed class MapDisplayController : MonoBehaviour
    {
        private const float DefaultAspect = 16f / 9f;
        private const float InputEpsilon = 0.0001f;
        private const string DevelopmentZoomArgumentPrefix = "--yc-dev-map-zoom=";

        [Header("References")]
        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private MonoBehaviour navigationBoundsSource;

        [Header("Tabletop Camera")]
        [SerializeField, Range(1f, 89f)] private float cameraPitch = 30f;
        [SerializeField, Range(1f, 179f)] private float fieldOfView = 45f;
        [SerializeField, Min(1f)] private float cameraPadding = 1.03f;
        [SerializeField] private Rect tabletopViewport = new Rect(0.02865f, 0f, 0.78385f, 1f);

        [Header("Navigation")]
        [SerializeField, Range(0.1f, 1f)] private float minZoom = 0.9f;
        [SerializeField, Range(1f, 3f)] private float maxZoom = 2f;
        [SerializeField, Min(0.01f)] private float wheelZoomStep = 0.1f;
        [SerializeField, Min(0.01f)] private float zoomSmoothTime = 0.12f;

        private readonly List<Vector3> tabletopCorners = new List<Vector3>(32);

        private Plane tabletopPlane;
        private Quaternion cameraRotation;
        private Vector3 focusPoint;
        private Vector3 panOrigin;
        private Vector3 panAxisX;
        private Vector3 panAxisY;
        private ITabletopNavigationBoundsProvider navigationBounds;
        private float baseDistance;
        private float currentZoom = 1f;
        private float targetZoom = 1f;
        private float zoomVelocity;
        private bool hasCameraLayout;
        private bool isDragging;
        private Vector3 dragAnchorWorld;
        private bool hasZoomAnchor;
        private Vector2 zoomAnchorScreen;

        private void Awake()
        {
            FitCameraToMap();
        }

        private void OnEnable()
        {
            FitCameraToMap();
            ApplyDevelopmentZoomOverride();
        }

        private void OnDisable()
        {
            EndDrag();
            hasZoomAnchor = false;
            zoomVelocity = 0f;
        }

        private void OnValidate()
        {
            cameraPitch = Mathf.Clamp(cameraPitch, 1f, 89f);
            fieldOfView = Mathf.Clamp(fieldOfView, 1f, 179f);
            cameraPadding = Mathf.Max(1f, cameraPadding);
            tabletopViewport.x = Mathf.Clamp01(tabletopViewport.x);
            tabletopViewport.y = Mathf.Clamp01(tabletopViewport.y);
            tabletopViewport.width = Mathf.Clamp(tabletopViewport.width, 0.1f, 1f - tabletopViewport.x);
            tabletopViewport.height = Mathf.Clamp(tabletopViewport.height, 0.1f, 1f - tabletopViewport.y);
            minZoom = Mathf.Clamp(minZoom, 0.1f, 1f);
            maxZoom = Mathf.Max(1f, maxZoom);
            wheelZoomStep = Mathf.Max(0.01f, wheelZoomStep);
            zoomSmoothTime = Mathf.Max(0.01f, zoomSmoothTime);
            FitCameraToMap();
        }

        private void Update()
        {
            if (!UnityEngine.Application.isPlaying || !hasCameraLayout || targetCamera == null)
            {
                return;
            }

            UpdateZoomSmoothing();
            ReadNavigationInput();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                EndDrag();
            }
        }

        public void FitCameraToMap()
        {
            ResolveReferences();
            ApplyThemeBackground();

            if (mapRenderer == null || mapRenderer.sprite == null || targetCamera == null)
            {
                hasCameraLayout = false;
                return;
            }

            CollectMapCorners(tabletopCorners);
            if (tabletopCorners.Count == 0)
            {
                hasCameraLayout = false;
                return;
            }

            var spriteBounds = mapRenderer.sprite.bounds;
            var mapCenter = mapRenderer.transform.TransformPoint(spriteBounds.center);
            panAxisX = mapRenderer.transform.right.normalized;
            panAxisY = mapRenderer.transform.up.normalized;
            tabletopPlane = new Plane(mapRenderer.transform.forward.normalized, mapCenter);
            cameraRotation = Quaternion.Euler(-cameraPitch, 0f, 0f);

            focusPoint = mapCenter;

            targetCamera.orthographic = false;
            targetCamera.fieldOfView = fieldOfView;
            targetCamera.rect = new Rect(0f, 0f, 1f, 1f);
            var aspect = targetCamera.aspect > 0f ? targetCamera.aspect : DefaultAspect;
            var safeAspect = aspect * tabletopViewport.width / tabletopViewport.height;
            baseDistance = MapCameraGeometry.CalculatePerspectiveFitDistance(
                tabletopCorners,
                focusPoint,
                cameraRotation,
                fieldOfView,
                safeAspect,
                cameraPadding,
                targetCamera.nearClipPlane);

            currentZoom = 1f;
            targetZoom = 1f;
            zoomVelocity = 0f;
            hasZoomAnchor = false;
            EndDrag();
            hasCameraLayout = baseDistance > 0f;
            ApplyCameraTransform();

            panOrigin = focusPoint;
            var initialZoom = currentZoom;
            currentZoom = minZoom;
            ApplyCameraTransform();
            var hasNavigationBounds = ResolveNavigationBounds(out var reason) &&
                                      navigationBounds.Initialize(
                                          targetCamera,
                                          tabletopViewport,
                                          tabletopPlane,
                                          panOrigin,
                                          panAxisX,
                                          panAxisY,
                                          mapRenderer,
                                          out reason);
            currentZoom = initialZoom;
            targetZoom = initialZoom;
            ApplyCameraTransform();
            if (!hasNavigationBounds)
            {
                hasCameraLayout = false;
                Debug.LogError("[MapDisplayController] 桌面导航边界配置失败：" + reason, this);
            }
        }

        private void ResolveReferences()
        {
            if (mapRenderer == null)
            {
                mapRenderer = GetComponent<SpriteRenderer>();
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
        }

        private bool ResolveNavigationBounds(out string reason)
        {
            navigationBounds = navigationBoundsSource as ITabletopNavigationBoundsProvider;
            if (navigationBounds == null)
            {
                var behaviours = GetComponents<MonoBehaviour>();
                for (var i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] is ITabletopNavigationBoundsProvider provider)
                    {
                        navigationBoundsSource = behaviours[i];
                        navigationBounds = provider;
                        break;
                    }
                }
            }

            if (navigationBounds == null)
            {
                reason = "未绑定实现 ITabletopNavigationBoundsProvider 的边界组件。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void ApplyThemeBackground()
        {
            if (targetCamera == null)
            {
                return;
            }

            targetCamera.clearFlags = CameraClearFlags.SolidColor;
            if (UiTheme.IsInitialized)
            {
                targetCamera.backgroundColor = UiTheme.TacticalMapBg;
            }
        }

        private void CollectMapCorners(List<Vector3> corners)
        {
            corners.Clear();

            var spriteBounds = mapRenderer.sprite.bounds;
            var min = spriteBounds.min;
            var max = spriteBounds.max;
            corners.Add(mapRenderer.transform.TransformPoint(new Vector3(min.x, min.y, spriteBounds.center.z)));
            corners.Add(mapRenderer.transform.TransformPoint(new Vector3(min.x, max.y, spriteBounds.center.z)));
            corners.Add(mapRenderer.transform.TransformPoint(new Vector3(max.x, max.y, spriteBounds.center.z)));
            corners.Add(mapRenderer.transform.TransformPoint(new Vector3(max.x, min.y, spriteBounds.center.z)));
        }

        private void ReadNavigationInput()
        {
            var mousePosition = (Vector2)Input.mousePosition;

            if (Input.GetMouseButtonUp(1))
            {
                EndDrag();
            }

            if (isDragging && !Input.GetMouseButton(1))
            {
                EndDrag();
            }

            if (Input.GetMouseButtonDown(1) &&
                !TabletopPointerClassifier.IsBlockedByFlatHud(mousePosition) &&
                MapCameraGeometry.TryScreenPointToPlane(targetCamera, mousePosition, tabletopPlane, out dragAnchorWorld))
            {
                isDragging = true;
            }

            if (isDragging && Input.GetMouseButton(1))
            {
                UpdateDrag(mousePosition);
            }

            var wheelDelta = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheelDelta) <= InputEpsilon ||
                TabletopPointerClassifier.IsBlockedByFlatHud(mousePosition))
            {
                return;
            }

            targetZoom = Mathf.Clamp(targetZoom + wheelDelta * wheelZoomStep, minZoom, maxZoom);
            zoomAnchorScreen = mousePosition;
            hasZoomAnchor = MapCameraGeometry.TryScreenPointToPlane(
                targetCamera,
                zoomAnchorScreen,
                tabletopPlane,
                out _);
        }

        private void UpdateDrag(Vector2 mousePosition)
        {
            if (!MapCameraGeometry.TryScreenPointToPlane(
                    targetCamera,
                    mousePosition,
                    tabletopPlane,
                    out var currentPointerWorld))
            {
                return;
            }

            focusPoint = MapCameraGeometry.ApplyPlanarDrag(
                focusPoint,
                dragAnchorWorld,
                currentPointerWorld,
                panOrigin,
                panAxisX,
                panAxisY,
                GetCurrentPanBounds());
            ApplyCameraTransform();
        }

        private void UpdateZoomSmoothing()
        {
            if (Mathf.Abs(currentZoom - targetZoom) <= InputEpsilon)
            {
                currentZoom = targetZoom;
                zoomVelocity = 0f;
                hasZoomAnchor = false;
                return;
            }

            var pointBeforeZoom = default(Vector3);
            var hadPointBeforeZoom = hasZoomAnchor && MapCameraGeometry.TryScreenPointToPlane(
                targetCamera,
                zoomAnchorScreen,
                tabletopPlane,
                out pointBeforeZoom);

            currentZoom = Mathf.SmoothDamp(
                currentZoom,
                targetZoom,
                ref zoomVelocity,
                zoomSmoothTime,
                Mathf.Infinity,
                Time.unscaledDeltaTime);
            currentZoom = Mathf.Clamp(currentZoom, minZoom, maxZoom);
            ApplyCameraTransform();

            var desiredFocus = focusPoint;
            if (hadPointBeforeZoom && MapCameraGeometry.TryScreenPointToPlane(
                    targetCamera,
                    zoomAnchorScreen,
                    tabletopPlane,
                    out var pointAfterZoom))
            {
                desiredFocus += pointBeforeZoom - pointAfterZoom;
            }

            focusPoint = MapCameraGeometry.ClampPlanarPosition(
                desiredFocus,
                panOrigin,
                panAxisX,
                panAxisY,
                GetCurrentPanBounds());
            ApplyCameraTransform();
        }

        private Rect GetCurrentPanBounds()
        {
            return navigationBounds != null && navigationBounds.TryGetFocusBounds(
                targetCamera,
                tabletopViewport,
                tabletopPlane,
                focusPoint,
                panAxisX,
                panAxisY,
                out var bounds)
                ? bounds
                : Rect.zero;
        }

        private void ApplyCameraTransform()
        {
            if (!hasCameraLayout || targetCamera == null)
            {
                return;
            }

            var safeZoom = Mathf.Max(minZoom, currentZoom);
            var cameraPosition = MapCameraGeometry.CalculateCameraPosition(
                focusPoint,
                cameraRotation,
                MapCameraGeometry.CalculateZoomedDistance(baseDistance, safeZoom));
            targetCamera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
        }

        private void EndDrag()
        {
            isDragging = false;
        }

        private void ApplyDevelopmentZoomOverride()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!UnityEngine.Application.isPlaying || !hasCameraLayout)
            {
                return;
            }

            var arguments = Environment.GetCommandLineArgs();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (!arguments[i].StartsWith(
                        DevelopmentZoomArgumentPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var valueText = arguments[i].Substring(DevelopmentZoomArgumentPrefix.Length);
                if (!float.TryParse(
                        valueText,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    Debug.LogError("开发版地图缩放验收参数无效：" + arguments[i], this);
                    return;
                }

                currentZoom = Mathf.Clamp(value, minZoom, maxZoom);
                targetZoom = currentZoom;
                zoomVelocity = 0f;
                hasZoomAnchor = false;
                ApplyCameraTransform();
                return;
            }
#endif
        }
    }
}
