using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public enum PlayerDetailEntryKind { Effect, StorageArea }
    public sealed class PlayerDetailEntry
    {
        public string EntryId, SourceItemId, SourceEffectId, AbilityId, DisplayName, EffectText, BindingId;
        // 业务载体仍由SourceItemId绑定；只读看卡可以来自实际授予该条规则的实例。
        public string InspectionSourceItemId, InspectionDefinitionId, InspectionFaceId;
        public YC.Domain.Presentation.GameplayTextRef EffectTextRef;
        public int DetailPlayerId;
        public bool HasCoverArea, CanInspectCoverContents;
        public PlayerDetailEntryKind EntryKind;
        public List<string> StorageZoneIds = new List<string>(), CoverSlotIds = new List<string>();
    }

    /// <summary>只消费当前观察者投影；不以卡名、OptionId或日志猜测能力。</summary>
    public static class PlayerDetailEntryProjector
    {
        public static IReadOnlyList<PlayerDetailEntry> Project(GameStateView view, IEnumerable<PlayerDetailDisplayDefinition> definitions)
        {
            var result = new List<PlayerDetailEntry>();
            if (view == null || view.ViewerRole == GameStateViewerRole.Unknown) return result;
            var catalog = (definitions ?? Enumerable.Empty<PlayerDetailDisplayDefinition>()).ToList();
            var items = view.ItemZones.Items.ToDictionary(i => i.ItemId, StringComparer.Ordinal);
            var rules = (view.PlayerRules ?? new List<PlayerRuleFactView>()).Where(r => items.ContainsKey(r.SourceItemId) && items.ContainsKey(r.RuleOwnerItemId))
                .ToLookup(r => r.RuleRef, StringComparer.Ordinal);
            foreach (var fact in view.PlayerEffects)
            {
                foreach (var definition in catalog.Where(value => value.DefinitionId == fact.DefinitionId && value.AbilityId == fact.AbilityId && value.Activation == fact.Activation))
                    Add(result, view, definition, fact, items, rules);
            }
            // 纯规则段沿真实已选规则来源显示，不注册虚构能力或借用其它动作来源。
            foreach (var rule in view.PlayerRules ?? new List<PlayerRuleFactView>())
            {
                if (!items.TryGetValue(rule.SourceItemId, out var source)) continue;
                foreach (var definition in catalog.Where(d => d.DefinitionId == source.DefinitionId && d.Activation == PlayerDetailActivation.Held &&
                    string.IsNullOrEmpty(d.AbilityId) && !string.IsNullOrEmpty(d.RequiredRuleRef) && d.RequiredRuleBinding == "owner" && d.RequiredRuleRef == rule.RuleRef))
                    Add(result, view, definition, new PlayerEffectFactView { FactId = rule.FactId, SourceItemId = source.ItemId,
                        DefinitionId = source.DefinitionId, DetailPlayerId = rule.DetailPlayerId, Activation = PlayerDetailActivation.Held }, items, rules);
            }
            foreach (var item in view.ItemZones.Items.Where(item => !view.HasAbilityActionCatalog && item.ControllerSubject.Kind == ItemSubjectKind.Player))
            {
                var zone = view.ItemZones.Zones.Find(value => value.Zone.ZoneId == item.ZoneId)?.Zone;
                if (zone?.TemplateId != "core.zone.player_rewards" || zone.OwnerPlayerId != item.ControllerSubject.PlayerId) continue;
                foreach (var definition in catalog.Where(value => value.DefinitionId == item.DefinitionId && value.Activation == PlayerDetailActivation.Held))
                    Add(result, view, definition, new PlayerEffectFactView { FactId = item.ItemId, SourceItemId = item.ItemId,
                        DefinitionId = item.DefinitionId, AbilityId = definition.AbilityId, Activation = PlayerDetailActivation.Held, DetailPlayerId = zone.OwnerPlayerId }, items, rules);
            }
            return result;
        }

        private static void Add(List<PlayerDetailEntry> entries, GameStateView view, PlayerDetailDisplayDefinition definition, PlayerEffectFactView fact,
            IReadOnlyDictionary<string, ItemInstanceState> items, ILookup<string, PlayerRuleFactView> rules)
        {
            if (definition == null || (definition.DisplayCategory != PlayerDetailDisplayCategory.PlayerEffect && definition.DisplayCategory != PlayerDetailDisplayCategory.StorageArea) ||
                (string.IsNullOrWhiteSpace(definition.EffectText) && definition.EffectTextRef == null) || !view.Players.Any(player => player.PlayerId == fact.DetailPlayerId) ||
                !MatchesRule(definition, fact, rules)) return;
            if (!items.ContainsKey(fact.SourceItemId)) return;
            var inspection = items[fact.SourceItemId];
            if (!string.IsNullOrEmpty(definition.RequiredRuleRef) && definition.RequiredRuleBinding == "grant")
            {
                var grants = rules[definition.RequiredRuleRef].Where(rule => rule.DetailPlayerId == fact.DetailPlayerId &&
                    rule.RuleOwnerItemId == fact.SourceItemId && fact.GrantingRuleFactIds?.Contains(rule.FactId) == true)
                    .Select(rule => rule.SourceItemId).Distinct(StringComparer.Ordinal).ToArray();
                // 缺失或多个不同实体不能任选一张，更不能退回能力载体冒充本条来源。
                inspection = grants.Length == 1 ? items[grants[0]] : null;
            }
            string[] identity = { fact.DetailPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.FactId, fact.AbilityId };
            if (!string.IsNullOrEmpty(definition.RequiredRuleRef)) identity = identity.Concat(new[] { definition.RequiredRuleRef, definition.RequiredRuleBinding }).ToArray();
            var entry = new PlayerDetailEntry { EntryId = StableIdFactory.Create("player_detail", identity),
                SourceItemId = fact.SourceItemId, SourceEffectId = fact.SourceEffectId, AbilityId = fact.AbilityId,
                InspectionSourceItemId = inspection?.ItemId ?? string.Empty,
                InspectionDefinitionId = inspection?.DefinitionId ?? string.Empty,
                InspectionFaceId = SourceFaceId(inspection),
                DetailPlayerId = fact.DetailPlayerId, DisplayName = definition.DisplayName, EffectText = definition.EffectText,
                EffectTextRef = definition.EffectTextRef?.Clone(),
                EntryKind = definition.DisplayCategory == PlayerDetailDisplayCategory.StorageArea ? PlayerDetailEntryKind.StorageArea : PlayerDetailEntryKind.Effect,
                BindingId = fact.BindingId };
            if (fact.Activation == PlayerDetailActivation.Subscription || view.HasAbilityActionCatalog && fact.Activation == PlayerDetailActivation.Held)
            {
                entry.StorageZoneIds.AddRange(view.ItemZones.Zones.Where(value => value.Zone.ParentKind == ZoneParentKind.Item &&
                    value.Zone.ParentId == fact.SourceItemId && definition.StorageLocalZoneIds.Contains(value.Zone.LocalZoneId)).Select(value => value.Zone.ZoneId));
                entry.HasCoverArea = definition.IncludeCoverSlots;
                entry.CanInspectCoverContents = view.ViewerRole == GameStateViewerRole.Player && view.ViewerPlayerId == fact.DetailPlayerId;
                if (entry.HasCoverArea) entry.CoverSlotIds.AddRange(fact.CoverSlotIds);
            }
            if (!entries.Any(value => value.EntryId == entry.EntryId)) entries.Add(entry);
        }

        private static bool MatchesRule(PlayerDetailDisplayDefinition definition, PlayerEffectFactView fact, ILookup<string, PlayerRuleFactView> rules)
        {
            if (string.IsNullOrEmpty(definition.RequiredRuleRef)) return true;
            if (definition.RequiredRuleBinding != "owner" && definition.RequiredRuleBinding != "grant") return false;
            return rules[definition.RequiredRuleRef].Any(rule => rule.DetailPlayerId == fact.DetailPlayerId &&
                (definition.RequiredRuleBinding == "grant" ? rule.RuleOwnerItemId == fact.SourceItemId && fact.GrantingRuleFactIds?.Contains(rule.FactId) == true :
                    rule.SourceItemId == fact.SourceItemId || rule.RuleOwnerItemId == fact.SourceItemId));
        }

        public static string SourceFaceId(ItemInstanceState item) =>
            item?.LocalState?.Properties?.FirstOrDefault(field => field.Name == "activeFaceId")?.Value?.StringValue ?? string.Empty;

        public static bool SameInspectionSource(PlayerDetailEntry expected, PlayerDetailEntry current) =>
            expected != null && current != null && expected.EntryId == current.EntryId &&
            expected.DetailPlayerId == current.DetailPlayerId && expected.EntryKind == current.EntryKind &&
            expected.SourceItemId == current.SourceItemId && expected.SourceEffectId == current.SourceEffectId &&
            expected.AbilityId == current.AbilityId && expected.BindingId == current.BindingId &&
            expected.InspectionSourceItemId == current.InspectionSourceItemId &&
            expected.InspectionDefinitionId == current.InspectionDefinitionId && expected.InspectionFaceId == current.InspectionFaceId;
    }
}
