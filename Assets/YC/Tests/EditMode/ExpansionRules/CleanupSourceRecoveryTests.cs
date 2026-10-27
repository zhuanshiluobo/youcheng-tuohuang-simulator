using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CleanupSourceRecoveryTests
    {
        private IsolatedTestArtifacts artifacts;
        private CleanupSourceTestFixture f;
        [SetUp] public void Begin() { f = null; artifacts = IsolatedTestArtifacts.BeginCase(); }
        [TearDown] public void Finish() { f?.Snapshot("008恢复最终状态"); artifacts.Finish(); }
        [TestCase("accepted")]
        [TestCase("paid_choice")]
        [TestCase("returned")]
        [TestCase("closed")]
        public void FullHostRecoveryKeepsTheOriginalInvocationAndPayment(string point)
        {
            f = new CleanupSourceTestFixture(artifacts); var request = f.Options.Single().CleanupRequest;
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "recover", request, out _, out string id, out string reason), Is.True, reason);
            if (point != "accepted")
            {
                f.Drain(); f.Answer("continue"); Assert.That(f.Stored, Is.EqualTo(3));
                if (point != "paid_choice") f.Answer("resource");
                if (point == "closed") Assert.That(f.Engine.TryEndSettlementAction(1, f.Window.Context.Id, "", "", f.State.EffectRuntime.StateRevision, "close", out reason), Is.True, reason);
            }
            f.Restore("008断点-" + point);
            Assert.That(f.Source.Invocations.Single().InvocationId, Is.EqualTo(id));
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "recover", request, out bool replay, out var old, out reason), Is.True, reason);
            Assert.That(replay, Is.True); Assert.That(old, Is.EqualTo(id));
            if (point == "accepted") { f.Drain(); f.Answer("continue"); }
            if (point == "accepted" || point == "paid_choice") f.Answer("resource");
            Assert.That(f.Stored, Is.EqualTo(3)); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(1)); Assert.That(f.Window.IsClosed, Is.EqualTo(point == "closed"));
            f.Restore("008续行后-" + point);
        }

        [TestCase("ProtocolVersion")]
        [TestCase("RegistrationReceiptId")]
        [TestCase("RequestGeneration")]
        public void MissingRawSourceFieldsCannotBecomeValidDefaults(string field)
        {
            f = new CleanupSourceTestFixture(artifacts);
            string json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State));
            var archive = JObject.Parse(json); int changed = 0;
            foreach (var source in archive.Descendants().OfType<JProperty>().Where(p => p.Name == "CleanupSource" && p.Value is JObject).Select(p => (JObject)p.Value))
            { source.Remove(field); changed++; }
            Assert.That(changed, Is.GreaterThan(0)); artifacts.WriteLabeledText("008损坏原始字段-" + field, archive.ToString());
            var result = HostRecoveryService.RecoverJson(archive.ToString(), f.Registry);
            Assert.That(result.Succeeded, Is.False, "公共Host必须接入CleanupSourceRecovery.HasRawFields，不能靠缺省值恢复。");
        }

        [Test] public void RecoveryDuringSecondAcceptanceDoesNotReplayTheFirstReward()
        {
            f = new CleanupSourceTestFixture(artifacts); f.Start("first"); f.Answer("continue"); f.Answer("resource");
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "second", f.Options.Single().CleanupRequest, out _, out string second, out string reason), Is.True, reason);
            f.Restore("008下一次受理后"); f.Drain(); f.Answer("continue"); f.Answer("resource");
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(2)); Assert.That(f.Source.Invocations[1].InvocationId, Is.EqualTo(second));
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(2));
            f.Restore("008第二次完成回列表"); Assert.That(f.Stored, Is.Zero);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void OnlyTheRealRoundEndedResetsAfterEveryPlayerWindowAndRecovery(int players)
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 0, playerCount: players); f.State.MaxRounds = 1;
            for (int player = 1; player <= players; player++)
            {
                var window = f.State.EffectRuntime.EffectNodes.Single(n => n.SettlementWindow?.Context?.Id == f.State.EffectRuntime.ActiveSettlementWindowId).SettlementWindow;
                Assert.That(window.Context.PlayerId, Is.EqualTo(player));
                Assert.That(f.State.EffectRuntime.UsageTrackResets, Is.Empty);
                Assert.That(f.Engine.TryEndSettlementAction(player, window.Context.Id, "", "", f.State.EffectRuntime.StateRevision,
                    "close-player-" + player, out string reason), Is.True, reason);
                f.Drain();
                if (player < players)
                {
                    var advance = f.Rounds.Advance(f.State); Assert.That(advance.IsValid, Is.True, advance.Reason);
                    Assert.That(f.State.EffectRuntime.UsageTrackResets, Is.Empty);
                    f.Restore("008全体复位前-" + players + "人-已完成" + player);
                }
            }
            f.Restore("008全体关闭尚未RoundEnded");
            var final = f.Rounds.Advance(f.State); Assert.That(final.IsValid, Is.True, final.Reason);
            Assert.That(f.State.EffectRuntime.UsageTrackResets.Count, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.UsageTrackResets.Single().Phase, Is.EqualTo("completed"));
            f.Restore("008统一复位后");
            Assert.That(f.State.EffectRuntime.UsageTrackResets.Count, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.EffectNodes.Where(n => n.SettlementWindow != null).All(n => n.SettlementWindow.IsClosed), Is.True);
        }

        [Test] public void ARealFaultAfterPaymentKeepsTheSameInvocationOccupied()
        {
            const string body = @"return function(ctx) return {Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
leftEffects={tokenTransfer(ctx.payload.sourceItemId,3)},
rightEffects={tokenTransfer(ctx.payload.sourceItemId,1,3)}})} end";
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 4, bodyOverride: body); var request = f.Options.Single().CleanupRequest;
            f.Start("fault"); var confirm = f.Open;
            Assert.That(f.Engine.TrySubmitInteraction(confirm.GetStableInteractionId(), 1, confirm.StateRevision, NormalizedValue.CreateString("continue"), out _), Is.True);
            for (int i = 0; i < 80 && !f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId && Number(n.NormalizedArguments, "apiVersion") == 3 && n.Status == EffectNodeStatus.Completed); i++)
                Assert.That(f.Engine.Advance(), Is.True, f.Engine.LastDiagnostic);
            Assert.That(f.Stored, Is.EqualTo(1));
            f.State.ItemZones.Items.Single(i => i.ItemId == f.CardId).ZoneId = f.State.ItemZones.Zones.First(z => z.TemplateId == "core.zone.box_retired").ZoneId;
            Assert.That(f.Engine.RunUntilQuiescent().Faulted, Is.True); f.Snapshot("008已付右式故障首次现场");
            Assert.That(f.Source.ActiveInvocationId, Is.Not.Empty); Assert.That(f.Stored, Is.EqualTo(1));
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "replacement-is-forbidden", request, out _, out _, out _), Is.False);
            Assert.That(f.Source.Invocations.Count, Is.EqualTo(1));
            f.Restore("008已付右式故障恢复"); Assert.That(f.Source.ActiveInvocationId, Is.Not.Empty); Assert.That(f.Stored, Is.EqualTo(1));
        }

        [TestCase("store")]
        [TestCase("decline")]
        public void CompletionEventCreatesARealBypassAndWaitsWithoutFaultOrRepeatingPayment(string answer)
        {
            f = new CleanupSourceTestFixture(artifacts, initialMarkers: 3, storageResponse: true, concurrent: true);
            var map = StaticMapDefinitions.Resolve(f.State.MapId);
            string slot = InfluenceService.GetLocationSlotId(map.Locations.First(l => !l.IsRedZone && l.InfluenceSlotCount > 0).LocationId, 0);
            f.Engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, slot, itemTypeId: "core.player_marker")); f.Drain();
            string itemId = f.State.Map.Influences.Single(i => i.PlayerId == 1).InfluenceId;
            // 隔离Kernel合同探针：完成事件追加真实006 Remove；去向正文仍由私有Lua来源和真实claim执行。
            // 不是正式卡面实现、假响应或正常局覆盖。
            f.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("qy008.completion.probe", "EffectCompleted", "*", "qy008.completion.probe",
                context => new[] { InfluenceEffectSpecFactory.RemoveInfluence(1, itemId) }) { EffectTypeFilter = CleanupSourceInvocationExecutor.TypeId });
            f.Start("completion-bypass"); f.Answer("continue"); f.Answer("resource");
            var invocation = f.Source.Invocations.Single(); string targetId = invocation.TargetEffectId;
            var target = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == targetId);
            var response = f.State.EffectRuntime.DeferredResponses.Single();
            Assert.That(response.IsBypass, Is.True); Assert.That(response.RequiresProducerCompletion, Is.False);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == response.SourceOperationEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(target.Status, Is.EqualTo(EffectNodeStatus.Blocked)); Assert.That(target.PendingOutcome, Is.EqualTo(EffectPendingOutcome.Completed));
            Assert.That(target.CompletedEventId, Is.Not.Empty); string completion = target.CompletedEventId;
            Assert.That(f.State.EffectRuntime.Blockers.Any(b => b.OwnerEffectId == targetId && b.TargetEffectId == response.ResponseRootEffectId && !b.IsResolved), Is.True,
                "公共Kernel必须在EffectCompleted后重接008真实响应依赖；不能走终态钩子抛Fault。");
            Assert.That(invocation.Status, Is.EqualTo("executing")); Assert.That(f.Source.ActiveInvocationId, Is.EqualTo(invocation.InvocationId));
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(f.Engine.TryEndSettlementAction(1, f.Window.Context.Id, "", "", f.State.EffectRuntime.StateRevision, "completion-too-early", out _), Is.False);
            Assert.That(f.Engine.TryInvokeCleanupSource(1, "completion-bypass", invocation.RequestRef, out bool replay, out string original, out string reason), Is.True, reason);
            Assert.That(replay, Is.True); Assert.That(original, Is.EqualTo(invocation.InvocationId));
            // 独立无关节点仍能完成；008不得把FinalizeGame的全局候选未经归属过滤全挂上。
            string independent = f.Engine.CreateRoot(ResourceEffectSpecFactory.GainScore(2, 1)); f.Drain();
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == independent).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(CleanupSourceDependencies.Instance.GetPendingResponses(f.State, independent), Is.Empty);
            f.Restore("008完成事件已分发-真实旁路待答-" + answer);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == targetId).CompletedEventId, Is.EqualTo(completion));
            f.Answer(answer);
            Assert.That(f.Source.ActiveInvocationId, Is.Empty); Assert.That(f.Source.Invocations.Count, Is.EqualTo(1));
            Assert.That(f.Source.Invocations.Single().Outcome, Is.EqualTo("completed"));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventId == completion), Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.DispatchReceipts.Count(r => r.EventId == completion && r.SubscriptionId == "qy008.completion.probe"), Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.InfluenceDepartures.Count, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.DeferredResponses.Count, Is.EqualTo(1));
            Assert.That(f.Stored, Is.EqualTo(answer == "store" ? 1 : 0));
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(2).Score, Is.EqualTo(1));
            f.Restore("008完成事件响应结束回列表-" + answer);
        }
    }
}
