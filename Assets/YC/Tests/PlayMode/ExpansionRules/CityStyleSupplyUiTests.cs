using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Domain.CityStyles;
using YC.Domain.Rules;
using Newtonsoft.Json.Linq;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class CityStyleSupplyUiTests
    {
        private HotseatProfilerSampler profiler;
        private HotseatUiInputDriver evidence;
        private Component panel;
        private GameState original;
        private bool completed;
        [UnityTearDown] public IEnumerator Cleanup()
        {
            evidence?.FlushRecords(); evidence = null;
            if (panel != null && original != null) GameplayUiReworkFixture.Call(panel, "Refresh", original, 1);
            if (profiler != null) { profiler.Finish(completed); UnityEngine.Object.Destroy(profiler.gameObject); }
            yield return null;
        }
        [UnityTest] public IEnumerator ThreePlayerScene_EnterpriseSupplyAndEmptySupplyRemainExact() => Verify(3);
        [UnityTest] public IEnumerator SampleScene_EnterpriseSupplyAndEmptySupplyRemainExact() => Verify(4);
        private IEnumerator Verify(int players)
        {
            completed = false; yield return GameplayUiReworkFixture.Load(players);
            var directory = GameplayUiReworkFixture.EvidenceDirectory("城市样式供应", players);
            profiler = HotseatProfilerSampler.Create(directory, false, 0, 0);
            var input = evidence = new HotseatUiInputDriver(directory, () => new { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path }, profiler);
            yield return GameplayUiReworkFixture.Resolution(new Vector2Int(900, 600), input);
            var controller = GameplayUiReworkFixture.Find("MobileCityInteractionController");
            original = (GameState)GameplayUiReworkFixture.Property(controller, "CurrentState");
            panel = GameplayUiReworkFixture.Find("BuildInfoPanel");
            var view = GameplayUiReworkFixture.Find("BuildInfoPanelView");
            var host = (RectTransform)GameplayUiReworkFixture.Property(view, "ExternalCityStyleArea");
            var ids = new[] { "city_style_city_construction_frontier", "city_style_pioneer_outpost", "city_style_core_commercial_area",
                "city_style_large_enterprise_park", "city_style_security_district", "city_style_pioneer_command_hub" };
            var infected = new[] { "infected.city_style.district_management", "infected.city_style.emerging_buildings", "infected.city_style.enterprise_offices" };
            var catalog = GameplayUiReworkFixture.Property(GameplayUiReworkFixture.Field(panel, "cardBoardVisualLayout"), "CityStyleVisuals");
            foreach (var id in infected)
            {
                var definition = JObject.Parse(File.ReadAllText(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core/city_style", id + ".json")));
                var mapping = GameplayUiReworkFixture.Call(catalog, "Find", id);
                Assert.That(mapping, Is.Not.Null);
                Assert.That(GameplayUiReworkFixture.Field(mapping, "artworkSpriteId"), Is.EqualTo((string)definition["artworkSpriteId"]));
            }
            var tabs = (Button[])GameplayUiReworkFixture.Field(GameplayUiReworkFixture.Find("UiMainActionTabs"), "tabs");
            yield return input.Click(() => tabs[2].gameObject, "城市样式页签");
            Func<Component[]> rows = () => host.GetComponentsInChildren(GameplayUiReworkFixture.Type("CityStyleStatusRowView"), true)
                .Cast<Component>().Where(c => c.gameObject.activeSelf).ToArray();
            var baseline = original.Decks.CityStyleSupply.ToArray();
            foreach (var supply in new[] { baseline, ids, infected, baseline.Concat(ids).Concat(infected).ToArray(), Array.Empty<string>() })
            {
                var state = Supply(original, supply);
                Assert.That(state.Decks.CityStyleSupply.ToArray(), Is.EqualTo(supply));
                GameplayUiReworkFixture.Call(panel, "Refresh", state, 1); yield return null;
                Assert.That(rows().Select(c => (string)GameplayUiReworkFixture.Property(c, "Id")).ToArray(), Is.EqualTo(supply),
                    "本局供应与实际活动行必须按顺序逐项相等，空供应不能回填");
                foreach (var row in rows())
                {
                    var id = (string)GameplayUiReworkFixture.Property(row, "Id");
                    var name = ((Text)GameplayUiReworkFixture.Field(row, "nameText")).text;
                    Assert.That(name, Is.EqualTo(CityStyleDatabase.Get(state, id).Name), "名称必须来自本局冻结定义");
                    Assert.That(name, Is.Not.EqualTo(id), "稳定 ID 不能替代中文名称");
                }
                input.Record("受控实体供应", new { ids = supply, count = rows().Length,
                    names = rows().Select(row => ((Text)GameplayUiReworkFixture.Field(row, "nameText")).text).ToArray(),
                    note = "只改克隆的实际实体/订单及本局冻结定义；感染者为真实定义展示目录验证，不开启正式组合" });
                yield return input.Capture("供应-" + supply.Length + "项-顶部", 1);
                if (supply.Length == 0) continue;
                var scroll = host.GetComponentInParent<ScrollRect>(); Assert.That(scroll, Is.Not.Null);
                var last = (RectTransform)rows().Last().transform;
                bool visible = false;
                for (int i = 0; i < 80; i++)
                {
                    var r = GameplayUiReworkFixture.ScreenRect(last); var v = GameplayUiReworkFixture.ScreenRect(scroll.viewport);
                    visible = r.yMin >= v.yMin - 1 && r.yMax <= v.yMax + 1;
                    if (visible) break;
                    yield return GameplayUiReworkFixture.Wheel(scroll, new Vector2(0, -3), input);
                }
                Assert.That(visible, Is.True, "末行必须能通过真实滚轮完整进入视口");
                input.Record("末行可达", new { id = supply.Last(), row = GameplayUiReworkFixture.ScreenRect(last),
                    viewport = GameplayUiReworkFixture.ScreenRect(scroll.viewport) });
                yield return input.Capture("供应-" + supply.Length + "项-底部", 1);
            }
            var missing = Supply(original, new[] { "city_style_missing_display_mapping" });
            var error = Assert.Throws<TargetInvocationException>(() => GameplayUiReworkFixture.Call(panel, "Refresh", missing, 1));
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain("city_style_missing_display_mapping"));
            Assert.That(rows(), Is.Empty, "缺映射不能显示另一张默认基础样式");
            completed = true;
        }
        private static GameState Supply(GameState source, string[] definitions)
        {
            var state = GameStateCloneService.DeepClone(source);
            if (state.ContentDefinitions == null) state.ContentDefinitions = new GameContentDefinitions();
            foreach (var id in definitions)
            {
                // 缺展示映射用例仍有合法定义，避免提前在定义校验处失败。
                var sourceId = id == "city_style_missing_display_mapping" ? "city_style_city_construction_frontier" : id;
                var path = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core/city_style", sourceId + ".json");
                var content = JObject.Parse(File.ReadAllText(path));
                var definition = content["data"].ToObject<CityStyleDefinition>();
                Assert.That(definition.CityStyleId, Is.EqualTo(sourceId));
                Assert.That(definition.Name, Is.EqualTo((string)content["displayName"]));
                definition.CityStyleId = id;
                state.ContentDefinitions.CityStyles.RemoveAll(existing => existing.CityStyleId == id);
                state.ContentDefinitions.CityStyles.Add(definition);
            }
            Assert.That(ItemZoneLegacyProjection.IsActive(state), Is.True);
            var zone = state.ItemZones.Zones.Single(z => z.LocalZoneId == "city_styles");
            var retired = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired");
            var previous = state.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId).ToArray();
            var type = previous.FirstOrDefault()?.TypeId ?? "core.board_card";
            foreach (var item in previous)
            {
                foreach (var order in state.ItemZones.Orders) order.ItemIds.Remove(item.ItemId);
                item.ZoneId = retired.ZoneId; item.SlotId = string.Empty;
                state.ItemZones.Orders.Find(o => o.ZoneId == retired.ZoneId)?.ItemIds.Add(item.ItemId);
            }
            var suppliedOrder = state.ItemZones.Orders.Find(o => o.ZoneId == zone.ZoneId);
            for (int i = 0; i < definitions.Length; i++)
            {
                var item = new ItemInstanceState { ItemId = "ui-style-fixture-" + i, DefinitionId = definitions[i], TypeId = type,
                    PhysicalKind = ItemPhysicalKind.Card, ZoneId = zone.ZoneId, OriginalSupplyZoneId = string.Empty };
                state.ItemZones.Items.Add(item); suppliedOrder?.ItemIds.Add(item.ItemId);
            }
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
            return state;
        }
    }
}
