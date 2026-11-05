using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class ItemTypeQueryIndexTests
    {
        [Test]
        public void AllBuiltInInheritanceAndOperationQueriesMatchOriginalWalks()
        {
            var current = ItemTypeRegistry.CreateBuiltIn(); var reference = LegacyItemTypeQueriesReference.CreateBuiltIn();
            var types = current.Definitions.Select(d => d.TypeId).Concat(new[] { null, "", "unknown" }).ToArray();
            var profiles = current.Definitions.SelectMany(d => d.OperationProfileIds).Distinct().Concat(new[] { null, "", "unknown" }).ToArray();
            foreach (string type in types)
            {
                foreach (string parent in types) Assert.That(current.IsAssignableTo(type, parent), Is.EqualTo(reference.IsAssignableTo(type, parent)), type + " -> " + parent);
                foreach (string profile in profiles) Assert.That(current.HasOperationProfile(type, profile), Is.EqualTo(reference.HasOperationProfile(type, profile)), type + " / " + profile);
            }
        }
        [Test]
        public void NewRegistrationsInputMutationAndSeparateBuiltInCopiesCannotChangeEarlierRules()
        {
            var current = ItemTypeRegistry.CreateBuiltIn(); var reference = LegacyItemTypeQueriesReference.CreateBuiltIn();
            var other = ItemTypeRegistry.CreateBuiltIn();
            const string id = "isolated.custom.marker";
            Assert.That(current.IsAssignableTo(id, "core.cube"), Is.False);
            var input = new ItemTypeDefinition { TypeId = id, BaseTypeId = "core.player_marker", PhysicalKind = ItemPhysicalKind.Cube };
            Assert.That(current.TryRegister(input, out var reason), Is.EqualTo(reference.TryRegister(input, out var expected)));
            Assert.That(reason, Is.EqualTo(expected));
            input.BaseTypeId = "core.card"; input.OperationProfileIds.Add("core.deck_shuffle");
            var returned = current.Find(id); returned.BaseTypeId = "core.card"; returned.OperationProfileIds.Clear();
            foreach (string parent in new[] { "core.cube", "core.player_marker", "core.card", id })
                Assert.That(current.IsAssignableTo(id, parent), Is.EqualTo(reference.IsAssignableTo(id, parent)));
            Assert.That(current.HasOperationProfile(id, "core.influence_cube"), Is.True);
            Assert.That(current.HasOperationProfile(id, "core.deck_shuffle"), Is.False);
            Assert.That(other.IsAssignableTo(id, "core.cube"), Is.False);
            current.Freeze(); reference.Freeze();
            Assert.That(current.TryRegister(input, out reason), Is.EqualTo(reference.TryRegister(input, out expected)));
            Assert.That(reason, Is.EqualTo(expected));
        }
        [Test]
        public void RegistrationFailuresKeepTheOriginalReasonsAndDoNotPublishPartialIndexes()
        {
            var current = ItemTypeRegistry.CreateBuiltIn(); var reference = LegacyItemTypeQueriesReference.CreateBuiltIn();
            var cases = new ItemTypeDefinition[] {
                null, new ItemTypeDefinition(), new ItemTypeDefinition { TypeId = "core.cube" },
                new ItemTypeDefinition { TypeId = "x", BaseTypeId = "missing", PhysicalKind = ItemPhysicalKind.Cube },
                new ItemTypeDefinition { TypeId = "x", BaseTypeId = "x", PhysicalKind = ItemPhysicalKind.Cube },
                new ItemTypeDefinition { TypeId = "x", BaseTypeId = "core.cube", PhysicalKind = ItemPhysicalKind.Card },
                new ItemTypeDefinition { TypeId = "x", BaseTypeId = "core.cube", PhysicalKind = ItemPhysicalKind.Cube, OperationProfileIds = null },
                new ItemTypeDefinition { TypeId = "x", BaseTypeId = "core.cube", PhysicalKind = ItemPhysicalKind.Cube, OperationProfileIds = new List<string> { "core.player_marker_storage" } }
            };
            foreach (var input in cases)
            {
                Assert.That(current.TryRegister(input, out var reason), Is.EqualTo(reference.TryRegister(input, out var expected)));
                Assert.That(reason, Is.EqualTo(expected));
                Assert.That(current.IsAssignableTo("x", "core.cube"), Is.False);
            }
        }
        private static CardZoneDefinition Storage() => new CardZoneDefinition {
            ZoneTemplateId = "core.zone.card_cube_storage", LocalZoneId = "storage", AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube },
            AcceptedTypeIds = new List<string> { "core.player_marker" }, CapacityPolicy = "bounded", Capacity = 2,
            EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents",
            VisibilityPolicyId = ItemZoneVisibility.Owner, OrderingKind = ZoneOrderingKind.Unordered };
        private static string Outcome(Func<(bool, string)> run)
        {
            try { var result = run(); return result.Item1 + "|" + result.Item2; }
            catch (Exception error) { return error.GetType().FullName + "|" + error.Message; }
        }
        [Test]
        public void ImmutableTemplateReadsKeepEveryDeclarationCheckAndOriginalFailureOrder()
        {
            var types = ItemTypeRegistry.CreateBuiltIn(); var templates = ZoneTemplateRegistry.CreateBuiltIn(types);
            var reference = new LegacyZoneTemplateValidationReference(types, templates);
            var cases = new Dictionary<string, Action<CardZoneDefinition>> {
                { "正常", d => { } }, { "未知模板", d => d.ZoneTemplateId = "unknown" },
                { "空模板", d => d.ZoneTemplateId = null }, { "空本地名", d => d.LocalZoneId = "" },
                { "空种类表", d => d.AcceptedItemKinds = null }, { "无种类", d => d.AcceptedItemKinds.Clear() },
                { "未知种类", d => d.AcceptedItemKinds[0] = (ItemPhysicalKind)999 },
                { "空类型表", d => d.AcceptedTypeIds = null }, { "未知类型", d => d.AcceptedTypeIds[0] = "unknown" },
                { "错误类型", d => d.AcceptedTypeIds[0] = "core.card" },
                { "错误容量政策", d => d.CapacityPolicy = "unknown" }, { "缺容量", d => d.Capacity = null },
                { "负容量", d => d.Capacity = -1 }, { "超模板容量", d => d.Capacity = 4 },
                { "错误顺序", d => d.OrderingKind = ZoneOrderingKind.Track },
                { "错误可见性", d => d.VisibilityPolicyId = ItemZoneVisibility.SecretRetired },
                { "错误进出政策", d => { d.EntryPolicyId = "unknown"; d.ExitPolicyId = "unknown"; } },
                { "错误载体政策", d => d.CarrierExitPolicyId = "unknown" }, { "空槽位表", d => d.Slots = null },
                { "重复槽位", d => d.Slots.AddRange(new[] { "a", "a" }) },
                { "错误付款政策", d => d.PaymentPolicyId = "unknown" }
            };
            Assert.That(templates.ValidateCardZoneDeclaration("core.basic_character_card", Storage(), out _), Is.True);
            foreach (var change in cases)
            foreach (string carrier in new[] { "core.basic_character_card", "core.cube", "unknown", null })
            {
                var value = Storage(); change.Value(value); string before = JsonConvert.SerializeObject(value);
                Assert.That(Outcome(() => (templates.ValidateCardZoneDeclaration(carrier, value, out var reason), reason)),
                    Is.EqualTo(Outcome(() => (reference.ValidateCardZoneDeclaration(carrier, value, out var reason), reason))), change.Key + " / " + carrier);
                Assert.That(JsonConvert.SerializeObject(value), Is.EqualTo(before));
            }
        }
    }
}
