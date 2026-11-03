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
        // 规则隔离：真实16枚自动批次；compact仅对同一真实事实换用协议已支持的编码，不计生产17枚或正常验收。
        private HostSessionArchiveDto B2BatchArchive(int n, bool stateAfter, bool compact = false)
        {
            string slot = InfluenceService.GetLocationSlotId(StaticMapDefinitions.CreateThreePlayerMap().Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0).LocationId, 0);
            Setup(batch: true, markerStock: Math.Max(8, n), initialGold: 20);
            var archive = HostRecoveryService.CreateArchive(state);
            Run(DepartureProduction(n, slot, "qy006.b2"));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" && state.EffectRuntime.EffectNodes.Single(node => node.EffectId == q.OwnerEffectId).EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence), Is.False);
            if (compact)
            {
                var batch = state.EffectRuntime.InfluenceDepartureBatches.Single(); var offer = batch.Offers.Single();
                var response = state.EffectRuntime.DeferredResponses.Single(r => r.BatchId == batch.BatchId);
                var payload = Value(response.PendingBody.Single().NormalizedArguments, "triggerPayload");
                payload.Properties.RemoveAll(p => p.Name == "departureIds" || p.Name == "sourceEventIds");
                payload.Properties.Add(new NormalizedValueEntry { Name = "batchFactsProtocolVersion", Value = NormalizedValue.CreateInteger(2) });
                payload.Properties.Add(new NormalizedValueEntry { Name = "batchFactsDigest", Value = NormalizedValue.CreateString(InfluenceDepartureProtocol.BatchFactsDigest(batch.BatchId, offer)) });
                payload.Properties.Sort((a,b) => StringComparer.Ordinal.Compare(a.Name,b.Name));
                Assert.That(InfluenceDepartureProtocol.ValidBatchPayload(state, response), Is.True);
                artifacts.WriteLabeledText("B2紧凑编码隔离说明", "当前基础操作上限16，不构造17枚生产范围；只把同一真实16枚事实由内联转为合同已支持的compact2编码，实际物品/事件/收据/分组均不变。不计现行生产17覆盖。");
            }
            B2AssertBatchFacts(state, n, compact);
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
            B2AssertBatchFacts(recovered.State, n, compact);
            return archive;
        }
        private static void B2AssertBatchFacts(GameState current, int n, bool compact = false)
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
            if (!compact)
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
        [TestCase(false)] [TestCase(true)]
        public void B2AutomaticSixteenKeepsFactsWithInlineOrCompactEncodingAndContinuesWithLegalK(bool compact)
        {
            const int n = 16; var archive = B2BatchArchive(n, false, compact);
            var recovered = HostRecoveryService.Recover(archive, registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); state = recovered.State;
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(request.CandidateIds, Is.EqualTo(Enumerable.Range(1, n).Select(i => i.ToString()).ToArray()));
            // 本例只选择当前供应和资金均允许的k=2；真实n=16，内联/compact编码不改变生产范围。
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
            B2AssertBatchFacts(control.State, 16, true);
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
            string legal = HostRecoveryService.SerializeArchive(B2BatchArchive(16, stateAfter, true));
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
            var original = B2BatchArchive(16, stateAfter, true);
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
                    case "count": B2BatchRawValue(payload, "departureCount")["IntegerValue"] = 15; break;
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
        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void AutomaticDepartureCompletionCannotOmitBothReceiptAndReference(bool stateAfter, bool domain)
        {
            var original = B2BatchArchive(16, stateAfter, true); string legal = HostRecoveryService.SerializeArchive(original);
            if (domain) B2BatchAssertDomainControl(original, legal);
            var raw = JObject.Parse(legal); var targetState = B2BatchRawState(raw, stateAfter);
            var targetNode = targetState["EffectRuntime"]["EffectNodes"].Single(n => n["DepartureCompletionReceipt"]?.Type == JTokenType.Object);
            targetNode["DepartureCompletionReceipt"] = JValue.CreateNull(); B2BatchDrop((JObject)targetNode["NormalizedResult"], "destinationFacts");
            string bad = raw.ToString(); var converted = B2BatchReadDomainArchive(raw);
            var current = stateAfter ? converted.Journal.Entries.Last(e => e.StateAfter != null).StateAfter : converted.Snapshot.State;
            var owner = current.EffectRuntime.EffectNodes.Single(n => n.EffectId == (string)targetNode["EffectId"]);
            Assert.That(current.EffectRuntime.TryValidate(out string shapeReason), Is.True, shapeReason);
            Assert.That(InfluenceDepartureProtocol.Validate(current, out string protocolReason), Is.True, protocolReason);
            bool gateAccepted = InfluenceDepartureCompletion.TryValidate(current, owner, out string reason);
            var result = domain ? HostRecoveryService.Recover(converted, registry) : HostRecoveryService.RecoverJson(bad, registry);
            artifacts.WriteLabeledText("完成收据和引用同时删除坏档_" + stateAfter + "_" + domain, bad);
            artifacts.WriteLabeledText("同时删除的恢复结果", JsonConvert.SerializeObject(new { gateAccepted, reason, result.Succeeded, result.FaultCode, result.Diagnostic }));
            Assert.That(gateAccepted, Is.False, "结果删去引用不能掩盖实际完整去向超预算所需收据");
            Assert.That(reason, Is.EqualTo("departure_completion_receipt_missing"));
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Is.EqualTo(reason));
            Assert.That(raw.ToString(), Is.EqualTo(bad)); Assert.That(HostRecoveryService.RecoverJson(legal, registry).Succeeded, Is.True);
        }
        [TestCase("receipt")][TestCase("summary")][TestCase("missing_protocol")][TestCase("missing_responses")]
        [TestCase("rehash_supply")][TestCase("rehash_item")]
        public void AutomaticDepartureCompletionKeepsAllDestinationsAndRejectsChangedReceipt(string fault)
        {
            var archive = B2BatchArchive(16, false); string clean = HostRecoveryService.SerializeArchive(archive);
            var raw = JObject.Parse(clean); var runtime = raw["Snapshot"]["State"]["EffectRuntime"];
            var node = runtime["EffectNodes"].Single(n => n["DepartureCompletionReceipt"]?.Type == JTokenType.Object);
            var receipt = (JObject)node["DepartureCompletionReceipt"];
            Assert.That(((JArray)receipt["Destinations"]).Count, Is.EqualTo(16));
            Assert.That(((JArray)receipt["Destinations"]).All(row => (string)row["Status"] == "returned"), Is.True);
            Assert.That(B2BatchRawValue((JObject)node["NormalizedResult"], "destinationFacts"), Is.Not.Null);
            if (fault == "receipt") receipt["ReceiptId"] = "not-the-receipt";
            if (fault == "summary") B2BatchRawValue((JObject)node["NormalizedResult"], "actualRemovedCount")["IntegerValue"] = 15;
            if (fault == "missing_protocol") receipt.Property("ProtocolVersion").Remove();
            if (fault == "missing_responses") ((JObject)receipt["Destinations"][0]).Property("ResponseIds").Remove();
            if (fault.StartsWith("rehash", StringComparison.Ordinal))
            {
                if (fault == "rehash_supply") receipt["Destinations"][0]["OriginalSupplyZoneId"] = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == 2).ZoneId;
                else receipt["Destinations"][0]["ItemId"] = receipt["Destinations"][1]["ItemId"].DeepClone();
                var typed = B2BatchReadDomainArchive(raw); var target = typed.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.DepartureCompletionReceipt != null);
                var digest = typeof(InfluenceDepartureCompletion).GetMethod("Digest", BindingFlags.NonPublic | BindingFlags.Static);
                target.DepartureCompletionReceipt.ReceiptId = StableIdFactory.Create("departure-completion", target.EffectId, (string)digest.Invoke(null, new object[] { target.DepartureCompletionReceipt }));
                Value(Value(target.NormalizedResult, "destinationFacts"), "receiptId").StringValue = target.DepartureCompletionReceipt.ReceiptId;
                Assert.That(InfluenceDepartureCompletion.TryValidate(typed.Snapshot.State, target, out _), Is.False, "重算摘要不能伪造真实原供应或实例");
                raw = JObject.Parse(HostRecoveryService.SerializeArchive(typed));
            }
            string bad = raw.ToString(); Assert.That(HostRecoveryService.RecoverJson(bad, registry).Succeeded, Is.False); Assert.That(raw.ToString(), Is.EqualTo(bad));
            Assert.That(HostRecoveryService.RecoverJson(clean, registry).Succeeded, Is.True);
            artifacts.WriteLabeledText("自动离场完成收据坏档_" + fault, bad);
        }
    }
}
