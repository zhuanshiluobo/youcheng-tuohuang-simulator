using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections.Generic;
using YC.Presentation.Workflows;
using YC.Domain.State;

namespace YC.Presentation
{
    /// <summary>主界面公共信息和本机盖放牌背的只读投影。</summary>
    public sealed class GameplayMainModules : MonoBehaviour
    {
        [SerializeField] private GameObject[] opponentData;
        // 每个席位依次为源石、源石碎片、铁、至纯源石、金券。
        [SerializeField] private Text[] opponentResourceValues;
        [SerializeField] private Text[] opponentName;
        [SerializeField] private Text[] opponentScore;
        [SerializeField] private Text[] opponentHandCount;
        [SerializeField] private Text[] opponentStyleCount;
        [SerializeField] private Image[] opponentColor;
        [SerializeField] private Button[] opponentDetailToggles;
        [SerializeField] private GameObject[] opponentDetailPanels;
        [SerializeField] private RectTransform[] opponentSeats;
        [SerializeField] private RectTransform cityRegion;
        [SerializeField] private RawImage cityBoardArtwork;
        [SerializeField] private UiOpponentDetailInput playerCityLayout;
        [SerializeField] private Sprite detailToggleCollapsed;
        [SerializeField] private Sprite detailToggleExpanded;
        [SerializeField] private Sprite detailToggleDown;
        [SerializeField] private Sprite detailToggleUp;
        [SerializeField] private float detailHeight = 108f;
        [SerializeField] private float expandedDetailHeight = 224f;
        [SerializeField] private CardPileView coveredPile;
        [SerializeField] private CardPileView discardPile;
        [SerializeField] private Text coveredCountText;
        [SerializeField] private string coveredCountFormat = "{0}";
        [SerializeField] private Text[] localResourceValues;
        [SerializeField] private Text localVoucherValue;
        [SerializeField] private Text localScoreValue;
        [SerializeField] private Text discardCountText;
        [SerializeField] private Button discardPreviewButton;
        [SerializeField] private string discardCountFormat = "弃牌 {0}";
        [SerializeField] private string unnamedPlayerFormat = "玩家 {0}";
        private readonly int[] opponentIds = { -1, -1, -1 };
        private readonly int[] detailCounts = new int[3];
        private int expandedPlayerId = -1;
        private Action openDiscardPreview;
        private Action openCoveredPreview;
        public Text DiscardCountText => discardCountText;

        public void ConfigureCoveredPreview(Action openPreview)
        {
            openCoveredPreview = openPreview;
        }

        public void RenderCharacterPiles(CharacterCardPanelViewModel model, CardVisualCatalog catalog)
        {
            var discarded = new List<string>();
            var covered = new List<string>();
            if (model != null)
            {
                foreach (var card in model.DiscardCards) discarded.Add(card.CardId);
                if (!string.IsNullOrEmpty(model.CoveredCardId)) covered.Add(model.CoveredCardId);
            }
            // 原顺序由规则提供，最后追加的弃牌为顶牌，不经过手牌排序。
            discardPile.Render(discarded, id => catalog.GetCharacterFront(id), null);
            coveredPile.Render(covered, _ => catalog.GetCharacterBack(model.CoveredCardBackColor),
                _ => openCoveredPreview?.Invoke());
            if (coveredCountText != null)
                coveredCountText.text = string.Format(coveredCountFormat, covered.Count);
        }

        public void ConfigureDiscardPreview(Action openPreview)
        {
            if (discardPreviewButton != null)
            {
                discardPreviewButton.onClick.RemoveListener(OpenDiscardPreview);
                openDiscardPreview = openPreview;
                if (openDiscardPreview != null)
                    discardPreviewButton.onClick.AddListener(OpenDiscardPreview);
                discardPreviewButton.interactable = openDiscardPreview != null;
            }
        }

        public void RenderDiscardCount(int count)
        {
            if (discardCountText != null)
                discardCountText.text = string.Format(discardCountFormat, Mathf.Max(0, count));
        }

        public void RenderResources(ResourceSet resources)
        {
            if (resources == null || localResourceValues == null || localResourceValues.Length != 4 ||
                localVoucherValue == null) return;
            localResourceValues[0].text = resources.Originium.ToString();
            localResourceValues[1].text = resources.OriginiumShard.ToString();
            localResourceValues[2].text = resources.Iron.ToString();
            localResourceValues[3].text = resources.PureOriginium.ToString();
            localVoucherValue.text = resources.GoldVoucher.ToString();
        }

