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
        [SerializeField] private string steamDescription = "输入房主显示的 Lobby 房间码";
        [SerializeField] private string steamPlaceholder = "请输入 Steam Lobby ID";
        [SerializeField] private string localDescription = "输入房主显示的本地地址（IP:端口）";
        [SerializeField] private string localPlaceholder = "例如 127.0.0.1:7780";

        public void ShowConnectionType(bool localNetwork)
        {
            descriptionText.text = localNetwork ? localDescription : steamDescription;
            if (roomCodeInput.placeholder is Text placeholder)
                placeholder.text = localNetwork ? localPlaceholder : steamPlaceholder;
        }

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
