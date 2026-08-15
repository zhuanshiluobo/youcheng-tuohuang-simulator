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
        [SerializeField] private GameObject generalContentObject;
        [SerializeField] private GameObject futureContentObject;

        [Header("Buttons")]
        [SerializeField] private Button gearButton;
        [SerializeField] private Button actionLogButton;
        [SerializeField] private Button overlayCloseButton;
        [SerializeField] private Button headerCloseButton;
        [SerializeField] private Button generalTabButton;
        [SerializeField] private Button rulebookButton;
        [SerializeField] private Button placeholderTabButton;
        [SerializeField] private Button returnButton;
        [SerializeField] private Button confirmReturnButton;
        [SerializeField] private Button cancelReturnButton;

        [Header("General")]
        [SerializeField] private Dropdown resolutionDropdown;

        public RectTransform CanvasTransform => canvasTransform;
        public RectTransform MenuPanel => menuPanel;
        public GameObject OverlayObject => overlayObject;
        public GameObject ConfirmationObject => confirmationObject;
        public GameObject ReturnButtonObject => returnButtonObject;
        public GameObject ActionLogButtonObject => actionLogButtonObject;
        public GameObject GeneralContentObject => generalContentObject;
        public GameObject FutureContentObject => futureContentObject;
        public Button GearButton => gearButton;
        public Button ActionLogButton => actionLogButton;
        public Button OverlayCloseButton => overlayCloseButton;
        public Button HeaderCloseButton => headerCloseButton;
        public Button GeneralTabButton => generalTabButton;
        public Button RulebookButton => rulebookButton;
        public Button PlaceholderTabButton => placeholderTabButton;
        public Button ReturnButton => returnButton;
        public Button ConfirmReturnButton => confirmReturnButton;
        public Button CancelReturnButton => cancelReturnButton;
        public Dropdown ResolutionDropdown => resolutionDropdown;

        public bool TryValidateConfiguration(out string reason)
        {
            if (canvasTransform == null || menuPanel == null || overlayObject == null ||
                confirmationObject == null || returnButtonObject == null || actionLogButtonObject == null ||
                generalContentObject == null || futureContentObject == null)
            {
                reason = "设置菜单层级引用不完整。";
                return false;
            }

            if (gearButton == null || actionLogButton == null || overlayCloseButton == null ||
                headerCloseButton == null || generalTabButton == null || rulebookButton == null ||
                placeholderTabButton == null || returnButton == null ||
                confirmReturnButton == null || cancelReturnButton == null)
            {
                reason = "设置菜单按钮引用不完整。";
                return false;
            }

            if (resolutionDropdown == null)
            {
                reason = "通用设置缺少分辨率下拉菜单。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