        public void RenderScore(int score)
        {
            if (localScoreValue != null) localScoreValue.text = score.ToString();
        }

        private void OpenDiscardPreview() => openDiscardPreview?.Invoke();
        private void Awake()
        {
            if (opponentSeats == null) return;
            for (var i = 0; i < opponentSeats.Length; i++)
            {
                if (opponentDetailToggles == null || i >= opponentDetailToggles.Length ||
                    opponentDetailToggles[i] == null) continue;
                var slot = i;
                opponentDetailToggles[i].onClick.RemoveAllListeners();
                opponentDetailToggles[i].onClick.AddListener(() => ToggleOpponent(slot));
            }
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (opponentData == null || opponentData.Length != 3 ||
                opponentResourceValues == null || opponentResourceValues.Length != 15 ||
                opponentName == null || opponentName.Length != 3 ||
                opponentScore == null || opponentScore.Length != 3 ||
                opponentHandCount == null || opponentHandCount.Length != 3 ||
                opponentStyleCount == null || opponentStyleCount.Length != 3 ||
                opponentColor == null || opponentColor.Length != 3 ||
                opponentDetailToggles == null || opponentDetailToggles.Length != 3 ||
                opponentDetailPanels == null || opponentDetailPanels.Length != 3 ||
                opponentSeats == null || opponentSeats.Length != 3 ||
                cityRegion == null || cityBoardArtwork == null || playerCityLayout == null ||
                detailToggleCollapsed == null || detailToggleExpanded == null ||
                detailToggleDown == null || detailToggleUp == null ||
                coveredPile == null || !coveredPile.IsConfigured ||
                discardPile == null || !discardPile.IsConfigured || coveredCountText == null ||
                localResourceValues == null || localResourceValues.Length != 4 ||
                localVoucherValue == null || localScoreValue == null ||
                discardCountText == null || discardPreviewButton == null)
            {
                reason = "主界面玩家或牌堆投影引用不完整。";
                return false;
            }
            for (var i = 0; i < 3; i++)
            {
                if (opponentData[i] == null ||
                    opponentName[i] == null || opponentScore[i] == null ||
                    opponentHandCount[i] == null || opponentStyleCount[i] == null ||
                    opponentColor[i] == null)
                {
                    reason = "主界面对手席位 " + (i + 1) + " 引用不完整。";
                    return false;
                }
                if (opponentDetailToggles[i] == null || opponentDetailPanels[i] == null ||
                    opponentSeats[i] == null)
                {
                    reason = "主界面对手明细宿主引用不完整。";
                    return false;
                }
            }
            foreach (var value in opponentResourceValues)
            {
                if (value != null) continue;
                reason = "主界面对手资源栏引用不完整。";
                return false;
            }
            for (var i = 0; i < localResourceValues.Length; i++)
            {
                if (localResourceValues[i] != null) continue;
                reason = "主界面本机资源栏引用不完整。";
                return false;
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
                        player.Score, player.Resources, player.HandCardCount,
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
                        player.Score, player.Resources,
                        player.HandCardIds == null ? 0 : player.HandCardIds.Count,
                        player.DeclaredCityStyles == null ? 0 : player.DeclaredCityStyles.Count);
                }
            }
            for (var i = nextOpponent; i < opponentData.Length; i++)
            {
                if (opponentIds[i] >= 0) ClearOpponentDetailHost(i);
                opponentIds[i] = -1;
                detailCounts[i] = 0;
                opponentData[i].SetActive(false);
            }
            UpdateExpandedLayout();

