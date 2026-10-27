using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfluenceDepartureLuaTests
    {
        // 规则隔离：真实 Lua → 行动入口 → 逐次合法选择，不计正常独立程序或页面验收。
        private HostSessionArchiveDto B2BatchArchive(int n, bool stateAfter)
        {
            string slot = InfluenceService.GetLocationSlotId(StaticMapDefinitions.CreateThreePlayerMap().Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0).LocationId, 0);
            string script = @"return function(ctx) if ctx.eventType=='EffectCompleted' then return {} end
return {Effect.DepartureBatch({effects={Effect.Repeat({apiVersion=1,count=" + n + @",effects={
Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,influenceSource='qy006.b2',targetSlotId='" + slot + @"'}),
Effect.SelectInfluence({player=ctx.playerId,operation='remove',ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false})}})}})} end";
            Setup(batch: true, quickScript: script, markerStock: 8, initialGold: 20);
            var archive = HostRecoveryService.CreateArchive(state);
            var option = registry.QueryActions(state, 1).Single(o => o.ActionId == "qy006.quick.action");
            Assert.That(Engine().TryInvokeAbilityAction(1, option.OptionId, option.RequestRef.InteractionId, option.RequestRef.FactsDigest,
                option.VersionRef.ContentPackHash, "qy006.b2.batch", Object(), out _, out string reason), Is.True, reason);
            Pump();
            for (int i = 0; i < n; i++)
            {
                var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open" &&
                    state.EffectRuntime.EffectNodes.Single(node => node.EffectId == q.OwnerEffectId).EffectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence);
                Assert.That(request.CandidateIds.Count, Is.EqualTo(1));
                Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, Strings(request.CandidateIds), out reason), Is.True, reason);
                Pump();
            }
            B2AssertBatchFacts(state, n);
            if (stateAfter)
            {
                HostRecoveryService.AppendJournal(archive, state, pack.ContentHash);
                Assert.That(archive.Journal.Entries.Count(e => e.StateAfter != null), Is.EqualTo(1));
                Assert.That(archive.Snapshot.State.EffectRuntime.InfluenceDepartures, Is.Empty);
                Assert.That(archive.Journal.Entries.Last().StateAfter.EffectRuntime.InfluenceDepartures.Count, Is.EqualTo(n));
            }
            else archive = HostRecoveryService.CreateArchive(state);
            string json = HostRecoveryService.SerializeArchive(archive);
            artifacts.WriteLabeledText("B2合法批次原归档" + n + (stateAfter ? "日志" : "快照"), json);
            var recovered = HostRecoveryService.RecoverJson(json, registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            B2AssertBatchFacts(recovered.State, n);
            return archive;
        }
        private static void B2AssertBatchFacts(GameState current, int n)
        {
            var runtime = current.EffectRuntime;
            var batch = runtime.InfluenceDepartureBatches.Single();
            var offer = batch.Offers.Single();
            var response = runtime.DeferredResponses.Single(r => r.BatchId == batch.BatchId);
            Assert.That(batch.Sealed, Is.True);
            Assert.That(batch.DepartureIds.Count, Is.EqualTo(n));
            Assert.That(batch.DepartureIds.Distinct().Count(), Is.EqualTo(n));
            Assert.That(offer.DepartureIds, Is.EqualTo(batch.DepartureIds));
            Assert.That(offer.EventIds.Count, Is.EqualTo(n));
            Assert.That(offer.EventIds.Distinct().Count(), Is.EqualTo(n));
            Assert.That(response.SourceEventIds, Is.EqualTo(offer.EventIds));
            Assert.That(response.SourceKey, Is.EqualTo(offer.SourceKey).And.Not.Empty);
            Assert.That(batch.ResponseIds, Does.Contain(response.DeferredResponseId));
            for (int i = 0; i < n; i++)
            {
                var departure = runtime.InfluenceDepartures.Single(d => d.DepartureId == batch.DepartureIds[i]);
                var sourceEvent = runtime.RuleEvents.Single(e => e.EventId == offer.EventIds[i]);
                Assert.That(departure.BatchId, Is.EqualTo(batch.BatchId));
                Assert.That(departure.EventIds, Does.Contain(sourceEvent.EventId));
                Assert.That(sourceEvent.SourceEffectId, Is.EqualTo(departure.SourceEffectId));
                Assert.That(Text(sourceEvent.Payload, "departureId"), Is.EqualTo(departure.DepartureId));
            }
            var payload = Value(response.PendingBody.Single().NormalizedArguments, "triggerPayload");
            Assert.That(Text(payload, "batchId"), Is.EqualTo(batch.BatchId));
            Assert.That(Number(payload, "departureCount"), Is.EqualTo(n));
            if (n == 16)
            {
                Assert.That(Value(payload, "departureIds").Items.Select(v => v.StringValue), Is.EqualTo(batch.DepartureIds));
                Assert.That(Value(payload, "sourceEventIds").Items.Select(v => v.StringValue), Is.EqualTo(offer.EventIds));
                Assert.That(Value(payload, "batchFactsProtocolVersion"), Is.Null);
                Assert.That(Value(payload, "batchFactsDigest"), Is.Null);
            }
            else
            {
                Assert.That(Number(payload, "batchFactsProtocolVersion"), Is.EqualTo(2));
                Assert.That(Text(payload, "batchFactsDigest"), Is.EqualTo(InfluenceDepartureProtocol.BatchFactsDigest(batch.BatchId, offer)));
                Assert.That(Value(payload, "departureIds"), Is.Null);
                Assert.That(Value(payload, "sourceEventIds"), Is.Null);
            }
            Assert.That(payload.TryValidate(out string reason), Is.True, reason);
        }
        [TestCase(16)] [TestCase(17)]
        public void B2BatchSixteenAndSeventeenKeepFullTypedFactsAndContinueWithLegalK(int n)
        {
            var archive = B2BatchArchive(n, false);
            var recovered = HostRecoveryService.Recover(archive, registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); state = recovered.State;
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(request.CandidateIds, Is.EqualTo(Enumerable.Range(1, n).Select(i => i.ToString()).ToArray()));
            // 本例只选择当前供应和资金均允许的 k=2；n 保留 16/17，不把候选上界当成实际交付量。
            AnswerResponse("optional_effect", "2");
            AnswerResponse("optional_effect", "continue");
            Assert.That(Stored(commandCard), Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(16));
            Assert.That(state.EffectRuntime.InfluenceDepartureBatches.Single().DepartureIds.Count, Is.EqualTo(n));
            Restore("B2边界完整n与合法k完成");
            Pump();
            Assert.That(Stored(commandCard), Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(16));
        }
        // 只读取正式Host合同，不复制、改写或替换生产ArchiveSettings。
        private static HostSessionArchiveDto B2BatchReadDomainArchive(JObject raw)
        {
            var field = typeof(HostRecoveryService).GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null); Assert.That(field.IsInitOnly, Is.True);
            var settings = field.GetValue(null) as JsonSerializerSettings;
            Assert.That(settings, Is.Not.Null);
            return raw.ToObject<HostSessionArchiveDto>(JsonSerializer.Create(settings));
        }
        private void B2BatchAssertDomainControl(HostSessionArchiveDto original, string legal)
        {
            var raw = JObject.Parse(legal); var converted = B2BatchReadDomainArchive(raw);
            var convertedRaw = JObject.Parse(HostRecoveryService.SerializeArchive(converted));
            bool graphEqual = JToken.DeepEquals(raw, convertedRaw);
            var digests = new JArray();
            void StateDigest(GameState expected, GameState actual, string position)
            {
                Assert.That(actual, Is.Not.Null, position);
                digests.Add(new JObject { ["position"] = position, ["before"] = RuleStateDigestService.Compute(expected), ["after"] = RuleStateDigestService.Compute(actual) });
            }
            StateDigest(original.Snapshot.State, converted.Snapshot.State, "Snapshot.State");
            Assert.That(converted.Journal.Entries.Count, Is.EqualTo(original.Journal.Entries.Count));
            for (int i = 0; i < original.Journal.Entries.Count; i++)
            {
                var expected = original.Journal.Entries[i].StateAfter; var actual = converted.Journal.Entries[i].StateAfter;
                if (expected == null) Assert.That(actual, Is.Null, "原空StateAfter不可补状态：" + i);
                else StateDigest(expected, actual, "Journal.Entries[" + i + "].StateAfter");
            }
            var control = HostRecoveryService.Recover(converted, registry);
            artifacts.WriteLabeledText("B2批次domain同通路合法转换图", convertedRaw.ToString());
            artifacts.WriteLabeledText("B2批次domain同通路合法对照", new JObject { ["path"] = "ArchiveSettings→DTO→Recover", ["graphEqual"] = graphEqual,
                ["stateDigests"] = digests, ["Succeeded"] = control.Succeeded, ["FaultCode"] = control.FaultCode, ["Diagnostic"] = control.Diagnostic }.ToString());
            Assert.That(graphEqual, Is.True, "正式合同转换不得丢失归档字段图。");
            Assert.That(digests.All(d => (string)d["before"] == (string)d["after"]), Is.True, "快照与全部实际StateAfter规则摘要须保持。");
            Assert.That(control.Succeeded, Is.True, "同通路合法控制失败，不能记作坏档被拒：" + control.FaultCode + ":" + control.Diagnostic);
            B2AssertBatchFacts(control.State, 17);
        }
        private static JObject B2BatchRawState(JObject archive, bool stateAfter) => stateAfter
            ? (JObject)archive["Journal"]["Entries"].Last(e => e["StateAfter"] is JObject)["StateAfter"]
            : (JObject)archive["Snapshot"]["State"];
        private static JObject B2BatchRawValue(JObject value, string key) =>
            (JObject)((JArray)value["Properties"]).Single(p => (string)p["Name"] == key)["Value"];
        private static void B2BatchDrop(JObject value, string key) =>
            ((JArray)value["Properties"]).Single(p => (string)p["Name"] == key).Remove();
        private static void B2BatchAdd(JObject value, string key, NormalizedValue added)
        {
            var properties = (JArray)value["Properties"];
            properties.Add(new JObject { ["Name"] = key, ["Value"] = JObject.FromObject(added) });
            // 新增目标字段也保持归一化属性合法排序，不能借无关形状错误通过负例。
            value["Properties"] = new JArray(properties.OrderBy(p => (string)p["Name"], StringComparer.Ordinal));
        }
        private static void B2BatchSwap(JArray values)
        { JToken first = values[0].DeepClone(); values[0] = values[1].DeepClone(); values[1] = first; }
        [TestCase(false)] [TestCase(true)]
        public void B2BatchCompactRawPresenceAndTypesAreRequiredAtBothArchivePositions(bool stateAfter)
        {
            string legal = HostRecoveryService.SerializeArchive(B2BatchArchive(17, stateAfter));
            var failures = new List<string>();
            foreach (string field in new[] { "batchFactsProtocolVersion", "batchFactsDigest", "batchId", "departureCount" })
            foreach (bool missing in new[] { true, false })
            {
                var raw = JObject.Parse(legal);
                var runtime = B2BatchRawState(raw, stateAfter)["EffectRuntime"];
                var response = runtime["DeferredResponses"].Single(r => !string.IsNullOrEmpty((string)r["BatchId"]));
                var payload = B2BatchRawValue((JObject)response["PendingBody"][0]["NormalizedArguments"], "triggerPayload");
                if (missing) B2BatchDrop(payload, field);
                else
                {
                    var value = B2BatchRawValue(payload, field);
                    value["Kind"] = (int)NormalizedValueKind.Boolean; value["BooleanValue"] = true;
                }
                string label = "B2批次raw" + field + (missing ? "缺失" : "坏类型");
                artifacts.WriteLabeledText(label, raw.ToString());
                var result = HostRecoveryService.RecoverJson(raw.ToString(), registry);
                artifacts.WriteLabeledText(label + "恢复结果", JsonConvert.SerializeObject(new { result.Succeeded, result.FaultCode, result.Diagnostic }));
                if (result.Succeeded) failures.Add(label);
            }
            Assert.That(failures, Is.Empty, "下列坏档被接受：" + string.Join("、", failures));
        }
        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void B2BatchTypedIdentityOrderCountAndMixedPayloadCorruptionAreRejected(bool stateAfter, bool domain)
        {
            var original = B2BatchArchive(17, stateAfter);
            string legal = HostRecoveryService.SerializeArchive(original);
            if (domain) B2BatchAssertDomainControl(original, legal);
            var failures = new List<string>();
            foreach (string mutation in new[] { "protocol", "digest", "batchId", "count", "mixed_departures", "mixed_events",
                "typed_batch_id", "batch_missing", "batch_duplicate", "batch_order", "batch_response_missing", "batch_response_duplicate", "offer_head_event", "offer_subscription", "offer_missing", "offer_duplicate", "offer_order", "offer_source",
                "event_missing", "event_duplicate", "event_order", "response_missing", "response_duplicate", "response_order", "response_source",
                "response_id", "response_head_event", "departure_id", "departure_event_missing", "event_departure_binding" })
            {
                var raw = JObject.Parse(legal);
                var runtime = B2BatchRawState(raw, stateAfter)["EffectRuntime"];
                var batch = runtime["InfluenceDepartureBatches"].Single(); var offer = batch["Offers"].Single();
                var response = runtime["DeferredResponses"].Single(r => (string)r["BatchId"] == (string)batch["BatchId"]);
                var payload = B2BatchRawValue((JObject)response["PendingBody"][0]["NormalizedArguments"], "triggerPayload");
                switch (mutation)
                {
                    case "protocol": B2BatchRawValue(payload, "batchFactsProtocolVersion")["IntegerValue"] = 1; break;
                    case "digest": B2BatchRawValue(payload, "batchFactsDigest")["StringValue"] = "forged-digest"; break;
                    case "batchId": B2BatchRawValue(payload, "batchId")["StringValue"] = "forged-batch"; break;
                    case "count": B2BatchRawValue(payload, "departureCount")["IntegerValue"] = 16; break;
                    case "mixed_departures": B2BatchAdd(payload, "departureIds", Strings(((JArray)offer["DepartureIds"]).Values<string>())); break;
                    case "mixed_events": B2BatchAdd(payload, "sourceEventIds", Strings(((JArray)offer["EventIds"]).Values<string>())); break;
                    case "typed_batch_id": batch["BatchId"] = "forged-typed-batch"; break;
                    case "batch_response_missing": ((JArray)batch["ResponseIds"]).Clear(); break;
                    case "batch_response_duplicate": ((JArray)batch["ResponseIds"]).Add(batch["ResponseIds"][0].DeepClone()); break;
                    case "offer_head_event": offer["EventId"] = "forged-head-event"; break;
                    case "offer_subscription": offer["SubscriptionId"] = "forged-subscription"; break;
                    case "response_id": response["DeferredResponseId"] = "forged-response"; break;
                    case "response_head_event": response["SourceEventId"] = "forged-source-event"; break;
                    case "batch_missing": batch["DepartureIds"].Last.Remove(); break;
                    case "batch_duplicate": ((JArray)batch["DepartureIds"]).Add(batch["DepartureIds"][0].DeepClone()); break;
                    case "batch_order": B2BatchSwap((JArray)batch["DepartureIds"]); break;
                    case "offer_missing": offer["DepartureIds"].Last.Remove(); break;
                    case "offer_duplicate": offer["DepartureIds"][1] = offer["DepartureIds"][0].DeepClone(); break;
                    case "offer_order": B2BatchSwap((JArray)offer["DepartureIds"]); break;
                    case "offer_source": offer["SourceKey"] = "forged-source"; break;
                    case "event_missing": offer["EventIds"].Last.Remove(); break;
                    case "event_duplicate": offer["EventIds"][1] = offer["EventIds"][0].DeepClone(); break;
                    case "event_order": B2BatchSwap((JArray)offer["EventIds"]); break;
                    case "response_missing": response["SourceEventIds"].Last.Remove(); break;
                    case "response_duplicate": response["SourceEventIds"][1] = response["SourceEventIds"][0].DeepClone(); break;
                    case "response_order": B2BatchSwap((JArray)response["SourceEventIds"]); break;
                    case "response_source": response["SourceKey"] = "forged-source"; break;
                    case "departure_id": runtime["InfluenceDepartures"][0]["DepartureId"] = "forged-departure"; break;
                    case "departure_event_missing": ((JArray)runtime["InfluenceDepartures"][0]["EventIds"]).Clear(); break;
                    case "event_departure_binding":
                        var sourceEvent = runtime["RuleEvents"].Single(e => (string)e["EventId"] == (string)offer["EventIds"][0]);
                        B2BatchRawValue((JObject)sourceEvent["Payload"], "departureId")["StringValue"] = (string)offer["DepartureIds"][1]; break;
                }
                string label = "B2批次" + (domain ? "domain" : "raw") + mutation;
                artifacts.WriteLabeledText(label, raw.ToString());
                HostRecoveryResult result; string targetGate = "RecoverJson原始形状预检及恢复", targetReason = "";
                if (domain)
                {
                    var archive = B2BatchReadDomainArchive(raw);
                    Assert.That(JToken.DeepEquals(raw, JObject.Parse(HostRecoveryService.SerializeArchive(archive))), Is.True, label + "转换只能保留目标变异，不能额外丢字段。");
                    var target = stateAfter ? archive.Journal.Entries.Last(e => e.StateAfter != null).StateAfter : archive.Snapshot.State;
                    bool shapeValid = target.EffectRuntime.TryValidate(out string shapeReason);
                    if (mutation == "response_id")
                    {
                        targetGate = "EffectRuntime旁路身份结构"; targetReason = shapeReason;
                        if (shapeValid) failures.Add(mutation + "目标旁路身份门禁未拒绝");
                    }
                    else
                    {
                        if (!shapeValid) failures.Add(mutation + "被无关运行时形状错误提前拒绝：" + shapeReason);
                        targetGate = "InfluenceDepartureProtocol.Validate";
                        if (InfluenceDepartureProtocol.Validate(target, out targetReason))
                        {
                            targetGate = "TryValidateDeferredResponses";
                            if (EffectTreeExecutor.TryValidateDeferredResponses(target, registry, out targetReason))
                                failures.Add(mutation + "目标离场domain门禁未拒绝");
                        }
                    }
                    // 不调用RecoverJson坏档、不CreateArchive坏状态，直接走正式DTO的domain恢复。
                    result = HostRecoveryService.Recover(archive, registry);
                    if (!result.Succeeded && (result.FaultCode != (stateAfter ? HostRecoveryService.JournalConflict : HostRecoveryService.SnapshotInvalid) ||
                        string.IsNullOrEmpty(targetReason) || result.Diagnostic != targetReason))
                        failures.Add(mutation + "未命中目标domain门禁：" + result.FaultCode + ":" + result.Diagnostic);
                }
                else result = HostRecoveryService.RecoverJson(raw.ToString(), registry);
                artifacts.WriteLabeledText(label + "恢复结果", JsonConvert.SerializeObject(new { path = domain ? "ArchiveSettings→DTO→Recover(domain)" : "RecoverJson(raw)", targetGate, targetReason,
                    result.Succeeded, result.FaultCode, result.Diagnostic }));
                if (result.Succeeded) failures.Add(mutation);
            }
            Assert.That(failures, Is.Empty, "下列坏档被接受：" + string.Join("、", failures));
        }
    }
}
