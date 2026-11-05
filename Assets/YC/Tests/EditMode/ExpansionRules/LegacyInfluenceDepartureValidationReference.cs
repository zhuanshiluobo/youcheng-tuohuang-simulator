using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;
namespace YC.Tests.EditMode.ExpansionRules
{
    internal static class LegacyInfluenceDepartureValidationReference
    {
        public static bool Validate(GameState state, out string reason)
        {
            reason = "departure_protocol_invalid";
            var runtime = state.EffectRuntime;
            if (runtime.InfluenceDepartures == null || runtime.InfluenceDepartureBatches == null) return false;
            var ids = new HashSet<string>(); var held = new HashSet<string>();
            foreach (var d in runtime.InfluenceDepartures)
            {
                if (d == null || d.ProtocolVersion != 1 || d.OperationIndex < 0 || d.DepartureId != StableIdFactory.Create("departure", d.SourceEffectId, d.OperationIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)) || !ids.Add(d.DepartureId) || d.OriginalSubject == null || d.OriginalPosition == null ||
                    d.ResponseIds == null || d.EventIds == null || d.CurrentIndex < 0 || d.CurrentIndex > d.ResponseIds.Count ||
                    !new[] { "open", "stored", "returned" }.Contains(d.Status) || !new[] { "remove", "replace" }.Contains(d.CauseKind)) return false;
                var op = runtime.EffectNodes.Find(n => n.EffectId == d.SourceEffectId);
                var item = state.ItemZones.Items.Find(i => i.ItemId == d.ItemId);
                if (op == null || item == null || !new[] { InfluenceEffectTypeIds.RemoveInfluence, InfluenceEffectTypeIds.ReplaceInfluence }.Contains(op.EffectTypeId) ||
                    item.OriginalSupplyZoneId != d.OriginalSupplyZoneId || !state.ItemZones.Zones.Any(z => z.ZoneId == d.OriginalSupplyZoneId) ||
                    !op.PositionChanges.Any(p => p.ItemId == d.ItemId && p.From?.ZoneId == d.OriginalPosition.ZoneId && p.From?.SlotId == d.OriginalPosition.SlotId) ||
                    !d.EventIds.Any(id => runtime.RuleEvents.Any(e => e.EventId == id && e.SourceEffectId == op.EffectId && e.EventType == InfluenceEventTypeIds.InfluenceRemoved && Text(e.Payload, "departureId") == d.DepartureId)) ||
                    d.EventIds.Distinct().Count() != d.EventIds.Count || d.EventIds.Any(id => !runtime.RuleEvents.Any(e => e.EventId == id && e.SourceEffectId == d.SourceEffectId && Text(e.Payload, "departureId") == d.DepartureId && Number(e.Payload, "eventProtocolVersion") == 2)) ||
                    d.ResponseIds.Distinct().Count() != d.ResponseIds.Count || d.ResponseIds.Any(id => !runtime.DeferredResponses.Any(r => r.DeferredResponseId == id && r.DepartureId == d.DepartureId))) return false;
                if (d.Status == "open" && (!held.Add(d.ItemId) || item.ZoneId != d.CustodyZoneId || !state.ItemZones.Zones.Any(z => z.ZoneId == item.ZoneId && z.OwnerEffectId == d.CustodyRootId && z.LocalZoneId == "removed_influence") || !string.IsNullOrEmpty(d.DestinationZoneId))) return false;
                if (d.Status != "open" && (string.IsNullOrEmpty(d.DestinationZoneId) || !runtime.EffectNodes.Any(n => n.PositionChanges.Any(p => p.ItemId == d.ItemId && p.From?.ZoneId == d.CustodyZoneId && p.To?.ZoneId == d.DestinationZoneId)))) return false;
            }
            foreach (var b in runtime.InfluenceDepartureBatches)
            {
                if (b == null || b.ProtocolVersion != 1 || b.ProducerSteps == null || b.FinishedSteps == null || b.DepartureIds == null || b.ResponseIds == null || b.Offers == null ||
                    b.BatchId != StableIdFactory.Create("departure_batch", b.OwnerEffectId) || b.ProducerSteps.Distinct().Count() != b.ProducerSteps.Count ||
                    b.FinishedSteps.Distinct().Count() != b.FinishedSteps.Count || b.FinishedSteps.Any(id => !b.ProducerSteps.Contains(id)) ||
                    b.Sealed && b.ProducerSteps.Count != b.FinishedSteps.Count || b.DepartureIds.Distinct().Count() != b.DepartureIds.Count ||
                    b.DepartureIds.Any(id => !runtime.InfluenceDepartures.Any(d => d.DepartureId == id && d.BatchId == b.BatchId)) ||
                    b.ResponseIds.Any(id => !runtime.DeferredResponses.Any(r => r.DeferredResponseId == id && r.BatchId == b.BatchId)) ||
                    b.Offers.Any(o => o == null || o.Body == null || o.EventIds == null || o.DepartureIds == null || o.DepartureIds.Count == 0 || o.DepartureIds.Count != o.EventIds.Count || o.DepartureIds.Distinct().Count() != o.DepartureIds.Count ||
                        o.DepartureIds.Any(id => !b.DepartureIds.Contains(id)) || o.EventIds.Any(id => !runtime.RuleEvents.Any(e => e.EventId == id && e.EventType == InfluenceEventTypeIds.InfluenceRemoved && o.DepartureIds.Contains(Text(e.Payload, "departureId")))))) return false;
                if (!b.DepartureIds.SequenceEqual(runtime.InfluenceDepartures.Where(d => d.BatchId == b.BatchId).Select(d => d.DepartureId)) ||
                    !b.ResponseIds.SequenceEqual(runtime.DeferredResponses.Where(r => r.BatchId == b.BatchId).Select(r => r.DeferredResponseId))) return false;
                foreach (var offer in b.Offers)
                {
                    if (offer.EventId != offer.EventIds[0] || !runtime.DispatchReceipts.Any(r => r.EventId == offer.EventId && r.SubscriptionId == offer.SubscriptionId)) return false;
                    for (int index = 0; index < offer.DepartureIds.Count; index++)
                    {
                        var departure = runtime.InfluenceDepartures.Single(d => d.DepartureId == offer.DepartureIds[index]);
                        if (!runtime.RuleEvents.Any(e => e.EventId == offer.EventIds[index] && e.SourceEffectId == departure.SourceEffectId &&
                            e.EventType == InfluenceEventTypeIds.InfluenceRemoved && Text(e.Payload, "departureId") == departure.DepartureId)) return false;
                    }
                }
            }
            reason = ""; return true;
        }
    }
}
