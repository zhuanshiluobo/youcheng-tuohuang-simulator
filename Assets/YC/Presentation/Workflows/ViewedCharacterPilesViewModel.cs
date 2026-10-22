using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    /// <summary>临时查看只消费真正查看者的授权投影，不参与本人行动或隐藏牌身份解析。</summary>
    public sealed class ViewedCharacterPilesViewModel
    {
        public string GameId { get; private set; }
        public int ViewerPlayerId { get; private set; }
        public int PlayerId { get; private set; }
        public int StateRevision { get; private set; }
        public int HandCardCount { get; private set; }
        public int CoveredCardCount { get; private set; }
        public int DiscardCardCount { get; private set; }
        public bool HasCoveredProjection { get; private set; }
        public List<string> HandBackIds { get; } = new List<string>();
        public List<string> CoveredBackIds { get; } = new List<string>();
        public List<CharacterCardHandItemViewModel> DiscardCards { get; } = new List<CharacterCardHandItemViewModel>();
        private readonly Dictionary<string, string> backs = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> definitions = new Dictionary<string, string>(StringComparer.Ordinal);

        public string ResolveBackVisualKey(string displayId) => backs.TryGetValue(displayId, out var key) ? key : string.Empty;
        public string ResolveDefinitionId(string cardId) => definitions.TryGetValue(cardId, out var id) ? id : string.Empty;

        public static ViewedCharacterPilesViewModel BuildInspectionView(GameStateView visible, int inspectedPlayerId)
        {
            if (visible == null || visible.ViewerRole != GameStateViewerRole.Player ||
                !visible.Players.Exists(player => player.PlayerId == inspectedPlayerId)) return null;
            var result = new ViewedCharacterPilesViewModel
            {
                GameId = visible.GameId, ViewerPlayerId = visible.ViewerPlayerId,
                PlayerId = inspectedPlayerId, StateRevision = visible.StateRevision
            };
            var hand = new Dictionary<string, int>(StringComparer.Ordinal);
            var covered = new Dictionary<string, int>(StringComparer.Ordinal);
            var discardZones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var zone in visible.ItemZones.Zones)
            {
                if (zone.Zone.OwnerPlayerId != inspectedPlayerId) continue;
                switch (zone.Zone.TemplateId)
                {
                    case "core.zone.character_hand":
                        result.HandCardCount += zone.ItemCount;
                        AddGroups(hand, zone.CardBackGroups);
                        break;
                    case "core.zone.character_covered":
                        result.HasCoveredProjection = true;
                        result.CoveredCardCount += zone.ItemCount;
                        AddGroups(covered, zone.CardBackGroups);
                        break;
                    case "core.zone.character_discard":
                        result.DiscardCardCount += zone.ItemCount;
                        if (zone.IdentitiesVisible) discardZones.Add(zone.Zone.ZoneId);
                        break;
                }
            }
            result.ExpandBacks(hand, "hand", result.HandBackIds);
            result.ExpandBacks(covered, "covered", result.CoveredBackIds);
            // 只读公开区域，不使用其他玩家的 legacy 身份列表或任何 Host 回退。
            foreach (var item in visible.ItemZones.Items)
            {
                if (!discardZones.Contains(item.ZoneId)) continue;
                result.definitions[item.ItemId] = item.DefinitionId;
                result.DiscardCards.Add(new CharacterCardHandItemViewModel(item.ItemId,
                    CharacterCardPanelPresenter.ResolveCardDisplayName(item.DefinitionId), false));
            }
            return result;
        }

        private static void AddGroups(IDictionary<string, int> counts, IEnumerable<CardBackGroupView> groups)
        {
            if (groups == null) return;
            foreach (var group in groups)
            {
                if (group == null || group.Count <= 0 || string.IsNullOrEmpty(group.BackVisualKey)) continue;
                counts.TryGetValue(group.BackVisualKey, out var count);
                counts[group.BackVisualKey] = count + group.Count;
            }
        }

        private void ExpandBacks(IDictionary<string, int> counts, string area, ICollection<string> displayIds)
        {
            foreach (var pair in counts.OrderBy(value => value.Key, StringComparer.Ordinal))
                for (var index = 0; index < pair.Value; index++)
                {
                    // 该序号仅展开公开数量，与真实实例、顺序或身份散列均无关联。
                    var id = "hidden-" + area + ":" + PlayerId.ToString(CultureInfo.InvariantCulture) + ":" +
                        pair.Key + ":" + index.ToString(CultureInfo.InvariantCulture);
                    displayIds.Add(id);
                    backs.Add(id, pair.Key);
                }
        }
    }
}
