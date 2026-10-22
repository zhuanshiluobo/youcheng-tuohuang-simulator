using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class InfluencePlacementQueryTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void BatchCandidatesMatchFreshSingleSlotValidationInMapOrder(bool entityMode)
        {
            var f = new Fixture(entityMode);
            f.Occupy("route:R0:0"); f.AddRoad("R1"); f.MoveOpponentCity("B");
            f.State.Map.OpenLocationIds.Remove("A");
            AssertEquivalent(f, 1); AssertEquivalent(f, 2); AssertEquivalent(f, 99);
        }

        [TestCase("occupied", "route:R0:0")]
        [TestCase("token", "location:A:0")]
        [TestCase("city", "location:A:0")]
        [TestCase("road", "route:R0:0")]
        [TestCase("lock", "location:A:0")]
        [TestCase("supply", "location:A:0")]
        [TestCase("closed", "location:A:0")]
        [TestCase("custom", "location:A:0")]
        public void SameRevisionChangesAreReadByTheNextBatchAndSingleSlotCheck(string change, string affected)
        {
            var f = new Fixture(true);
            var first = f.Service.GetLegalPlacementSlotIds(f.State, 1);
            Assert.That(first, Does.Contain(affected)); int revision = f.State.EffectRuntime.StateRevision;
            // 直接改测试输入，专门证明同revision不缓存；生产移动仍由正式Effect提交。
            switch (change)
            {
                case "occupied": f.Occupy(affected); break;
                case "token": f.State.ItemZones.Items.RemoveAll(item => item.TypeId == "core.resource_point_token" && item.ZoneId == f.Child("A", "core.zone.map_location", "core.zone.resource_point_markers").ZoneId); break;
                case "city": f.MoveOpponentCity("A"); break;
                case "road": f.AddRoad("R0"); break;
                case "lock":
                    var city = f.State.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 1);
                    city.ZoneId = f.Child("A", "core.zone.map_location", "core.zone.city_dock").ZoneId;
                    f.State.InfluenceLocks.Add(new InfluenceLockState { LockId = "query-lock", BoundCityItemId = city.ItemId, Active = true, ExpiresBeforeActionRoundSequence = 3 }); break;
                case "supply": f.State.ItemZones.Items.RemoveAll(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == 1); break;
                case "closed": f.State.Map.OpenLocationIds.Remove("A"); break;
                case "custom": f.Service.PlacementAllowed = (state, slot) => slot != affected; break;
            }
            Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(f.Service.GetLegalPlacementSlotIds(f.State, 1), Does.Not.Contain(affected));
            Assert.That(f.Service.CanPlace(f.State, 1, affected).IsValid, Is.False);
            Assert.That(first, Does.Contain(affected), "已返回的候选列表不被后续查询或状态写入改变。");
            AssertEquivalent(f, 1);
        }

        [Test]
        public void BatchMaterializesEachLegacyPositionProjectionOnce()
        {
            var f = new Fixture(true); int influences = 0, tokens = 0, roads = 0;
            Observe(f.State.Map.Influences, () => influences++);
            Observe(f.State.Map.ResourceTokens, () => tokens++);
            Observe(f.State.Map.RoadRouteIds, () => roads++);
            var expected = f.AllSlots().Where(slot => f.Service.CanPlace(f.State, 1, slot).IsValid).ToArray();
            Assert.That(influences, Is.GreaterThan(1)); Assert.That(tokens, Is.GreaterThan(1));
            influences = tokens = roads = 0;
            Assert.That(f.Service.GetLegalPlacementSlotIds(f.State, 1), Is.EqualTo(expected));
            Assert.That(influences, Is.EqualTo(1)); Assert.That(tokens, Is.EqualTo(1)); Assert.That(roads, Is.EqualTo(1));
            influences = tokens = roads = 0;
            Assert.That(f.Service.HasAnyLegalPlacementSlot(f.State, 1), Is.True);
            Assert.That(influences, Is.EqualTo(1)); Assert.That(tokens, Is.EqualTo(1)); Assert.That(roads, Is.EqualTo(1));
        }

        [Test]
        public void ExistenceQueryStopsTheSharedPredicateAfterTheFirstLegalSlot()
        {
            var f = new Fixture(true); int calls = 0;
            f.Service.PlacementAllowed = (state, slot) => { calls++; return true; };
            Assert.That(f.Service.HasAnyLegalPlacementSlot(f.State, 1), Is.True); Assert.That(calls, Is.EqualTo(1));
            calls = 0; f.Service.GetLegalPlacementSlotIds(f.State, 1);
            Assert.That(calls, Is.EqualTo(f.AllSlots().Count()));
            f.Service.PlacementAllowed = (state, slot) => false;
            Assert.That(f.Service.HasAnyLegalPlacementSlot(f.State, 1), Is.False);
            Assert.That(f.Service.GetLegalPlacementSlotIds(f.State, 1), Is.Empty);
        }

        [Test]
        public void InjectedRoadQueryRunsOncePerRouteAndIsReevaluatedOnEveryCall()
        {
            var f = new Fixture(true); var road = new CountingRoadQuery();
            var service = new InfluenceService(f.State, f.Query, road);
            road.Covered.Add("R0");
            var expected = f.AllSlots().Where(slot => service.CanPlace(f.State, 1, slot).IsValid).ToArray();
            road.Calls.Clear();
            Assert.That(service.GetLegalPlacementSlotIds(f.State, 1), Is.EqualTo(expected));
            Assert.That(road.Calls.Count, Is.EqualTo(2)); Assert.That(road.Calls.Values.All(value => value == 1), Is.True);
            road.Covered.Clear(); road.Covered.Add("R1"); road.Calls.Clear();
            var changed = service.GetLegalPlacementSlotIds(f.State, 1);
            Assert.That(changed, Does.Contain("route:R0:0")); Assert.That(changed, Does.Not.Contain("route:R1:0"));
            Assert.That(road.Calls.Values.All(value => value == 1), Is.True);
        }

        [Test]
        public void CompanyOccupancyAndCompanyValidationKeepTheirOriginalSubjectRules()
        {
            var f = new Fixture(true); var company = ItemSubjectRef.Company(CompanyInfluenceRules.CompanyId);
            var marker = f.State.ItemZones.Items.First(item => item.OwnerSubject.Kind == ItemSubjectKind.Company);
            marker.ZoneId = f.State.ItemZones.Zones.Single(zone => zone.LegalSlotIds.Contains("location:A:0") && zone.TemplateId == "core.zone.influence_slots").ZoneId;
            marker.SlotId = "location:A:0";
            Assert.That(f.Service.GetLegalPlacementSlotIds(f.State, 1), Does.Not.Contain(marker.SlotId));
            var companySlots = new CompanyInfluenceRules(f.Query, f.Service.PlacementAllowed).GetPlacementSlotIds(f.State);
            foreach (var slot in f.AllSlots())
                Assert.That(f.Service.ValidateItemPlacement(f.State, company, slot).Succeeded, Is.EqualTo(companySlots.Contains(slot)), slot);
            Assert.That(f.Service.ValidateItemPlacement(f.State, company, "route:R0:0").Succeeded, Is.False);
            AssertEquivalent(f, 1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingPlayerAndEmptySupplyReturnNoCandidates(bool entityMode)
        {
            var f = new Fixture(entityMode);
            Assert.That(f.Service.GetLegalPlacementSlotIds(f.State, 99), Is.Empty);
            Assert.That(f.Service.HasAnyLegalPlacementSlot(f.State, 99), Is.False);
            if (entityMode) f.State.ItemZones.Items.RemoveAll(item => item.OwnerSubject.PlayerId == 1 && item.TypeId == "core.player_marker");
            else f.State.FindPlayer(1).InfluenceSupply = 0;
            Assert.That(f.Service.GetLegalPlacementSlotIds(f.State, 1), Is.Empty);
            Assert.That(f.Service.HasAnyLegalPlacementSlot(f.State, 1), Is.False);
        }

        private static void AssertEquivalent(Fixture fixture, int playerId)
        {
            var expected = fixture.AllSlots().Where(slot => fixture.Service.CanPlace(fixture.State, playerId, slot).IsValid).Distinct().ToArray();
            Assert.That(fixture.Service.GetLegalPlacementSlotIds(fixture.State, playerId), Is.EqualTo(expected));
            Assert.That(fixture.Service.HasAnyLegalPlacementSlot(fixture.State, playerId), Is.EqualTo(expected.Length > 0));
        }

        private static void Observe<T>(LegacyPositionList<T> list, Action observed)
        {
            var projection = (Func<List<T>>)typeof(LegacyPositionList<T>).GetField("projection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(list);
            Assert.That(projection, Is.Not.Null);
            list.Bind(() => { observed(); return projection(); });
        }

        private sealed class CountingRoadQuery : IInfluenceRoadCoverageQuery
        {
            public readonly HashSet<string> Covered = new HashSet<string>();
            public readonly Dictionary<string, int> Calls = new Dictionary<string, int>();
            public bool IsRouteCoveredByRoad(GameState state, string route)
            {
                Calls[route] = Calls.TryGetValue(route, out var count) ? count + 1 : 1;
                return Covered.Contains(route);
            }
        }

        private sealed class Fixture
        {
            public readonly GameMapDefinition Map;
            public readonly MapQueryService Query;
            public readonly InfluenceService Service;
            public readonly GameState State;
            private readonly bool entityMode;
            public Fixture(bool entityMode)
            {
                this.entityMode = entityMode;
                Map = new GameMapDefinition { MapId = "placement-query", MinPlayers = 2, MaxPlayers = 4 };
                Map.Regions.Add(new MapRegionDefinition { RegionId = "region" });
                foreach (var id in new[] { "A", "B", "C" })
                    Map.Locations.Add(new MapLocationDefinition { LocationId = id, RegionId = "region", InfluenceSlotCount = 3, CanDockCity = true });
                Map.Routes.Add(new MapRouteDefinition { RouteId = "R0", RegionId = "region", FromLocationId = "A", ToLocationId = "B", InfluenceSlotCount = 3 });
                Map.Routes.Add(new MapRouteDefinition { RouteId = "R1", RegionId = "region", FromLocationId = "B", ToLocationId = "C", InfluenceSlotCount = 3 });
                Query = new MapQueryService(Map); Service = new InfluenceService(Query);
                var source = new GameState { GameId = "placement-query", MapId = Map.MapId, CurrentPlayerId = 1,
                    Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1,
                    RuleConfiguration = new GameRuleConfiguration { PlayerCount = 2, MapId = Map.MapId } };
                source.Players.Add(new PlayerState { PlayerId = 1 }); source.Players.Add(new PlayerState { PlayerId = 2 });
                if (entityMode)
                    State = new ItemZoneStateFactory().CreateForSetup(source, Map, new List<ItemRecipeEntry>
                    {
                        new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.component.marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "player_markers" },
                        new ItemRecipeEntry { EntryId = "companies", DefinitionId = "infected.component.company_marker", TypeId = "infected.company_marker", Quantity = 3, InitialZoneRole = "company_markers" },
                        new ItemRecipeEntry { EntryId = "cities", DefinitionId = "core.component.city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" },
                        new ItemRecipeEntry { EntryId = "resource_a", DefinitionId = "core.component.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/A" },
                        new ItemRecipeEntry { EntryId = "resource_b", DefinitionId = "core.component.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/B" }
                    });
                else
                {
                    State = source;
                    foreach (var player in State.Players) player.InfluenceSupply = 8;
                    foreach (var id in new[] { "A", "B" }) State.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = id, ResourceType = ResourceType.Iron });
                }
                State.Map.OpenLocationIds.AddRange(new[] { "A", "B", "C" });
            }

            public IEnumerable<string> AllSlots() => Map.Locations.SelectMany(location => Enumerable.Range(0, location.InfluenceSlotCount).Select(index => InfluenceService.GetLocationSlotId(location.LocationId, index)))
                .Concat(Map.Routes.SelectMany(route => Enumerable.Range(0, route.InfluenceSlotCount).Select(index => InfluenceService.GetRouteSlotId(route.RouteId, index))));
            public ZoneInstanceState Child(string parentId, string parentType, string template)
            {
                string parent = State.ItemZones.Zones.Single(zone => zone.TemplateId == parentType && zone.LocalZoneId == parentId).ZoneId;
                return State.ItemZones.Zones.Single(zone => zone.ParentId == parent && zone.TemplateId == template);
            }
            public void Occupy(string slot)
            {
                if (!entityMode) { State.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, SlotId = slot }); return; }
                var marker = State.ItemZones.Items.First(item => item.TypeId == "core.player_marker" && item.OwnerSubject.PlayerId == 2);
                marker.ZoneId = State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.influence_slots" && zone.LegalSlotIds.Contains(slot)).ZoneId;
                marker.SlotId = slot;
            }
            public void MoveOpponentCity(string location)
            {
                if (!entityMode) { State.FindPlayer(2).CityLocationId = location; return; }
                State.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == 2).ZoneId = Child(location, "core.zone.map_location", "core.zone.city_dock").ZoneId;
            }
            public void AddRoad(string route)
            {
                if (!entityMode) { State.Map.RoadRouteIds.Add(route); return; }
                State.ItemZones.Items.Add(new ItemInstanceState { ItemId = "road-" + route, TypeId = "core.road_token", PhysicalKind = ItemPhysicalKind.Token,
                    ZoneId = Child(route, "core.zone.map_route", "core.zone.route_road").ZoneId });
            }
        }
    }
}
