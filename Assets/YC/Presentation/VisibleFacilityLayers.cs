using System.Collections.Generic;
using System.Linq;
using YC.Domain.Facilities;
using YC.Domain.Items;

namespace YC.Presentation
{
    internal static class VisibleFacilityLayers
    {
        internal sealed class Cell
        {
            internal int Index;
            internal string ZoneId;
            internal List<ItemInstanceState> Cards = new List<ItemInstanceState>();
        }
        internal static IReadOnlyList<Cell> City(ItemZoneStateView visible, int owner)
        {
            var cells = new List<Cell>();
            if (visible == null) return cells;
            foreach (var entry in visible.Zones.Where(z => z.Zone.TemplateId == FacilityLayerQuery.CellTemplate && z.Zone.OwnerPlayerId == owner))
            {
                var cell = new Cell { Index = FacilityLayerQuery.Slot(entry.Zone), ZoneId = entry.Zone.ZoneId };
                cell.Cards.AddRange(visible.Items.Where(i => i.ZoneId == cell.ZoneId));
                foreach (var lower in visible.Zones.Where(z => z.Zone.ParentId == cell.ZoneId && z.Zone.TemplateId == FacilityLayerQuery.CoveredTemplate))
                {
                    var order = visible.Orders.FirstOrDefault(o => o.ZoneId == lower.Zone.ZoneId);
                    if (order != null) cell.Cards.AddRange(order.ItemIds.Select(id => visible.Items.FirstOrDefault(i => i.ItemId == id && i.ZoneId == lower.Zone.ZoneId)).Where(i => i != null));
                }
                cells.Add(cell);
            }
            return cells.OrderBy(c => c.Index).ToArray();
        }
    }
}
