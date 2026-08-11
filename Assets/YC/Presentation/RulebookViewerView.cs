using UnityEngine;

namespace YC.Presentation
{
    public sealed class RulebookViewerView : MonoBehaviour
    {
        [SerializeField] private ZoomableImageViewerController imageViewer;
        [SerializeField] private Texture2D[] pages = new Texture2D[0];

        public ZoomableImageViewerController ImageViewer => imageViewer;
        public int PageCount => pages == null ? 0 : pages.Length;

        public Texture2D GetPage(int index)
        {
            return pages != null && index >= 0 && index < pages.Length ? pages[index] : null;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (imageViewer == null)
            {
                reason = "规则书缺少序列化图片查看器。";
                return false;
            }

            if (pages == null || pages.Length == 0)
            {
                reason = "规则书页资源数组为空。";
                return false;
            }

            for (var i = 0; i < pages.Length; i++)
            {
                if (pages[i] != null)
                {
                    continue;
                }

                reason = "规则书第 " + (i + 1) + " 页资源未绑定。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
