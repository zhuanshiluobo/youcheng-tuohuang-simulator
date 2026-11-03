using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Movement;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public static partial class ItemMovementV4
    {
        internal static bool MapTokenOperation(NormalizedValue args) => Text(args, "operation") == "move_map_token";
        private static string ValidateMapTokenArguments(NormalizedValue args)
        {
            if (!Fields(args, "kind", "effectTypeId", "apiVersion", "player", "operation", "selector", "destinationLocationId", "operationProfileId")) return "item_move_v4_field_rejected";
            var selector = Value(args, "selector"); var ids = Value(selector, "itemIds"); var source = Value(selector, "expectedSource");
            if (!Fields(selector, "mode", "itemIds", "expectedSource") || Text(selector, "mode") != "explicit" || ids?.Kind != NormalizedValueKind.Array ||
                ids.Items.Count != 1 || !String(ids.Items[0])) return "item_move_v4_items_invalid";
            if (!Fields(source, "zoneId", "slotId") || !String(Value(source, "zoneId")) || Value(source, "slotId") != null && !String(Value(source, "slotId"))) return "invalid_zone_ref";
            if (!String(Value(args, "destinationLocationId"))) return "token_destination_rejected";
            return Text(args, "operationProfileId") == "core.adjacent_location_token_move" ? "" : "operation_profile_rejected";
        }
        // 地图区域由真实地点解析；原请求与原源区仍由共同计划摘要绑定。
        internal static NormalizedValue CommonArguments(GameState state, EffectNodeRuntimeState node)
        {
            var args = node.NormalizedArguments;
            if (StyleReturnOperation(args)) return StyleReturnArguments(state, node);
            if (!MapTokenOperation(args)) return args;
            string zone = TokenMovementProfileRegistry.DestinationZoneId(state, Text(args, "destinationLocationId"));
            return Object("apiVersion", NormalizedValue.CreateInteger(4), "player", Value(args, "player").Clone(), "operation", NormalizedValue.CreateString("transfer"),
                "selector", Value(args, "selector").Clone(), "destination", Object("zoneId", NormalizedValue.CreateString(zone)),
                "insertion", Object("kind", NormalizedValue.CreateString("unordered")), "operationProfileId", Value(args, "operationProfileId").Clone(), "requiredCount", NormalizedValue.CreateInteger(1));
        }
        internal static string ValidateMapTokenMove(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            if (!MapTokenOperation(args)) return "";
            var selector = Value(args, "selector"); var source = Value(selector, "expectedSource");
            var item = context.State.ItemZones.Items.SingleOrDefault(i => i.ItemId == Value(selector, "itemIds").Items[0].StringValue);
            if (item != null && (item.ZoneId != Text(source, "zoneId") || Value(source, "slotId") != null && item.SlotId != Text(source, "slotId"))) return "source_zone_changed";
            return context.Registry.TokenMovementProfiles.ValidateMove(context.State, context.Node.PlayerId,
                Value(Value(args, "selector"), "itemIds").Items[0].StringValue, Text(args, "destinationLocationId"), Text(args, "operationProfileId"));
        }
        internal static EffectEventRequest MapTokenEvent(EffectNodeRuntimeState node, MarkerMoveItemFactV4 fact, GameState state) =>
            TokenMovementProfileRegistry.MovedEvent(node, fact.ItemId, fact.From, fact.To,
                TokenMovementProfileRegistry.LocationForZone(state, fact.From.ZoneId), Text(node.NormalizedArguments, "destinationLocationId"));
        internal static bool MapTokenFactsValid(GameState state, EffectNodeRuntimeState node, CommonItemMovementPlanV4 plan)
        {
            if (!MapTokenOperation(node.NormalizedArguments)) return true;
            if (plan.Items.Count != 1 || plan.Outcome != "completed") return false;
            var fact = plan.Items[0]; var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == fact.ItemId);
            return item?.PhysicalKind == ItemPhysicalKind.Token && ItemTypeRegistry.CreateBuiltIn().HasOperationProfile(fact.TypeId, "core.adjacent_location_token_move") &&
                (fact.Controller.Kind != ItemSubjectKind.Player || fact.Controller.PlayerId == node.PlayerId) &&
                TokenMovementProfileRegistry.LocationForZone(state, fact.From.ZoneId).Length > 0 &&
                fact.To.ZoneId == TokenMovementProfileRegistry.DestinationZoneId(state, Text(node.NormalizedArguments, "destinationLocationId"));
        }
    }
}
