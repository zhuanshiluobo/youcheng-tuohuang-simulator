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

        [Header("本地联机文案")]
        [SerializeField] private string localDescription = "输入房主显示的本地地址（IP:端口）";
        [SerializeField] private string localRoomCodePlaceholder = "例如 127.0.0.1:7780";

        [Header("Steam 联机文案")]
        [SerializeField] private string steamDescription = "输入房主显示的 Lobby 房间码";
        [SerializeField] private string steamRoomCodePlaceholder = "请输入 Steam Lobby ID";

        public Text DescriptionText => descriptionText;
        public InputField RoomCodeInput => roomCodeInput;
        public Text StatusText => statusText;
        public Button PasteButton => pasteButton;
        public Button JoinButton => joinButton;
        public Button BackButton => backButton;
        public string LocalDescription => localDescription;
        public string LocalRoomCodePlaceholder => localRoomCodePlaceholder;
        public string SteamDescription => steamDescription;
        public string SteamRoomCodePlaceholder => steamRoomCodePlaceholder;

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
