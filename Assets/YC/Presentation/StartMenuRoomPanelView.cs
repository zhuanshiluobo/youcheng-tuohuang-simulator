using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuRoomPanelView : MonoBehaviour
    {
        [SerializeField] private Text roomCodeText;
        [SerializeField] private Button copyButton;
        [SerializeField] private Text validationText;
        [SerializeField] private RectTransform seatListRoot;
        [SerializeField] private Text seatTemplate;
        [SerializeField] private Text statusText;
        [SerializeField] private Button inviteButton;
        [SerializeField] private Button startButton;
        [SerializeField] private Button backButton;

        public Text RoomCodeText => roomCodeText;
        public Button CopyButton => copyButton;
        public Text ValidationText => validationText;
        public RectTransform SeatListRoot => seatListRoot;
        public Text SeatTemplate => seatTemplate;
        public Text StatusText => statusText;
        public Button InviteButton => inviteButton;
        public Button StartButton => startButton;
        public Button BackButton => backButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (roomCodeText == null || copyButton == null || validationText == null ||
                seatListRoot == null || seatTemplate == null || statusText == null ||
                inviteButton == null || startButton == null || backButton == null)
            {
                reason = "等待房间面板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
