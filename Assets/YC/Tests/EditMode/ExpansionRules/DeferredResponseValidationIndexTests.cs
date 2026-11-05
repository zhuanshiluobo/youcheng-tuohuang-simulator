using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离规则及原路径对照，不计正常页面或性能目标覆盖。
    public sealed class DeferredResponseValidationIndexTests
    {
        private static GameState State(out EffectRegistry registry)
        {
            registry = new EffectRegistry();
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("subscription", "InfluenceRemoved", "*", "handler", c => Array.Empty<EffectSpec>()));
            var state = new GameState(); var runtime = state.EffectRuntime;
            var r = new DeferredResponseState { ProtocolVersion = 1, Kind = "optional_effect", Status = DeferredResponseStatus.EffectCompleted,
                SourceCardInstanceId = "card", AbilityId = "ability", SourceEventId = "event", SourceOperationEffectId = "operation", SubscriptionId = "subscription",
                AnsweringPlayerId = 1, RequiresProducerCompletion = true, DefinitionVersion = "v1",
                VersionRef = new AbilityVersionRef { ExecutorVersion = InfluenceDepartureProtocol.Version },
                SourceEventIds = new List<string> { "event" }, PendingBody = new List<EffectSpecRuntimeState> { new EffectSpecRuntimeState() } };
            r.DeferredResponseId = StableIdFactory.Create("departure_response", "departure", r.SourceCardInstanceId, r.AbilityId, "");
            r.ResponseRootEffectId = StableIdFactory.Create("departure_response_root", r.DeferredResponseId);
            runtime.DeferredResponses.Add(r);
            runtime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "operation", Status = EffectNodeStatus.Completed });
            runtime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = r.ResponseRootEffectId, PlayerId = 1, Status = EffectNodeStatus.Completed });
            runtime.RuleEvents.Add(new RuleEvent { EventId = "event", SourceEffectId = "operation", EventType = InfluenceEventTypeIds.InfluenceRemoved,
                Payload = Object("departureId", NormalizedValue.CreateString("departure")) });
            runtime.DispatchReceipts.Add(new DispatchReceipt { EventId = "event", SubscriptionId = "subscription", Succeeded = true,
                AttachedEffectIds = new List<string> { r.ResponseRootEffectId }, DeferredResponseIds = new List<string> { r.DeferredResponseId } });
            return state;
        }
        private static string Result(GameState state, EffectRegistry registry, bool full, out int hits)
        {
            int calls = 0;
            using var mode = full ? EffectTreeExecutor.UseFullDeferredResponseValidationForDiagnostics() : null;
            using var timer = GameStateCloneService.BeginTimingScope((phase,ticks) => { if (phase == "deferred.validation.index") calls++; });
            try { return EffectTreeExecutor.TryValidateDeferredResponses(state,registry,out var reason) + "|" + reason; }
            catch (Exception error) { return error.GetType().FullName + "|" + error.Message; }
            finally { hits = calls; }
        }
        private static void Compare(Action<GameState> mutation, string name)
        {
            var actual = State(out var registry); mutation(actual);
            var original = State(out var referenceRegistry); mutation(original);
            Assert.That(Result(actual,registry,false,out _), Is.EqualTo(Result(original,referenceRegistry,true,out _)), name);
        }
        [Test]
        public void ValidHistoryUsesIndexWithoutMutatingInputAndRebuildsAtSameRevision()
        {
            var state = State(out var registry); var before = GameStateCloneService.DeepClone(state);
            Assert.That(Result(state,registry,false,out var hits), Is.EqualTo("True|"));
            Assert.That(hits, Is.EqualTo(1));
            Assert.That(GameStateCloneService.AreEquivalent(state,before), Is.True);
            Assert.That(Result(state,registry,true,out hits), Is.EqualTo("True|")); Assert.That(hits, Is.Zero);
            state.EffectRuntime.RuleEvents[0].SourceEffectId = "changed";
            Assert.That(Result(state,registry,false,out _), Is.EqualTo(Result(state,registry,true,out _)));
            Assert.That(Result(state,registry,false,out _), Does.StartWith("False|"));
        }
        [Test]
        public void IndexRetainsDuplicateNullShortCircuitAndMalformedHistoryOutcomes()
        {
            var mutations = new Dictionary<string,Action<GameState>> {
                { "正常", s => {} },
                { "空字符串与null键互不混淆", s => { s.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState {EffectId=null}); s.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState {EffectId=""}); s.EffectRuntime.RuleEvents.Add(new RuleEvent {EventId=null}); } },
                { "null事件键按原查询匹配", s => { var r=s.EffectRuntime.DeferredResponses[0];r.SourceEventId=null;r.SourceEventIds[0]=null;s.EffectRuntime.RuleEvents[0].EventId=null;s.EffectRuntime.DispatchReceipts[0].EventId=null; } },
                { "缺版本", s => s.EffectRuntime.DeferredResponses[0].VersionRef = null },
                { "提前拒绝时后续节点损坏", s => { s.EffectRuntime.DeferredResponses[0].VersionRef = null; s.EffectRuntime.EffectNodes = null; } },
                { "重复事件", s => s.EffectRuntime.RuleEvents.Add(GameStateCloneService.DeepClone(s.EffectRuntime.RuleEvents[0])) },
                { "重复响应根", s => s.EffectRuntime.EffectNodes.Add(GameStateCloneService.DeepClone(s.EffectRuntime.EffectNodes[1])) },
                { "重复操作先匹配", s => s.EffectRuntime.EffectNodes.Add(GameStateCloneService.DeepClone(s.EffectRuntime.EffectNodes[0])) },
                { "空事件在开头", s => s.EffectRuntime.RuleEvents.Insert(0,null) },
                { "空事件在末尾", s => s.EffectRuntime.RuleEvents.Add(null) },
                { "空节点在开头", s => s.EffectRuntime.EffectNodes.Insert(0,null) },
                { "空节点在末尾", s => s.EffectRuntime.EffectNodes.Add(null) },
                { "空收据在前", s => s.EffectRuntime.DispatchReceipts.Insert(0,null) },
                { "空收据在后", s => s.EffectRuntime.DispatchReceipts.Add(null) },
                { "缺事件列表", s => s.EffectRuntime.RuleEvents = null },
                { "缺节点列表", s => s.EffectRuntime.EffectNodes = null },
                { "缺响应", s => s.EffectRuntime.DeferredResponses = null },
                { "空响应", s => s.EffectRuntime.DeferredResponses.Insert(0,null) },
                { "事件来源不符", s => s.EffectRuntime.RuleEvents[0].SourceEffectId = "other" },
                { "重复源事件", s => s.EffectRuntime.DeferredResponses[0].SourceEventIds.Add("event") },
                { "源事件为空", s => s.EffectRuntime.DeferredResponses[0].SourceEventIds = null },
                { "缺正文", s => s.EffectRuntime.DeferredResponses[0].PendingBody = null },
                { "多份正文", s => s.EffectRuntime.DeferredResponses[0].PendingBody.Add(new EffectSpecRuntimeState()) },
                { "未知状态", s => s.EffectRuntime.DeferredResponses[0].Status = (DeferredResponseStatus)99 },
                { "空子组", s => s.EffectRuntime.EffectNodes[0].ChildSchedulingGroups.Add(null) },
                { "完成根却等待", s => s.EffectRuntime.DeferredResponses[0].Status = DeferredResponseStatus.Pending },
                { "缺处理器", s => s.EffectRuntime.DeferredResponses[0].SubscriptionId = "missing" },
                { "请求缺失", s => s.EffectRuntime.DeferredResponses[0].RequestId = "missing" },
                { "原始Protocol0", s => s.EffectRuntime.DeferredResponses[0].ProtocolVersion = 0 },
                { "空旧payload字段", s => s.EffectRuntime.RuleEvents[0].Payload.Properties.Insert(0,null) }
            };
            foreach (var pair in mutations) Compare(pair.Value,pair.Key);
        }
        private static void Batch(GameState state,bool compact)
        {
            var runtime = state.EffectRuntime; var r = runtime.DeferredResponses[0]; r.BatchId = "batch";
            var offer = new DepartureBatchOffer { EventIds = new List<string>{"event"}, DepartureIds = new List<string>{"departure"} };
            runtime.InfluenceDepartureBatches.Add(new InfluenceDepartureBatch { BatchId = "batch", Offers = new List<DepartureBatchOffer>{offer} });
            var payload = compact ? Object("batchId",NormalizedValue.CreateString("batch"),"departureCount",NormalizedValue.CreateInteger(1),
                "batchFactsProtocolVersion",NormalizedValue.CreateInteger(2),"batchFactsDigest",NormalizedValue.CreateString(InfluenceDepartureProtocol.BatchFactsDigest("batch",offer))) :
                Object("batchId",NormalizedValue.CreateString("batch"),"departureCount",NormalizedValue.CreateInteger(1),"departureIds",Strings(new[]{"departure"}),"sourceEventIds",Strings(new[]{"event"}));
            r.PendingBody[0].NormalizedArguments = Object("triggerPayload",payload);
            r.DeferredResponseId = StableIdFactory.Create("departure_response","batch",r.SourceCardInstanceId,r.AbilityId,"");
            r.ResponseRootEffectId = StableIdFactory.Create("departure_response_root",r.DeferredResponseId);
            runtime.EffectNodes[1].EffectId = r.ResponseRootEffectId;
            runtime.DispatchReceipts[0].AttachedEffectIds[0] = r.ResponseRootEffectId;
            runtime.DispatchReceipts[0].DeferredResponseIds[0] = r.DeferredResponseId;
        }
        [TestCase(false)][TestCase(true)]
        public void OriginalAndCompactBatchFactsRetainFullValidation(bool compact)
        {
            var state = State(out var registry); Batch(state,compact);
            Assert.That(Result(state,registry,false,out var hits), Is.EqualTo("True|")); Assert.That(hits,Is.EqualTo(1));
            var mutations = new Dictionary<string,Action<GameState>> {
                { "正常", s => {} },
                { "重复批次", s => s.EffectRuntime.InfluenceDepartureBatches.Add(GameStateCloneService.DeepClone(s.EffectRuntime.InfluenceDepartureBatches[0])) },
                { "重复offer", s => s.EffectRuntime.InfluenceDepartureBatches[0].Offers.Add(GameStateCloneService.DeepClone(s.EffectRuntime.InfluenceDepartureBatches[0].Offers[0])) },
                { "错事件", s => s.EffectRuntime.InfluenceDepartureBatches[0].Offers[0].EventIds[0] = "wrong" },
                { "错离场", s => s.EffectRuntime.InfluenceDepartureBatches[0].Offers[0].DepartureIds[0] = "wrong" },
                { "空offer", s => s.EffectRuntime.InfluenceDepartureBatches[0].Offers.Insert(0,null) },
                { "空正文", s => s.EffectRuntime.DeferredResponses[0].PendingBody[0] = null },
                { "损坏payload字段", s => Value(s.EffectRuntime.DeferredResponses[0].PendingBody[0].NormalizedArguments,"triggerPayload").Properties.Insert(0,null) }
            };
            foreach (var pair in mutations) Compare(s => { Batch(s,compact); pair.Value(s); }, pair.Key);
        }
        private static List<DepartureBatchOffer> Unqueued(GameState state, InfluenceDepartureBatch batch,bool full)
        {
            using var mode=full?EffectTreeExecutor.UseFullDeferredResponseValidationForDiagnostics():null;
            return (List<DepartureBatchOffer>)typeof(EffectTreeExecutor).GetMethod("FindUnqueuedDepartureOffers",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{state,batch});
        }
        [Test]
        public void OfferFilterRetainsOrderDuplicatesAndRebuildsAfterEachBatchCallback()
        {
            var state=State(out _);var batch=new InfluenceDepartureBatch {BatchId="batch"};
            var first=new DepartureBatchOffer {SourceKey="first"};var second=new DepartureBatchOffer {SourceKey="second"};
            batch.Offers.AddRange(new[]{first,second,first,new DepartureBatchOffer {SourceKey=null}});
            Assert.That(Unqueued(state,batch,false),Is.EqualTo(Unqueued(state,batch,true)));
            state.EffectRuntime.DeferredResponses.Add(new DeferredResponseState {ProtocolVersion=1,BatchId="batch",SourceKey="first"});
            Assert.That(Unqueued(state,batch,false),Is.EqualTo(new[]{second,batch.Offers[3]}));
            Assert.That(Unqueued(state,batch,false),Is.EqualTo(Unqueued(state,batch,true)));
            state.EffectRuntime.DeferredResponses[1].BatchId="other";
            Assert.That(Unqueued(state,batch,false),Is.EqualTo(batch.Offers));
            state.EffectRuntime.DeferredResponses[1].BatchId="batch";state.EffectRuntime.DeferredResponses[1].ProtocolVersion=0;
            Assert.That(Unqueued(state,batch,false),Is.EqualTo(batch.Offers));
        }
        [TestCase(false)][TestCase(true)]
        public void OfferFilterKeepsDerivedEnumerableSideEffects(bool offers)
        {
            (GameState state,InfluenceDepartureBatch batch) Build()
            {
                var state=State(out _);var batch=new InfluenceDepartureBatch {BatchId="batch",Offers=new List<DepartureBatchOffer>{new DepartureBatchOffer {SourceKey="key"}}};
                state.EffectRuntime.DeferredResponses.Add(new DeferredResponseState {ProtocolVersion=1,BatchId="batch",SourceKey="key"});
                Action action=()=>state.EffectRuntime.DeferredResponses[1].SourceKey="changed";
                if(offers)batch.Offers=new CallbackList<DepartureBatchOffer>(batch.Offers,action);
                else state.EffectRuntime.DeferredResponses=new CallbackList<DeferredResponseState>(state.EffectRuntime.DeferredResponses,action);
                return(state,batch);
            }
            var actual=Build();var reference=Build();
            Assert.That(Unqueued(actual.state,actual.batch,false).Select(o=>o.SourceKey),Is.EqualTo(Unqueued(reference.state,reference.batch,true).Select(o=>o.SourceKey)));
            Assert.That(actual.state.EffectRuntime.DeferredResponses[1].SourceKey,Is.EqualTo(reference.state.EffectRuntime.DeferredResponses[1].SourceKey));
        }
        private sealed class CallbackList<T> : List<T>, IEnumerable<T>
        {
            private readonly Action callback;
            internal CallbackList(IEnumerable<T> values,Action callback) : base(values) { this.callback = callback; }
            IEnumerator<T> IEnumerable<T>.GetEnumerator() { callback(); return base.GetEnumerator(); }
            IEnumerator IEnumerable.GetEnumerator() { callback(); return base.GetEnumerator(); }
        }
        [TestCase("source_events")][TestCase("root_events")][TestCase("child_groups")][TestCase("batch_offers")][TestCase("payload_items")]
        public void DerivedEnumerableWithSideEffectsFallsBackBeforeAnyIndexIsBuilt(string location)
        {
            void Mutate(GameState state)
            {
                var runtime = state.EffectRuntime; Action callback = () => runtime.RuleEvents[0].SourceEffectId = "callback_changed";
                if (location == "source_events") runtime.DeferredResponses[0].SourceEventIds = new CallbackList<string>(runtime.DeferredResponses[0].SourceEventIds,callback);
                if (location == "root_events") runtime.RuleEvents = new CallbackList<RuleEvent>(runtime.RuleEvents,callback);
                if (location == "child_groups") runtime.EffectNodes[0].ChildSchedulingGroups = new CallbackList<EffectChildSchedulingGroup>(runtime.EffectNodes[0].ChildSchedulingGroups,callback);
                if (location == "batch_offers") { Batch(state,false); runtime.InfluenceDepartureBatches[0].Offers = new CallbackList<DepartureBatchOffer>(runtime.InfluenceDepartureBatches[0].Offers,callback); }
                if (location == "payload_items") { Batch(state,false); var payload = Value(runtime.DeferredResponses[0].PendingBody[0].NormalizedArguments,"triggerPayload"); var value = Value(payload,"departureIds"); value.Items = new CallbackList<NormalizedValue>(value.Items,callback); }
            }
            var state = State(out var registry); Mutate(state);
            var reference = State(out var referenceRegistry); Mutate(reference);
            Assert.That(Result(state,registry,false,out var hits),Is.EqualTo(Result(reference,referenceRegistry,true,out _)));
            Assert.That(hits,Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents[0].SourceEffectId,Is.EqualTo(reference.EffectRuntime.RuleEvents[0].SourceEffectId));
        }
        [Test]
        public void PendingOptionalResponseWaitsForActualDepartureSettlementAndValidatesRequestAncestry()
        {
            void Pending(GameState state)
            {
                var runtime=state.EffectRuntime; runtime.DeferredResponses[0].Status=DeferredResponseStatus.Pending;
                runtime.EffectNodes[1].Status=EffectNodeStatus.Ready;
                runtime.InfluenceDepartures.Add(new InfluenceDepartureRecord {DepartureId="departure",Status="returned"});
                runtime.DeferredResponses[0].RequestId="request";
                runtime.InteractionRequests.Add(new InteractionRequest {RequestId="request",AnsweringPlayerId=1,OwnerEffectId="request-child"});
                runtime.EffectNodes.Add(new EffectNodeRuntimeState {EffectId="request-child",ParentEffectId=runtime.EffectNodes[1].EffectId});
            }
            var state=State(out var registry);Pending(state);
            Assert.That(Result(state,registry,false,out var hits),Is.EqualTo("True|"));Assert.That(hits,Is.EqualTo(1));
            Compare(s=>{Pending(s);s.EffectRuntime.InfluenceDepartures[0].Status="open";},"真正去向未结清");
            Compare(s=>{Pending(s);s.EffectRuntime.EffectNodes[2].ParentEffectId="wrong";},"请求归属不符");
            Compare(s=>{Pending(s);s.EffectRuntime.InfluenceDepartures.Add(GameStateCloneService.DeepClone(s.EffectRuntime.InfluenceDepartures[0]));},"重复真实离场");
        }
    }
}
