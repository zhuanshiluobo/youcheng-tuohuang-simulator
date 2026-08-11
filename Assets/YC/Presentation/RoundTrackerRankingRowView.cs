using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RoundTrackerRankingRowView : MonoBehaviour
    {
        [SerializeField] private Image background;
        [SerializeField] private Image playerBadge;
        [SerializeField] private Text playerNameText;
        [SerializeField] private Text liveScoreText;
        [SerializeField] private Text regionScoreText;
        [SerializeField] private Text resourceScoreText;
        [SerializeField] private Text totalScoreText;
        [SerializeField] private Button detailsButton;

        public void Bind(
            int playerId,
            string playerName,
            Color playerColor,
            int liveScore,
            int regionScore,
            int resourceScore,
            int totalScore,
            bool isWinner,
            int rowIndex,
            Action<int> showDetails)
        {
            gameObject.name = "Final Score Row P" + playerId;
            background.color = isWinner
                ? new Color(0.48f, 0.36f, 0.13f, 0.72f)
                : rowIndex % 2 == 0
                    ? new Color(0.15f, 0.09f, 0.045f, 0.7f)
                    : new Color(0.1f, 0.065f, 0.035f, 0.7f);
            playerBadge.color = playerColor;
            playerNameText.text = isWinner ? "★ " + playerName : playerName;
            liveScoreText.text = liveScore.ToString();
            regionScoreText.text = regionScore.ToString();
            resourceScoreText.text = resourceScore.ToString();
            totalScoreText.text = totalScore.ToString();
            detailsButton.gameObject.name = "Final Score Player Details Button P" + playerId;
            detailsButton.onClick.RemoveAllListeners();
            detailsButton.onClick.AddListener(() => showDetails(playerId));
        }
    }
}
