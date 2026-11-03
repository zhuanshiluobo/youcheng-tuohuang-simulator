using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    [Serializable] public sealed class MarkerMoveItemFactV4
    {
        public string ItemId = "", TypeId = "", OriginalSupplyZoneId = "";
        public ItemSubjectRef Owner = new ItemSubjectRef(), Controller = new ItemSubjectRef(), ControllerAfter = new ItemSubjectRef();
        public ItemPositionRef From = new ItemPositionRef(), To = new ItemPositionRef();
    }
    [Serializable] public sealed class MarkerMoveOrderFactV4
    {
        public string ZoneId = "";
        public bool HasOrder;
        public int Revision;
        public List<string> ItemIds = new List<string>();
    }
    [Serializable] public sealed class MarkerMoveUsageFactV4
    {
        public string BindingId = "", MarkerItemId = "", ZoneId = "", SlotId = "";
    }
    [Serializable] public sealed class TransferReceiptV4
    {
        public string ContractVersion = ItemMovementV4.ContractVersion, ReceiptId = "", OperationId = "", OwnerEffectId = "", ArgumentsDigest = "";
        public int ExecutingPlayerId;
        public string Outcome = "completed";
        public List<MarkerMoveItemFactV4> Items = new List<MarkerMoveItemFactV4>();
        public List<MarkerMoveOrderFactV4> OrdersBefore = new List<MarkerMoveOrderFactV4>(), OrdersAfter = new List<MarkerMoveOrderFactV4>();
        public List<MarkerMoveUsageFactV4> BindingsBefore = new List<MarkerMoveUsageFactV4>(), BindingsAfter = new List<MarkerMoveUsageFactV4>();
        public List<string> EventIds = new List<string>();
        public List<MarkerCarrierExitV4> CarrierExits = new List<MarkerCarrierExitV4>();
        public List<MarkerMoveItemFactV4> LifecycleItems = new List<MarkerMoveItemFactV4>();
        public ItemTransferResult Result = new ItemTransferResult();
    }
    [Serializable] public sealed class MarkerCarrierExitV4
    {
        public string CardItemId = "", SourceStorageZoneId = "", FromZoneId = "", ToZoneId = "";
        public int PlayerId;
    }
    [Serializable] public sealed class ItemMovementPlanV4
    {
        public string ContractVersion = ItemMovementV4.ContractVersion, OwnerEffectId = "", ArgumentsDigest = "";
        public int ExecutingPlayerId;
        public List<ItemTransferRequest> Transfers = new List<ItemTransferRequest>();
        public List<MarkerMoveItemFactV4> Items = new List<MarkerMoveItemFactV4>();
        public TransferReceiptV4 Receipt;
    }

    /// <summary>API-25玩家标记与API-30共同转移/地图Token策略；未实现的操作明确拒绝。</summary>
    public static partial class ItemMovementV4
    {
        public const string ContractVersion = "item.move.v4.3";
        // 内部生产者直接声明共同v4步骤；不再创建旧itemRefs节点后由旧执行分支搬运。
        public static EffectSpec TransferSpec(int player, IEnumerable<string> ids, string source, string destination,
            string insertion = "unordered", int required = 0, string profile = "core.facility_card_transfer") =>
            new EffectSpec(TypeId, Object("apiVersion", NormalizedValue.CreateInteger(4),
                "player", NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)), "operation", NormalizedValue.CreateString("transfer"),
                "selector", Object("mode", NormalizedValue.CreateString("explicit"), "itemIds", Strings(ids),
                    "expectedSource", Object("zoneId", NormalizedValue.CreateString(source))),
                "destination", Object("zoneId", NormalizedValue.CreateString(destination)), "insertion", Object("kind", NormalizedValue.CreateString(insertion)),
                "operationProfileId", NormalizedValue.CreateString(profile), "requiredCount", NormalizedValue.CreateInteger(required)))
            { PlayerId = player, DefinitionVersion = TransferDefinitionVersion, Visibility = GameStateVisibilityPolicy.Owner };
        private static bool Fields(NormalizedValue value, params string[] names) => value?.Kind == NormalizedValueKind.Object &&
            value.Properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.Properties.Count && value.Properties.All(p => names.Contains(p.Name));
        private static bool Array(NormalizedValue value) => value?.Kind == NormalizedValueKind.Array || value?.Kind == NormalizedValueKind.Object && value.Properties.Count == 0;
        private static bool String(NormalizedValue value) => value?.Kind == NormalizedValueKind.String && !string.IsNullOrWhiteSpace(value.StringValue);
        public static bool IsV4(string type, NormalizedValue args) => type == TypeId && Value(args, "apiVersion")?.Kind == NormalizedValueKind.Integer && Value(args, "apiVersion").IntegerValue == 4;
        public static IEnumerable<NormalizedValue> Legs(NormalizedValue args) => Text(args, "operation") == "transfer" ? new[] { args } : Value(args, "transfers").Items;
        public static string ValidateArguments(NormalizedValue args) => ValidateCommonArguments(args);
        internal static bool MarkerSubset(NormalizedValue args) => new[] { "transfer", "transfer_batch" }.Contains(Text(args, "operation")) &&
            Legs(args).All(l => Text(l, "operationProfileId") == "core.player_marker_storage" && Text(Value(l, "selector"), "mode") == "explicit") &&
            (Text(args, "operation") != "transfer_batch" || Value(args, "expectedOrders").Items.Count == 0);

        public static EffectStepResult Execute(EffectExecutionContext context)
        {
            if (!ItemMoveInputNormalization.TryValidate(context.Node.EffectTypeId,context.Node.NormalizedArguments,context.Node.ItemMoveInput,out string inputReason))
                throw new InvalidOperationException(inputReason);
            if (Text(context.Node.NormalizedArguments, "player") != context.Node.PlayerId.ToString(CultureInfo.InvariantCulture)) return EffectStepResult.Failed("transfer_permission_denied");
            if (CoverOperation(context.Node.NormalizedArguments)) return ExecuteCover(context);
            if (!MarkerSubset(context.Node.NormalizedArguments)) return ExecuteCommon(context);
            var service = new ItemTransferService();
            if (context.Node.ItemMovementV4 == null)
            {
                if (!service.TryPlanMarkersV4(context, out var plan, out string reason)) return EffectStepResult.Failed(reason);
                context.Node.ItemMovementV4 = plan;
                // 独立Host步骤封存意向，再进入依赖等待/提交；不能在恢复后重新读取原供应或源格位。
                return EffectStepResult.Continue("v4_prepared");
            }
            if (!TryValidateState(context.State, context.Node, out var invalid)) throw new InvalidOperationException(invalid);
            var current = context.Node.ItemMovementV4;
            if (current.Receipt == null)
            {
                if (CharacterLifecycleService.TryAwaitDeferredDependencies(context, new DeferredDependencyQuery { DependencyKind = DeferredDependencyKind.ExactItemUse,
                    ItemIds = current.Items.Select(i => i.ItemId).ToList() }) || CharacterLifecycleService.TryAwaitEmptyCarrierExits(context,
                    current.Transfers.Select(t => t.ExpectedSource.ZoneId), current.Items.Select(i => i.ItemId)))
                    return CharacterLifecycleService.DeferredDependencyWait().WithFlowStage("v4_wait");
                var validation = service.ValidateMarkersV4(context, current);
                if (!validation.IsValid) return EffectStepResult.Failed(validation.ReasonCode);
                service.RelocateMarkersV4(context, current);
                // 提交后按真实载体的当前控制者组织自动离场；父节点保留实际执行者。
                var exits = EffectStepResult.Continue("v4_carrier_exit");
                foreach (var exit in current.Receipt.CarrierExits)
                {
                    var child = ExitSpec(exit);
                    child.StableKey = context.Node.EffectId + ":empty:" + exit.CardItemId;
                    exits.AddChild(child);
                }
                if (exits.ChildEffects.Count > 0) return exits;
            }
            // 载体离场期间标记收据保持不变；恢复只续接既有子节点，不再次移动。
            if (context.ChildNodes.Any(n => n.Status == EffectNodeStatus.Failed || n.Status == EffectNodeStatus.Faulted)) return EffectStepResult.Failed("marker_carrier_exit_failed");
            return EffectStepResult.Completed(Result(current.Receipt)).WithFlowStage("v4_finished");
        }
        internal static EffectSpec ExitSpec(MarkerCarrierExitV4 exit) => EffectSpec.Create(LuaDomainEffectTypeIds.MoveCharacterCard,
            Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString(exit.PlayerId.ToString(CultureInfo.InvariantCulture)),
                "itemRefs", NormalizedValue.CreateArray(new[] { Object("itemId", NormalizedValue.CreateString(exit.CardItemId)) }),
                "expectedSource", Object("zoneId", NormalizedValue.CreateString(exit.FromZoneId)), "destination", Object("zoneId", NormalizedValue.CreateString(exit.ToZoneId)),
                "insertion", Object("kind", NormalizedValue.CreateString("unordered")), "operationProfileId", NormalizedValue.CreateString("core.character_card_transfer"),
                "requiredCount", NormalizedValue.CreateInteger(1)), exit.PlayerId);

        public static NormalizedValue Result(TransferReceiptV4 receipt, NormalizedValue arguments = null)
        {
            var result = receipt.Result;
            var value = Object("outcome", NormalizedValue.CreateString(receipt.Outcome), "changed", NormalizedValue.CreateBoolean(result.Changed),
                "movedCount", NormalizedValue.CreateInteger(result.MovedCount), "movedItemIds", Strings(result.MovedItemIds), "receiptId", NormalizedValue.CreateString(receipt.ReceiptId),
                "reasonCode", NormalizedValue.CreateString(receipt.Outcome == "completed" ? result.Changed ? "" : "no_change" : receipt.Outcome));
            if (receipt.Items.Count == 1 && receipt.Items[0].TypeId == "core.player_marker") value.Properties.Add(new NormalizedValueEntry { Name = "originalOwner", Value = NormalizedValue.CreateString("p" + receipt.Items[0].Owner.PlayerId.ToString(CultureInfo.InvariantCulture)) });
            if (MapTokenOperation(arguments)) value.Properties.Add(new NormalizedValueEntry { Name = "tokenItemId", Value = NormalizedValue.CreateString(receipt.Items.Single().ItemId) });
            value.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name)); return value;
        }
        public static string ValidateSavedVersion(string type, NormalizedValue args, string version, bool runtimeNode = false) =>
            ItemMoveInputNormalization.IsSupportedLegacy(type,args) ? "item_move_contract_version_unsupported" :
            !IsV4(type, args) || version == TransferDefinitionVersion || !runtimeNode && string.IsNullOrEmpty(version) ? "" : "item_move_contract_version_unsupported";
        public static bool TryValidateState(GameState state, EffectNodeRuntimeState node, out string reason)
        {
            if (!ItemMoveInputNormalization.TryValidate(node.EffectTypeId,node.NormalizedArguments,node.ItemMoveInput,out reason)) return false;
            if (ItemMoveInputNormalization.IsSupportedLegacy(node.EffectTypeId,node.NormalizedArguments)) { reason = "item_move_contract_version_unsupported"; return false; }
            reason = "item_move_v4_state_invalid";
            if (!IsV4(node.EffectTypeId, node.NormalizedArguments)) return node.ItemMovementV4 == null && node.CommonItemMovementV4 == null && node.CharacterCoverMovementV4 == null && (node.MainCoverFlowV1==null||node.EffectTypeId==CharacterCoverEffectExecutor.EffectTypeId&&Value(node.NormalizedArguments,"apiVersion")==null);
            if (ValidateSavedVersion(node.EffectTypeId, node.NormalizedArguments, node.DefinitionVersion, true).Length > 0) { reason = "item_move_contract_version_unsupported"; return false; }
            if (ValidateArguments(node.NormalizedArguments).Length > 0) return false;
            if(state.EffectRuntime.InteractionRequests.Any(r=>r.OwnerEffectId==node.EffectId))return false;
            if (CoverOperation(node.NormalizedArguments)) return ValidateCoverState(state, node, out reason);
            if (node.CharacterCoverMovementV4 != null||node.MainCoverFlowV1!=null) return false;
            if (!MarkerSubset(node.NormalizedArguments)) return TryValidateCommonState(state, node, out reason);
            if (node.CommonItemMovementV4 != null) return false;
            var plan = node.ItemMovementV4;
            if (plan == null) return node.FlowStage.Length == 0 && node.PositionChanges.Count == 0 && node.Status != EffectNodeStatus.Completed;
            if (plan.ContractVersion != ContractVersion || plan.OwnerEffectId != node.EffectId || plan.ExecutingPlayerId != node.PlayerId ||
                Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) ||
                plan.ArgumentsDigest != ItemTransferService.MovementV4Digest(node.NormalizedArguments) || plan.Transfers == null || plan.Items == null ||
                !new[] { "", "v4_prepared", "v4_wait", "v4_carrier_exit", "v4_finished" }.Contains(node.FlowStage)) return false;
            if (!ItemTransferService.MatchesMarkerPlanV4(state, node, plan)) return false;
            if (plan.Receipt == null) return node.PositionChanges.Count == 0 && node.Status != EffectNodeStatus.Completed && node.FlowStage != "v4_finished" && node.FlowStage != "v4_carrier_exit";
            return ItemTransferService.ValidateMarkerReceiptV4(state, node, plan);
        }
        public static bool HasRawFields(JObject state)
        {
            foreach (var raw in state["EffectRuntime"]?["EffectNodes"] as JArray ?? new JArray())
            {
                if (raw["ItemMovementV4"] is JToken plan && plan.Type != JTokenType.Null && !RawShape(plan, typeof(ItemMovementPlanV4))) return false;
                if (raw["CommonItemMovementV4"] is JToken common && common.Type != JTokenType.Null && !RawShape(common, typeof(CommonItemMovementPlanV4))) return false;
                if (raw["CharacterCoverMovementV4"] is JToken cover && cover.Type != JTokenType.Null && !RawShape(cover, typeof(CharacterCoverMovementPlanV4))) return false;
                if(raw["MainCoverFlowV1"] is JToken main&&main.Type!=JTokenType.Null&&!RawShape(main,typeof(MainCoverFlowStateV1)))return false;
            }
            return true;
        }
        internal static bool RawShape(JToken token, Type type)
        {
            if (token == null || token.Type == JTokenType.Null) return false;
            if (type == typeof(string)) return token.Type == JTokenType.String;
            if (type == typeof(int)) return token.Type == JTokenType.Integer && token.Value<long>() >= int.MinValue && token.Value<long>() <= int.MaxValue;
            if (type == typeof(long)) return token.Type == JTokenType.Integer && long.TryParse(token.ToString(), out _);
            if (type == typeof(bool)) return token.Type == JTokenType.Boolean;
            if (type.IsEnum) return token.Type == JTokenType.Integer && Enum.IsDefined(type, token.Value<int>());
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return token is JArray array && array.All(t => RawShape(t, type.GetGenericArguments()[0]));
            if (token is not JObject obj) return false;
            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return obj.Properties().Count() == fields.Length && fields.All(f => obj.Property(f.Name) != null &&
                ((type == typeof(ItemMovementPlanV4) || type == typeof(CommonItemMovementPlanV4) || type == typeof(CharacterCoverMovementPlanV4)) && f.Name == "Receipt" && obj[f.Name].Type == JTokenType.Null || type==typeof(CommonItemMovementPlanV4)&&f.Name=="FacilityPlacement"&&obj[f.Name].Type==JTokenType.Null||type==typeof(MainCoverFlowStateV1)&&f.Name=="NoChangeReceipt"&&obj[f.Name].Type==JTokenType.Null||RawShape(obj[f.Name], f.FieldType)));
        }
    }
}
