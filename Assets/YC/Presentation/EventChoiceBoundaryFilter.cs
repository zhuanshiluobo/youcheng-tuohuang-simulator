using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>卡面区间从顶部闭合、到底部开口；相邻按钮在共同边界不会同时命中。</summary>
    public sealed class EventChoiceBoundaryFilter : MonoBehaviour, ICanvasRaycastFilter
    {
        private RectTransform artworkHost;
        private float lowerY, upperY;

        public void Configure(RectTransform host, float lower, float upper)
        {
            artworkHost = host;
            lowerY = lower;
            upperY = upper;
        }

        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            if (artworkHost == null || artworkHost.rect.height <= 0f ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(artworkHost, screenPoint, eventCamera, out var local))
                return false;
            // 两个相邻按钮共用同一容器及同一计算式，消除各自矩形变换的舍入差。
            float normalizedY = (local.y - artworkHost.rect.yMin) / artworkHost.rect.height;
            return normalizedY > lowerY && normalizedY <= upperY;
        }
    }
}
