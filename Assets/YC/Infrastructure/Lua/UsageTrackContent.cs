using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;

namespace YC.Infrastructure.Lua
{
    public static class UsageTrackContent
    {
        private static void Fields(JToken raw, Type type)
        {
            if (raw is not JObject obj || obj.Properties().Any(p => !type.GetFields().Any(f => string.Equals(f.Name, p.Name, StringComparison.OrdinalIgnoreCase))) ||
                obj.Properties().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != obj.Properties().Count()) throw new InvalidDataException("usage_or_color_unknown_field");
            foreach (var property in obj.Properties())
            {
                var field = type.GetFields().Single(f => string.Equals(f.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (field.FieldType == typeof(string) && property.Value.Type != JTokenType.String || field.FieldType == typeof(int) && property.Value.Type != JTokenType.Integer) throw new InvalidDataException("usage_or_color_field_type_invalid");
            }
        }
        public static void Validate(JObject json, ExternalContentDefinition definition)
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (json["cubeAppearance"] != null)
            {
                Fields(json["cubeAppearance"], typeof(CubeAppearanceDeclaration)); Fields(json["cubeAppearance"]?["color"], typeof(CubeColorDeclaration));
                if (definition.ContentType != "component" || types.Find(definition.ItemTypeId)?.PhysicalKind != ItemPhysicalKind.Cube ||
                    !CubeAppearance.TryNormalize(definition.CubeAppearance, out var color, out var reason)) throw new InvalidDataException("cube_appearance_invalid:" + definition.DefinitionId);
                definition.CubeAppearance = color;
            }
            else if (definition.ContentType == "component" && types.Find(definition.ItemTypeId)?.PhysicalKind == ItemPhysicalKind.Cube)
            {
                if (definition.ItemTypeId != "core.player_marker") throw new InvalidDataException("cube_appearance_required:" + definition.DefinitionId);
                definition.CubeAppearance = CubeAppearance.PlayerDefault;
            }
            if (definition.UsageTracks == null || json["usageTracks"] != null && json["usageTracks"] is not JArray) throw new InvalidDataException("usage_tracks_array_required");
            foreach (var raw in json["usageTracks"] ?? new JArray())
            {
                Fields(raw, typeof(UsageTrackDeclaration));
                foreach (string required in new[] { "trackId", "version", "cardZoneLocalId", "slots", "initialSlotId", "activationTransitions", "resetTransitions", "resetBoundary", "initializationPolicy", "usageGroupScope" })
                    if (raw[required] == null || raw[required].Type == JTokenType.Null) throw new InvalidDataException("usage_track_field_required:" + required);
                foreach (string name in new[] { "activationTransitions", "resetTransitions" })
                {
                    if (raw[name] is not JArray transitions) throw new InvalidDataException("usage_transition_array_required");
                    foreach (var t in transitions) { Fields(t, typeof(UsageTrackTransition)); if (t["fromSlotId"] == null || t["toSlotId"] == null) throw new InvalidDataException("usage_transition_slot_required"); }
                }
                if (raw["slots"] is not JArray slots || slots.Any(s => s.Type != JTokenType.String)) throw new InvalidDataException("usage_slots_string_array_required");
            }
            var ids = new HashSet<string>();
            foreach (var d in definition.UsageTracks)
            {
                if (d.DefinitionId != "" && d.DefinitionId != definition.DefinitionId) throw new InvalidDataException("usage_track_definition_mismatch");
                d.DefinitionId = definition.DefinitionId;
                var zone = definition.CardZones.SingleOrDefault(z => z.LocalZoneId == d.CardZoneLocalId);
                if (d.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(d.TrackId) || !ids.Add(d.TrackId) || string.IsNullOrWhiteSpace(d.Version) ||
                    d.ResetBoundary != "all_cleanup_completed" || d.UsageGroupScope != "source_player_round" || !new[] { "owner_supply_or_retrieve", "public_declaration" }.Contains(d.InitializationPolicy) ||
                    d.Slots == null || d.Slots.Count == 0 || d.Slots.Any(string.IsNullOrWhiteSpace) || d.Slots.Count != d.Slots.Distinct().Count() || !d.Slots.Contains(d.InitialSlotId) ||
                    zone?.ZoneTemplateId != "core.zone.board_marker_track" || zone.CapacityPolicy != "bounded" || zone.Capacity != 1 || !zone.Slots.SequenceEqual(d.Slots) || zone.AcceptedTypeIds.Count != 1 || zone.AcceptedTypeIds[0] != "core.player_marker" ||
                    d.ActivationTransitions == null || d.ActivationTransitions.Count == 0 || d.ResetTransitions == null) throw new InvalidDataException("usage_track_declaration_invalid:" + d.TrackId);
                foreach (var transitions in new[] { d.ActivationTransitions, d.ResetTransitions })
                    if (transitions.Any(t => t == null || !d.Slots.Contains(t.FromSlotId) || !d.Slots.Contains(t.ToSlotId) || t.FromSlotId == t.ToSlotId) ||
                        transitions.Select(t => t.FromSlotId).Distinct().Count() != transitions.Count) throw new InvalidDataException("usage_track_transition_invalid:" + d.TrackId);
            }
            foreach (var action in definition.AbilitySources.SelectMany(s => s.Actions).Where(a => a.UsageTrackId.Length > 0))
                if (!definition.UsageTracks.Any(t => t.TrackId == action.UsageTrackId)) throw new InvalidDataException("usage_track_reference_missing:" + action.UsageTrackId);
        }
    }
}
