using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>在原参考画布适配之上整体放大窗口，只以物理屏幕安全区约束尺寸。</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(RectTransform))]
    public sealed class CardPickerPageScale : UIBehaviour, ILayoutSelfController
    {
        [SerializeField, Min(.01f)] private float requestedScale = 1.2f;
        private RectTransform window;
        private Canvas rootCanvas;
        public float RequestedScale => requestedScale;
        public float AppliedScale { get; private set; } = 1f;

        protected override void OnEnable()
        {
            base.OnEnable();
            window = (RectTransform)transform;
            RefreshCanvas();
            Canvas.willRenderCanvases += Apply;
            Apply();
        }

        protected override void OnDisable()
        {
            Canvas.willRenderCanvases -= Apply;
            base.OnDisable();
        }

        protected override void OnCanvasHierarchyChanged() { base.OnCanvasHierarchyChanged(); RefreshCanvas(); }
        private void RefreshCanvas() { var canvas = GetComponentInParent<Canvas>(); rootCanvas = canvas == null ? null : canvas.rootCanvas; }
        private void LateUpdate() => Apply();
        public void SetLayoutHorizontal() { }
        public void SetLayoutVertical() => Apply();

        public static float CalculateScale(Vector2 unscaledScreenSize, Rect safeArea, Vector2 screenCenter, float requested)
        {
            if (unscaledScreenSize.x <= 0f || unscaledScreenSize.y <= 0f) return 1f;
            var width = 2f * Mathf.Max(0f, Mathf.Min(screenCenter.x - safeArea.xMin, safeArea.xMax - screenCenter.x));
            var height = 2f * Mathf.Max(0f, Mathf.Min(screenCenter.y - safeArea.yMin, safeArea.yMax - screenCenter.y));
            return Mathf.Max(0f, Mathf.Min(requested, width / unscaledScreenSize.x, height / unscaledScreenSize.y));
        }

        private void Apply()
        {
            if (!IsActive() || window == null || !(window.parent is RectTransform parent) || rootCanvas == null) return;
            // 无相机的世界空间夹具没有物理屏幕映射；不能把它当作实际游戏窗口。
            var camera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
            if (rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay && camera == null) return;
            var center = new Vector2(Screen.width * .5f, Screen.height * .5f);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, center, camera, out var localCenter)) return;
            var size = window.rect.size;
            Vector2 ScreenPoint(Vector2 offset) => RectTransformUtility.WorldToScreenPoint(camera, parent.TransformPoint(localCenter + offset));
            var baselineSize = new Vector2(
                Vector2.Distance(ScreenPoint(new Vector2(-size.x * .5f, 0f)), ScreenPoint(new Vector2(size.x * .5f, 0f))),
                Vector2.Distance(ScreenPoint(new Vector2(0f, -size.y * .5f)), ScreenPoint(new Vector2(0f, size.y * .5f))));
            var safe = Screen.safeArea;
            if (safe.width <= 0f || safe.height <= 0f) safe = new Rect(0f, 0f, Screen.width, Screen.height);
            AppliedScale = CalculateScale(baselineSize, safe, center, requestedScale);
            var scale = new Vector3(AppliedScale, AppliedScale, 1f);
            if ((window.localScale - scale).sqrMagnitude > .00000001f) window.localScale = scale;
            var position = new Vector3(localCenter.x, localCenter.y, window.localPosition.z);
            if ((window.localPosition - position).sqrMagnitude > .00000001f) window.localPosition = position;
        }
    }
}
