using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuJoinPanelView : MonoBehaviour
    {
        [SerializeField] private Text descriptionText;
        [SerializeField] private InputField roomCodeInput;
        [SerializeField] private Text statusText;
        [SerializeField] private Button pasteButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button backButton;

        public Text DescriptionText => descriptionText;
        public InputField RoomCodeInput => roomCodeInput;
        public Text StatusText => statusText;
        public Button PasteButton => pasteButton;
        public Button JoinButton => joinButton;
        public Button BackButton => backButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (descriptionText == null || roomCodeInput == null || statusText == null ||
                pasteButton == null || joinButton == null || backButton == null)
            {
                reason = "加入房间面板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
