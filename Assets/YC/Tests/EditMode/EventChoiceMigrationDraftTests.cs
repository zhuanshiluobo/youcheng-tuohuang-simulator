using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    /// <summary>用隔离旧内容夹具比较新事件分支效果树，不计画面或正常对局验收。</summary>
    public sealed class EventChoiceMigrationDraftTests
    {
        private static LuaSnapshotEntry E(string name, LuaSnapshotValue value) => new LuaSnapshotEntry(name, value);

        private static LuaInvocationResult Invoke(string source, int oldIndex, string choiceId, string target = "", bool completion = false)
        {
            const string version = "1.0.0";
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", 5, 20, 4, version);
            snapshot.EventPayload = LuaSnapshotValue.Object(new[] {
                E("choiceId", LuaSnapshotValue.String(choiceId)),
                E("optionIndex", LuaSnapshotValue.String(oldIndex.ToString(CultureInfo.InvariantCulture))),
                E("primaryInfluenceSlotId", LuaSnapshotValue.String("")),
                E("selectedInfluenceSlotIds", LuaSnapshotValue.Array(target.Length == 0 ? new List<LuaSnapshotValue>() : new List<LuaSnapshotValue> { LuaSnapshotValue.String(target) })),
                E("outcome", LuaSnapshotValue.String("completed")),
                E("normalizedResult", LuaSnapshotValue.Object(new[] { E("value", LuaSnapshotValue.String(target)),
                    E("context", LuaSnapshotValue.Object(new[] { E("origin", LuaSnapshotValue.String("A-01")) })) })),
                E("targetLocationId", LuaSnapshotValue.String("A-01")) });
            var instances = new[] { "fixture.enterprise.one", "fixture.enterprise.two" }.Select(id => LuaSnapshotValue.Object(new[] {
                E("id", LuaSnapshotValue.String(id)), E("contentType", LuaSnapshotValue.String("enterprise")),
                E("zoneId", LuaSnapshotValue.String("fixture.enterprise.display")) })).ToList();
            var zones = new[] { LuaSnapshotValue.Object(new[] { E("id", LuaSnapshotValue.String("fixture.enterprise.display")),
                E("templateId", LuaSnapshotValue.String("core.zone.enterprise_display")) }) };
            // 此对照限定企业均在场；退场企业的旧错误由独立的正式执行链夹具覆盖。
            snapshot.GlobalContent = LuaSnapshotValue.Object(new[] { E("instances", LuaSnapshotValue.Array(instances)), E("zones", LuaSnapshotValue.Array(zones)) });
            snapshot.GlobalMap = LuaSnapshotValue.Object(new[] {
                E("locations", LuaSnapshotValue.Array(new[] { LuaSnapshotValue.Object(new[] {
                    E("locationId", LuaSnapshotValue.String("A-01")), E("influenceSlotIds", LuaSnapshotValue.Array(new[] {
                        LuaSnapshotValue.String(InfluenceService.GetLocationSlotId("A-01", 0)) })) }) })),
                E("routes", LuaSnapshotValue.Array(new[] { LuaSnapshotValue.Object(new[] {
                    E("hasRoad", LuaSnapshotValue.Boolean(false)), E("coveredLocationIds", LuaSnapshotValue.Array(new[] { LuaSnapshotValue.String("A-01") })),
                    E("influenceSlotIds", LuaSnapshotValue.Array(new[] { LuaSnapshotValue.String(InfluenceService.GetRouteSlotId("A1", 0)) })) }) })) });
            var definition = new LuaScriptDefinition("fixture.event.migration", "fixture.event.migration", "fixture.event.migration",
                version, source, LuaContentHasher.ComputeSha256(source));
            return new MoonSharpLuaRuntimeHost().Invoke(definition,
                new LuaInvocationContext("fixture.event.migration", completion ? "EffectCompleted" : "EventCardResolved", "p1", 0, version, 4, 5, 20, snapshot));
        }

        [Test]
        public void StagedChoicesCompileAndMatchOriginalBranchEffects()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            string stage = Environment.GetEnvironmentVariable("YC_EVENT_MIGRATION_DRAFT");
            if (string.IsNullOrEmpty(stage)) stage = root;
            var mapping = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
                "prompt/DIY接入实施计划-20261101/04-事件选择迁移映射.json")));
            var baseline = JObject.Parse(File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),
                "Assets/YC/Tests/EditMode/Fixtures/event_migration_legacy.json")));
            int cards = 0, branches = 0;
            foreach (var record in mapping["definitions"])
            {
                string relative = (string)record["source"];
                string draftPath = Path.Combine(stage, relative);
                if (!File.Exists(draftPath)) continue;
                var original = JObject.Parse((string)baseline["definitions"][relative]);
                var draft = JObject.Parse(File.ReadAllText(draftPath));
                var handlers = (draft["abilityHandlers"] as JArray ?? new JArray()).Select(handler => (string)handler["handlerId"]).ToArray();
                var parsed = EventChoiceContract.Parse((JObject)draft["data"], relative,
                    path => File.ReadAllText(Path.Combine(stage, path)), handlers);
                Assert.That(parsed.Choices, Has.Count.EqualTo(record["choices"].Count()));
                string oldScript = string.IsNullOrEmpty((string)record["legacyEffectScript"])
                    ? LegacyEventLuaFixture.CreateScriptSource(original["data"].ToObject<EventCardDefinition>())
                    : (string)baseline["scripts"][(string)record["legacyEffectScript"]];
                for (int i = 0; i < parsed.Choices.Count; i++)
                {
                    var choice = parsed.Choices[i]; var prior = record["choices"][i];
                    Assert.That(choice.ChoiceId, Is.EqualTo((string)prior["choiceId"]));
                    string selected = File.ReadAllText(Path.Combine(stage, choice.Script));
                    Assert.That(selected, Does.Not.Contain("optionIndex").And.Not.Contain("primaryInfluenceSlotId"));
                    bool preselection = !string.IsNullOrEmpty(choice.CompletionHandlerId);
                    string target = preselection ? InfluenceService.GetRouteSlotId("A1", 0) : "";
                    var oldResult = Invoke(oldScript, (int)prior["legacyIndex"], choice.ChoiceId, target);
                    var newResult = Invoke(selected, (int)prior["legacyIndex"], choice.ChoiceId);
                    Assert.That(oldResult.IsSuccess, Is.True, relative + " 旧分支：" + oldResult.Diagnostic);
                    Assert.That(newResult.IsSuccess, Is.True, relative + "/" + choice.ChoiceId + "：" + newResult.Diagnostic);
                    if (preselection)
                    {
                        Assert.That(newResult.Effects.Single().EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
                        var handler = ((JArray)draft["abilityHandlers"]).Single(value => (string)value["handlerId"] == choice.CompletionHandlerId);
                        string continuation = File.ReadAllText(Path.Combine(stage, (string)handler["script"]));
                        newResult = Invoke(continuation, (int)prior["legacyIndex"], choice.ChoiceId, target, true);
                        Assert.That(newResult.IsSuccess, Is.True, relative + " 完成处理器：" + newResult.Diagnostic);
                    }
                    Assert.That(JsonConvert.SerializeObject(newResult.Effects), Is.EqualTo(JsonConvert.SerializeObject(oldResult.Effects)),
                        relative + "/" + choice.ChoiceId + " 的效果树与旧分支不同。");
                    branches++;
                }
                cards++;
            }
            Assert.That(cards, Is.EqualTo(24));
            Assert.That(branches, Is.EqualTo(55));
        }
    }
}
