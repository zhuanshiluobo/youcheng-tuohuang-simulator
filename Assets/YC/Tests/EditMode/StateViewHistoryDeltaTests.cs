using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class StateViewHistoryDeltaTests
    {
        [Test]
        public void CreateDelta_FirstSnapshotIsFull_ThenOnlySendsNewHistory()
        {
            var sender = new StateViewHistoryTracker();
            var first = View(new[] { 1, 3 }, new[] { 1, 4 });
            var initial = sender.CreateDelta(first, false);
            Assert.IsTrue(initial.Reset);
            CollectionAssert.AreEqual(new[] { 1, 3 }, first.Events.ConvertAll(item => item.Sequence));

            var next = View(new[] { 1, 3, 7 }, new[] { 1, 4, 8 });
            var delta = sender.CreateDelta(next, false);
            Assert.IsFalse(delta.Reset);
            Assert.AreEqual(3, delta.BaseEventSequence);
            Assert.AreEqual(4, delta.BaseLogSequence);
            Assert.AreEqual(7, delta.EventSequence);
            Assert.AreEqual(8, delta.LogSequence);
            CollectionAssert.AreEqual(new[] { 7 }, next.Events.ConvertAll(item => item.Sequence));
            CollectionAssert.AreEqual(new[] { 8 }, next.Logs.ConvertAll(item => item.Sequence));
        }

        [Test]
        public void TryRestore_AppendsPrivateSequenceGaps_AndRestoresFullView()
        {
            var sender = new StateViewHistoryTracker();
            var receiver = new StateViewHistoryTracker();
            var first = View(new[] { 2, 5 }, new[] { 3 });
            Assert.IsTrue(receiver.TryRestore(first, sender.CreateDelta(first, false)));

            var next = View(new[] { 2, 5, 9 }, new[] { 3, 11 });
            var delta = sender.CreateDelta(next, false);
            Assert.IsTrue(receiver.TryRestore(next, delta));
            CollectionAssert.AreEqual(new[] { 2, 5, 9 }, next.Events.ConvertAll(item => item.Sequence));
            CollectionAssert.AreEqual(new[] { 3, 11 }, next.Logs.ConvertAll(item => item.Sequence));
            Assert.AreEqual("日志 11", next.Logs[1].Message);
            // 前一视图不会被后续增量改动。
            CollectionAssert.AreEqual(new[] { 2, 5 }, first.Events.ConvertAll(item => item.Sequence));

            var unchanged = View(new[] { 2, 5, 9 }, new[] { 3, 11 });
            var emptyDelta = sender.CreateDelta(unchanged, false);
            Assert.IsEmpty(unchanged.Events);
            Assert.IsEmpty(unchanged.Logs);
            Assert.IsTrue(receiver.TryRestore(unchanged, emptyDelta));
            Assert.AreEqual(3, unchanged.Events.Count);
            Assert.AreEqual(2, unchanged.Logs.Count);
        }

        [Test]
        public void TryRestore_RejectsMissingAndDuplicateDelta_ThenAcceptsFullResync()
        {
            var sender = new StateViewHistoryTracker();
            var receiver = new StateViewHistoryTracker();
            var first = View(new[] { 1 }, new[] { 1 });
            Assert.IsTrue(receiver.TryRestore(first, sender.CreateDelta(first, false)));

            var missed = View(new[] { 1, 2 }, new[] { 1, 2 });
            sender.CreateDelta(missed, false);
            var skipped = View(new[] { 1, 2, 3 }, new[] { 1, 2, 3 });
            Assert.IsFalse(receiver.TryRestore(skipped, sender.CreateDelta(skipped, false)));
            CollectionAssert.AreEqual(new[] { 3 }, skipped.Events.ConvertAll(item => item.Sequence));

            var repaired = View(new[] { 1, 2, 3 }, new[] { 1, 2, 3 });
            Assert.IsTrue(receiver.TryRestore(repaired, sender.CreateDelta(repaired, true)));
            var fourth = View(new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3, 4 });
            var delta = sender.CreateDelta(fourth, false);
            var duplicate = View(new[] { 4 }, new[] { 4 });
            Assert.IsTrue(receiver.TryRestore(fourth, delta));
            Assert.IsFalse(receiver.TryRestore(duplicate, delta));
            CollectionAssert.AreEqual(new[] { 4 }, duplicate.Events.ConvertAll(item => item.Sequence));

            var fifth = View(new[] { 1, 2, 3, 4, 5 }, new[] { 1, 2, 3, 4, 5 });
            Assert.IsTrue(receiver.TryRestore(fifth, sender.CreateDelta(fifth, false)));
            Assert.AreEqual(5, fifth.Events.Count);
            Assert.AreEqual(5, fifth.Logs.Count);
        }

        [Test]
        public void TryRestore_InvalidLogDoesNotPartiallyAppendEvents()
        {
            var sender = new StateViewHistoryTracker();
            var receiver = new StateViewHistoryTracker();
            var first = View(new[] { 1 }, new[] { 1 });
            Assert.IsTrue(receiver.TryRestore(first, sender.CreateDelta(first, false)));
            var next = View(new[] { 1, 2 }, new[] { 1, 2 });
            var delta = sender.CreateDelta(next, false);
            var bad = View(new[] { 2 }, new[] { 1 });
            Assert.IsFalse(receiver.TryRestore(bad, delta));
            Assert.IsTrue(receiver.TryRestore(next, delta));
            CollectionAssert.AreEqual(new[] { 1, 2 }, next.Events.ConvertAll(item => item.Sequence));
        }

        [Test]
        public void CreateDelta_RestartsForGameChangeAndSequenceRollback()
        {
            var sender = new StateViewHistoryTracker();
            var receiver = new StateViewHistoryTracker();
            var old = View(new[] { 4, 7 }, new[] { 4, 7 });
            Assert.IsTrue(receiver.TryRestore(old, sender.CreateDelta(old, false)));

            var rolledBack = View(new[] { 2 }, new[] { 2 });
            var rollback = sender.CreateDelta(rolledBack, false);
            Assert.IsTrue(rollback.Reset);
            Assert.AreEqual(0, rollback.BaseEventSequence);
            Assert.IsTrue(receiver.TryRestore(rolledBack, rollback));
            CollectionAssert.AreEqual(new[] { 2 }, rolledBack.Events.ConvertAll(item => item.Sequence));

            var newGame = View(new[] { 5 }, new[] { 5 });
            newGame.GameId = "另一局";
            var restart = sender.CreateDelta(newGame, false);
            Assert.IsTrue(restart.Reset);
            Assert.IsTrue(receiver.TryRestore(newGame, restart));
            CollectionAssert.AreEqual(new[] { 5 }, newGame.Events.ConvertAll(item => item.Sequence));
            CollectionAssert.AreEqual(new[] { 5 }, newGame.Logs.ConvertAll(item => item.Sequence));
        }

        [Test]
        public void CreateDelta_ObserverChangeResetsPreviouslyVisibleHistory()
        {
            var sender = new StateViewHistoryTracker();
            var receiver = new StateViewHistoryTracker();
            var old = View(new[] { 1, 3 }, new[] { 1, 3 });
            Assert.IsTrue(receiver.TryRestore(old, sender.CreateDelta(old, false)));
            var spectator = View(new[] { 3 }, new[] { 3 });
            spectator.ViewerRole = GameStateViewerRole.Spectator;
            spectator.ViewerPlayerId = -1;
            var reset = sender.CreateDelta(spectator, false);
            Assert.IsTrue(reset.Reset);
            Assert.IsTrue(receiver.TryRestore(spectator, reset));
            CollectionAssert.AreEqual(new[] { 3 }, spectator.Events.ConvertAll(item => item.Sequence));
        }

        [Test]
        public void Projector_SequencesOnlyVisibleEventsAfterPrivacyFiltering()
        {
            var state = new GameState();
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "公开 1", Visibility = "public" });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "他人私密", PlayerId = 2, Visibility = "owner" });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "公开 3", Visibility = "public" });
            state.Logs.Add(new GameLogEntry { Sequence = 1, Visibility = "public" });
            state.Logs.Add(new GameLogEntry { Sequence = 2, Visibility = "owner", PlayerId = 2 });
            state.Logs.Add(new GameLogEntry { Sequence = 3, Visibility = "public" });
            var projected = GameStateViewProjector.ProjectForPlayer(state, 1);
            CollectionAssert.AreEqual(new[] { 1, 2 }, projected.Events.ConvertAll(item => item.Sequence));
            CollectionAssert.AreEqual(new[] { 1, 3 }, projected.Logs.ConvertAll(item => item.Sequence));
        }

        private static GameStateView View(int[] eventSequences, int[] logSequences)
        {
            var view = new GameStateView
            {
                GameId = "测试局",
                ViewerRole = GameStateViewerRole.Player,
                ViewerPlayerId = 1
            };
            foreach (int sequence in eventSequences)
                view.Events.Add(new RuleEventView { Sequence = sequence, EventId = "事件 " + sequence });
            foreach (int sequence in logSequences)
                view.Logs.Add(new GameLogView { Sequence = sequence, Message = "日志 " + sequence });
            return view;
        }
    }
}
