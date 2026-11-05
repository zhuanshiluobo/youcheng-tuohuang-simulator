using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.State
{
    [Serializable] public sealed class InfluenceDepartureRecord
    {
        public int ProtocolVersion = 1;
        public int OperationIndex;
        public string DepartureId = "", SourceEffectId = "", ItemId = "", CauseKind = "", OriginalSupplyZoneId = "";
        public ItemSubjectRef OriginalSubject;
        public ItemPositionRef OriginalPosition;
        public List<string> EventIds = new List<string>(), ResponseIds = new List<string>();
        public int CurrentIndex;
        public string CustodyRootId = "", CustodyZoneId = "", Status = "open", DestinationZoneId = "", BatchId = "";
    }
    [Serializable] public sealed class InfluenceDepartureBatch
    {
        public int ProtocolVersion = 1;
        public string BatchId = "", OwnerEffectId = "";
        public List<string> ProducerSteps = new List<string>(), FinishedSteps = new List<string>(), DepartureIds = new List<string>(), ResponseIds = new List<string>();
        public bool Sealed;
        public List<DepartureBatchOffer> Offers = new List<DepartureBatchOffer>();
    }
    [Serializable] public sealed class DepartureBatchOffer
    {
        public string SourceKey = "", EventId = "", SubscriptionId = "";
        public EffectSpecRuntimeState Body;
        public List<string> DepartureIds = new List<string>(), EventIds = new List<string>();
    }
    public sealed partial class EffectRuntimeState
    {
        public List<InfluenceDepartureRecord> InfluenceDepartures = new List<InfluenceDepartureRecord>();
        public List<InfluenceDepartureBatch> InfluenceDepartureBatches = new List<InfluenceDepartureBatch>();
    }
}

