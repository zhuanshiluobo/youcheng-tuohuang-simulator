using System;
using NoMadCity.SelectionFX;
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
        [SerializeField] private SelectionEffectGraphic companyEffect, rowEffect, departmentEffect;
        [SerializeField] private SelectionEffectInput companyInput, rowInput, departmentInput;
        public void Bind(Rect area, bool available, bool selected, bool pending, Action choose,
            bool readOnly = false, RectTransform boardBounds = null, bool company = false,
            Action details = null, Func<bool> canPreview = null, bool department = false)
        {
            var rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(area.xMin, 1 - area.yMax);
            rect.anchorMax = new Vector2(area.xMax, 1 - area.yMin);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            state.SetState(!available ? UiSelectionVisualState.Unavailable : selected
                ? (pending ? UiSelectionVisualState.Pending : UiSelectionVisualState.Selected) : UiSelectionVisualState.Available);
            bool selectedFx = !pending && selected && (department && departmentEffect != null ||
                company && !department && companyEffect != null || !company && !department && rowEffect != null);
            if (stateFrame != null)
            {
                // 特效是旧状态框的子对象：只隐藏图片，不能关闭整个对象。
                stateFrame.SetActive(!readOnly);
                var frameImage = stateFrame.GetComponent<Image>();
                if (frameImage != null) frameImage.enabled = !selectedFx;
            }
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
            void SetEffect(SelectionEffectGraphic effect, SelectionEffectInput input, bool show)
            {
                if (effect == null) return;
                if (input != null) input.enabled = show;
                effect.gameObject.SetActive(show);
                if (!show) return;
                var inset = company ? companyFrameOutset : 0f;
                effect.rectTransform.offsetMin = new Vector2(inset, inset);
                effect.rectTransform.offsetMax = new Vector2(-inset, -inset);
                effect.SetState(!available ? SelectionEffectGraphic.SelectionState.Unavailable : selected
                    ? SelectionEffectGraphic.SelectionState.Selected : SelectionEffectGraphic.SelectionState.Available);
            }
            SetEffect(companyEffect, companyInput, !readOnly && company && !department);
            SetEffect(rowEffect, rowInput, !readOnly && !company && !department);
            SetEffect(departmentEffect, departmentInput, !readOnly && department);
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => choose?.Invoke());
            // 不可选仍可查看原因；资格守卫在草稿及正式宿主。
            button.interactable = !pending;
            if (button is CardPickerCardButton gestures)
                gestures.ConfigurePreview(details, canPreview);
        }
    }
}
