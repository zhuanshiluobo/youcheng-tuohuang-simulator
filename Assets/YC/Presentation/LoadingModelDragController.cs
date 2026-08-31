using UnityEngine;

namespace YC.Presentation
{
    [RequireComponent(typeof(RectTransform))]
    public sealed class LoadingModelDragController : MonoBehaviour
    {
        private const int NoFinger = int.MinValue;
        private const float MaximumInputFrameStep = 1f / 20f;

        [SerializeField] private Transform rotationTarget;
        [SerializeField, Min(0.01f)] private float degreesPerPixel = 0.28f;
        [SerializeField, Min(0.1f)] private float velocityResponse = 16f;
        [SerializeField, Min(1f)] private float maximumInertiaSpeed = 180f;
        [SerializeField, Min(0.1f)] private float inertiaDamping = 6.5f;
        [SerializeField, Min(0.01f)] private float stopSpeed = 1.5f;
        [SerializeField, Min(0.01f)] private float inertiaReleaseWindow = 0.06f;

        private RectTransform interactionArea;
        private Vector2 previousPointerPosition;
        private Vector2 angularVelocity;
        private int activeFingerId = NoFinger;
        private bool dragging;
        private float lastMotionTime = float.NegativeInfinity;

        public Transform RotationTarget => rotationTarget;
        public Vector2 AngularVelocity => angularVelocity;

        private void Awake()
        {
            interactionArea = (RectTransform)transform;
            if (rotationTarget == null)
            {
                Debug.LogError("LoadingModelDragController 缺少旋转目标。", this);
                enabled = false;
            }
        }

        private void Update()
        {
            var deltaTime = Mathf.Min(Time.unscaledDeltaTime, MaximumInputFrameStep);
            var touchHandled = HandleTouch(deltaTime);
            if (!touchHandled)
            {
                HandleMouse(deltaTime);
            }

            if (!dragging)
            {
                ApplyInertia(deltaTime);
            }
        }

        private bool HandleTouch(float deltaTime)
        {
            if (Input.touchCount == 0)
            {
                if (activeFingerId != NoFinger)
                {
                    EndDrag();
                    return true;
                }

                return false;
            }

            if (activeFingerId == NoFinger)
            {
                for (var index = 0; index < Input.touchCount; index++)
                {
                    var touch = Input.GetTouch(index);
                    if (touch.phase != TouchPhase.Began || !Contains(touch.position))
                    {
                        continue;
                    }

                    activeFingerId = touch.fingerId;
                    BeginDrag(touch.position);
                    return true;
                }

                return true;
            }

            for (var index = 0; index < Input.touchCount; index++)
            {
                var touch = Input.GetTouch(index);
                if (touch.fingerId != activeFingerId)
                {
                    continue;
                }

                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    EndDrag();
                }
                else if (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
                {
                    ContinueDrag(touch.position, deltaTime);
                }

                return true;
            }

            EndDrag();
            return true;
        }

        private void HandleMouse(float deltaTime)
        {
            var pointerPosition = (Vector2)Input.mousePosition;
            if (Input.GetMouseButtonDown(0) && Contains(pointerPosition))
            {
                BeginDrag(pointerPosition);
            }

            if (dragging && Input.GetMouseButton(0))
            {
                ContinueDrag(pointerPosition, deltaTime);
            }

            if (dragging && Input.GetMouseButtonUp(0))
            {
                EndDrag();
            }
        }

        private bool Contains(Vector2 screenPosition)
        {
            return RectTransformUtility.RectangleContainsScreenPoint(interactionArea, screenPosition, null);
        }

        private void BeginDrag(Vector2 screenPosition)
        {
            dragging = true;
            previousPointerPosition = screenPosition;
            angularVelocity = Vector2.zero;
            lastMotionTime = float.NegativeInfinity;
        }

        private void ContinueDrag(Vector2 screenPosition, float deltaTime)
        {
            var pointerDelta = screenPosition - previousPointerPosition;
            previousPointerPosition = screenPosition;
            if (pointerDelta.sqrMagnitude <= Mathf.Epsilon)
            {
                if (Time.unscaledTime - lastMotionTime > inertiaReleaseWindow)
                {
                    angularVelocity = Vector2.zero;
                }

                return;
            }

            var rotationDelta = new Vector2(pointerDelta.y, -pointerDelta.x) * degreesPerPixel;
            ApplyRotation(rotationDelta);
            lastMotionTime = Time.unscaledTime;

            var safeDeltaTime = Mathf.Max(deltaTime, 0.001f);
            var instantVelocity = rotationDelta / safeDeltaTime;
            var response = 1f - Mathf.Exp(-velocityResponse * safeDeltaTime);
            angularVelocity = Vector2.Lerp(angularVelocity, instantVelocity, response);
            angularVelocity = Vector2.ClampMagnitude(angularVelocity, maximumInertiaSpeed);
        }

        private void EndDrag()
        {
            dragging = false;
            activeFingerId = NoFinger;
            if (Time.unscaledTime - lastMotionTime > inertiaReleaseWindow)
            {
                angularVelocity = Vector2.zero;
            }
        }

        private void ApplyInertia(float deltaTime)
        {
            if (angularVelocity.sqrMagnitude <= stopSpeed * stopSpeed)
            {
                angularVelocity = Vector2.zero;
                return;
            }

            ApplyRotation(angularVelocity * deltaTime);
            angularVelocity *= Mathf.Exp(-inertiaDamping * deltaTime);
        }

        private void ApplyRotation(Vector2 degrees)
        {
            rotationTarget.Rotate(Vector3.up, degrees.y, Space.World);
            rotationTarget.Rotate(Vector3.right, degrees.x, Space.World);
        }

        private void OnDisable()
        {
            StopMotion();
        }

        public void StopMotion()
        {
            dragging = false;
            activeFingerId = NoFinger;
            angularVelocity = Vector2.zero;
            lastMotionTime = float.NegativeInfinity;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(Transform target)
        {
            rotationTarget = target;
        }
#endif
    }
}