            var localScore = 0;
            if (visible != null && visible.Players != null)
            {
                foreach (var player in visible.Players)
                {
                    if (player == null || player.PlayerId != localPlayerId) continue;
                    localScore = player.Score;
                    break;
                }
            }
            else
            {
                var local = state == null ? null : state.FindPlayer(localPlayerId);
                localScore = local == null ? 0 : local.Score;
            }
            var localState = state == null ? null : state.FindPlayer(localPlayerId);
            var resources = localState == null ? null : localState.Resources;
            RenderResources(resources);
            RenderScore(localScore);
        }

        private void RenderOpponent(int slot, int playerId, string name,
            YC.Domain.Rules.PlayerColor color, int score, ResourceSet resources, int handCount, int styleCount)
        {
            if (opponentIds[slot] != playerId)
            {
                ClearOpponentDetailHost(slot);
                detailCounts[slot] = 0;
            }
            opponentIds[slot] = playerId;
            opponentData[slot].SetActive(true);
            opponentName[slot].text = string.IsNullOrEmpty(name)
                ? string.Format(unnamedPlayerFormat, playerId) : name;
            opponentScore[slot].text = score.ToString();
            var offset = slot * 5;
            opponentResourceValues[offset].text = (resources?.Originium ?? 0).ToString();
            opponentResourceValues[offset + 1].text = (resources?.OriginiumShard ?? 0).ToString();
            opponentResourceValues[offset + 2].text = (resources?.Iron ?? 0).ToString();
            opponentResourceValues[offset + 3].text = (resources?.PureOriginium ?? 0).ToString();
            opponentResourceValues[offset + 4].text = (resources?.GoldVoucher ?? 0).ToString();
            // 只使用公开数量，不把对手卡 ID、缩略图或详情送到主界面。
            opponentHandCount[slot].text = handCount.ToString();
            opponentStyleCount[slot].text = styleCount.ToString();
            opponentColor[slot].color = UiTheme.GetPlayerColor(color, 1f);
        }

        /// <summary>后续只读投影适配器在此插入 Registry 共用行；当前公开状态无永续条目。</summary>
        public RectTransform GetOpponentDetailHost(int playerId)
        {
            for (var i = 0; i < opponentIds.Length; i++)
            {
                if (opponentIds[i] != playerId || opponentDetailPanels[i] == null) continue;
                return opponentDetailPanels[i].transform.Find("Detail Viewport/Detail Content")
                    as RectTransform;
            }
            return null;
        }

        public void SetOpponentDetailCount(int playerId, int visibleCount)
        {
            for (var i = 0; i < opponentIds.Length; i++)
            {
                if (opponentIds[i] != playerId) continue;
                detailCounts[i] = Mathf.Max(0, visibleCount);
                var empty = opponentDetailPanels[i].transform.Find(
                    "Detail Viewport/Detail Content/No Persistent Effects");
                if (empty != null) empty.gameObject.SetActive(detailCounts[i] == 0);
                UpdateExpandedLayout();
                return;
            }
        }

        private void ClearOpponentDetailHost(int slot)
        {
            if (opponentDetailPanels == null || slot < 0 ||
                slot >= opponentDetailPanels.Length || opponentDetailPanels[slot] == null) return;
            var host = opponentDetailPanels[slot].transform.Find("Detail Viewport/Detail Content");
            if (host == null) return;
            for (var i = host.childCount - 1; i >= 0; i--)
            {
                var child = host.GetChild(i);
                if (child.name == "No Persistent Effects") continue;
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
        }

        private void ToggleOpponent(int slot)
        {
            if (slot < 0 || slot >= opponentIds.Length || opponentIds[slot] < 0) return;
            expandedPlayerId = expandedPlayerId == opponentIds[slot] ? -1 : opponentIds[slot];
            UpdateExpandedLayout();
        }

        private void UpdateExpandedLayout()
        {
            if (playerCityLayout == null) return;
            var expandedSlot = -1;
            for (var i = 0; i < opponentIds.Length; i++)
                if (opponentIds[i] >= 0 && opponentIds[i] == expandedPlayerId) expandedSlot = i;
            if (expandedSlot < 0) expandedPlayerId = -1;
            var desired = expandedSlot < 0 ? 0f :
                detailCounts[expandedSlot] > 0 ? expandedDetailHeight : detailHeight;
            playerCityLayout.SetExpanded(expandedSlot, desired);
            for (var i = 0; i < opponentSeats.Length; i++)
            {
                if (opponentDetailToggles[i] != null)
                {
                    opponentDetailToggles[i].gameObject.SetActive(opponentIds[i] >= 0);
                    var toggleFace = opponentDetailToggles[i].GetComponent<Image>();
                    if (toggleFace != null) toggleFace.sprite = i == expandedSlot
                        ? detailToggleExpanded : detailToggleCollapsed;
                    var arrow = opponentDetailToggles[i].transform.Find("Toggle Icon");
                    var arrowImage = arrow == null ? null : arrow.GetComponent<Image>();
                    if (arrowImage != null) arrowImage.sprite = i == expandedSlot
                        ? detailToggleUp : detailToggleDown;
                }
            }
        }
    }
}
