using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    /// <summary>只更新展开明细的显示和布局输入；原生 VLG 独占席位与城市矩形。</summary>
    public sealed class UiOpponentDetailInput : MonoBehaviour
    {
        [SerializeField] private RectTransform[] details;
        [SerializeField, Min(0)] private float minimumExpandedDetailHeight = 112f;
        [SerializeField, Min(0)] private float maximumExpandedDetailHeight = 220f;
        private int expandedSeat = -1;
        private float requestedDetailHeight;

        public void SetExpanded(int index, float preferredHeight)
        {
            if (expandedSeat == index && Mathf.Approximately(requestedDetailHeight, preferredHeight)) return;
            expandedSeat = index;
            requestedDetailHeight = preferredHeight;
            for (var i = 0; details != null && i < details.Length; i++)
            {
                if (details[i] == null) continue;
                var element = details[i].GetComponent<LayoutElement>();
                if (element != null)
                {
                    element.minHeight = minimumExpandedDetailHeight;
                    element.preferredHeight = Mathf.Clamp(preferredHeight,
                        minimumExpandedDetailHeight, maximumExpandedDetailHeight);
                    element.flexibleHeight = 0;
                }
                details[i].gameObject.SetActive(i == expandedSeat);
            }
        }
    }
}
