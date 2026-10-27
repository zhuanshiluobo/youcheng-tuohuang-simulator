using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class FinalScoringRecoveryTests
    {
        [TestCase("InfluenceCountFacts",false)] [TestCase("PhysicalCountFacts",false)] [TestCase("CityCountFacts",false)]
        [TestCase("InfluenceCountFacts",true)] [TestCase("PhysicalCountFacts",true)] [TestCase("CityCountFacts",true)]
        public void CurrentCountFactListsMustBePresentInSnapshotAndEveryRealStateAfter(string field,bool stateAfter)
        {
            fixture=new FinalScoringTestFixture();var archive=HostRecoveryService.CreateArchive(fixture.State);fixture.Start();fixture.Pump();
            Assert.That(fixture.State.FinalScoring.IsResolved,Is.True);HostRecoveryService.AppendJournal(archive,fixture.State);
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(stateAfter?archive:HostRecoveryService.CreateArchive(fixture.State)));
            var state=stateAfter?(JObject)((JArray)raw["Journal"]["Entries"]).Last(t=>t["StateAfter"] is JObject)["StateAfter"]:(JObject)raw["Snapshot"]["State"];
            var region=(JObject)state["FinalScoring"]["Session"]["Control"]["Regions"][0];Assert.That(region[field],Is.TypeOf<JArray>());region.Remove(field);
            var refused=HostRecoveryService.RecoverJson(raw.ToString(),fixture.Registry);Assert.That(refused.Succeeded,Is.False);Assert.That(refused.Diagnostic,Does.Contain("final_scoring_raw_invalid"));
            fixture.Artifacts.WriteLabeledText("当前CountFacts缺字段拒绝"+field+(stateAfter?"-StateAfter":"-Snapshot"),raw.ToString());
        }
        private static IEnumerable<TestCaseData> QY011B2RawCountFactCases()
        {
            foreach (string field in new[] { "InfluenceCountFacts", "PhysicalCountFacts", "CityCountFacts" })
            foreach (string damage in new[] { "list_object", "null_entry", "count_string", "player_boolean", "case_alias_field", "case_alias_entry" })
            foreach (bool stateAfter in new[] { false, true })
                yield return new TestCaseData(field, damage, stateAfter).SetName("QY011B2原始计数_" + field + "_" + damage + (stateAfter ? "_StateAfter" : "_Snapshot"));
        }
        [TestCaseSource(nameof(QY011B2RawCountFactCases))]
        public void QY011B2CountFactRawDamageIsRejectedInSnapshotAndRealStateAfter(string field, string damage, bool stateAfter)
        {
            var archive = QY011B2LegalArchive(stateAfter); string original = HostRecoveryService.SerializeArchive(archive);
            var raw = JObject.Parse(original); var state = QY011B2RawState(raw, stateAfter);
            var region = (JObject)state["FinalScoring"]["Session"]["Control"]["Regions"][0]; var facts = (JArray)region[field];
            switch (damage)
            {
                case "list_object": region[field] = new JObject(); break;
                case "null_entry": facts[0] = JValue.CreateNull(); break;
                case "count_string": facts[0]["Count"] = ((int)facts[0]["Count"]).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                case "player_boolean": facts[0]["PlayerId"] = true; break;
                case "case_alias_field": region.Add(field.ToLowerInvariant(), facts.DeepClone()); break;
                case "case_alias_entry": ((JObject)facts[0]).Add("playerId", facts[0]["PlayerId"].DeepClone()); break;
                default: throw new ArgumentOutOfRangeException(nameof(damage));
            }
            string damaged = raw.ToString(Formatting.None);
            QY011B2RecoverMustReject(damaged, "原始计数损坏-" + field + "-" + damage, "final_scoring_raw_invalid");
            Assert.That(FinalScoringRecovery.ValidateRaw(state, out string reason), Is.False);
            Assert.That(reason, Does.StartWith("final_scoring_raw_invalid"));
            Assert.That(raw.ToString(Formatting.None), Is.EqualTo(damaged), "原始校验不得补值或删别名");
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original), "合法归档原件不得被负例改写");
        }
        private static IEnumerable<TestCaseData> QY011B2DomainCountFactCases()
        {
            foreach (string field in new[] { "InfluenceCountFacts", "PhysicalCountFacts", "CityCountFacts" })
            foreach (string damage in new[] { "duplicate_player", "reverse_order", "unknown_player", "count_mismatch" })
            foreach (bool stateAfter in new[] { false, true })
                yield return new TestCaseData(field, damage, stateAfter).SetName("QY011B2领域计数_" + field + "_" + damage + (stateAfter ? "_StateAfter" : "_Snapshot"));
        }
        [TestCaseSource(nameof(QY011B2DomainCountFactCases))]
        public void QY011B2NoncanonicalCountFactsAreRejectedByDomainCreationAndRecovery(string field, string damage, bool stateAfter)
        {
            var archive = QY011B2LegalArchive(stateAfter); string original = HostRecoveryService.SerializeArchive(archive);
            var raw = JObject.Parse(original); var state = QY011B2RawState(raw, stateAfter);
            var facts = (JArray)state["FinalScoring"]["Session"]["Control"]["Regions"][0][field];
            var badState = GameStateCloneService.DeepClone(QY011B2SavedState(archive, stateAfter));
            var typed = QY011B2CountFacts(badState.FinalScoring.Session.Control.Regions[0], field);
            Assert.That(typed.Count, Is.EqualTo(2)); Assert.That(typed[0].PlayerId, Is.LessThan(typed[1].PlayerId));
            switch (damage)
            {
                case "duplicate_player": facts[1]["PlayerId"] = facts[0]["PlayerId"].DeepClone(); typed[1].PlayerId = typed[0].PlayerId; break;
                case "reverse_order":
                    var first = facts[0].DeepClone(); var second = facts[1].DeepClone(); facts[0] = second; facts[1] = first;
                    typed.Reverse(); break;
                case "unknown_player": facts[1]["PlayerId"] = 999; typed[1].PlayerId = 999; break;
                case "count_mismatch": facts[0]["Count"] = (int)facts[0]["Count"] + 1; typed[0].Count++; break;
                default: throw new ArgumentOutOfRangeException(nameof(damage));
            }
            QY011B2RecoverMustReject(raw.ToString(Formatting.None), "领域计数损坏-" + field + "-" + damage);
            // 这些负例仍满足JSON shape；拒绝必须来自玩家集合/规范顺序/真实计数语义。
            Assert.That(FinalScoringRecovery.ValidateRaw(state, out string rawReason), Is.True, rawReason);
            string before = JsonConvert.SerializeObject(badState);
            Assert.That(FinalScoringRecovery.TryValidate(badState, fixture.Registry, out string reason), Is.False);
            string expected = damage == "count_mismatch" ? "final_scoring_control_sum_invalid" : "final_scoring_control_count_facts_noncanonical";
            Assert.That(reason, Does.Contain(expected)); Assert.That(JsonConvert.SerializeObject(badState), Is.EqualTo(before));
            var creation = Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateArchive(badState));
            Assert.That(creation.Message, Does.Contain(expected), "另核CreateArchive的先拒，不能当作RecoverJson已拒");
            Assert.That(JsonConvert.SerializeObject(badState), Is.EqualTo(before));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }
        private static IEnumerable<TestCaseData> QY011B2DuplicateCountKeys()
        {
            foreach (string field in new[] { "InfluenceCountFacts", "PhysicalCountFacts", "CityCountFacts" })
            foreach (bool entry in new[] { false, true })
            foreach (bool stateAfter in new[] { false, true })
                yield return new TestCaseData(field, entry, stateAfter).SetName("QY011B2同名重复键_" + field + (entry ? "_Count" : "_List") + (stateAfter ? "_StateAfter" : "_Snapshot"));
        }
        [TestCaseSource(nameof(QY011B2DuplicateCountKeys))]
        public void QY011B2HostMustRejectExactDuplicateCountFactKeysBeforeTheyAreLost(string field, bool entry, bool stateAfter)
        {
            var archive = QY011B2LegalArchive(stateAfter); string original = HostRecoveryService.SerializeArchive(archive);
            var raw = JObject.Parse(original); var region = (JObject)QY011B2RawState(raw, stateAfter)["FinalScoring"]["Session"]["Control"]["Regions"][0];
            var owner = entry ? (JObject)region[field][0] : region; string name = entry ? "Count" : field;
            const string marker = "QY011B2DuplicateKeyOnly";
            owner.Property(name).AddAfterSelf(new JProperty(marker, owner[name].DeepClone()));
            string encoded = raw.ToString(Formatting.None); Assert.That(encoded.Split(new[] { "\"" + marker + "\":" }, StringSplitOptions.None).Length, Is.EqualTo(2));
            string damaged = encoded.Replace("\"" + marker + "\":", "\"" + name + "\":");
            Assert.That(damaged, Does.Not.Contain(marker)); Assert.That(damaged, Is.Not.EqualTo(original));
            // 故意直接传重复键原串；先JObject.Parse再转存会丢证据，不能替代这个Host边界。
            QY011B2RecoverMustReject(damaged, "同名重复键-" + field + (entry ? "-Count" : "-List"));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }
        [Test] public void QY011B2LegacyCompletedWithoutSessionKeepsActualResultAndNeverReplaysIncome()
        {
            // 旧形状兼容定位：结果/完成节点来自真实当前结算，仅去掉新Session及其阶段标记；不是旧构建运行证据。
            var archive = QY011B2LegalArchive(false); var raw = JObject.Parse(HostRecoveryService.SerializeArchive(archive));
            var state = QY011B2RawState(raw, false); var session = (JObject)state["FinalScoring"]["Session"];
            string ownerId = (string)session["OwnerEffectId"]; string actualResult = ((JObject)state["FinalScoring"]).ToString(Formatting.None);
            var owner = ((JArray)state["EffectRuntime"]["EffectNodes"]).OfType<JObject>().Single(n => (string)n["EffectId"] == ownerId);
            Assert.That((string)owner["FlowStage"], Is.EqualTo(FinalScoringSessionService.Finished));
            ((JObject)state["FinalScoring"]).Remove("Session"); owner["FlowStage"] = "";
            var expected = JObject.Parse(actualResult); expected.Remove("Session");
            string legacy = raw.ToString(Formatting.None); fixture.Artifacts.WriteLabeledText("旧完成形状兼容输入非旧版本实跑", legacy);
            Assert.That(FinalScoringRecovery.ValidateRaw(state, out string rawReason), Is.True, rawReason);
            var restored = HostRecoveryService.RecoverJson(legacy, fixture.Registry); Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            fixture.State = restored.State; Assert.That(fixture.State.FinalScoring.Session, Is.Null);
            var final = JObject.FromObject(fixture.State.FinalScoring); final.Remove("Session");
            Assert.That(JToken.DeepEquals(final, expected), Is.True, "保留实际完成分项、胜者及资源，不倒算");
            string before = JsonConvert.SerializeObject(fixture.State);
            Assert.That(FinalScoringRecovery.TryValidate(fixture.State, fixture.Registry, out string reason), Is.True, reason);
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before), "领域校验不得改输入");
            string balances = JsonConvert.SerializeObject(fixture.State.Players.Select(p => new { p.PlayerId, p.Score, p.Resources }));
            int started = fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted");
            fixture.Pump();
            Assert.That(JsonConvert.SerializeObject(fixture.State.Players.Select(p => new { p.PlayerId, p.Score, p.Resources })), Is.EqualTo(balances));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(started), "完成节点不得重放Started或收益");
            fixture.Restore("旧完成形状再次保存恢复");
            Assert.That(fixture.State.FinalScoring.Session, Is.Null); var again = JObject.FromObject(fixture.State.FinalScoring); again.Remove("Session");
            Assert.That(JToken.DeepEquals(again, expected), Is.True);
        }
        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void QY011B2StartedWithoutSessionIsRejectedForCurrentAndLegacyStageShapes(bool stateAfter, bool legacyStage)
        {
            var archive = QY011B2LegalArchive(stateAfter, startedOnly: true); string original = HostRecoveryService.SerializeArchive(archive);
            var raw = JObject.Parse(original); var state = QY011B2RawState(raw, stateAfter);
            var badState = GameStateCloneService.DeepClone(QY011B2SavedState(archive, stateAfter));
            string ownerId = badState.FinalScoring.Session.OwnerEffectId;
            Assert.That(badState.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
            ((JObject)state["FinalScoring"]).Remove("Session"); badState.FinalScoring.Session = null;
            if (legacyStage)
            {
                ((JArray)state["EffectRuntime"]["EffectNodes"]).OfType<JObject>().Single(n => (string)n["EffectId"] == ownerId)["FlowStage"] = "";
                badState.EffectRuntime.EffectNodes.Single(n => n.EffectId == ownerId).FlowStage = "";
            }
            QY011B2RecoverMustReject(raw.ToString(Formatting.None), legacyStage ? "旧Started无Session" : "当前Started丢Session", legacyStage ? "legacy_final_scoring_requires_original_execution_version" : "final_scoring_raw_invalid");
            bool rawValid = FinalScoringRecovery.ValidateRaw(state, out _); Assert.That(rawValid, Is.EqualTo(legacyStage));
            string before = JsonConvert.SerializeObject(badState);
            Assert.That(FinalScoringRecovery.TryValidate(badState, fixture.Registry, out string reason), Is.False);
            string expected = legacyStage ? "legacy_final_scoring_requires_original_execution_version" : "final_scoring_session_missing";
            Assert.That(reason, Does.Contain(expected));
            var creation = Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateArchive(badState)); Assert.That(creation.Message, Does.Contain(expected));
            Assert.That(JsonConvert.SerializeObject(badState), Is.EqualTo(before)); Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
        }
        private HostSessionArchiveDto QY011B2LegalArchive(bool stateAfter, bool startedOnly = false)
        {
            fixture = new FinalScoringTestFixture(); fixture.PlacePlayer(1, 1);
            var before = HostRecoveryService.CreateArchive(fixture.State); fixture.Start();
            if (startedOnly) fixture.Until(() => fixture.State.FinalScoring?.Session != null && fixture.State.FinalScoring.Session.StartedEventId.Length > 0 && fixture.State.EffectRuntime.RuleEvents.Any(e => e.EventType == "FinalScoringStarted"));
            else fixture.Pump();
            var archive = stateAfter ? before : HostRecoveryService.CreateArchive(fixture.State);
            if (stateAfter)
            {
                HostRecoveryService.AppendJournal(archive, fixture.State);
                Assert.That(archive.Journal.Entries.Any(e => e.StateAfter != null), Is.True, "必须由真实AppendJournal生成checkpoint");
            }
            string original = HostRecoveryService.SerializeArchive(archive); fixture.Artifacts.WriteLabeledText("B2合法原归档" + (stateAfter ? "-StateAfter" : "-Snapshot"), original);
            var restored = HostRecoveryService.RecoverJson(original, fixture.Registry); Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            Assert.That(restored.State.FinalScoring.Session.SessionId, Is.EqualTo(fixture.State.FinalScoring.Session.SessionId));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(original));
            return archive;
        }
        private static JObject QY011B2RawState(JObject archive, bool stateAfter) => stateAfter
            ? (JObject)((JArray)archive["Journal"]["Entries"]).Last(e => e["StateAfter"] is JObject)["StateAfter"]
            : (JObject)archive["Snapshot"]["State"];
        private static GameState QY011B2SavedState(HostSessionArchiveDto archive, bool stateAfter) => stateAfter
            ? archive.Journal.Entries.Last(e => e.StateAfter != null).StateAfter : archive.Snapshot.State;
        private static List<PlayerInfluenceCountState> QY011B2CountFacts(FinalRegionControlSnapshot region, string field)
        {
            switch (field)
            {
                case "InfluenceCountFacts": return region.InfluenceCountFacts;
                case "PhysicalCountFacts": return region.PhysicalCountFacts;
                case "CityCountFacts": return region.CityCountFacts;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
        private void QY011B2RecoverMustReject(string json, string label, string diagnostic = null)
        {
            string before = JsonConvert.SerializeObject(fixture.State);
            fixture.Artifacts.WriteLabeledText(label + "-原始坏档", json);
            var result = HostRecoveryService.RecoverJson(json, fixture.Registry);
            fixture.Artifacts.WriteLabeledText(label + "-实际恢复结果", JsonConvert.SerializeObject(new { result.Succeeded, result.FaultCode, result.Diagnostic }));
            Assert.That(result.Succeeded, Is.False, "必须由RecoverJson拒绝，不能拿CreateArchive先拒充数");
            Assert.That(result.FaultCode, Is.Not.Empty); Assert.That(result.Diagnostic, Is.Not.Empty);
            if (diagnostic != null) Assert.That(result.Diagnostic, Does.Contain(diagnostic));
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
        }
        private FinalScoringTestFixture fixture;
        [TearDown] public void FinishArtifacts() => fixture?.Finish();
        [TestCase(FinalScoringSessionService.Prepare)]
        [TestCase(FinalScoringSessionService.Special)]
        [TestCase(FinalScoringSessionService.RegionCommit)]
        [TestCase(FinalScoringSessionService.ResourceSnapshot)]
        [TestCase(FinalScoringSessionService.ResourceCommit)]
        [TestCase(FinalScoringSessionService.Complete)]
        [TestCase(FinalScoringSessionService.Finished)]
        public void EachSavedStageRestoresAndCommitsIncomeOnlyOnce(string stage)
        {
            fixture = new FinalScoringTestFixture(eventScript: "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=3}),Effect.GainResource({recipient=ctx.playerId,resourceType='Iron',amount=1})} end");
            fixture.State.FindPlayer(1).Resources.Iron = 5; fixture.Start(); fixture.Until(() => fixture.Owner.FlowStage == stage);
            Assert.That(FinalScoringRecovery.ValidateRaw(JObject.FromObject(fixture.State), out string rawReason), Is.True, rawReason);
            Assert.That(FinalScoringRecovery.TryValidate(fixture.State, fixture.Registry, out string reason), Is.True, reason);
            fixture.Restore("阶段" + stage); Assert.That(FinalScoringRecovery.TryValidate(fixture.State, fixture.Registry, out reason), Is.True, reason);
            fixture.Pump(); fixture.Pump();
            Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(4)); Assert.That(fixture.State.FindPlayer(1).Resources.Iron, Is.EqualTo(6));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
        }
        [Test] public void MissingProtocolFieldIsRejectedBeforeDefaultValuesCanRepairIt()
        {
            fixture = new FinalScoringTestFixture(); fixture.Start(); fixture.Until(() => fixture.Owner.FlowStage == FinalScoringSessionService.Special);
            var raw = JObject.FromObject(fixture.State); ((JObject)raw["FinalScoring"]["Session"]).Remove("ProtocolVersion");
            Assert.That(FinalScoringRecovery.ValidateRaw(raw, out _), Is.False);
        }
        [Test] public void CompletedEffectWithUnarchivedScoreCanRestoreDuringSpecial()
        {
            fixture = new FinalScoringTestFixture(eventScript: "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=2})} end");
            fixture.Start(); fixture.Until(() => fixture.State.FindPlayer(1).Score == 2 && fixture.Owner.FlowStage == FinalScoringSessionService.Special);
            Assert.That(FinalScoringRecovery.TryValidate(fixture.State, fixture.Registry, out string reason), Is.True, reason);
            fixture.Restore("特殊分已发尚未归档"); fixture.Pump(); Assert.That(fixture.State.FindPlayer(1).Score, Is.EqualTo(2));
        }
        [Test] public void CorruptedStageReceiptsRejectWithoutReplayingOrRepairing()
        {
            fixture = new FinalScoringTestFixture(); fixture.Start(); fixture.Until(() => fixture.Owner.FlowStage == FinalScoringSessionService.ResourceSnapshot);
            fixture.State.FinalScoring.Session.RegionCommitReceipt = "corrupted";
            string before = JsonConvert.SerializeObject(fixture.State);
            Assert.That(FinalScoringRecovery.TryValidate(fixture.State, fixture.Registry, out _), Is.False); Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
        }
        [Test] public void HostRecoveryMustUseNewRawAndDomainValidators()
        {
            // 本例故意通过正式Host入口；整合漏接新验证器就应失败，不以直接调用验证器代替它。
            fixture = new FinalScoringTestFixture(); fixture.Start(); fixture.Until(() => fixture.Owner.FlowStage == FinalScoringSessionService.Special);
            string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(fixture.State));
            var corrupt = JObject.Parse(archive);
            foreach (var token in corrupt.SelectTokens("$..Session").OfType<JObject>().ToArray()) token.Remove("OwnerEffectId");
            fixture.Artifacts.WriteText("终局缺归属字段原始负例.archive.json", corrupt.ToString());
            var result = HostRecoveryService.RecoverJson(corrupt.ToString(), fixture.Registry);
            Assert.That(result.Succeeded, Is.False);
        }
        [Test] public void SavedZeroContributionAndExplicitNullsRemainValid()
        {
            // 使用正式合约Lua产生零贡献，不向保存数据补造delta。
            fixture = new FinalScoringTestFixture(companyCount: 0, holders: 2);
            fixture.Start(); fixture.Until(() => fixture.Owner.FlowStage == FinalScoringSessionService.Special);
            var session = fixture.State.FinalScoring.Session;
            Assert.That(session.Control.Regions.SelectMany(r => r.InfluenceModifiers).Any(), Is.True);
            Assert.That(session.Control.Regions.SelectMany(r => r.InfluenceModifiers).All(m => m.Delta == 0), Is.True);
            Assert.That(session.Players.All(p => p.Resources == null && p.Profile == null), Is.True);
            Assert.That(session.Control.Regions.All(r => r.ControllerPlayerId == null && r.ControlClaims.Count == 0), Is.True);
            AssertValidWithoutMutation(); fixture.Restore("合法零贡献及显式空值"); AssertValidWithoutMutation();
        }
        [Test] public void EmptySavedProfileReplacesDefaultAndCompletedResultRestoresWithoutMutation()
        {
            BuildCompletedFixture(emptyProfile: true);
            Assert.That(fixture.State.FinalScoring.Session.Players.All(p => p.Profile != null && p.Profile.Entries.Count == 0 && p.ResourceScore == 0), Is.True);
            Assert.That(FinalScoringService.CalculateResourceScore(fixture.State.FindPlayer(1).Resources), Is.GreaterThan(0));
            string before = JsonConvert.SerializeObject(fixture.State);
            AssertValidWithoutMutation(); fixture.Restore("合法空资源方案及完成结果"); AssertValidWithoutMutation();
            Assert.That(JsonConvert.SerializeObject(fixture.State.FinalScoring), Is.EqualTo(JObject.Parse(before)["FinalScoring"].ToString(Formatting.None)));
        }
        [Test] public void CompletedResultAcceptsEquivalentCollectionOrderWithoutRewritingIt()
        {
            BuildCompletedFixture();
            fixture.State.FinalScoring.PlayerScores.Reverse(); fixture.State.FinalScoring.RegionScores.Reverse();
            foreach (var region in fixture.State.FinalScoring.RegionScores) region.InfluenceCounts.Reverse();
            foreach (var player in fixture.State.FinalScoring.PlayerScores) player.ControlledRegionIds.Reverse();
            fixture.State.FinalScoring.WinnerPlayerIds.Reverse();
            AssertValidWithoutMutation(); fixture.Restore("合法完成集合顺序"); AssertValidWithoutMutation();
        }
        [TestCase("{unexpected=1}")]
        [TestCase("{Iron={units=4,points=1}}")]
        public void EmptyProfileCompatibilityDoesNotAcceptNonemptyLuaObjects(string entries)
        {
            string script = FinalScoringTestFixture.ReturnItem("{kind='resource_profile',playerId=me,profileId='fixture.invalid',entries=" + entries + "}");
            fixture = new FinalScoringTestFixture(new[] { (script, new[] { "resource_profile" }) });
            string before = JsonConvert.SerializeObject(fixture.State);
            Assert.Throws<InvalidOperationException>(() => fixture.Queries.QueryResourceProfile(fixture.State, "test", 1));
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
        }
        [TestCase("region", "InfluenceModifiers[0].Delta")]
        [TestCase("region", "InfluenceModifiers[0].Entries")]
        [TestCase("region", "InfluenceModifiers[0].Source.AuthorizedPlayerId")]
        [TestCase("region", "InfluenceModifiers[0].Source.GrantingSourceRefs")]
        [TestCase("region", "InfluenceModifiers[0].Version.ExecutorVersion")]
        [TestCase("region", "TieModifiers[0].RuleId")]
        [TestCase("region", "TieModifiers[0].Source.AuthorizationRef")]
        [TestCase("region", "ValueModifiers[0].Delta")]
        [TestCase("region", "ValueModifiers[0].Version.HandlerHash")]
        [TestCase("region", "ControlClaims[0].PlayerId")]
        [TestCase("region", "ControlClaims[0].SourceKey")]
        [TestCase("source", "Source.AuthorizationRef")]
        [TestCase("source", "Version.ContractVersion")]
        [TestCase("player", "Resources.OriginiumShard")]
        [TestCase("player", "Resources")]
        [TestCase("player", "Profile")]
        [TestCase("player", "Profile.PlayerId")]
        [TestCase("player", "Profile.Source.AuthorizedPlayerId")]
        [TestCase("player", "Profile.Version.ExecutorVersion")]
        [TestCase("player", "Profile.Entries")]
        [TestCase("player", "Profile.Entries[0].Units")]
        [TestCase("player", "Profile.Entries[0].Points")]
        [TestCase("effect", "ScoreDelta")]
        [TestCase("effect", "Evidence")]
        [TestCase("effect", "Source.AuthorizedPlayerId")]
        [TestCase("effect", "Version.ExecutorVersion")]
        [TestCase("resultRegion", "ScoreValue")]
        [TestCase("resultRegion", "InfluenceCounts[0].Count")]
        [TestCase("resultPlayer", "ControlledRegionIds")]
        [TestCase("resultPlayer", "RemainingResources.OriginiumShard")]
        [TestCase("final", "IsResolved")]
        [TestCase("final", "PlayerScores")]
        [TestCase("final", "RegionScores")]
        [TestCase("final", "WinnerPlayerIds")]
        public void EveryRequiredNestedFieldMustBeSavedInsteadOfDefaulted(string owner, string path)
        {
            BuildCompletedFixture(); var raw = JObject.FromObject(fixture.State);
            var token = RawOwner(raw, owner).SelectToken(path); Assert.That(token, Is.Not.Null, path);
            if (path.EndsWith(".Delta", StringComparison.Ordinal)) Assert.That((int)token, Is.Zero);
            ((JProperty)token.Parent).Remove(); AssertRawRejectedWithoutMutation(raw);
        }
        [TestCase("region", "InfluenceModifiers[0].Delta", "\"0\"")]
        [TestCase("region", "InfluenceModifiers[0].Delta", "0.0")]
        [TestCase("region", "InfluenceModifiers[0].Delta", "2147483648")]
        [TestCase("region", "ControllerPlayerId", "2147483648")]
        [TestCase("region", "ControllerPlayerId", "\"1\"")]
        [TestCase("region", "InfluenceCountFacts", "{}")]
        [TestCase("region", "InfluenceCountFacts[0].Count", "\"1\"")]
        [TestCase("region", "ControlClaims", "null")]
        [TestCase("region", "TieModifiers[0]", "null")]
        [TestCase("source", "Source", "null")]
        [TestCase("source", "Version", "[]")]
        [TestCase("source", "Source.GrantingSourceRefs", "[null]")]
        [TestCase("player", "Resources.Iron", "null")]
        [TestCase("player", "Profile.Source", "null")]
        [TestCase("player", "Profile.Entries", "null")]
        [TestCase("player", "Profile.Entries", "[null]")]
        [TestCase("player", "Profile.Entries[0].Points", "true")]
        [TestCase("resultPlayer", "RemainingResources", "null")]
        [TestCase("resultRegion", "InfluenceCounts", "null")]
        public void NestedTypesAndNullableIntegerRangeAreCheckedBeforeDeserialization(string owner, string path, string replacement)
        {
            BuildCompletedFixture(); var raw = JObject.FromObject(fixture.State);
            var token = RawOwner(raw, owner).SelectToken(path); Assert.That(token, Is.Not.Null, path);
            token.Replace(JToken.Parse(replacement)); AssertRawRejectedWithoutMutation(raw);
        }
        [TestCase(false)] [TestCase(true)]
        public void NestedUnknownFieldAndNoncanonicalPlayerCountAreRejected(bool countAlias)
        {
            BuildCompletedFixture(); var raw = JObject.FromObject(fixture.State);
            if (countAlias)
            {
                var rows = (JArray)RawOwner(raw, "region")["InfluenceCountFacts"];
                rows.Add(rows[0].DeepClone());
            }
            else ((JObject)RawOwner(raw, "source")["Source"]).Add("Unexpected", 0);
            if (countAlias)
            {
                string rawBefore = raw.ToString(Formatting.None);
                Assert.That(FinalScoringRecovery.ValidateRaw(raw, out string rawReason), Is.True, rawReason);
                Assert.That(raw.ToString(Formatting.None), Is.EqualTo(rawBefore));
                var corrupt = GameStateCloneService.DeepClone(fixture.State);
                var facts = corrupt.FinalScoring.Session.Control.Regions.Single(r => r.RegionId == fixture.RegionId).InfluenceCountFacts;
                facts.Add(new PlayerInfluenceCountState { PlayerId = facts[0].PlayerId, Count = facts[0].Count });
                string before = JsonConvert.SerializeObject(corrupt);
                Assert.That(FinalScoringRecovery.TryValidate(corrupt, fixture.Registry, out string reason), Is.False);
                Assert.That(reason, Does.Contain("final_scoring_control_count_facts_noncanonical"));
                Assert.That(JsonConvert.SerializeObject(corrupt), Is.EqualTo(before));
            }
            else AssertRawRejectedWithoutMutation(raw);
            var legal = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(fixture.State));
            Assert.That(HostRecoveryService.RecoverJson(legal, fixture.Registry).Succeeded, Is.True);
            fixture.Artifacts.WriteLabeledText("嵌套字段合法归档对照", legal);
            var archive = JObject.Parse(legal); archive["Snapshot"]["State"]["FinalScoring"] = raw["FinalScoring"].DeepClone();
            QY011B2RecoverMustReject(archive.ToString(Formatting.None), "嵌套未知或非规范玩家实际恢复", countAlias ?
                "final_scoring_control_count_facts_noncanonical" : "final_scoring_raw_invalid");
        }
        [TestCase("Source.AuthorizedPlayerId")]
        [TestCase("Version.ExecutorVersion")]
        [TestCase("Evidence")]
        [TestCase("Evidence.Properties[0].Value.IntegerValue")]
        public void FactEvidenceAndItsNestedNormalizedValuesCannotAcquireDefaults(string path)
        {
            // 真实WithContext事实，依赖公共Global.Scoring接线；未接线时应失败，不注入替身。
            const string script = @"return function(ctx) if ctx.eventType=='EffectCompleted' then return {} end
              local f=Global.Scoring.GetFinalControl()
              return {Effect.WithContext(Effect.Repeat({apiVersion=1,count=0,effects={}}),
                {scoringEvidence={sessionId=f.sessionId,snapshotId=f.snapshotId,count=0}})} end";
            fixture = new FinalScoringTestFixture(eventScript: script, completionHandler: true); fixture.Start(); fixture.Pump();
            Assert.That(fixture.State.FinalScoring.Session.Facts.Count, Is.GreaterThan(0)); AssertValidWithoutMutation();
            var raw = JObject.FromObject(fixture.State); var fact = (JObject)raw["FinalScoring"]["Session"]["Facts"][0];
            var token = fact.SelectToken(path); Assert.That(token, Is.Not.Null, path); ((JProperty)token.Parent).Remove();
            AssertRawRejectedWithoutMutation(raw);
        }
        [Test] public void HostRecoveryRejectsMissingNestedZeroDeltaInOriginalJson()
        {
            fixture = new FinalScoringTestFixture(companyCount: 0, holders: 2); fixture.Start(); fixture.Pump();
            AssertValidWithoutMutation();
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(fixture.State)));
            var sessions = archive.SelectTokens("$..Session").OfType<JObject>().ToArray(); Assert.That(sessions, Is.Not.Empty);
            foreach (var session in sessions)
            {
                var modifier = (JObject)session["Control"]["Regions"][0]["InfluenceModifiers"][0];
                Assert.That((int)modifier["Delta"], Is.Zero); modifier.Remove("Delta");
            }
            string corrupt = archive.ToString(); fixture.Artifacts.WriteText("终局嵌套零贡献缺字段负例.archive.json", corrupt);
            var result = HostRecoveryService.RecoverJson(corrupt, fixture.Registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Does.Contain("final_scoring_raw_invalid"));
        }
        [TestCase("region_missing", "final_region_set_mismatch")]
        [TestCase("region_duplicate", "final_region_set_mismatch")]
        [TestCase("region_replaced", "final_region_set_mismatch")]
        [TestCase("region_score", "final_region_result_mismatch")]
        [TestCase("region_controller", "final_region_result_mismatch")]
        [TestCase("influence_missing", "final_region_result_mismatch")]
        [TestCase("influence_duplicate", "final_region_result_mismatch")]
        [TestCase("influence_replaced", "final_region_result_mismatch")]
        [TestCase("influence_count", "final_region_result_mismatch")]
        [TestCase("influence_null", "final_region_result_mismatch")]
        [TestCase("player_missing", "final_player_set_mismatch")]
        [TestCase("player_duplicate", "final_player_set_mismatch")]
        [TestCase("player_replaced", "final_player_set_mismatch")]
        [TestCase("player_total", "final_result_mismatch")]
        [TestCase("controlled_missing", "final_controlled_regions_mismatch")]
        [TestCase("controlled_duplicate", "final_controlled_regions_mismatch")]
        [TestCase("controlled_replaced", "final_controlled_regions_mismatch")]
        [TestCase("controlled_null", "final_controlled_regions_mismatch")]
        [TestCase("remaining_null", "final_remaining_resources_mismatch")]
        [TestCase("Originium", "final_remaining_resources_mismatch")]
        [TestCase("OriginiumShard", "final_remaining_resources_mismatch")]
        [TestCase("Iron", "final_remaining_resources_mismatch")]
        [TestCase("PureOriginium", "final_remaining_resources_mismatch")]
        [TestCase("GoldVoucher", "final_remaining_resources_mismatch")]
        public void CompletedDisplayMustMatchSavedControlAndLedgersWithoutRepair(string corruption, string diagnostic)
        {
            BuildCompletedFixture(); var original = JsonConvert.SerializeObject(fixture.State);
            var corrupt = GameStateCloneService.DeepClone(fixture.State); CorruptCompletedDisplay(corrupt, corruption);
            string before = JsonConvert.SerializeObject(corrupt);
            Assert.That(FinalScoringRecovery.TryValidate(corrupt, fixture.Registry, out string reason), Is.False);
            Assert.That(reason, Does.Contain(diagnostic)); Assert.That(JsonConvert.SerializeObject(corrupt), Is.EqualTo(before));
            // 使用真实RecoverJson核公共整合；只有直接调用验证器拒绝不能算Host已覆盖。
            string legal = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(fixture.State));
            var control = HostRecoveryService.RecoverJson(legal, fixture.Registry);
            Assert.That(control.Succeeded, Is.True, control.Diagnostic);
            fixture.Artifacts.WriteLabeledText("完成展示合法恢复对照-" + corruption, legal);
            var archive = JObject.Parse(legal);
            archive["Snapshot"]["State"]["FinalScoring"] = JObject.FromObject(corrupt.FinalScoring);
            string damaged = archive.ToString(Formatting.None);
            fixture.Artifacts.WriteText("完成展示篡改-" + corruption + ".archive.json", damaged);
            var result = HostRecoveryService.RecoverJson(damaged, fixture.Registry);
            bool missingRequiredObject = corruption == "influence_null" || corruption == "controlled_null" || corruption == "remaining_null";
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Does.Contain(missingRequiredObject ? "final_scoring_raw_invalid" : diagnostic));
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(original));
        }
        private void BuildCompletedFixture(bool emptyProfile = false)
        {
            string entries = emptyProfile ? "{}" : "{{resourceType='Iron',units=4,points=1}}";
            string script = @"return function(ctx) local f=ctx.payload.facts; local me=ctx.payload.authorizedPlayerId; local items={}
              if f.pass=='influence_delta' then items={{kind='influence_delta',regionId=f.regionId,playerId=me,delta=0}}
              elseif f.pass=='tie_claim' and me==1 then items={{kind='tie_claim',regionId=f.regionId,playerId=me,ruleId='fixture.recovery'}}
              elseif f.pass=='region_value_delta' and me==1 then items={{kind='region_value_delta',regionId=f.regionId,delta=0,reasonRef='region'}}
              elseif f.pass=='resource_profile' then items={{kind='resource_profile',playerId=me,profileId='fixture.recovery',entries=" + entries + @"}} end
              return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items} end";
            fixture = new FinalScoringTestFixture(new[] { (script, new[] { "influence_delta", "tie_claim", "region_value_delta", "resource_profile" }) },
                eventScript: "return function(ctx) return {Effect.GainScore({recipient=ctx.playerId,amount=1}),Effect.GainResource({recipient=ctx.playerId,resourceType='Iron',amount=1})} end");
            fixture.PlacePlayer(1, 1); fixture.PlacePlayer(2, 1, skip: 1);
            fixture.State.FindPlayer(1).Resources = new ResourceSet { Originium = 9, OriginiumShard = 8, Iron = 6, PureOriginium = 2, GoldVoucher = 3 };
            fixture.Start(); fixture.Pump(); Assert.That(fixture.State.FinalScoring.IsResolved, Is.True);
            Assert.That(fixture.State.FinalScoring.Session.Control.Regions.Single(r => r.RegionId == fixture.RegionId).ControllerPlayerId, Is.EqualTo(1));
            AssertValidWithoutMutation();
        }
        private JObject RawOwner(JObject raw, string owner)
        {
            var final = (JObject)raw["FinalScoring"]; var session = (JObject)final["Session"];
            switch (owner)
            {
                case "final": return final;
                case "region": return ((JArray)session["Control"]["Regions"]).OfType<JObject>().Single(r => (string)r["RegionId"] == fixture.RegionId);
                case "source": return (JObject)session["Control"]["Sources"][0];
                case "player": return (JObject)session["Players"][0];
                case "effect": return (JObject)session["Effects"][0];
                case "resultRegion": return ((JArray)final["RegionScores"]).OfType<JObject>().Single(r => (string)r["RegionId"] == fixture.RegionId);
                case "resultPlayer": return ((JArray)final["PlayerScores"]).OfType<JObject>().Single(p => (int)p["PlayerId"] == 1);
                default: throw new ArgumentOutOfRangeException(nameof(owner));
            }
        }
        private void AssertValidWithoutMutation()
        {
            string before = JsonConvert.SerializeObject(fixture.State); var raw = JObject.Parse(before); string rawBefore = raw.ToString(Formatting.None);
            Assert.That(FinalScoringRecovery.ValidateRaw(raw, out string rawReason), Is.True, rawReason);
            Assert.That(raw.ToString(Formatting.None), Is.EqualTo(rawBefore));
            Assert.That(FinalScoringRecovery.TryValidate(fixture.State, fixture.Registry, out string reason), Is.True, reason);
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before));
        }
        private static void AssertRawRejectedWithoutMutation(JObject raw)
        {
            string before = raw.ToString(Formatting.None);
            Assert.That(FinalScoringRecovery.ValidateRaw(raw, out string reason), Is.False);
            Assert.That(reason, Does.StartWith("final_scoring_raw_invalid")); Assert.That(raw.ToString(Formatting.None), Is.EqualTo(before));
        }
        private void CorruptCompletedDisplay(GameState state, string corruption)
        {
            var final = state.FinalScoring; var region = final.RegionScores.Single(r => r.RegionId == fixture.RegionId);
            var player = final.PlayerScores.Single(p => p.PlayerId == 1);
            switch (corruption)
            {
                case "region_missing": final.RegionScores.RemoveAt(0); break;
                case "region_duplicate": final.RegionScores[1].RegionId = final.RegionScores[0].RegionId; break;
                case "region_replaced": region.RegionId = "unknown.region"; break;
                case "region_score": region.ScoreValue++; break;
                case "region_controller": region.ControllerPlayerId = -1; break;
                case "influence_missing": region.InfluenceCounts.RemoveAt(0); break;
                case "influence_duplicate": region.InfluenceCounts[1].PlayerId = region.InfluenceCounts[0].PlayerId; break;
                case "influence_replaced": region.InfluenceCounts[0].PlayerId = 99; break;
                case "influence_count": region.InfluenceCounts[0].Count++; break;
                case "influence_null": region.InfluenceCounts = null; break;
                case "player_missing": final.PlayerScores.RemoveAt(0); break;
                case "player_duplicate": final.PlayerScores[1].PlayerId = final.PlayerScores[0].PlayerId; break;
                case "player_replaced": player.PlayerId = 99; break;
                case "player_total": player.TotalScore++; break;
                case "controlled_missing": player.ControlledRegionIds.Clear(); break;
                case "controlled_duplicate": player.ControlledRegionIds.Add(player.ControlledRegionIds[0]); break;
                case "controlled_replaced": player.ControlledRegionIds[0] = "unknown.region"; break;
                case "controlled_null": player.ControlledRegionIds = null; break;
                case "remaining_null": player.RemainingResources = null; break;
                default:
                    var resource = (ResourceType)Enum.Parse(typeof(ResourceType), corruption);
                    player.RemainingResources.Set(resource, player.RemainingResources.Get(resource) + 1); break;
            }
        }
        [Test] public void OldResolvedResultIsPreservedWithoutInventingNewHistory()
        {
            fixture = new FinalScoringTestFixture(); fixture.State.FinalScoring = new FinalScoringState { IsResolved = true, TiebreakSummary = "旧结果" };
            string before = JsonConvert.SerializeObject(fixture.State);
            Assert.That(FinalScoringRecovery.TryValidate(fixture.State, fixture.Registry, out _), Is.True);
            Assert.That(JsonConvert.SerializeObject(fixture.State), Is.EqualTo(before)); Assert.That(fixture.State.FinalScoring.Session, Is.Null);
            var raw = JObject.FromObject(fixture.State); ((JObject)raw["FinalScoring"]).Remove("Session");
            ((JObject)raw["FinalScoring"]).Remove("PlayerScores"); ((JObject)raw["FinalScoring"]).Remove("RegionScores");
            Assert.That(FinalScoringRecovery.ValidateRaw(raw, out _), Is.True, "旧无Session结果不强加新嵌套字段。");
        }
    }
}
