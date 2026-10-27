using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Facilities;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Presentation
{
    internal sealed class FacilityPanelSelectionModel
    {
        internal InteractionRequestProjection Request;
        internal IReadOnlyList<VisibleFacilityLayers.Cell> Cells;
        internal IReadOnlyList<FacilityPanelSlotCandidate> Candidates;
        internal string Identity;
        internal bool CanSelect(int slot) => Candidates.Any(c => c.SlotIndex == slot);
        internal static bool TryCreate(InteractionRequestProjection request, GameStateView view, int player, out FacilityPanelSelectionModel model)
        {
            model = null;
            if (request == null || view == null || !request.VisibleToViewer || request.Status != "open" ||
                request.InteractionTypeId != FacilityPanelSelectionRequest.InteractionType || request.AnsweringPlayerId != player ||
                view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != player || request.StateRevision != view.StateRevision || request.AnswerSchema != "candidate_id" ||
                request.MinSelections != 1 || request.MaxSelections != 1) return false;
            var values = request.PromptParameters?.Properties?.FirstOrDefault(p => p.Name == "candidateSlots")?.Value;
            var owner = request.PromptParameters?.Properties?.FirstOrDefault(p => p.Name == "player")?.Value;
            if (values?.Kind != NormalizedValueKind.Array || owner?.Kind != NormalizedValueKind.Integer || owner.IntegerValue != player) return false;
            var cells = VisibleFacilityLayers.City(view.ItemZones, player);
            var candidates = new List<FacilityPanelSlotCandidate>();
            foreach (var value in values.Items)
            {
                var index = ItemTransferEffectExecutor.Number(value, "slotIndex", -1);
                var cellId = ItemTransferEffectExecutor.Text(value, "cellZoneId");
                var top = ItemTransferEffectExecutor.Text(value, "topItemId");
                var cell = cells.FirstOrDefault(c => c.Index == index && c.ZoneId == cellId);
                if (cell == null || cell.Cards.FirstOrDefault()?.ItemId != top ||
                    !request.CandidateIds.Contains(index.ToString(CultureInfo.InvariantCulture))) return false;
                candidates.Add(new FacilityPanelSlotCandidate { SlotIndex = index, CellZoneId = cellId, TopItemId = top,
                    OrderDigest = ItemTransferEffectExecutor.Text(value, "orderDigest") });
            }
            if (candidates.Count != request.CandidateIds.Count || candidates.Select(c => c.SlotIndex).Distinct().Count() != candidates.Count) return false;
            model = new FacilityPanelSelectionModel { Request = request, Cells = cells, Candidates = candidates };
            model.Identity = string.Join("|", view.GameId, player, request.InteractionId, request.OwnerEffectId, request.CandidateSetId,
                request.CandidateSetVersion, request.StateRevision, string.Join(",", request.CandidateIds),
                request.PromptParameters.ToDeterministicString(), string.Join(";", cells.Select(c => c.ZoneId + ":" + string.Join(",", c.Cards.Select(i => i.ItemId)))),
                string.Join(";", view.ItemZones.Orders.Select(o => o.ZoneId + ":" + o.OrderRevision + ":" + string.Join(",", o.ItemIds))));
            return true;
        }
    }
}
