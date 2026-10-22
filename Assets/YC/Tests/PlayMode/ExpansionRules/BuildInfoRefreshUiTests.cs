using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class BuildInfoRefreshUiTests
    {
        private GameplayPageAcceptanceFixture f;
        private GameState original;
        private Component Panel => (Component)Read(f.Gameplay, "buildInfoPanel");
        private int Viewer => (int)Read(f.Gameplay, "localPlayerId");
        private object[] Bindings(string field) => ((IEnumerable)Read(Panel, field)).Cast<object>().ToArray();
        private Dictionary<string, Component> Rows() => Bindings("cityStyleRows").ToDictionary(
            b => (string)Read(b, "Id"), b => (Component)Read(b, "Row"));

        [UnityTearDown]
        public IEnumerator RestoreOriginalState()
        {
            if (f?.Session != null && original != null && f.Gameplay != null)
            {
                f.Session.ReplaceState(original);
                Call(f.Gameplay, "SynchronizeInteractionFromState");
                yield return Settle();
            }
        }

        private IEnumerator Start()
        {
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            original = GameStateCloneService.DeepClone(f.State);
            Assert.That(Panel, Is.Not.Null);
            f.Record("建设刷新测试边界", new
            {
                scope = "真实四人企业开局；后续为明确制备的状态副本，经 Session.ReplaceState 和正常同步刷新实际场景组件。不是正常开局已获得这些设施或宣告标记。",
                panel = HotseatUiInputDriver.PathOf(Panel.gameObject),
                note = "只记录存活对象实例身份增减，不把净数量或存活集合等同于 Instantiate 调用次数；不激活隐藏旧拖放区。"
            });
        }

        private IEnumerator Present(GameState state)
        {
            f.Session.ReplaceState(state);
            Call(f.Gameplay, "SynchronizeInteractionFromState");
            yield return Settle();
            Assert.That(Read(Panel, "currentState"), Is.SameAs(f.State), "必须刷新实际订阅的场景面板");
        }

        private Dictionary<string, int> DynamicRoots()
        {
            var result = Rows().ToDictionary(p => "样式:" + p.Key, p => p.Value.GetInstanceID());
            foreach (var group in new[] { "cityBoardSlotBindings", "facilityCardBindings" })
            {
                var bindings = Bindings(group);
                for (int index = 0; index < bindings.Length; index++)
                {
                    var item = (Component)Read(bindings[index], "Item");
                    if (item != null) result.Add(group + ":" + index, item.GetInstanceID());
                }
            }
            return result;
        }

        private Dictionary<string, Component> ObjectRoots()
        {
            var roots = Rows().ToDictionary(p => "样式:" + p.Key, p => p.Value);
            roots.Add("面板", Panel);
            foreach (var group in new[] { "cityBoardSlotBindings", "facilityCardBindings" })
            {
                var bindings = Bindings(group);
                for (int index = 0; index < bindings.Length; index++)
                {
                    var item = (Component)Read(bindings[index], "Item");
                    if (item != null) roots.Add(group + ":" + index, item);
                }
            }
            return roots;
        }

        private HashSet<int> LivingObjects() => new HashSet<int>(ObjectRoots().Values
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject.GetInstanceID()));

        private void RecordObjectCoverage(string label) => f.Record(label, ObjectRoots().Select(p => new
        {
            group = p.Key, path = HotseatUiInputDriver.PathOf(p.Value.gameObject),
            insidePanel = p.Value.transform.IsChildOf(Panel.transform),
            objects = p.Value.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject.GetInstanceID()).ToArray()
        }).ToArray());

        private void AssertUnchanged(string label, Dictionary<string, int> roots, HashSet<int> objects)
        {
            var after = LivingObjects();
            var added = after.Except(objects).ToArray();
            var removed = objects.Except(after).ToArray();
            f.Record(label, new { before = roots, after = DynamicRoots(), added, removed,
                metric = "稳定帧间存活实例 ID 集合差；不是分配计数或耗时指标" });
            Assert.That(DynamicRoots(), Is.EquivalentTo(roots), label + "替换了动态根实例");
            Assert.That(added, Is.Empty, label + "新增了存活对象");
            Assert.That(removed, Is.Empty, label + "销毁了原对象");
        }

        [UnityTest]
        public IEnumerator SameDataAndSameRevisionMarkersKeepInstancesAndRefreshVisibleCounts()
        {
            yield return Start();
            var tabs = Active("UiMainActionTabs").Single();
            yield return f.Click(((Button[])Read(tabs, "tabs"))[2].gameObject, "打开真实城市样式区");
            var roots = DynamicRoots();
            var objects = LivingObjects();
            Assert.That(Rows(), Is.Not.Empty, "需要真实动态样式行，不能以空集合通过");
            for (int i = 0; i < 3; i++)
            {
                yield return Present(GameStateCloneService.DeepClone(f.State));
                AssertUnchanged("同数据重复刷新_" + i, roots, objects);
            }

            var row = Rows().First(p => ((IEnumerable)Read(p.Value, "slots")).Cast<object>()
                .Any(s => (string)Read(s, "area") == "unused"));
            var slots = ((IEnumerable)Read(row.Value, "slots")).Cast<object>().ToArray();
            var slot = slots.First(s => (string)Read(s, "area") == "unused");
            var state = GameStateCloneService.DeepClone(f.State);
            foreach (var player in state.Players) player.DeclaredCityStyles.Clear();
            var owner = state.FindPlayer(Viewer);
            var revision = state.EffectRuntime.StateRevision;
            // 显示夹具中的声明 DTO，没有调用宣告行为，也不冒充规则结算结果。
            owner.DeclaredCityStyles.Add(new CityStyleDeclarationState
            { CityStyleId = row.Key, MarkerArea = "unused", InfluenceMarkerId = "display-marker" });
            yield return Present(state);
            Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
            AssertUnchanged("同 revision 标记更新", roots, objects);
            var block = ((Image[])Read(slot, "blocks"))[(int)owner.Color];
            var marker = block.GetComponent(T("InfluenceMarker2DView"));
            Assert.That(marker, Is.Not.Null, "读取实际编号标记组件");
            var number = (Text)Read(marker, "NumberText");
            Assert.That(number.text, Is.EqualTo("1"));
            Assert.That(number.gameObject.activeInHierarchy && number.color.a > 0, Is.True);
            Assert.That(number.cachedTextGenerator.vertexCount, Is.GreaterThan(0), "必须实际生成数字字形");
            yield return f.Capture("同修订号标记实际刷新", row.Value);

            var cleared = GameStateCloneService.DeepClone(f.State);
            cleared.FindPlayer(Viewer).DeclaredCityStyles.Clear();
            yield return Present(cleared);
            Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
            AssertUnchanged("同 revision 清除标记", roots, objects);
            Assert.That(number.text, Is.EqualTo("0"));
            Assert.That(number.color.a, Is.Zero);
            Assert.That(((Image)Read(marker, "Face")).color.a, Is.Zero);
        }

        [UnityTest]
        public IEnumerator RawFacilityReplacementAndSupplyOrderRefreshWithoutUnrelatedInstanceChurn()
        {
            yield return Start();
            var state = GameStateCloneService.DeepClone(f.State);
            var deck = state.ItemZones.Zones.Single(z => z.LocalZoneId == "facility_deck");
            var pair = state.ItemZones.Items.Where(i => i.TypeId == "core.facility_card")
                .GroupBy(i => i.DefinitionId).First(g => g.Count() >= 2).Take(2).ToArray();
            Assert.That(pair[0].ItemId, Is.Not.EqualTo(pair[1].ItemId));
            f.Record("同定义真实设施输入来源", new { items = pair.Select(i => new { i.ItemId, i.DefinitionId, i.ZoneId }).ToArray(),
                scope = "从本局全部设施区域取相同定义的真实原始实体，起始设施 reserve_001 有每玩家一份；仅在显示状态副本中移位，不合成副本，不称建造经历" });
            var cell = state.ItemZones.Zones.First(z => z.OwnerPlayerId == Viewer &&
                z.TemplateId == "core.zone.city_facility_cell" && !state.ItemZones.Items.Any(i => i.ZoneId == z.ZoneId));
            var slotIndex = int.Parse(cell.LocalZoneId.Substring("city_cell_".Length));
            MoveDisplayItem(state, pair[0], cell);
            yield return Present(state);
            var roots = DynamicRoots();
            var oldObjects = LivingObjects();
            var oldCard = (Component)Read(Bindings("cityBoardSlotBindings").Single(b => (int)Read(b, "SlotIndex") == slotIndex), "Item");
            Assert.That(oldCard, Is.Not.Null);
            var oldCardObjectId = oldCard.gameObject.GetInstanceID();
            CollectionAssert.Contains(oldObjects, oldCardObjectId, "统计必须包含实际绑定卡根，不局限于控制器子树");
            RecordObjectCoverage("替换前实际绑定子树");

            var changed = GameStateCloneService.DeepClone(f.State);
            var revision = changed.EffectRuntime.StateRevision;
            MoveDisplayItem(changed, changed.ItemZones.Items.Single(i => i.ItemId == pair[0].ItemId), deck);
            MoveDisplayItem(changed, changed.ItemZones.Items.Single(i => i.ItemId == pair[1].ItemId), cell);
            yield return Present(changed);
            var newRoots = DynamicRoots();
            var binding = Bindings("cityBoardSlotBindings").Single(b => (int)Read(b, "SlotIndex") == slotIndex);
            var newCard = (Component)Read(binding, "Item");
            Assert.That(f.State.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That((string)Read(binding, "ContentInstanceId"), Is.EqualTo(pair[1].ItemId));
            Assert.That((string)Read(binding, "DefinitionId"), Is.EqualTo(pair[0].DefinitionId));
            Assert.That(newCard.GetInstanceID(), Is.Not.EqualTo(roots["cityBoardSlotBindings:" + slotIndex]));
            Assert.That(oldCard == null, Is.True, "退休卡实例必须在正常帧末销毁");
            foreach (var entry in roots.Where(p => p.Key != "cityBoardSlotBindings:" + slotIndex))
                Assert.That(newRoots[entry.Key], Is.EqualTo(entry.Value), "无关动态对象被替换：" + entry.Key);
            var living = LivingObjects();
            var added = living.Except(oldObjects).ToArray();
            var removed = oldObjects.Except(living).ToArray();
            Assert.That(added, Is.Not.Empty); Assert.That(removed, Is.Not.Empty);
            CollectionAssert.Contains(added, newCard.gameObject.GetInstanceID());
            CollectionAssert.Contains(removed, oldCardObjectId);
            RecordObjectCoverage("替换后实际绑定子树");
            f.Record("同定义不同 raw 实体替换", new { beforeItem = pair[0].ItemId, afterItem = pair[1].ItemId,
                pair[0].DefinitionId, slotIndex, revision, before = roots, after = newRoots, added, removed });

            roots = DynamicRoots(); oldObjects = LivingObjects();
            var reordered = GameStateCloneService.DeepClone(f.State);
            var supply = reordered.ItemZones.Zones.Single(z => z.LocalZoneId == "city_styles");
            var ids = new ItemZoneQuery().GetItemIds(reordered, supply.ZoneId).Reverse().ToList();
            var order = reordered.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == supply.ZoneId);
            if (order == null) reordered.ItemZones.Orders.Add(order = new ZoneOrderState { ZoneId = supply.ZoneId });
            order.ItemIds = ids;
            var expected = reordered.Decks.CityStyleSupply.Select((id, index) => new { id, index,
                level = CityStyleDatabase.Get(reordered, id).Level }).OrderBy(x => x.level == 2 ? 1 : 0)
                .ThenBy(x => x.index).Select(x => x.id).ToArray();
            var levels = expected.Select(id => CityStyleDatabase.Get(reordered, id).Level).ToArray();
            CollectionAssert.Contains(levels, 1);
            CollectionAssert.Contains(levels, 2);
            Assert.That(expected, Is.Not.EqualTo(Rows().Keys.ToArray()), "输入必须实际改变供应顺序");
            yield return Present(reordered);
            AssertUnchanged("供应重排复用原行", roots, oldObjects);
            Assert.That(Bindings("cityStyleRows").Select(b => (string)Read(b, "Id")), Is.EqualTo(expected));
            Assert.That(Rows().OrderBy(p => p.Value.transform.GetSiblingIndex()).Select(p => p.Key), Is.EqualTo(expected),
                "实际 Transform 顺序必须一级在前、二级在后并保持组内供应顺序");
            f.Record("实际供应顺序", new { rawOrder = ids, displayedDefinitions = expected, revision });
        }

        private static void MoveDisplayItem(GameState state, ItemInstanceState item, ZoneInstanceState destination)
        {
            // 仅制备副本显示数据：真实 raw Item 身份不变，更新其区域及顺序；不声称经过设施支付。
            foreach (var order in state.ItemZones.Orders) order.ItemIds.Remove(item.ItemId);
            item.ZoneId = destination.ZoneId;
            item.SlotId = destination.LegalSlotIds.FirstOrDefault() ?? string.Empty;
            state.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == destination.ZoneId)?.ItemIds.Add(item.ItemId);
        }
    }
}
