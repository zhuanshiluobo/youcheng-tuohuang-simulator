using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuView : MonoBehaviour
    {
        [Header("Cover")]
        [SerializeField] private RectTransform coverFrame;
        [SerializeField] private RawImage coverImage;

        [Header("Main menu")]
        [SerializeField] private Button startGameButton;
        [SerializeField] private Button onlineModeButton;
        [SerializeField] private Button achievementsButton;
        [SerializeField] private Button creatorSiteButton;
        [SerializeField] private Button officialSiteButton;
        [SerializeField] private Button wikiButton;

        [Header("Room panels")]
        [SerializeField] private StartMenuOnlineModePanelView onlineModePanel;
        [SerializeField] private StartMenuAchievementsPanelView achievementsPanel;
        [SerializeField] private StartMenuMapSelectionPanelView mapSelectionPanel;
        [SerializeField] private StartMenuJoinPanelView joinPanel;
        [SerializeField] private StartMenuRoomPanelView roomPanel;
        [SerializeField] private StartMenuMessagePanelView messagePanel;

        public RectTransform CoverFrame => coverFrame;
        public RawImage CoverImage => coverImage;
        public Button StartGameButton => startGameButton;
        public Button OnlineModeButton => onlineModeButton;
        public Button AchievementsButton => achievementsButton;
        public Button CreatorSiteButton => creatorSiteButton;
        public Button OfficialSiteButton => officialSiteButton;
        public Button WikiButton => wikiButton;
        public StartMenuOnlineModePanelView OnlineModePanel => onlineModePanel;
        public StartMenuAchievementsPanelView AchievementsPanel => achievementsPanel;
        public StartMenuMapSelectionPanelView MapSelectionPanel => mapSelectionPanel;
        public StartMenuJoinPanelView JoinPanel => joinPanel;
        public StartMenuRoomPanelView RoomPanel => roomPanel;
        public StartMenuMessagePanelView MessagePanel => messagePanel;

        public bool TryValidateConfiguration(out string reason)
        {
            if (coverFrame == null || coverImage == null)
            {
                reason = "封面框或封面图引用缺失。";
                return false;
            }

            if (startGameButton == null || onlineModeButton == null || achievementsButton == null ||
                creatorSiteButton == null || officialSiteButton == null || wikiButton == null)
            {
                reason = "主菜单按钮引用不完整。";
                return false;
            }

            if (onlineModePanel == null)
            {
                reason = "联机模式面板引用缺失。";
                return false;
            }

            if (!onlineModePanel.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (achievementsPanel == null)
            {
                reason = "成就面板引用缺失。";
                return false;
            }

            if (!achievementsPanel.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (mapSelectionPanel == null)
            {
                reason = "地图选择面板引用缺失。";
                return false;
            }

            if (!mapSelectionPanel.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (joinPanel == null)
            {
                reason = "加入房间面板引用缺失。";
                return false;
            }

            if (!joinPanel.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (roomPanel == null)
            {
                reason = "等待房间面板引用缺失。";
                return false;
            }

            if (!roomPanel.TryValidateConfiguration(out reason))
            {
                return false;
            }

            if (messagePanel == null)
            {
                reason = "消息面板引用缺失。";
                return false;
            }

            if (!messagePanel.TryValidateConfiguration(out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public void HideRoomPanels()
        {
            onlineModePanel.gameObject.SetActive(false);
            achievementsPanel.gameObject.SetActive(false);
            mapSelectionPanel.gameObject.SetActive(false);
            joinPanel.gameObject.SetActive(false);
            roomPanel.gameObject.SetActive(false);
            messagePanel.gameObject.SetActive(false);
        }
    }
}
