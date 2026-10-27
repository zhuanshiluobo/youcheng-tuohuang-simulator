using System;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Facilities
{
    /// <summary>层事实凭据；count为所指格的真实设施数，得分及发放数量由Lua决定。</summary>
    public static class FacilityLayerEvidence
    {
        public const string Version = "1.0.0";
        public static string Digest(GameState state, int playerId) => FacilityBuildQueryService.Hash(
            Version + "|" + playerId.ToString(CultureInfo.InvariantCulture) + "|" +
            JsonConvert.SerializeObject(FacilityLayerQuery.ResolveCity(state, playerId)));
        public static string Validate(GameState state, NormalizedValue evidence)
        {
            if (evidence?.Kind != NormalizedValueKind.Object || !evidence.IsValid() ||
                Value(evidence, "playerId")?.Kind != NormalizedValueKind.Integer ||
                state.FindPlayer(Number(evidence, "playerId", -1)) == null ||
                Value(evidence, "facilityFactsDigest")?.Kind != NormalizedValueKind.String ||
                Value(evidence, "facilityItemIds")?.Kind != NormalizedValueKind.Array ||
                Value(evidence, "facilityItemIds").Items.Any(i => i.Kind != NormalizedValueKind.String) ||
                Value(evidence, "count")?.Kind != NormalizedValueKind.Integer)
                return "facility_scoring_evidence_shape_invalid";
            int player = Number(evidence, "playerId");
            var cells = FacilityLayerQuery.ResolveCity(state, player);
            if (Text(evidence, "facilityFactsDigest") != Digest(state, player)) return "facility_scoring_facts_changed";
            var ids = Value(evidence, "facilityItemIds").Items.Select(i => i.StringValue).ToList();
            string slot = Text(evidence, "slotId");
            if (slot.Length == 0)
                return cells.All(c => c.AllItemIds.Count == 0) && ids.Count == 0 && Number(evidence, "count", -1) == 0
                    ? "" : "facility_scoring_empty_evidence_invalid";
            if (Value(evidence, "slotId")?.Kind != NormalizedValueKind.String) return "facility_scoring_slot_invalid";
            var selected = cells.SingleOrDefault(c => c.CellZoneId == slot);
            return selected != null && selected.AllItemIds.SequenceEqual(ids) && Number(evidence, "count", -1) == ids.Count
                ? "" : "facility_scoring_cell_evidence_invalid";
        }
    }
}
