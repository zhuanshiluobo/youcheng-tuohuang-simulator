using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>真实付款/放置流程的私有协议测试，不计正常菜单、页面或旧构建互读。</summary>
    public sealed class PlacementObservationV2Tests : FacilityLayerTestFixture
    {
        private sealed class Waiting
        {
            internal GameState State;
            internal string BuildId, ItemId, DefinitionId;
            internal int BeforeGold, PaidGold;
            internal HostSessionArchiveDto StartArchive;
        }
        private Waiting Paid(bool singleSlot = false)
        {
            var state = State(response: true, copies: BuildFacilityService.CityBoardSlotCount + 2);
            state.LaunchPlayerOrder = state.Players.Select(p => p.PlayerId).ToList();
            OwnInfluence(state);
            string definition = Definition(state, d => !d.Unique && !d.HasEntryEffect && d.GoldVoucherCost > 0);
            if (singleSlot)
                for (int slot = 0; slot < BuildFacilityService.CityBoardSlotCount - 1; slot++) Put(state, Card(state, definition).ItemId, slot);
            string item = Card(state, definition).ItemId;
            int before = state.FindPlayer(1).Resources.GoldVoucher;
            var archive = HostRecoveryService.CreateArchive(state);
            var build = Run(state, Build(state, item, -1, BuildFacilityService.PaymentModeGold));
            Assert.That(build.FlowStage, Is.EqualTo("awaiting_placement"));
            Assert.That(Flag(build.ContinuationData, "paid"), Is.True);
            Assert.That(Open(state).InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before - FacilityCardDatabase.Get(state, definition).GoldVoucherCost));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ParentEffectId == build.EffectId && n.EffectTypeId == FacilityBuildEffectExecutor.PaymentTypeId), Is.EqualTo(1));
            return new Waiting { State = state, BuildId = build.EffectId, ItemId = item, DefinitionId = definition, BeforeGold = before,
                PaidGold = state.FindPlayer(1).Resources.GoldVoucher, StartArchive = archive };
        }
        private static JsonSerializer HostSerializer() => JsonSerializer.Create((JsonSerializerSettings)typeof(HostRecoveryService)
            .GetField("ArchiveSettings", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
        private static JObject RawValue(NormalizedValue value) => JObject.FromObject(value, HostSerializer());
        private static JObject Property(JToken value, string name) => (JObject)((JArray)value["Properties"]).Single(p => (string)p["Name"] == name);
        private static JObject Field(JToken value, string name) => (JObject)Property(value, name)["Value"];
        private static void Replace(JToken value, string name, NormalizedValue replacement) => Property(value, name)["Value"] = RawValue(replacement);
        private static JObject BuildNode(JObject state, string id) => (JObject)((JArray)state["EffectRuntime"]["EffectNodes"]).Single(n => (string)n["EffectId"] == id);
        private static JObject CandidateArray(JObject state, string id) => Field(BuildNode(state, id)["ContinuationData"], "placementCandidates");
        private static void MirrorRequest(JObject state, string id)
        {
            var request = ((JArray)state["EffectRuntime"]["InteractionRequests"]).Single(r => (string)r["OwnerEffectId"] == id && (string)r["Status"] == "open");
            Property(request["PromptParameters"], "candidateSlots")["Value"] = CandidateArray(state, id).DeepClone();
        }
        private JObject Archive(Waiting waiting, bool journal)
        {
            HostSessionArchiveDto archive;
            if (journal)
            {
                archive = waiting.StartArchive; HostRecoveryService.AppendJournal(archive, waiting.State, Pack.ContentHash);
                Assert.That(archive.Journal.Entries.Count(e => e.StateAfter != null), Is.EqualTo(1));
                Assert.That(archive.Snapshot.State.EffectRuntime.EffectNodes.Any(n => n.EffectId == waiting.BuildId), Is.False);
            }
            else archive = HostRecoveryService.CreateArchive(waiting.State);
            string raw = HostRecoveryService.SerializeArchive(archive);
            artifacts.WriteLabeledText("观察合法完整归档-" + (journal ? "StateAfter" : "Snapshot"), raw);
            var roundtrip = JObject.Parse(raw).ToObject<HostSessionArchiveDto>(HostSerializer());
            Assert.That(JToken.DeepEquals(JObject.Parse(raw), JObject.Parse(HostRecoveryService.SerializeArchive(roundtrip))), Is.True, "必须沿原Host合同完整保留字段图。");
            var control = HostRecoveryService.RecoverJson(raw, Registry);
            Assert.That(control.Succeeded, Is.True, control.Diagnostic);
            Assert.That(RuleStateDigestService.Compute(control.State), Is.EqualTo(RuleStateDigestService.Compute(waiting.State)));
            artifacts.WriteLabeledText("观察同通路合法控制", JsonConvert.SerializeObject(new { journal, control.Succeeded, control.Diagnostic,
                gameId = waiting.State.GameId, buildId = waiting.BuildId, waiting.PaidGold, digest = RuleStateDigestService.Compute(control.State) }));
            return JObject.Parse(raw);
        }
        private static JObject Checkpoint(JObject raw, bool journal) => journal
            ? (JObject)((JArray)raw["Journal"]["Entries"]).Last(e => e["StateAfter"] is JObject)["StateAfter"]
            : (JObject)raw["Snapshot"]["State"];
        private static string Digest(FacilityBuildSlotCandidate candidate, int revision) => (string)typeof(FacilityBuildEffectExecutor)
            .GetMethod("PlacementObservationDigest", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { candidate, revision });
        private void Complete(Waiting waiting, GameState state)
        {
            var engine = new EffectTreeExecutor(state, Registry);
            for (int i = 0; i < 20 && engine.GetNode(waiting.BuildId).Status != EffectNodeStatus.Completed; i++)
            {
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var request = state.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
                if (request == null) break;
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                    NormalizedValue.CreateString(request.CandidateIds[0]), out string reason), Is.True, reason);
            }
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(waiting.BuildId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(waiting.PaidGold));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.ParentEffectId == waiting.BuildId && n.EffectTypeId == FacilityBuildEffectExecutor.PaymentTypeId), Is.EqualTo(1));
            Assert.That(FacilityLayerQuery.IsDirectTop(state, waiting.ItemId, 1), Is.True);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.SourceEffectId == waiting.BuildId && e.EventType == FacilityBuildJournal.EventType), Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void DamagedObservationIsRejectedFromLegalPaidSnapshotAndActualStateAfter(bool journal)
        {
            var waiting = Paid(); var legal = Archive(waiting, journal);
            var bads = new List<(string Name, Action<JObject> Change)>();
            foreach (string key in new[] { "protocolVersion", "slotIndex", "stateRevision", "observationDigest" })
            {
                string field = key;
                bads.Add(("missing_" + field, array => Property(array["Items"][0], field).Remove()));
                bads.Add(("type_" + field, array => Replace(array["Items"][0], field,
                    field == "observationDigest" ? NormalizedValue.CreateInteger(1) : NormalizedValue.CreateString("2"))));
                bads.Add(("raw_type_" + field, array => {
                    var value = Field(array["Items"][0], field);
                    if (field == "observationDigest") value["StringValue"] = 1;
                    else value["IntegerValue"] = "2";
                }));
            }
            bads.Add(("unknown_field", array => { var p = (JObject)Property(array["Items"][0], "slotIndex").DeepClone(); p["Name"] = "unknown"; ((JArray)array["Items"][0]["Properties"]).Add(p); }));
            bads.Add(("duplicate_slot", array => array["Items"][1] = array["Items"][0].DeepClone()));
            bads.Add(("protocol_zero", array => Replace(array["Items"][0], "protocolVersion", NormalizedValue.CreateInteger(0))));
            bads.Add(("protocol_unknown", array => Replace(array["Items"][0], "protocolVersion", NormalizedValue.CreateInteger(3))));
            bads.Add(("negative_revision", array => Replace(array["Items"][0], "stateRevision", NormalizedValue.CreateInteger(-1))));
            // 非法值不能经工厂创建；只变异已经合法封存的原始字段，实际进入恢复raw门禁。
            bads.Add(("overflow_revision", array => Field(array["Items"][0], "stateRevision")["IntegerValue"] = (long)int.MaxValue + 1));
            bads.Add(("future_revision_valid_digest", array => {
                var candidate = Registry.FacilityBuildQueries.ResolveSlots(waiting.State, 1, waiting.ItemId, waiting.BuildId).First();
                int future = waiting.State.EffectRuntime.StateRevision + 100;
                Replace(array["Items"][0], "stateRevision", NormalizedValue.CreateInteger(future));
                Replace(array["Items"][0], "observationDigest", NormalizedValue.CreateString(Digest(candidate, future)));
            }));
            bads.Add(("slot_out_of_bounds", array => Replace(array["Items"][0], "slotIndex", NormalizedValue.CreateInteger(BuildFacilityService.CityBoardSlotCount))));
            bads.Add(("empty_digest", array => Replace(array["Items"][0], "observationDigest", NormalizedValue.CreateString(""))));
            var results = new List<(string Name, HostRecoveryResult Result)>();
            string original = RuleStateDigestService.Compute(waiting.State);
            foreach (var bad in bads)
            {
                var raw = (JObject)legal.DeepClone(); var state = Checkpoint(raw, journal);
                bad.Change(CandidateArray(state, waiting.BuildId)); MirrorRequest(state, waiting.BuildId);
                string label = (journal ? "StateAfter-" : "Snapshot-") + bad.Name;
                string path = artifacts.WriteLabeledText("观察损坏原串-" + label, raw.ToString(Formatting.None));
                var result = HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), Registry);
                results.Add((bad.Name, result));
                artifacts.WriteLabeledText("观察损坏结果-" + label, JsonConvert.SerializeObject(new { input = path, bad.Name,
                    result.Succeeded, result.FaultCode, result.Diagnostic, originalStateUnchanged = original == RuleStateDigestService.Compute(waiting.State) }));
            }
            Assert.That(RuleStateDigestService.Compute(waiting.State), Is.EqualTo(original));
            Assert.That(results.Where(bad => bad.Result.Succeeded || string.IsNullOrEmpty(bad.Result.Diagnostic) ||
                bad.Result.Diagnostic.Contains("克隆失败")).Select(bad => bad.Name + ":" + bad.Result.Succeeded + ":" + bad.Result.Diagnostic),
                Is.Empty, "所有变体已先保留原串及实际结果；不能靠任意夹具克隆崩溃通过。");
        }

        [TestCase(false)] [TestCase(true)]
        public void CurrentAndLegacyOneObservationRecoverAndCompleteWithoutRepayment(bool legacy)
        {
            var waiting = Paid(singleSlot: legacy);
            if (legacy)
            {
                var build = waiting.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == waiting.BuildId);
                var current = Registry.FacilityBuildQueries.ResolveSlots(waiting.State, 1, waiting.ItemId, waiting.BuildId).Single();
                current.StateRevision = Number(Value(build.ContinuationData, "placementCandidates").Items.Single(), "stateRevision");
                var observations = NormalizedValue.CreateArray(new[] { FacilityBuildJournal.EncodeSlot(current) });
                Set(build, "placementCandidates", observations);
                Open(waiting.State).PromptParameters.Properties.Single(p => p.Name == "candidateSlots").Value = observations.Clone();
            }
            var raw = Archive(waiting, false); var result = HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), Registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); Complete(waiting, result.State);
            var restored = Recover(result.State, legacy ? "旧观察1合同完成后" : "观察2完成后");
            Assert.That(restored.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(waiting.PaidGold));
            artifacts.WriteLabeledText("观察合同继续", JsonConvert.SerializeObject(new { legacy, paid = waiting.BeforeGold - waiting.PaidGold,
                result.Succeeded, completed = true, noRepayment = true, scope = "当前构建旧观察1合同，不是旧构建互读" }));
        }

        [Test] public void LegitimateSlotFactsChangeReissuesActualCandidatesWithoutRepayment()
        {
            var waiting = Paid(); var original = Open(waiting.State);
            Assert.That(original.CandidateIds, Does.Contain("0"));
            Put(waiting.State, Card(waiting.State, waiting.DefinitionId).ItemId, 0);
            Assert.That(Registry.FacilityBuildQueries.ResolveSlots(waiting.State, 1, waiting.ItemId, waiting.BuildId).Any(c => c.SlotIndex == 0), Is.False);
            // 正式推进至静止点后，宿主应重发；不改request.StateRevision，不强填答案绕过陈旧保护。
            var state = waiting.State; var engine = new EffectTreeExecutor(state, Registry);
            var fresh = Open(state);
            Assert.That(fresh.GetStableInteractionId(), Is.Not.EqualTo(original.GetStableInteractionId()));
            Assert.That(fresh.CandidateIds, Is.EquivalentTo(Registry.FacilityBuildQueries.ResolveSlots(state, 1, waiting.ItemId, waiting.BuildId).Select(c => c.SlotIndex.ToString(CultureInfo.InvariantCulture))));
            Assert.That(fresh.CandidateIds, Does.Not.Contain("0"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(waiting.PaidGold));
            Assert.That(FacilityLayerQuery.IsDirectTop(state, waiting.ItemId, 1), Is.False);
            string beforeOldAnswer = RuleStateDigestService.Compute(state);
            Assert.That(engine.TrySubmitInteraction(original.GetStableInteractionId(), 1, original.StateRevision,
                NormalizedValue.CreateString("0"), out string staleReason), Is.False);
            Assert.That(staleReason, Is.Not.Empty);
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(beforeOldAnswer));
            artifacts.WriteLabeledText("合法候选变化重发", JsonConvert.SerializeObject(new { oldRequest = original.GetStableInteractionId(), freshRequest = fresh.GetStableInteractionId(), fresh.CandidateIds, noRepayment = true, noAutoSelection = true, oldAnswerRejected = true, staleReason, stateUnchanged = true }));
            var raw = Archive(waiting, false); var recovered = HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); Complete(waiting, recovered.State);
        }

        [Test] public void ForgedDigestRefreshesInsteadOfPlacingOrRepaying()
        {
            var waiting = Paid(); var raw = Archive(waiting, false); var checkpoint = Checkpoint(raw, false);
            Replace(CandidateArray(checkpoint, waiting.BuildId)["Items"][0], "observationDigest",
                NormalizedValue.CreateString(StableIdFactory.Create("facility_slot_observation.v2", "forged")));
            MirrorRequest(checkpoint, waiting.BuildId);
            artifacts.WriteLabeledText("伪摘要完整原串", raw.ToString(Formatting.None));
            var recovered = HostRecoveryService.RecoverJson(raw.ToString(Formatting.None), Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var state = recovered.State; var request = Open(state); var engine = new EffectTreeExecutor(state, Registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(request.CandidateIds[0]), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(Open(state).GetStableInteractionId(), Is.Not.EqualTo(request.GetStableInteractionId()));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(waiting.PaidGold));
            Assert.That(FacilityLayerQuery.IsDirectTop(state, waiting.ItemId, 1), Is.False);
            artifacts.WriteLabeledText("伪摘要重发边界", JsonConvert.SerializeObject(new { recovered.Succeeded, refreshed = true, noRepayment = true, noPlacement = true }));
            Complete(waiting, state);
        }

        [Test] public void ActiveIdentityAndG0AreObservedWithoutGrantingEntrepreneurReadiness()
        {
            var configuration = new GameRuleConfiguration { PlayerCount = 2 };
            var active = ActiveContentCatalogBuilder.Build(Pack, configuration);
            artifacts.WriteLabeledText("放置机制实际活动身份", JsonConvert.SerializeObject(new { Pack.ContentHash, active.ContentPackHash,
                FacilityBuildEffectExecutor.PlacementObservationProtocolVersion, required = RuleModeReadiness.EntrepreneurRequirements,
                missing = RuleModeReadiness.Missing(RuleModeReadiness.EntrepreneurRequirements) }));
            Assert.That(RuleModeReadiness.Missing(RuleModeReadiness.EntrepreneurRequirements), Is.Not.Empty);
            var entrepreneur = configuration.Clone(); entrepreneur.RuleModeId = "entrepreneur"; entrepreneur.EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" };
            Assert.That(Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(Pack, entrepreneur)).Message, Does.StartWith("rule_mode_not_ready"));
        }
    }
}
