using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class FinalScoreView : MonoBehaviour
    {
        [Header("Fixed game-over shell")]
        [SerializeField] private GameObject gameOverOverlay;
        [SerializeField] private Canvas gameOverCanvas;
        [SerializeField] private GraphicRaycaster gameOverRaycaster;
        [SerializeField] private GameObject finalScoreSummaryView;
        [SerializeField] private GameObject finalScoreDetailsView;
        [SerializeField] private GameObject finalScoreMessage;
        [SerializeField] private Text finalScoreMessageText;
        [SerializeField] private Text winnerText;
        [SerializeField] private Text tiebreakText;
        [SerializeField] private Text finalScoreDetailsButtonLabel;
        [SerializeField] private Button finalScoreDetailsButton;
        [SerializeField] private Button finalScoreDetailsBackButton;
        [SerializeField] private Button returnStartButton;
        [SerializeField] private RectTransform rankingRowsContainer;
        [SerializeField] private RectTransform detailPlayerRowsContainer;
        [SerializeField] private RectTransform detailChartsContainer;

        [Header("Dynamic templates")]
        [SerializeField] private FinalScoreRankingRowView rankingRowTemplate;
        [SerializeField] private FinalScoreDetailPlayerRowView detailPlayerRowTemplate;
        [SerializeField] private FinalScoreChartView scoreChartTemplate;
        [SerializeField] private FinalScoreBarView scoreBarTemplate;

        public GameObject GameOverOverlay => gameOverOverlay;
        public Canvas GameOverCanvas => gameOverCanvas;
        public GameObject FinalScoreSummaryView => finalScoreSummaryView;
        public GameObject FinalScoreDetailsView => finalScoreDetailsView;
        public GameObject FinalScoreMessage => finalScoreMessage;
        public Text FinalScoreMessageText => finalScoreMessageText;
        public Text WinnerText => winnerText;
        public Text TiebreakText => tiebreakText;
        public Text FinalScoreDetailsButtonLabel => finalScoreDetailsButtonLabel;
        public Button FinalScoreDetailsButton => finalScoreDetailsButton;
        public Button FinalScoreDetailsBackButton => finalScoreDetailsBackButton;
        public Button ReturnStartButton => returnStartButton;
        public RectTransform RankingRowsContainer => rankingRowsContainer;
        public RectTransform DetailPlayerRowsContainer => detailPlayerRowsContainer;
        public RectTransform DetailChartsContainer => detailChartsContainer;
        public FinalScoreRankingRowView RankingRowTemplate => rankingRowTemplate;
        public FinalScoreDetailPlayerRowView DetailPlayerRowTemplate => detailPlayerRowTemplate;
        public FinalScoreChartView ScoreChartTemplate => scoreChartTemplate;
        public FinalScoreBarView ScoreBarTemplate => scoreBarTemplate;

        public bool TryValidateConfiguration(out string reason)
        {
            if (gameOverOverlay == null || gameOverCanvas == null || gameOverRaycaster == null ||
                gameOverCanvas.gameObject != gameOverOverlay || gameOverRaycaster.gameObject != gameOverOverlay ||
                finalScoreSummaryView == null || finalScoreDetailsView == null ||
                finalScoreMessage == null || finalScoreMessageText == null || winnerText == null ||
                tiebreakText == null || finalScoreDetailsButtonLabel == null ||
                finalScoreDetailsButton == null || finalScoreDetailsBackButton == null || returnStartButton == null)
            {
                reason = "游戏结束固定窗口引用不完整。";
                return false;
            }

            if (rankingRowsContainer == null || detailPlayerRowsContainer == null || detailChartsContainer == null)
            {
                reason = "最终计分动态容器引用不完整。";
                return false;
            }

            if (rankingRowTemplate == null || detailPlayerRowTemplate == null ||
                scoreChartTemplate == null || scoreBarTemplate == null)
            {
                reason = "最终计分动态模板引用不完整。";
                return false;
            }

            if (rankingRowTemplate.gameObject.activeSelf ||
                detailPlayerRowTemplate.gameObject.activeSelf || scoreChartTemplate.gameObject.activeSelf ||
                scoreBarTemplate.gameObject.activeSelf)
            {
                reason = "最终计分动态模板必须保持停用。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public void PrepareGameOverPage()
        {
            // 终局内容页使用独立排序层级，保持常驻HUD可用。
            gameOverCanvas.overrideSorting = true;
            gameOverCanvas.sortingOrder = GameplayUiLayers.Page;
        }
    }
}
