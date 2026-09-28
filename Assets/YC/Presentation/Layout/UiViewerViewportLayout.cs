using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    /// <summary>只在图片 viewport 改变时重新拟合内部图像，不移动窗口。</summary>
    public sealed class UiViewerViewportLayout : UIBehaviour
    {
        [SerializeField] private ZoomableImageViewerController viewer;
        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (viewer != null) viewer.RefreshViewportGeometry();
        }
#if UNITY_EDITOR
        public void ConfigureForEditor(ZoomableImageViewerController value) { viewer = value; }
#endif
    }
}
