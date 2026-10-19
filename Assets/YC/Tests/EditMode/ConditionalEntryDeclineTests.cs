using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 正式534存档与原535～539命令的隔离定位；不计正常可见程序验收。
    public sealed class ConditionalEntryDeclineTests
    {
        private GameSession session;
        private EffectRegistry registry;
        private GameCommand decline;
        private const string EvidenceRoot = "Logs/NetworkUiFixes/20261019/建设付款回归-123500";

        private void RestoreOriginal()
        {
            var envelope = JObject.Parse(File.ReadAllText(Path.Combine(EvidenceRoot, "结束时正式存档只读备份/auto-2.json")));
            var save = JsonConvert.DeserializeObject<MatchSaveData>((string)envelope["Payload"]);
            var bootstrap = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var restored = bootstrap.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, save });
            session = (GameSession)restored.GetType().GetField("Session").GetValue(restored);
            registry = (EffectRegistry)restored.GetType().GetField("EffectRegistry").GetValue(restored);
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(5411));
            var receipts = File.ReadLines(Path.Combine(EvidenceRoot, "四端正常联机/client2/四端全局应用回执.jsonl"))
                .Select(JObject.Parse).Select(x => x["Applied"]).Where(x => (long)x["GlobalCommandSequence"] >= 535 && (long)x["GlobalCommandSequence"] <= 539)
                .OrderBy(x => (long)x["GlobalCommandSequence"]).ToArray();
            Assert.That(receipts.Select(x => (long)x["GlobalCommandSequence"]), Is.EqualTo(new long[] {535,536,537,538,539}));
            foreach (var receipt in receipts.Take(4)) {
                var result = session.Submit(receipt["Command"].ToObject<GameCommandDto>().ToCommand());
                TestContext.WriteLine("ENTRY_PREFIX " + JsonConvert.SerializeObject(new {G=(long)receipt["GlobalCommandSequence"],result.Succeeded,result.Validation,Revision=session.State.EffectRuntime.StateRevision}));
                Assert.That(result.Succeeded, Is.True, result.LogMessage);
            }
            decline = receipts.Last()["Command"].ToObject<GameCommandDto>().ToCommand();
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(5456));
        }

        private static object Chain(GameState state, string owner)
        {
            var nodes = state.EffectRuntime.EffectNodes;
            var ids = new HashSet<string>();
            for (var n = nodes.Single(x => x.EffectId == owner); n != null; n = nodes.FirstOrDefault(x => x.EffectId == n.ParentEffectId)) ids.Add(n.EffectId);
            return nodes.Where(n => ids.Contains(n.EffectId) || ids.Contains(n.ParentEffectId)).Select(n => new {
                n.EffectId,n.ParentEffectId,n.EffectTypeId,n.Status,n.PendingOutcome,n.FlowStage,n.FailureReason,n.NormalizedResult,n.NormalizedArguments,n.ChildEffectIds,n.ContinuationContext });
        }

        [Test]
        public void OriginalEntryConditionDeclineKeepsBuiltFacilityAndClosesHealthyParentChain()
        {
            RestoreOriginal(); var state = session.State;
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.GetStableInteractionId() == decline.Parameters["interactionId"]);
            var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            Assert.That(owner.Payment.SourceKind, Is.EqualTo("condition")); Assert.That(owner.Payment.PaymentCost.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            string placed = JsonConvert.SerializeObject(state.Map.Facilities); int score = state.FindPlayer(1).Score;
            int actionBudget = state.FindPlayer(1).RemainingMainActionsThisTurn;
            var work = GameStateCloneService.DeepClone(state);
            OperationWindowService.Synchronize(work);
            Assert.That(OperationWindowService.TryResolveCommandContext(work, decline, out var context, out var validation), Is.True, validation.Reason);
            work.EffectRuntime.CurrentOperationContext = context;
            var result = new AnswerInteractionCommandHandler(registry, new RoundExecutionService(registry)).Handle(work, decline);
            TestContext.WriteLine("ENTRY_539 " + JsonConvert.SerializeObject(new {result.Succeeded,result.Validation,work.EffectRuntime.LastFaultCode,work.EffectRuntime.LastFaultMessage,Chain=Chain(work,request.OwnerEffectId)}));
            Assert.That(result.Succeeded, Is.True, work.EffectRuntime.LastFaultMessage);
            Assert.That(work.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active));
            Assert.That(JsonConvert.SerializeObject(work.Map.Facilities), Is.EqualTo(placed)); Assert.That(work.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(work.FindPlayer(1).Resources.GoldVoucher, Is.Zero); Assert.That(work.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(actionBudget));
            var build = work.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build);
            Assert.That(build.Status, Is.EqualTo(EffectNodeStatus.Completed));
            var condition = work.EffectRuntime.EffectNodes.Single(n => n.EffectId == owner.ParentEffectId);
            Assert.That(condition.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Text(condition.NormalizedResult,"outcome"), Is.EqualTo("condition_not_met"));
            Assert.That(condition.ChildEffectIds.Count, Is.EqualTo(1), "放弃不能执行右式影响力替换。");
            var applied = session.Submit(decline); Assert.That(applied.Succeeded, Is.True, applied.LogMessage);
            Assert.That(JsonConvert.SerializeObject(session.State.Map.Facilities), Is.EqualTo(placed));
            Assert.That(session.State.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(session.State.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(session.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==build.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            var follow = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 };
            var next = session.Submit(follow); Assert.That(next.Succeeded, Is.True, next.LogMessage);
        }

        [TestCase(false)] [TestCase(true)]
        public void LocalRuleConditionDeclineAndPaymentKeepSynchronousSequentialSemantics(bool execute)
        {
            RestoreOriginal(); session.State.RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential;
            if (execute) session.State.FindPlayer(1).Resources.GoldVoucher = 4;
            int gold = session.State.FindPlayer(1).Resources.GoldVoucher;
            var result = session.Submit(execute ? new GameCommand { Kind=decline.Kind, PlayerId=decline.PlayerId,
                OptionIds=new List<string>{"continue"}, Parameters=new Dictionary<string,string>(decline.Parameters) } : decline);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(session.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(execute ? gold-4 : gold));
            Assert.That(session.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Sequential));
            if (execute) Assert.That(session.State.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ContentPrimitiveEffectExecutor.SelectInfluence), Is.True);
        }

        [Test]
        public void TechnicalConditionFailureAndRealEntryFailureAreNotLegalDeclines()
        {
            RestoreOriginal();
            registry.Register("entry.test.failure", c => EffectStepResult.Failed("real_entry_failure"));
            var condition = EffectSpec.Condition(new[]{EffectSpec.Create("entry.test.failure",playerId:1)},new[]{EffectSpec.Create("entry.test.failure",playerId:1)});condition.PlayerId=1;
            var isolated = new GameState { GameId="entry.technical", Players=new List<PlayerState>{new PlayerState{PlayerId=1}} };
            var technicalRegistry=new EffectRegistry(); technicalRegistry.Register("entry.test.failure", c=>EffectStepResult.Failed("real_entry_failure"));
            var engine = new EffectTreeExecutor(isolated,technicalRegistry); string id=engine.CreateRoot(condition);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(engine.GetNode(id).ChildEffectIds.Count, Is.EqualTo(1));
            Assert.That(Text(engine.GetNode(id).NormalizedResult,"outcome"), Is.EqualTo("condition_not_met"));
            // 真正设施子树失败仍应报错；不能把任意 Failed当合法条件放弃。
            RestoreOriginal(); var request=session.State.EffectRuntime.InteractionRequests.Single(q=>q.GetStableInteractionId()==decline.Parameters["interactionId"]);
            var payment=session.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==request.OwnerEffectId);
            registry.Register(new EffectRegistration("entry.test.fault", c=>EffectStepResult.Failed("real_entry_failure")));
            var parent=session.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==payment.ParentEffectId);
            parent.EffectTypeId="entry.test.fault";
            var failed=session.Submit(decline);
            Assert.That(failed.Succeeded, Is.False);
            Assert.That(failed.Validation.Reason, Does.Contain("entity_build_child_failed"));
            TestContext.WriteLine("ENTRY_REAL_FAILURE "+JsonConvert.SerializeObject(new {failed.Succeeded,failed.Validation}));
        }
    }
}
