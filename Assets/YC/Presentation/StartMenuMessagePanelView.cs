using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuMessagePanelView : MonoBehaviour
    {
        [SerializeField] private Text titleText;
        [SerializeField] private Text messageText;
        [SerializeField] private Button actionButton;
        [SerializeField] private Text actionButtonText;

        public Text TitleText => titleText;
        public Text MessageText => messageText;
        public Button ActionButton => actionButton;
        public Text ActionButtonText => actionButtonText;

        public bool TryValidateConfiguration(out string reason)
        {
            if (titleText == null || messageText == null || actionButton == null || actionButtonText == null)
            {
                reason = "消息面板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
