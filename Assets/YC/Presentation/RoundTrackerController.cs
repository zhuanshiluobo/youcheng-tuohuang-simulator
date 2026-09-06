using System.Collections.Generic;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class RoundTrackerController : MonoBehaviour
    {
        private const int FirstRoundIndex = RoundTrackRule.FirstRoundIndex;
        private const int FinalIndex = RoundTrackRule.FinalIndex;
        private const float PanelHeight = 78f;
        private const float PanelWidth = 720f;
        private const float BorderHeightRatio = 0.2f;
        private const float BorderThickness = PanelHeight * BorderHeightRatio;

        private static readonly string[] RoundLabels =
        {
            "START", "1", "2", "3", "4", "5", "6", "7", "8", "FINAL"
        };

        [SerializeField] private string startSceneName = "StartScene";
        [SerializeField] private RoundTrackerView view;

        // These retain the established reflection-facing shape while pointing at serialized View content.
        private RectTransform panelTransform;
        private RectTransform contentArea;
        private RectTransform canvasTransform;
        private RectTransform markerTransform;
        private RectTransform trackSlotsTransform;
        private int currentIndex = FirstRoundIndex;
        private bool initialized;
        private bool gameOverDialogShown;
        private GameObject finalScoreSummaryView;
        private GameObject finalScoreDetailsView;
        private UnityEngine.UI.Text finalScoreDetailsButtonLabel;
        private readonly List<RoundTrackerPlayerMarkerView> playerMarkers =
            new List<RoundTrackerPlayerMarkerView>();
        private readonly Dictionary<int, RoundTrackerScoreChartView> finalScoreDetailCharts =
            new Dictionary<int, RoundTrackerScoreChartView>();
        private readonly Dictionary<int, RoundTrackerDetailPlayerRowView> finalScoreDetailSelectorRows =
            new Dictionary<int, RoundTrackerDetailPlayerRowView>();

        public bool IsExpanded => true;
        public bool IsConfigured { get; private set; }
        public bool IsFinalScoreDetailsOpen { get; private set; }
        public int SelectedFinalScorePlayerId { get; private set; } = -1;

        private void Awake()
        {
            TryInitialize();
        }

        private bool TryInitialize()
        {
            if (initialized)
            {
                return IsConfigured;
            }

            initialized = true;
            var reason = string.Empty;
            if (view == null || !view.TryValidateConfiguration(out reason))
            {
                Debug.LogError("[RoundTrackerController] 配置无效：" +
                               (view == null ? "缺少 RoundTrackerView 引用。" : reason), this);
                enabled = false;
                return false;
            }

            IsConfigured = true;
            canvasTransform = view.CanvasTransform;
            panelTransform = view.PanelTransform;
            contentArea = view.ContentArea;
            trackSlotsTransform = view.TrackSlotsTransform;
            finalScoreSummaryView = view.FinalScoreSummaryView;
            finalScoreDetailsView = view.FinalScoreDetailsView;
            finalScoreDetailsButtonLabel = view.FinalScoreDetailsButtonLabel;

            view.FinalScoreDetailsButton.onClick.AddListener(ToggleFinalScoreDetails);
            view.FinalScoreDetailsBackButton.onClick.AddListener(CloseFinalScoreDetails);
            view.ReturnStartButton.onClick.AddListener(ReturnToStartScene);

            view.GameOverOverlay.SetActive(false);
            finalScoreSummaryView.SetActive(true);
            finalScoreDetailsView.SetActive(false);
            view.FinalScoreMessage.SetActive(false);
            BuildPlayerMarkers(null);
            MoveMarkerToCurrentIndex();
            return true;
        }

        private void Update()
        {
        }

        private void StepPanelAnimation(float deltaTime)
        {
        }

        public void RefreshFromState(GameState state)
        {
            // Scene root Awake order is undefined. The tabletop controller can request its first
            // refresh before this component receives Awake, so initialization must be idempotent
            // and available on demand instead of relying on sibling object ordering.
            if (!TryInitialize())
            {
                return;
            }

            if (state == null)
            {
                Debug.LogError("[RoundTrackerController] 无法刷新：GameState 为空。", this);
                return;
            }

            currentIndex = RoundTrackRule.GetRoundIndex(state);
            BuildPlayerMarkers(state);
            MoveMarkerToCurrentIndex();

            if (currentIndex >= FinalIndex)
            {
                ShowGameOverDialog(state);
            }
        }

        public void ReturnToStartScene()
        {
            GameLaunchContext.ShutdownOnlineSession();
            SceneTransitionContext.TryBeginBlackTransition(startSceneName);
        }

        public void Toggle()
        {
        }

        private void BuildPlayerMarkers(GameState _)
        {
            if (playerMarkers.Count == 1 &&
                playerMarkers[0] != null &&
                playerMarkers[0].MarkerTransform != null)
            {
                markerTransform = playerMarkers[0].MarkerTransform;
                playerMarkers[0].Bind("Round Marker", Color.white, GetSlotX(currentIndex), 0f);
                return;
            }

            ClearDynamicChildren(view.MarkerContainer);
            playerMarkers.Clear();
            markerTransform = null;
            AddPlayerMarker("Round Marker", Color.white, 0f);
        }

        private void AddPlayerMarker(string objectName, Color color, float y)
        {
            var marker = Instantiate(view.PlayerMarkerTemplate, view.MarkerContainer);
            marker.gameObject.SetActive(true);
            marker.Bind(objectName, color, GetSlotX(currentIndex), y);
            playerMarkers.Add(marker);
            if (markerTransform == null)
            {
                markerTransform = marker.MarkerTransform;
            }
        }

        private void ShowGameOverDialog(GameState state)
        {
            if (gameOverDialogShown)
            {
                return;
            }

            gameOverDialogShown = true;
            view.GameOverOverlay.SetActive(true);
            PopulateFinalScoreboard(state);
        }

        private void PopulateFinalScoreboard(GameState state)
        {
            ClearFinalScoreDynamics();
            finalScoreSummaryView.SetActive(true);
            finalScoreDetailsView.SetActive(false);
            IsFinalScoreDetailsOpen = false;

            if (state.FinalScoring == null || !state.FinalScoring.IsResolved)
            {
                SelectedFinalScorePlayerId = -1;
                view.WinnerText.text = "胜者：待定";
                view.TiebreakText.text = string.Empty;
                view.FinalScoreMessageText.text = "最终计分尚未生成。";
                view.FinalScoreMessage.SetActive(true);
                view.FinalScoreDetailsButton.interactable = false;
                UpdateFinalScoreDetailsEntryLabel();
                return;
            }

            view.FinalScoreMessage.SetActive(false);
            var orderedScores = BuildOrderedFinalScores(state.FinalScoring.PlayerScores);
            SelectedFinalScorePlayerId = ResolveDefaultDetailsPlayerId(state.FinalScoring, orderedScores);
            view.WinnerText.text = "胜者：" + FormatWinnerIds(state.FinalScoring.WinnerPlayerIds);
            view.TiebreakText.text = string.IsNullOrEmpty(state.FinalScoring.TiebreakSummary)
                ? "同分时依次比较剩余金券、至纯源石。"
                : state.FinalScoring.TiebreakSummary;
            view.FinalScoreDetailsButton.interactable = SelectedFinalScorePlayerId >= 0;

            for (var i = 0; i < orderedScores.Count; i++)
            {
                var score = orderedScores[i];
                var player = state.FindPlayer(score.PlayerId);
                var playerName = player == null || string.IsNullOrEmpty(player.Name)
                    ? "P" + score.PlayerId
                    : player.Name;
                var playerColor = player == null ? Color.white : UiTheme.GetPlayerColor(player.Color, 1f);
                var isWinner = state.FinalScoring.WinnerPlayerIds.Contains(score.PlayerId);

                var rankingRow = Instantiate(view.RankingRowTemplate, view.RankingRowsContainer);
                rankingRow.gameObject.SetActive(true);
                rankingRow.Bind(
                    score.PlayerId,
                    playerName,
                    playerColor,
                    score.BaseScore + score.FacilityScore + score.CityStyleScore,
                    score.RegionScore,
                    score.ResourceScore,
                    score.TotalScore,
                    isWinner,
                    i,
                    ShowFinalScoreDetailsForPlayer);

                var selectorRow = Instantiate(view.DetailPlayerRowTemplate, view.DetailPlayerRowsContainer);
                selectorRow.gameObject.SetActive(true);
                selectorRow.Bind(
                    score.PlayerId,
                    playerName,
                    playerColor,
                    playerId => SelectFinalScoreDetailsPlayer(playerId));
                finalScoreDetailSelectorRows[score.PlayerId] = selectorRow;

                var chart = Instantiate(view.ScoreChartTemplate, view.DetailChartsContainer);
                chart.gameObject.SetActive(true);
                chart.Bind(score.PlayerId, playerName, playerColor, BuildFormula(score));
                PopulateScoreBars(chart.BarsContainer, score);
                finalScoreDetailCharts[score.PlayerId] = chart;
            }

            ApplyFinalScoreDetailsSelection();
            UpdateFinalScoreDetailsEntryLabel();
        }

        private void PopulateScoreBars(RectTransform parent, FinalPlayerScoreState score)
        {
            var maxMagnitude = Mathf.Max(
                1,
                Mathf.Abs(score.BaseScore),
                Mathf.Abs(score.FacilityScore),
                Mathf.Abs(score.CityStyleScore),
                Mathf.Abs(score.RegionScore),
                Mathf.Abs(score.ResourceScore),
                Mathf.Abs(score.TotalScore));

            AddScoreBar(parent, "Base", "基础分", score.BaseScore, maxMagnitude, new Color(0.62f, 0.43f, 0.21f, 1f));
            AddScoreBar(parent, "Facility", "设施分", score.FacilityScore, maxMagnitude, new Color(0.28f, 0.58f, 0.66f, 1f));
            AddScoreBar(parent, "CityStyle", "城市样式分", score.CityStyleScore, maxMagnitude, new Color(0.75f, 0.47f, 0.2f, 1f));
            AddScoreBar(parent, "Region", "区控分", score.RegionScore, maxMagnitude, new Color(0.32f, 0.62f, 0.38f, 1f));
            AddScoreBar(parent, "Resource", "资源分", score.ResourceScore, maxMagnitude, new Color(0.55f, 0.43f, 0.72f, 1f));
            AddScoreBar(parent, "Total", "总分", score.TotalScore, maxMagnitude, new Color(0.84f, 0.66f, 0.27f, 1f));
        }

        private void AddScoreBar(
            RectTransform parent,
            string scoreKey,
            string label,
            int value,
            int maxMagnitude,
            Color fillColor)
        {
            var bar = Instantiate(view.ScoreBarTemplate, parent);
            bar.gameObject.SetActive(true);
            bar.Bind(scoreKey, label, value, maxMagnitude, fillColor);
        }

        private void ClearFinalScoreDynamics()
        {
            ClearDynamicChildren(view.RankingRowsContainer);
            ClearDynamicChildren(view.DetailPlayerRowsContainer);
            ClearDynamicChildren(view.DetailChartsContainer);
            finalScoreDetailCharts.Clear();
            finalScoreDetailSelectorRows.Clear();
        }

        private static void ClearDynamicChildren(RectTransform parent)
        {
            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                if (UnityEngine.Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    DestroyImmediate(child);
                }
            }
        }

        public void ToggleFinalScoreDetails()
        {
            if (IsFinalScoreDetailsOpen)
            {
                CloseFinalScoreDetails();
                return;
            }

            OpenFinalScoreDetails();
        }

        public void OpenFinalScoreDetails()
        {
            if (!IsConfigured || SelectedFinalScorePlayerId < 0)
            {
                return;
            }

            finalScoreSummaryView.SetActive(false);
            finalScoreDetailsView.SetActive(true);
            IsFinalScoreDetailsOpen = true;
            UpdateFinalScoreDetailsEntryLabel();
        }

        public void CloseFinalScoreDetails()
        {
            if (!IsConfigured)
            {
                return;
            }

            finalScoreDetailsView.SetActive(false);
            finalScoreSummaryView.SetActive(true);
            IsFinalScoreDetailsOpen = false;
            UpdateFinalScoreDetailsEntryLabel();
        }

        public void ShowFinalScoreDetailsForPlayer(int playerId)
        {
            if (SelectFinalScoreDetailsPlayer(playerId))
            {
                OpenFinalScoreDetails();
            }
        }

        public bool SelectFinalScoreDetailsPlayer(int playerId)
        {
            if (!finalScoreDetailCharts.ContainsKey(playerId))
            {
                return false;
            }

            SelectedFinalScorePlayerId = playerId;
            ApplyFinalScoreDetailsSelection();
            return true;
        }

        private void ApplyFinalScoreDetailsSelection()
        {
            foreach (var pair in finalScoreDetailCharts)
            {
                pair.Value.gameObject.SetActive(pair.Key == SelectedFinalScorePlayerId);
            }

            foreach (var pair in finalScoreDetailSelectorRows)
            {
                pair.Value.SetSelected(pair.Key == SelectedFinalScorePlayerId);
            }
        }

        private void UpdateFinalScoreDetailsEntryLabel()
        {
            finalScoreDetailsButtonLabel.text = IsFinalScoreDetailsOpen ? "<  收起" : "详情  >";
        }

        private void MoveMarkerToCurrentIndex()
        {
            for (var i = 0; i < playerMarkers.Count; i++)
            {
                var marker = playerMarkers[i].MarkerTransform;
                marker.anchoredPosition = new Vector2(GetSlotX(currentIndex), marker.anchoredPosition.y);
            }

            markerTransform = playerMarkers.Count == 0 ? null : playerMarkers[0].MarkerTransform;
        }

        private static float GetSlotX(int index)
        {
            return (index - (RoundLabels.Length - 1) * 0.5f) * 65f;
        }

        private static string BuildFormula(FinalPlayerScoreState score)
        {
            return "计分公式：基础分 " + score.BaseScore +
                   " + 设施分 " + score.FacilityScore +
                   " + 城市样式分 " + score.CityStyleScore +
                   " + 区控分 " + score.RegionScore +
                   " + 资源分 " + score.ResourceScore +
                   " = 总分 " + score.TotalScore;
        }

        private static int ResolveDefaultDetailsPlayerId(
            FinalScoringState scoring,
            List<FinalPlayerScoreState> orderedScores)
        {
            if (orderedScores == null || orderedScores.Count == 0)
            {
                return -1;
            }

            if (scoring != null && scoring.WinnerPlayerIds != null)
            {
                for (var i = 0; i < orderedScores.Count; i++)
                {
                    if (scoring.WinnerPlayerIds.Contains(orderedScores[i].PlayerId))
                    {
                        return orderedScores[i].PlayerId;
                    }
                }
            }

            return orderedScores[0].PlayerId;
        }

        private static List<FinalPlayerScoreState> BuildOrderedFinalScores(List<FinalPlayerScoreState> scores)
        {
            var ordered = scores == null
                ? new List<FinalPlayerScoreState>()
                : new List<FinalPlayerScoreState>(scores);
            ordered.Sort((left, right) =>
            {
                var comparison = right.TotalScore.CompareTo(left.TotalScore);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = right.GoldVoucherTiebreaker.CompareTo(left.GoldVoucherTiebreaker);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = right.PureOriginiumTiebreaker.CompareTo(left.PureOriginiumTiebreaker);
                return comparison != 0 ? comparison : left.PlayerId.CompareTo(right.PlayerId);
            });
            return ordered;
        }

        private static string FormatWinnerIds(List<int> winnerPlayerIds)
        {
            if (winnerPlayerIds == null || winnerPlayerIds.Count == 0)
            {
                return "无";
            }

            var result = string.Empty;
            for (var i = 0; i < winnerPlayerIds.Count; i++)
            {
                if (i > 0)
                {
                    result += "、";
                }

                result += "P" + winnerPlayerIds[i];
            }

            return result;
        }
    }
}
