using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class CardMarkerContractTests
    {
        private bool B2CompileMarkerLua(string effects, out List<EffectSpec> specs, out string diagnostic)
        {
            string source = "return function(ctx) return {" + effects + "} end";
            artifacts.WriteLabeledText("B2标记Lua输入", source);
            var script = new LuaScriptDefinition("qy006.b2.markers", "qy006.b2.markers", "qy006.b2.markers", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var sourceRef = registry.QuerySources(state, 1).Single(s => s.AbilityId == "qy005.allowed");
            var context = new LuaInvocationContext("qy006.b2.markers", "ContentAbilityInvoked", "p1", state.EffectRuntime.StateRevision, "1.0.0", 2, 0, 0)
                { SourceFacts = SourceFactsLuaCatalog.Create(state, registry, sourceRef, includeCallableActions: false) };
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, context);
            specs = null; diagnostic = invocation.Diagnostic;
            if (!invocation.IsSuccess) return false;
            bool compiled = new LuaEffectSpecCompiler(presentationPack: pack).TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out specs, out var fault);
            diagnostic = fault?.Diagnostic ?? "";
            return compiled;
        }
        private EffectSpec[] B2MarkerSpecs(string effects)
        {
            Assert.That(B2CompileMarkerLua(effects, out var specs, out string diagnostic), Is.True, diagnostic);
            return specs.ToArray();
        }
        // 只读取正式Host合同，不复制、改写或替换生产ArchiveSettings。
        private static HostSessionArchiveDto B2MarkerReadDomainArchive(JObject raw)
        {
            var field = typeof(HostRecoveryService).GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null); Assert.That(field.IsInitOnly, Is.True);
            var settings = field.GetValue(null) as JsonSerializerSettings;
            Assert.That(settings, Is.Not.Null);
            return raw.ToObject<HostSessionArchiveDto>(JsonSerializer.Create(settings));
        }
        private void B2MarkerAssertDomainControl(HostSessionArchiveDto original, string legal)
        {
            var raw = JObject.Parse(legal); var converted = B2MarkerReadDomainArchive(raw);
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
            artifacts.WriteLabeledText("B2标记domain同通路合法转换图", convertedRaw.ToString());
            artifacts.WriteLabeledText("B2标记domain同通路合法对照", new JObject { ["path"] = "ArchiveSettings→DTO→Recover", ["graphEqual"] = graphEqual,
                ["stateDigests"] = digests, ["Succeeded"] = control.Succeeded, ["FaultCode"] = control.FaultCode, ["Diagnostic"] = control.Diagnostic }.ToString());
            Assert.That(graphEqual, Is.True, "正式合同转换不得丢失归档字段图。");
            Assert.That(digests.All(d => (string)d["before"] == (string)d["after"]), Is.True, "快照与全部实际StateAfter规则摘要须保持。");
            Assert.That(control.Succeeded, Is.True, "同通路合法控制失败，不能记作坏档被拒：" + control.FaultCode + ":" + control.Diagnostic);
        }
        private static JObject B2MarkerRawState(JObject archive, bool stateAfter) => stateAfter
            ? (JObject)archive["Journal"]["Entries"].Last(e => e["StateAfter"] is JObject)["StateAfter"]
            : (JObject)archive["Snapshot"]["State"];
        private static JObject B2MarkerRawValue(JObject value, string key) =>
            (JObject)((JArray)value["Properties"]).Single(p => (string)p["Name"] == key)["Value"];
        private static void B2MarkerDrop(JObject value, string key) =>
            ((JArray)value["Properties"]).Single(p => (string)p["Name"] == key).Remove();
        private void B2AssertMarkerRejected(JObject raw, bool domain, string label, List<string> failures)
        {
            artifacts.WriteLabeledText(label, raw.ToString());
            HostRecoveryResult result; string targetGate = "RecoverJson原始形状预检及恢复", targetReason = "";
            if (domain)
            {
                var archive = B2MarkerReadDomainArchive(raw);
                Assert.That(JToken.DeepEquals(raw, JObject.Parse(HostRecoveryService.SerializeArchive(archive))), Is.True, label + "转换只能保留目标变异，不能额外丢字段。");
                bool stateAfter = archive.Journal.Entries.Any(e => e.StateAfter != null);
                var target = stateAfter ? archive.Journal.Entries.Last(e => e.StateAfter != null).StateAfter : archive.Snapshot.State;
                if (!target.EffectRuntime.TryValidate(out string shapeReason)) failures.Add(label + "被无关运行时形状错误提前拒绝：" + shapeReason);
                var token = target.EffectRuntime.EffectNodes.SingleOrDefault(IsTokenTransfer);
                if (token != null && !YC.Domain.Items.ItemTransferService.TryValidateTokenState(target, token, out targetReason))
                {
                    // 原生收据门禁先于内容续接门禁；缺收据也必须精确命中实际token收据拒绝。
                    targetGate = "ItemTransferService.TryValidateTokenState";
                }
                else
                {
                    targetGate = "ContentPrimitive.TryValidateContentContinuations";
                    if (ContentPrimitiveEffectExecutor.TryValidateContentContinuations(target, out targetReason))
                    {
                        failures.Add(label + "目标token/domain门禁未拒绝");
                    }
                }
                result = HostRecoveryService.Recover(archive, registry);
                if (!result.Succeeded && (result.FaultCode != (stateAfter ? HostRecoveryService.JournalConflict : HostRecoveryService.SnapshotInvalid) ||
                    string.IsNullOrEmpty(targetReason) || result.Diagnostic != targetReason))
                    failures.Add(label + "未命中目标domain门禁：" + result.FaultCode + ":" + result.Diagnostic);
            }
            else result = HostRecoveryService.RecoverJson(raw.ToString(), registry);
            artifacts.WriteLabeledText(label + "恢复结果", JsonConvert.SerializeObject(new { path = domain ? "ArchiveSettings→DTO→Recover(domain)" : "RecoverJson(raw)", targetGate, targetReason,
                result.Succeeded, result.FaultCode, result.Diagnostic }));
            if (result.Succeeded) failures.Add(label);
        }
        private HostSessionArchiveDto B2MarkerResultArchive(string operation, bool stateAfter)
        {
            State(supply: 29, initialGold: 100); Invoke(SeedStoredTokens(17));
            var ids = Stored.Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var archive = HostRecoveryService.CreateArchive(state);
            Invoke(TransferTokens(17)); Assert.That(Stored, Is.Empty);
            Assert.That(ids.All(id => state.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == state.ItemZones.Items.Single(i => i.ItemId == id).OriginalSupplyZoneId), Is.True);
            var payment = state.EffectRuntime.EffectNodes.Single(IsTokenTransfer);
            Assert.That(payment.PositionChanges.Count, Is.EqualTo(17)); Assert.That(Number(Value(payment.ContinuationData, "transferBatchReceipt"), "protocolVersion"), Is.EqualTo(3));
            Assert.That(payment.TransferBatchRequest.Transfers.SelectMany(v => v.ItemIds), Is.EqualTo(ids));
            if (stateAfter)
            {
                HostRecoveryService.AppendJournal(archive, state, pack.ContentHash);
                Assert.That(archive.Journal.Entries.Count(e => e.StateAfter != null), Is.EqualTo(1));
                Assert.That(archive.Snapshot.State.EffectRuntime.EffectNodes.Any(IsTokenTransfer), Is.False);
            }
            else archive = HostRecoveryService.CreateArchive(state);
            string legal = HostRecoveryService.SerializeArchive(archive); artifacts.WriteLabeledText("B2合法v3标记支付原归档", legal);
            var control = HostRecoveryService.RecoverJson(legal, registry); Assert.That(control.Succeeded, Is.True, control.Diagnostic);
            return archive;
        }

        [TestCase("token", false, false)] [TestCase("token", true, false)] [TestCase("token", false, true)] [TestCase("token", true, true)]
        public void B2TokenReceiptMustAgreeWithActualTypedPositionChanges(string operation, bool stateAfter, bool domain)
        {
            var original = B2MarkerResultArchive(operation, stateAfter);
            string legal = HostRecoveryService.SerializeArchive(original);
            if (domain) B2MarkerAssertDomainControl(original, legal);
            string effectType = ItemTransferEffectExecutor.TypeId;
            var failures = new List<string>();
            string[] mutations = new[] { "protocol", "requested", "moved", "changed", "receipt", "receipt_operation", "receipt_count", "ids_missing", "ids_duplicate", "ids_wrong", "typed_missing", "typed_duplicate", "typed_item", "typed_from", "typed_to", "request_actor", "request_order", "request_source", "request_destination", "receipt_missing" };
            foreach (string mutation in mutations)
            {
                var raw = JObject.Parse(legal);
                var node = B2MarkerRawState(raw, stateAfter)["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectTypeId"] == effectType);
                var result = (JObject)node["NormalizedResult"];
                var receipt = operation == "token" ? B2MarkerRawValue((JObject)node["ContinuationData"], "transferBatchReceipt") : null;
                switch (mutation)
                {
                    case "protocol": B2MarkerRawValue(receipt, "protocolVersion")["IntegerValue"] = 1; break;
                    case "requested": B2MarkerRawValue((JObject)node["NormalizedArguments"], "requiredCount")["IntegerValue"] = 16; break;
                    case "moved": B2MarkerRawValue(result, "movedCount")["IntegerValue"] = 16; break;
                    case "changed": B2MarkerRawValue(result, "changed")["BooleanValue"] = false; break;
                    case "receipt": B2MarkerRawValue(receipt, "requestDigest")["StringValue"] = "forged-receipt"; break;
                    case "receipt_operation": B2MarkerRawValue(receipt, "operationId")["StringValue"] = "forged-operation"; break;
                    case "receipt_count": B2MarkerRawValue(receipt, "movedCount")["IntegerValue"] = 16; break;
                    case "ids_missing": B2MarkerRawValue(result, "movedItemIds")["Items"].Last.Remove(); break;
                    case "ids_duplicate": B2MarkerRawValue(result, "movedItemIds")["Items"][1] = B2MarkerRawValue(result, "movedItemIds")["Items"][0].DeepClone(); break;
                    case "ids_wrong": B2MarkerRawValue(result, "movedItemIds")["Items"][0]["StringValue"] = "forged-item"; break;
                    case "legacy_positions":
                        ((JArray)result["Properties"]).Add(new JObject { ["Name"] = "positionChanges", ["Value"] = JObject.FromObject(NormalizedValue.CreateArray(new List<NormalizedValue>())) });
                        result["Properties"] = new JArray(((JArray)result["Properties"]).OrderBy(v => (string)v["Name"], StringComparer.Ordinal)); break;
                    case "typed_missing": node["PositionChanges"].Last.Remove(); break;
                    case "typed_duplicate": node["PositionChanges"][1] = node["PositionChanges"][0].DeepClone(); break;
                    case "typed_item": node["PositionChanges"][0]["ItemId"] = "forged-item"; break;
                    case "typed_from": node["PositionChanges"][0]["From"]["ZoneId"] = "forged-from"; break;
                    case "typed_to": node["PositionChanges"][0]["To"]["ZoneId"] = "forged-to"; break;
                    case "request_actor": node["TransferBatchRequest"]["ExecutingPlayerId"] = 2; break;
                    case "request_order":
                        var ids = (JArray)node["TransferBatchRequest"]["Transfers"][0]["ItemIds"];
                        var first = ids[0].DeepClone(); ids[0] = ids[1].DeepClone(); ids[1] = first; break;
                    case "request_source": node["TransferBatchRequest"]["Transfers"][0]["ExpectedSource"]["ZoneId"] = "forged-source"; break;
                    case "request_destination": node["TransferBatchRequest"]["Transfers"][0]["Destination"]["ZoneId"] = "forged-destination"; break;
                    case "receipt_missing": B2MarkerDrop((JObject)node["ContinuationData"], "transferBatchReceipt"); break;
                }
                B2AssertMarkerRejected(raw, domain, "Load2或token3" + operation + (domain ? "domain" : "raw") + mutation, failures);
            }
            if (!domain)
            foreach (string field in operation == "load" ? new[] { "protocolVersion", "requestedCount", "movedCount", "changed", "movedItemIds", "receiptId", "failureReason" } : new[] { "movedCount", "changed", "movedItemIds" })
            foreach (bool missing in new[] { true, false })
            {
                var raw = JObject.Parse(legal);
                var result = (JObject)B2MarkerRawState(raw, stateAfter)["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectTypeId"] == effectType)["NormalizedResult"];
                if (missing) B2MarkerDrop(result, field); else B2MarkerRawValue(result, field)["Kind"] = (int)NormalizedValueKind.Null;
                B2AssertMarkerRejected(raw, false, operation + "结果" + field + (missing ? "缺失" : "坏类型"), failures);
            }
            if (!domain && operation == "token")
            foreach (string field in new[] { "protocolVersion", "operationId", "requestDigest", "movedCount" })
            foreach (bool missing in new[] { true, false })
            {
                var raw = JObject.Parse(legal);
                var node = B2MarkerRawState(raw, stateAfter)["EffectRuntime"]["EffectNodes"].Single(n => (string)n["EffectTypeId"] == ItemTransferEffectExecutor.TypeId);
                var receipt = B2MarkerRawValue((JObject)node["ContinuationData"], "transferBatchReceipt");
                if (missing) B2MarkerDrop(receipt, field); else B2MarkerRawValue(receipt, field)["Kind"] = (int)NormalizedValueKind.Null;
                B2AssertMarkerRejected(raw, false, "token3收据" + field + (missing ? "缺失" : "坏类型"), failures);
            }
            Assert.That(failures, Is.Empty, "下列坏档被接受或非目标误拒：" + string.Join("、", failures));
        }
    }
}
