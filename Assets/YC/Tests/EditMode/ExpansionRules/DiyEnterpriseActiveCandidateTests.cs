using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Enterprises;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 合成区域只用于规则隔离验证，不作为可见程序或正常联机验收。
    public sealed class DiyEnterpriseActiveCandidateTests
    {
        private static LuaSnapshotValue Entry(string id, string definition, string type, string zone) => LuaSnapshotValue.Object(new[] {
            new LuaSnapshotEntry("id", LuaSnapshotValue.String(id)), new LuaSnapshotEntry("definitionId", LuaSnapshotValue.String(definition)),
            new LuaSnapshotEntry("contentType", LuaSnapshotValue.String(type)), new LuaSnapshotEntry("zoneId", LuaSnapshotValue.String(zone)) });
        private static LuaSnapshotValue Zone(string id, string template) => LuaSnapshotValue.Object(new[] {
            new LuaSnapshotEntry("id", LuaSnapshotValue.String(id)), new LuaSnapshotEntry("templateId", LuaSnapshotValue.String(template)) });

        private static LuaInvocationResult Invoke(string path, string selfDefinition, int activeCount)
        {
            var zones = Enumerable.Range(0, 350).Select(i => Zone("other.zone." + i, "core.zone.map_location")).ToList();
            zones.Add(Zone("display.1", "core.zone.enterprise_display"));
            zones.Add(Zone("display.2", "core.zone.enterprise_display"));
            zones.Add(Zone("retired", "core.zone.box_retired"));
            zones.Add(Zone("reserve", "core.zone.box_reserve"));
            var items = Enumerable.Range(0, 350).Select(i => Entry("other." + i, "other.definition", "facility", "other.zone." + i)).ToList();
            items.Add(Entry("retired.enterprise", "enterprise.retired", "enterprise", "retired"));
            items.Add(Entry("reserve.enterprise", "enterprise.reserve", "enterprise", "reserve"));
            items.Add(Entry("display.facility", "facility.fake", "facility", "display.1"));
            if (activeCount >= 1) items.Add(Entry("active.self", selfDefinition, "enterprise", "display.1"));
            if (activeCount >= 2) items.Add(Entry("active.other", "enterprise.other", "enterprise", "display.2"));
            var snapshot = LuaFacadeSnapshot.FromInvocation("p2", 0, 0, 4, "1.0.1");
            snapshot.GlobalContent = LuaSnapshotValue.Object(new[] {
                new LuaSnapshotEntry("zones", LuaSnapshotValue.Array(zones)), new LuaSnapshotEntry("instances", LuaSnapshotValue.Array(items)) });
            var context = new LuaInvocationContext("fixture", "AbilityInvoked", "p2", 7, "1.0.1", 4, 0, 0, snapshot);
            string source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua", path));
            var script = new LuaScriptDefinition("fixture", "fixture.ability", "fixture.subscription", "1.0.1", source, LuaContentHasher.ComputeSha256(source));
            return new MoonSharpLuaRuntimeHost().Invoke(script, context);
        }

        private static NormalizedValue Field(NormalizedValue value, string key) => value.Properties.Single(entry => entry.Name == key).Value;

        private static IEnumerable<TestCaseData> UpgradeCases()
        {
            var cases = new[] {
                ("enterprises/enterprise.rhodes_island.level_1.special.lua", "enterprise.rhodes_island", true, 1, "effect.enterprise.upgrade"),
                ("enterprises/infected.enterprise.rhodes_island.level_1.special.lua", "infected.enterprise.rhodes_island", true, 1, "effect.enterprise.upgrade"),
                ("characters/infected.character.flowing_shape_gamma.tactic.lua", "enterprise.rhodes_island", false, 1, "effect.enterprise.upgrade"),
                ("characters/mlynar.strategy.lua", "enterprise.rhodes_island", false, 1, EnterpriseEffectExecutor.UpgradeAllPlayersTypeId),
                ("characters/mlynar.tactic.lua", "enterprise.rhodes_island", false, 1, "effect.enterprise.upgrade"),
                ("characters/operator.kaltsit.strategy.lua", "enterprise.rhodes_island", false, 2, "effect.enterprise.upgrade"),
                ("city_styles/city_style_large_enterprise_park.special.lua", "enterprise.rhodes_island", false, 2, "effect.enterprise.upgrade")
            };
            foreach (var item in cases)
                foreach (int count in new[] { 0, 1, 2 })
                    yield return new TestCaseData(item.Item1, item.Item2, item.Item3, item.Item4, item.Item5, count)
                        .SetName(Path.GetFileName(item.Item1) + "_场上企业" + count);
        }

        [TestCaseSource(nameof(UpgradeCases))]
        public void UpgradeScopesContainOnlyEligibleActiveEnterprises(string path, string selfDefinition, bool excludeSelf,
            int levels, string effectType, int activeCount)
        {
            var result = Invoke(path, selfDefinition, activeCount);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects[0].EffectTypeId, Is.EqualTo(effectType));
            var args = result.Effects[0].RawArguments;
            Assert.That(Field(args, "player").StringValue, Is.EqualTo("2"));
            Assert.That(Field(args, "levels").IntegerValue, Is.EqualTo(levels));
            var expected = new List<string>();
            if (activeCount >= 1 && !excludeSelf) expected.Add("active.self");
            if (activeCount >= 2) expected.Add("active.other");
            Assert.That(Field(args, "enterpriseItemIds").Items.Select(value => value.StringValue), Is.EquivalentTo(expected));
            Assert.That(EnterpriseEffectExecutor.ValidateArguments(effectType, args), Is.Empty);
            if (path.EndsWith("mlynar.strategy.lua"))
                Assert.That(result.Effects.Skip(1).Select(value => value.EffectTypeId), Is.EqualTo(new[] { "effect.enterprise.activate_special" }));
            if (path.EndsWith("mlynar.tactic.lua"))
                Assert.That(result.Effects.Skip(1).Select(value => value.EffectTypeId), Is.EqualTo(new[] { "effect.resource.gain", "effect.resource.gain", "effect.resource.gain" }));
            if (path.EndsWith("operator.kaltsit.strategy.lua"))
                Assert.That(result.Effects.Skip(1).Select(value => value.EffectTypeId), Is.EqualTo(new[] { "effect.score.gain" }));
        }

        [TestCase("red", 0)] [TestCase("red", 1)] [TestCase("red", 2)]
        [TestCase("blue", 0)] [TestCase("blue", 1)] [TestCase("blue", 2)]
        [TestCase("yellow", 0)] [TestCase("yellow", 1)] [TestCase("yellow", 2)]
        public void RhodesOfficesOfferTwoUpgradesOnlyForTwoActiveEnterprises(string color, int activeCount)
        {
            var result = Invoke("facilities/infected.facility.rhodes_office_" + color + ".entry.lua", "enterprise.rhodes_island", activeCount);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var choice = result.Effects.Single();
            var options = Field(choice.RawArguments, "options").Items.Select(value => value.StringValue).ToArray();
            Assert.That(options, Is.EqualTo(activeCount >= 2 ? new[] { "dispatch", "upgrade_two" } : new[] { "dispatch" }));
            Assert.That(choice.NestedEffectGroups.Length, Is.EqualTo(options.Length));
        }
    }
}
