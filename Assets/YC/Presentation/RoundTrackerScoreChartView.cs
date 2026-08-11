using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RoundTrackerScoreChartView : MonoBehaviour
    {
        [SerializeField] private Image playerBadge;
        [SerializeField] private Text playerNameText;
        [SerializeField] private Text formulaText;
        [SerializeField] private RectTransform barsContainer;

        public RectTransform BarsContainer => barsContainer;

        public void Bind(int playerId, string playerName, Color playerColor, string formula)
        {
            gameObject.name = "Final Score Detail Chart P" + playerId;
            playerBadge.gameObject.name = "Final Score Detail Chart Player Color P" + playerId;
            playerBadge.color = playerColor;
            playerNameText.gameObject.name = "Final Score Detail Chart Player Name P" + playerId;
            playerNameText.text = playerName + "（P" + playerId + "）";
            formulaText.gameObject.name = "Final Score Detail Formula P" + playerId;
            formulaText.text = formula;
        }
    }
}
