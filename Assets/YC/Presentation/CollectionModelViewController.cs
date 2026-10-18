using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class CollectionModelViewController : MonoBehaviour, IScrollHandler
    {
        [Serializable]
        private sealed class AxisView
        {
            public RectTransform line;
            public RectTransform tip;
            public RectTransform label;
        }

        [Header("收藏室已有组件")]
        [SerializeField] private CollectionRoomController room;
        [SerializeField] private Transform modelPivot;
        [SerializeField] private Camera previewCamera;
        [SerializeField] private LoadingModelDragController dragController;
        [SerializeField] private Slider horizontalSlider;
        [SerializeField] private Slider verticalSlider;
        [SerializeField] private Slider rollSlider;
        [SerializeField] private Text horizontalAngleLabel;
        [SerializeField] private Text verticalAngleLabel;
        [SerializeField] private Text rollAngleLabel;
        [SerializeField] private CanvasGroup horizontalHint;
        [SerializeField] private CanvasGroup verticalHint;
        [SerializeField] private CanvasGroup rollHint;
        [SerializeField] private CanvasGroup zoomHint;
        [SerializeField] private Text zoomLabel;
        [SerializeField] private AxisView[] axes;
        [Header("交互与动态文案")]
        [SerializeField, Range(0.1f, 1f)] private float minimumZoom = 0.5f;
        [SerializeField, Min(1.01f)] private float wheelZoomFactor = 1.1f;
        [SerializeField, Min(0)] private float hintHoldSeconds = 0.3f;
        [SerializeField, Min(0.01f)] private float hintFadeSeconds = 0.18f;
        [SerializeField] private string zoomFormat = "缩放大小：{0:0}%";
        [SerializeField] private string angleFormat = "{0:0}°";

        private GameObject boundModel;
        private Transform poseTransform;
        private Quaternion poseRelativeToPivot;
        private Quaternion presetPivotRotation;
        private Quaternion lastPivotRotation;
        private Quaternion frontRotation;
        private Vector3 originalScale;
        private Vector3 viewEuler;
        private float zoomMultiplier = 1f;
        private float horizontalHintTime = float.NegativeInfinity;
        private float verticalHintTime = float.NegativeInfinity;
        private float rollHintTime = float.NegativeInfinity;
        private float zoomHintTime = float.NegativeInfinity;
        private float axisLength;
        private Vector2 axisDepthProjection;
        private bool horizontalHeld;
        private bool verticalHeld;
        private bool rollHeld;
        private int heldFinger = -1;

        public float ZoomMultiplier => zoomMultiplier;
        public Vector2 ViewAngles => new Vector2(viewEuler.y, viewEuler.x);
        public float RollAngle => viewEuler.z;
        public Transform PoseTransform => poseTransform;
        public Quaternion StandardViewRotation => previewCamera.transform.rotation * frontRotation;
        private Quaternion ViewRotation => Quaternion.Inverse(previewCamera.transform.rotation)
            * poseTransform.rotation * Quaternion.Inverse(frontRotation);
        private bool CanInteract => isActiveAndEnabled && poseTransform != null && room != null && room.isActiveAndEnabled
            && room.CurrentModel == boundModel && !room.IsSwitching;

        private void Awake()
        {
            originalScale = modelPivot.localScale;
            horizontalSlider.onValueChanged.AddListener(OnHorizontalChanged);
            verticalSlider.onValueChanged.AddListener(OnVerticalChanged);
            rollSlider.onValueChanged.AddListener(OnRollChanged);
            // 图形比例取自已编辑的场景，不在运行时重建坐标轴。
            axisLength = axes[0].tip.anchoredPosition.magnitude;
            axisDepthProjection = axes[2].tip.anchoredPosition / Mathf.Max(axisLength, 0.001f);
            HideHints();
        }

        public void BindModel(GameObject model, string posePath, Vector3 frontViewEuler)
        {
            boundModel = model;
            poseTransform = model == null ? null : string.IsNullOrEmpty(posePath)
                ? model.transform : model.transform.Find(posePath);
            frontRotation = Quaternion.Euler(frontViewEuler);
            zoomMultiplier = 1f;
            modelPivot.localScale = originalScale;
            presetPivotRotation = modelPivot.localRotation;
            horizontalHeld = verticalHeld = rollHeld = false;
            heldFinger = -1;
            HideHints();
            UpdateZoomLabel();
            if (poseTransform == null)
            {
                SetInteractionEnabled(false);
                return;
            }
            poseRelativeToPivot = Quaternion.Inverse(modelPivot.rotation) * poseTransform.rotation;
            viewEuler = SignedEuler(ViewRotation);
            SyncSliders();
            lastPivotRotation = modelPivot.rotation;
            UpdateAxes();
        }

        public void SetInteractionEnabled(bool available)
        {
            available &= poseTransform != null;
            horizontalSlider.interactable = available;
            verticalSlider.interactable = available;
            rollSlider.interactable = available;
            if (!available)
            {
                horizontalHeld = verticalHeld = rollHeld = false;
                heldFinger = -1;
                HideHints();
            }
        }

        private void OnHorizontalChanged(float angle)
        {
            if (!CanInteract) { SyncSliders(); return; }
            viewEuler.y = angle;
            ApplyViewRotation();
            ShowHint(horizontalHint, ref horizontalHintTime);
        }

        private void OnVerticalChanged(float angle)
        {
            if (!CanInteract) { SyncSliders(); return; }
            viewEuler.x = angle;
            ApplyViewRotation();
            ShowHint(verticalHint, ref verticalHintTime);
        }

        private void OnRollChanged(float angle)
        {
            if (!CanInteract) { SyncSliders(); return; }
            viewEuler.z = angle;
            ApplyViewRotation();
            ShowHint(rollHint, ref rollHintTime);
        }

        private void ApplyViewRotation()
        {
            dragController.StopMotion();
            // 翻转位于组合最外层，始终绕屏幕视线旋转。
            var rotation = Quaternion.AngleAxis(viewEuler.z, Vector3.forward)
                * Quaternion.AngleAxis(viewEuler.y, Vector3.up) * Quaternion.AngleAxis(viewEuler.x, Vector3.right);
            modelPivot.rotation = previewCamera.transform.rotation * rotation * frontRotation
                * Quaternion.Inverse(poseRelativeToPivot);
            lastPivotRotation = modelPivot.rotation;
            UpdateAngleLabels();
            UpdateAxes();
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!CanInteract || Mathf.Approximately(eventData.scrollDelta.y, 0)) return;
            ApplyScroll(eventData.scrollDelta.y);
            eventData.Use();
        }

        public void ApplyScroll(float steps)
        {
            if (!CanInteract) return;
            SetZoom(zoomMultiplier * Mathf.Pow(wheelZoomFactor, steps));
        }

        public void SetZoom(float multiplier)
        {
            if (!CanInteract) return;
            zoomMultiplier = Mathf.Clamp(multiplier, minimumZoom, 2f);
            modelPivot.localScale = originalScale * zoomMultiplier;
            UpdateZoomLabel();
            ShowHint(zoomHint, ref zoomHintTime);
        }

        public void ResetToPreset()
        {
            if (!CanInteract) return;
            dragController.StopMotion();
            modelPivot.localRotation = presetPivotRotation;
            modelPivot.localScale = originalScale;
            zoomMultiplier = 1f;
            viewEuler = SignedEuler(ViewRotation);
            SyncSliders();
            UpdateZoomLabel();
            lastPivotRotation = modelPivot.rotation;
            horizontalHeld = verticalHeld = rollHeld = false;
            heldFinger = -1;
            HideHints();
            UpdateAxes();
        }

        private void Update()
        {
            if (!CanInteract) return;
            UpdateSliderHold();
        }

        private void LateUpdate()
        {
            if (!CanInteract) return;
            // 原有自由拖转和惯性仍可使用，读数与三轴同步真实姿态。
            if (Quaternion.Angle(lastPivotRotation, modelPivot.rotation) > 0.001f)
            {
                var next = SignedEuler(ViewRotation, viewEuler.z);
                // 同一旋转有两组欧拉角，选择接近上一帧的一组，跨过顶视图时不跳回。
                var alternate = new Vector3(SignedAngle(next.x + 180f), SignedAngle(180f - next.y), SignedAngle(next.z + 180f));
                viewEuler = EulerDistance(viewEuler, alternate) < EulerDistance(viewEuler, next) ? alternate : next;
                SyncSliders();
                lastPivotRotation = modelPivot.rotation;
                UpdateAxes();
            }
            FadeHint(horizontalHint, horizontalHintTime, horizontalHeld);
            FadeHint(verticalHint, verticalHintTime, verticalHeld);
            FadeHint(rollHint, rollHintTime, rollHeld);
            FadeHint(zoomHint, zoomHintTime, false);
        }

        private void UpdateSliderHold()
        {
            if (Input.touchCount > 0)
            {
                foreach (var touch in Input.touches)
                {
                    if (touch.phase == TouchPhase.Began && heldFinger < 0)
                    {
                        BeginSliderHold(touch.position);
                        if (horizontalHeld || verticalHeld || rollHeld) heldFinger = touch.fingerId;
                    }
                    if (touch.fingerId == heldFinger && (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled))
                        EndSliderHold();
                }
                return;
            }
            if (Input.GetMouseButtonDown(0)) BeginSliderHold(Input.mousePosition);
            if (!Input.GetMouseButton(0) && (horizontalHeld || verticalHeld || rollHeld)) EndSliderHold();
        }

        private void BeginSliderHold(Vector2 screenPosition)
        {
            var canvas = horizontalSlider.GetComponentInParent<Canvas>();
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            horizontalHeld = RectTransformUtility.RectangleContainsScreenPoint((RectTransform)horizontalSlider.transform, screenPosition, camera);
            verticalHeld = RectTransformUtility.RectangleContainsScreenPoint((RectTransform)verticalSlider.transform, screenPosition, camera);
            rollHeld = RectTransformUtility.RectangleContainsScreenPoint((RectTransform)rollSlider.transform, screenPosition, camera);
            if (horizontalHeld) ShowHint(horizontalHint, ref horizontalHintTime);
            if (verticalHeld) ShowHint(verticalHint, ref verticalHintTime);
            if (rollHeld) ShowHint(rollHint, ref rollHintTime);
        }

        private void EndSliderHold()
        {
            if (horizontalHeld) horizontalHintTime = Time.unscaledTime;
            if (verticalHeld) verticalHintTime = Time.unscaledTime;
            if (rollHeld) rollHintTime = Time.unscaledTime;
            horizontalHeld = verticalHeld = rollHeld = false;
            heldFinger = -1;
        }

        private void SyncSliders()
        {
            horizontalSlider.SetValueWithoutNotify(viewEuler.y);
            verticalSlider.SetValueWithoutNotify(viewEuler.x);
            rollSlider.SetValueWithoutNotify(viewEuler.z);
            UpdateAngleLabels();
        }

        private void UpdateAngleLabels()
        {
            horizontalAngleLabel.text = string.Format(angleFormat, viewEuler.y);
            verticalAngleLabel.text = string.Format(angleFormat, viewEuler.x);
            rollAngleLabel.text = string.Format(angleFormat, viewEuler.z);
        }

        private void UpdateZoomLabel() => zoomLabel.text = string.Format(zoomFormat, zoomMultiplier * 100f);

        private static void ShowHint(CanvasGroup hint, ref float changedAt)
        {
            changedAt = Time.unscaledTime;
            hint.alpha = 1f;
        }

        private void FadeHint(CanvasGroup hint, float changedAt, bool held)
        {
            hint.alpha = held ? 1f : Mathf.Clamp01(1f - (Time.unscaledTime - changedAt - hintHoldSeconds) / hintFadeSeconds);
        }

        private void HideHints()
        {
            horizontalHintTime = verticalHintTime = rollHintTime = zoomHintTime = float.NegativeInfinity;
            horizontalHint.alpha = verticalHint.alpha = rollHint.alpha = zoomHint.alpha = 0f;
        }

        private void UpdateAxes()
        {
            var rotation = ViewRotation;
            for (int i = 0; i < axes.Length; i++)
            {
                var vector = rotation * (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
                // 场景中的正视三箭头定义斜投影基底，保留深度方向的可读性。
                var direction = new Vector2(vector.x, vector.y) + axisDepthProjection * vector.z;
                // 保留投影长度差，让近共线的箭头错开；深度轴也不缩成一个点。
                var projected = direction.sqrMagnitude > 0.0001f
                    ? direction.normalized * Mathf.Clamp(direction.magnitude * axisLength, axisLength * 0.45f, axisLength)
                    : axes[i].tip.anchoredPosition;
                var angle = Mathf.Atan2(projected.y, projected.x) * Mathf.Rad2Deg;
                axes[i].line.anchoredPosition = projected * 0.5f;
                axes[i].line.sizeDelta = new Vector2(projected.magnitude, axes[i].line.sizeDelta.y);
                axes[i].line.localRotation = Quaternion.Euler(0, 0, angle);
                axes[i].tip.anchoredPosition = projected;
                axes[i].tip.localRotation = Quaternion.Euler(0, 0, angle);
                axes[i].label.localRotation = Quaternion.Euler(0, 0, -angle);
            }
        }

        private static float SignedAngle(float angle) => Mathf.DeltaAngle(0f, angle);
        private static Vector3 SignedEuler(Quaternion rotation, float previousRoll = 0f)
        {
            var right = rotation * Vector3.right;
            var up = rotation * Vector3.up;
            var forward = rotation * Vector3.forward;
            var yaw = Mathf.Asin(Mathf.Clamp(-right.z, -1f, 1f));
            if (Mathf.Abs(Mathf.Cos(yaw)) < 0.0001f)
            {
                // 横向恰好到侧视时两轴等价，保留上次翻转角避免读数跳变。
                var pitch = Mathf.Atan2(Mathf.Sign(yaw) * up.x, up.y) * Mathf.Rad2Deg
                    + Mathf.Sign(yaw) * previousRoll;
                return new Vector3(SignedAngle(pitch), yaw * Mathf.Rad2Deg, previousRoll);
            }
            return new Vector3(Mathf.Atan2(up.z, forward.z) * Mathf.Rad2Deg,
                yaw * Mathf.Rad2Deg, Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg);
        }
        private static float EulerDistance(Vector3 a, Vector3 b) => Mathf.Abs(Mathf.DeltaAngle(a.x, b.x))
            + Mathf.Abs(Mathf.DeltaAngle(a.y, b.y)) + Mathf.Abs(Mathf.DeltaAngle(a.z, b.z));

        private void OnDisable()
        {
            horizontalHeld = verticalHeld = rollHeld = false;
            heldFinger = -1;
            if (horizontalHint != null) HideHints();
        }

        private void OnDestroy()
        {
            if (horizontalSlider != null) horizontalSlider.onValueChanged.RemoveListener(OnHorizontalChanged);
            if (verticalSlider != null) verticalSlider.onValueChanged.RemoveListener(OnVerticalChanged);
            if (rollSlider != null) rollSlider.onValueChanged.RemoveListener(OnRollChanged);
        }
    }
}
