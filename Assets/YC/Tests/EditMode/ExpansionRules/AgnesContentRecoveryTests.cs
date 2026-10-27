using System.Linq;
using System;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class AgnesContentRecoveryTests
    {
        [TestCase("quick", false)][TestCase("special", true)]
        public void ActualA08ObligationRestoresBeforeDueDuringRemovalAndAfterCompletion(string entry, bool exitSource)
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            int beforeGold = f.State.FindPlayer(1).Resources.GoldVoucher;
            string action = "entrepreneur.A08.tactic." + entry;
            f.Invoke(action);
            if (entry == "quick") { f.AssertQuickEntryPending(action); Assert.That(f.Stored, Is.EqualTo(2)); f.Answer("continue"); }
            f.ResolveMapRequests();
            Assert.That(f.Stored, Is.EqualTo(entry == "quick" ? 0 : 2));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(beforeGold + (entry == "special" ? 10 : 0)));
            var record = f.State.EffectRuntime.CleanupObligations.Single(); string id = record.ObligationId;
            Assert.That(record.OriginKind, Is.EqualTo("action")); Assert.That(record.OriginVersion.ExecutorVersion, Is.EqualTo(AbilityActionExecutor.Version));
            f.Restore("A08真实行动义务登记后");
            f.AssertNoTokenPaymentSelection();
            if (exitSource) { f.ExitSelectedSource("A08"); f.Restore("A08真实来源退出后保留冻结义务"); }
            f.Cleanup(); f.Restore("A08必做条目待选择"); f.StartCleanupObligation(); string request = f.Open.GetStableInteractionId();
            Assert.That(f.Open.AllowDecline, Is.False); f.Restore("A08真实必做移除待答");
            Assert.That(f.Open.GetStableInteractionId(), Is.EqualTo(request)); f.ResolveMapRequests();
            var completed = f.State.EffectRuntime.CleanupObligations.Single();
            Assert.That(completed.ObligationId, Is.EqualTo(id)); Assert.That(completed.Status, Is.EqualTo("completed"));
            Assert.That(f.OwnMapCount, Is.EqualTo(1)); f.Restore("A08完成义务收据后"); f.Pump(); Assert.That(f.OwnMapCount, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == CleanupObligation.InvokeType), Is.EqualTo(1));
        }
        [TestCase(0)][TestCase(1)][TestCase(4)]
        public void TwoRealA08BodyInvocationsKeepIndependentDutiesAndRequeryRemainingTargets(int remaining)
        {
            // 私有元数据只加两个无轨道调用入口；原文正文/生产Host/真实Action收据均未替换。
            // 不把本协议例解释为正式A08每回合可发动两次，正式共轨道另有拒绝正例。
            using var f = new AgnesContentTestFixture(multiObligationProbe: true); f.Formal(); f.Choose(1, "A08", "1");
            f.Invoke("qy013.obligation.first"); f.ResolveMapRequests(); f.Invoke("qy013.obligation.second"); f.ResolveMapRequests();
            var duties = f.State.EffectRuntime.CleanupObligations;
            Assert.That(duties.Count, Is.EqualTo(2)); Assert.That(duties.Select(r => r.OriginId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(duties.Select(r => r.ObligationId).Distinct().Count(), Is.EqualTo(2));
            f.LeaveOwnMapCount(remaining); f.Restore("两份真实A08正文调用义务及当前剩余目标");
            f.ExitSelectedSource("A08"); f.Restore("多义务真实来源退出后"); f.Cleanup();
            foreach (string obligation in duties.Select(r => r.ObligationId).Reverse().ToArray())
            {
                f.StartCleanupObligation(obligation); f.Restore("多义务选择及可能移除待答"); f.ResolveMapRequests();
            }
            Assert.That(f.State.EffectRuntime.CleanupObligations.All(r => r.Status == "completed"), Is.True);
            Assert.That(f.OwnMapCount, Is.EqualTo(Math.Max(0, remaining - 2)));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == CleanupObligation.InvokeType), Is.EqualTo(2));
            f.Restore("多义务全部完成后"); f.Pump(); Assert.That(f.OwnMapCount, Is.EqualTo(Math.Max(0, remaining - 2)));
        }
        [Test] public void A05NewA08DutyRestoresInsideSameCleanupBarrierAndNeverSchedulesAgainFromDue()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "1"); f.Choose(5, "A05");
            string target = f.Action("entrepreneur.A08.tactic.quick").OptionId;
            f.Cleanup(); f.StartCleanupOption(); f.Answer("continue"); f.Restore("A05付1至0战术选择待答");
            Assert.That(f.Stored, Is.Zero); f.Answer(target);
            // 只回答两次Place，新义务须先成为同窗独立条目，不能自动移除或挂成A05未完成子树。
            f.Answer(f.Open.CandidateIds.First()); f.Answer(f.Open.CandidateIds.First());
            f.Restore("A05同收尾新必做条目待选择"); f.StartCleanupObligation();
            var due = f.State.EffectRuntime.CleanupObligations.Single(); Assert.That(due.Status, Is.EqualTo("executing"));
            Assert.That(due.BodyFrozen, Is.True); string invoke = due.InvokeEffectId, request = f.Open.GetStableInteractionId();
            f.Restore("A05同收尾新增义务必做移除待答"); Assert.That(f.Open.GetStableInteractionId(), Is.EqualTo(request));
            f.ResolveMapRequests(); f.Restore("A05同收尾新义务完成"); f.Pump();
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().InvokeEffectId, Is.EqualTo(invoke));
            Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status, Is.EqualTo("completed"));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == CleanupObligation.ScheduleType), Is.EqualTo(1));
            Assert.That(f.Stored, Is.Zero); Assert.That(f.OwnMapCount, Is.EqualTo(1));
        }
        [Test] public void A05PaidDeclineRestoresWithoutRefundReopeningOrMarkingUnusedTactic()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "1"); f.Choose(5, "A05");
            f.Cleanup(); f.StartCleanupOption(); f.Answer("continue"); string request = f.Open.GetStableInteractionId();
            f.Restore("A05付后可放弃选择待答"); Assert.That(f.Stored, Is.Zero); Assert.That(f.Open.GetStableInteractionId(), Is.EqualTo(request)); f.AssertNoTokenPaymentSelection(); f.Decline();
            f.Restore("A05已付放弃once关闭后"); f.Pump();
            Assert.That(f.Stored, Is.Zero); Assert.That(f.State.EffectRuntime.CleanupObligations, Is.Empty);
            Assert.That(f.Registry.GetSettlementOptions(f.State, 1).Any(o => o.LocalId == "entrepreneur.A05.cleanup.cleanup_order"), Is.False);
            var track = f.State.EffectRuntime.UsageTrackBindings.Single(b => b.TrackId == "entrepreneur.A08.track" && b.PlayerId == 1);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == track.MarkerItemId).SlotId, Is.EqualTo("unused"));
        }
        [Test] public void FreeQuantityChoiceRestoresSameRequestAndDeliversOnce()
        {
            using var f = new AgnesContentTestFixture(); f.Host("return A.gainCommand(ctx,{},2)"); f.Start();
            string request = f.Open.GetStableInteractionId(); f.Restore("指令数量选择待答");
            Assert.That(f.Open.GetStableInteractionId(), Is.EqualTo(request)); f.Answer("1");
            Assert.That(f.Stored, Is.EqualTo(1)); f.Restore("指令选择交付完成"); f.Pump();
            Assert.That(f.Stored, Is.EqualTo(1)); Assert.That(f.State.EffectRuntime.InteractionRequests.Any(x => x.Status == "open"), Is.False);
        }
        [TestCase(1)] [TestCase(4)] [TestCase(29)]
        public void PaidConditionAndIndependentMovesRestoreWithoutChargingOrMovingTwice(int k)
        {
            using var f = new AgnesContentTestFixture(); f.Host("return A.purchaseCommand(ctx," + k + ")", supply: 29); f.Start();
            var request = f.Open;
            Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                NormalizedValue.CreateString("continue"), out string reason), Is.True, reason);
            for (int i = 0; i < 80 && !f.State.EffectRuntime.EffectNodes.Any(x => x.Payment?.IsCommitted == true); i++)
                Assert.That(f.Engine.Advance(), Is.True, f.Engine.LastDiagnostic);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100 - 2 * k));
            f.Restore("原资源付款已成后续独立移动未重复"); f.Pump();
            Assert.That(f.Stored, Is.EqualTo(k)); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100 - 2 * k));
            f.Restore("单枚移动全部结束"); f.Pump(); Assert.That(f.Stored, Is.EqualTo(k));
        }
        [Test] public void PaidChoiceCannotBeReusedAfterRestore()
        {
            using var f = new AgnesContentTestFixture(); f.Host("return A.purchaseCommand(ctx,1)"); f.Start();
            var old = f.Open; string id = old.GetStableInteractionId(); int player = old.AnsweringPlayerId, revision = old.StateRevision;
            f.Answer("continue"); f.Restore("指令付款收据完成");
            Assert.That(f.Engine.TrySubmitInteraction(id, player, revision, NormalizedValue.CreateString("continue"), out _), Is.False);
            Assert.That(f.Stored, Is.EqualTo(1)); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(98));
        }
    }
}
