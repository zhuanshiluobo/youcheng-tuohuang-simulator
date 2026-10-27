using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class FacilityLayerTransferTests : FacilityLayerTestFixture
    {

        // B2-P：仅通用转移/恢复协议。所有坏档先由真实转移和原AppendJournal生成合法归档再变坏。
        private sealed class QY010B2TransferCase
        {
            public GameState State;
            public string NodeId, Site;
            public JObject Archive;
        }
        private QY010B2TransferCase QY010B2LegalTransfer(string site, bool inFlight = false)
        {
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string a = Card(state, definition).ItemId, b = Card(state, definition, 1).ItemId;
            string left = FindZone(state, "facility_supply_0").ZoneId, right = FindZone(state, "facility_supply_1").ZoneId;
            if (inFlight) { Put(state, a, 0); Overlay(state, b, 0); }
            else { Transfer(state, Leg(state, a, left, "top")); Transfer(state, Leg(state, b, right, "top")); }
            state = Recover(state, "QY010B2批量边界的合法起点");
            var archive = HostRecoveryService.CreateArchive(state);
            string id;
            if (inFlight)
            {
                var cell = Cell(state, 0);
                var engine = new EffectTreeExecutor(state, Registry);
                id = engine.CreateRoot(EffectSpec.Create(ContentLifecycleEffectExecutor.RetireFacilityTypeId,
                    Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"),
                        "facilityItemId", NormalizedValue.CreateString(b), "expectedCellZoneId", NormalizedValue.CreateString(cell.CellZoneId),
                        "expectedOrderDigest", NormalizedValue.CreateString(cell.OrderDigest)), 1));
                for (int guard = 0; engine.GetNode(id).TransferBatchRequest == null && guard < 100; guard++)
                    Assert.That(engine.Advance(), Is.True, state.EffectRuntime.LastFaultMessage);
                var node = engine.GetNode(id);
                Assert.That(node.TransferBatchRequest, Is.Not.Null);
                Assert.That(new[] { EffectNodeStatus.Completed, EffectNodeStatus.Failed, EffectNodeStatus.Faulted }.Contains(node.Status), Is.False);
                Assert.That(node.FlowStage, Is.EqualTo("retiring_bound_characters"));
            }
            else id = Transfer(state, Leg(state, a, right, "top"), Leg(state, b, left, "top")).EffectId;
            if (site == "stateAfter") HostRecoveryService.AppendJournal(archive, state);
            else archive = HostRecoveryService.CreateArchive(state);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            artifacts.WriteLabeledText("QY010B2批量" + site + (inFlight ? "真实在途" : "已完成") + "合法归档", raw.ToString());
            var result = HostRecoveryService.RecoverJson(raw.ToString(), Registry);
            Assert.That(result.Succeeded, Is.True, "必须先证明合法归档可Recover：" + result.Diagnostic);
            var fixture = new QY010B2TransferCase { State = state, NodeId = id, Site = site, Archive = raw };
            Assert.That(QY010B2TransferNode(QY010B2TransferState(raw, fixture), id)["TransferBatchRequest"], Is.TypeOf<JObject>());
            QY010B2TransferConversionControl(fixture);
            return fixture;
        }
        private static JsonSerializer QY010B2HostSerializer()
        {
            var field = typeof(HostRecoveryService).GetField("ArchiveSettings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null); Assert.That(field.IsInitOnly, Is.True);
            return JsonSerializer.Create((JsonSerializerSettings)field.GetValue(null));
        }
        private void QY010B2TransferConversionControl(QY010B2TransferCase fixture)
        {
            var raw = QY010B2TransferNode(QY010B2TransferState(fixture.Archive, fixture), fixture.NodeId);
            var copy = GameStateCloneService.DeepClone(fixture.State);
            int index = copy.EffectRuntime.EffectNodes.FindIndex(n => n.EffectId == fixture.NodeId);
            copy.EffectRuntime.EffectNodes[index] = raw.ToObject<EffectNodeRuntimeState>(QY010B2HostSerializer());
            bool graphEqual = JToken.DeepEquals(raw, JObject.FromObject(copy.EffectRuntime.EffectNodes[index], QY010B2HostSerializer()));
            string before = RuleStateDigestService.Compute(fixture.State), after = RuleStateDigestService.Compute(copy);
            bool typed = ItemTransferService.TryValidateBatchReceipt(copy.EffectRuntime.EffectNodes[index], out string typedReason);
            bool domain = FacilityLayerRecovery.TryValidateState(copy, Registry, out string domainReason);
            bool context = EffectContinuationContext.TryValidate(copy.EffectRuntime, out string contextReason);
            artifacts.WriteLabeledText("V1批量同通路合法控制-" + fixture.Site,
                JsonConvert.SerializeObject(new { graphEqual, before, after, typed, typedReason, domain, domainReason, context, contextReason, converted = JObject.FromObject(copy.EffectRuntime.EffectNodes[index], QY010B2HostSerializer()) }));
            Assert.That(graphEqual, Is.True, "同通路转换必须保持完整节点图"); Assert.That(after, Is.EqualTo(before));
            Assert.That(typed, Is.True, typedReason); Assert.That(domain, Is.True, domainReason); Assert.That(context, Is.True, contextReason);
        }
        private static JObject QY010B2TransferState(JObject archive, QY010B2TransferCase fixture)
        {
            if (fixture.Site == "snapshot") return (JObject)archive["Snapshot"]["State"];
            var checkpoint = ((JArray)archive["Journal"]["Entries"]).OfType<JObject>()
                .LastOrDefault(e => e["StateAfter"] is JObject s &&
                    ((JArray)s["EffectRuntime"]["EffectNodes"]).Any(n => (string)n["EffectId"] == fixture.NodeId));
            Assert.That(checkpoint, Is.Not.Null, "StateAfter必须来自原AppendJournal且含本次真实转移节点");
            return (JObject)checkpoint["StateAfter"];
        }
        private static JObject QY010B2TransferNode(JObject state, string id) =>
            (JObject)((JArray)state["EffectRuntime"]["EffectNodes"]).Single(n => (string)n["EffectId"] == id);
        private static JObject QY010B2TransferReceipt(JObject node) =>
            (JObject)((JArray)node["ContinuationData"]["Properties"]).Single(p => (string)p["Name"] == "transferBatchReceipt")["Value"];
        private static JObject QY010B2ReceiptValue(JObject receipt, string field) =>
            (JObject)((JArray)receipt["Properties"]).Single(p => (string)p["Name"] == field)["Value"];
        private void QY010B2CollectTransferAssertion(Action action, System.Collections.Generic.List<string> failures, string label)
        {
            try { action(); }
            catch (AssertionException error)
            {
                failures.Add(label + ": " + error.Message);
                artifacts.WriteLabeledText("V1矩阵首次断言-" + label, Newtonsoft.Json.JsonConvert.SerializeObject(new { label, error.Message, error.StackTrace }));
            }
        }
        private void QY010B2RecordTransferRejection(QY010B2TransferCase fixture, JObject bad, string label, bool checkRaw, bool checkDomain)
        {
            var stateJson = QY010B2TransferState(bad, fixture); string rawReason = "", domainReason = "";
            artifacts.WriteLabeledText("QY010B2批量坏档-" + fixture.Site + "-" + label, bad.ToString());
            bool? rawValid = null, domainValid = null;
            if (checkRaw)
            {
                Assert.DoesNotThrow(() => rawValid = FacilityLayerRecovery.TryValidateRaw(stateJson, out rawReason), label + " raw不得抛异常代替拒绝");
                Assert.That(rawValid, Is.False, label + " raw不应靠字段默认值升级");
            }
            if (checkDomain)
            {
                var copy = GameStateCloneService.DeepClone(fixture.State);
                int index = copy.EffectRuntime.EffectNodes.FindIndex(n => n.EffectId == fixture.NodeId);
                copy.EffectRuntime.EffectNodes[index] = QY010B2TransferNode(stateJson, fixture.NodeId).ToObject<EffectNodeRuntimeState>(QY010B2HostSerializer());
                Assert.DoesNotThrow(() => domainValid = ItemTransferService.TryValidateBatchReceipt(copy.EffectRuntime.EffectNodes[index], out domainReason),
                    label + " typed校验不得抛异常代替拒绝");
                Assert.That(domainValid, Is.False, label + " 完整请求、摘要与实际位置证据不一致必须拒绝");
                bool? stateValid = null;
                Assert.DoesNotThrow(() => stateValid = FacilityLayerRecovery.TryValidateState(copy, Registry, out _), label);
                Assert.That(stateValid, Is.False, label + " 领域恢复必须消费同一证据");
            }
            // 不对坏对象调用CreateArchive；以下才是RecoverJson实际拒绝证据。
            var result = HostRecoveryService.RecoverJson(bad.ToString(), Registry);
            artifacts.WriteLabeledText("QY010B2批量拒绝结果-" + fixture.Site + "-" + label,
                JsonConvert.SerializeObject(new { label, fixture.Site, rawValid, rawReason, domainValid, domainReason,
                    result.Succeeded, result.FaultCode, result.Diagnostic }));
            Assert.That(result.Succeeded, Is.False, label + " RecoverJson不得接受坏档：" + result.Diagnostic);
        }

        [TestCase("snapshot")] [TestCase("stateAfter")]
        public void QY010B2TransferRawContractRejectsMissingOrWrongTypedFields(string site)
        {
            var failures = new System.Collections.Generic.List<string>();
            var fixture = QY010B2LegalTransfer(site);
            // 显式协议字段表，覆盖每一层DTO；不从生产校验器反射生成预期。
            var fields = new[] {
                "ProtocolVersion", "ExecutingPlayerId", "OperationId", "Transfers", "ExpectedOrders",
                "Transfers[0].ItemIds", "Transfers[0].ExpectedSource", "Transfers[0].ExpectedSource.ZoneId",
                "Transfers[0].ExpectedSource.SlotId", "Transfers[0].Destination", "Transfers[0].Destination.ZoneId",
                "Transfers[0].InsertionKind", "Transfers[0].DestinationSlotId", "Transfers[0].OperationProfileId",
                "Transfers[0].CarrierPolicyId", "Transfers[0].ExecutingPlayerId", "Transfers[0].RequiredCount",
                "ExpectedOrders[0].ZoneId", "ExpectedOrders[0].Digest", "ExpectedOrders[0].OrderRevision", "ExpectedOrders[0].ExpectedTopItemId" };
            {
                foreach (string field in fields)
                foreach (bool missing in new[] { true, false })
                {
                    var bad = (JObject)fixture.Archive.DeepClone();
                    var request = QY010B2TransferNode(QY010B2TransferState(bad, fixture), fixture.NodeId)["TransferBatchRequest"];
                    var value = request.SelectToken(field);
                    if (missing) ((JProperty)value.Parent).Remove();
                    else value.Replace(value.Type == JTokenType.Integer ? (JToken)new JValue("wrong-integer") :
                        value.Type == JTokenType.String ? new JArray("wrong-string") : new JValue(false));
                    QY010B2CollectTransferAssertion(() => QY010B2RecordTransferRejection(fixture, bad, field + (missing ? ".missing" : ".type"), true, false), failures, "QY010B2TransferRawContractRejectsMissingOrWrongTypedFields");
                }
            }
            Assert.That(failures, Is.Empty, "各变体保持全部断言；合法控制在聚合前独立完成");
        }

        [TestCase("snapshot")] [TestCase("stateAfter")]
        public void QY010B2TransferDomainBindsOriginalOperationOrderAndEveryPosition(string site)
        {
            var failures = new System.Collections.Generic.List<string>();
            var fixture = QY010B2LegalTransfer(site);
            var cases = new[] { "digest", "receipt-protocol", "request-protocol", "receipt-operation", "typed-operation", "typed-player", "moved-order",
                "receipt-from", "receipt-to", "actual-from", "actual-to", "duplicate-receipt-position", "duplicate-actual-position",
                "rehash-order-digest", "rehash-order-revision", "rehash-order-top", "rehash-order-missing", "rehash-order-duplicate",
                "rehash-source-slot", "rehash-destination-slot", "rehash-profile" };
            {
                foreach (string mutation in cases)
                {
                    var bad = (JObject)fixture.Archive.DeepClone();
                    var node = QY010B2TransferNode(QY010B2TransferState(bad, fixture), fixture.NodeId);
                    var request = (JObject)node["TransferBatchRequest"]; var receipt = QY010B2TransferReceipt(node);
                    var positions = (JArray)QY010B2ReceiptValue(receipt, "positionChanges")["Items"];
                    var leg = request["Transfers"][0]; var order = request["ExpectedOrders"][0];
                    switch (mutation)
                    {
                        case "digest": QY010B2ReceiptValue(receipt, "requestDigest")["StringValue"] = "yc.transfer-batch.sha256.v2:" + new string('0', 64); break;
                        case "receipt-protocol": QY010B2ReceiptValue(receipt, "protocolVersion")["IntegerValue"] = 1; break;
                        case "request-protocol": request["ProtocolVersion"] = 1; break;
                        case "receipt-operation": QY010B2ReceiptValue(receipt, "operationId")["StringValue"] = "QY010B2.wrong-operation"; break;
                        case "typed-operation": request["OperationId"] = "QY010B2.wrong-operation"; break;
                        case "typed-player": request["ExecutingPlayerId"] = 2; break;
                        case "moved-order":
                            var moved = (JArray)QY010B2ReceiptValue(receipt, "movedItemIds")["Items"];
                            moved.ReplaceAll(moved.Reverse().Select(v => v.DeepClone()).ToArray()); break;
                        case "receipt-from": QY010B2ReceiptValue((JObject)positions[0], "fromZone")["StringValue"] = "QY010B2.wrong-zone"; break;
                        case "receipt-to": QY010B2ReceiptValue((JObject)positions[0], "toZone")["StringValue"] = "QY010B2.wrong-zone"; break;
                        case "actual-from": node["PositionChanges"][0]["From"]["ZoneId"] = "QY010B2.wrong-zone"; break;
                        case "actual-to": node["PositionChanges"][0]["To"]["ZoneId"] = "QY010B2.wrong-zone"; break;
                        case "duplicate-receipt-position": positions[1] = positions[0].DeepClone(); break;
                        case "duplicate-actual-position": ((JArray)node["PositionChanges"]).Add(node["PositionChanges"][0].DeepClone()); break;
                        case "rehash-order-digest": order["Digest"] = new string('0', 64); break;
                        case "rehash-order-revision": order["OrderRevision"] = (int)order["OrderRevision"] + 1; break;
                        case "rehash-order-top": order["ExpectedTopItemId"] = "QY010B2.wrong-top"; break;
                        case "rehash-order-missing": ((JArray)request["ExpectedOrders"]).RemoveAt(0); break;
                        case "rehash-order-duplicate": ((JArray)request["ExpectedOrders"]).Add(order.DeepClone()); break;
                        case "rehash-source-slot": leg["ExpectedSource"]["SlotId"] = "QY010B2.wrong-slot"; break;
                        case "rehash-destination-slot": leg["DestinationSlotId"] = "QY010B2.wrong-slot"; break;
                        case "rehash-profile": leg["OperationProfileId"] = "core.deck_shuffle"; break;
                    }
                    if (mutation.StartsWith("rehash-", StringComparison.Ordinal))
                    {
                        // 对照节点原NormalizedArguments不动；同步重算摘要不能把另一份typed请求洗成真实历史。
                        QY010B2ReceiptValue(receipt, "requestDigest")["StringValue"] =
                            ItemTransferService.BatchRequestDigest(request.ToObject<ItemTransferBatchRequest>());
                    }
                    QY010B2CollectTransferAssertion(() => QY010B2RecordTransferRejection(fixture, bad, mutation, false, true), failures, "QY010B2TransferDomainBindsOriginalOperationOrderAndEveryPosition");
                }
            }
            Assert.That(failures, Is.Empty, "各变体保持全部断言；合法控制在聚合前独立完成");
        }

        [TestCase("snapshot", false)] [TestCase("snapshot", true)]
        [TestCase("stateAfter", false)] [TestCase("stateAfter", true)]
        public void QY010B2OldAndCurrentReceiptsWithoutOriginalRequestAreExplicitlyRefused(string site, bool inFlight)
        {
            var failures = new System.Collections.Generic.List<string>();
            var fixture = QY010B2LegalTransfer(site, inFlight);
            {
                foreach (int receiptVersion in new[] { 1, 2 })
                foreach (bool missing in new[] { true, false })
                {
                    var bad = (JObject)fixture.Archive.DeepClone();
                    var node = QY010B2TransferNode(QY010B2TransferState(bad, fixture), fixture.NodeId);
                    QY010B2ReceiptValue(QY010B2TransferReceipt(node), "protocolVersion")["IntegerValue"] = receiptVersion;
                    if (receiptVersion == 1)
                    {
                        // 旧收据布局使用完整原规范化请求串；不截断或保留v2摘要冒充旧格式。
                        var original = node["TransferBatchRequest"].ToObject<ItemTransferBatchRequest>();
                        QY010B2ReceiptValue(QY010B2TransferReceipt(node), "requestDigest")["StringValue"] =
                            ItemTransferService.BatchArguments(original).ToDeterministicString();
                    }
                    if (missing) node.Property("TransferBatchRequest").Remove(); else node["TransferBatchRequest"] = JValue.CreateNull();
                    string label = (inFlight ? "inflight" : "completed") + ".receipt" + receiptVersion + (missing ? ".original-missing" : ".original-null");
                    QY010B2CollectTransferAssertion(() => QY010B2RecordTransferRejection(fixture, bad, label, true, true), failures, "QY010B2OldAndCurrentReceiptsWithoutOriginalRequestAreExplicitlyRefused");
                }
            }
            Assert.That(failures, Is.Empty, "各变体保持全部断言；合法控制在聚合前独立完成");
        }

        [TestCase("snapshot", false)] [TestCase("snapshot", true)]
        [TestCase("stateAfter", false)] [TestCase("stateAfter", true)]
        public void QY010B2ApiOneSingleMoveWithoutBatchFieldContinuesWithoutInventingARequest(string site, bool inFlight)
        {
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string item = Card(state, definition).ItemId, source = state.ItemZones.Items.Single(i => i.ItemId == item).ZoneId;
            string target = FindZone(state, "facility_supply_0").ZoneId;
            state = Recover(state, "QY010B2旧单段合法起点"); var archive = HostRecoveryService.CreateArchive(state);
            var engine = new EffectTreeExecutor(state, Registry); string id = engine.CreateRoot(MoveSpec(1, new[] { item }, source, target, "top", 1));
            if (!inFlight) Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var original = engine.GetNode(id);
            Assert.That(Number(original.NormalizedArguments, "apiVersion"), Is.EqualTo(1));
            Assert.That(original.TransferBatchRequest, Is.Null);
            Assert.That(Value(original.ContinuationData, "transferBatchReceipt"), Is.Null);
            if (site == "stateAfter") HostRecoveryService.AppendJournal(archive, state); else archive = HostRecoveryService.CreateArchive(state);
            var raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            Assert.That(HostRecoveryService.RecoverJson(raw.ToString(), Registry).Succeeded, Is.True, "先核未损坏的真实归档");
            var fixture = new QY010B2TransferCase { State = state, NodeId = id, Site = site, Archive = raw };
            QY010B2TransferNode(QY010B2TransferState(raw, fixture), id).Property("TransferBatchRequest").Remove();
            var recovered = HostRecoveryService.RecoverJson(raw.ToString(), Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).TransferBatchRequest, Is.Null);
            engine = new EffectTreeExecutor(recovered.State, Registry);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, recovered.State.EffectRuntime.LastFaultMessage);
            var finished = engine.GetNode(id);
            Assert.That(finished.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(finished.TransferBatchRequest, Is.Null, "旧单段不得默认补成批量请求2");
            Assert.That(finished.PositionChanges.Count(c => c.ItemId == item && c.To.ZoneId == target), Is.EqualTo(1));
            Assert.That(recovered.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(target));
            var again = Recover(recovered.State, "QY010B2旧单段完成后不重转移");
            Assert.That(again.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).PositionChanges.Count(c => c.ItemId == item), Is.EqualTo(1));
            artifacts.WriteLabeledText("QY010B2旧api1无typed字段-" + site + "-" + inFlight, raw.ToString());
        }


        [Test] public void QY010B2CompletedBatchKeepsHistoricalEvidenceAfterALaterLegalTopMove()
        {
            var fixture = QY010B2LegalTransfer("snapshot");
            var original = fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == fixture.NodeId);
            string digest = ItemTransferService.BatchRequestDigest(original.TransferBatchRequest);
            string item = original.TransferBatchRequest.Transfers[0].ItemIds.Single();
            string previousDestination = original.TransferBatchRequest.Transfers[0].Destination.ZoneId;
            string deck = FindZone(fixture.State, "facility_deck").ZoneId;
            Run(fixture.State, MoveSpec(1, new[] { item }, previousDestination, deck, "bottom", 1));
            Assert.That(fixture.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(deck));
            var saved = fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == fixture.NodeId);
            Assert.That(saved.PositionChanges.Single(c => c.ItemId == item).To.ZoneId, Is.EqualTo(previousDestination));
            Assert.That(ItemTransferService.TryValidateBatchReceipt(saved, out string reason), Is.True, reason);
            var final = Recover(fixture.State, "QY010B2已完成批量之后真实再次移牌");
            saved = final.EffectRuntime.EffectNodes.Single(n => n.EffectId == fixture.NodeId);
            Assert.That(ItemTransferService.BatchRequestDigest(saved.TransferBatchRequest), Is.EqualTo(digest));
            Assert.That(final.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(deck));
            Assert.That(saved.PositionChanges.Count(c => c.ItemId == item), Is.EqualTo(1),
                "历史核验比对原请求/当次位置，不把后来合法移区误判或补回原目的地");
        }

        [TestCase("cover")] [TestCase("swap")] [TestCase("promote")]
        public void RealStableIdsKeepTheCompleteLongBatchRequestAndFixedDigestAcrossReplayAndRecovery(string operation)
        {
            var state=State(); string definition=Definition(state,d=>!d.Unique && !d.HasEntryEffect);
            string a=Card(state,definition).ItemId,b=Card(state,definition,1).ItemId;
            YC.Domain.Items.ItemTransferRequest[] legs;
            if(operation=="swap") {
                string left=FindZone(state,"facility_supply_0").ZoneId,right=FindZone(state,"facility_supply_1").ZoneId;
                Transfer(state,Leg(state,a,left,"top")); Transfer(state,Leg(state,b,right,"top"));
                legs=new[]{Leg(state,a,right,"top"),Leg(state,b,left,"top")};
            } else {
                Put(state,a,0);
                if(operation=="cover") legs=new[]{Leg(state,a,Cell(state,0).CoveredZoneId,"top"),Leg(state,b,Cell(state,0).CellZoneId)};
                else { Overlay(state,b,0); legs=new[]{Leg(state,b,FindZone(state,"retired").ZoneId),Leg(state,a,Cell(state,0).CellZoneId)}; }
            }
            var request=YC.Domain.Items.ItemTransferService.CreateBatch(state,1,"qy007.long."+operation,legs);
            string original=JsonConvert.SerializeObject(request);
            Assert.That(original.Length,Is.GreaterThan(1024),"真实StableId构成的完整请求必须超过旧单串预算，不能只用短手填ID证明");
            Assert.That(request.Transfers.SelectMany(t=>t.ItemIds).All(id=>id.StartsWith("item_") && id.Length>=69),Is.True);
            Registry.Register("qy007.long.replay",c=>{
                var service=new YC.Domain.Items.ItemTransferService(); var actual=BatchRequest(c.Node.NormalizedArguments);
                var first=service.RelocateBatch(c,actual); string after=RuleStateDigestService.Compute(c.State);
                var repeated=service.RelocateBatch(c,actual); Assert.That(repeated.MovedItemIds,Is.EqualTo(first.MovedItemIds));
                Assert.That(RuleStateDigestService.Compute(c.State),Is.EqualTo(after));
                var wrong=GameStateCloneService.DeepClone(actual); wrong.Transfers[0].InsertionKind=wrong.Transfers[0].InsertionKind=="top"?"bottom":"top";
                var error=Assert.Throws<InvalidOperationException>(()=>service.RelocateBatch(c,wrong)); Assert.That(error.Message,Is.EqualTo("transfer_batch_receipt_conflict"));
                Assert.That(RuleStateDigestService.Compute(c.State),Is.EqualTo(after));
                return EffectStepResult.Completed();
            });
            var node=Run(state,EffectSpec.Create("qy007.long.replay",YC.Domain.Items.ItemTransferService.BatchArguments(request),1));
            Assert.That(JsonConvert.SerializeObject(node.TransferBatchRequest),Is.EqualTo(original));
            var receipt=Value(node.ContinuationData,"transferBatchReceipt");
            Assert.That(Text(receipt,"requestDigest"),Does.Match("^yc\\.transfer-batch\\.sha256\\.v2:[0-9a-f]{64}$"));
            Assert.That(node.PositionChanges.Count,Is.EqualTo(2));
            Assert.That(YC.Domain.Items.ItemTransferService.TryValidateBatchReceipt(node,out var reason),Is.True,reason);
            state=Recover(state,"完整长请求"+operation+"重放后恢复");
            Assert.That(JsonConvert.SerializeObject(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId).TransferBatchRequest),Is.EqualTo(original));
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var saved=((JArray)raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"]).Single(n=>(string)n["EffectId"]==node.EffectId);
            saved["TransferBatchRequest"]["Transfers"][0]["ItemIds"][0]="wrong-item";
            var refused=HostRecoveryService.RecoverJson(raw.ToString(),Registry); Assert.That(refused.Succeeded,Is.False);
            artifacts.WriteLabeledText("长请求"+operation+"原请求损坏恢复拒绝",raw.ToString());
            Evidence(state,"长ID覆盖交换提顶完整实际位置证明"+operation);
        }

        [Test] public void ThreeLayersKeepInstanceDeclarationsAndPromoteExactlyOneWithoutBuild()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string bottom = Card(state, definition).ItemId; Put(state, bottom, 0);
            state.FindPlayer(1).DeclaredCityStyles.Add(new CityStyleDeclarationState { UsedFacilityIds = { bottom }, UsedCityBoardSlotIndexes = { 0 } });
            string middle = Card(state, definition).ItemId; Overlay(state, middle, 0);
            string top = Card(state, definition).ItemId; Overlay(state, top, 0);
            Assert.That(Cell(state, 0).AllItemIds, Is.EqualTo(new[] { top, middle, bottom }));
            Assert.That(FacilityLayerQuery.CurrentDeclaredTopSlots(state, 1), Is.Empty);
            Assert.That(FacilityLayerQuery.IsInCity(state, bottom, 1), Is.True);
            int score = state.FindPlayer(1).Score;
            var cell = Cell(state, 0);
            Run(state, EffectSpec.Create(ContentLifecycleEffectExecutor.RetireFacilityTypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"),
                    "facilityItemId", NormalizedValue.CreateString(top), "expectedCellZoneId", NormalizedValue.CreateString(cell.CellZoneId),
                    "expectedOrderDigest", NormalizedValue.CreateString(cell.OrderDigest)), 1));
            state = Recover(state, "三层回盒提顶后");
            Assert.That(Cell(state, 0).AllItemIds, Is.EqualTo(new[] { middle, bottom }));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(state.FindPlayer(1).DeclaredCityStyles[0].UsedFacilityIds, Does.Contain(bottom));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build || n.EffectTypeId == FacilityEntryEffectTypeIds.Activate), Is.False);
        }

        [Test] public void SameCityCoverAndPromotionKeepRealFacilityCharacterBinding()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string bottom = Card(state, definition).ItemId; Put(state, bottom, 0);
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
            string character = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.closure" && i.ControllerSubject.PlayerId == 1).ItemId;
            Answer(state, character);
            Registry.Register(new EffectRegistration("qy010.fixture.bind", c => CharacterLifecycleService.BindFacility(c, character, bottom)));
            Assert.That(Run(state, EffectSpec.Create("qy010.fixture.bind", playerId: 1)).Status, Is.EqualTo(EffectNodeStatus.Completed));
            string top = Card(state, definition).ItemId; Overlay(state, top, 0);
            Assert.That(state.FacilityCharacterBindings.Single(b => b.FacilityItemId == bottom).Active, Is.True);
            var cell = Cell(state, 0);
            Run(state, EffectSpec.Create(ContentLifecycleEffectExecutor.RetireFacilityTypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"),
                    "facilityItemId", NormalizedValue.CreateString(top), "expectedCellZoneId", NormalizedValue.CreateString(cell.CellZoneId),
                    "expectedOrderDigest", NormalizedValue.CreateString(cell.OrderDigest)), 1));
            Assert.That(Cell(state, 0).TopItemId, Is.EqualTo(bottom));
            Assert.That(state.FacilityCharacterBindings.Single(b => b.FacilityItemId == bottom).Active, Is.True);
        }

        [Test] public void BatchChecksAllLegsBeforeChangingAnyLocationOrOrder()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique);
            string a = Card(state, definition).ItemId; Put(state, a, 0);
            string b = Card(state, definition).ItemId;
            var cell = Cell(state, 0); var first = Leg(state, a, cell.CoveredZoneId, "top");
            var second = Leg(state, b, cell.CellZoneId);
            var batch = ItemTransferService.CreateBatch(state, 1, "invalid-second-leg", first, second);
            second.ExpectedSource.ZoneId = cell.CellZoneId;
            string before = RuleStateDigestService.Compute(state);
            Assert.That(new ItemTransferService().ValidateBatch(state, batch).IsValid, Is.False);
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            Assert.That(Cell(state, 0).AllItemIds, Is.EqualTo(new[] { a }));
        }

        [Test] public void SupplyTopBottomAndSwapAreOrderedAndDoNotRefillIntermediateVacancy()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique);
            string a = Card(state, definition).ItemId, b = Card(state, definition, 1).ItemId, c = Card(state, definition, 2).ItemId;
            string left = FindZone(state, "facility_supply_0").ZoneId, right = FindZone(state, "facility_supply_1").ZoneId;
            Transfer(state, Leg(state, a, left, "bottom")); Transfer(state, Leg(state, b, left, "bottom")); Transfer(state, Leg(state, c, right, "top"));
            Assert.That(FacilityLayerQuery.ResolveSupply(state, left).ItemIds, Is.EqualTo(new[] { a, b }));
            Assert.That(new ItemTransferService().Validate(state, Leg(state, b, FindZone(state, "facility_deck").ZoneId, "bottom")).IsValid, Is.False);
            Transfer(state, Leg(state, a, right, "top"), Leg(state, c, left, "top"));
            Assert.That(FacilityLayerQuery.ResolveSupply(state, left).ItemIds, Is.EqualTo(new[] { c, b }));
            Assert.That(FacilityLayerQuery.ResolveSupply(state, right).ItemIds, Is.EqualTo(new[] { a }));
            state = Recover(state, "供应成对交换后");
            Run(state, MoveSpec(1, new[] { c }, left, FindZone(state, "facility_deck").ZoneId, "bottom", 1));
            Assert.That(FacilityLayerQuery.ResolveSupply(state, left).ItemIds, Is.EqualTo(new[] { b }));
            var engine = new EffectTreeExecutor(state, Registry); engine.CreateRoot(RefillSpec(1, left));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True); // 占用槽明确拒绝，原层序不得改变。
            Assert.That(FacilityLayerQuery.ResolveSupply(state, left).ItemIds, Is.EqualTo(new[] { b }));
        }

        [Test] public void BuildFromSupplyRevealsLowerAndOnlyRefillsActuallyEmptySlot()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string a = Card(state, definition).ItemId, b = Card(state, definition, 1).ItemId;
            string supply = FindZone(state, "facility_supply_0").ZoneId;
            Transfer(state, Leg(state, a, supply, "top")); Transfer(state, Leg(state, b, supply, "bottom"));
            var first = Run(state, FacilityBuildEffectSpecFactory.Build(1, a, 0, "free", false, "qy010.supply", false, supply));
            Assert.That(first.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(FacilityLayerQuery.ResolveSupply(state, supply).ItemIds, Is.EqualTo(new[] { b }));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.ParentEffectId == first.EffectId && n.EffectTypeId == RefillTypeId), Is.False);
            var second = Run(state, FacilityBuildEffectSpecFactory.Build(1, b, 1, "free", false, "qy010.supply", false, supply));
            Assert.That(second.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ParentEffectId == second.EffectId && n.EffectTypeId == RefillTypeId), Is.EqualTo(1));
            Assert.That(FacilityLayerQuery.ResolveSupply(state, supply).ItemIds.Count, Is.EqualTo(1));
            state = Recover(state, "供应真正空后只补一次");
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ParentEffectId == second.EffectId && n.EffectTypeId == RefillTypeId), Is.EqualTo(1));
        }

        [Test] public void FullSupplyRefreshReturnsEveryLayerOnceAndRefillsSixTopSlots()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique);
            string a = Card(state, definition).ItemId, b = Card(state, definition, 1).ItemId;
            string slot = FindZone(state, "facility_supply_0").ZoneId;
            Transfer(state, Leg(state, a, slot, "top")); Transfer(state, Leg(state, b, slot, "bottom"));
            var before = state.ItemZones.Items.Where(FacilityLayerQuery.IsFacility).Select(i => i.ItemId).OrderBy(x => x).ToArray();
            Run(state, RefreshSpec(1)); state = Recover(state, "全层供应刷新后");
            Assert.That(FacilityLayerQuery.Supply(state).Select(s => s.ItemIds.Count), Is.All.EqualTo(1));
            Assert.That(state.ItemZones.Items.Where(FacilityLayerQuery.IsFacility).Select(i => i.ItemId).OrderBy(x => x), Is.EqualTo(before));
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
        }

        [Test] public void PanelSelectionUsesItsOwnRequestAndRejectsChangedTopInstance()
        {
            var state = State(); string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect);
            string original = Card(state, definition).ItemId; Put(state, original, 0);
            var selection = Run(state, FacilitySelectionEffectExecutor.PanelSpec(1, true,
                ContentLifecycleEffectExecutor.RetireFacilityTypeId, true, null, "qy010.fixture.panel"));
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilityPanelSelectionRequest.InteractionType));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.False);
            var originalRequest = Open(state); int originalRevision = originalRequest.StateRevision;
            string next = Card(state, definition).ItemId; Overlay(state, next, 0);
            var engine = new EffectTreeExecutor(state, Registry); string before = RuleStateDigestService.Compute(state);
            Assert.That(engine.TrySubmitInteraction(originalRequest.GetStableInteractionId(), 1, originalRevision,
                NormalizedValue.CreateString("0"), out string reason), Is.False, "旧请求不能因顶牌变化被直接接受");
            Assert.That(reason, Is.Not.Empty); Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            Assert.That(engine.GetNode(selection.EffectId).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            var fresh = Open(state);
            Assert.That(fresh.InteractionTypeId, Is.EqualTo(FacilityPanelSelectionRequest.InteractionType));
            Assert.That(fresh.GetStableInteractionId(), Is.Not.EqualTo(originalRequest.GetStableInteractionId()));
            Assert.That(Text(Value(fresh.PromptParameters, "candidateSlots").Items.Single(), "topItemId"), Is.EqualTo(next));
            artifacts.WriteLabeledText("面板实际候选重发且旧答零副作用", JsonConvert.SerializeObject(new {
                oldRequest = originalRequest.GetStableInteractionId(), freshRequest = fresh.GetStableInteractionId(), reason, fresh.PromptParameters }));
            state = Recover(state, "面板变化重发后仍待本人确认");
            Answer(state, "0");
            var finished = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == selection.EffectId);
            Assert.That(Text(finished.NormalizedResult, "facilityItemId"), Is.EqualTo(next));
            Assert.That(FacilityLayerQuery.IsDirectTop(state, original, 1), Is.False);
        }

        [Test] public void CoveredTowerIsCountedSeparatelyAndSharedNeighbourCountsForEachTower()
        {
            var state = State(); string tower = Definition(state, d => d.EffectId == FacilityCardEffectIds.SetupCoreCommandTower);
            string ordinary = Definition(state, d => !d.Unique && d.EffectId != FacilityCardEffectIds.SetupCoreCommandTower);
            string t1 = Card(state, tower).ItemId; Put(state, t1, 0);
            string t2 = Card(state, tower).ItemId; Put(state, t2, 2);
            string neighbour = Card(state, ordinary).ItemId; Put(state, neighbour, 1);
            string covering = Card(state, ordinary).ItemId; Overlay(state, covering, 0);
            var counts = FacilityLayerQuery.ResolveTowerAdjacency(state, 1);
            Assert.That(counts.Select(t => t.TowerItemId), Is.EquivalentTo(new[] { t1, t2 }));
            Assert.That(counts.Sum(t => t.AdjacentTopItemIds.Count(id => id == neighbour)), Is.EqualTo(2));
            Assert.That(counts.All(t => t.AdjacentTopItemIds.Count <= 4), Is.True);
            Assert.That(FacilityLayerQuery.TopCityItems(state, 1), Does.Not.Contain(t1));
        }
    }
}

