using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    [Serializable] public sealed class ItemTransferOrderExpectation
    {
        public string ZoneId = "", Digest = "", ExpectedTopItemId = "";
        public int OrderRevision = -1;
    }
    [Serializable] public sealed class ItemTransferBatchRequest
    {
        public int ProtocolVersion = 2, ExecutingPlayerId;
        public string OperationId = "";
        public List<ItemTransferRequest> Transfers = new List<ItemTransferRequest>();
        public List<ItemTransferOrderExpectation> ExpectedOrders = new List<ItemTransferOrderExpectation>();
    }
    [Serializable] public sealed class ItemTransferBatchCommit
    {
        public ItemTransferBatchRequest Request;
        public NormalizedValue Receipt;
        public ItemTransferResult Result;
    }
    public sealed partial class ItemTransferService
    {
        private sealed class CanonicalNormalizedResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            protected override IList<Newtonsoft.Json.Serialization.JsonProperty> CreateProperties(Type type, Newtonsoft.Json.MemberSerialization serialization)
                => base.CreateProperties(type, serialization).OrderBy(property => property.PropertyName, StringComparer.Ordinal).ToList();
        }
        private static readonly JsonSerializerSettings CanonicalNormalizedSettings = new JsonSerializerSettings {
            ContractResolver = new CanonicalNormalizedResolver()
        };
        private static readonly bool DirectNormalizedShape = HasDirectNormalizedShape();
        [ThreadStatic] private static HashSet<NormalizedValue> normalizedHashPath;
        private static bool HasDirectNormalizedShape()
        {
            bool Shape(Type type, string[] names) => type.GetProperties().Length == 0 &&
                type.GetCustomAttributes(typeof(JsonObjectAttribute), true).Length == 0 &&
                type.GetCustomAttributes(typeof(JsonConverterAttribute), true).Length == 0 &&
                type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .All(field => field.GetCustomAttributes(typeof(JsonPropertyAttribute), true).Length == 0 &&
                        field.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length == 0) &&
                type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(field => field.Name)
                    .OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(names);
            return Shape(typeof(NormalizedValue), new[] { "BooleanValue", "IntegerValue", "Items", "Kind", "Properties", "ReferenceId", "ReferenceType", "StringValue" }) &&
                Shape(typeof(NormalizedValueEntry), new[] { "Name", "Value" });
        }
        // NormalizedValue的封闭字段图只有DTO/List/标量；直接按同一JSON契约排序输出，
        // 不缓存可变值。自定义全局序列化设置仍沿原JToken分支，以保留转换器语义。
        private static string CanonicalNormalizedHash(NormalizedValue value)
        {
            using var diagnostic = GameStateCloneService.MeasureDiagnostic("canonical.hash");
            var path = normalizedHashPath ?? new HashSet<NormalizedValue>(); normalizedHashPath = null;
            try
            {
                if (DirectNormalizedShape)
                {
                    using var text = new StringWriter(CultureInfo.InvariantCulture);
                    using var writer = new JsonTextWriter(text) { Formatting = Formatting.None };
                    if (WriteNormalizedCanonical(writer, value, path, 0))
                    { writer.Flush(); return HashCanonicalText(text.ToString()); }
                }
                // 非标准容器/循环/未来新增字段交回完整序列化器；不放宽错误输入的合同。
                return HashCanonicalText(JsonConvert.SerializeObject(value, Formatting.None, CanonicalNormalizedSettings));
            }
            finally { path.Clear(); if (normalizedHashPath == null) normalizedHashPath = path; }
        }
        private static bool WriteNormalizedCanonical(JsonWriter writer, NormalizedValue value, HashSet<NormalizedValue> path, int depth)
        {
            if (value == null) { writer.WriteNull(); return true; }
            if (depth > 64 || !path.Add(value)) return false;
            try
            {
                if (value.Items != null && value.Items.GetType() != typeof(List<NormalizedValue>) ||
                    value.Properties != null && value.Properties.GetType() != typeof(List<NormalizedValueEntry>)) return false;
                writer.WriteStartObject();
                writer.WritePropertyName("BooleanValue"); writer.WriteValue(value.BooleanValue);
                writer.WritePropertyName("IntegerValue"); writer.WriteValue(value.IntegerValue);
                writer.WritePropertyName("Items");
                if (value.Items == null) writer.WriteNull();
                else
                {
                    writer.WriteStartArray();
                    foreach (var item in value.Items) if (!WriteNormalizedCanonical(writer, item, path, depth + 1)) return false;
                    writer.WriteEndArray();
                }
                writer.WritePropertyName("Kind"); writer.WriteValue((int)value.Kind);
                writer.WritePropertyName("Properties");
                if (value.Properties == null) writer.WriteNull();
                else
                {
                    writer.WriteStartArray();
                    foreach (var entry in value.Properties)
                    {
                        if (entry == null) { writer.WriteNull(); continue; }
                        writer.WriteStartObject(); writer.WritePropertyName("Name"); writer.WriteValue(entry.Name);
                        writer.WritePropertyName("Value"); if (!WriteNormalizedCanonical(writer, entry.Value, path, depth + 1)) return false;
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WritePropertyName("ReferenceId"); writer.WriteValue(value.ReferenceId);
                writer.WritePropertyName("ReferenceType"); writer.WriteValue(value.ReferenceType);
                writer.WritePropertyName("StringValue"); writer.WriteValue(value.StringValue);
                writer.WriteEndObject(); return true;
            }
            finally { path.Remove(value); }
        }
        public const int BatchReceiptProtocolVersion = 2;
        public const string BatchRequestDigestVersion = "yc.transfer-batch.sha256.v2";
        public const int TokenTransferProtocolVersion = 3;
        public const string TokenRequestDigestVersion = "yc.token-transfer.sha256.v1";
        public static string BatchRequestDigest(ItemTransferBatchRequest batch) => batch?.ProtocolVersion == TokenTransferProtocolVersion
            ? TokenRequestDigestVersion + ":" + CanonicalHash(JToken.FromObject(batch))
            : BatchRequestDigestVersion + ":" + FacilityBuildQueryService.Hash(CanonicalBatchText(JToken.FromObject(batch)));
        private static string CanonicalHash(JToken value)
        {
            using var diagnostic = GameStateCloneService.MeasureDiagnostic("canonical.hash");
            return HashCanonicalText(CanonicalBatchText(value));
        }
        private static string HashCanonicalText(string value)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
        }
        // 保持原Ordinal排序和Json.NET标量格式，直接输出，避免每次校验再复制整棵JToken树。
        private static string CanonicalBatchText(JToken value)
        {
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            using var writer = new JsonTextWriter(text) { Formatting = Formatting.None };
            WriteCanonicalBatch(writer, value);
            writer.Flush(); return text.ToString();
        }
        private static void WriteCanonicalBatch(JsonWriter writer, JToken value)
        {
            if (value is JObject obj)
            {
                writer.WriteStartObject();
                foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonicalBatch(writer, property.Value); }
                writer.WriteEndObject();
            }
            else if (value is JArray array)
            {
                writer.WriteStartArray();
                foreach (var item in array) WriteCanonicalBatch(writer, item);
                writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
        public static string LocalStateDigest(ItemInstanceState item)
        {
            const string prefix = "yc.item-local-state.sha256.v1:";
            // 与旧JToken规范JSON完全相同的封闭字段写入；全局设置、null、派生容器、
            // 环及未来字段继续原分支，保持原错误类型、先后与消息。
            if (JsonConvert.DefaultSettings == null && item.LocalState != null && DirectNormalizedShape &&
                TryDirectLocalStateHash(item.LocalState, out string hash)) return prefix + hash;
            return prefix + CanonicalHash(JToken.FromObject(item.LocalState));
        }
        private static bool TryDirectLocalStateHash(NormalizedValue value, out string hash)
        {
            using var diagnostic = GameStateCloneService.MeasureDiagnostic("canonical.hash");
            hash = null;
            var path = normalizedHashPath ?? new HashSet<NormalizedValue>(); normalizedHashPath = null;
            try
            {
                using var text = new StringWriter(CultureInfo.InvariantCulture);
                using var writer = new JsonTextWriter(text) { Formatting = Formatting.None };
                if (!WriteNormalizedCanonical(writer, value, path, 0)) return false;
                writer.Flush(); hash = HashCanonicalText(text.ToString()); return true;
            }
            finally { path.Clear(); if (normalizedHashPath == null) normalizedHashPath = path; }
        }
        // 付款内部无序库存事务不限方块，私有批次3不是旧公开MoveItem v3执行节点。
        // 新物品参数统一v4；地图、轨道及奖励取得仍遵守各自生命周期。
        // 这些操作配置不是token的全集定义，卡牌也不会因共用底层事务获得token操作许可。
        public static bool IsTokenProfile(string profile) => profile == "core.cube_storage" || profile == "core.player_marker_storage" || profile == "core.token_storage";
        public static bool IsTokenKind(ItemPhysicalKind kind) => kind == ItemPhysicalKind.Cube || kind == ItemPhysicalKind.Token;
        // 只规划显式实例：数量、是否需选择、去向及后继顺序属于调用者。
        public bool TryPlanTokens(GameState state, AbilitySourceRef source, NormalizedValue args, IEnumerable<AbilitySourceDeclaration> declarations,
            string operationId, out ItemTransferBatchRequest batch, out string reason)
        {
            batch = null;
            if (!TryTokenCostArguments(args, out args, out reason)) return false;
            int player = int.Parse(Text(args, "player"), System.Globalization.CultureInfo.InvariantCulture);
            var ids = TokenCostIds(args).Items.Select(i => i.StringValue).ToList();
            if (ids.Count != Number(args, "requiredCount")) { reason = ids.Count < Number(args, "requiredCount") ? "insufficient_items" : "transfer_count_mismatch"; return false; }
            var legs = new List<ItemTransferRequest>();
            foreach (string id in ids)
            {
                var item = query.FindItem(state, id);
                if (item == null) { reason = "unknown_item"; return false; }
                string destination = Text(Value(args, "destination"), "kind") == "original_supply" ? item.OriginalSupplyZoneId : Text(Value(args, "destination"), "zoneId");
                if (string.IsNullOrEmpty(destination)) { reason = "original_supply_missing"; return false; }
                if (query.FindZone(state, TokenCostSource(args)) == null) { reason = "source_zone_changed"; return false; }
                if (query.FindZone(state, destination) == null) { reason = "destination_rejected"; return false; }
                var leg = new ItemTransferRequest { ExecutingPlayerId = player, ItemIds = new List<string> { id },
                    ExpectedSource = new ItemPositionRef { ZoneId = TokenCostSource(args) }, Destination = new ZoneRef { ZoneId = destination },
                    InsertionKind = "unordered", OperationProfileId = Text(args, "operationProfileId"), RequiredCount = 1 };
                reason = CardMarkerCarrier.ValidateTransferWithSource(state, source, leg, declarations);
                if (reason.Length > 0) return false;
                // 相邻同去向合成同一段，保持Lua给定顺序，避免为同组逐枚重复整区预检。
                var prior = legs.LastOrDefault();
                if (prior != null && prior.Destination.ZoneId == destination) { prior.ItemIds.Add(id); prior.RequiredCount++; }
                else legs.Add(leg);
            }
            // token无设施层序，不读取/保存设施expectedOrders。只复用事务提交和类型化位置证据。
            var planned = new ItemTransferBatchRequest { ProtocolVersion = TokenTransferProtocolVersion, ExecutingPlayerId = player, OperationId = operationId, Transfers = legs };
            var valid = ValidateTokenBatch(state, planned);
            if (!valid.IsValid) { reason = valid.ReasonCode; return false; }
            batch = planned; reason = ""; return true;
        }
        private ItemTransferValidationResult ValidateTokenBatch(GameState state, ItemTransferBatchRequest batch)
        {
            if (batch == null || batch.ProtocolVersion != 3 || string.IsNullOrEmpty(batch.OperationId) || batch.ExecutingPlayerId < 1 ||
                batch.Transfers == null || batch.Transfers.Count == 0 || batch.ExpectedOrders == null || batch.ExpectedOrders.Count != 0) return Invalid("token_transfer_batch_invalid");
            if (!query.TryValidate(state, out string reason)) return Invalid(reason);
            var ids = new HashSet<string>(StringComparer.Ordinal); var touched = new HashSet<string>(StringComparer.Ordinal);
            var protectedZones = new[] { "core.zone.influence_slots", "core.zone.score_track", "core.zone.board_marker_track", "core.zone.covered_cubes", FacilityLayerQuery.CellTemplate, FacilityLayerQuery.CoveredTemplate };
            foreach (var leg in batch.Transfers)
            {
                if (leg?.ExpectedSource == null || leg.Destination == null || leg.ItemIds == null || leg.ItemIds.Count == 0 || leg.RequiredCount != leg.ItemIds.Count ||
                    leg.ExecutingPlayerId != batch.ExecutingPlayerId || !IsTokenProfile(leg.OperationProfileId) || leg.CarrierPolicyId != "preserve" ||
                    leg.InsertionKind != "unordered" || leg.ExpectedSource.SlotId != "" || leg.DestinationSlotId != "") return Invalid("token_transfer_leg_invalid");
                var from = query.FindZone(state, leg.ExpectedSource.ZoneId); var to = query.FindZone(state, leg.Destination.ZoneId);
                if (from == null || to == null || from.ZoneId == to.ZoneId) return Invalid("token_transfer_zone_invalid");
                if (new[] { from, to }.Any(z => protectedZones.Contains(z.TemplateId) || templates.Find(z.TemplateId)?.OrderingKind != ZoneOrderingKind.Unordered)) return Invalid("operation_profile_rejected");
                foreach (string id in leg.ItemIds)
                {
                    if (!ids.Add(id)) return Invalid("duplicate_item_id");
                    var item = query.FindItem(state, id);
                    if (item == null || !IsTokenKind(item.PhysicalKind)) return Invalid("token_transfer_item_kind_rejected");
                    if (state.ItemZones.Zones.Any(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == id && state.ItemZones.Items.Any(i => i.ZoneId == z.ZoneId))) return Invalid("carrier_exit_requires_lifecycle");
                    if (UsageTracks.HasCurrentMarker(state, id)) return Invalid("usage_track_host_managed");
                }
                var valid = ValidateCore(state, leg, false, false, false); if (!valid.IsValid) return valid;
                touched.Add(from.ZoneId); touched.Add(to.ZoneId);
            }
            if (ids.Count > MaximumTokenCount) return Invalid("token_transfer_count_exceeded");
            var preview = state.CreateItemTransferPreview(); var moved = ApplyBatch(preview, batch);
            if (moved.MovedCount != ids.Count) return Invalid("insufficient_items");
            foreach (string zoneId in touched)
            {
                var zone = query.FindZone(preview, zoneId);
                int capacity = zone.Configuration?.CapacityPolicy == "bounded" ? zone.Configuration.Capacity.Value : templates.Find(zone.TemplateId).Capacity;
                if (capacity >= 0 && preview.ItemZones.Items.Count(i => i.ZoneId == zoneId) > capacity) return Invalid("destination_capacity_exceeded");
            }
            if (!query.TryValidate(preview, out reason)) return Invalid(reason);
            return new ItemTransferValidationResult { IsValid = true };
        }
        private static bool TryValidateTokenReceipt(EffectNodeRuntimeState owner, out string reason)
        {
            reason = "token_transfer_receipt_invalid";
            var batch = owner.TransferBatchRequest; var receipt = Value(owner.ContinuationData, "transferBatchReceipt");
            if (batch == null || batch.ProtocolVersion != 3 || batch.ExecutingPlayerId != owner.PlayerId || string.IsNullOrEmpty(batch.OperationId) ||
                batch.Transfers == null || batch.Transfers.Count == 0 || batch.ExpectedOrders == null || batch.ExpectedOrders.Count != 0 || receipt?.Kind != NormalizedValueKind.Object || receipt.Properties.Count != 4 ||
                Value(receipt, "protocolVersion")?.Kind != NormalizedValueKind.Integer || Number(receipt, "protocolVersion") != 3 ||
                Text(receipt, "operationId") != batch.OperationId || Text(receipt, "requestDigest") != BatchRequestDigest(batch) ||
                batch.Transfers.Any(t => t?.ItemIds == null || t.ItemIds.Count == 0 || t.RequiredCount != t.ItemIds.Count || t.ExecutingPlayerId != owner.PlayerId ||
                    t.ExpectedSource == null || t.Destination == null || string.IsNullOrEmpty(t.ExpectedSource.ZoneId) || string.IsNullOrEmpty(t.Destination.ZoneId) || t.ExpectedSource.ZoneId == t.Destination.ZoneId ||
                    t.ExpectedSource.SlotId != "" || t.DestinationSlotId != "" || t.InsertionKind != "unordered" || t.CarrierPolicyId != "preserve" || !IsTokenProfile(t.OperationProfileId))) return false;
            var ids = batch.Transfers.SelectMany(t => t.ItemIds).ToList();
            if (ids.Count > MaximumTokenCount || ids.Any(string.IsNullOrEmpty) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count ||
                Value(receipt, "movedCount")?.Kind != NormalizedValueKind.Integer || Number(receipt, "movedCount") != ids.Count) return false;
            foreach (var leg in batch.Transfers) foreach (string id in leg.ItemIds)
            {
                var changes = owner.PositionChanges.Where(c => c.ItemId == id).ToList();
                if (changes.Count != 1 || changes[0].From?.ZoneId != leg.ExpectedSource.ZoneId || changes[0].To?.ZoneId != leg.Destination.ZoneId || changes[0].From.SlotId != "" || changes[0].To.SlotId != "") return false;
            }
            if (owner.EffectTypeId == TypeId)
            {
                var args = owner.NormalizedArguments;
                if (!TryTokenCostArguments(args, out args, out _) || Text(args, "player") != owner.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) || batch.OperationId != owner.EffectId + ":move" ||
                    !ids.SequenceEqual(TokenCostIds(args).Items.Select(i => i.StringValue)) || ids.Count != Number(args, "requiredCount") ||
                    batch.Transfers.Any(t => t.ExpectedSource.ZoneId != TokenCostSource(args) || t.OperationProfileId != Text(args, "operationProfileId") ||
                        Text(Value(args, "destination"), "kind") != "original_supply" && t.Destination.ZoneId != Text(Value(args, "destination"), "zoneId"))) return false;
            }
            reason = ""; return true;
        }
        public static bool TryValidateTokenState(GameState state, EffectNodeRuntimeState owner, out string reason)
        {
            if (!TryValidateTokenReceipt(owner, out reason)) return false;
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (owner.TransferBatchRequest.Transfers.Any(t => t.ItemIds.Any(id => {
                var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == id);
                return item == null || !IsTokenKind(item.PhysicalKind) || !types.HasOperationProfile(item.TypeId, t.OperationProfileId);
            })))
            { reason = "token_transfer_item_kind_rejected"; return false; }
            if (owner.EffectTypeId == TypeId && Text(Value(owner.NormalizedArguments, "destination"), "kind") == "original_supply" &&
                owner.TransferBatchRequest.Transfers.Any(t => t.ItemIds.Any(id => state.ItemZones.Items.SingleOrDefault(i => i.ItemId == id)?.OriginalSupplyZoneId != t.Destination.ZoneId)))
            { reason = "token_transfer_original_supply_changed"; return false; }
            if (owner.Status == EffectNodeStatus.Completed)
            {
                var result = owner.NormalizedResult; var ids = owner.TransferBatchRequest.Transfers.SelectMany(t => t.ItemIds).ToList();
                if (owner.EffectTypeId == TypeId && (!Flag(result, "changed") || Number(result, "movedCount") != ids.Count || Value(result, "movedItemIds")?.Kind != NormalizedValueKind.Array ||
                    !Value(result, "movedItemIds").Items.Select(i => i.StringValue).SequenceEqual(ids))) { reason = "token_transfer_result_invalid"; return false; }
            }
            reason = ""; return true;
        }
        private static ItemTransferResult ReadTokenResult(EffectNodeRuntimeState owner)
        {
            var ids = owner.TransferBatchRequest.Transfers.SelectMany(t => t.ItemIds).ToList();
            return new ItemTransferResult { Changed = true, MovedCount = ids.Count, MovedItemIds = ids,
                PositionChanges = ids.Select(id => GameStateCloneService.DeepClone(owner.PositionChanges.Single(c => c.ItemId == id))).ToList() };
        }
        public static bool TryValidateBatchReceipt(EffectNodeRuntimeState owner, out string reason)
        {
            if (owner.TransferBatchRequest?.ProtocolVersion == 3 || Number(Value(owner.ContinuationData, "transferBatchReceipt"), "protocolVersion") == 3)
                return TryValidateTokenReceipt(owner, out reason);
            reason = "transfer_batch_receipt_invalid";
            var receipt = Value(owner.ContinuationData, "transferBatchReceipt"); var batch = owner.TransferBatchRequest;
            if (receipt == null) { if (batch != null) return false; reason = ""; return true; }
            if (batch == null || batch.ProtocolVersion != 2 || batch.ExecutingPlayerId != owner.PlayerId ||
                batch.Transfers == null || batch.Transfers.Count == 0 || batch.ExpectedOrders == null ||
                batch.Transfers.Any(t => t?.ItemIds == null || t.ExpectedSource == null || t.Destination == null ||
                    t.ExecutingPlayerId != batch.ExecutingPlayerId || t.ItemIds.Count == 0 || t.RequiredCount != t.ItemIds.Count ||
                    t.OperationProfileId != "core.facility_card_transfer" || t.CarrierPolicyId != "preserve") ||
                batch.ExpectedOrders.Any(e => e == null || string.IsNullOrEmpty(e.ZoneId) || string.IsNullOrEmpty(e.Digest) || e.ExpectedTopItemId == null) ||
                Number(receipt, "protocolVersion") != BatchReceiptProtocolVersion || Text(receipt, "operationId") != batch.OperationId ||
                Text(receipt, "requestDigest") != BatchRequestDigest(batch)) return false;
            var touched = new HashSet<string>(batch.Transfers.SelectMany(t => new[] { t.ExpectedSource.ZoneId, t.Destination.ZoneId }), StringComparer.Ordinal);
            if (batch.ExpectedOrders.Select(e => e.ZoneId).Distinct(StringComparer.Ordinal).Count() != batch.ExpectedOrders.Count ||
                !touched.SetEquals(batch.ExpectedOrders.Select(e => e.ZoneId))) return false;
            // 通用批量入口保存原参数；不能只把typed请求与收据摘要一起重算便改变历史请求。
            // Build/Retire等原生调用保存的是各自业务参数，继续消费其typed原请求及当次位置证据。
            if (Number(owner.NormalizedArguments, "apiVersion") == 2 && Value(owner.NormalizedArguments, "transfers") != null)
            {
                try { if (BatchRequestDigest(ItemTransferEffectExecutor.BatchRequest(owner.NormalizedArguments)) != BatchRequestDigest(batch)) return false; }
                catch (InvalidOperationException) { return false; }
            }
            var ids = batch.Transfers.SelectMany(t => t.ItemIds).ToList(); var moved = Value(receipt, "movedItemIds"); var positions = Value(receipt, "positionChanges");
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || moved?.Kind != NormalizedValueKind.Array || positions?.Kind != NormalizedValueKind.Array ||
                !moved.Items.Select(v => v.StringValue).SequenceEqual(ids) || positions.Items.Count != ids.Count) return false;
            foreach (var leg in batch.Transfers) foreach (string id in leg.ItemIds)
            {
                var changes = positions.Items.Where(p => Text(p, "itemId") == id).ToList();
                if (changes.Count != 1) return false;
                var saved = changes[0];
                if (Text(saved, "fromZone") != leg.ExpectedSource.ZoneId || Text(saved, "toZone") != leg.Destination.ZoneId ||
                    leg.ExpectedSource.SlotId.Length > 0 && Text(saved, "fromSlot") != leg.ExpectedSource.SlotId || Text(saved, "toSlot") != leg.DestinationSlotId ||
                    owner.PositionChanges.Count(c => c.ItemId == id) != 1 ||
                    !owner.PositionChanges.Any(c => c.ItemId == id && c.From.ZoneId == Text(saved, "fromZone") && c.From.SlotId == Text(saved, "fromSlot") &&
                        c.To.ZoneId == Text(saved, "toZone") && c.To.SlotId == Text(saved, "toSlot"))) return false;
            }
            reason = ""; return true;
        }
        public ItemTransferValidationResult ValidateBatch(GameState state, ItemTransferBatchRequest batch)
        {
            if (batch?.ProtocolVersion == 3) return ValidateTokenBatch(state, batch);
            if (batch == null || batch.ProtocolVersion != 2 || string.IsNullOrEmpty(batch.OperationId) ||
                batch.Transfers == null || batch.Transfers.Count == 0 || batch.ExpectedOrders == null) return Invalid("transfer_batch_invalid");
            if (!query.TryValidate(state, out string invalid)) return Invalid(invalid);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transfer in batch.Transfers)
            {
                if (transfer == null || transfer.ExecutingPlayerId != batch.ExecutingPlayerId ||
                    transfer.OperationProfileId != "core.facility_card_transfer" || transfer.ItemIds == null ||
                    transfer.ItemIds.Count == 0 || transfer.RequiredCount != transfer.ItemIds.Count) return Invalid("transfer_batch_leg_invalid");
                foreach (var id in transfer.ItemIds)
                    if (!ids.Add(id) || !FacilityLayerQuery.IsFacility(query.FindItem(state, id))) return Invalid("transfer_batch_item_invalid");
                var validation = ValidateCore(state, transfer, false, false, true);
                if (!validation.IsValid) return validation;
                touched.Add(transfer.ExpectedSource.ZoneId); touched.Add(transfer.Destination.ZoneId);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var expected in batch.ExpectedOrders)
            {
                var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == expected?.ZoneId);
                if (zone == null || !seen.Add(zone.ZoneId) || !touched.Contains(zone.ZoneId) ||
                    expected.Digest != FacilityLayerQuery.Digest(state, zone.ZoneId)) return Invalid("transfer_batch_order_changed");
                var order = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone.ZoneId);
                if (expected.OrderRevision >= 0 && expected.OrderRevision != (order?.OrderRevision ?? 0)) return Invalid("transfer_batch_order_changed");
                var top = order?.ItemIds.FirstOrDefault() ?? state.ItemZones.Items.FirstOrDefault(i => i.ZoneId == zone.ZoneId)?.ItemId ?? "";
                if (expected.ExpectedTopItemId.Length > 0 && expected.ExpectedTopItemId != top) return Invalid("transfer_batch_top_changed");
            }
            if (!seen.SetEquals(touched)) return Invalid("transfer_batch_all_zones_required");
            // 下层只用于同次覆盖或真实回盒提顶，不能给普通内容开放任意下层取牌。
            foreach (var transfer in batch.Transfers)
            {
                var source = query.FindZone(state, transfer.ExpectedSource.ZoneId);
                var destination = query.FindZone(state, transfer.Destination.ZoneId);
                if (source.TemplateId == FacilityLayerQuery.CoveredTemplate)
                {
                    if (destination.TemplateId != FacilityLayerQuery.CellTemplate || source.ParentId != destination.ZoneId ||
                        transfer.ItemIds.Count != 1 || FacilityLayerQuery.Ordered(state, source.ZoneId).FirstOrDefault() != transfer.ItemIds[0] ||
                        !batch.Transfers.Any(t => t.ExpectedSource.ZoneId == destination.ZoneId &&
                            query.FindZone(state, t.Destination.ZoneId)?.TemplateId == "core.zone.box_retired"))
                        return Invalid("facility_layer_promotion_invalid");
                }
                if (destination.TemplateId == FacilityLayerQuery.CoveredTemplate)
                {
                    if (source.TemplateId != FacilityLayerQuery.CellTemplate || destination.ParentId != source.ZoneId ||
                        transfer.ItemIds.Count != 1 || transfer.InsertionKind != "top" ||
                        !batch.Transfers.Any(t => t.Destination.ZoneId == source.ZoneId && t.ExpectedSource.ZoneId != destination.ZoneId))
                        return Invalid("facility_layer_overlay_invalid");
                }
            }
            var preview = state.CreateItemTransferPreview();
            ApplyBatch(preview, batch);
            if (!query.TryValidate(preview, out invalid)) return Invalid(invalid);
            return new ItemTransferValidationResult { IsValid = true };
        }
        public ItemTransferResult RelocateBatch(EffectExecutionContext context, ItemTransferBatchRequest batch)
        {
            if (context == null || batch == null || context.Node.PlayerId != batch.ExecutingPlayerId)
                throw new InvalidOperationException("transfer_permission_denied");
            string digest = BatchRequestDigest(batch);
            var receipt = Value(context.Node.ContinuationData, "transferBatchReceipt");
            if (receipt != null)
            {
                if (!TryValidateBatchReceipt(context.Node, out _) || Text(receipt, "operationId") != batch.OperationId || Text(receipt, "requestDigest") != digest)
                    throw new InvalidOperationException("transfer_batch_receipt_conflict");
                if (batch.ProtocolVersion == 3 && !TryValidateTokenState(context.State, context.Node, out string invalid)) throw new InvalidOperationException(invalid);
                return batch.ProtocolVersion == 3 ? ReadTokenResult(context.Node) : ReadBatchResult(receipt);
            }
            foreach (var leg in batch.Transfers) ValidateExecutionGuards(context, leg, batch.ProtocolVersion == 3);
            var validation = ValidateBatch(context.State, batch);
            if (!validation.IsValid) throw new InvalidOperationException(validation.ReasonCode);
            var preview = GameStateCloneService.DeepClone(context.State);
            var moved = ApplyBatch(preview, batch);
            if (!query.TryValidate(preview, out string reason)) throw new InvalidOperationException(reason);
            var owner = preview.EffectRuntime.EffectNodes.Single(n => n.EffectId == context.Node.EffectId);
            owner.PositionChanges.AddRange(GameStateCloneService.DeepClone(moved.PositionChanges));
            owner.TransferBatchRequest = GameStateCloneService.DeepClone(batch);
            Set(owner, "transferBatchReceipt", batch.ProtocolVersion == 3 ? Object("protocolVersion", NormalizedValue.CreateInteger(3),
                "operationId", NormalizedValue.CreateString(batch.OperationId), "requestDigest", NormalizedValue.CreateString(digest),
                "movedCount", NormalizedValue.CreateInteger(moved.MovedCount)) : Object("protocolVersion", NormalizedValue.CreateInteger(BatchReceiptProtocolVersion),
                "operationId", NormalizedValue.CreateString(batch.OperationId), "requestDigest", NormalizedValue.CreateString(digest),
                "movedItemIds", Strings(moved.MovedItemIds), "positionChanges", NormalizedValue.CreateArray(moved.PositionChanges.Select(c =>
                    Object("itemId", NormalizedValue.CreateString(c.ItemId), "fromZone", NormalizedValue.CreateString(c.From.ZoneId),
                        "fromSlot", NormalizedValue.CreateString(c.From.SlotId), "toZone", NormalizedValue.CreateString(c.To.ZoneId),
                        "toSlot", NormalizedValue.CreateString(c.To.SlotId))).ToList())));
            // 不在每一段提交；外层Kernel对工作副本和位置收据做一次RuleCommit。
            GameStateCloneService.CopyTo(context.State, preview);
            return moved;
        }
        // 多个已选定来源的原生事务消费：不接受source参数，每段仍从实际执行节点复核来源。
        // 一次副本提交覆盖所有token；不挪用单节点transferBatchReceipt槽位覆盖前一来源。
        public IReadOnlyList<ItemTransferBatchCommit> RelocateTokenBatches(EffectExecutionContext context, IReadOnlyList<ItemTransferBatchRequest> batches)
        {
            if (context == null || batches == null || batches.Any(b => b?.ProtocolVersion != TokenTransferProtocolVersion || b.ExecutingPlayerId != context.Node.PlayerId) ||
                batches.Select(b => b.OperationId).Distinct(StringComparer.Ordinal).Count() != batches.Count) throw new InvalidOperationException("token_transfer_batch_set_invalid");
            if (batches.Count == 0) return Array.Empty<ItemTransferBatchCommit>();
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in batches)
            {
                if (batch.Transfers == null) throw new InvalidOperationException("token_transfer_batch_invalid");
                if (context.Node.Payment != null && !ResourcePaymentSourceService.HasActualTransferBatchAuthority(context, batch))
                    throw new InvalidOperationException("payment_token_source_authority_invalid");
                foreach (var leg in batch.Transfers)
                {
                    if (leg?.ItemIds == null || leg.ItemIds.Any(id => !selected.Add(id))) throw new InvalidOperationException("duplicate_item_id");
                    ValidateExecutionGuards(context, leg, true);
                }
            }
            var preview = GameStateCloneService.DeepClone(context.State); var commits = new List<ItemTransferBatchCommit>();
            foreach (var batch in batches)
            {
                var valid = ValidateTokenBatch(preview, batch);
                if (!valid.IsValid) throw new InvalidOperationException(valid.ReasonCode);
                var moved = ApplyBatch(preview, batch);
                commits.Add(new ItemTransferBatchCommit { Request = GameStateCloneService.DeepClone(batch), Result = moved,
                    Receipt = Object("protocolVersion", NormalizedValue.CreateInteger(TokenTransferProtocolVersion), "operationId", NormalizedValue.CreateString(batch.OperationId),
                        "requestDigest", NormalizedValue.CreateString(BatchRequestDigest(batch)), "movedCount", NormalizedValue.CreateInteger(moved.MovedCount)) });
            }
            if (!query.TryValidate(preview, out string reason)) throw new InvalidOperationException(reason);
            preview.EffectRuntime.EffectNodes.Single(n => n.EffectId == context.Node.EffectId).PositionChanges.AddRange(
                GameStateCloneService.DeepClone(commits.SelectMany(c => c.Result.PositionChanges).ToList()));
            GameStateCloneService.CopyTo(context.State, preview); return commits;
        }
        public static bool TryValidateTokenCommit(GameState state, int player, NormalizedValue args, ItemTransferBatchCommit commit, out string reason)
        {
            reason = "token_transfer_commit_invalid";
            if (commit?.Request == null || commit.Result?.PositionChanges == null || commit.Result.MovedItemIds == null || commit.Receipt == null || commit.Request.OperationId == null || !commit.Request.OperationId.EndsWith(":move", StringComparison.Ordinal)) return false;
            var view = new EffectNodeRuntimeState { EffectId = commit.Request.OperationId.Substring(0, commit.Request.OperationId.Length - 5),
                EffectTypeId = TypeId, PlayerId = player, Status = EffectNodeStatus.Completed, TransferBatchRequest = commit.Request,
                NormalizedArguments = args, ContinuationData = Object("transferBatchReceipt", commit.Receipt), PositionChanges = commit.Result.PositionChanges,
                NormalizedResult = Object("changed", NormalizedValue.CreateBoolean(commit.Result.Changed), "movedCount", NormalizedValue.CreateInteger(commit.Result.MovedCount),
                    "movedItemIds", Strings(commit.Result.MovedItemIds)) };
            return TryValidateTokenState(state, view, out reason);
        }
        private static ItemTransferResult ApplyBatch(GameState state, ItemTransferBatchRequest batch)
        {
            var result = new ItemTransferResult();
            var ids = new HashSet<string>(batch.Transfers.SelectMany(t => t.ItemIds), StringComparer.Ordinal);
            var oldOrders = state.ItemZones.Orders.ToDictionary(o => o.ZoneId, o => o.ItemIds.ToArray(), StringComparer.Ordinal);
            foreach (var order in state.ItemZones.Orders) order.ItemIds.RemoveAll(ids.Contains);
            foreach (var leg in batch.Transfers)
            {
                var moved = Apply(state, leg, false);
                result.MovedItemIds.AddRange(moved.MovedItemIds); result.PositionChanges.AddRange(moved.PositionChanges);
            }
            foreach (var leg in batch.Transfers)
            {
                var order = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == leg.Destination.ZoneId);
                if (order == null) continue;
                if (leg.InsertionKind == "top") order.ItemIds.InsertRange(0, leg.ItemIds);
                else order.ItemIds.AddRange(leg.ItemIds);
            }
            foreach (var order in state.ItemZones.Orders)
                if (!oldOrders[order.ZoneId].SequenceEqual(order.ItemIds)) order.OrderRevision++;
            result.MovedCount = result.MovedItemIds.Count; result.Changed = result.MovedCount > 0;
            return result;
        }
        private static ItemTransferResult ReadBatchResult(NormalizedValue receipt)
        {
            var result = new ItemTransferResult { MovedItemIds = Value(receipt, "movedItemIds").Items.Select(v => v.StringValue).ToList() };
            foreach (var c in Value(receipt, "positionChanges").Items)
                result.PositionChanges.Add(new ItemPositionChange { ItemId = Text(c, "itemId"),
                    From = new ItemPositionRef { ZoneId = Text(c, "fromZone"), SlotId = Text(c, "fromSlot") },
                    To = new ItemPositionRef { ZoneId = Text(c, "toZone"), SlotId = Text(c, "toSlot") } });
            result.MovedCount = result.MovedItemIds.Count; result.Changed = result.MovedCount > 0; return result;
        }
        public static ItemTransferBatchRequest CreateBatch(GameState state, int player, string operationId, params ItemTransferRequest[] legs)
        {
            var batch = new ItemTransferBatchRequest { ExecutingPlayerId = player, OperationId = operationId, Transfers = legs.ToList() };
            foreach (string id in legs.SelectMany(t => new[] { t.ExpectedSource.ZoneId, t.Destination.ZoneId }).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal))
                batch.ExpectedOrders.Add(new ItemTransferOrderExpectation { ZoneId = id, Digest = FacilityLayerQuery.Digest(state, id),
                    OrderRevision = state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == id)?.OrderRevision ?? 0 });
            return batch;
        }
        public static NormalizedValue BatchArguments(ItemTransferBatchRequest batch) => batch.ProtocolVersion != 2
            ? throw new InvalidOperationException("token_transfer_requires_token_arguments") : Object(
            "apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString(batch.ExecutingPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "operationId", NormalizedValue.CreateString(batch.OperationId),
            "transfers", NormalizedValue.CreateArray(batch.Transfers.Select(t => Object(
                "itemRefs", NormalizedValue.CreateArray(t.ItemIds.Select(id => Object("itemId", NormalizedValue.CreateString(id))).ToList()),
                "expectedSource", Object("zoneId", NormalizedValue.CreateString(t.ExpectedSource.ZoneId)),
                "destination", Object("zoneId", NormalizedValue.CreateString(t.Destination.ZoneId)),
                "insertion", Object("kind", NormalizedValue.CreateString(t.InsertionKind)),
                "operationProfileId", NormalizedValue.CreateString(t.OperationProfileId),
                "carrierPolicy", NormalizedValue.CreateString(t.CarrierPolicyId),
                "requiredCount", NormalizedValue.CreateInteger(t.RequiredCount))).ToList()),
            "expectedOrders", NormalizedValue.CreateArray(batch.ExpectedOrders.Select(e => Object(
                "zoneId", NormalizedValue.CreateString(e.ZoneId), "digest", NormalizedValue.CreateString(e.Digest),
                "orderRevision", NormalizedValue.CreateInteger(e.OrderRevision),
                "expectedTopItemId", NormalizedValue.CreateString(e.ExpectedTopItemId))).ToList()));
    }
}

