using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Interactions;
using YC.Domain.Items;

namespace YC.Presentation
{
    /// <summary>正式供应选择的可见草稿，不包含建设或付款资格。</summary>
    public sealed class FacilitySupplySelectionModel
    {
        public const string InteractionType = "facility.supply.select";
        public sealed class Slot
        {
            public string ZoneId { get; internal set; }
            public int Index { get; internal set; }
            public int OrderRevision { get; internal set; }
            public IReadOnlyList<ItemInstanceState> Layers { get; internal set; }
            public string TopId => Layers.FirstOrDefault()?.ItemId ?? string.Empty;
        }
        public InteractionRequestProjection Request { get; private set; }
        public IReadOnlyList<Slot> Slots { get; private set; }
        public string Identity { get; private set; }
        public bool CanSelect(string id) => !string.IsNullOrEmpty(id) && Request.CandidateIds.Contains(id) &&
            Slots.Any(slot => slot.TopId == id);

        public static bool TryCreate(InteractionRequestProjection request, ItemZoneStateView visibleItems,
            int player, int stateRevision, out FacilitySupplySelectionModel model)
        {
            model = null;
            if (request == null || !request.VisibleToViewer || request.Status != "open" ||
                request.InteractionTypeId != InteractionType || request.AnsweringPlayerId != player ||
                request.StateRevision != stateRevision || request.AnswerSchema != "candidate_id" ||
                request.MinSelections != 1 || request.MaxSelections != 1 || request.CandidateIds.Count == 0 || visibleItems == null)
                return false;
            var slots = new List<Slot>();
            foreach (var projected in visibleItems.Zones.Where(z => z.Zone.TemplateId == "core.zone.facility_supply_slot"))
            {
                var zone = projected.Zone;
                const string prefix = "facility_supply_";
                if (!zone.LocalZoneId.StartsWith(prefix, StringComparison.Ordinal) ||
                    !int.TryParse(zone.LocalZoneId.Substring(prefix.Length), out var index)) return false;
                var order = visibleItems.Orders.SingleOrDefault(o => o.ZoneId == zone.ZoneId);
                if (order == null) return false;
                var layers = order.ItemIds.Select(id => visibleItems.Items.SingleOrDefault(i => i.ItemId == id && i.ZoneId == zone.ZoneId)).ToArray();
                if (layers.Any(i => i == null)) return false;
                slots.Add(new Slot { ZoneId = zone.ZoneId, Index = index, OrderRevision = order.OrderRevision,
                    Layers = Array.AsReadOnly(layers) });
            }
            slots.Sort((a, b) => a.Index.CompareTo(b.Index));
            // 一个过期候选也不静默替成新顶牌；等待正式请求/转移复验后的新快照。
            if (request.CandidateIds.Any(id => slots.All(s => s.TopId != id))) return false;
            model = new FacilitySupplySelectionModel { Request = request, Slots = slots.AsReadOnly() };
            model.Identity = string.Join("|", request.InteractionId, request.OwnerEffectId, request.CandidateSetId,
                request.CandidateSetVersion, request.StateRevision, player, string.Join(",", request.CandidateIds),
                string.Join(";", slots.Select(s => s.ZoneId + ":" + s.OrderRevision + ":" + string.Join(",", s.Layers.Select(i => i.ItemId)))));
            return true;
        }
    }
}
