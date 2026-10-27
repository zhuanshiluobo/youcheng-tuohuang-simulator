using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 本轮独占回归。只由017在共享模块登记完成后运行，不补manifest、不开正常局。
    public sealed class AgnesInterfaceLuaCleanupTests
    {
        [TestCase("own", 1)]
        [TestCase("foreign", 0)]
        [TestCase("mixed", 1)]
        [TestCase("wrong_rule", 0)]
        [TestCase("wrong_player", 0)]
        [TestCase("no_claim", 0)]
        [TestCase("highest", 0)]
        [TestCase("other_controller", 0)]
        public void FormalScoringValueUsesOnlyThisAuthorizedSourceClaim(string scenario, int expected)
        {
            using var f = new AgnesContentTestFixture();
            f.Formal(); f.Choose(1, "A09", declineGain: true); f.Choose(7, "A02");
            var map = new MapQueryService(StaticMapDefinitions.Resolve(f.State.MapId));
            var service = new ScoringModifierQueryService(map, f.Registry);
            string region = map.Map.Regions.First().RegionId;
            const string basis = "qy013.source-claim.isolated-basis";
            var tie = new JObject { ["pass"] = ScoringModifierProtocol.Tie, ["regionId"] = region,
                ["playerId"] = -1, ["positiveHighest"] = new JArray(1, 2) };
            var owner = service.QueryPass(f.State, Request(tie, basis)).Single(r => r.Source.AbilityId == "entrepreneur.A02.scoring");
            var claims = new JArray();
            JObject Claim(string source, int player = 1, string rule = "entrepreneur.A02.tie")
                => new JObject { ["sourceKey"] = source, ["playerId"] = player, ["ruleId"] = rule };
            if (scenario == "mixed") claims.Add(Claim("isolated.foreign.source"));
            if (scenario != "no_claim") claims.Add(Claim(scenario == "foreign" ? "isolated.foreign.source" : owner.Source.SourceKey,
                scenario == "wrong_player" ? 2 : 1, scenario == "wrong_rule" ? "other.rule" : "entrepreneur.A02.tie"));
            var value = new JObject { ["pass"] = ScoringModifierProtocol.Value, ["regionId"] = region, ["playerId"] = -1,
                ["controllerPlayerId"] = scenario == "other_controller" ? 2 : 1,
                ["controlReason"] = scenario == "highest" ? "highest" : "tie_claim", ["controlClaims"] = claims };
            string before = JsonConvert.SerializeObject(f.State);
            var records = service.QueryPass(f.State, Request(value, basis)).Where(r => r.Source.AbilityId == "entrepreneur.A02.scoring").ToList();
            Assert.That(records.Count, Is.EqualTo(expected));
            if (expected > 0)
            {
                Assert.That(records.Single().Delta, Is.EqualTo(-2));
                Assert.That(records.Single().ReasonRef, Is.EqualTo("claim:entrepreneur.A02.tie"));
                Assert.That(records.Single().Source.SourceKey, Is.EqualTo(owner.Source.SourceKey));
                Assert.That(records.Single().QueryId, Is.Not.Empty);
                Assert.That(records.Single().BasisDigest, Is.EqualTo(basis));
            }
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before), "区控查询不得写入对局状态");
            f.Artifacts.WriteLabeledText("本轮来源归属隔离输入", value.ToString());
            f.Artifacts.WriteLabeledText("本轮来源归属隔离结果", JsonConvert.SerializeObject(records));
            // 真实声明/来源/生产查询服务；controlClaims是隔离输入，不计正常区控或页面验收。
        }

        [TestCase("opaque.owner", 1)]
        [TestCase("another.owner", 0)]
        public void LuaUsesOpaqueScopeHeaderEvenWhenOwnerIsOutsideFirstPage(string claimSource, int expected)
        {
            using var f = new AgnesContentTestFixture();
            var facts = SourceScope();
            // 第一页均为其他来源；本次授权header才是归属依据，不能猜key或拿第一页凑全集。
            facts["sources"] = new JArray(Enumerable.Range(0, 5).Select(i => new JObject { ["sourceKey"] = "foreign." + i }));
            var result = f.Lua(ValuePayload(claimSource).ToString(), responseKind: "abilityRuleQuery",
                sourceFacts: AgnesContentTestFixture.Snapshot(facts));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(Value(result.AbilityQueryResult, "items").Items.Count, Is.EqualTo(expected));
            Assert.That(Text(result.AbilityQueryResult, "queryId"), Is.EqualTo("qy013.scope.query"));
            Assert.That(Text(result.AbilityQueryResult, "basisDigest"), Is.EqualTo("qy013.scope.basis"));
        }

        [TestCase("missing", "source_facts_unavailable")]
        [TestCase("stale", "source_facts_scope_stale")]
        [TestCase("empty_owner", "agnes_scoring_source_scope_invalid")]
        public void LuaRejectsMissingOrStaleSourceScope(string scenario, string diagnostic)
        {
            using var f = new AgnesContentTestFixture();
            var facts = SourceScope();
            if (scenario == "stale") facts["stateRevision"] = 1;
            if (scenario == "empty_owner") facts["ruleOwnerSourceKey"] = "";
            var result = f.Lua(ValuePayload("opaque.owner").ToString(), responseKind: "abilityRuleQuery",
                sourceFacts: scenario == "missing" ? LuaSnapshotValue.Null : AgnesContentTestFixture.Snapshot(facts));
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Diagnostic, Does.Contain(diagnostic));
        }

        [TestCase("A09", 1)] [TestCase("A08", 1)] [TestCase("A06", 3)]
        [TestCase("A07", 3)] [TestCase("A05", 5)]
        public void DeclarationsKeepQuickSpecialAndCleanupDirectPoliciesSeparate(string face, int round)
        {
            using var f = new AgnesContentTestFixture();
            var definition = Definition(f, round);
            string body = "entrepreneur." + face + ".tactic";
            var source = definition["abilitySources"].Single(s => (string)s["abilityId"] == body);
            var quick = source["actions"].Single(a => (string)a["entryKind"] == "quick");
            var special = source["actions"].Single(a => (string)a["entryKind"] == "special");
            Assert.That((string)quick["bodyHandlerRef"], Is.EqualTo(body));
            Assert.That((string)quick["entryCostRef"], Is.EqualTo(body + ".cost"));
            Assert.That((string)quick["entryAfterEffectsRef"], Is.Empty);
            Assert.That((string)special["bodyHandlerRef"], Is.EqualTo(body));
            Assert.That((string)special["entryCostRef"], Is.Empty);
            Assert.That((string)special["entryAfterEffectsRef"], Is.EqualTo(body + ".after"));
            Assert.That((string)quick["usageTrackId"], Is.Not.Empty);
            Assert.That((string)special["usageTrackId"], Is.EqualTo((string)quick["usageTrackId"]));
            var cost = definition["abilityHandlers"].Single(h => (string)h["handlerId"] == body + ".cost");
            var after = definition["abilityHandlers"].Single(h => (string)h["handlerId"] == body + ".after");
            Assert.That((string)cost["script"], Is.EqualTo("lua/entrepreneurs/agnes.cost.lua"));
            Assert.That((string)after["script"], Is.EqualTo("lua/entrepreneurs/agnes.after.lua"));
            var cleanupDefinition = Definition(f, 5);
            var cleanup = cleanupDefinition["abilitySources"].Single(s => (string)s["abilityId"] == "entrepreneur.A05.cleanup");
            Assert.That((string)cleanup["repeatPolicy"], Is.EqualTo("once"));
            Assert.That((string)cleanup["cleanupInjectionPolicy"], Is.EqualTo("when_available"));
            var policy = cleanupDefinition["actionCallPolicies"].Single(p => (string)p["targetActionId"] == (string)quick["actionId"]);
            Assert.That((string)policy["callerAbilityId"], Is.EqualTo("entrepreneur.A05.cleanup"));
            Assert.That((string)policy["callerHandlerRef"], Is.EqualTo("entrepreneur.A05.cleanup"));
            Assert.That((bool)policy["omitEntryCost"], Is.True);
            Assert.That((bool)policy["omitEntryAfterEffects"], Is.True);
            Assert.That((bool)policy["omitMainAction"], Is.True);
            // 不锁人工静态文案、坐标或样式；费用的真实执行及恢复另有定向用例。
        }

        [TestCase(0, 2)] [TestCase(1, 2)] [TestCase(2, 2)] [TestCase(3, 2)]
        [TestCase(9, 2)] [TestCase(99, 2)] [TestCase(9, 1)]
        public void CommandCostUsesOneExactTokenTransactionAcrossAllInventoryPages(int stock, int cost)
        {
            using var f = new AgnesContentTestFixture();
            var inventory = AgnesContentTests.Inventory(0, -1, 0);
            var facts = AgnesContentTests.CallableFacts(0, stock);
            var actualInput = JsonConvert.SerializeObject(new { stock, cost, inventory, facts });
            f.Artifacts.WriteLabeledText("013分页费用实际源事实输入", actualInput);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = f.Lua("{}", "return {A.payCommand(ctx," + cost + ")}", snapshot: inventory, sourceFacts: facts);
            timer.Stop();
            f.Artifacts.WriteLabeledText("013分页费用预算观测", JsonConvert.SerializeObject(new {
                stock, cost, sourceTokenCount = facts.Get("transferableTokens").Items.Count,
                inputSha256 = LuaContentHasher.ComputeSha256(actualInput),
                moduleSha256 = LuaContentHasher.ComputeSha256(File.ReadAllText(Path.Combine(f.Pack.RootPath, "lua/entrepreneurs/agnes.lua"))),
                maxInstructions = LuaExecutionBudget.Default.MaxInstructions, result.Status, result.FailureCode, result.Diagnostic,
                fixtureLuaAndArtifactMilliseconds = timer.Elapsed.TotalMilliseconds, effects = result.Effects,
                scope = "同一真实源事实输入的隔离构树；计时含夹具与证据I/O，不是纯VM或产品时延" }));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var effect = result.Effects.Single(); var args = effect.RawArguments;
            Assert.That(effect.EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(Number(args, "apiVersion"), Is.EqualTo(3));
            Assert.That(Number(args, "requiredCount"), Is.EqualTo(cost));
            Assert.That(Text(args, "operationProfileId"), Is.EqualTo("core.player_marker_storage"));
            Assert.That(Text(Value(args, "expectedSource"), "zoneId"), Is.EqualTo("commands"));
            Assert.That(Text(Value(args, "destination"), "kind"), Is.EqualTo("original_supply"));
            var ids = Value(args, "itemIds").Items.Select(v => v.StringValue).ToArray();
            Assert.That(ids.Length, Is.EqualTo(System.Math.Min(stock, cost)));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length));
            Assert.That(ids, Is.SubsetOf(Enumerable.Range(0, stock).Select(i => "command." + i).ToArray()));
            // 短缺仍提交requiredCount，由原子预检拒绝；不返回空费用树绕过入口。
        }

        [TestCase(false)] [TestCase(true)]
        public void PureAvailabilityCountsOnlyActualA01CommandsAndIgnoresLegacyTotals(bool actualCommand)
        {
            using var f = new AgnesContentTestFixture();
            var facts = AgnesContentTests.CallableFactsJson(1, actualCommand ? 1 : 0);
            ((JArray)facts["transferableTokens"]).Add(AgnesContentTests.TokenFact("foreign.card.marker", "another.card"));
            ((JArray)facts["transferableTokens"]).Add(AgnesContentTests.TokenFact("other.owner.marker", owner: "p2"));
            facts["transferableTokenCount"] = 99;
            facts["payableMarkerCount"] = 99;
            var result = f.Lua("{}", "return A.availability(ctx)", responseKind: "settlementAvailability",
                sourceFacts: AgnesContentTestFixture.Snapshot(facts));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.SettlementAvailability.Enabled, Is.EqualTo(actualCommand));
        }

        [Test] public void FormalThreeCommandsQuickTransfersTwoBeforeMapAndRestoresWithoutDoubleCost()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            f.Signal("CharacterCoverCompleted", -1, Object("coveredPlayerIds", NormalizedValue.CreateArray(new[] {
                NormalizedValue.CreateStableReference("player", "p1") })));
            f.Answer("1"); Assert.That(f.Stored, Is.EqualTo(3));
            f.Invoke("entrepreneur.A08.tactic.quick");
            f.AssertQuickEntryPending("entrepreneur.A08.tactic.quick");
            Assert.That(f.Stored, Is.EqualTo(3));
            f.Answer("continue");
            Assert.That(f.Stored, Is.EqualTo(1)); f.AssertNoTokenPaymentSelection();
            string request = f.Open.GetStableInteractionId();
            string operation = AssertCommandReceipt(f, 2);
            f.Restore("R2快速3付2后真实地图请求待答");
            Assert.That(f.Open.GetStableInteractionId(), Is.EqualTo(request));
            Assert.That(f.Stored, Is.EqualTo(1)); Assert.That(AssertCommandReceipt(f, 2), Is.EqualTo(operation));
            f.ResolveMapRequests(); Assert.That(f.OwnMapCount, Is.EqualTo(2));
            f.Restore("R2快速正文完成后"); f.Pump();
            Assert.That(f.Stored, Is.EqualTo(1)); Assert.That(f.OwnMapCount, Is.EqualTo(2));
            Assert.That(AssertCommandReceipt(f, 2), Is.EqualTo(operation));
            Assert.That(f.Action("entrepreneur.A08.tactic.special").Enabled, Is.False);
        }

        [TestCase(0)] [TestCase(1)]
        public void FormalQuickShortageKeepsInventoryAndDoesNotActivateTactic(int stock)
        {
            using var f = new AgnesContentTestFixture(); f.Formal();
            f.Choose(1, "A08", stock == 0 ? null : "1", declineGain: stock == 0);
            Assert.That(f.Action("entrepreneur.A08.tactic.quick").Enabled, Is.False);
            Assert.That(f.Stored, Is.EqualTo(stock));
            Assert.That(f.State.EffectRuntime.AbilityActionInvocations.Any(i => i.ActionId == "entrepreneur.A08.tactic.quick"), Is.False);
            f.AssertNoTokenPaymentSelection();
        }

        [Test] public void FormalCleanupTwoCommandsTransfersOneAndRestoresPaidDeclineWithoutRefund()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2"); f.Choose(5, "A05");
            f.Cleanup(); f.StartCleanupOption(); f.Answer("continue");
            Assert.That(f.Stored, Is.EqualTo(1)); string operation = AssertCommandReceipt(f, 1);
            string request = f.Open.GetStableInteractionId();
            Assert.That(f.Open.CandidateIds, Does.Contain(f.Action("entrepreneur.A08.tactic.quick").OptionId));
            f.Restore("R2收尾2付1后战术可放弃选择待答");
            Assert.That(f.Open.GetStableInteractionId(), Is.EqualTo(request)); Assert.That(f.Stored, Is.EqualTo(1));
            f.Decline(); f.Restore("R2收尾付后弃选once关闭"); f.Pump();
            Assert.That(f.Stored, Is.EqualTo(1)); Assert.That(AssertCommandReceipt(f, 1), Is.EqualTo(operation));
            Assert.That(f.Registry.GetSettlementOptions(f.State, 1).Any(o => o.LocalId == "entrepreneur.A05.cleanup.cleanup_order"), Is.False);
            var track = f.State.EffectRuntime.UsageTrackBindings.Single(b => b.TrackId == "entrepreneur.A08.track" && b.PlayerId == 1);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == track.MarkerItemId).SlotId, Is.EqualTo("unused"));
            f.AssertNoTokenPaymentSelection();
        }

        private static string AssertCommandReceipt(AgnesContentTestFixture f, int count)
        {
            var node = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId &&
                Number(n.NormalizedArguments, "apiVersion") == 3 &&
                Text(Value(n.NormalizedArguments, "expectedSource"), "zoneId") == f.Storage.ZoneId);
            Assert.That(Number(node.NormalizedResult, "movedCount"), Is.EqualTo(count));
            var receipt = Value(node.ContinuationData, "transferBatchReceipt");
            Assert.That(Number(receipt, "protocolVersion"), Is.EqualTo(3));
            Assert.That(Number(receipt, "movedCount"), Is.EqualTo(count));
            Assert.That(Text(receipt, "operationId"), Is.EqualTo(node.EffectId + ":move"));
            var ids = Value(node.NormalizedResult, "movedItemIds").Items.Select(v => v.StringValue).ToArray();
            Assert.That(ids.Length, Is.EqualTo(count));
            foreach (string id in ids)
            {
                var item = f.State.ItemZones.Items.Single(i => i.ItemId == id);
                var position = node.PositionChanges.Single(p => p.ItemId == id);
                Assert.That(position.From.ZoneId, Is.EqualTo(f.Storage.ZoneId));
                Assert.That(position.To.ZoneId, Is.EqualTo(item.OriginalSupplyZoneId));
            }
            // 后续正文可合法再使用已返供实例；恢复核历史位置，不要求它永远停在供应区。
            return Text(receipt, "operationId");
        }

        private static JObject Definition(AgnesContentTestFixture f, int round) => JObject.Parse(File.ReadAllText(
            Path.Combine(f.Pack.RootPath, "entrepreneur/entrepreneur.round.A." + round + ".json")));

        private static ScoringQueryRequest Request(JObject facts, string basis) => new ScoringQueryRequest {
            SessionId = "qy013.interface-lua-cleanup.isolated", Pass = (string)facts["pass"],
            RegionId = (string)facts["regionId"], PlayerId = -1, BasisDigest = basis,
            Facts = Normalize(facts) };

        private static NormalizedValue Normalize(JToken token)
        {
            if (token is JObject obj) return NormalizedValue.CreateObject(obj.Properties().Select(p =>
                new NormalizedValueEntry { Name = p.Name, Value = Normalize(p.Value) }).ToList());
            if (token is JArray array) return NormalizedValue.CreateArray(array.Select(Normalize).ToList());
            if (token.Type == JTokenType.Integer) return NormalizedValue.CreateInteger((long)token);
            if (token.Type == JTokenType.String) return NormalizedValue.CreateString((string)token);
            throw new System.InvalidOperationException("qy013_isolated_fact_type_invalid");
        }

        private static JObject ValuePayload(string sourceKey) => new JObject {
            ["abilityId"] = "entrepreneur.A02.scoring", ["queryKind"] = "scoringModifiers.v1",
            ["queryId"] = "qy013.scope.query", ["basisDigest"] = "qy013.scope.basis",
            ["facts"] = new JObject { ["pass"] = ScoringModifierProtocol.Value, ["regionId"] = "isolated.region",
                ["controllerPlayerId"] = 1, ["controlReason"] = "tie_claim",
                ["controlClaims"] = new JArray(new JObject { ["sourceKey"] = sourceKey,
                    ["playerId"] = 1, ["ruleId"] = "entrepreneur.A02.tie" }) } };

        private static JObject SourceScope() => new JObject { ["protocolVersion"] = 1, ["stateRevision"] = 0,
            ["contentHash"] = "qy013.interface-lua-cleanup.snapshot", ["authorizedPlayerId"] = "p1",
            ["ruleOwnerSourceKey"] = "opaque.owner", ["ruleOwnerItemId"] = "isolated.owner",
            ["sources"] = new JArray(), ["grants"] = new JArray() };
    }
}
