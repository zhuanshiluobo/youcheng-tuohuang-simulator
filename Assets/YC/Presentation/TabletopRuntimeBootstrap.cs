using UnityEngine;

namespace YC.Presentation
{
    internal static class TabletopRuntimeBootstrap
    {
        public static bool TryConfigure(
            TabletopCanvasLayout layout,
            Camera targetCamera,
            SpriteRenderer mapRenderer,
            Behaviour owner)
        {
            var reason = layout == null ? "缺少 TabletopCanvasLayout。" : string.Empty;
            if (layout == null || !layout.Configure(targetCamera, mapRenderer, out reason))
            {
                Debug.LogError("[TabletopRuntimeBootstrap] 桌面俯视层配置失败：" + reason, owner);
                Disable(owner);
                return false;
            }

            var display = mapRenderer == null ? null : mapRenderer.GetComponent<MapDisplayController>();
            if (display == null && mapRenderer != null)
            {
                display = mapRenderer.GetComponentInParent<MapDisplayController>();
            }

            if (display == null)
            {
                Debug.LogError("[TabletopRuntimeBootstrap] 地图缺少 MapDisplayController。", owner);
                Disable(owner);
                return false;
            }

            display.FitCameraToMap();
            return true;
        }

        private static void Disable(Behaviour owner)
        {
            if (owner != null)
            {
                owner.enabled = false;
            }
        }
    }
}
