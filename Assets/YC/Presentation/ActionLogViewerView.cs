using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class ActionLogViewerView : MonoBehaviour
    {
        [SerializeField] private GameObject overlayObject;
        [SerializeField] private RectTransform contentTransform;
        [SerializeField] private Button overlayCloseButton;
        [SerializeField] private Button closeButton;
        [SerializeField] private ActionLogRowView rowTemplate;

        public GameObject OverlayObject => overlayObject;
        public RectTransform ContentTransform => contentTransform;
        public Button OverlayCloseButton => overlayCloseButton;
        public Button CloseButton => closeButton;
        public ActionLogRowView RowTemplate => rowTemplate;

        public bool TryValidateConfiguration(out string reason)
        {
            if (overlayObject == null || contentTransform == null || overlayCloseButton == null ||
                closeButton == null || rowTemplate == null)
            {
                reason = "行动日志查看器固定层级引用不完整。";
                return false;
            }

            if (!rowTemplate.TryValidateConfiguration(out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
