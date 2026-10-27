using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    // 隔离的原始字段合同检查；这些最小DTO片段不代表真实来源授权、合法Host档或正常局验收。
    public sealed class QY006PersistentRawProtocolTests
    {
        private const string Invalid = "persistent_source_facts.v1:raw_fields_missing_or_invalid";

        [TestCase("node", false)]
        [TestCase("node", true)]
        [TestCase("pending", false)]
        [TestCase("pending", true)]
        [TestCase("offer", false)]
        [TestCase("offer", true)]
        [TestCase("original", false)]
        [TestCase("original", true)]
        [TestCase("settlement", false)]
        [TestCase("settlement", true)]
        public void EveryStoredCarrierChecksItsBindingBeforeCoercion(string carrier, bool nested)
        {
            var state = RawState(carrier, nested, out var binding);
            AssertGuard(state, true);
            binding["ProtocolVersion"] = "1";
            AssertGuard(state, false);
        }

        [Test]
        public void BindingAndSourceIdentityFieldsRequireExplicitValuesOfTheirDeclaredTypes()
        {
            var state = RawState("node", false, out var binding);
            AssertGuard(state, true);
            var owners = new[] { (JObject)binding["SourceRef"], binding };
            foreach (var owner in owners)
            foreach (var property in owner.Properties().ToList())
            {
                string name = property.Name;
                var original = property.Value.DeepClone();
                owner.Remove(name);
                AssertGuard(state, false, name + " 缺失");
                owner[name] = true;
                AssertGuard(state, false, name + " 类型错误");
                owner[name] = JValue.CreateNull();
                AssertGuard(state, false, name + " 为null");
                owner[name] = original;
            }
            binding["SourceRef"]["GrantingSourceRefs"] = new JArray(1);
            AssertGuard(state, false, "授权引用的数组元素必须是字符串");
        }

        [Test]
        public void UnsupportedVersionsAndNonObjectBindingsUseTheSameRawDiagnostic()
        {
            var state = RawState("node", false, out var binding);
            foreach (var version in new JToken[] { new JValue(0), new JValue(2), new JValue(-1), new JValue(long.MaxValue), new JValue(1.0), new JValue("1") })
            {
                binding["ProtocolVersion"] = version;
                AssertGuard(state, false);
            }
            var node = (JObject)state.SelectToken("EffectRuntime.EffectNodes[0]");
            foreach (var value in new JToken[] { new JValue(true), new JValue(1), new JValue("binding"), new JArray() })
            {
                node["PersistentSourceFacts"] = value;
                AssertGuard(state, false);
            }
        }

        [Test]
        public void MissingOrNullBindingsAndOpaquePayloadNamesRemainOutsideTheRawCredentialCheck()
        {
            foreach (string carrier in new[] { "node", "pending", "offer", "original", "settlement" })
            {
                var state = RawState(carrier, true, out var binding);
                var spec = (JObject)binding.Parent.Parent;
                spec.Remove("PersistentSourceFacts");
                AssertGuard(state, true);
                spec["PersistentSourceFacts"] = JValue.CreateNull();
                AssertGuard(state, true);
                spec["NormalizedArguments"] = JObject.FromObject(NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                    new NormalizedValueEntry { Name = "PersistentSourceFacts", Value = NormalizedValue.CreateString("用户Lua载荷") },
                    new NormalizedValueEntry { Name = "ProtocolVersion", Value = NormalizedValue.CreateString("1") }
                }), ArchiveSerializer());
                AssertGuard(state, true);
                // 非DTO载荷形状只探测本门禁的访问边界，不能作为完整Host合法性的证据。
                spec["ContinuationContext"] = new JObject { ["PersistentSourceFacts"] = "opaque", ["NestedEffects"] = new JArray(new JObject { ["PersistentSourceFacts"] = false }) };
                AssertGuard(state, true);
            }
        }

        [Test]
        public void CaseInsensitiveCarrierAliasesCannotHideOrOverwriteMalformedBindings()
        {
            var state = RawState("node", true, out var binding);
            var node = (JObject)state.SelectToken("EffectRuntime.EffectNodes[0]");
            binding["ProtocolVersion"] = "1";
            Rename(node, "NestedEffects", "nestedEffects");
            Rename((JObject)state["EffectRuntime"], "EffectNodes", "effectNodes");
            AssertGuard(state, false);

            state = RawState("node", false, out binding);
            binding["protocolVersion"] = "1";
            AssertGuard(state, false, "同名大小写别名不能覆盖已检查的整数版本");
            state = RawState("node", false, out binding);
            binding["SourceRef"]["authorizedPlayerId"] = "1";
            AssertGuard(state, false, "同名大小写别名不能覆盖已检查的来源身份");
        }

        private static JObject RawState(string carrier, bool nested, out JObject binding)
        {
            // 默认空字符串也是明确的字段值；来源语义由typed阶段负责，本夹具不制造授权。
            var spec = new EffectSpecRuntimeState { PersistentSourceFacts = new PersistentSourceFactsBinding { SourceRef = new AbilitySourceRef() } };
            if (nested)
                spec = new EffectSpecRuntimeState { NestedEffects = new List<EffectSpecRuntimeState> {
                    new EffectSpecRuntimeState { NestedEffects = new List<EffectSpecRuntimeState> { spec } }
                } };
            var runtime = new EffectRuntimeState();
            string path;
            switch (carrier)
            {
                case "node":
                    runtime.EffectNodes.Add(new EffectNodeRuntimeState { PersistentSourceFacts = spec.PersistentSourceFacts, NestedEffects = spec.NestedEffects });
                    path = "EffectNodes[0]";
                    break;
                case "pending":
                    runtime.DeferredResponses.Add(new DeferredResponseState { PendingBody = new List<EffectSpecRuntimeState> { spec } });
                    path = "DeferredResponses[0].PendingBody[0]";
                    break;
                case "offer":
                    runtime.InfluenceDepartureBatches.Add(new InfluenceDepartureBatch { Offers = new List<DepartureBatchOffer> { new DepartureBatchOffer { Body = spec } } });
                    path = "InfluenceDepartureBatches[0].Offers[0].Body";
                    break;
                case "original":
                    runtime.InfluencePlaceMappings.Add(new InfluencePlaceMapping { OriginalSpec = spec });
                    path = "InfluencePlaceMappings[0].OriginalSpec";
                    break;
                case "settlement":
                    runtime.EffectNodes.Add(new EffectNodeRuntimeState { SettlementWindow = new SettlementWindowRuntimeState {
                        Entries = new List<SettlementEntryRuntimeState> { new SettlementEntryRuntimeState { Effects = new List<EffectSpecRuntimeState> { spec } } }
                    } });
                    path = "EffectNodes[0].SettlementWindow.Entries[0].Effects[0]";
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(carrier));
            }
            var state = new JObject { ["EffectRuntime"] = JObject.FromObject(runtime, ArchiveSerializer()) };
            binding = (JObject)state.SelectToken("EffectRuntime." + path + (nested ? ".NestedEffects[0].NestedEffects[0]" : "") + ".PersistentSourceFacts");
            Assert.That(binding, Is.Not.Null);
            return state;
        }

        private static JsonSerializer ArchiveSerializer()
        {
            var field = typeof(HostRecoveryService).GetField("ArchiveSettings", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null);
            return JsonSerializer.Create((JsonSerializerSettings)field.GetValue(null));
        }

        private static void AssertGuard(JObject state, bool expected, string detail = "")
        {
            var before = state.DeepClone();
            var method = typeof(HostRecoveryService).GetMethod("HasRawPersistentSourceFacts", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { state, null };
            Assert.That((bool)method.Invoke(null, arguments), Is.EqualTo(expected), detail);
            Assert.That(arguments[1], Is.EqualTo(expected ? string.Empty : Invalid), detail);
            Assert.That(JToken.DeepEquals(before, state), Is.True, "raw门禁不得修改输入：" + detail);
        }

        private static void Rename(JObject owner, string name, string alias)
        {
            var property = owner.Property(name);
            property.Replace(new JProperty(alias, property.Value.DeepClone()));
        }
    }
}
