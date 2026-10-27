using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class QY008ValidationContextR6Tests
    {
        private static object Call(object target, string name, params object[] args)
        {
            var method = target.GetType().GetMethod(name);
            Assert.That(method, Is.Not.Null, name);
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }

        [Test]
        public void BuiltInRegistriesKeepIndependentRegistrationFreezeAndDefensiveValues()
        {
            var first = ItemTypeRegistry.CreateBuiltIn(); var second = ItemTypeRegistry.CreateBuiltIn();
            var source = new ItemTypeDefinition { TypeId = "qy008.custom", BaseTypeId = "core.player_marker", PhysicalKind = ItemPhysicalKind.Cube };
            Assert.That(first.TryRegister(source, out string reason), Is.True, reason);
            source.BaseTypeId = "changed"; source.OperationProfileIds.Add("changed");
            Assert.That(first.IsAssignableTo("qy008.custom", "core.player_marker"), Is.True);
            Assert.That(second.Find("qy008.custom"), Is.Null);
            var found = first.Find("core.player_marker"); found.BaseTypeId = "changed"; found.OperationProfileIds.Clear();
            first.Definitions.Single(d => d.TypeId == "core.player_marker").OperationProfileIds.Clear();
            Assert.That(first.HasOperationProfile("core.player_marker", "core.influence_cube"), Is.True);
            Assert.That(second.HasOperationProfile("core.player_marker", "core.influence_cube"), Is.True);
            first.Freeze();
            Assert.That(first.TryRegister(new ItemTypeDefinition { TypeId = "qy008.after.freeze" }, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("registry_frozen"));
            Assert.That(second.TryRegister(new ItemTypeDefinition { TypeId = "qy008.second", BaseTypeId = "core.player_marker", PhysicalKind = ItemPhysicalKind.Cube }, out reason), Is.True, reason);
            var templates = ZoneTemplateRegistry.CreateBuiltIn(second);
            var template = templates.Find("core.zone.player_supply"); template.AcceptedItemKinds.Clear(); template.Capacity = 0;
            templates.Definitions.Single(d => d.TemplateId == "core.zone.player_supply").AllowedParentTemplateIds.Clear();
            Assert.That(templates.Find("core.zone.player_supply").AcceptedItemKinds, Does.Contain(ItemPhysicalKind.Cube));
            Assert.That(templates.Find("core.zone.player_supply").AllowedParentTemplateIds.Count > 0, Is.True);
        }

        [Test]
        public void ValidationReadsNewRegistrationAndSameRevisionChanges()
        {
            var state = State(); var types = ItemTypeRegistry.CreateBuiltIn(); var query = new ItemZoneQuery(types);
            var marker = state.ItemZones.Items.First(i => i.TypeId == "core.player_marker");
            int revision = state.EffectRuntime.StateRevision;
            Assert.That(query.TryValidate(state, out string reason), Is.True, reason);
            marker.TypeId = "qy008.live";
            Assert.That(query.TryValidate(state, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("PhysicalKind_type_mismatch"));
            Assert.That(types.TryRegister(new ItemTypeDefinition { TypeId = "qy008.live", BaseTypeId = "core.player_marker", PhysicalKind = ItemPhysicalKind.Cube }, out reason), Is.True, reason);
            Assert.That(query.TryValidate(state, out reason), Is.True, reason);
            Assert.That(new ItemZoneQuery().TryValidate(state, out reason), Is.False);
            string zone = marker.ZoneId; marker.ZoneId = "qy008.missing";
            Assert.That(query.TryValidate(state, out reason), Is.False);
            marker.ZoneId = zone;
            Assert.That(query.TryValidate(state, out reason), Is.True, reason);
            var returned = query.FindItem(state, marker.ItemId); returned.ZoneId = "changed";
            Assert.That(marker.ZoneId, Is.EqualTo(zone));
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
        }

        [Test]
        public void NarrowSourceQueriesKeepDefensiveValuesAndOriginalCarrierAuthorization()
        {
            using var fixture = new AgnesContentTestFixture(); fixture.Formal(); fixture.Choose(1, "A08", "2");
            var registry = fixture.Registry; var source = registry.QuerySources(fixture.State, 1).Single(s => s.AbilityId == "entrepreneur.A08.tactic");
            var declaration = registry.AbilityDeclarations.Single(d => d.AbilityId == source.AbilityId);
            string expectedHash = registry.GetAbilityHandlerHash(declaration.HandlerRef);
            Assert.That(Call(registry, "GetAbilitySourceHandlerHash", source.AbilityId), Is.EqualTo(expectedHash));
            var operations = (IReadOnlyList<string>)Call(registry, "GetAbilitySourceOperations", source.AbilityId);
            Assert.That(operations, Is.EqualTo(declaration.AllowedOperations));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)operations).Clear());
            declaration.HandlerRef = "changed"; declaration.AllowedOperations.Clear();
            Assert.That(Call(registry, "GetAbilitySourceHandlerHash", source.AbilityId), Is.EqualTo(expectedHash));
            var parts = operations.First(p => p.StartsWith("carrier.transfer.", StringComparison.Ordinal)).Split(':');
            var card = fixture.State.ItemZones.Items.First(i => i.DefinitionId == parts[1]);
            string operation = parts[0].Substring("carrier.".Length);
            var expected = CardMarkerCarrier.ResolveWithSource(fixture.State, source, 1, card.ItemId, parts[2], operation, registry.AbilityDeclarations, out var accepted, out string reason);
            object[] args = { fixture.State, source, 1, card.ItemId, parts[2], operation, null, null };
            var actual = (ZoneInstanceState)Call(registry, "QueryAuthorizedCarrierZone", args);
            Assert.That(JsonConvert.SerializeObject(actual), Is.EqualTo(JsonConvert.SerializeObject(expected)));
            Assert.That((List<string>)args[6], Is.EqualTo(accepted)); Assert.That((string)args[7], Is.EqualTo(reason));
            Assert.That(actual, Is.Not.Null);
            actual.Configuration.AcceptedItemKinds.Clear(); actual.ParentId = "changed"; ((List<string>)args[6]).Clear();
            Assert.That(expected.ParentId, Is.EqualTo(card.ItemId));
            Assert.That(expected.Configuration.AcceptedItemKinds.Count > 0, Is.True);
        }

        [Test]
        public void ContinuationLevelLookupPreservesValuesAndMissingErrors()
        {
            using var fixture = new AgnesContentTestFixture(); fixture.Formal();
            var definitions = fixture.Registry.EnterpriseDefinitions;
            var enterprise = fixture.State.ContentDefinitions.EnterpriseDefinitionIds.Select(definitions.FindEnterprise).First(d => d.Levels.Any(l => !string.IsNullOrEmpty(l.AbilityId)));
            var level = enterprise.Levels.First(l => !string.IsNullOrEmpty(l.AbilityId));
            Assert.That(Call(definitions, "GetSpecialAbilityLevel", enterprise.DefinitionId, level.AbilityId), Is.EqualTo(level.Level));
            enterprise.Levels.Clear();
            Assert.That(Call(definitions, "GetSpecialAbilityLevel", enterprise.DefinitionId, level.AbilityId), Is.EqualTo(level.Level));
            Assert.Throws<NullReferenceException>(() => Call(definitions, "GetSpecialAbilityLevel", "qy008.unknown", "missing"));
            Assert.Throws<InvalidOperationException>(() => Call(definitions, "GetSpecialAbilityLevel", enterprise.DefinitionId, "missing"));
        }

        private static GameState State()
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            return new ItemZoneStateFactory().CreateForSetup(new GameState
            {
                GameId = "qy008.r6.registration", MapId = map.MapId, ContentPackHash = "qy008.r6.content",
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "qy008.r6.content" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, InfluenceSupply = 1 }, new PlayerState { PlayerId = 2, InfluenceSupply = 1 } }
            }, map, new List<ItemRecipeEntry>
            {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "component.player_marker", TypeId = "core.player_marker", Quantity = 1, PerPlayer = true, InitialZoneRole = "supply" }
            });
        }
    }
}
