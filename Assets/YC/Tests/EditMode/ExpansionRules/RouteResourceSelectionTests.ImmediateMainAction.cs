using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Domain.Travel;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class RouteResourceSelectionTests
    {
        [Test]
        public void ImmediateExploreCancellationIsReportedWithoutClaimingAnExecutedAction()
        {
            Production(out var state, out var registry, out _);
            var engine = new EffectTreeExecutor(state, registry);
            string root = engine.CreateRoot(CompileLua("Effect.ExecuteMainAction({player=ctx.playerId,executionMode='immediate',allowedActionTypes={'explore'}})"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            Submit(state, registry, state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open"), new[] { "explore" });
            var request = Selection(state);
            Submit(state, registry, request, new[] { request.CandidateIds.Single(id => id.EndsWith(".cancel", System.StringComparison.Ordinal)) });
            var result = ExploreRoot(state, root);
            Assert.That(YC.Domain.Effects.ItemTransferEffectExecutor.Text(result.NormalizedResult, "outcome"), Is.EqualTo("cancelled"));
            Assert.That(YC.Domain.Effects.ItemTransferEffectExecutor.Value(result.NormalizedResult, "executed").BooleanValue, Is.False);
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            ExploreRecover(state, registry, "即时探索取消结果冷恢复");
        }
        [TestCase(false)][TestCase(true)]
        public void ImmediateExploreWaitsForActualResponsesAndKeepsFailureAcrossRecovery(bool fault)
        {
            Production(out var state, out var registry, out _);
            if (fault) registry.RegisterEventHandler(new EffectEventHandlerRegistration("api18.event_fault", EventCardEffectEventTypeIds.Revealed, "*", "api18.event_fault",
                c => { throw new KernelException("api18.event_fault", "API-18 隔离必要响应故障。"); }, role: EffectHandlerRole.Observer));
            string root = new EffectTreeExecutor(state, registry).CreateRoot(CompileLua("Effect.ExecuteMainAction({player=ctx.playerId,executionMode='immediate',allowedActionTypes={'explore'}})"));
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            var choice = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open"); Submit(state, registry, choice, new[] { "explore" });
            // 按刚回答的行动选择定位探索效果，不假定它直接挂在最外层效果下。
            var selection = state.EffectRuntime.EffectNodes.Single(n =>
                n.ParentEffectId == choice.OwnerEffectId && n.EffectTypeId == ExplorationEffectTypeIds.Explore);
            Assert.That(YC.Domain.Effects.ItemTransferEffectExecutor.Number(selection.NormalizedArguments, "apiVersion"), Is.EqualTo(3));
            Assert.That(YC.Domain.Effects.ItemTransferEffectExecutor.Value(selection.NormalizedArguments, "consumeMainAction").BooleanValue, Is.False);
            state = ExploreRecover(state, registry, "即时探索共用地图待答"); var q = Selection(state);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision,
                NormalizedValue.CreateArray(SelectionAnswer(q, "A-01", "A1", "A2").Select(NormalizedValue.CreateString).ToList()), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.EqualTo(fault), state.EffectRuntime.LastFaultMessage);
            Assert.That(ExploreRoot(state, root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(4));
            Assert.That(state.EffectRuntime.RuleEvents.Any(e => e.EventType == "EffectCompleted" && e.SourceEffectId == root), Is.False);
            state = ExploreRecover(state, registry, fault ? "即时探索故障现场" : "即时探索已托管待必要响应");
            if (fault)
            {
                int count = state.EffectRuntime.EffectNodes.Count;
                Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.True);
                Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(count));
                Assert.That(ExploreRoot(state, root).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            }
            else
            {
                FinishEvents(state, registry); Assert.That(ExploreRoot(state, root).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.Zero);
                Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "EffectCompleted" && e.SourceEffectId == root), Is.EqualTo(1));
                state = ExploreRecover(state, registry, "即时探索完成后不重放");
                Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Completed, Is.True);
                Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "EffectCompleted" && e.SourceEffectId == root), Is.EqualTo(1));
            }
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1)); Assert.That(state.FindPlayer(1).CompletedMainActionsThisTurn, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.RetiredExplorationSelectionTypeId), Is.False);
        }
    }
}
