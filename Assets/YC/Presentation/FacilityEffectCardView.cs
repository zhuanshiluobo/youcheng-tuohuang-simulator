using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class FacilityEffectCardView : MonoBehaviour
    {
        [SerializeField] private RectTransform cardRect;
        [SerializeField] private Image background;
        [SerializeField] private Button button;
        [SerializeField] private Outline outline;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private RawImage cardImage;
        [SerializeField] private Text fallbackLabel;
        [SerializeField] private CardPointerInteraction pointerInteraction;
        [SerializeField] private Button detailsButton;
        [SerializeField] private Image selectionImage;

        [SerializeField] private RectTransform face;
        [SerializeField] private Vector2 referenceFaceSize = new Vector2(180f, 255f);
        private bool fitCell;

        public void FitIntoCell()
        {
            fitCell = true;
            RefreshCellLayout();
        }

        private void OnRectTransformDimensionsChange()
        {
            if (fitCell) RefreshCellLayout();
        }

        // 供供应区布局在设置矩形之前测量完整底框，避免依赖上一帧尺寸。
        public Vector2 MeasureSlotSize(float availableWidth)
        {
            if (background == null) return Vector2.zero;
            var frame = background.rectTransform;
            var faceSize = FaceSizeForSlotWidth(availableWidth);
            return Vector2.Scale(faceSize, frame.anchorMax - frame.anchorMin) + frame.sizeDelta;
        }

        private Vector2 FaceSizeForSlotWidth(float availableWidth)
        {
            var frame = background.rectTransform;
            var span = frame.anchorMax - frame.anchorMin;
            var width = Mathf.Max(0f, (availableWidth - frame.sizeDelta.x) / Mathf.Max(.001f, span.x));
            return new Vector2(width, width * referenceFaceSize.y / Mathf.Max(.001f, referenceFaceSize.x));
        }

        private void RefreshCellLayout()
        {
            if (face == null || cardRect == null || background == null) return;
            var column = GetComponent<VerticalLayoutGroup>();
            var input = face.GetComponent<LayoutElement>();
            var slotInput = cardRect.GetComponent<LayoutElement>();
            var supplySlot = cardRect.parent as RectTransform;
            if (column == null || input == null || slotInput == null || supplySlot == null) return;
            // background 引用 Card Face 下向外扩展的 Card Slot 底框。
            // 按底框完整宽度适配供应位，布局占位也包含它，不能只取 Artwork 的尺寸。
            var faceSize = FaceSizeForSlotWidth(supplySlot.rect.width);
            var frameSize = MeasureSlotSize(supplySlot.rect.width);
            slotInput.minWidth = slotInput.preferredWidth = frameSize.x;
            slotInput.minHeight = slotInput.preferredHeight = frameSize.y;
            slotInput.flexibleWidth = slotInput.flexibleHeight = 0f;
            input.minWidth = input.preferredWidth = faceSize.x;
            input.minHeight = input.preferredHeight = faceSize.y;
            input.flexibleWidth = input.flexibleHeight = 0f;
            column.padding = new RectOffset(0, 0, 0, 0);
            if (!CanvasUpdateRegistry.IsRebuildingLayout()) LayoutRebuilder.MarkLayoutForRebuild(cardRect);
        }

        public RectTransform CardRect => cardRect;
        public Image Background => background;
        public Button Button => button;
        public Outline Outline => outline;
        public CanvasGroup CanvasGroup => canvasGroup;
        public RawImage CardImage => cardImage;
        public Text FallbackLabel => fallbackLabel;
        public CardPointerInteraction PointerInteraction => pointerInteraction;
        public Button DetailsButton => detailsButton;
        public Image SelectionImage => selectionImage;

        // 卡槽预制体统一拥有查看手势；页面只提供查看内容与返回上下文。
        public void ConfigureInspection(Action open, Func<bool> isCurrent)
        {
            pointerInteraction.ConfigureDrag(null, null, null, null);
            if (button is CardPickerCardButton picker)
            {
                pointerInteraction.enabled = false;
                picker.ConfigurePreview(open, isCurrent);
            }
            else
            {
                pointerInteraction.ConfigureClick(button, null, () =>
                {
                    if (isCurrent == null || isCurrent()) open?.Invoke();
                });
            }
            if (detailsButton != null) detailsButton.gameObject.SetActive(false);
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (cardRect == null || background == null || button == null || outline == null ||
                canvasGroup == null || cardImage == null || fallbackLabel == null || pointerInteraction == null)
            {
                reason = "设施效果卡模板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
