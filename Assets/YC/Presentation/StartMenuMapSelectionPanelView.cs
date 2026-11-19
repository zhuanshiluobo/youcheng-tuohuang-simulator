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

        [Header("房间外内容管理")]
        [SerializeField] private Text countLabel, mapLabel, startCaption, diyStatusText;
        [SerializeField] private string diyTitle = "DIY 内容管理", diyPackLabel = "内容包:", diyVersionLabel = "版本:", diyApplyLabel = "使用版本";
        [SerializeField] private string diySavedTitle = "版本已保存，重启后生效", diyEmptyCaption = "未发现 DIY 内容";
        [SerializeField] private string diyBoundSuffix = "（已绑定）", diyDamagedSuffix = "（不可用）", diyErrorTitle = "DIY 内容不可用";
        [SerializeField] private string diyNoSelection = "当前没有可选版本。", diyDetailsFormat = "{0}\n{1}", diyUnknownPack = "未知内容包";
        [SerializeField] private string diyFolderCaption = "DIY 文件夹", diyFolderAction = "打开文件夹", diyFolderDescription = "打开文件夹以放入或查看 DIY 内容。";
        public Text DiyStatusText => diyStatusText;
        public string DiyTitle => diyTitle;
        public string DiySavedTitle => diySavedTitle;
        public string DiyEmptyCaption => diyEmptyCaption;
        public string DiyBoundSuffix => diyBoundSuffix;
        public string DiyDamagedSuffix => diyDamagedSuffix;
        public string DiyErrorTitle => diyErrorTitle;
        public string DiyNoSelection => diyNoSelection;
        public string DiyDetailsFormat => diyDetailsFormat;
        public string DiyUnknownPack => diyUnknownPack;
        public string DiyFolderCaption => diyFolderCaption;
        public string DiyFolderDescription => diyFolderDescription;
        private bool capturedCaptions;
        private string normalCountLabel, normalMapLabel, normalStartCaption;

        public void SetDiyMode(bool active)
        {
            if (!capturedCaptions)
            {
                normalCountLabel = countLabel.text; normalMapLabel = mapLabel.text; normalStartCaption = startCaption.text;
                capturedCaptions = true;
            }
            countLabel.text = active ? diyPackLabel : normalCountLabel;
            mapLabel.text = active ? diyVersionLabel : normalMapLabel;
            startCaption.text = active ? diyApplyLabel : normalStartCaption;
            diyStatusText.gameObject.SetActive(active);
        }

        public void SetDiyFolderAction(bool folder) { startCaption.text = folder ? diyFolderAction : diyApplyLabel; }

        [Header("状态文案")]
        [SerializeField] private string onlineModeTitle = "联机模式";
        public string OnlineModeTitle => onlineModeTitle;

        public bool TryValidateConfiguration(out string reason)
        {
            var valid = titleText != null && countText != null && mapText != null && previousCountButton != null && nextCountButton != null && previousMapButton != null && nextMapButton != null && startButton != null && backButton != null &&
                countLabel != null && mapLabel != null && startCaption != null && diyStatusText != null;
            reason = valid ? string.Empty : "地图选择面板引用不完整。";
            return valid;
        }
    }
}
