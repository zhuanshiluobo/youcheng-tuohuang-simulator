using UnityEngine;
using UnityEngine.UI;
namespace YC.Presentation
{
    public sealed class StartMenuMapSelectionPanelView : MonoBehaviour
    {
        [SerializeField] private Text titleText;
        public Text TitleText => titleText;
        [SerializeField] private Text countText;
        public Text CountText => countText;
        [SerializeField] private Text mapText;
        public Text MapText => mapText;
        [SerializeField] private Button previousCountButton;
        public Button PreviousCountButton => previousCountButton;
        [SerializeField] private Button nextCountButton;
        public Button NextCountButton => nextCountButton;
        [SerializeField] private Button previousMapButton;
        public Button PreviousMapButton => previousMapButton;
        [SerializeField] private Button nextMapButton;
        public Button NextMapButton => nextMapButton;
        [SerializeField] private Button startButton;
        public Button StartButton => startButton;
        [SerializeField] private Button backButton;
        public Button BackButton => backButton;
        public bool TryValidateConfiguration(out string reason)
        {
            var valid = titleText != null && countText != null && mapText != null && previousCountButton != null && nextCountButton != null && previousMapButton != null && nextMapButton != null && startButton != null && backButton != null;
            reason = valid ? string.Empty : "地图选择面板引用不完整。";
            return valid;
        }
    }
}
