using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ReadOnlyStateQueryScopeTests
    {
        private static GameState OrderState()
        {
            var state = new GameState();
            state.ItemZones.Zones.AddRange(new[] { new ZoneInstanceState { ZoneId = "ordered" }, new ZoneInstanceState { ZoneId = "unordered" } });
            state.ItemZones.Items.AddRange(new[] {
                new ItemInstanceState { ItemId = "二", ZoneId = "ordered" }, new ItemInstanceState { ItemId = "一", ZoneId = "ordered" },
                new ItemInstanceState { ItemId = "z", ZoneId = "unordered" }, new ItemInstanceState { ItemId = "a", ZoneId = "unordered" } });
            state.ItemZones.Orders.Add(new ZoneOrderState { ZoneId = "ordered", OrderRevision = 9, ItemIds = new List<string> { "一", "二" } });
            return state;
        }

        [Test]
        public void IndexedOrderDigestsKeepOriginalBytesAndCannotSurviveTheNextReadScope()
        {
            var state = OrderState();
            var ordered = FacilityLayerQuery.Digest(state, "ordered"); var unordered = FacilityLayerQuery.Digest(state, "unordered");
            // 无关区域/顺序的重复ID仍只在真正查询它们时拒绝。
            state.ItemZones.Zones.AddRange(new[] { new ZoneInstanceState { ZoneId = "unused" }, new ZoneInstanceState { ZoneId = "unused" } });
            using (ReadOnlyStateQueryScope.Begin(state))
            {
                Assert.That(FacilityLayerQuery.Digest(state, "ordered"), Is.EqualTo(ordered));
                Assert.That(FacilityLayerQuery.Digest(state, "unordered"), Is.EqualTo(unordered));
                Assert.That(ReadOnlyStateQueryScope.IsActiveFor(new GameState()), Is.False);
            }
            state.ItemZones.Orders[0].ItemIds.Reverse(); state.ItemZones.Orders[0].OrderRevision++;
            var changed = FacilityLayerQuery.Digest(state, "ordered");
            Assert.That(changed, Is.Not.EqualTo(ordered));
            using (ReadOnlyStateQueryScope.Begin(state))
                Assert.That(FacilityLayerQuery.Digest(state, "ordered"), Is.EqualTo(changed), "即使revision未提交，下一次查询也必须重新读取");
            Assert.That(ReadOnlyStateQueryScope.IsActiveFor(state), Is.False);
        }

        [TestCase("membership")]
        [TestCase("duplicate_item")]
        [TestCase("negative_revision")]
        [TestCase("duplicate_order")]
        [TestCase("duplicate_zone")]
        [TestCase("missing_zone")]
        [TestCase("missing_zone_and_null_orders")]
        [TestCase("duplicate_zone_and_null_items")]
        public void IndexedOrderDigestsKeepTheOriginalMalformedStateRejections(string damage)
        {
            var state = OrderState();
            switch (damage)
            {
                case "membership": state.ItemZones.Orders[0].ItemIds.RemoveAt(0); break;
                case "duplicate_item": state.ItemZones.Orders[0].ItemIds.Add("一"); break;
                case "negative_revision": state.ItemZones.Orders[0].OrderRevision = -1; break;
                case "duplicate_order": state.ItemZones.Orders.Add(new ZoneOrderState { ZoneId = "ordered" }); break;
                case "duplicate_zone": state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "ordered" }); break;
                case "missing_zone": state.ItemZones.Zones.RemoveAt(0); break;
                case "missing_zone_and_null_orders": state.ItemZones.Zones.RemoveAt(0); state.ItemZones.Orders = null; break;
                case "duplicate_zone_and_null_items": state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "ordered" }); state.ItemZones.Items = null; break;
            }
            var expected = Assert.Throws<InvalidOperationException>(() => FacilityLayerQuery.Digest(state, "ordered"));
            using (ReadOnlyStateQueryScope.Begin(state))
                Assert.That(Assert.Throws<InvalidOperationException>(() => FacilityLayerQuery.Digest(state, "ordered")).Message, Is.EqualTo(expected.Message));
        }

        [TestCase(null)]
        [TestCase("")]
        public void MissingFacilityIdCannotSelectFirstAvailableCandidate(string facilityId)
        {
            var selection=new YC.Presentation.BuildFacilitySelectionController();selection.Begin(1);
            Assert.That(selection.TryBeginDrag(new GameState(),facilityId,out _),Is.False);
            Assert.That(selection.FacilityId,Is.Empty);
        }
        [Test]
        public void WorkCopiesNewCallsRevisionChangesAndNestedLifetimesCannotUseOtherSnapshots()
        {
            var state=new GameState();var other=new GameState();var key=new object();var value=new object();
            Assert.That(ReadOnlyStateQueryScope.TryRead<object>(state,key,out _),Is.False);
            using(var outer=ReadOnlyStateQueryScope.Begin(state))
            {
                ReadOnlyStateQueryScope.Store(state,key,value);
                Assert.That(ReadOnlyStateQueryScope.TryRead<object>(state,key,out var found),Is.True);Assert.That(found,Is.SameAs(value));
                Assert.That(ReadOnlyStateQueryScope.TryRead<object>(other,key,out _),Is.False);
                using(var inner=ReadOnlyStateQueryScope.Begin(other))
                {
                    Assert.That(ReadOnlyStateQueryScope.TryRead<object>(state,key,out _),Is.False);
                    outer.Dispose();
                }
                Assert.That(ReadOnlyStateQueryScope.TryRead<object>(state,key,out _),Is.False);
            }
            using(ReadOnlyStateQueryScope.Begin(state))
            {
                Assert.That(ReadOnlyStateQueryScope.TryRead<object>(state,key,out _),Is.False);
                ReadOnlyStateQueryScope.Store(state,key,value);state.EffectRuntime.StateRevision++;
                Assert.That(ReadOnlyStateQueryScope.TryRead<object>(state,key,out _),Is.False);
            }
            Assert.Throws<InvalidOperationException>(()=> {
                using(ReadOnlyStateQueryScope.Begin(state))
                {ReadOnlyStateQueryScope.Store(state,key,value);throw new InvalidOperationException();}
            });
            Assert.That(ReadOnlyStateQueryScope.TryRead<object>(state,key,out _),Is.False);
        }
    }
}
