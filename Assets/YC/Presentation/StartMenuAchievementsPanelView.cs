using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuAchievementsPanelView : MonoBehaviour
    {
        [SerializeField] private Text achievementListText;
        [SerializeField] private Button collectionRoomButton;
        [SerializeField] private Button backButton;

        public Text AchievementListText => achievementListText;
        public Button CollectionRoomButton => collectionRoomButton;
        public Button BackButton => backButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (achievementListText == null || collectionRoomButton == null || backButton == null)
            {
                reason = "成就面板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
