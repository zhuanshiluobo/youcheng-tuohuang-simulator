using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class QY018SchedulingTransactionTests
    {
        private static GameState NewState(string id) => new GameState
        {
            GameId = id, MapId = "起点",
            Players = new List<PlayerState> { new PlayerState { PlayerId = 1 } }
        };

        private static EffectRegistry Registry(List<GameState> escaped = null)
        {
            var registry = new EffectRegistry();
            registry.Register("test.parent", context =>
            {
                escaped?.Add(context.State);
                context.State.MapId = "已提交";
                return EffectStepResult.Completed().AddChild(EffectSpec.Create("test.leaf"));
            });
            registry.Register("test.leaf", context =>
            {
                escaped?.Add(context.State);
                context.State.MapId += "子步骤";
                return EffectStepResult.Completed();
            });
            return registry;
        }

        [Test]
        public void RunMatchesIndividualAdvanceAndPreservesHeldReferences()
        {
            var state = NewState("scheduling.equivalent");
            var expected = GameStateCloneService.DeepClone(state);
            var escaped = new List<GameState>();
            var observed = new List<GameState>();
            var executor = new EffectTreeExecutor(state, Registry(escaped), null,
                (work, node) => observed.Add(work));
            var control = new EffectTreeExecutor(expected, Registry());
            string id = executor.CreateRoot(EffectSpec.Create("test.parent"));
            control.CreateRoot(EffectSpec.Create("test.parent"));
            var heldNodes = state.EffectRuntime.EffectNodes;
            var heldRoot = executor.GetNode(id);
            var heldPlayer = state.Players[0];
            var report = executor.RunUntilQuiescent();
            int steps = 0;
            while (control.Advance()) Assert.That(++steps, Is.LessThan(100));
            Assert.That(report.Faulted, Is.False);
            Assert.That(GameStateCloneService.AreEquivalent(state, expected), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes, Is.SameAs(heldNodes));
            Assert.That(executor.GetNode(id), Is.SameAs(heldRoot));
            Assert.That(state.Players[0], Is.SameAs(heldPlayer));
            Assert.That(escaped.Count, Is.EqualTo(2));
            Assert.That(escaped[0], Is.Not.SameAs(escaped[1]));
            Assert.That(escaped[0], Is.Not.SameAs(state));
            Assert.That(escaped[0].MapId, Is.EqualTo("已提交"));
            Assert.That(state.MapId, Is.EqualTo("已提交子步骤"));
            string previous = RuleStateDigestService.Compute(escaped[0]);
            Assert.That(observed, Is.Not.Empty);
            string previousObserved = RuleStateDigestService.Compute(observed[0]);
            executor.CreateRoot(EffectSpec.Create("test.leaf"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(RuleStateDigestService.Compute(escaped[0]), Is.EqualTo(previous));
            Assert.That(RuleStateDigestService.Compute(observed[0]), Is.EqualTo(previousObserved));
        }

        [Test]
        public void LaterExecutorExceptionKeepsEarlierCommit()
        {
            CheckLaterFailure(true);
        }

        [Test]
        public void LaterValidationRejectionKeepsEarlierCommit()
        {
            CheckLaterFailure(false);
        }

        private static void CheckLaterFailure(bool throws)
        {
            var state = NewState("scheduling.failure." + throws);
            var registry = Registry();
            GameState failedWork = null;
            registry.Register("test.bad", context =>
            {
                failedWork = context.State;
                context.State.MapId = "不应发布";
                if (throws) throw new InvalidOperationException("隔离执行器异常");
                context.State.EffectRuntime.Journal[0].CommitSequence = -1;
                return EffectStepResult.Completed();
            });
            var executor = new EffectTreeExecutor(state, registry);
            string committed = executor.CreateRoot(EffectSpec.Create("test.leaf"));
            string failed = executor.CreateRoot(EffectSpec.Create("test.bad"));
            var heldRoot = executor.GetNode(committed);
            var heldJournal = state.EffectRuntime.Journal[0];
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.True);
            Assert.That(state.MapId, Is.EqualTo("起点子步骤"));
            Assert.That(executor.GetNode(committed), Is.SameAs(heldRoot));
            Assert.That(heldRoot.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(executor.GetNode(failed).Status, Is.EqualTo(EffectNodeStatus.Faulted));
            Assert.That(state.EffectRuntime.Journal[0], Is.SameAs(heldJournal));
            Assert.That(heldJournal.CommitSequence, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.Journal.Any(e => e.Detail == "effect.running:" + failed), Is.True);
            Assert.That(failedWork, Is.Not.SameAs(state));
            Assert.That(failedWork.MapId, Is.EqualTo("不应发布"));
        }

        [Test]
        public void NestedAndThreadRunsKeepIndependentOwners()
        {
            var state = NewState("scheduling.outer");
            var registry = Registry();
            GameState nested = null, threaded = null;
            Exception threadError = null;
            registry.Register("test.nested", context =>
            {
                nested = NewState("scheduling.nested");
                var inner = new EffectTreeExecutor(nested, Registry());
                inner.CreateRoot(EffectSpec.Create("test.parent"));
                Assert.That(inner.RunUntilQuiescent().Faulted, Is.False);
                var thread = new Thread(() =>
                {
                    try
                    {
                        threaded = NewState("scheduling.thread");
                        var other = new EffectTreeExecutor(threaded, Registry());
                        other.CreateRoot(EffectSpec.Create("test.parent"));
                        Assert.That(other.RunUntilQuiescent().Faulted, Is.False);
                    }
                    catch (Exception error) { threadError = error; }
                });
                thread.Start();
                Assert.That(thread.Join(10000), Is.True);
                if (threadError != null) throw threadError;
                context.State.MapId = "外层已提交";
                return EffectStepResult.Completed().AddChild(EffectSpec.Create("test.leaf"));
            });
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(EffectSpec.Create("test.nested"));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.MapId, Is.EqualTo("外层已提交子步骤"));
            Assert.That(nested.MapId, Is.EqualTo("已提交子步骤"));
            Assert.That(threaded.MapId, Is.EqualTo("已提交子步骤"));
            Assert.That(nested.EffectRuntime, Is.Not.SameAs(threaded.EffectRuntime));
        }

        [Test]
        public void WorkspaceResetsAliasesAndDropsGraphOnDispose()
        {
            Type type = typeof(EffectTreeExecutor).GetNestedType("SchedulingWork", BindingFlags.NonPublic);
            Assert.That(type, Is.Not.Null);
            var lease = (IDisposable)Activator.CreateInstance(type, true);
            var prepare = type.GetMethod("Prepare");
            var field = type.GetField("work", BindingFlags.Instance | BindingFlags.NonPublic);
            var source = NewState("scheduling.alias");
            var shared = NormalizedValue.CreateString("原值");
            source.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "a", NormalizedArguments = shared });
            source.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "b", NormalizedArguments = shared });
            try
            {
                var first = (GameState)prepare.Invoke(lease, new object[] { source });
                shared.StringValue = "新值";
                source.ContentPackHash = "新字段值";
                var second = (GameState)prepare.Invoke(lease, new object[] { source });
                Assert.That(second, Is.SameAs(first));
                Assert.That(GameStateCloneService.AreEquivalent(source, second), Is.True);
                Assert.That(second.EffectRuntime.EffectNodes[0].NormalizedArguments,
                    Is.Not.SameAs(second.EffectRuntime.EffectNodes[1].NormalizedArguments));
                Assert.That(second.EffectRuntime.EffectNodes[0].NormalizedArguments, Is.Not.SameAs(shared));
                shared.Items.Add(shared);
                Assert.Throws<TargetInvocationException>(() => prepare.Invoke(lease, new object[] { source }));
                shared.Items.Clear();
                var reset = (GameState)prepare.Invoke(lease, new object[] { source });
                Assert.That(GameStateCloneService.AreEquivalent(source, reset), Is.True);
            }
            finally { lease.Dispose(); }
            Assert.That(field.GetValue(lease), Is.Null);
        }
    }
}
