using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class GameSettingsMenuView : MonoBehaviour
    {
        [SerializeField] private RectTransform canvasTransform;
        [SerializeField] private RectTransform menuPanel;
        [SerializeField] private GameObject overlayObject;
        [SerializeField] private GameObject confirmationObject;
        [SerializeField] private GameObject returnButtonObject;
        [SerializeField] private GameObject actionLogButtonObject;

        [Header("Buttons")]
        [SerializeField] private Button gearButton;
        [SerializeField] private Button actionLogButton;
        [SerializeField] private Button overlayCloseButton;
        [SerializeField] private Button headerCloseButton;
        [SerializeField] private Button rulebookButton;
        [SerializeField] private Button returnButton;
        [SerializeField] private Button confirmReturnButton;
        [SerializeField] private Button cancelReturnButton;

        public RectTransform CanvasTransform => canvasTransform;
        public RectTransform MenuPanel => menuPanel;
        public GameObject OverlayObject => overlayObject;
        public GameObject ConfirmationObject => confirmationObject;
        public GameObject ReturnButtonObject => returnButtonObject;
        public GameObject ActionLogButtonObject => actionLogButtonObject;
        public Button GearButton => gearButton;
        public Button ActionLogButton => actionLogButton;
        public Button OverlayCloseButton => overlayCloseButton;
        public Button HeaderCloseButton => headerCloseButton;
        public Button RulebookButton => rulebookButton;
        public Button ReturnButton => returnButton;
        public Button ConfirmReturnButton => confirmReturnButton;
        public Button CancelReturnButton => cancelReturnButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (canvasTransform == null || menuPanel == null || overlayObject == null ||
                confirmationObject == null || returnButtonObject == null || actionLogButtonObject == null)
            {
                reason = "设置菜单层级引用不完整。";
                return false;
            }

            if (gearButton == null || actionLogButton == null || overlayCloseButton == null ||
                headerCloseButton == null || rulebookButton == null || returnButton == null ||
                confirmReturnButton == null || cancelReturnButton == null)
            {
                reason = "设置菜单按钮引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
