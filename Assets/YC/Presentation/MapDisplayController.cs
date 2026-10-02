using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace YC.Presentation
{
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
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
        [SerializeField, Range(1f, 179f)] private float fieldOfView = 45f;
        [SerializeField] private Rect tabletopViewport = new Rect(0.02865f, 0f, 0.78385f, 1f);
        [SerializeField] private Rect cameraViewport = new Rect(0f, 0f, 1f, 1f);

        [Header("Navigation")]
        [SerializeField, Range(0.1f, 1f)] private float minZoom = 0.9f;
        [SerializeField, Range(1f, 3f)] private float maxZoom = 2f;
        [SerializeField, Min(0.01f)] private float wheelZoomStep = 0.18f;
        [SerializeField, Min(0.01f)] private float zoomSmoothTime = 0.12f;

        private readonly List<Vector3> tabletopCorners = new List<Vector3>(32);

        private static MapDisplayController active;
        private Vector2 mapSize;
        private float minimumNavigationZoom;
        private bool pointerPending;
        private Vector2 pointerStart;
        private int mouseDragButton;
        private bool isPinching;
        private bool waitForTouchRelease;
        private int pinchFingerA, pinchFingerB;
        private Vector2 previousPinchCenter;
        private float previousPinchDistance;
        private int suppressClickThroughFrame = -1;
        public static MapDisplayController Active => active;
        public Rect VisibleViewportInRegion { get; private set; } = new Rect(0f, 0f, 1f, 1f);
        public static bool SuppressMapClick => active != null &&
            (active.isDragging || active.isPinching || active.waitForTouchRelease ||
             Time.frameCount <= active.suppressClickThroughFrame);
        private Plane tabletopPlane;
        private Quaternion cameraRotation;
        private Vector3 focusPoint;
        private Vector3 panOrigin;
        private Vector3 panAxisX;
        private Vector3 panAxisY;
        private ITabletopNavigationBoundsProvider navigationBounds;
        private float baseDistance;
        private float layoutAspect;
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
            active = this;
            FitCameraToMap();
            ApplyDevelopmentZoomOverride();
        }

        private void OnDisable()
        {
            if (active == this) active = null;
            isPinching = waitForTouchRelease = false;
            EndDrag();
            hasZoomAnchor = false;
            zoomVelocity = 0f;
        }

        private void OnValidate()
        {
            fieldOfView = Mathf.Clamp(fieldOfView, 1f, 179f);
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
            if (!UnityEngine.Application.isPlaying || !hasCameraLayout ||
                targetCamera == null || !targetCamera.enabled)
            {
                return;
            }

            if (!Mathf.Approximately(AvailableAspect, layoutAspect))
            {
                FitCameraToMap(true);
            }

            UpdateZoomSmoothing();
            ReadNavigationInput();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                EndDrag();
                isPinching = false;
                waitForTouchRelease = Input.touchCount > 0;
            }
        }

        private float AvailableAspect => cameraViewport.width * Mathf.Max(1, Screen.width) /
            (cameraViewport.height * Mathf.Max(1, Screen.height));

        public void FitCameraToMap()
        {
            FitCameraToMap(false);
        }

        private void FitCameraToMap(bool preserveNavigation)
        {
            var previousFocus = focusPoint;
            var previousZoom = currentZoom;
            var previousTargetZoom = targetZoom;
            preserveNavigation &= hasCameraLayout;
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
            // 地图位于 Sprite 的局部 XY 平面，沿其法线垂直观察。
            cameraRotation = Quaternion.LookRotation(mapRenderer.transform.forward, panAxisY);

            focusPoint = mapCenter;
            panOrigin = mapCenter;

            targetCamera.orthographic = false;
            targetCamera.fieldOfView = fieldOfView;
            targetCamera.rect = cameraViewport;
            var aspect = AvailableAspect > 0f ? AvailableAspect : DefaultAspect;
            layoutAspect = aspect;
            var mapWidth = mapRenderer.transform.TransformVector(Vector3.right * spriteBounds.size.x).magnitude;
            var mapHeight = mapRenderer.transform.TransformVector(Vector3.up * spriteBounds.size.y).magnitude;
            var verticalTangent = Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f);
            // 初始仍铺满观察框；继续缩小时收缩开口的多余一轴，直到全图可见。
            var maximumDistance = Mathf.Min(mapWidth / aspect, mapHeight) / (2f * verticalTangent);
            baseDistance = maximumDistance * minZoom;
            mapSize = new Vector2(mapWidth, mapHeight);
            var wholeMapDistance = Mathf.Max(mapWidth / aspect, mapHeight) / (2f * verticalTangent);
            minimumNavigationZoom = baseDistance / Mathf.Max(.01f, wholeMapDistance);

            currentZoom = preserveNavigation
                ? Mathf.Clamp(previousZoom, minimumNavigationZoom, maxZoom)
                : minZoom;
            targetZoom = currentZoom;
            zoomVelocity = 0f;
            hasZoomAnchor = false;
            EndDrag();
            hasCameraLayout = baseDistance > 0f;
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
            if (!hasNavigationBounds)
            {
                hasCameraLayout = false;
                Debug.LogError("[MapDisplayController] 桌面导航边界配置失败：" + reason, this);
                return;
            }

            if (preserveNavigation)
            {
                currentZoom = Mathf.Clamp(previousZoom, minimumNavigationZoom, maxZoom);
                targetZoom = Mathf.Clamp(previousTargetZoom, minimumNavigationZoom, maxZoom);
                focusPoint = previousFocus;
            }
            ApplyCameraTransform();
        }

        public void SetScreenViewport(Rect viewport)
        {
            viewport = Rect.MinMaxRect(
                Mathf.Clamp01(viewport.xMin), Mathf.Clamp01(viewport.yMin),
                Mathf.Clamp01(viewport.xMax), Mathf.Clamp01(viewport.yMax));
            if (viewport.width <= 0f || viewport.height <= 0f)
            {
                EndDrag();
                isPinching = false;
                waitForTouchRelease = Input.touchCount > 0;
                suppressClickThroughFrame = Time.frameCount + 1;
                hasZoomAnchor = false;
                if (targetCamera != null) targetCamera.enabled = false;
                return;
            }
            if (targetCamera != null) targetCamera.enabled = true;
            if (cameraViewport == viewport && tabletopViewport == new Rect(0f, 0f, 1f, 1f)) return;
            cameraViewport = viewport;
            tabletopViewport = new Rect(0f, 0f, 1f, 1f);
            FitCameraToMap(true);
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

        private bool CanStartNavigation(Vector2 position) =>
            MapCameraGeometry.IsScreenPointInCameraViewport(targetCamera, position) &&
            !TabletopPointerClassifier.IsBlockedByFlatHud(position);

        private void ReadNavigationInput()
        {
            if (Input.touchCount > 0)
            {
                ReadTouchNavigation();
                return; // 不重复消费触摸模拟的鼠标事件。
            }
            if (isPinching || waitForTouchRelease)
            {
                isPinching = waitForTouchRelease = false;
                suppressClickThroughFrame = Time.frameCount + 1;
                EndDrag();
            }
            var position = (Vector2)Input.mousePosition;
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
            {
                mouseDragButton = Input.GetMouseButtonDown(0) ? 0 : 1;
                BeginPointer(position);
            }
            if (pointerPending && Input.GetMouseButton(mouseDragButton)) MovePointer(position);
            if (pointerPending && !Input.GetMouseButton(mouseDragButton)) EndDrag();
            var wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > InputEpsilon && CanStartNavigation(position))
                SetZoomTarget(position, targetZoom + wheel * wheelZoomStep);
        }

        private void BeginPointer(Vector2 position)
        {
            EndDrag();
            if (!CanStartNavigation(position) || !MapCameraGeometry.TryScreenPointToPlane(
                targetCamera, position, tabletopPlane, out dragAnchorWorld)) return;
            pointerPending = true;
            pointerStart = position;
            targetZoom = currentZoom;
            hasZoomAnchor = false;
        }

        private void MovePointer(Vector2 position)
        {
            if (!pointerPending) return;
            if (!CanStartNavigation(position)) { EndDrag(); return; }
            var threshold = UnityEngine.EventSystems.EventSystem.current == null ? 6f :
                UnityEngine.EventSystems.EventSystem.current.pixelDragThreshold;
            if (!isDragging && (position - pointerStart).sqrMagnitude < threshold * threshold) return;
            isDragging = true;
            suppressClickThroughFrame = Time.frameCount + 1;
            UpdateDrag(position);
        }

        private void SetZoomTarget(Vector2 position, float zoom)
        {
            targetZoom = Mathf.Clamp(zoom, minimumNavigationZoom, maxZoom);
            zoomAnchorScreen = position;
            hasZoomAnchor = MapCameraGeometry.TryScreenPointToPlane(
                targetCamera, position, tabletopPlane, out _);
        }

        private void ReadTouchNavigation()
        {
            if (Input.touchCount >= 2)
            {
                if (!isPinching)
                {
                    var a = Input.GetTouch(0); var b = Input.GetTouch(1);
                    if (waitForTouchRelease || !CanStartNavigation(a.position) || !CanStartNavigation(b.position))
                    { waitForTouchRelease = true; EndDrag(); return; }
                    EndDrag();
                    isPinching = true;
                    pinchFingerA = a.fingerId; pinchFingerB = b.fingerId;
                    previousPinchCenter = (a.position + b.position) * .5f;
                    previousPinchDistance = Vector2.Distance(a.position, b.position);
                    targetZoom = currentZoom;
                }
                Touch first = default, second = default;
                var foundA = false; var foundB = false;
                for (var i = 0; i < Input.touchCount; i++)
                {
                    var touch = Input.GetTouch(i);
                    if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled) continue;
                    if (touch.fingerId == pinchFingerA) { first = touch; foundA = true; }
                    if (touch.fingerId == pinchFingerB) { second = touch; foundB = true; }
                }
                if (!foundA || !foundB || !CanContinuePinch(first.position) || !CanContinuePinch(second.position))
                { isPinching = false; waitForTouchRelease = true; return; }
                ApplyPinch(first.position, second.position);
                return;
            }
            if (isPinching) { isPinching = false; waitForTouchRelease = true; }
            if (waitForTouchRelease) return;
            var single = Input.GetTouch(0);
            if (single.phase == TouchPhase.Began) BeginPointer(single.position);
            else if (single.phase == TouchPhase.Moved || single.phase == TouchPhase.Stationary) MovePointer(single.position);
            else EndDrag();
        }

        private bool CanContinuePinch(Vector2 position)
        {
            // 开口会随缩小收缩；已捕获的双指仍可留在原地图区域，避免缩放自行中断。
            var availablePixels = new Rect(cameraViewport.x * Screen.width, cameraViewport.y * Screen.height,
                cameraViewport.width * Screen.width, cameraViewport.height * Screen.height);
            return availablePixels.Contains(position) && !TabletopPointerClassifier.IsBlockedByFlatHud(position);
        }

        private void ApplyPinch(Vector2 first, Vector2 second)
        {
            var center = (first + second) * .5f;
            var distance = Vector2.Distance(first, second);
            if (previousPinchDistance > 1f && distance > 1f)
            {
                if (MapCameraGeometry.TryScreenPointToPlane(targetCamera, previousPinchCenter,
                    tabletopPlane, out dragAnchorWorld)) UpdateDrag(center);
                SetZoomTarget(center, targetZoom * distance / previousPinchDistance);
            }
            previousPinchCenter = center;
            previousPinchDistance = distance;
            suppressClickThroughFrame = Time.frameCount + 1;
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
            currentZoom = Mathf.Clamp(currentZoom, minimumNavigationZoom, maxZoom);
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

            var safeZoom = Mathf.Max(minimumNavigationZoom, currentZoom);
            var distance = MapCameraGeometry.CalculateZoomedDistance(baseDistance, safeZoom);
            VisibleViewportInRegion = MapCameraGeometry.CalculateContainedViewport(
                mapSize, layoutAspect, distance, fieldOfView);
            var opening = VisibleViewportInRegion;
            targetCamera.rect = new Rect(cameraViewport.x + opening.x * cameraViewport.width,
                cameraViewport.y + opening.y * cameraViewport.height,
                cameraViewport.width * opening.width, cameraViewport.height * opening.height);
            // 裁掉的是多余视口，不改变剩余屏幕像素对应的地图比例。
            targetCamera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(fieldOfView * Mathf.Deg2Rad * .5f) *
                opening.height) * Mathf.Rad2Deg;
            var cameraPosition = MapCameraGeometry.CalculateCameraPosition(
                focusPoint,
                cameraRotation,
                MapCameraGeometry.CalculateZoomedDistance(baseDistance, safeZoom));
            targetCamera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
            // 每次距离改变后用新视口收紧焦点，避免边缘缩小时短暂露底。
            focusPoint = MapCameraGeometry.ClampPlanarPosition(
                focusPoint, panOrigin, panAxisX, panAxisY, GetCurrentPanBounds());
            targetCamera.transform.position = MapCameraGeometry.CalculateCameraPosition(
                focusPoint, cameraRotation,
                MapCameraGeometry.CalculateZoomedDistance(baseDistance, safeZoom));
        }

        private void EndDrag()
        {
            if (isDragging) suppressClickThroughFrame = Time.frameCount + 1;
            pointerPending = false;
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

                currentZoom = Mathf.Clamp(value, minimumNavigationZoom, maxZoom);
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
