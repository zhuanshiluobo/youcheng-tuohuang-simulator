using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuOnlineModePanelView : MonoBehaviour
    {
        [SerializeField] private Button createRoomButton;
        [SerializeField] private Button joinRoomButton;
        [SerializeField] private Button backButton;

        public Button CreateRoomButton => createRoomButton;
        public Button JoinRoomButton => joinRoomButton;
        public Button BackButton => backButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (createRoomButton == null || joinRoomButton == null || backButton == null)
            {
                reason = "联机模式面板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
