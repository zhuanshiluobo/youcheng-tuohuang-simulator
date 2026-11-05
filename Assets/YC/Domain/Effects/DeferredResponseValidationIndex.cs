using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public partial class EffectTreeExecutor
    {
        [ThreadStatic] private static bool fullDeferredResponseValidation;
        public static IDisposable UseFullDeferredResponseValidationForDiagnostics() => new FullDeferredResponseValidationScope();
        private sealed class FullDeferredResponseValidationScope : IDisposable
        {
            private readonly bool previous = fullDeferredResponseValidation;
            private bool closed;
            internal FullDeferredResponseValidationScope() { fullDeferredResponseValidation = true; }
            public void Dispose() { if (closed) return; closed = true; fullDeferredResponseValidation = previous; }
        }

        // 在每个批次实际处理前重新取当前列表；返回之后的规则回调不会复用该集合。
        // 保留原ToList的批次内候选快照、顺序和重复项，不跨回调缓存响应状态。
        private static List<DepartureBatchOffer> FindUnqueuedDepartureOffers(GameState work, InfluenceDepartureBatch batch)
        {
            var responses = work.EffectRuntime.DeferredResponses;
            if (fullDeferredResponseValidation || batch.Offers == null || batch.Offers.GetType() != typeof(List<DepartureBatchOffer>) ||
                responses == null || responses.GetType() != typeof(List<DeferredResponseState>) ||
                batch.Offers.Exists(o => o == null) || responses.Exists(r => r == null))
                return batch.Offers.Where(o => !work.EffectRuntime.DeferredResponses.Any(r => r.ProtocolVersion == 1 && r.BatchId == batch.BatchId && r.SourceKey == o.SourceKey)).ToList();
            using var sample = GameStateCloneService.MeasureDiagnostic("deferred.offers.index");
            var queuedSources = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < responses.Count; i++)
                if (responses[i].ProtocolVersion == 1 && responses[i].BatchId == batch.BatchId) queuedSources.Add(responses[i].SourceKey);
            return batch.Offers.Where(o => !queuedSources.Contains(o.SourceKey)).ToList();
        }

        // 精确List/无空项的资格由下方上下文统一核验。唯一键只存一个引用，
        // 真正发生重复时才分配额外列表；避免给每个历史节点分配Grouping和单元素数组。
        private sealed class KeyedValidationLookup<T> where T : class
        {
            private readonly List<T> source;
            private readonly Func<T,string> key;
            private Dictionary<string,T> first;
            private Dictionary<string,List<T>> duplicates;
            private T nullFirst;
            private List<T> nullDuplicates;
            internal KeyedValidationLookup(List<T> source,Func<T,string> key) { this.source=source;this.key=key; }
            private void Ensure()
            {
                if(first!=null)return;
                first=new Dictionary<string,T>(source.Count,StringComparer.Ordinal);
                for(int i=0;i<source.Count;i++)
                {
                    var value=source[i];string id=key(value);
                    if(id==null)
                    {
                        if(nullFirst==null)nullFirst=value;
                        else { if(nullDuplicates==null)nullDuplicates=new List<T>();nullDuplicates.Add(value); }
                    }
                    else if(first.ContainsKey(id))
                    {
                        if(duplicates==null)duplicates=new Dictionary<string,List<T>>(StringComparer.Ordinal);
                        if(!duplicates.TryGetValue(id,out var values)){values=new List<T>();duplicates.Add(id,values);}values.Add(value);
                    }
                    else first.Add(id,value);
                }
            }
            internal T FindFirst(string id)
            {
                Ensure();if(id==null)return nullFirst;
                return first.TryGetValue(id,out var value)?value:null;
            }
            internal IEnumerable<T> ForKey(string id)
            {
                var value=FindFirst(id);if(value==null)yield break;
                yield return value;
                List<T> values=null;
                if(id==null)values=nullDuplicates;
                else if(duplicates!=null)duplicates.TryGetValue(id,out values);
                if(values!=null)for(int i=0;i<values.Count;i++)yield return values[i];
            }
        }

        // 仅存活于一次无外部回调的同步校验。Protocol0和任何可自定义枚举的集合整段走原路径。
        // 只索引身份，不缓存合法性；保留同键顺序、重复项及Find/Single/Any各自的语义。
        private sealed class DeferredValidationIndex
        {
            internal readonly KeyedValidationLookup<EffectNodeRuntimeState> Nodes;
            internal readonly KeyedValidationLookup<MainlineNodeRuntimeState> MainNodes;
            internal readonly KeyedValidationLookup<RuleEvent> Events;
            internal readonly KeyedValidationLookup<DispatchReceipt> Receipts;
            internal readonly KeyedValidationLookup<DeferredResponseState> Responses;
            internal readonly KeyedValidationLookup<InfluenceDepartureRecord> Departures;
            internal readonly KeyedValidationLookup<InfluenceDepartureBatch> Batches;
            internal readonly KeyedValidationLookup<InteractionRequest> Requests;

            private DeferredValidationIndex(EffectRuntimeState runtime)
            {
                Nodes = new KeyedValidationLookup<EffectNodeRuntimeState>(runtime.EffectNodes, n => n.EffectId);
                MainNodes = new KeyedValidationLookup<MainlineNodeRuntimeState>(runtime.MainNodes, m => m.ExecutionEffectId);
                Events = new KeyedValidationLookup<RuleEvent>(runtime.RuleEvents, e => e.EventId);
                Receipts = new KeyedValidationLookup<DispatchReceipt>(runtime.DispatchReceipts, r => r.EventId);
                Responses = new KeyedValidationLookup<DeferredResponseState>(runtime.DeferredResponses, r => r.DeferredResponseId);
                Departures = new KeyedValidationLookup<InfluenceDepartureRecord>(runtime.InfluenceDepartures, d => d.DepartureId);
                Batches = new KeyedValidationLookup<InfluenceDepartureBatch>(runtime.InfluenceDepartureBatches, b => b.BatchId);
                Requests = new KeyedValidationLookup<InteractionRequest>(runtime.InteractionRequests, q => q.RequestId);
            }
            private static bool Plain<T>(List<T> values) => values != null && values.GetType() == typeof(List<T>);
            private static bool PlainObjects<T>(List<T> values) where T : class
            {
                if (!Plain(values)) return false;
                for (int i = 0; i < values.Count; i++) if (values[i] == null) return false;
                return true;
            }
            // 预检只读字段/基类索引器，不运行Value或枚举，损坏输入留给原首次使用位置处理。
            private static bool TryProperty(NormalizedValue value, string name, out NormalizedValue result)
            {
                result = null;
                var fields = value?.Properties;
                if (fields == null) return true;
                for (int i = 0; i < fields.Count; i++)
                {
                    if (fields[i] == null) return false;
                    if (fields[i].Name == name) { result = fields[i].Value; return true; }
                }
                return true;
            }
            private static bool PlainBatchPayload(DeferredResponseState r)
            {
                if (string.IsNullOrEmpty(r.BatchId)) return true;
                if (r.PendingBody == null || r.PendingBody.Count == 0 || r.PendingBody[0] == null ||
                    !TryProperty(r.PendingBody[0].NormalizedArguments, "triggerPayload", out var payload) ||
                    !TryProperty(payload, "departureIds", out var departures) ||
                    !TryProperty(payload, "sourceEventIds", out var events)) return false;
                return (departures?.Items == null || Plain(departures.Items)) && (events?.Items == null || Plain(events.Items));
            }
            internal static DeferredValidationIndex TryCreate(GameState state)
            {
                if (fullDeferredResponseValidation) return null;
                var runtime = state.EffectRuntime;
                if (!PlainObjects(runtime.DeferredResponses) || runtime.DeferredResponses.Count == 0) return null;
                for (int i = 0; i < runtime.DeferredResponses.Count; i++)
                {
                    var r = runtime.DeferredResponses[i];
                    if (r.ProtocolVersion != 1 || !Plain(r.SourceEventIds) || !PlainBatchPayload(r)) return null;
                }
                if (!PlainObjects(runtime.EffectNodes) || !PlainObjects(runtime.MainNodes) || !PlainObjects(runtime.RuleEvents) ||
                    !PlainObjects(runtime.DispatchReceipts) || !PlainObjects(runtime.InfluenceDepartures) ||
                    !PlainObjects(runtime.InfluenceDepartureBatches) || !PlainObjects(runtime.InteractionRequests)) return null;
                for (int i = 0; i < runtime.EffectNodes.Count; i++)
                {
                    var groups = runtime.EffectNodes[i].ChildSchedulingGroups;
                    if (!PlainObjects(groups)) return null;
                    for (int j = 0; j < groups.Count; j++) if (!Plain(groups[j].ChildEffectIds)) return null;
                }
                for (int i = 0; i < runtime.InfluenceDepartures.Count; i++)
                    if (!Plain(runtime.InfluenceDepartures[i].ResponseIds)) return null;
                for (int i = 0; i < runtime.InfluenceDepartureBatches.Count; i++)
                {
                    var offers = runtime.InfluenceDepartureBatches[i].Offers;
                    if (!PlainObjects(offers)) return null;
                    for (int j = 0; j < offers.Count; j++)
                        if (!Plain(offers[j].EventIds) || !Plain(offers[j].DepartureIds)) return null;
                }
                return new DeferredValidationIndex(runtime);
            }
            internal bool ValidBatchPayload(DeferredResponseState response)
            {
                var batch = Batches.ForKey(response.BatchId).SingleOrDefault(_ => true);
                var offer = batch?.Offers.SingleOrDefault(o => o.SourceKey == response.SourceKey);
                if (offer == null || !response.SourceEventIds.SequenceEqual(offer.EventIds)) return false;
                var payload = Value(response.PendingBody[0].NormalizedArguments, "triggerPayload");
                if (Text(payload,"batchId") != batch.BatchId || Number(payload,"departureCount") != offer.DepartureIds.Count) return false;
                if (Value(payload,"batchFactsProtocolVersion") != null || Value(payload,"batchFactsDigest") != null)
                    return Value(payload,"batchFactsProtocolVersion")?.Kind == NormalizedValueKind.Integer && Number(payload,"batchFactsProtocolVersion") == 2 &&
                        Text(payload,"batchFactsDigest") == InfluenceDepartureProtocol.BatchFactsDigest(batch.BatchId,offer) && Value(payload,"departureIds") == null && Value(payload,"sourceEventIds") == null;
                return Value(payload,"departureIds")?.Kind == NormalizedValueKind.Array && Value(payload,"sourceEventIds")?.Kind == NormalizedValueKind.Array &&
                    Value(payload,"departureIds").Items.Select(v=>v.StringValue).SequenceEqual(offer.DepartureIds) &&
                    Value(payload,"sourceEventIds").Items.Select(v=>v.StringValue).SequenceEqual(offer.EventIds);
            }
            internal bool DestinationsSettled(IEnumerable<string> sourceEventIds)
            {
                foreach (string id in sourceEventIds)
                {
                    var e = Events.ForKey(id).SingleOrDefault(_ => true);
                    var d = e == null ? null : Departures.ForKey(Text(e.Payload, "departureId")).SingleOrDefault(_ => true);
                    if (d == null || d.Status == "open" || d.ResponseIds.Any(responseId => Responses.ForKey(responseId).Any(r =>
                        r.Status == DeferredResponseStatus.Pending || r.Status == DeferredResponseStatus.Queued))) return false;
                }
                return true;
            }
            internal bool IsWithin(string nodeId, string root)
            {
                var seen = new HashSet<string>();
                while (!string.IsNullOrEmpty(nodeId) && seen.Add(nodeId))
                { if (nodeId == root) return true; nodeId = Nodes.FindFirst(nodeId)?.ParentEffectId; }
                return false;
            }
        }
    }
}
