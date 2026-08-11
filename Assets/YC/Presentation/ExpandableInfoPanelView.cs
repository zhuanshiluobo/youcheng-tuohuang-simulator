using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ExpandableInfoPanelView : MonoBehaviour
    {
        [SerializeField] private ExpandableInfoPanel controller;
        [SerializeField] private RectTransform root;
        [SerializeField] private RectTransform panelTransform;
        [SerializeField] private RectTransform contentArea;
        [SerializeField] private RectTransform scrollContent;
        [SerializeField] private Button toggleButton;
        [SerializeField] private Text toggleButtonText;
        [SerializeField] private ExpandableInfoItemView moduleTemplate;
        [SerializeField] private ExpandableInfoItemView rowTemplate;
        [SerializeField] private ExpandableInfoItemView imagePreviewTemplate;
        [SerializeField] private ExpandableInfoItemView cardStripTemplate;
        [SerializeField] private ExpandableInfoItemView cardThumbnailTemplate;
        [SerializeField] private ExpandableInfoItemView dragGhostTemplate;

        public ExpandableInfoPanel Controller => controller;
        public RectTransform Root => root;
        public RectTransform PanelTransform => panelTransform;
        public RectTransform ContentArea => contentArea;
        public RectTransform ScrollContent => scrollContent;
        public Button ToggleButton => toggleButton;
        public Text ToggleButtonText => toggleButtonText;
        public ExpandableInfoItemView ModuleTemplate => moduleTemplate;
        public ExpandableInfoItemView RowTemplate => rowTemplate;
        public ExpandableInfoItemView ImagePreviewTemplate => imagePreviewTemplate;
        public ExpandableInfoItemView CardStripTemplate => cardStripTemplate;
        public ExpandableInfoItemView CardThumbnailTemplate => cardThumbnailTemplate;
        public ExpandableInfoItemView DragGhostTemplate => dragGhostTemplate;

        public bool IsBoundTo(ExpandableInfoPanel candidate)
        {
            return candidate != null && controller == candidate;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (controller == null || root == null || panelTransform == null || contentArea == null ||
                scrollContent == null || toggleButton == null || toggleButtonText == null)
            {
                reason = "信息面板固定 View 引用不完整。";
                return false;
            }

            if (moduleTemplate == null || rowTemplate == null || imagePreviewTemplate == null ||
                cardStripTemplate == null || cardThumbnailTemplate == null || dragGhostTemplate == null)
            {
                reason = "信息面板动态模板引用不完整。";
                return false;
            }

            if (!moduleTemplate.TryValidateAs(ExpandableInfoItemKind.Module, out reason) ||
                !rowTemplate.TryValidateAs(ExpandableInfoItemKind.Row, out reason) ||
                !imagePreviewTemplate.TryValidateAs(ExpandableInfoItemKind.ImagePreview, out reason) ||
                !cardStripTemplate.TryValidateAs(ExpandableInfoItemKind.CardStrip, out reason) ||
                !cardThumbnailTemplate.TryValidateAs(ExpandableInfoItemKind.CardThumbnail, out reason) ||
                !dragGhostTemplate.TryValidateAs(ExpandableInfoItemKind.DragGhost, out reason))
            {
                return false;
            }

            if (moduleTemplate.gameObject.activeSelf || rowTemplate.gameObject.activeSelf ||
                imagePreviewTemplate.gameObject.activeSelf || cardStripTemplate.gameObject.activeSelf ||
                cardThumbnailTemplate.gameObject.activeSelf || dragGhostTemplate.gameObject.activeSelf)
            {
                reason = "信息面板动态模板必须在 Prefab 中默认禁用。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
