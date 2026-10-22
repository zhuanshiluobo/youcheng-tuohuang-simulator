using UnityEngine;
using UnityEngine.EventSystems;

namespace YC.Presentation
{
    /// <summary>只读内容也消费按下与抬起，避免冒泡至空白关闭层。</summary>
    public sealed class InformationContentInput : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        public void OnPointerDown(PointerEventData e) { }
        public void OnPointerUp(PointerEventData e) { }
        public void OnPointerClick(PointerEventData e) { }
    }
}
