using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Items;
using YC.Domain.Influence;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    [Serializable] public sealed class InfluenceDepartureDestinationReceipt
    {
        public string DepartureId = "", ItemId = "", OriginalSupplyZoneId = "", DestinationZoneId = "", Status = "";
        public List<string> ResponseIds = new List<string>();
    }
    [Serializable] public sealed class InfluenceDepartureCompletionReceipt
    {
        public int ProtocolVersion = 2, ActorId;
        public string ReceiptId = "", OwnerEffectId = "", DefinitionVersion = "", ArgumentsDigest = "";
        public NormalizedValue BaseResult;
        public List<InfluenceDepartureDestinationReceipt> Destinations = new List<InfluenceDepartureDestinationReceipt>();
    }
    // API-34的合法自动批次前提；4.13完整typed收据/128值边界。分页归API-30，不能截断去向或扩张移除数量。
    public static class InfluenceDepartureCompletion
    {
        private static string Digest(InfluenceDepartureCompletionReceipt r) => ItemTransferService.MovementV4Digest(new {
            r.ProtocolVersion, r.ActorId, r.OwnerEffectId, r.DefinitionVersion, r.ArgumentsDigest, r.BaseResult, r.Destinations });
        public static void Complete(EffectExecutionContext context, IReadOnlyList<InfluenceDepartureRecord> records)
        {
            var node = context.Node; var original = Base(node.NormalizedResult);
            var expanded = Expanded(original, records);
            if (expanded.IsValid()) { node.NormalizedResult = expanded; return; }
            var receipt = new InfluenceDepartureCompletionReceipt { ActorId = node.PlayerId, OwnerEffectId = node.EffectId, DefinitionVersion = node.DefinitionVersion,
                ArgumentsDigest = ItemTransferService.MovementV4Digest(node.NormalizedArguments), BaseResult = original,
                Destinations = records.Select(d => new InfluenceDepartureDestinationReceipt { DepartureId = d.DepartureId, ItemId = d.ItemId,
                    OriginalSupplyZoneId = d.OriginalSupplyZoneId, DestinationZoneId = d.DestinationZoneId, Status = d.Status, ResponseIds = d.ResponseIds.ToList() }).ToList() };
            receipt.ReceiptId = StableIdFactory.Create("departure-completion", node.EffectId, Digest(receipt));
            node.DepartureCompletionReceipt = receipt; node.NormalizedResult = Result(receipt);
        }
        private static NormalizedValue Base(NormalizedValue result)
        {
            var original = result.Clone();
            original.Properties.RemoveAll(p => new[] { "destinations", "responseRefs", "destinationFacts", "responseRefCount", "responseRefsRequireReceipt" }.Contains(p.Name));
            return original;
        }
        private static NormalizedValue Expanded(NormalizedValue original, IReadOnlyList<InfluenceDepartureRecord> records)
        {
            var expanded = original.Clone();
            InfluenceDestinationResolver.Set(expanded, "responseRefs", Strings(records.SelectMany(d => d.ResponseIds)));
            InfluenceDestinationResolver.Set(expanded, "destinations", NormalizedValue.CreateArray(records.Select(d => Object("departureId", NormalizedValue.CreateString(d.DepartureId),
                "zoneId", NormalizedValue.CreateString(d.DestinationZoneId), "status", NormalizedValue.CreateString(d.Status))).ToList()));
            return expanded;
        }
        private static NormalizedValue Result(InfluenceDepartureCompletionReceipt receipt)
        {
            var result = receipt.BaseResult.Clone();
            InfluenceDestinationResolver.Set(result, "destinationFacts", Object("protocolVersion", NormalizedValue.CreateInteger(2),
                "receiptId", NormalizedValue.CreateString(receipt.ReceiptId), "count", NormalizedValue.CreateInteger(receipt.Destinations.Count)));
            var refs = receipt.Destinations.SelectMany(d => d.ResponseIds).ToList();
            InfluenceDestinationResolver.Set(result, "responseRefs", Strings(refs));
            if (!result.IsValid())
            {
                result.Properties.RemoveAll(p => p.Name == "responseRefs");
                InfluenceDestinationResolver.Set(result, "responseRefCount", NormalizedValue.CreateInteger(refs.Count));
                InfluenceDestinationResolver.Set(result, "responseRefsRequireReceipt", NormalizedValue.CreateBoolean(true));
            }
            if (!result.IsValid()) throw new InvalidOperationException("departure_completion_summary_overflow");
            return result;
        }
        public static bool TryValidate(GameState state, EffectNodeRuntimeState node, out string reason)
        {
            reason = "departure_completion_receipt_invalid";
            var r = node.DepartureCompletionReceipt; var reference = Value(node.NormalizedResult, "destinationFacts");
            if (r == null)
            {
                if (node.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence)
                {
                    if (reference != null) return false;
                    if (node.Status == EffectNodeStatus.Completed || node.PendingOutcome == EffectPendingOutcome.Completed)
                    {
                        var actual = state.EffectRuntime.InfluenceDepartures.Where(d => d.SourceEffectId == node.EffectId).ToList();
                        if (actual.Count > 0)
                        {
                            reason = "departure_completion_receipt_missing";
                            if (node.NormalizedResult?.Kind != NormalizedValueKind.Object) return false;
                            var original = Base(node.NormalizedResult);
                            // 不能通过再删除摘要中的逐枚数组来缩小实际完成结果预算。
                            InfluenceDestinationResolver.Set(original, "removedItemIds", Strings(actual.Select(d => d.ItemId)));
                            InfluenceDestinationResolver.Set(original, "affectedItemIds", Strings(actual.Select(d => d.ItemId)));
                            InfluenceDestinationResolver.Set(original, "departureIds", Strings(actual.Select(d => d.DepartureId)));
                            if (!Expanded(original, actual).IsValid()) return false;
                        }
                    }
                }
                reason = ""; return true;
            }
            // 先保留原自动批次的目标门禁诊断，不能由附加收据遮盖真实批次/去向错误。
            if (!InfluenceDepartureProtocol.Validate(state, out reason)) return false;
            reason = "departure_completion_receipt_invalid";
            if (node.EffectTypeId != InfluenceEffectTypeIds.RemoveInfluence || node.Status != EffectNodeStatus.Completed && node.PendingOutcome != EffectPendingOutcome.Completed ||
                r.ProtocolVersion != 2 || r.ActorId != node.PlayerId || r.OwnerEffectId != node.EffectId || r.DefinitionVersion != node.DefinitionVersion ||
                r.ArgumentsDigest != ItemTransferService.MovementV4Digest(node.NormalizedArguments) || r.BaseResult?.Kind != NormalizedValueKind.Object || !r.BaseResult.IsValid() ||
                r.Destinations == null || r.Destinations.Count == 0 || r.Destinations.Any(d => d == null || d.ResponseIds == null) ||
                r.ReceiptId != StableIdFactory.Create("departure-completion", node.EffectId, Digest(r))) return false;
            var records = state.EffectRuntime.InfluenceDepartures.Where(d => d.SourceEffectId == node.EffectId).ToList();
            if (Value(r.BaseResult, "removedItemIds")?.Kind != NormalizedValueKind.Array || Value(r.BaseResult, "departureIds")?.Kind != NormalizedValueKind.Array ||
                !records.Select(d => d.DepartureId).SequenceEqual(r.Destinations.Select(d => d.DepartureId)) ||
                !Value(r.BaseResult, "removedItemIds").Items.Select(v => v.StringValue).SequenceEqual(records.Select(d => d.ItemId)) ||
                !Value(r.BaseResult, "departureIds").Items.Select(v => v.StringValue).SequenceEqual(records.Select(d => d.DepartureId)) ||
                Number(r.BaseResult, "actualRemovedCount", -1) != records.Count) return false;
            var targets = Value(node.NormalizedArguments, "targets");
            if (targets != null && !targets.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue).SequenceEqual(records.Select(d => d.ItemId))) return false;
            for (int i = 0; i < records.Count; i++)
            {
                var d = records[i]; var row = r.Destinations[i]; var item = state.ItemZones.Items.SingleOrDefault(v => v.ItemId == row.ItemId);
                var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == row.DestinationZoneId);
                if (row.ItemId != d.ItemId || row.OriginalSupplyZoneId != d.OriginalSupplyZoneId || item?.OriginalSupplyZoneId != row.OriginalSupplyZoneId ||
                    !row.ResponseIds.SequenceEqual(d.ResponseIds) || zone == null || !new[] { "returned", "stored" }.Contains(row.Status)) return false;
                if (row.Status == "returned" && row.DestinationZoneId != d.OriginalSupplyZoneId) return false;
                if (row.Status == "stored" && (zone.ParentKind != ZoneParentKind.Item || !row.ResponseIds.Any(id => state.EffectRuntime.DeferredResponses.Any(response =>
                    response.DeferredResponseId == id && response.DepartureId == d.DepartureId && response.SourceCardInstanceId == zone.ParentId)))) return false;
            }
            if (ItemTransferService.MovementV4Digest(node.NormalizedResult) != ItemTransferService.MovementV4Digest(Result(r))) return false;
            reason = ""; return true;
        }
        public static bool HasRawFields(JObject state)
        {
            foreach (var node in state["EffectRuntime"]?["EffectNodes"] as JArray ?? new JArray())
                if (node["DepartureCompletionReceipt"] is JToken raw && raw.Type != JTokenType.Null && !ItemMovementV4.RawShape(raw, typeof(InfluenceDepartureCompletionReceipt))) return false;
            return true;
        }
    }
}
