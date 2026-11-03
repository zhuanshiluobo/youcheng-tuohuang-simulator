using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public static partial class ItemMovementV4
    {
        internal static bool StyleReturnOperation(NormalizedValue args) => Text(args, "operation") == "return_city_style_marker";
        private static string ValidateStyleReturnArguments(NormalizedValue args)
        {
            if (!Fields(args, "kind", "effectTypeId", "apiVersion", "player", "operation", "selector")) return "item_move_v4_field_rejected";
            var selector = Value(args, "selector"); if (selector == null) return "item_move_explicit_target_required";
            var ids = Value(selector, "itemIds"); var source = Value(selector, "expectedSource");
            if (!Fields(selector, "mode", "itemIds", "expectedSource") || Text(selector, "mode") != "explicit" || ids?.Kind != NormalizedValueKind.Array || ids.Items.Count != 1 || !String(ids.Items[0])) return "item_move_v4_items_invalid";
            return Fields(source, "zoneId", "slotId") && String(Value(source, "zoneId")) && (Value(source, "slotId") == null || String(Value(source, "slotId"))) ? "" : "invalid_zone_ref";
        }
        internal static NormalizedValue StyleReturnArguments(GameState state, EffectNodeRuntimeState node)
        {
            var selector = Value(node.NormalizedArguments, "selector").Clone();
            return Object("apiVersion", NormalizedValue.CreateInteger(4), "player", Value(node.NormalizedArguments, "player").Clone(), "operation", NormalizedValue.CreateString("transfer"),
                "selector", selector, "destination", Object("kind", NormalizedValue.CreateString("original_supply")), "insertion", Object("kind", NormalizedValue.CreateString("unordered")),
                "operationProfileId", NormalizedValue.CreateString("core.player_marker_storage"), "requiredCount", NormalizedValue.CreateInteger(1));
        }
        internal static bool StyleReturnFactsValid(GameState state, EffectNodeRuntimeState node, CommonItemMovementPlanV4 plan) =>
            !StyleReturnOperation(node.NormalizedArguments) || plan.Items.Count == 1 && plan.Outcome == "completed" && plan.Items.All(f =>
                CityStyleMarkerReturn.EligibleSource(state, node.PlayerId, f.ItemId, f.From.ZoneId) && f.To.ZoneId == f.OriginalSupplyZoneId && f.To.SlotId == "" &&
                state.ItemZones.Zones.Any(z => z.ZoneId == f.To.ZoneId && z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == node.PlayerId));
    }
}
