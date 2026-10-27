using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Domain.Items
{
    public sealed partial class ItemTransferService
    {
        /// <summary>窄入口：只能按冻结机会队列交接该次真实离场物，付款暂存永不匹配。</summary>
        internal ItemTransferResult TransferDepartureCustodyOnWorking(GameState work, InfluenceDepartureRecord d, EffectNodeRuntimeState nextRoot, string destination)
        {
            if (d == null || !work.EffectRuntime.InfluenceDepartures.Any(v => ReferenceEquals(v, d)) || d.Status != "open") throw new InvalidOperationException("departure_handoff_invalid");
            var item = work.ItemZones.Items.Single(i => i.ItemId == d.ItemId);
            var from = work.ItemZones.Zones.Single(z => z.ZoneId == item.ZoneId);
            var to = work.ItemZones.Zones.Single(z => z.ZoneId == destination);
            bool final = destination == d.OriginalSupplyZoneId && d.CurrentIndex == d.ResponseIds.Count;
            bool next = d.CurrentIndex < d.ResponseIds.Count && work.EffectRuntime.DeferredResponses.Any(r => r.ProtocolVersion == 1 && r.Kind == "destination" &&
                r.DeferredResponseId == d.ResponseIds[d.CurrentIndex] && r.ResponseRootEffectId == nextRoot.EffectId && r.Status == DeferredResponseStatus.Queued && r.DepartureId == d.DepartureId);
            if (item.ZoneId != d.CustodyZoneId || from.TemplateId != "core.zone.effect_staging" || from.LocalZoneId != "removed_influence" || from.OwnerEffectId != d.CustodyRootId ||
                !final && (!next || to.TemplateId != "core.zone.effect_staging" || to.LocalZoneId != "removed_influence" || to.OwnerEffectId != nextRoot.EffectId) || !query.Accepts(to, item))
                throw new InvalidOperationException("departure_handoff_permission_denied");
            var request = new ItemTransferRequest { ItemIds = new List<string> { item.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId },
                Destination = new ZoneRef { ZoneId = destination }, InsertionKind = "unordered", OperationProfileId = "core.cube_storage", RequiredCount = 1, ExecutingPlayerId = nextRoot.PlayerId };
            var preview = GameStateCloneService.DeepClone(work); Apply(preview, request);
            if (!query.TryValidate(preview, out var reason)) throw new InvalidOperationException(reason);
            var result = Apply(work, request); nextRoot.PositionChanges.AddRange(GameStateCloneService.DeepClone(result.PositionChanges));
            if (!final) { d.CustodyRootId = nextRoot.EffectId; d.CustodyZoneId = destination; }
            return result;
        }
    }
}
