using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Facilities
{
    /// <summary>未选中的有界观察只约束形状/归属/因果修订；事实变化在正式查询上重发，不能当作坏档拒绝。</summary>
    public static class FacilityPlacementObservations
    {
        public const string ValidationVersion = "1.0.0";
        public const string Changed = "facility_placement_observation_changed";
        private static readonly string[] CompactFields = { "protocolVersion", "slotIndex", "stateRevision", "observationDigest" };
        private static readonly string[] LegacyFields = { "protocolVersion", "playerId", "slotIndex", "stateRevision", "cellZoneId", "newItemId",
            "expectedTopItemId", "orderDigest", "isOverlay", "requiredResponseAbilityId", "overlayAuthorization", "removalPrecondition" };
        public static bool Applies(EffectNodeRuntimeState node) => node?.EffectTypeId == FacilityEntryEffectTypeIds.Build &&
            node.DefinitionVersion == FacilityBuildEffectExecutor.DefinitionVersion && node.FlowStage == "awaiting_placement";
        public static bool TryValidate(NormalizedValue values, int player, string item, int revision, out string diagnostic)
        {
            diagnostic = "facility_placement_observation_shape_invalid";
            if (values?.Kind != NormalizedValueKind.Array || !values.IsValid() || values.Items.Count == 0 ||
                values.Items.Count > BuildFacilityService.CityBoardSlotCount || string.IsNullOrEmpty(item)) return false;
            var slots = new System.Collections.Generic.HashSet<long>();
            foreach (var value in values.Items)
            {
                if (value?.Kind != NormalizedValueKind.Object || Value(value, "protocolVersion")?.Kind != NormalizedValueKind.Integer) return false;
                long protocol = Value(value, "protocolVersion").IntegerValue;
                if (protocol != 1 && protocol != FacilityBuildEffectExecutor.PlacementObservationProtocolVersion)
                { diagnostic = "facility_placement_observation_protocol_unknown"; return false; }
                string[] required = protocol == 1 ? LegacyFields : CompactFields;
                if (value.Properties.Count != required.Length || value.Properties.Any(p => !required.Contains(p.Name)) ||
                    Value(value, "slotIndex")?.Kind != NormalizedValueKind.Integer || Value(value, "stateRevision")?.Kind != NormalizedValueKind.Integer) return false;
                long slot = Value(value, "slotIndex").IntegerValue, observed = Value(value, "stateRevision").IntegerValue;
                if (slot < 0 || slot >= BuildFacilityService.CityBoardSlotCount || !slots.Add(slot))
                { diagnostic = "facility_placement_observation_slot_invalid"; return false; }
                if (observed < 0 || observed > revision)
                { diagnostic = "facility_placement_observation_revision_invalid"; return false; }
                if (protocol == FacilityBuildEffectExecutor.PlacementObservationProtocolVersion)
                {
                    var digest = Value(value, "observationDigest");
                    if (digest?.Kind != NormalizedValueKind.String || !System.Text.RegularExpressions.Regex.IsMatch(digest.StringValue ?? "",
                        @"\Afacility_slot_observation\.v2_[0-9a-f]{64}\z")) return false;
                }
                else
                {
                    if (Value(value, "playerId")?.Kind != NormalizedValueKind.Integer || Value(value, "playerId").IntegerValue != player ||
                        new[] { "cellZoneId", "newItemId", "expectedTopItemId", "orderDigest", "requiredResponseAbilityId" }.Any(k => Value(value, k)?.Kind != NormalizedValueKind.String) ||
                        Text(value, "newItemId") != item || Text(value, "cellZoneId").Length == 0 || Text(value, "orderDigest").Length == 0 ||
                        Value(value, "isOverlay")?.Kind != NormalizedValueKind.Boolean ||
                        !new[] { NormalizedValueKind.Null, NormalizedValueKind.Object }.Contains(Value(value, "overlayAuthorization")?.Kind ?? NormalizedValueKind.String) ||
                        !new[] { NormalizedValueKind.Null, NormalizedValueKind.Object }.Contains(Value(value, "removalPrecondition")?.Kind ?? NormalizedValueKind.String)) return false;
                    try { FacilityBuildJournal.DecodeSlot(value); }
                    catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is NullReferenceException) { return false; }
                }
            }
            diagnostic = ""; return true;
        }
        public static bool TryValidateState(GameState state, EffectNodeRuntimeState node, out string diagnostic)
        {
            diagnostic = "";
            if (!Applies(node)) return true;
            var selected = Value(node.ContinuationData, "selectedItemIds");
            string item = selected?.Kind == NormalizedValueKind.Array && selected.Items.Count == 1 && selected.Items[0].Kind == NormalizedValueKind.String ? selected.Items[0].StringValue : "";
            return TryValidate(Value(node.ContinuationData, "placementCandidates"), node.PlayerId, item, state.EffectRuntime.StateRevision, out diagnostic);
        }
        internal static bool NeedsRefresh(GameState state, EffectRegistry registry, EffectNodeRuntimeState node)
        {
            if (!TryValidateState(state, node, out string diagnostic)) throw new InvalidOperationException(diagnostic);
            string item = Value(node.ContinuationData, "selectedItemIds").Items[0].StringValue;
            var current = registry.FacilityBuildQueries.ResolveSlots(state, node.PlayerId, item, node.EffectId);
            var saved = Value(node.ContinuationData, "placementCandidates").Items;
            if (!current.Select(c => c.SlotIndex).OrderBy(i => i).SequenceEqual(saved.Select(v => Number(v, "slotIndex")).OrderBy(i => i))) return true;
            foreach (var observation in saved)
            {
                if (Number(observation, "protocolVersion") == FacilityBuildEffectExecutor.PlacementObservationProtocolVersion)
                {
                    var candidate = current.Single(c => c.SlotIndex == Number(observation, "slotIndex"));
                    if (Text(observation, "observationDigest") != FacilityBuildEffectExecutor.PlacementObservationDigest(candidate, Number(observation, "stateRevision"))) return true;
                }
                else
                {
                    try { registry.FacilityBuildQueries.RevalidateSlot(state, FacilityBuildJournal.DecodeSlot(observation), node.EffectId); }
                    catch (InvalidOperationException) { return true; }
                }
            }
            return false;
        }
    }
}
