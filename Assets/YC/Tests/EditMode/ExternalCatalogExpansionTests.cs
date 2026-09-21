using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class ExternalCatalogExpansionTests
    {
        private List<FacilityCardDefinition> facilities;
        private List<EventCardDefinition> events;
        private List<CityStyleDefinition> styles;
        private List<SpecialActionDefinition> actions;
        [SetUp] public void CaptureCatalogs()
        {
            facilities = FacilityCardDatabase.All.ToList();
            events = new[] { EventColor.Green, EventColor.Red, EventColor.Yellow }.SelectMany(EventCardDatabase.GetCardIds).Select(EventCardDatabase.Get).ToList();
            styles = CityStyleDatabase.All.ToList(); actions = SpecialActionDatabase.All.ToList();
        }
        private static void Reset(Type type)
        {
            if (type == typeof(FacilityCardDatabase))
            {
                ((IDictionary)type.GetField("Definitions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Clear();
                type.GetField("injectedDefinitions", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
                return;
            }
            type.GetMethod("ResetForTests", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }
        [TearDown] public void RestoreCatalogs()
        {
            Reset(typeof(FacilityCardDatabase)); FacilityCardDatabase.Initialize(facilities);
            Reset(typeof(EventCardDatabase)); EventCardDatabase.Initialize(events);
            Reset(typeof(CityStyleDatabase)); CityStyleDatabase.Initialize(styles);
            Reset(typeof(SpecialActionDatabase)); SpecialActionDatabase.Initialize(actions);
        }
        private static string InventoryPack()
        {
            string root = Path.GetFullPath(Path.Combine("Logs/ExternalContentTests", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
            string core = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
            var template = JObject.Parse(File.ReadAllText(Path.Combine(core, "templates/facilities/gain_iron_4.json")));
            template["data"]["effectId"] = "external.custom.reward"; template["data"]["effectScript"] = "reward.lua";
            File.WriteAllText(Path.Combine(root, "template.json"), template.ToString());
            File.WriteAllText(Path.Combine(root, "reward.lua"), "return function(ctx) return { Effect.GainResource({recipient=ctx.playerId,resourceType='iron',amount=7}) } end");
            File.Copy(Path.Combine(core, "artwork/facility/building_001.jpg"), Path.Combine(root, "card.jpg"));
            var definition = new JObject { ["contentType"] = "facility", ["displayName"] = "扩展设施", ["version"] = "1", ["expansionId"] = "test",
                ["replaces"] = JValue.CreateNull(), ["abilities"] = new JArray(), ["data"] = new JObject(), ["dataTemplate"] = "template.json" };
            var inventory = new JObject { ["groups"] = new JArray(new JObject { ["idPrefix"] = "external", ["definition"] = definition,
                ["variants"] = new JArray(new JObject { ["color"] = "blue", ["count"] = 2, ["artwork"] = "card.jpg" },
                    new JObject { ["color"] = "yellow", ["count"] = 1, ["artwork"] = "card.jpg" }) }) };
            File.WriteAllText(Path.Combine(root, "inventory.json"), inventory.ToString());
            File.WriteAllText(Path.Combine(root, "pack.json"), new JObject { ["schemaVersion"] = 2, ["packId"] = "test", ["version"] = "1",
                ["definitions"] = new JArray(), ["facilityInventory"] = "inventory.json" }.ToString());
            return root;
        }
        [Test]
        public void GeneratedFacilityInventory_AcceptsNewIdsAndEffectAndRunsLuaAfterRecovery()
        {
            var pack = ExternalContentPack.Load(InventoryPack()); Reset(typeof(FacilityCardDatabase));
            FacilityCardDatabase.InitializeExternal(pack.CreateFacilities());
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Is.EqualTo(new[] { "external_blue_001", "external_blue_002", "external_yellow_001" }));
            var registry = new EffectRegistry(); new RoundExecutionService(registry); ResourceEffectExecutor.Register(registry);
            FacilityEntryEffectExecutor.Register(registry); FacilityLuaCatalog.Register(registry, pack); CharacterCardLuaCatalog.Register(registry, pack);
            var state = new GameState { GameId = "external-facility", Round = 1, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1 };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            var placement = new FacilityPlacement { PlayerId = 1, FacilityCardId = "external_blue_001", CityBoardSlotIndex = 0 };
            state.Map.Facilities.Add(placement); FacilityInstanceStateService.EnsureIdentity(state, placement);
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(FacilityEntryEffectSpecFactory.Activate(1, placement.FacilityCardId, placement.ContentInstanceId, 0));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, executor.LastDiagnostic);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(7));
            var saved = GameStateCloneService.DeepClone(state);
            Assert.That(new EffectTreeExecutor(saved, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(saved.FindPlayer(1).Resources.Iron, Is.EqualTo(7));
        }
        [Test]
        public void InventoryQuantityChange_KeepsExistingIdsAndChangesHash()
        {
            string root = InventoryPack(); var first = ExternalContentPack.Load(root);
            string path = Path.Combine(root, "inventory.json"); var inventory = JObject.Parse(File.ReadAllText(path));
            inventory["groups"][0]["variants"][0]["count"] = 3; File.WriteAllText(path, inventory.ToString());
            var next = ExternalContentPack.Load(root);
            Assert.That(next.CreateFacilities().Select(d => d.FacilityId), Does.Contain("external_blue_003"));
            Assert.That(first.CreateFacilities().All(d => next.CreateFacilities().Any(n => n.FacilityId == d.FacilityId)), Is.True);
            Assert.That(next.ContentHash, Is.Not.EqualTo(first.ContentHash));
        }
        [TestCase("negative")]
        [TestCase("fraction")]
        [TestCase("color")]
        [TestCase("duplicate_id")]
        [TestCase("too_many")]
        public void InvalidInventory_IsRejected(string fault)
        {
            string root = InventoryPack(); string path = Path.Combine(root, "inventory.json"); var inventory = JObject.Parse(File.ReadAllText(path));
            var variant = inventory["groups"][0]["variants"][0];
            if (fault == "negative") variant["count"] = -1;
            if (fault == "fraction") variant["count"] = 1.5;
            if (fault == "color") variant["color"] = "unknown";
            if (fault == "too_many") variant["count"] = 513;
            if (fault == "duplicate_id") variant["instances"] = new JArray(new JObject { ["id"] = "same" }, new JObject { ["id"] = "same" });
            File.WriteAllText(path, inventory.ToString()); Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root));
        }
        [Test]
        public void EventCatalog_UsesExternalColorPoolsInsteadOfCanonicalIds()
        {
            var card = JObject.FromObject(events.First(e => e.Color == EventColor.Green)).ToObject<EventCardDefinition>(); card.CardId = "external-event";
            Reset(typeof(EventCardDatabase)); EventCardDatabase.InitializeExternal(new[] { card });
            Assert.That(EventCardDatabase.GetCardIds(EventColor.Green), Is.EqualTo(new[] { "external-event" }));
            Assert.That(EventCardDatabase.GetCardIds(EventColor.Red), Is.Empty);
            var registry = new EffectRegistry(); new RoundExecutionService(registry); EventCardLuaCatalog.Register(registry);
            Assert.That(registry.HasEventHandler("lua:event-card:external-event"), Is.True);
            var decks = new DeckRuntimeState(); var service = new EventDeckService(1);
            service.InitializeDecks(decks, EventCardDatabase.GetCardIds(EventColor.Green), EventCardDatabase.GetCardIds(EventColor.Yellow), EventCardDatabase.GetCardIds(EventColor.Red));
            Assert.That(service.Draw(decks, EventColor.Green), Is.EqualTo("external-event"));
        }
        [Test]
        public void CityStyleAndActionCatalogs_AllowNewIdsAndExternalCosts()
        {
            var style = JObject.FromObject(styles.First(s => s.SpecialActionId != "")).ToObject<CityStyleDefinition>(); var action = actions.First(a => a.SpecialActionId == style.SpecialActionId).Clone();
            style.CityStyleId = "external-style"; style.SpecialActionId = "external-action";
            action.SpecialActionId = "external-action"; action.CityStyleId = style.CityStyleId; action.FixedCost.GoldVoucher = 2;
            Reset(typeof(CityStyleDatabase)); Reset(typeof(SpecialActionDatabase));
            CityStyleDatabase.InitializeExternal(new[] { style }); SpecialActionDatabase.InitializeExternal(new[] { action });
            Assert.That(CityStyleDatabase.DefaultSupplyIds, Is.EqualTo(new[] { "external-style" }));
            Assert.That(CityStyleDatabase.All, Has.Count.EqualTo(1)); Assert.That(SpecialActionDatabase.All, Has.Count.EqualTo(1));
            Assert.That(SpecialActionDatabase.Get("external-action").FixedCost.GoldVoucher, Is.EqualTo(2));
        }
    }
}
