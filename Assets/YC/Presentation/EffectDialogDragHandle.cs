using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    /// <summary>效果对话框共用的标题栏拖动手柄。</summary>
    public sealed class EffectDialogDragHandle : MonoBehaviour
    {
        public void Configure(RectTransform configuredPanel)
        {
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData)
        {
            // 窗口位置仅由预制体与布局配置决定。
        }

        public void OnEndDrag(PointerEventData eventData)
        {
        }
    }
}
