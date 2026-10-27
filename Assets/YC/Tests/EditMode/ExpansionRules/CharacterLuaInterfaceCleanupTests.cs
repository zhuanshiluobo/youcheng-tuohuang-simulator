using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // QY-007 独占：真实角色Lua的隔离输入合同；不运行正常对局、不复制内容包、不改共享夹具。
    // 这里验证Lua候选/续接，未替代实际MoveItem原子性、保存恢复或页面验收。
    public sealed class CharacterLuaInterfaceCleanupTests
    {
        private const string Card = "fixture.character", Supply = "fixture.supply", Other = "fixture.other";
        private static readonly string Root = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
        private static LuaSnapshotValue S(string value) => LuaSnapshotValue.String(value);
        private static LuaSnapshotValue A(params LuaSnapshotValue[] values) => LuaSnapshotValue.Array(values);
        private static LuaSnapshotEntry F(string key, LuaSnapshotValue value) => new LuaSnapshotEntry(key, value);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] fields) => LuaSnapshotValue.Object(fields);
        private static LuaSnapshotValue Zone(string id, string template, string local, string top = "", string parent = "") =>
            O(F("id", S(id)), F("templateId", S(template)), F("localZoneId", S(local)), F("topItemId", S(top)), F("parentId", S(parent)));
        private static LuaSnapshotValue Item(string id, string definition, string zone, string owner = "p1", string type = "core.facility_card") =>
            O(F("id", S(id)), F("definitionId", S(definition)), F("zoneId", S(zone)), F("typeId", S(type)), F("ownerSubject", S(owner)));
        private static LuaSnapshotValue Definition(string id, string name) => O(F("id", S(id)), F("name", S(name)));

        private static LuaFacadeSnapshot Snapshot(bool freshTop = false)
        {
            var result = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 3, "2.0.1");
            result.GlobalContent = O(
                F("instances", A(Item("top", "fixture.a", Supply), Item("lower", "fixture.c", Supply),
                    Item("other-top", "fixture.b", Other), Item(Card, "fixture.character", "fixture.persistent", type: "core.operator_card"))),
                F("zones", A(Zone(Supply, "core.zone.facility_supply_slot", "supply-1", freshTop ? "lower" : "top"),
                    Zone(Other, "core.zone.facility_supply_slot", "supply-2", "other-top"),
                    Zone("fixture.box", "core.zone.box_retired", "box"),
                    Zone("fixture.deck", "core.zone.facility_deck", "facility_deck"),
                    Zone("fixture.orders", "core.zone.card_facility_storage", "orders", parent: Card))),
                F("definitions", A(Definition("fixture.a", "甲设施"), Definition("fixture.b", "乙设施"), Definition("fixture.c", "丙设施"))));
            return result;
        }
        private static LuaSnapshotValue Context(string stage, string[] names = null) =>
            O(F("source", S(Card)), F("stage", S(stage)), F("supply", S(Supply)),
                F("names", A((names ?? Array.Empty<string>()).Select(S).ToArray())));
        private static LuaInvocationResult Invoke(string role, string mode, LuaFacadeSnapshot snapshot, string stage = null,
            string selected = null, string[] names = null, string eventType = "CharacterEffectActivated")
        {
            var declaration = JObject.Parse(File.ReadAllText(Path.Combine(Root, "character", role + ".json")));
            var ability = declaration["abilities"].Single(a => (string)a["mode"] == mode);
            string source = File.ReadAllText(Path.Combine(Root, (string)ability["script"]));
            string version = (string)ability["version"];
            snapshot.EventPayload = stage == null ? O(F("cardInstanceRef", S(Card))) :
                O(F("continuationContext", Context(stage, names)), F("outcome", S("completed")),
                    F("normalizedResult", selected == null ? O() : S(selected)));
            var definition = new LuaScriptDefinition(role, (string)ability["abilityId"], "qy007.cleanup.actual",
                version, source, LuaContentHasher.ComputeSha256(source));
            return new MoonSharpLuaRuntimeHost().Invoke(definition,
                new LuaInvocationContext("qy007.cleanup.isolated", stage == null ? eventType : "EffectCompleted", "p1", 0, version, 3, 0, 0, snapshot));
        }
        private static LuaEffectSpec Only(LuaInvocationResult result)
        {
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Count, Is.EqualTo(1));
            return result.Effects.Single();
        }
        private static string[] Options(LuaEffectSpec effect) => Value(effect.RawArguments, "options").Items.Select(v => v.StringValue).ToArray();

        [TestCase("operator.eunectes", "tactic")]
        [TestCase("operator.closure", "tactic")]
        [TestCase("infected.character.flowing_shape_beta", "strategy")]
        public void SupplyChoiceIncludesCurrentTopsAndExcludesVisibleLowerLayers(string role, string mode)
        {
            var choice = Only(Invoke(role, mode, Snapshot()));
            Assert.That(choice.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            Assert.That(Options(choice), Is.EquivalentTo(new[] { "top", "other-top" }));
        }
        [TestCase("operator.eunectes", "tactic")]
        [TestCase("operator.closure", "tactic")]
        [TestCase("infected.character.flowing_shape_beta", "strategy")]
        public void OldAnswerForANowCoveredCardCannotConstructATransfer(string role, string mode)
        {
            var result = Invoke(role, mode, Snapshot(true), "selected", "top");
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Diagnostic, Does.Contain("facility_source_changed"));
            Assert.That(result.Effects, Is.Empty);
        }
        [TestCase("operator.eunectes", "tactic", "fixture.box", "unordered")]
        [TestCase("operator.closure", "tactic", "fixture.orders", "unordered")]
        [TestCase("infected.character.flowing_shape_beta", "strategy", "fixture.deck", "bottom")]
        public void ValidTopConstructsOneExactTransferToTheCardsDeclaredDestination(string role, string mode, string destination, string insertion)
        {
            // selected阶段的目的地正常由start保存；给出独立输入明确验证本次续接，不冒充真实转移已完成。
            var snapshot = Snapshot();
            var declaration = JObject.Parse(File.ReadAllText(Path.Combine(Root, "character", role + ".json")));
            var ability = declaration["abilities"].Single(a => (string)a["mode"] == mode);
            string code = File.ReadAllText(Path.Combine(Root, (string)ability["script"]));
            string version = (string)ability["version"];
            snapshot.EventPayload = O(F("continuationContext", O(F("source", S(Card)), F("stage", S("selected")),
                F("destination", S(destination)), F("names", A()))), F("outcome", S("completed")), F("normalizedResult", S("top")));
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(role, (string)ability["abilityId"],
                "qy007.cleanup.actual", version, code, LuaContentHasher.ComputeSha256(code)),
                new LuaInvocationContext("qy007.cleanup.isolated", "EffectCompleted", "p1", 0, version, 3, 0, 0, snapshot));
            var transfer = Only(result);
            Assert.That(transfer.EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(Text(Value(transfer.RawArguments, "itemRefs").Items.Single(), "itemId"), Is.EqualTo("top"));
            Assert.That(Text(Value(transfer.RawArguments, "expectedSource"), "zoneId"), Is.EqualTo(Supply));
            Assert.That(Text(Value(transfer.RawArguments, "destination"), "zoneId"), Is.EqualTo(destination));
            Assert.That(Text(Value(transfer.RawArguments, "insertion"), "kind"), Is.EqualTo(insertion));
            Assert.That(Number(transfer.RawArguments, "requiredCount"), Is.EqualTo(1));
        }
        [TestCase("operator.eunectes", "tactic")]
        [TestCase("operator.closure", "tactic")]
        [TestCase("infected.character.flowing_shape_beta", "strategy")]
        public void AfterRefillTheLuaRequeriesTopsAndExcludesAnAlreadyTakenName(string role, string mode)
        {
            var choice = Only(Invoke(role, mode, Snapshot(true), "refilled", names: new[] { "丙设施" }));
            Assert.That(Options(choice), Is.EqualTo(new[] { "other-top" }));
        }
        [Test]
        public void SwireDifferentMarkerOwnersRemainDistinctChoices()
        {
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 3, "2.0.0");
            snapshot.GlobalContent = O(F("zones", A(Zone("fixture.markers", "core.zone.card_cube_storage", "markers", parent: Card))),
                F("instances", A(Item("marker.p2", "core.player_marker", "fixture.markers", "p2", "core.player_marker"),
                    Item("marker.p3", "core.player_marker", "fixture.markers", "p3", "core.player_marker"))));
            var choice = Only(Invoke("infected.character.swire", "tactic", snapshot, eventType: "PlayerCleanupStarted"));
            Assert.That(choice.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            Assert.That(Options(choice), Is.EquivalentTo(new[] { "marker.p2", "marker.p3" }));
        }
    }
}
