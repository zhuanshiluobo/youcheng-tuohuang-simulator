using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class EnterpriseTargetView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private UiSelectionState state;
        [SerializeField] private RectTransform badge;
        [SerializeField] private GameObject stateFrame, unavailableFill, checkBacking;
        [SerializeField] private Vector2 badgeSize;
        [SerializeField] private float badgeOutsideOffset, companyFrameOutset;
        public void Bind(Rect area, bool available, bool selected, bool pending, Action choose,
            bool readOnly = false, RectTransform boardBounds = null, bool company = false,
            Action details = null, Func<bool> canPreview = null)
        {
            var rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(area.xMin, 1 - area.yMax);
            rect.anchorMax = new Vector2(area.xMax, 1 - area.yMin);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            state.SetState(!available ? UiSelectionVisualState.Unavailable : selected
                ? (pending ? UiSelectionVisualState.Pending : UiSelectionVisualState.Selected) : UiSelectionVisualState.Available);
            if (stateFrame != null) stateFrame.SetActive(!readOnly);
            if (unavailableFill != null) unavailableFill.SetActive(!readOnly && !available);
            if (checkBacking != null) checkBacking.SetActive(!readOnly && selected && !pending);
            if (badge != null && boardBounds != null)
            {
                badge.gameObject.SetActive(!readOnly && (!available || selected && !pending));
                // 用板内比例定位，避免首次布局尚未完成时读取到零宽度。
                badge.anchorMin = badge.anchorMax = new Vector2((1 - area.x) / area.width, 1);
                badge.pivot = new Vector2(0, 1);
                badge.sizeDelta = badgeSize;
                badge.anchoredPosition = new Vector2(badgeOutsideOffset, -badgeOutsideOffset);
                if (checkBacking != null)
                {
                    var backing = (RectTransform)checkBacking.transform;
                    backing.anchorMin = backing.anchorMax = badge.anchorMin;
                    backing.pivot = badge.pivot;
                    backing.sizeDelta = badge.sizeDelta;
                    backing.anchoredPosition = badge.anchoredPosition;
                }
            }
            if (stateFrame != null)
            {
                var frame = stateFrame.transform as RectTransform;
                var outset = company ? companyFrameOutset : 0;
                frame.offsetMin = new Vector2(-outset, -outset);
                frame.offsetMax = new Vector2(outset, outset);
            }
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => choose?.Invoke());
            // 不可选仍可查看原因；资格守卫在草稿及正式宿主。
            button.interactable = !pending;
            if (button is CardPickerCardButton gestures)
                gestures.ConfigurePreview(details, canPreview);
        }
    }
}
