using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    [DisallowMultipleComponent]
    public sealed class TabletopCanvasLayout : MonoBehaviour
    {
        public static readonly Vector2 ReferenceResolution = Vector2.right * 1920f + Vector2.up * 1080f;

        [SerializeField] private Canvas canvas;
        [SerializeField] private RectTransform rectTransform;
        [SerializeField] private GraphicRaycaster graphicRaycaster;
        [SerializeField] private CanvasScaler canvasScaler;

        public Canvas Canvas => canvas;
        public RectTransform CanvasRectTransform => rectTransform;
        public GraphicRaycaster GraphicRaycaster => graphicRaycaster;
        public CanvasScaler CanvasScaler => canvasScaler;
        public float WorldUnitsPerPixel { get; private set; }

        public bool TryValidateConfiguration(out string reason)
        {
            if (canvas == null || rectTransform == null)
            {
                reason = "桌面 Canvas 缺少 Canvas 或 RectTransform 序列化引用。";
                return false;
            }

            if (canvas.transform != rectTransform)
            {
                reason = "桌面 Canvas 的 RectTransform 必须属于被配置的 Canvas。";
                return false;
            }

            if (graphicRaycaster == null)
            {
                graphicRaycaster = canvas.GetComponent<GraphicRaycaster>();
            }

            if (graphicRaycaster == null || graphicRaycaster.gameObject != canvas.gameObject)
            {
                reason = "桌面 Canvas 缺少同对象上的 GraphicRaycaster，无法保持卡牌点击交互。";
                return false;
            }

            if (canvasScaler == null)
            {
                canvasScaler = canvas.GetComponent<CanvasScaler>();
            }

            if (canvasScaler != null && canvasScaler.gameObject != canvas.gameObject)
            {
                reason = "桌面 Canvas 的 CanvasScaler 必须与 Canvas 位于同一对象。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public bool Configure(Camera targetCamera, SpriteRenderer mapRenderer)
        {
            if (Configure(targetCamera, mapRenderer, out var reason))
            {
                return true;
            }

            Debug.LogError("[TabletopCanvasLayout] 配置失败：" + reason, this);
            return false;
        }

        public bool Configure(Camera targetCamera, SpriteRenderer mapRenderer, out string reason)
        {
            if (!TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (targetCamera == null)
            {
                reason = "缺少桌面层目标相机。";
                return false;
            }

            if (mapRenderer == null || mapRenderer.sprite == null)
            {
                reason = "缺少带有效 Sprite 的地图 SpriteRenderer。";
                return false;
            }

            var mapBounds = mapRenderer.bounds;
            var worldUnitsPerPixel = mapBounds.size.y / ReferenceResolution.y;
            if (!IsFinitePositive(worldUnitsPerPixel))
            {
                reason = "地图世界高度无效，无法计算桌面 Canvas 的像素比例。";
                return false;
            }

            if (!TryGetLocalScale(worldUnitsPerPixel, rectTransform.parent, out var localScale))
            {
                reason = "桌面 Canvas 的父级世界缩放无效，无法应用统一像素比例。";
                return false;
            }

            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = targetCamera;
            rectTransform.pivot = Vector2.one * 0.5f;
            rectTransform.sizeDelta = ReferenceResolution;
            rectTransform.SetPositionAndRotation(mapBounds.center, mapRenderer.transform.rotation);
            rectTransform.localScale = localScale;

            graphicRaycaster.enabled = true;
            if (canvasScaler != null)
            {
                canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasScaler.referenceResolution = ReferenceResolution;
                canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                canvasScaler.matchWidthOrHeight = 0.5f;
            }

            WorldUnitsPerPixel = worldUnitsPerPixel;
            reason = string.Empty;
            return true;
        }

        private static bool TryGetLocalScale(
            float worldUnitsPerPixel,
            Transform parent,
            out Vector3 localScale)
        {
            if (parent == null)
            {
                localScale = Vector3.one * worldUnitsPerPixel;
                return true;
            }

            var parentScale = parent.lossyScale;
            if (!IsFiniteNonZero(parentScale.x) ||
                !IsFiniteNonZero(parentScale.y) ||
                !IsFiniteNonZero(parentScale.z))
            {
                localScale = Vector3.zero;
                return false;
            }

            localScale = new Vector3(
                worldUnitsPerPixel / Mathf.Abs(parentScale.x),
                worldUnitsPerPixel / Mathf.Abs(parentScale.y),
                worldUnitsPerPixel / Mathf.Abs(parentScale.z));
            return true;
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFiniteNonZero(float value)
        {
            return Mathf.Abs(value) > Mathf.Epsilon && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
