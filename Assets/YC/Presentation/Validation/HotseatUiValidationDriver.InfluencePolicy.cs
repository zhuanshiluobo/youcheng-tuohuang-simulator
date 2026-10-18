#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Travel;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private IReadOnlyList<string> RankInfluencePlacementSlots(IEnumerable<string> legal)
        {
            var map = new MapQueryService(StaticMapDefinitions.Resolve(State.MapId));
            string city = State.FindPlayer(LocalPlayer).CityLocationId;
            var ownLocations = new HashSet<string>(); var ownRoutes = new HashSet<string>();
            foreach (var influence in State.Map.Influences.Where(value => value.PlayerId == LocalPlayer && value.OwnerSubject?.SubjectType != "company"))
                if (InfluenceSlotReference.TryParse(map, influence.SlotId, out var slot, out _))
                { if (slot.Kind == InfluenceSlotKind.Location) ownLocations.Add(slot.LocationId); else ownRoutes.Add(slot.RouteId); }
            var facts = new RouteTollService(map).ReadRouteFacts(State, LocalPlayer);
            var freeRoutes = new HashSet<string>(facts.Where(value => value.TollGoldVoucherAmount == 0).Select(value => value.RouteId));
            var search = new MapPathSearchService(map);
            var free = search.FindReachablePaths(city, route => freeRoutes.Contains(route.RouteId));
            var resourcePoints = new HashSet<string>(State.Map.ResourceTokens.Select(value => value.LocationId));
            var missing = ownLocations.Where(id => !resourcePoints.Contains(id) || !free.ContainsKey(id)).ToArray();
            bool allOwnFree = missing.Length == 0;
            var distances = search.FindReachablePaths(city, route => true);
            var candidates = new List<InfluencePolicyCandidate>();
            foreach (string id in legal.Distinct())
            {
                if (!InfluenceSlotReference.TryParse(map, id, out var slot, out _)) continue;
                if (slot.Kind == InfluenceSlotKind.Location)
                {
                    if (slot.LocationId == city || ownLocations.Contains(slot.LocationId) || !allOwnFree) continue;
                    candidates.Add(new InfluencePolicyCandidate { Id = id, Kind = free.ContainsKey(slot.LocationId) ? 0 : 2,
                        Distance = distances.TryGetValue(slot.LocationId, out var path) ? path.RouteIds.Count : 999 });
                }
                else
                {
                    if (ownRoutes.Contains(slot.RouteId)) continue;
                    var connected = search.FindReachablePaths(city, route => freeRoutes.Contains(route.RouteId) || route.RouteId == slot.RouteId);
                    var route = map.GetRoute(slot.RouteId);
                    int distance = new[] { route.FromLocationId, route.ToLocationId }.Select(location =>
                        distances.TryGetValue(location, out var path) ? path.RouteIds.Count : 999).Min();
                    candidates.Add(new InfluencePolicyCandidate { Id = id, Kind = 1, Distance = distance,
                        OwnedGain = missing.Count(connected.ContainsKey), ResourceGain = resourcePoints.Count(location => connected.ContainsKey(location) && !free.ContainsKey(location)),
                        AlreadyFree = freeRoutes.Contains(slot.RouteId) });
                }
            }
            var ranked = candidates.OrderBy(value => value.Kind).ThenByDescending(value => value.OwnedGain)
                .ThenBy(value => value.AlreadyFree).ThenByDescending(value => value.ResourceGain).ThenBy(value => value.Distance).ThenBy(value => value.Id).ToArray();
            Append("影响力决策.jsonl", new { Frame = UnityEngine.Time.frameCount, AtUtc = DateTime.UtcNow.ToString("o"), State.GameId, State.Round, State.ActionRound, PlayerId = LocalPlayer, City = city,
                OwnLocations = ownLocations.ToArray(), OwnRoutes = ownRoutes.ToArray(), MissingFreeCollection = missing,
                AllOwnInfluencePointsCollectableForFree = allOwnFree, FreeReachable = free.Keys.ToArray(), Candidates = ranked,
                Rule = "排除脚下资源点与已有本人影响力的点/航道；全部本人点当前可免路费采集才允许新点，否则只铺航道；不主动支付采集路费" });
            return ranked.Select(value => value.Id).ToArray();
        }
        private sealed class InfluencePolicyCandidate
        {
            public string Id;
            public int Kind, OwnedGain, ResourceGain, Distance;
            public bool AlreadyFree;
        }
    }
}
#endif
