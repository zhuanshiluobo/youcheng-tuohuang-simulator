using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    [Serializable] public sealed class CommonItemMovementPlanV4
    {
        public string ContractVersion = ItemMovementV4.ContractVersion, OwnerEffectId = "", ArgumentsDigest = "", Outcome = "completed";
        public int ExecutingPlayerId;
        public List<int> LegSizes = new List<int>();
        public List<ItemTransferRequest> Transfers = new List<ItemTransferRequest>();
        public List<MarkerMoveItemFactV4> Items = new List<MarkerMoveItemFactV4>();
        public List<MarkerMoveOrderFactV4> BoundOrders = new List<MarkerMoveOrderFactV4>();
        public TransferReceiptV4 Receipt;
        public FacilityPlacementProofV1 FacilityPlacement;
    }
    public static partial class ItemMovementV4
    {
        public const string MobileCityTransferVersion = "1.0.0";
        internal static readonly string[] TransferProfiles = { "core.facility_card_transfer", "core.character_card_transfer", "core.reward_card_transfer", "core.cube_storage", "core.player_marker_storage", "core.token_storage", "core.mobile_city_move" };
        internal static string ValidateCommonArguments(NormalizedValue args)
        {
            if (args == null || !args.IsValid() || !IsV4(TypeId, args) || !int.TryParse(Text(args, "player"), NumberStyles.None, CultureInfo.InvariantCulture, out int player) || player < 1)
                return "item_move_v4_arguments_invalid";
            if (MapTokenOperation(args)) return ValidateMapTokenArguments(args);
            if (StyleReturnOperation(args)) return ValidateStyleReturnArguments(args);
            if (CoverOperation(args)) return ValidateCoverArguments(args);
            bool single = Text(args, "operation") == "transfer";
            if (!single && Text(args, "operation") != "transfer_batch") return "item_move_v4_operation_not_implemented";
            if (!Fields(args, single ? new[] { "kind", "effectTypeId", "apiVersion", "player", "operation", "selector", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy", "reveal" } :
                new[] { "kind", "effectTypeId", "apiVersion", "player", "operation", "transfers", "expectedOrders" })) return "item_move_v4_field_rejected";
            if (!single && (!Array(Value(args, "transfers")) || Value(args, "transfers").Items.Count == 0 || !Array(Value(args, "expectedOrders")))) return "item_move_v4_batch_invalid";
            var all = new HashSet<string>(StringComparer.Ordinal); string profile = null;
            foreach (var leg in Legs(args))
            {
                if (!single && !Fields(leg, "selector", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy")) return "item_move_v4_field_rejected";
                string currentProfile = Text(leg, "operationProfileId");
                if (!TransferProfiles.Contains(currentProfile) || !single && profile != null && profile != currentProfile) return "operation_profile_rejected";
                profile = currentProfile;
                if (Value(leg, "carrierPolicy") != null && Text(leg, "carrierPolicy") != "preserve") return "carrier_policy_rejected";
                if (Value(leg, "reveal") != null && (Value(leg, "reveal").Kind != NormalizedValueKind.Boolean || currentProfile != "core.character_card_transfer")) return "character_reveal_invalid";
                var selector = Value(leg, "selector"); int maximum;
                string mode = Text(selector, "mode");
                if (!single && mode != "explicit") return "item_move_v4_batch_requires_explicit";
                if (mode == "explicit")
                {
                    var ids = Value(selector, "itemIds"); var source = Value(selector, "expectedSource");
                    if (!Fields(selector, "mode", "itemIds", "expectedSource") || !Array(ids) || ids.Items.Count > 99 || ids.Items.Any(v => !String(v) || !all.Add(v.StringValue))) return "item_move_v4_items_invalid";
                    if (!Fields(source, "zoneId", "slotId") || !String(Value(source, "zoneId")) || Value(source, "slotId") != null && !String(Value(source, "slotId"))) return "invalid_zone_ref";
                    // 主计划4.2：显式库存不足是合法整笔失败，不能降低最低数量或虚构实例。
                    maximum = MaximumTokenCount;
                }
                else if (mode == "top")
                {
                    if (!Fields(selector, "mode", "zoneId", "count", "itemTypeId") || !String(Value(selector, "zoneId")) || !String(Value(selector, "itemTypeId")) || !Count(Value(selector, "count"), 1, 99)) return "item_move_v4_top_invalid";
                    maximum = Number(selector, "count");
                }
                else if (mode == "select") return "item_move_selection_not_supported";
                else return "item_move_v4_selector_invalid";
                var destination = Value(leg, "destination");
                bool original = Fields(destination, "kind") && destination.Properties.Count == 1 && Text(destination, "kind") == "original_supply";
                if (!(Fields(destination, "zoneId", "mustBeEmpty") && String(Value(destination, "zoneId")) &&
                    (Value(destination, "mustBeEmpty") == null || Value(destination, "mustBeEmpty").Kind == NormalizedValueKind.Boolean)) && !original) return "invalid_zone_ref";
                if (original && !ItemTransferService.IsTokenProfile(currentProfile)) return "original_supply_not_applicable";
                var insertion = Value(leg, "insertion"); string kind = Text(insertion, "kind");
                if (!Fields(insertion, "kind", "slotId") || !(new[] { "unordered", "top", "bottom" }.Contains(kind) && insertion.Properties.Count == 1 || kind == "slot" && insertion.Properties.Count == 2 && String(Value(insertion, "slotId")))) return "insertion_rejected";
                if (original && kind != "unordered") return "insertion_rejected";
                if (!Count(Value(leg, "requiredCount"), 0, maximum)) return "required_count_invalid";
            }
            if (all.Count > 99) return "item_move_v4_items_invalid";
            if (!single)
            {
                var orders = Value(args, "expectedOrders").Items;
                if (orders.Any(v => !Fields(v, "zoneId", "digest", "orderRevision", "expectedTopItemId") || v.Properties.Count != 4 || !String(Value(v, "zoneId")) ||
                    !String(Value(v, "digest")) || !Count(Value(v, "orderRevision"), 0, int.MaxValue) || Value(v, "expectedTopItemId")?.Kind != NormalizedValueKind.String) ||
                    orders.Select(v => Text(v, "zoneId")).Distinct(StringComparer.Ordinal).Count() != orders.Count) return "transfer_batch_order_invalid";
            }
            return "";
        }
        private static bool Count(NormalizedValue value, int min, int max) => value?.Kind == NormalizedValueKind.Integer && value.IntegerValue >= min && value.IntegerValue <= max;
        private static EffectStepResult ExecuteCommon(EffectExecutionContext context)
        {
            var node = context.Node; node.Visibility = GameStateVisibilityPolicy.Owner;
            var args = CommonArguments(context.State, node); var service = new ItemTransferService();
            if (node.CommonItemMovementV4 == null)
            {
                string mapError = ValidateMapTokenMove(context);
                if (mapError.Length > 0) return EffectStepResult.Failed(mapError);
                if (!service.TryPlanCommonV4(context, out var plan, out string error)) return EffectStepResult.Failed(error);
                node.CommonItemMovementV4 = plan; return EffectStepResult.Continue("v4_bound");
            }
            if (!TryValidateCommonState(context.State, node, out string invalid)) throw new InvalidOperationException(invalid);
            var pending = node.CommonItemMovementV4;
            if (pending.Receipt == null)
            {
                foreach (var transfer in pending.Transfers)
                    if (CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.ExactItemUse, ItemIds = transfer.ItemIds }) ||
                        transfer.OperationProfileId == "core.character_card_transfer" && CharacterLifecycleService.TryAwaitCardMoves(context, transfer.ItemIds, transfer.Destination.ZoneId) ||
                        CharacterLifecycleService.TryAwaitEmptyCarrierExits(context, new[] { transfer.ExpectedSource.ZoneId }, transfer.ItemIds)) return CharacterLifecycleService.DeferredDependencyWait();
                if (!service.TryCommitCommonV4(context, pending, out var events, out string error)) return EffectStepResult.Failed(error);
                var step = EffectStepResult.Continue("v4_committed"); foreach (var e in events) step.AddEvent(e);
                foreach (var exit in context.Node.CommonItemMovementV4.Receipt.CarrierExits) { var child = ExitSpec(exit); child.StableKey = node.EffectId + ":empty:" + exit.CardItemId; step.AddChild(child); }
                return step;
            }
            return EffectStepResult.Completed(Result(pending.Receipt, node.NormalizedArguments)).WithFlowStage("v4_finished");
        }
        internal static bool TryValidateCommonState(GameState state, EffectNodeRuntimeState node, out string reason) => ItemTransferService.ValidateCommonV4(state, node, out reason);
    }
}