namespace YC.Domain.Effects
{
    /// <summary>记录属于真实 Remove/Replace，不创建第二种离场事件或库存。</summary>
    public static class InfluenceDepartureProtocol
    {
        public const string Version = "2.0.0";
        public const string BatchFactsVersion = "departure_batch_facts.v2";
        public static string BatchFactsDigest(string batchId, DepartureBatchOffer offer) => StableIdFactory.ComputeSha256(
            StableIdFactory.EncodeCanonicalInput(BatchFactsVersion, new[] { batchId, offer.SourceKey, offer.DepartureIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                .Concat(offer.DepartureIds).Concat(offer.EventIds).ToArray()));
        public static bool ValidBatchPayload(GameState state, DeferredResponseState response)
        {
            var batch = state.EffectRuntime.InfluenceDepartureBatches.SingleOrDefault(b => b.BatchId == response.BatchId);
            var offer = batch?.Offers.SingleOrDefault(o => o.SourceKey == response.SourceKey);
            if (offer == null || !response.SourceEventIds.SequenceEqual(offer.EventIds)) return false;
            var payload = Value(response.PendingBody[0].NormalizedArguments, "triggerPayload");
            if (Text(payload,"batchId") != batch.BatchId || Number(payload,"departureCount") != offer.DepartureIds.Count) return false;
            if (Value(payload,"batchFactsProtocolVersion") != null || Value(payload,"batchFactsDigest") != null)
                return Value(payload,"batchFactsProtocolVersion")?.Kind == NormalizedValueKind.Integer && Number(payload,"batchFactsProtocolVersion") == 2 &&
                    Text(payload,"batchFactsDigest") == BatchFactsDigest(batch.BatchId,offer) && Value(payload,"departureIds") == null && Value(payload,"sourceEventIds") == null;
            return Value(payload,"departureIds")?.Kind == NormalizedValueKind.Array && Value(payload,"sourceEventIds")?.Kind == NormalizedValueKind.Array &&
                Value(payload,"departureIds").Items.Select(v=>v.StringValue).SequenceEqual(offer.DepartureIds) &&
                Value(payload,"sourceEventIds").Items.Select(v=>v.StringValue).SequenceEqual(offer.EventIds);
        }
        // 仅检查指定真实离场的去向队列，不等待可选收益本身或不相干事件。
        public static bool DestinationsSettled(GameState state, IEnumerable<string> sourceEventIds)
        {
            foreach (string eventId in sourceEventIds)
            {
                var e = state.EffectRuntime.RuleEvents.SingleOrDefault(v => v.EventId == eventId);
                var d = e == null ? null : ForEvent(state, e);
                if (d == null || d.Status == "open" || d.ResponseIds.Any(id => state.EffectRuntime.DeferredResponses.Any(r => r.DeferredResponseId == id &&
                    (r.Status == DeferredResponseStatus.Pending || r.Status == DeferredResponseStatus.Queued)))) return false;
            }
            return true;
        }
        public static InfluenceDepartureRecord Find(GameState state, string departureId) => state.EffectRuntime.InfluenceDepartures.SingleOrDefault(d => d.DepartureId == departureId);
        public static InfluenceDepartureRecord ForEvent(GameState state, RuleEvent e) => Find(state, Text(e.Payload, "departureId"));
        public static bool IsWithin(GameState state, string nodeId, string root)
        {
            var seen = new HashSet<string>();
            while (!string.IsNullOrEmpty(nodeId) && seen.Add(nodeId))
            { if (nodeId == root) return true; nodeId = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == nodeId)?.ParentEffectId; }
            return false;
        }
        public static InfluenceDepartureRecord Current(GameState state, string item, string node)
            => state.EffectRuntime.InfluenceDepartures.SingleOrDefault(d => d.ItemId == item && d.Status == "open" && IsWithin(state, node, d.CustodyRootId));
        public static DeferredResponseState Response(GameState state, string node)
            => state.EffectRuntime.DeferredResponses.SingleOrDefault(r => r.ProtocolVersion == 1 && r.Status == DeferredResponseStatus.Pending && IsWithin(state, node, r.ResponseRootEffectId));
        public static bool CanTransfer(GameState state, ItemTransferRequest request)
        {
            if (request?.ItemIds?.Count != 1) return false;
            var d = state.EffectRuntime.InfluenceDepartures.SingleOrDefault(v => v.ItemId == request.ItemIds[0] && v.Status == "open" && v.CustodyZoneId == request.ExpectedSource?.ZoneId);
            if (d == null) return false;
            var r = state.EffectRuntime.DeferredResponses.SingleOrDefault(v => v.ProtocolVersion == 1 && v.Kind == "destination" && v.Status == DeferredResponseStatus.Pending && v.DepartureId == d.DepartureId && v.ResponseRootEffectId == d.CustodyRootId);
            var destination = state.ItemZones.Zones.Find(z => z.ZoneId == request.Destination?.ZoneId);
            return request.OperationProfileId == "core.cube_storage" && (r == null
                ? d.SourceEffectId == d.CustodyRootId && destination?.ZoneId == d.OriginalSupplyZoneId
                : r.AnsweringPlayerId == request.ExecutingPlayerId && destination?.ParentKind == ZoneParentKind.Item && destination.ParentId == r.SourceCardInstanceId);
        }
        public static ItemTransferResult Resolve(EffectExecutionContext context, InfluenceDepartureRecord d, string destination)
        {
            using var diagnostic=GameStateCloneService.MeasureDiagnostic("influence.depart.resolve");
            var item = context.State.ItemZones.Items.Single(i => i.ItemId == d.ItemId);
            if (d.Status != "open" || item.ZoneId != d.CustodyZoneId || !IsWithin(context.State, context.Node.EffectId, d.CustodyRootId)) throw new InvalidOperationException("departure_custody_required");
            var r = Response(context.State, context.Node.EffectId);
            if (r != null && (r.Kind != "destination" || r.DepartureId != d.DepartureId || r.AnsweringPlayerId != context.Node.PlayerId)) throw new InvalidOperationException("departure_claim_permission_denied");
            var moved = new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ItemIds = new List<string> { item.ItemId },
                ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = destination }, InsertionKind = "unordered", OperationProfileId = "core.cube_storage", RequiredCount = 1 });
            d.DestinationZoneId = destination; d.Status = r == null ? "returned" : "stored";
            return moved;
        }
        internal sealed class ValidationLookup<T> where T : class
        {
            private readonly List<T> source;
            private readonly Func<T, string> key;
            private ILookup<string, T> index;
            private bool inspected;
            internal ValidationLookup(List<T> source, Func<T, string> key) { this.source = source; this.key = key; }
            internal T FindFirst(string id)
            {
                if (source == null || source.GetType() != typeof(List<T>)) return source.Find(value => key(value) == id);
                return ForKey(id).FirstOrDefault();
            }
            internal IEnumerable<T> ForKey(string id)
            {
                if (!inspected)
                {
                    inspected = true;
                    if (source != null && source.GetType() == typeof(List<T>) && !source.Exists(value => value == null))
                        index = source.ToLookup(key, StringComparer.Ordinal);
                }
                return index == null ? source.Where(value => key(value) == id) : index[id];
            }
        }
        public static bool Validate(GameState state, out string reason)
        {
            reason = "departure_protocol_invalid";
            var runtime = state.EffectRuntime;
            if (runtime.InfluenceDepartures == null || runtime.InfluenceDepartureBatches == null) return false;
            // 只在本次同步完整校验内建索引；按键保留原顺序和重复项，Single仍拒绝重复。
            // 空/损坏输入在原首次使用位置回旧查询，保持原短路和异常先后。
            var nodes = new ValidationLookup<EffectNodeRuntimeState>(runtime.EffectNodes, n => n.EffectId);
            var items = new ValidationLookup<ItemInstanceState>(state.ItemZones?.Items, i => i.ItemId);
            var zones = new ValidationLookup<ZoneInstanceState>(state.ItemZones?.Zones, z => z.ZoneId);
            var events = new ValidationLookup<RuleEvent>(runtime.RuleEvents, e => e.EventId);
            var responses = new ValidationLookup<DeferredResponseState>(runtime.DeferredResponses, r => r.DeferredResponseId);
            var departures = new ValidationLookup<InfluenceDepartureRecord>(runtime.InfluenceDepartures, d => d.DepartureId);
            var receipts = new ValidationLookup<DispatchReceipt>(runtime.DispatchReceipts, r => r.EventId);
            ValidationLookup<ItemPositionChange> positions = null;
            bool HasPosition(string itemId, string from, string to)
            {
                if (positions == null)
                {
                    bool safe = runtime.EffectNodes != null && runtime.EffectNodes.GetType() == typeof(List<EffectNodeRuntimeState>);
                    if (safe)
                        for (int i = 0; i < runtime.EffectNodes.Count; i++)
                        {
                            var node = runtime.EffectNodes[i];
                            if (node == null || node.PositionChanges == null || node.PositionChanges.GetType() != typeof(List<ItemPositionChange>) || node.PositionChanges.Exists(p => p == null)) { safe = false; break; }
                        }
                    if (!safe)
                        return runtime.EffectNodes.Any(n => n.PositionChanges.Any(p => p.ItemId == itemId && p.From?.ZoneId == from && p.To?.ZoneId == to));
                    positions = new ValidationLookup<ItemPositionChange>(runtime.EffectNodes.SelectMany(n => n.PositionChanges).ToList(), p => p.ItemId);
                }
                return positions.ForKey(itemId).Any(p => p.From?.ZoneId == from && p.To?.ZoneId == to);
            }
            var ids = new HashSet<string>(); var held = new HashSet<string>();
            foreach (var d in runtime.InfluenceDepartures)
            {
                if (d == null || d.ProtocolVersion != 1 || d.OperationIndex < 0 || d.DepartureId != StableIdFactory.Create("departure", d.SourceEffectId, d.OperationIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)) || !ids.Add(d.DepartureId) || d.OriginalSubject == null || d.OriginalPosition == null ||
                    d.ResponseIds == null || d.EventIds == null || d.CurrentIndex < 0 || d.CurrentIndex > d.ResponseIds.Count ||
                    !new[] { "open", "stored", "returned" }.Contains(d.Status) || !new[] { "remove", "replace" }.Contains(d.CauseKind)) return false;
                var op = nodes.FindFirst(d.SourceEffectId);
                var item = items.FindFirst(d.ItemId);
                if (op == null || item == null || !new[] { InfluenceEffectTypeIds.RemoveInfluence, InfluenceEffectTypeIds.ReplaceInfluence }.Contains(op.EffectTypeId) ||
                    item.OriginalSupplyZoneId != d.OriginalSupplyZoneId || !zones.ForKey(d.OriginalSupplyZoneId).Any() ||
                    !op.PositionChanges.Any(p => p.ItemId == d.ItemId && p.From?.ZoneId == d.OriginalPosition.ZoneId && p.From?.SlotId == d.OriginalPosition.SlotId) ||
                    !d.EventIds.Any(id => events.ForKey(id).Any(e => e.SourceEffectId == op.EffectId && e.EventType == InfluenceEventTypeIds.InfluenceRemoved && Text(e.Payload, "departureId") == d.DepartureId)) ||
                    d.EventIds.Distinct().Count() != d.EventIds.Count || d.EventIds.Any(id => !events.ForKey(id).Any(e => e.SourceEffectId == d.SourceEffectId && Text(e.Payload, "departureId") == d.DepartureId && Number(e.Payload, "eventProtocolVersion") == 2)) ||
                    d.ResponseIds.Distinct().Count() != d.ResponseIds.Count || d.ResponseIds.Any(id => !responses.ForKey(id).Any(r => r.DepartureId == d.DepartureId))) return false;
                if (d.Status == "open" && (!held.Add(d.ItemId) || item.ZoneId != d.CustodyZoneId || !zones.ForKey(item.ZoneId).Any(z => z.OwnerEffectId == d.CustodyRootId && z.LocalZoneId == "removed_influence") || !string.IsNullOrEmpty(d.DestinationZoneId))) return false;
                if (d.Status != "open" && (string.IsNullOrEmpty(d.DestinationZoneId) || !HasPosition(d.ItemId, d.CustodyZoneId, d.DestinationZoneId))) return false;
            }
            foreach (var b in runtime.InfluenceDepartureBatches)
            {
                if (b == null || b.ProtocolVersion != 1 || b.ProducerSteps == null || b.FinishedSteps == null || b.DepartureIds == null || b.ResponseIds == null || b.Offers == null ||
                    b.BatchId != StableIdFactory.Create("departure_batch", b.OwnerEffectId) || b.ProducerSteps.Distinct().Count() != b.ProducerSteps.Count ||
                    b.FinishedSteps.Distinct().Count() != b.FinishedSteps.Count || b.FinishedSteps.Any(id => !b.ProducerSteps.Contains(id)) ||
                    b.Sealed && b.ProducerSteps.Count != b.FinishedSteps.Count || b.DepartureIds.Distinct().Count() != b.DepartureIds.Count ||
                    b.DepartureIds.Any(id => !departures.ForKey(id).Any(d => d.BatchId == b.BatchId)) ||
                    b.ResponseIds.Any(id => !responses.ForKey(id).Any(r => r.BatchId == b.BatchId)) ||
                    b.Offers.Any(o => o == null || o.Body == null || o.EventIds == null || o.DepartureIds == null || o.DepartureIds.Count == 0 || o.DepartureIds.Count != o.EventIds.Count || o.DepartureIds.Distinct().Count() != o.DepartureIds.Count ||
                        o.DepartureIds.Any(id => !b.DepartureIds.Contains(id)) || o.EventIds.Any(id => !events.ForKey(id).Any(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved && o.DepartureIds.Contains(Text(e.Payload, "departureId")))))) return false;
                if (!b.DepartureIds.SequenceEqual(runtime.InfluenceDepartures.Where(d => d.BatchId == b.BatchId).Select(d => d.DepartureId)) ||
                    !b.ResponseIds.SequenceEqual(runtime.DeferredResponses.Where(r => r.BatchId == b.BatchId).Select(r => r.DeferredResponseId))) return false;
                foreach (var offer in b.Offers)
                {
                    if (offer.EventId != offer.EventIds[0] || !receipts.ForKey(offer.EventId).Any(r => r.SubscriptionId == offer.SubscriptionId)) return false;
                    for (int index = 0; index < offer.DepartureIds.Count; index++)
                    {
                        var departure = departures.ForKey(offer.DepartureIds[index]).Single();
                        if (!events.ForKey(offer.EventIds[index]).Any(e => e.SourceEffectId == departure.SourceEffectId &&
                            e.EventType == InfluenceEventTypeIds.InfluenceRemoved && Text(e.Payload, "departureId") == departure.DepartureId)) return false;
                    }
                }
            }
            reason = ""; return true;
        }
    }
}
