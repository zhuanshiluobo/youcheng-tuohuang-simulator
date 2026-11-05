using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离验证私有工作图的引用/回滚合同；不计正常对局或界面P95。
    public sealed class ExecutionWorkRecyclingTests
    {
        private static GameState State()
        {
            var state = new GameState { GameId = "isolated.work.recycling" };
            state.Players.Add(new PlayerState { PlayerId = 1 }); return state;
        }
        private static void RegisterClosed(EffectRegistry registry, Func<EffectExecutionContext, EffectStepResult> handler)
        {
            typeof(EffectRegistry).GetMethod("RegisterWithoutStateRetention", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(registry, new object[] { UsageTracks.ResetTypeId, handler, EffectExecutorKind.Atomic, "v1" });
        }
        [TestCase(false)] [TestCase(true)]
        public void ClosedWorkReusesOnlyPublishedPrivateStateAndFailureStillRollsBack(bool fail)
        {
            var state = State(); var player = state.Players[0]; var registry = new EffectRegistry(); int calls = 0, recycled = 0;
            RegisterClosed(registry, context => {
                calls++;
                if (calls == 1) return EffectStepResult.Continue("second");
                context.State.Players[0].Score = fail ? 999 : 7;
                if (fail) throw new InvalidOperationException("隔离失败");
                return EffectStepResult.Completed();
            });
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create(UsageTracks.ResetTypeId, playerId: 1));
            using (GameStateCloneService.BeginTimingScope((phase, ticks) => { if (phase == "kernel.recycled_execution_work") recycled++; }))
            using (GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.EqualTo(fail));
            Assert.That(calls, Is.EqualTo(2)); Assert.That(recycled, Is.GreaterThan(0));
            Assert.That(state.Players[0], Is.SameAs(player)); Assert.That(player.Score, Is.EqualTo(fail ? 0 : 7));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void DefaultRetainingObserverAndDeclinedOrThrowingContractKeepOldGraphsDetached(int contract)
        {
            var state = State(); var registry = new EffectRegistry(); var captured = new List<GameState>(); int recycled = 0;
            RegisterClosed(registry, context => { context.State.Players[0].Score++; return EffectStepResult.Completed(); });
            var observer = new EffectEventHandlerRegistration("isolated.retaining", "EffectCompleted", "*", "isolated.retaining", context => {
                captured.Add(context.State); return Array.Empty<EffectSpec>(); });
            if (contract == 1) observer.WithNonRetainedStateContract(() => false);
            if (contract == 2) observer.WithNonRetainedStateContract(() => throw new InvalidOperationException("只关闭复用"));
            registry.RegisterEventHandler(observer);
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create(UsageTracks.ResetTypeId, playerId: 1));
            engine.CreateRoot(EffectSpec.Create(UsageTracks.ResetTypeId, playerId: 1));
            using (GameStateCloneService.BeginTimingScope((phase, ticks) => { if (phase == "kernel.recycled_execution_work") recycled++; }))
            using (GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(recycled, Is.Zero); Assert.That(captured.Count, Is.EqualTo(2));
            Assert.That(captured[0], Is.Not.SameAs(captured[1]).And.Not.SameAs(state));
            Assert.That(captured[0].Players[0].Score, Is.EqualTo(1)); Assert.That(captured[1].Players[0].Score, Is.EqualTo(2));
        }

        [Test]
        public void SourceAliasesRejectRecyclingAndNextStepSeesOriginalFullCloneSemantics()
        {
            var state = State(); state.Players.Add(new PlayerState { PlayerId = 2 });
            var registry = new EffectRegistry(); int calls = 0; bool secondAliased = true; var recycledAt = new List<int>();
            RegisterClosed(registry, context => {
                calls++;
                if (calls == 1)
                {
                    var shared = new ResourceSet { Originium = 5 };
                    context.State.Players[0].Resources = shared; context.State.Players[1].Resources = shared;
                    return EffectStepResult.Continue("second");
                }
                secondAliased = ReferenceEquals(context.State.Players[0].Resources, context.State.Players[1].Resources);
                context.State.Players[0].Resources.Originium++; return EffectStepResult.Completed();
            });
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create(UsageTracks.ResetTypeId, playerId: 1));
            using (GameStateCloneService.BeginTimingScope((phase, ticks) => { if (phase == "kernel.recycled_execution_work") recycledAt.Add(calls); }))
            using (GameStateCloneService.VerifySchedulingPublicationCopies())
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(recycledAt, Has.No.Member(1)); Assert.That(secondAliased, Is.False);
            Assert.That(state.Players[0].Resources.Originium, Is.EqualTo(6)); Assert.That(state.Players[1].Resources.Originium, Is.EqualTo(5));
        }
    }
}
