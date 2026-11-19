using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 独立 Lua 快照：模拟长期对局中较多的区域；不作为正常游戏或画面验收。
    public sealed class FifthNumberDepartureBudgetTests
    {
        private static LuaSnapshotValue S(string value) => LuaSnapshotValue.String(value);
        private static LuaSnapshotEntry F(string key, LuaSnapshotValue value) => new LuaSnapshotEntry(key, value);
        private static LuaSnapshotValue O(params LuaSnapshotEntry[] fields) => LuaSnapshotValue.Object(fields);

        [TestCase(0, true)]
        [TestCase(600, true)]
        [TestCase(600, false)]
        public void DepartureChecksExactStagingZoneWithinTheNormalInstructionBudget(int otherZones, bool validZone)
        {
            var zones = Enumerable.Range(0, otherZones).Select(i => O(
                F("id", S("other." + i)), F("templateId", S("core.zone.player_supply")),
                F("localZoneId", S("supply")), F("parentKind", S("Zone")))).ToList();
            zones.Add(O(F("id", S("target.staging")), F("templateId", S(validZone ? "core.zone.effect_staging" : "core.zone.player_supply")),
                F("localZoneId", S("removed_influence")), F("parentKind", S("Zone"))));
            var instance = O(F("id", S("target.marker")), F("zoneId", S("target.staging")),
                F("ownerSubject", S("p1")), F("typeId", S("core.player_marker")),
                F("originalSupplyZoneId", S("player.supply")));
            var payload = O(F("eventProtocolVersion", LuaSnapshotValue.Integer(2)), F("ownerPlayerId", LuaSnapshotValue.Integer(1)),
                F("causeKind", S("remove")), F("departureId", S("departure.1")), F("sourceEffectId", S("effect.1")),
                F("influenceId", S("target.marker")), F("originalSupplyZoneId", S("player.supply")), F("sourceItemId", S("fifth.card")));
            const string version = "2.0.0";
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 4, version);
            snapshot.EventPayload = payload;
            snapshot.GlobalContent = O(F("zones", LuaSnapshotValue.Array(zones)),
                F("instances", LuaSnapshotValue.Array(new[] { instance })));
            string path = Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core/lua/entrepreneurs/fifth_number_response.lua");
            string source = File.ReadAllText(path);
            var definition = new LuaScriptDefinition("entrepreneur.ability.T09", "entrepreneur.T09.departure", "entrepreneur.T09.departure",
                version, source, LuaContentHasher.ComputeSha256(source));
            var context = new LuaInvocationContext("departure.test", "InfluenceRemoved", "p1", 0, version, 4, 0, 0, snapshot);
            var result = new MoonSharpLuaRuntimeHost().Invoke(definition, context);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Count, Is.EqualTo(validZone ? 1 : 0));
        }
    }
}
