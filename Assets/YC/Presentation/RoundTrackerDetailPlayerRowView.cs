using System;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class RoundTrackerDetailPlayerRowView : MonoBehaviour
    {
        [SerializeField] private Image background;
        [SerializeField] private Image playerBadge;
        [SerializeField] private Text playerNameText;
        [SerializeField] private Button switchButton;

        public Image Background => background;

        public void Bind(int playerId, string playerName, Color playerColor, Action<int> selectPlayer)
        {
            gameObject.name = "Final Score Detail Player Row P" + playerId;
            playerBadge.gameObject.name = "Final Score Detail Player Color P" + playerId;
            playerBadge.color = playerColor;
            playerNameText.gameObject.name = "Final Score Detail Player Name P" + playerId;
            playerNameText.text = playerName;
            switchButton.gameObject.name = "Final Score Detail Player Switch P" + playerId;
            switchButton.onClick.RemoveAllListeners();
            switchButton.onClick.AddListener(() => selectPlayer(playerId));
        }

        public void SetSelected(bool selected)
        {
            background.color = selected
                ? new Color(0.48f, 0.36f, 0.13f, 0.9f)
                : new Color(0.17f, 0.105f, 0.05f, 0.82f);
        }
    }
}
