using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class EnterpriseTargetView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private UiSelectionState state;
        public void Bind(Rect area, bool available, bool selected, bool pending, Action choose)
        {
            var rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(area.xMin, 1 - area.yMax);
            rect.anchorMax = new Vector2(area.xMax, 1 - area.yMin);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            state.SetState(!available ? UiSelectionVisualState.Unavailable : selected
                ? (pending ? UiSelectionVisualState.Pending : UiSelectionVisualState.Selected) : UiSelectionVisualState.Available);
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => choose?.Invoke());
            // 不可选仍可查看原因；资格守卫在草稿及正式宿主。
            button.interactable = !pending;
        }
    }
}
