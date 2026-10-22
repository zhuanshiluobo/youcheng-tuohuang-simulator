using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class PreparedEffectCommitTests
    {
        private static RuleCommit Prepared(GameState state, params RuleJournalEntry[] entries)
        {
            try
            {
                return (RuleCommit)typeof(RuleCommit).GetMethod("ApplyPreparedEffectCommit", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { state, "prepared.commit", entries });
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        public void PreparedCommitMatchesPublicCommitAndKeepsPayloadsIsolated(int schema)
        {
            var state = new GameState { GameId = "prepared.equivalence" };
            state.EffectRuntime.SchemaVersion = schema;
            RuleCommit.Apply(state, "prior");
            var expected = GameStateCloneService.DeepClone(state);
            var payload = NormalizedValue.CreateString("原文");
            var entry = new RuleJournalEntry { EntityId = "new", Payload = payload };
            var commit = Prepared(state, entry);
            RuleCommit.Apply(expected, "prepared.commit", entry);

            Assert.That(GameStateCloneService.AreEquivalent(state, expected), Is.True);
            Assert.That(state.EffectRuntime.TryValidate(out string reason), Is.True, reason);
            Assert.That(commit.FirstCommitSequence, Is.EqualTo(2));
            Assert.That(commit.LastCommitSequence, Is.EqualTo(2));
            payload.StringValue = "改动";
            commit.Entries[0].Payload.StringValue = "返回值改动";
            Assert.That(state.EffectRuntime.Journal[1].Payload.StringValue, Is.EqualTo("原文"));
            Assert.That(state.EffectRuntime.Journal[0].StateRevision, Is.EqualTo(1));
        }

        [Test]
        public void OnlyOpenResponsesBelowStoppedActivationReceiveNewRevision()
        {
            var state = new GameState { GameId = "prepared.response" };
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "activation" });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "response", ParentEffectId = "activation" });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "other" });
            var protectedRequest = new InteractionRequest { InteractionId = "protected", OwnerEffectId = "response" };
            var unrelated = new InteractionRequest { InteractionId = "unrelated", OwnerEffectId = "other" };
            var closed = new InteractionRequest { InteractionId = "closed", OwnerEffectId = "response", Status = "answered" };
            state.EffectRuntime.InteractionRequests.AddRange(new[] { protectedRequest, unrelated, closed });
            state.CharacterUses.Add(new CharacterUseRuntimeState { UseId = "stopped", StoppedActivationEffectId = "activation", SkippedModes = new List<string> { "tactic" } });

            var commit = Prepared(state);

            Assert.That(protectedRequest.StateRevision, Is.EqualTo(commit.StateRevision));
            Assert.That(unrelated.StateRevision, Is.Zero);
            Assert.That(closed.StateRevision, Is.Zero);
            Assert.That(protectedRequest.Status, Is.EqualTo("open"));
            Assert.That(state.EffectRuntime.TryValidate(out string reason), Is.True, reason);
        }

        [TestCase("journal")]
        [TestCase("closed_candidates")]
        [TestCase("main_link")]
        [TestCase("node_payload")]
        [TestCase("unknown_schema")]
        public void InvalidPriorGraphIsRejectedBeforeAnyCommitMutation(string corruption)
        {
            var state = new GameState { GameId = "prepared.corrupt" };
            RuleCommit.Apply(state, "prior");
            if (corruption == "journal") state.EffectRuntime.Journal[0].CommitSequence = -1;
            if (corruption == "closed_candidates") state.EffectRuntime.InteractionRequests.Add(new InteractionRequest {
                InteractionId = "closed", Status = "answered", CandidateIds = new List<string> { "duplicate", "duplicate" } });
            if (corruption == "main_link") state.EffectRuntime.MainNodes.Add(new MainlineNodeRuntimeState { NodeId = "main", NodeTypeId = "action", NextNodeId = "missing" });
            if (corruption == "node_payload") state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "bad", ContinuationData = NormalizedValue.CreateString("非法续接") });
            if (corruption == "unknown_schema") state.EffectRuntime.SchemaVersion = EffectRuntimeState.CurrentSchemaVersion + 1;
            var before = GameStateCloneService.DeepClone(state);

            Assert.Throws<InvalidOperationException>(() => Prepared(state));
            Assert.That(GameStateCloneService.AreEquivalent(state, before), Is.True);
        }

        [Test]
        public void FaultCommitRemainsSeparateFromPreparedActiveCommit()
        {
            var state = new GameState { GameId = "prepared.fault" };
            state.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
            Assert.Throws<InvalidOperationException>(() => Prepared(state));
            Assert.That(state.EffectRuntime.Journal, Is.Empty);
            var commit = RuleCommit.ApplyFault(state, "fault");
            Assert.That(commit.StateRevision, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.TryValidate(out string reason), Is.True, reason);
        }

        [TestCase("header")]
        [TestCase("tail_sequence")]
        [TestCase("tail_payload")]
        [TestCase("receipt_boundary")]
        public void IncrementalCheckRejectsCorruptedNewTailOrCommitBoundary(string corruption)
        {
            var state = new GameState { GameId = "prepared.tail" };
            var commit = Prepared(state);
            if (corruption == "header") state.EffectRuntime.NextCommitSequence++;
            if (corruption == "tail_sequence") state.EffectRuntime.Journal[0].CommitSequence++;
            if (corruption == "tail_payload") state.EffectRuntime.Journal[0].Payload = new NormalizedValue { Kind = (NormalizedValueKind)999 };
            if (corruption == "receipt_boundary") commit.FirstCommitSequence++;
            bool valid = (bool)typeof(RuleCommit).GetMethod("HasValidCommittedTail", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { state.EffectRuntime, commit, 0, 0, 1 });
            Assert.That(valid, Is.False);
        }

        [Serializable] public sealed class ListContainer
        {
            public List<string> Words = new List<string>();
            public List<object> Mixed = new List<object>();
            public List<ListContainer> Children = new List<ListContainer>();
        }

        [Test]
        public void ScalarListsAndMixedDtosCloneIndependentlyAndCyclesStillFail()
        {
            var source = new ListContainer { Words = new List<string> { null, "原文" } };
            var child = new ListContainer { Words = new List<string> { "子" } };
            source.Mixed.AddRange(new object[] { 3, "字", child });
            source.Children.Add(child);
            var clone = GameStateCloneService.DeepClone(source);
            source.Words[1] = "改动";
            child.Words[0] = "改动";
            Assert.That(clone.Words, Is.EqualTo(new[] { null, "原文" }));
            Assert.That(clone.Words, Is.Not.SameAs(source.Words));
            Assert.That(((ListContainer)clone.Mixed[2]).Words[0], Is.EqualTo("子"));
            Assert.That(clone.Children[0].Words[0], Is.EqualTo("子"));
            source.Children.Add(source);
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(source)).Message, Does.Contain("Children[1]"));
        }

        [Test]
        public void CopyToKeepsExistingListsAndDtosWithoutSharingSourceWhenListsGrowAndShrink()
        {
            var target = new GameState();
            target.Map.OpenLocationIds.Add("旧");
            var map = target.Map;
            var heldList = map.OpenLocationIds;
            var source = new GameState();
            source.Map.OpenLocationIds.AddRange(new[] { "甲", null, "乙" });
            GameStateCloneService.CopyTo(target, source);
            Assert.That(target.Map, Is.SameAs(map));
            Assert.That(target.Map.OpenLocationIds, Is.SameAs(heldList));
            source.Map.OpenLocationIds[0] = "改动";
            Assert.That(heldList[0], Is.EqualTo("甲"));
            source.Map.OpenLocationIds.RemoveRange(1, 2);
            GameStateCloneService.CopyTo(target, source);
            Assert.That(target.Map.OpenLocationIds, Is.SameAs(heldList));
            Assert.That(heldList, Is.EqualTo(new[] { "改动" }));
        }
    }
}
