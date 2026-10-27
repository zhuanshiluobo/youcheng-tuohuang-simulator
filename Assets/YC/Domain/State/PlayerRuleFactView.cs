using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.State
{
    /// <summary>当前观察者可见且确已选择的规则事实。只用于显示，不是规则授权输入。</summary>
    [Serializable]
    public sealed class PlayerRuleFactView
    {
        public string FactId = "", SourceItemId = "", RuleOwnerItemId = "", RuleRef = "";
        public int DetailPlayerId = -1;
    }

    public static class PlayerRuleFactProjector
    {
        public static List<PlayerRuleFactView> Project(GameState state, GameStateView visible)
        {
            var result = new List<PlayerRuleFactView>();
            var setup = state?.EntrepreneurSetup;
            if (visible == null || visible.ViewerRole == GameStateViewerRole.Unknown || setup == null ||
                state.EffectRuntime == null || state.ContentDefinitions == null || setup.ContentVersion != state.ContentPackHash) return result;
            var selectedGroups = setup.Players.SelectMany(p => p.RoundGroups).Where(g => g.SelectedCandidateId.Length > 0).ToList();
            if (selectedGroups.Count == 0) return result;
            var neededItems = new HashSet<string>(setup.Players.Select(p => p.AbilityItemId).Concat(selectedGroups.SelectMany(g =>
                g.Candidates.Where(c => c.CandidateId == g.SelectedCandidateId).Select(c => c.EntityItemId))), StringComparer.Ordinal);
            var shown = new HashSet<string>(visible.ItemZones.Items.Select(i => i.ItemId), StringComparer.Ordinal);
            var items = state.ItemZones.Items.Where(i => neededItems.Contains(i.ItemId)).GroupBy(i => i.ItemId)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
            var neededZones = new HashSet<string>(items.Values.Select(i => i.ZoneId), StringComparer.Ordinal);
            var zones = state.ItemZones.Zones.Where(z => neededZones.Contains(z.ZoneId)).ToDictionary(z => z.ZoneId, StringComparer.Ordinal);
            var neededNodes = new HashSet<string>(selectedGroups.Select(g => g.OwnerEffectId), StringComparer.Ordinal);
            var nodes = state.EffectRuntime.EffectNodes.Where(n => neededNodes.Contains(n.EffectId)).GroupBy(n => n.EffectId)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
            var neededRequests = new HashSet<string>(selectedGroups.Select(g => g.RequestId), StringComparer.Ordinal);
            var requests = state.EffectRuntime.InteractionRequests.Where(r => neededRequests.Contains(r.GetStableInteractionId())).GroupBy(r => r.GetStableInteractionId())
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
            var definitions = state.ContentDefinitions.Entrepreneurs.ToDictionary(d => d.DefinitionId, StringComparer.Ordinal);
            bool Active(ItemInstanceState item, int player) => item != null && shown.Contains(item.ItemId) &&
                item.OwnerSubject?.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == player &&
                item.ControllerSubject?.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId == player &&
                zones.TryGetValue(item.ZoneId, out var zone) && zone.TemplateId == "core.zone.entrepreneur_active" && zone.OwnerPlayerId == player;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var player in setup.Players)
            {
                if (!items.TryGetValue(player.AbilityItemId, out var owner) || owner.DefinitionId != player.DefinitionId ||
                    !Active(owner, player.PlayerId) || !definitions.TryGetValue(player.DefinitionId, out var definition)) continue;
                foreach (var group in player.RoundGroups.Where(g => g.SelectedCandidateId.Length > 0))
                {
                    var selected = group.Candidates.SingleOrDefault(c => c.CandidateId == group.SelectedCandidateId && c.FaceId == group.ActiveFaceId);
                    if (selected == null) continue;
                    var frozenGroup = definition.RoundGroups.SingleOrDefault(g => g.GroupId == group.GroupId && g.RoundNumber == group.RoundNumber);
                    var frozen = frozenGroup?.Candidates.SingleOrDefault(c => c.CandidateId == group.SelectedCandidateId &&
                        c.FaceId == group.ActiveFaceId && c.EntityDefinitionId == selected?.DefinitionId && c.DisplayOrder == selected.DisplayOrder);
                    if (frozen == null || selected == null || !items.TryGetValue(selected.EntityItemId, out var item) ||
                        item.DefinitionId != selected.DefinitionId || !Active(item, player.PlayerId) ||
                        !player.AllocatedItemIds.Contains(item.ItemId) || !player.AllocatedItemIds.Contains(owner.ItemId) ||
                        Text(item.LocalState, "activeFaceId") != group.ActiveFaceId ||
                        !nodes.TryGetValue(group.OwnerEffectId, out var selecting) || selecting.EffectTypeId != EntrepreneurFlow.FaceTypeId || selecting.PlayerId != player.PlayerId ||
                        !requests.TryGetValue(group.RequestId, out var request) || request.OwnerEffectId != selecting.EffectId ||
                        request.AnsweringPlayerId != player.PlayerId || request.InteractionTypeId != EntrepreneurFlow.FaceInteraction || request.Status != "answered") continue;
                    var receipt = Value(item.LocalState, "sourceSelectionReceipt");
                    if (Text(receipt, "requestId") != group.RequestId || Text(receipt, "ownerEffectId") != group.OwnerEffectId ||
                        Text(receipt, "candidateId") != group.SelectedCandidateId || !AbilitySourceSelection.HasSelectedFaceReceipt(state, item, group.ActiveFaceId, player.PlayerId)) continue;
                    foreach (string rule in frozen.RuleRefs.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal))
                    {
                        string id = StableIdFactory.Create("player_rule", player.PlayerId.ToString(CultureInfo.InvariantCulture), owner.ItemId, item.ItemId, rule);
                        if (seen.Add(id)) result.Add(new PlayerRuleFactView { FactId = id, SourceItemId = item.ItemId,
                            RuleOwnerItemId = owner.ItemId, DetailPlayerId = player.PlayerId, RuleRef = rule });
                    }
                }
            }
            return result;
        }
    }
}
