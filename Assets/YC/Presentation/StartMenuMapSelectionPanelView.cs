using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuMapSelectionPanelView : MonoBehaviour
    {
        [SerializeField] private Text titleText;
        [SerializeField] private Button threePlayerButton;
        [SerializeField] private Button fourPlayerButton;
        [SerializeField] private Button backButton;

        public Text TitleText => titleText;
        public Button ThreePlayerButton => threePlayerButton;
        public Button FourPlayerButton => fourPlayerButton;
        public Button BackButton => backButton;

        public bool TryValidateConfiguration(out string reason)
        {
            if (titleText == null || threePlayerButton == null || fourPlayerButton == null || backButton == null)
            {
                reason = "地图选择面板引用不完整。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
