using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceQueryServiceTests
    {
        [Test]
        public void HasInfluenceAt_EmptySlot_ReturnsFalse()
        {
            var state = CreateState();
            var service = CreateService();

            Assert.That(service.HasInfluenceAt(state, LocationSlot("A-01", 0)), Is.False);
        }

        [Test]
        public void HasInfluenceAt_OccupiedSlot_ReturnsTrue()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" });
            var service = CreateService();

            Assert.That(service.HasInfluenceAt(state, slotId), Is.True);
        }

        [Test]
        public void CountInRegion_IncludesLocationInfluence()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" });
            var service = CreateService();

            var count = service.CountInRegion(state, 1, "A", false);

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void CountInRegion_WithCityBonus_AddsTwo()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" });
            state.FindPlayer(1).CityLocationId = "A-01";
            var service = CreateService();

            var withoutCity = service.CountInRegion(state, 1, "A", false);
            var withCity = service.CountInRegion(state, 1, "A", true);

            Assert.That(withoutCity, Is.EqualTo(1));
            Assert.That(withCity, Is.EqualTo(3));
        }

        [Test]
        public void CountInRegion_WithoutCityBonus_ExcludesCity()
        {
            var state = CreateState();
            state.FindPlayer(1).CityLocationId = "A-01";
            var service = CreateService();

            var count = service.CountInRegion(state, 1, "A", false);

            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void CountAllPlayersInRegion_MultiplePlayers()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = LocationSlot("A-01", 0), LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = LocationSlot("A-01", 1), LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, SlotId = LocationSlot("A-02", 0), LocationId = "A-02" });
            var service = CreateService();

            var counts = service.CountAllPlayersInRegion(state, "A", false);

            Assert.That(counts[1], Is.EqualTo(2));
            Assert.That(counts[2], Is.EqualTo(1));
        }

        [Test]
        public void ListByPlayer_ReturnsCorrectInfluences()
        {
            var state = CreateState();
            var slot1 = LocationSlot("A-01", 0);
            var slot2 = LocationSlot("A-02", 0);
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slot1, LocationId = "A-01" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 1, SlotId = slot2, LocationId = "A-02" });
            state.Map.Influences.Add(new InfluencePlacement { PlayerId = 2, SlotId = LocationSlot("B-01", 0), LocationId = "B-01" });
            var service = CreateService();

            var list = service.ListByPlayer(state, 1);

            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0].SlotId, Is.EqualTo(slot1));
            Assert.That(list[1].SlotId, Is.EqualTo(slot2));
        }

        [Test]
        public void FindInfluence_ExistingSlot_ReturnsPlacement()
        {
            var state = CreateState();
            var slotId = LocationSlot("A-01", 0);
            var placement = new InfluencePlacement { PlayerId = 1, SlotId = slotId, LocationId = "A-01" };
            state.Map.Influences.Add(placement);
            var service = CreateService();

            var found = service.FindInfluence(state, slotId);

            Assert.That(found, Is.Not.Null);
            Assert.That(found.PlayerId, Is.EqualTo(1));
            Assert.That(found.SlotId, Is.EqualTo(slotId));
        }

        [Test]
        public void FindInfluence_NonexistentSlot_ReturnsNull()
        {
            var state = CreateState();
            var service = CreateService();

            var found = service.FindInfluence(state, LocationSlot("A-01", 0));

            Assert.That(found, Is.Null);
        }

        [Test]
        public void CountInRegion_IncludesRouteInfluence()
        {
            var state = CreateState();
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = RouteSlot("A1", 0),
                RouteId = "A1"
            });
            var service = CreateService();

            var count = service.CountInRegion(state, 1, "A", false);

            Assert.That(count, Is.EqualTo(1));
        }

        [TestCase("influence.location")]
        [TestCase("influence.route")]
        [TestCase("location:A-01:0")]
        [TestCase("A-01:0")]
        [TestCase("A-01#0")]
        [TestCase("route:A1:0")]
        [TestCase("A1:0")]
        [TestCase("A1#0")]
        [TestCase("invalid-stored-slot")]
        [TestCase("missing")]
        [TestCase("location:A-01:999")]
        [TestCase("")]
        [TestCase(null)]
        public void FindInfluence_SnapshotMatchesExistingLookup(string id)
        {
            var state = CreateState();
            var location = new InfluencePlacement { InfluenceId = "influence.location", PlayerId = 1, SlotId = LocationSlot("A-01", 0) };
            var route = new InfluencePlacement { InfluenceId = "influence.route", PlayerId = 2, SlotId = RouteSlot("A1", 0) };
            var invalid = new InfluencePlacement { InfluenceId = "influence.invalid", SlotId = "invalid-stored-slot" };
            state.Map.Influences.AddRange(new[] { location, route, invalid });
            var snapshot = state.Map.Influences.ToArray();
            var query = CreateService();
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            var expected = id == "influence.location" || id == "location:A-01:0" || id == "A-01:0" || id == "A-01#0" ? location
                : id == "influence.route" || id == "route:A1:0" || id == "A1:0" || id == "A1#0" ? route
                : id == "invalid-stored-slot" ? invalid : null;

            Assert.That(query.FindInfluence(state, id), Is.SameAs(expected));
            Assert.That(query.FindInfluence(state, id, snapshot), Is.SameAs(expected));
            Assert.That(service.FindInfluence(state, id), Is.SameAs(expected));
            Assert.That(service.FindInfluence(state, id, snapshot), Is.SameAs(expected));
        }

        [Test]
        public void FindInfluence_SnapshotKeepsStableIdentityAheadOfSlotAndFirstDuplicate()
        {
            var state = CreateState();
            var id = LocationSlot("A-01", 0);
            state.Map.Influences.Add(new InfluencePlacement { InfluenceId = "slot-match", SlotId = id });
            var identityMatch = new InfluencePlacement { InfluenceId = id, SlotId = RouteSlot("A1", 0) };
            state.Map.Influences.Add(identityMatch);
            state.Map.Influences.Add(new InfluencePlacement { InfluenceId = id, SlotId = LocationSlot("A-02", 0) });
            var query = CreateService();

            Assert.That(query.FindInfluence(state, id), Is.SameAs(identityMatch));
            Assert.That(query.FindInfluence(state, id, state.Map.Influences.ToArray()), Is.SameAs(identityMatch));
        }

        [Test]
        public void FindInfluence_SnapshotKeepsEarlierAliasAheadOfLaterExactSlot()
        {
            var state = CreateState();
            var alias = new InfluencePlacement { InfluenceId = "alias", SlotId = "A-01#0" };
            state.Map.Influences.Add(alias);
            state.Map.Influences.Add(new InfluencePlacement { InfluenceId = "exact", SlotId = LocationSlot("A-01", 0) });
            var query = CreateService();

            Assert.That(query.FindInfluence(state, LocationSlot("A-01", 0)), Is.SameAs(alias));
            Assert.That(query.FindInfluence(state, LocationSlot("A-01", 0), state.Map.Influences.ToArray()), Is.SameAs(alias));
        }

        [Test]
        public void FindInfluence_SnapshotPreservesLegacyIdentityFallback()
        {
            var state = CreateState();
            var placement = new InfluencePlacement { PlayerId = 1, SlotId = LocationSlot("A-01", 0) };
            state.Map.Influences.Add(placement);
            var id = InfluenceIdentity.GetStableId(state, placement);
            var query = CreateService();

            Assert.That(id, Is.Not.Empty);
            Assert.That(query.FindInfluence(state, id), Is.SameAs(placement));
            Assert.That(query.FindInfluence(state, id, state.Map.Influences.ToArray()), Is.SameAs(placement));
            Assert.That(placement.InfluenceId, Is.Empty, "查询不能写回旧实例身份");
        }

        [TestCase("missing")]
        [TestCase("")]
        [TestCase(null)]
        public void FindInfluence_NullSnapshotIsRejectedAndEmptySnapshotIsValid(string id)
        {
            var state = CreateState();
            var query = CreateService();
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            var empty = Array.Empty<InfluencePlacement>();

            Assert.That(Assert.Throws<ArgumentNullException>(() => query.FindInfluence(state, id, null)).ParamName, Is.EqualTo("placements"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => service.FindInfluence(state, id, null)).ParamName, Is.EqualTo("placements"));
            Assert.That(query.FindInfluence(state, id, empty), Is.Null);
            Assert.That(service.FindInfluence(state, id, empty), Is.Null);
        }

        [Test]
        public void FindInfluence_SnapshotPreservesStateValidationBeforeEmptyId()
        {
            var query = CreateService();
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            var empty = Array.Empty<InfluencePlacement>();
            Assert.That(Assert.Throws<ArgumentNullException>(() => query.FindInfluence(null, "")).ParamName, Is.EqualTo("state"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => query.FindInfluence(null, "", empty)).ParamName, Is.EqualTo("state"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => service.FindInfluence(null, "")).ParamName, Is.EqualTo("state"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => service.FindInfluence(null, "", null)).ParamName, Is.EqualTo("state"));
            var state = CreateState();
            state.Map = null;
            Assert.That(Assert.Throws<ArgumentException>(() => service.FindInfluence(state, "")).ParamName, Is.EqualTo("state"));
            Assert.That(Assert.Throws<ArgumentException>(() => service.FindInfluence(state, "", empty)).ParamName, Is.EqualTo("state"));
            Assert.That(query.FindInfluence(state, ""), Is.Null, "原查询的空 ID 不访问 Map");
        }

        [Test]
        public void FindInfluence_SnapshotQueriesDoNotRepeatProjectionOrCacheAcrossRefreshes()
        {
            var state = CreateState();
            var placement = new InfluencePlacement { InfluenceId = "influence.first", SlotId = LocationSlot("A-01", 0) };
            var current = new List<InfluencePlacement> { placement };
            var projectionReads = 0;
            state.Map.Influences.Bind(() => { projectionReads++; return current; });
            var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
            Assert.That(service.FindInfluence(state, ""), Is.Null);
            Assert.That(projectionReads, Is.Zero);
            var snapshot = state.Map.Influences.ToArray();
            foreach (var id in new[] { placement.InfluenceId, placement.SlotId, "A-01#0", "missing", "" })
                service.FindInfluence(state, id, snapshot);
            Assert.That(projectionReads, Is.EqualTo(1), "同一同步批次不能重新投影");

            current = new List<InfluencePlacement>();
            var nextSnapshot = state.Map.Influences.ToArray();
            Assert.That(service.FindInfluence(state, placement.InfluenceId, nextSnapshot), Is.Null);
            Assert.That(projectionReads, Is.EqualTo(2), "下一次刷新必须重新读取状态");
        }

        private static InfluenceQueryService CreateService()
        {
            return new InfluenceQueryService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap()));
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red, CityLocationId = "G-01" },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue, CityLocationId = "C-01" }
                }
            };
        }

        private static string LocationSlot(string locationId, int index)
        {
            return "location:" + locationId + ":" + index;
        }

        private static string RouteSlot(string routeId, int index)
        {
            return "route:" + routeId + ":" + index;
        }
    }
}
