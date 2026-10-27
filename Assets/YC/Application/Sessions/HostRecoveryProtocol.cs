using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    public static partial class HostRecoveryService
    {
        public const string GlobalVersionUnsupported = "host_global_version_unsupported";

        // 检查原始字段，不能靠DTO初始化器把旧版本或缺失事实伪装成当前发布。
        private static bool HasCurrentProtocolEnvelope(JObject snapshot, JObject journal, out string reason)
        {
            reason = GlobalVersionUnsupported;
            if (snapshot?["SchemaVersion"]?.Type != JTokenType.Integer ||
                journal?["SchemaVersion"]?.Type != JTokenType.Integer ||
                (int)snapshot["SchemaVersion"] != GameProtocolVersions.Host ||
                (int)journal["SchemaVersion"] != GameProtocolVersions.Journal) return false;
            reason = "host_envelope_raw_fields_missing";
            if (snapshot["RandomStateRequired"]?.Type != JTokenType.Boolean ||
                snapshot["StateRevision"]?.Type != JTokenType.Integer ||
                snapshot["LastCommitSequence"]?.Type != JTokenType.Integer ||
                snapshot["ContentHash"]?.Type != JTokenType.String ||
                snapshot["RuleContractVersion"]?.Type != JTokenType.String ||
                snapshot["ActiveMainNodeId"]?.Type != JTokenType.String ||
                snapshot["ActiveBlockerIds"] is not JArray blockers || blockers.Any(v => v.Type != JTokenType.String) ||
                snapshot["State"] is not JObject || journal["Entries"] is not JArray) return false;
            reason = string.Empty;
            return true;
        }

        private static bool HasCurrentCandidateProofs(JObject runtime)
        {
            if (runtime["CandidateResolutions"] is not JArray records) return false;
            foreach (var token in records)
            {
                if (token is not JObject record || record["FacilitySourceProofs"] is not JArray proofs) return false;
                foreach (var proof in proofs)
                    if (proof is not JObject fields || new[] { "ItemId", "SourceZoneId", "SourceKey", "ContentHash" }.Any(k => fields[k]?.Type != JTokenType.String)) return false;
            }
            if (runtime["InteractionRequests"] is not JArray requests) return false;
            foreach (var request in requests.OfType<JObject>().Where(r => (string)r["InteractionTypeId"] == "facility.build.normal"))
            {
                var record = records.OfType<JObject>().LastOrDefault(r => (string)r["CandidateSetId"] == (string)request["CandidateResolutionId"]);
                if (record == null || request["CandidateIds"] is not JArray ids || record["FacilitySourceProofs"] is not JArray proofs ||
                    !ids.Select(v => (string)v).SequenceEqual(proofs.Select(v => (string)v["ItemId"]))) return false;
            }
            return true;
        }

        // 只沿真实DTO的node/spec字段访问凭据，不扫描NormalizedValue或其它Lua载荷。
        private static bool HasRawPersistentSourceFacts(JObject state, out string reason)
        {
            // Json.NET按不区分大小写匹配成员；检查同一载体的全部别名，避免后写成员绕过门禁。
            bool Member(JObject owner, string name, Func<JToken, bool> validate) => owner.Properties()
                .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).All(p => validate(p.Value));
            // 集合本身的结构仍由既有门禁负责；本处只检查实际对象中的非空凭据。
            bool Objects(JToken value, Func<JObject, bool> validate) => value is not JArray items || items.OfType<JObject>().All(validate);
            bool Required(JObject value, string name, JTokenType type) => value[name]?.Type == type &&
                value.Properties().Count(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 1;
            bool Binding(JToken value)
            {
                if (value == null || value.Type == JTokenType.Null) return true;
                if (value is not JObject binding || !Required(binding, "ProtocolVersion", JTokenType.Integer) ||
                    !JToken.DeepEquals(binding["ProtocolVersion"], new JValue(1)) || !Required(binding, "SourceRef", JTokenType.Object) ||
                    new[] { "ContentPackHash", "TriggerEventId", "SubscriptionId", "HandlerId", "HandlerHash", "HandlerVersion", "ActivationEffectId", "UseId", "SourceDigest" }
                        .Any(name => !Required(binding, name, JTokenType.String))) return false;
                var source = (JObject)binding["SourceRef"];
                return new[] { "SourceKey", "ItemId", "DefinitionId", "AbilityId", "ActiveFaceId", "DeclarationVersion", "AuthorizationRef", "EffectiveZoneId", "RuleOwnerItemId" }
                    .All(name => Required(source, name, JTokenType.String)) && Required(source, "AuthorizedPlayerId", JTokenType.Integer) &&
                    Required(source, "GrantingSourceRefs", JTokenType.Array) && source["GrantingSourceRefs"].All(v => v.Type == JTokenType.String);
            }
            bool Spec(JObject spec) => Member(spec, "PersistentSourceFacts", Binding) &&
                Member(spec, "NestedEffects", value => Objects(value, Spec));
            bool Node(JObject node) => Spec(node) && Member(node, "SettlementWindow", value => value is not JObject window ||
                Member(window, "Entries", entries => Objects(entries, entry => Member(entry, "Effects", effects => Objects(effects, Spec)))));
            bool Runtime(JObject runtime) => Member(runtime, "EffectNodes", value => Objects(value, Node)) &&
                Member(runtime, "DeferredResponses", value => Objects(value, response => Member(response, "PendingBody", body => Objects(body, Spec)))) &&
                Member(runtime, "InfluenceDepartureBatches", value => Objects(value, batch => Member(batch, "Offers", offers => Objects(offers,
                    offer => Member(offer, "Body", body => body is not JObject spec || Spec(spec)))))) &&
                Member(runtime, "InfluencePlaceMappings", value => Objects(value, mapping => Member(mapping, "OriginalSpec", original => original is not JObject spec || Spec(spec))));
            bool valid = state != null && Member(state, "EffectRuntime", value => value is not JObject runtime || Runtime(runtime));
            reason = valid ? string.Empty : "persistent_source_facts.v1:raw_fields_missing_or_invalid";
            return valid;
        }

        private static bool HasCurrentStateProtocol(JObject state)
        {
            return state?["EntitySchemaVersion"]?.Type == JTokenType.Integer &&
                (int)state["EntitySchemaVersion"] == GameProtocolVersions.Entity &&
                state["EffectRuntime"] is JObject runtime && runtime["SchemaVersion"]?.Type == JTokenType.Integer &&
                (int)runtime["SchemaVersion"] == GameProtocolVersions.Runtime &&
                state["InfluenceLocks"] is JArray && state["ItemZones"] is JObject zones &&
                zones["FacilityLayerVersion"]?.Type == JTokenType.Integer &&
                state.Property("RandomRuntime") != null && HasCurrentCandidateProofs(runtime);
        }
    }
}
