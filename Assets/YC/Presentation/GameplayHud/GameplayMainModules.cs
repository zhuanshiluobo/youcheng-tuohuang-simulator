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
        [SerializeField] private InfluenceMarker2DView[] opponentSupplyMarkers;
        [SerializeField] private Text[] opponentHandCount;
        [SerializeField] private Text[] opponentStyleCount;
        [SerializeField] private Image[] opponentColor;
        [SerializeField] private Button[] opponentDetailToggles;
        [SerializeField] private GameObject[] opponentDetailPanels;
        [SerializeField] private RectTransform[] opponentDetailContents;
        [SerializeField] private GameObject[] opponentDetailEmptyHints;
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
        [SerializeField] private InfluenceMarker2DView localSupplyMarker;
        [SerializeField] private Button localDetailToggle;
        [SerializeField] private GameObject localDetailPanel;
        [SerializeField] private RectTransform[] localDetailContents;
        [SerializeField] private GameObject[] localDetailEmptyHints;
        [SerializeField] private Text discardCountText;
        [SerializeField] private Button discardPreviewButton;
        [SerializeField] private string discardCountFormat = "弃牌 {0}";
        [SerializeField] private string unnamedPlayerFormat = "玩家 {0}";
        private readonly int[] opponentIds = { -1, -1, -1 };
        private readonly int[] detailCounts = new int[3];
        private int expandedPlayerId = -1;
        private int currentLocalPlayerId = -1;
        private int localDetailCount;
        private bool localDetailExpanded;
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
            discardPile.RenderSprites(discarded, id => catalog.GetCharacterFront(id), null);
            coveredPile.RenderTextures(covered, _ => catalog.GetCharacterBack(model.CoveredCardBackColor),
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
            for (var i = 0; opponentDetailToggles != null && i < opponentDetailToggles.Length; i++)
            {
                if (opponentDetailToggles == null || i >= opponentDetailToggles.Length ||
                    opponentDetailToggles[i] == null) continue;
                var slot = i;
                opponentDetailToggles[i].onClick.RemoveAllListeners();
                opponentDetailToggles[i].onClick.AddListener(() => ToggleOpponent(slot));
            }
            if (localDetailToggle != null) localDetailToggle.onClick.AddListener(ToggleLocalDetail);
            RefreshDetailAvailability();
        }

        // 内容宿主在面板收起时仍可填充；仅在实际内容变化后更新显示与布局输入。
        private void LateUpdate() => RefreshDetailAvailability();

        public bool TryValidateConfiguration(out string reason)
        {
            if (opponentData == null || opponentData.Length != 3 ||
                opponentResourceValues == null || opponentResourceValues.Length != 15 ||
                opponentName == null || opponentName.Length != 3 ||
                opponentScore == null || opponentScore.Length != 3 ||
                opponentSupplyMarkers == null || opponentSupplyMarkers.Length != 3 ||
                opponentHandCount == null || opponentHandCount.Length != 3 ||
                opponentStyleCount == null || opponentStyleCount.Length != 3 ||
                opponentColor == null || opponentColor.Length != 3 ||
                opponentDetailToggles == null || opponentDetailToggles.Length != 3 ||
                opponentDetailPanels == null || opponentDetailPanels.Length != 3 ||
                opponentDetailContents == null || opponentDetailContents.Length != 3 ||
                opponentDetailEmptyHints == null || opponentDetailEmptyHints.Length != 3 ||
                opponentSeats == null || opponentSeats.Length != 3 ||
                cityRegion == null || cityBoardArtwork == null || playerCityLayout == null ||
                detailToggleCollapsed == null || detailToggleExpanded == null ||
                detailToggleDown == null || detailToggleUp == null ||
                coveredPile == null || !coveredPile.IsConfigured ||
                discardPile == null || !discardPile.IsConfigured || coveredCountText == null ||
                localResourceValues == null || localResourceValues.Length != 4 ||
                localVoucherValue == null || localScoreValue == null ||
                localSupplyMarker == null || !localSupplyMarker.ShowNumber ||
                localDetailToggle == null || localDetailPanel == null ||
                localDetailContents == null || localDetailContents.Length != 2 ||
                localDetailEmptyHints == null || localDetailEmptyHints.Length != 2 ||
                discardCountText == null || discardPreviewButton == null)
            {
                reason = "主界面玩家或牌堆投影引用不完整。";
                return false;
            }
            for (var i = 0; i < 3; i++)
            {
                if (opponentData[i] == null ||
                    opponentName[i] == null || opponentScore[i] == null ||
                    opponentSupplyMarkers[i] == null || !opponentSupplyMarkers[i].ShowNumber ||
                    opponentHandCount[i] == null || opponentStyleCount[i] == null ||
                    opponentColor[i] == null)
                {
                    reason = "主界面对手席位 " + (i + 1) + " 引用不完整。";
                    return false;
                }
                if (opponentDetailToggles[i] == null || opponentDetailPanels[i] == null ||
                    opponentDetailContents[i] == null || opponentDetailEmptyHints[i] == null ||
                    opponentSeats[i] == null)
                {
                    reason = "主界面对手明细宿主引用不完整。";
                    return false;
                }
            }
            for (var i = 0; i < localDetailContents.Length; i++)
            {
                if (localDetailContents[i] != null && localDetailEmptyHints[i] != null) continue;
                reason = "主界面玩家供应堆双栏详情宿主引用不完整。";
                return false;
            }
            foreach (var value in opponentResourceValues)
            {
                if (value != null) continue;
                reason = "主界面对手资源栏引用不完整。";
                return false;
            }
            if (!localSupplyMarker.TryValidateConfiguration(out reason)) return false;
            foreach (var marker in opponentSupplyMarkers)
                if (!marker.TryValidateConfiguration(out reason)) return false;
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
                        player.Score, player.Resources, player.InfluenceSupply, player.HandCardCount,
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
                        player.Score, player.Resources, player.InfluenceSupply,
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
            var localScore = 0;
            var hasLocalSupply = false;
            if (visible != null && visible.Players != null)
            {
                foreach (var player in visible.Players)
                {
                    if (player == null || player.PlayerId != localPlayerId) continue;
                    localScore = player.Score;
                    hasLocalSupply = true;
                    localSupplyMarker.RenderPlayer(player.Color, player.InfluenceSupply);
                    break;
                }
            }
            else
            {
                var local = state == null ? null : state.FindPlayer(localPlayerId);
                localScore = local == null ? 0 : local.Score;
                if (local != null)
                {
                    hasLocalSupply = true;
                    localSupplyMarker.RenderPlayer(local.Color, local.InfluenceSupply);
                }
            }
            localSupplyMarker.gameObject.SetActive(hasLocalSupply);
            var nextLocalPlayerId = hasLocalSupply ? localPlayerId : -1;
            if (currentLocalPlayerId != nextLocalPlayerId)
            {
                for (var i = 0; localDetailContents != null && i < localDetailContents.Length; i++)
                    ClearDetailHost(localDetailContents[i], localDetailEmptyHints[i]);
                localDetailExpanded = false;
                currentLocalPlayerId = nextLocalPlayerId;
            }
            var localState = state == null ? null : state.FindPlayer(localPlayerId);
            var resources = localState == null ? null : localState.Resources;
            RenderResources(resources);
            RenderScore(localScore);
            RefreshDetailAvailability();
            UpdateExpandedLayout();
        }

        private void RenderOpponent(int slot, int playerId, string name,
            YC.Domain.Rules.PlayerColor color, int score, ResourceSet resources,
            int influenceSupply, int handCount, int styleCount)
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
            opponentSupplyMarkers[slot].RenderPlayer(color, influenceSupply);
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

        /// <summary>只读投影适配器在此插入实际详情条目；空宿主不提供展开入口。</summary>
        public RectTransform GetOpponentDetailHost(int playerId)
        {
            for (var i = 0; i < opponentIds.Length; i++)
            {
                if (opponentIds[i] != playerId || opponentDetailPanels[i] == null) continue;
                return opponentDetailContents[i];
            }
            return null;
        }

        public void SetOpponentDetailCount(int playerId, int visibleCount)
        {
            // 保留投影适配器的旧通知入口；上报数量不能让没有填充内容的按钮出现。
            if (Array.IndexOf(opponentIds, playerId) >= 0) RefreshDetailAvailability();
        }

        public RectTransform GetLocalDetailHost(int column)
        {
            return currentLocalPlayerId >= 0 && localDetailContents != null &&
                column >= 0 && column < localDetailContents.Length ? localDetailContents[column] : null;
        }

        public void RefreshDetailAvailability()
        {
            var changed = false;
            for (var i = 0; i < detailCounts.Length; i++)
            {
                var count = opponentIds[i] >= 0 && opponentDetailContents != null &&
                    i < opponentDetailContents.Length
                    ? CountDetailRows(opponentDetailContents[i], opponentDetailEmptyHints[i]) : 0;
                if (count == detailCounts[i]) continue;
                detailCounts[i] = count;
                changed = true;
            }
            var localCount = 0;
            if (currentLocalPlayerId >= 0)
                for (var i = 0; localDetailContents != null && i < localDetailContents.Length; i++)
                    localCount += CountDetailRows(localDetailContents[i], localDetailEmptyHints[i]);
            if (localCount != localDetailCount)
            {
                localDetailCount = localCount;
                changed = true;
            }
            if (changed) UpdateExpandedLayout();
        }

        private static int CountDetailRows(RectTransform host, GameObject emptyHint)
        {
            if (host == null) return 0;
            var count = 0;
            foreach (Transform row in host)
            {
                if (IsDetailRow(row, emptyHint)) count++;
            }
            return count;
        }

        internal static bool IsDetailRow(Transform row, GameObject emptyHint)
        {
            if (row.gameObject == emptyHint || !row.gameObject.activeSelf) return false;
            var element = row.GetComponent<LayoutElement>();
            if (element != null && element.ignoreLayout) return false;
            var effect = row.GetComponent<UiEffectRowView>();
            return (effect == null || !string.IsNullOrEmpty(effect.ItemId)) && HasDetailContent(row);
        }

        private static bool HasDetailContent(Transform item)
        {
            if (!item.gameObject.activeSelf) return false;
            var text = item.GetComponent<Text>();
            if (text != null && text.enabled && !string.IsNullOrWhiteSpace(text.text)) return true;
            var image = item.GetComponent<Image>();
            if (image != null && image.enabled && image.sprite != null) return true;
            var rawImage = item.GetComponent<RawImage>();
            if (rawImage != null && rawImage.enabled && rawImage.texture != null) return true;
            foreach (Transform child in item)
                if (HasDetailContent(child)) return true;
            return false;
        }

        private void ClearOpponentDetailHost(int slot)
        {
            if (opponentDetailContents == null || slot < 0 || slot >= opponentDetailContents.Length) return;
            ClearDetailHost(opponentDetailContents[slot], opponentDetailEmptyHints[slot]);
        }

        private static void ClearDetailHost(RectTransform host, GameObject emptyHint)
        {
            if (host == null) return;
            for (var i = host.childCount - 1; i >= 0; i--)
            {
                var child = host.GetChild(i);
                if (child.gameObject == emptyHint) continue;
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
        }

        private void ToggleOpponent(int slot)
        {
            RefreshDetailAvailability();
            if (slot < 0 || slot >= opponentIds.Length || opponentIds[slot] < 0 || detailCounts[slot] == 0) return;
            expandedPlayerId = expandedPlayerId == opponentIds[slot] ? -1 : opponentIds[slot];
            UpdateExpandedLayout();
        }

        private void ToggleLocalDetail()
        {
            RefreshDetailAvailability();
            if (currentLocalPlayerId < 0 || localDetailCount == 0) return;
            localDetailExpanded = !localDetailExpanded;
            UpdateExpandedLayout();
        }

        private void UpdateExpandedLayout()
        {
            if (playerCityLayout == null) return;
            var expandedSlot = -1;
            for (var i = 0; i < opponentIds.Length; i++)
                if (opponentIds[i] >= 0 && opponentIds[i] == expandedPlayerId && detailCounts[i] > 0) expandedSlot = i;
            if (expandedSlot < 0) expandedPlayerId = -1;
            var desired = expandedSlot < 0 ? 0f :
                detailCounts[expandedSlot] > 1 ? expandedDetailHeight : detailHeight;
            playerCityLayout.SetExpanded(expandedSlot, desired);
            for (var i = 0; i < opponentSeats.Length; i++)
            {
                if (opponentDetailToggles[i] != null)
                {
                    UpdateDetailToggle(opponentDetailToggles[i], opponentIds[i] >= 0 && detailCounts[i] > 0,
                        i == expandedSlot, false);
                }
            }
            if (currentLocalPlayerId < 0 || localDetailCount == 0) localDetailExpanded = false;
            if (localDetailPanel != null) localDetailPanel.SetActive(localDetailExpanded);
            if (localDetailToggle != null)
                UpdateDetailToggle(localDetailToggle, currentLocalPlayerId >= 0 && localDetailCount > 0,
                    localDetailExpanded, true);
        }

        private void UpdateDetailToggle(Button toggle, bool available, bool expanded, bool opensUpward)
        {
            toggle.gameObject.SetActive(available);
            toggle.interactable = available;
            var face = toggle.GetComponent<Image>();
            if (face != null) face.sprite = expanded ? detailToggleExpanded : detailToggleCollapsed;
            var arrow = toggle.transform.Find("Toggle Icon");
            var icon = arrow == null ? null : arrow.GetComponent<Image>();
            if (icon != null) icon.sprite = expanded == opensUpward ? detailToggleDown : detailToggleUp;
        }
    }
}
