using UnityEngine;

namespace YC.Presentation
{
    public sealed class RulebookViewerController : MonoBehaviour
    {
        [SerializeField] private RulebookViewerView view;

        private bool initialized;
        private int pageIndex;

        private void Awake()
        {
            TryInitialize();
        }

        public void Open()
        {
            if (!TryInitialize())
            {
                return;
            }

            view.ImageViewer.Open(pageIndex);
        }

        public void Close()
        {
            if (initialized)
            {
                pageIndex = view.ImageViewer.PageIndex;
                view.ImageViewer.Close();
            }
        }

        private bool TryInitialize()
        {
            if (initialized)
            {
                return true;
            }

            var reason = "View 未绑定。";
            if (view == null || !view.TryValidateConfiguration(out reason))
            {
                Debug.LogError(
                    "RulebookViewerController 缺少完整编辑器 View 引用：" +
                    reason,
                    this);
                enabled = false;
                return false;
            }

            // 规则书内容边界：28 张图片由 View 的序列化数组绑定，不在运行时查找资源。
            view.ImageViewer.Configure("Rulebook", "规则书", view.PageCount, view.GetPage);
            view.ImageViewer.ConfigureActions(string.Empty, null);
            view.ImageViewer.DisableReferenceCollapse();
            initialized = true;
            return true;
        }
    }
}
