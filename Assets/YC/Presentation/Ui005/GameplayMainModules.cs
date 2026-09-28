using UnityEngine;
using UnityEngine.UI;
using YC.Domain.State;

namespace YC.Presentation
{
    /// <summary>主界面公共信息和本机盖放牌背的只读投影。</summary>
    public sealed class GameplayMainModules : MonoBehaviour
    {
        [SerializeField] private GameObject[] opponentData;
        [SerializeField] private GameObject[] opponentEmpty;
        [SerializeField] private Text[] opponentName;
        [SerializeField] private Text[] opponentScore;
        [SerializeField] private Text[] opponentInfluenceCount;
        [SerializeField] private Text[] opponentHandCount;
        [SerializeField] private Text[] opponentStyleCount;
        [SerializeField] private Image[] opponentColor;
        [SerializeField] private Image selfColor;
        [SerializeField] private Text selfScore;
        [SerializeField] private RawImage coveredBack;
        [SerializeField] private GameObject coveredEmpty;
        [SerializeField] private Text characterDeckCount;
        [SerializeField] private string unnamedPlayerFormat = "玩家 {0}";

        public bool TryValidateConfiguration(out string reason)
        {
            if (opponentData == null || opponentData.Length != 3 ||
                opponentEmpty == null || opponentEmpty.Length != 3 ||
                opponentName == null || opponentName.Length != 3 ||
                opponentScore == null || opponentScore.Length != 3 ||
                opponentInfluenceCount == null || opponentInfluenceCount.Length != 3 ||
                opponentHandCount == null || opponentHandCount.Length != 3 ||
                opponentStyleCount == null || opponentStyleCount.Length != 3 ||
                opponentColor == null || opponentColor.Length != 3 ||
                selfColor == null || selfScore == null ||
                coveredBack == null || coveredEmpty == null || characterDeckCount == null)
            {
                reason = "主界面玩家或牌堆投影引用不完整。";
                return false;
            }
            for (var i = 0; i < 3; i++)
            {
                if (opponentData[i] == null || opponentEmpty[i] == null ||
                    opponentName[i] == null || opponentScore[i] == null ||
                    opponentInfluenceCount[i] == null ||
                    opponentHandCount[i] == null || opponentStyleCount[i] == null ||
                    opponentColor[i] == null)
                {
                    reason = "主界面对手席位 " + (i + 1) + " 引用不完整。";
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }

        public void Render(GameState state, GameStateView visible,
            int localPlayerId, CardVisualCatalog catalog)
        {
            if (state == null && visible == null) return;
            var nextOpponent = 0;
            if (visible != null && visible.Players != null)
            {
                foreach (var player in visible.Players)
                {
                    if (player == null || player.PlayerId == localPlayerId) continue;
                    if (nextOpponent >= opponentData.Length) break;
                    RenderOpponent(nextOpponent++, player.PlayerId, player.Name, player.Color,
                        player.Score, player.InfluenceSupply, player.HandCardCount,
                        player.DeclaredCityStyleIds == null ? 0 : player.DeclaredCityStyleIds.Count);
                }
            }
            else if (state != null && state.Players != null)
            {
                foreach (var player in state.Players)
                {
                    if (player == null || player.PlayerId == localPlayerId) continue;
                    if (nextOpponent >= opponentData.Length) break;
                    RenderOpponent(nextOpponent++, player.PlayerId, player.Name, player.Color,
                        player.Score, player.InfluenceSupply,
                        player.HandCardIds == null ? 0 : player.HandCardIds.Count,
                        player.DeclaredCityStyles == null ? 0 : player.DeclaredCityStyles.Count);
                }
            }
            for (var i = nextOpponent; i < opponentData.Length; i++)
            {
                opponentData[i].SetActive(false);
                opponentEmpty[i].SetActive(true);
            }

            var hasLocalPlayer = false;
            var localScore = 0;
            var hasCovered = false;
            var localColor = YC.Domain.Rules.PlayerColor.Red;
            if (visible != null && visible.Players != null)
            {
                foreach (var player in visible.Players)
                {
                    if (player == null || player.PlayerId != localPlayerId) continue;
                    hasLocalPlayer = true;
                    localScore = player.Score;
                    hasCovered = player.HasCoveredCharacterCard;
                    localColor = player.Color;
                    break;
                }
            }
            else
            {
                var local = state == null ? null : state.FindPlayer(localPlayerId);
                hasCovered = local != null && !string.IsNullOrEmpty(local.CoveredCharacterCardId);
                if (local != null)
                {
                    hasLocalPlayer = true;
                    localScore = local.Score;
                    localColor = local.Color;
                }
            }
            // 自身名称和资源由 GameplayHudFrame 管理，这里只投影影响力颜色与分数。
            selfColor.enabled = hasLocalPlayer;
            if (hasLocalPlayer) selfColor.color = UiTheme.GetPlayerColor(localColor, 1f);
            selfScore.text = hasLocalPlayer ? localScore.ToString() : string.Empty;

            var back = hasCovered && catalog != null ? catalog.GetCharacterBack(localColor) : null;
            coveredBack.texture = back;
            coveredBack.gameObject.SetActive(back != null);
            coveredEmpty.SetActive(!hasCovered);
            characterDeckCount.text = visible != null && visible.Decks != null
                ? visible.Decks.CharacterDeckCount.ToString()
                : state == null || state.Decks == null || state.Decks.CharacterDeck == null
                    ? "0" : state.Decks.CharacterDeck.Count.ToString();
        }

        private void RenderOpponent(int slot, int playerId, string name,
            YC.Domain.Rules.PlayerColor color, int score, int influenceSupply, int handCount, int styleCount)
        {
            opponentEmpty[slot].SetActive(false);
            opponentData[slot].SetActive(true);
            opponentName[slot].text = string.IsNullOrEmpty(name)
                ? string.Format(unnamedPlayerFormat, playerId) : name;
            opponentScore[slot].text = score.ToString();
            opponentInfluenceCount[slot].text = "× " + Mathf.Max(0, influenceSupply);
            // 只使用公开数量，不把对手卡 ID、缩略图或详情送到主界面。
            opponentHandCount[slot].text = handCount.ToString();
            opponentStyleCount[slot].text = styleCount.ToString();
            opponentColor[slot].color = UiTheme.GetPlayerColor(color, 1f);
        }
    }
}
