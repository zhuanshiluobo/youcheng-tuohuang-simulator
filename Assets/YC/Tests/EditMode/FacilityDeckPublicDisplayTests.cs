using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class FacilityDeckPublicDisplayTests
    {
        [Test]
        public void HiddenDeckPublishesFourColorTotalsWithoutCardIdentitiesAndRefreshesTheActualPrefab()
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            var configuration = new GameRuleConfiguration { PlayerCount = 4, MapId = StaticMapDefinitions.FourPlayerMapId,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                Enumerable.Range(1, 4).Select(id => new PlayerSeat { PlayerId = id }).ToList(), configuration, 197,
                catalog.SetupRecipe, catalog.ContentDefinitions);
            var deck = state.ItemZones.Zones.Single(zone => zone.LocalZoneId == "facility_deck");
            var colors = state.ContentDefinitions.Facilities.ToDictionary(value => value.FacilityId, value => value.Color);
            foreach (string color in new[] { "red", "yellow", "blue", "rainbow" })
            {
                var item = state.ItemZones.Items.First(value => value.TypeId == "core.facility_card" &&
                    colors.TryGetValue(value.DefinitionId, out var actual) && actual == color);
                if (item.ZoneId == deck.ZoneId) continue;
                foreach (var order in state.ItemZones.Orders) order.ItemIds.Remove(item.ItemId);
                item.ZoneId = deck.ZoneId; item.SlotId = string.Empty;
                state.ItemZones.Orders.Single(order => order.ZoneId == deck.ZoneId).ItemIds.Add(item.ItemId);
            }
            var expected = new[] { "red", "yellow", "blue", "rainbow" }.Select(color =>
                state.ItemZones.Items.Count(item => item.ZoneId == deck.ZoneId && colors.TryGetValue(item.DefinitionId, out var actual) && actual == color)).ToArray();
            Assert.That(expected.All(count => count > 0), Is.True, "隔离夹具必须实际覆盖四色。");
            var view = GameStateViewProjector.ProjectForPlayer(state, 1);
            int[] Counts(PublicDeckStateView value) => new[] { value.FacilityDeckRedCount, value.FacilityDeckYellowCount,
                value.FacilityDeckBlueCount, value.FacilityDeckRainbowCount };
            Assert.That(Counts(view.Decks), Is.EqualTo(expected));
            Assert.That(view.Decks.FacilityDeckCount, Is.EqualTo(state.ItemZones.Items.Count(item => item.ZoneId == deck.ZoneId)));
            Assert.That(view.ItemZones.Items.Any(item => item.ZoneId == deck.ZoneId), Is.False, "不能公开隐藏牌堆的实例或顺序。");
            Assert.That(Counts(GameStateViewProjector.ProjectForPlayer(state, 2).Decks), Is.EqualTo(expected));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/FacilityBuildDialog.prefab");
            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                instance.SetActive(true);
                var type = Type.GetType("YC.Presentation.FacilityBuildDialogView, Assembly-CSharp", true);
                var page = instance.GetComponent(type);
                var client = GameStateViewProjector.ToClientState(view);
                Assert.That(client.ItemZones.Items.Any(item => item.ZoneId == deck.ZoneId), Is.False);
                type.GetMethod("RefreshSupplyStatus").Invoke(page, new object[] { client, 1, view.Decks });
                var label = (Text)type.GetField("supplyDeckText", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                var documentView = type.GetField("supplyDeckDocument", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                var documentField = documentView.GetType().GetField("document", BindingFlags.Instance | BindingFlags.NonPublic);
                var document = (GameplayTextDocument)documentField.GetValue(documentView);
                var numbers = document.Runs.OfType<NumberRun>().ToArray();
                Assert.That(numbers.Select(number => number.Value), Is.EqualTo(expected.Select(value => (long)value)));
                Assert.That(numbers.All(number => number.Style.SemanticRole == "UiNumber" && number.Style.WeightMode == "Normal"), Is.True);
                Assert.That(document.ToPlainText(), Is.EqualTo(label.text));
                var style = documentView.GetType().GetField("style", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(documentView);
                var roles = style.GetType().GetProperty("FontRoles").GetValue(style);
                var configuredFont = roles.GetType().GetProperty("UiNumber").GetValue(roles);
                Assert.That(configuredFont, Is.SameAs(AssetDatabase.LoadAssetAtPath<Font>("Assets/YC/Presentation/CommonUi/Fonts/Novecento wide Normal Regular.ttf")));
                var rainbow = state.ItemZones.Items.First(item => item.ZoneId == deck.ZoneId && colors[item.DefinitionId] == "rainbow");
                var retired = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.box_retired");
                foreach (var order in state.ItemZones.Orders) order.ItemIds.Remove(rainbow.ItemId);
                rainbow.ZoneId = retired.ZoneId;
                state.ItemZones.Orders.SingleOrDefault(order => order.ZoneId == retired.ZoneId)?.ItemIds.Add(rainbow.ItemId);
                var refreshed = GameStateViewProjector.ProjectForPlayer(state, 1);
                Assert.That(refreshed.Decks.FacilityDeckRainbowCount, Is.EqualTo(expected[3] - 1));
                Assert.That(refreshed.Decks.FacilityDeckCount, Is.EqualTo(view.Decks.FacilityDeckCount - 1));
                type.GetMethod("RefreshSupplyStatus").Invoke(page, new object[] { GameStateViewProjector.ToClientState(refreshed), 1, refreshed.Decks });
                var updated = ((GameplayTextDocument)documentField.GetValue(documentView)).Runs.OfType<NumberRun>().Select(number => number.Value);
                Assert.That(updated, Is.EqualTo(new long[] { expected[0], expected[1], expected[2], expected[3] - 1 }));
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
