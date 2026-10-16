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
            foreach (var fact in view.PlayerEffects)
            {
                var definition = catalog.Find(value => value.DefinitionId == fact.DefinitionId && value.AbilityId == fact.AbilityId && value.Activation == fact.Activation);
                Add(result, view, definition, fact);
            }
            foreach (var item in view.ItemZones.Items.Where(item => item.ControllerSubject.Kind == ItemSubjectKind.Player))
            {
                var zone = view.ItemZones.Zones.Find(value => value.Zone.ZoneId == item.ZoneId)?.Zone;
                if (zone?.TemplateId != "core.zone.player_awards" || zone.OwnerPlayerId != item.ControllerSubject.PlayerId) continue;
                foreach (var definition in catalog.Where(value => value.DefinitionId == item.DefinitionId && value.Activation == PlayerDetailActivation.Held))
                    Add(result, view, definition, new PlayerEffectFactView { FactId = item.ItemId, SourceItemId = item.ItemId,
                        DefinitionId = item.DefinitionId, AbilityId = definition.AbilityId, Activation = PlayerDetailActivation.Held, DetailPlayerId = zone.OwnerPlayerId });
            }
            return result;
        }

        private static void Add(List<PlayerDetailEntry> entries, GameStateView view, PlayerDetailDisplayDefinition definition, PlayerEffectFactView fact)
        {
            if (definition == null || (definition.DisplayCategory != PlayerDetailDisplayCategory.PlayerEffect && definition.DisplayCategory != PlayerDetailDisplayCategory.StorageArea) ||
                string.IsNullOrWhiteSpace(definition.EffectText) || !view.Players.Any(player => player.PlayerId == fact.DetailPlayerId)) return;
            var source = view.ItemZones.Items.Find(item => item.ItemId == fact.SourceItemId);
            if (source == null) return;
            var entry = new PlayerDetailEntry { EntryId = StableIdFactory.Create("player_detail", fact.DetailPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.FactId, fact.AbilityId),
                SourceItemId = fact.SourceItemId, SourceEffectId = fact.SourceEffectId, AbilityId = fact.AbilityId,
                DetailPlayerId = fact.DetailPlayerId, DisplayName = definition.DisplayName, EffectText = definition.EffectText,
                EntryKind = definition.DisplayCategory == PlayerDetailDisplayCategory.StorageArea ? PlayerDetailEntryKind.StorageArea : PlayerDetailEntryKind.Effect,
                BindingId = fact.BindingId };
            if (fact.Activation == PlayerDetailActivation.Subscription)
            {
                entry.StorageZoneIds.AddRange(view.ItemZones.Zones.Where(value => value.Zone.ParentKind == ZoneParentKind.Item &&
                    value.Zone.ParentId == fact.SourceItemId && definition.StorageLocalZoneIds.Contains(value.Zone.LocalZoneId)).Select(value => value.Zone.ZoneId));
                entry.HasCoverArea = definition.IncludeCoverSlots;
                entry.CanInspectCoverContents = view.ViewerRole == GameStateViewerRole.Player && view.ViewerPlayerId == fact.DetailPlayerId;
                if (entry.HasCoverArea) entry.CoverSlotIds.AddRange(fact.CoverSlotIds);
            }
            if (!entries.Any(value => value.EntryId == entry.EntryId)) entries.Add(entry);
        }
    }
}
