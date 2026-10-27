using System.Linq;
using Newtonsoft.Json.Linq;
namespace YC.Application.Sessions
{
    public static partial class HostRecoveryService
    {
        private static bool ValidateRawInfluenceProtocol(JObject runtime)
        {
            if (runtime == null) return false;
            bool Int(JToken x) => x?.Type == JTokenType.Integer;
            bool Texts(JToken x, params string[] keys) => x is JObject o && keys.All(k => o[k]?.Type == JTokenType.String);
            bool Arrays(JToken x, params string[] keys) => x is JObject o && keys.All(k => o[k] is JArray);
            string[] families = { "InfluenceDepartures", "InfluenceDepartureBatches", "InfluencePlaceMappings" };
            if (families.All(k => runtime[k] == null))
                return !((JArray)runtime["EffectNodes"]).Any(n => (string)n["DefinitionVersion"] == "2.0.0" && new[] { "effect.influence.remove", "effect.influence.replace" }.Contains((string)n["EffectTypeId"]));
            if (families.Any(k => runtime[k] is not JArray)) return false;
            foreach (var d in (JArray)runtime["InfluenceDepartures"])
                if (!Int(d["ProtocolVersion"]) || (long)d["ProtocolVersion"] != 1 || !Int(d["OperationIndex"]) || !Int(d["CurrentIndex"]) ||
                    !Texts(d, "DepartureId", "SourceEffectId", "ItemId", "CauseKind", "OriginalSupplyZoneId", "CustodyRootId", "CustodyZoneId", "Status", "DestinationZoneId", "BatchId") ||
                    !Arrays(d, "EventIds", "ResponseIds") || !Texts(d["OriginalPosition"], "ZoneId", "SlotId") || !Int(d["OriginalSubject"]?["Kind"]) || !Int(d["OriginalSubject"]?["PlayerId"])) return false;
            foreach (var b in (JArray)runtime["InfluenceDepartureBatches"])
                if (!Int(b["ProtocolVersion"]) || (long)b["ProtocolVersion"] != 1 || b["Sealed"]?.Type != JTokenType.Boolean || !Texts(b, "BatchId", "OwnerEffectId") || !Arrays(b, "ProducerSteps", "FinishedSteps", "DepartureIds", "ResponseIds", "Offers")) return false;
            foreach (var m in (JArray)runtime["InfluencePlaceMappings"])
                if (!Int(m["ProtocolVersion"]) || (long)m["ProtocolVersion"] != 1 || !Texts(m, "LogicalOperationId", "ActualRootEffectId", "ParentEffectId", "SelectedLeafEffectId", "ResultContract", "PermissionContract") ||
                    m["OriginalSpec"] is not JObject || !Arrays(m, "Sources", "HandlerHashes")) return false;
            foreach (var r in ((JArray)runtime["DeferredResponses"]).Where(r => r["ProtocolVersion"] != null && (long)r["ProtocolVersion"] != 0))
                if (!Int(r["ProtocolVersion"]) || (long)r["ProtocolVersion"] != 1 || r["IsBypass"]?.Type != JTokenType.Boolean || r["RequiresProducerCompletion"]?.Type != JTokenType.Boolean ||
                    !Texts(r, "Kind", "DepartureId", "BatchId", "SourceOperationEffectId", "SourceKey", "SubscriptionId", "DefinitionVersion", "HandlerHash") || !Arrays(r, "SourceEventIds", "PendingBody")) return false;
            return true;
        }
    }
}
