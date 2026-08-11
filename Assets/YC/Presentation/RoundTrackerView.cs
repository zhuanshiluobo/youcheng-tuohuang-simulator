using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RoundTrackerView : MonoBehaviour
    {
        [Header("Fixed round track")]
        [SerializeField] private RectTransform canvasTransform;
        [SerializeField] private RectTransform panelTransform;
        [SerializeField] private RectTransform contentArea;
        [SerializeField] private RectTransform trackSlotsTransform;
        [SerializeField] private RectTransform markerContainer;

        [Header("Fixed game-over shell")]
        [SerializeField] private GameObject gameOverOverlay;
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
        [SerializeField] private RoundTrackerPlayerMarkerView playerMarkerTemplate;
        [SerializeField] private RoundTrackerRankingRowView rankingRowTemplate;
        [SerializeField] private RoundTrackerDetailPlayerRowView detailPlayerRowTemplate;
        [SerializeField] private RoundTrackerScoreChartView scoreChartTemplate;
        [SerializeField] private RoundTrackerScoreBarView scoreBarTemplate;

        public RectTransform CanvasTransform => canvasTransform;
        public RectTransform PanelTransform => panelTransform;
        public RectTransform ContentArea => contentArea;
        public RectTransform TrackSlotsTransform => trackSlotsTransform;
        public RectTransform MarkerContainer => markerContainer;
        public GameObject GameOverOverlay => gameOverOverlay;
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
        public RoundTrackerPlayerMarkerView PlayerMarkerTemplate => playerMarkerTemplate;
        public RoundTrackerRankingRowView RankingRowTemplate => rankingRowTemplate;
        public RoundTrackerDetailPlayerRowView DetailPlayerRowTemplate => detailPlayerRowTemplate;
        public RoundTrackerScoreChartView ScoreChartTemplate => scoreChartTemplate;
        public RoundTrackerScoreBarView ScoreBarTemplate => scoreBarTemplate;

        public bool TryValidateConfiguration(out string reason)
        {
            if (canvasTransform == null || panelTransform == null || contentArea == null ||
                trackSlotsTransform == null || markerContainer == null)
            {
                reason = "回合轨道固定层级引用不完整。";
                return false;
            }

            if (gameOverOverlay == null || finalScoreSummaryView == null || finalScoreDetailsView == null ||
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

            if (playerMarkerTemplate == null || rankingRowTemplate == null || detailPlayerRowTemplate == null ||
                scoreChartTemplate == null || scoreBarTemplate == null)
            {
                reason = "回合追踪器动态模板引用不完整。";
                return false;
            }

            if (playerMarkerTemplate.gameObject.activeSelf || rankingRowTemplate.gameObject.activeSelf ||
                detailPlayerRowTemplate.gameObject.activeSelf || scoreChartTemplate.gameObject.activeSelf ||
                scoreBarTemplate.gameObject.activeSelf)
            {
                reason = "回合追踪器动态模板必须保持停用。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
