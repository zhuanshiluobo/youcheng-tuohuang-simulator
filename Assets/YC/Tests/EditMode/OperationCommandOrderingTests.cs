using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;
#if YC_OPERATION_COMMAND_OFFLINE
using Assert = YC.OperationCommandOrderingOffline.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class OperationCommandOrderingTests
    {
        [Test]
        public void CanonicalEncodingMatchesIndependentVectorsAndPreservesSemanticOrder()
        {
            // 向量由独立 Python struct/hashlib 按公开编码生成，原件存于本批日志。
            var proposal = Proposal(1, 1, "golden-command");
            proposal.Command.SourceId = "源";
            proposal.Command.OptionIds = new List<string> { "甲", "乙" };
            proposal.Command.Parameters.Add(new GameCommandParameterDto { Key = "z", Value = null });
            const string expected = "4a41796893919dcd417f6c4fcb03af544f2890dcfe813b159a85b3d23fe1dd8a";
            Assert.That(OperationCommandCanonicalizer.CanonicalProposalDigest(proposal), Is.EqualTo(expected));
            proposal.Command.Parameters.Reverse();
            Assert.That(OperationCommandCanonicalizer.CanonicalProposalDigest(proposal), Is.EqualTo(expected));
            var ordered = Sequencer(Window("main", 1)).AdmitProposal(1, proposal).OrderedCommand;
            Assert.That(ordered.OrderedCommandDigest, Is.EqualTo("4a4096fcd04b36dcbc924fdceb04653d83ed6794d00065836d5dfd24d856c704"));
            Assert.That(OperationCommandCanonicalizer.EmptyPrefixDigest, Is.EqualTo("3261992550c10bfd4b0f5e81c791fa81747b504df397cd9d0c525f08500d6274"));
            Assert.That(OperationCommandCanonicalizer.CanonicalLogPrefixDigest(OperationCommandCanonicalizer.EmptyPrefixDigest, ordered),
                Is.EqualTo("8acaa6adc1b6aacc145688a1933fba1ab9ea9e00a785da3feeed274c4f6912c9"));
            proposal.Command.OptionIds.Reverse();
            Assert.That(OperationCommandCanonicalizer.CanonicalProposalDigest(proposal), Is.Not.EqualTo(expected));
            proposal.Command.OptionIds.Reverse(); proposal.Command.SourceId = null;
            string nullDigest = OperationCommandCanonicalizer.CanonicalProposalDigest(proposal);
            proposal.Command.SourceId = string.Empty;
            Assert.That(OperationCommandCanonicalizer.CanonicalProposalDigest(proposal), Is.Not.EqualTo(nullDigest));
            proposal.Command.Parameters.Add(new GameCommandParameterDto { Key = "z", Value = "重复" });
            Rejects(() => OperationCommandCanonicalizer.CanonicalProposalDigest(proposal));
            proposal.Command.Parameters.RemoveAt(proposal.Command.Parameters.Count - 1);
            proposal.Command.SourceId = "\ud800";
            Rejects(() => OperationCommandCanonicalizer.CanonicalProposalDigest(proposal));
        }

        [Test]
        public void MultipleSourcesAndWindowsInterleaveWithoutAllocatingMissingSubmission()
        {
            var sequencer = Sequencer(Window("main", 1), Window("company", 2), Window("side", 3));
            var one = sequencer.AdmitProposal(1, Proposal(1, 1, "one"));
            var missing = sequencer.AdmitProposal(1, Proposal(1, 3, "three"));
            Assert.That(one.OrderedCommand.GlobalCommandSequence, Is.EqualTo(1));
            Assert.That(missing.Status, Is.EqualTo(OperationAdmissionStatus.MissingSubmission));
            Assert.That(missing.ExpectedClientSubmissionSequence, Is.EqualTo(2));
            Assert.That(missing.OrderedCommand, Is.Null);
            Assert.That(sequencer.LastSequencedSequence, Is.EqualTo(1));
            Assert.That(sequencer.AdmitProposal(2, Proposal(2, 1, "other", "company")).OrderedCommand.GlobalCommandSequence, Is.EqualTo(2));
            Assert.That(sequencer.AdmitProposal(1, Proposal(1, 2, "two")).OrderedCommand.WindowCommandSequence, Is.EqualTo(2));
            Assert.That(sequencer.AdmitProposal(3, Proposal(3, 1, "side-one", "side")).OrderedCommand.GlobalCommandSequence, Is.EqualTo(4));
            Assert.That(sequencer.AdmitProposal(1, Proposal(1, 3, "three")).OrderedCommand.GlobalCommandSequence, Is.EqualTo(5));
            var range = sequencer.ReadRange(1, 5);
            Assert.That(range.Commands.Select(c => c.GlobalCommandSequence), Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
            Assert.That(range.Commands.Select(c => c.WindowCommandSequence), Is.EqualTo(new long[] { 1, 1, 2, 1, 3 }));
            Assert.That(sequencer.ReadWindowProgress("main").CommandCount, Is.EqualTo(3));
            Assert.That(sequencer.ReadWindowProgress("company").CommandCount, Is.EqualTo(1));
            string prefix = OperationCommandCanonicalizer.EmptyPrefixDigest;
            foreach (var command in range.Commands) prefix = OperationCommandCanonicalizer.CanonicalLogPrefixDigest(prefix, command);
            Assert.That(range.PrefixDigest, Is.EqualTo(prefix));
            Assert.That(sequencer.ReadRange(3, 2).Commands.Select(c => c.Command.CommandId), Is.EqualTo(new[] { "two", "side-one" }));
            Assert.That(sequencer.ReadRange(6, 1).ErrorCode, Is.EqualTo("range_not_available"));
        }

        [Test]
        public void AdmissionRejectsIdentityConflictsAndKeepsOwnedLogUnderBackpressure()
        {
            var facts = Window("main", 1);
            var sequencer = new OrderedCommandSequencer("ordering-match", 1, 1, 2);
            sequencer.RegisterWindow(facts); facts.AuthorizedPlayerIds.Clear();
            Assert.That(sequencer.AdmitProposal(2, Proposal(1, 1, "source")).ErrorCode, Is.EqualTo("proposal_source_mismatch"));
            foreach (string foreign in new[] { "game", "epoch", "members" })
            {
                var proposal = Proposal(1, 1, "foreign");
                if (foreign == "game") proposal.GameId = "other-game";
                if (foreign == "epoch") proposal.SimulationEpoch = 2;
                if (foreign == "members") proposal.MembershipVersion = 2;
                Assert.That(sequencer.AdmitProposal(1, proposal).ErrorCode, Is.EqualTo("proposal_identity_mismatch"));
            }
            Assert.That(sequencer.AdmitProposal(2, Proposal(2, 1, "unauthorized")).ErrorCode, Is.EqualTo("window_not_authorized"));
            var original = Proposal(1, 1, "first");
            var accepted = sequencer.AdmitProposal(1, original);
            original.Command.SourceId = "调用者改写"; accepted.OrderedCommand.Command.Parameters.Clear();
            var duplicate = sequencer.AdmitProposal(1, Proposal(1, 1, "first"));
            Assert.That(duplicate.Status, Is.EqualTo(OperationAdmissionStatus.Duplicate));
            Assert.That(duplicate.OrderedCommand.GlobalCommandSequence, Is.EqualTo(1));
            Assert.That(duplicate.OrderedCommand.Command.SourceId, Is.Empty);
            Assert.That(duplicate.OrderedCommand.Command.Parameters.Count, Is.EqualTo(1));
            Assert.That(sequencer.AdmitProposal(1, Proposal(1, 1, "changed")).ErrorCode, Is.EqualTo("submission_conflict"));
            Assert.That(sequencer.AdmitProposal(1, Proposal(1, 2, "first")).ErrorCode, Is.EqualTo("command_id_conflict"));
            Assert.That(sequencer.AdmitProposal(1, Proposal(1, 2, "second")).Status, Is.EqualTo(OperationAdmissionStatus.Accepted));
            Assert.That(sequencer.AdmitProposal(1, Proposal(1, 3, "third")).Status, Is.EqualTo(OperationAdmissionStatus.Backpressure));
            Assert.That(sequencer.AdmitProposal(1, Proposal(1, 1, "first")).Status, Is.EqualTo(OperationAdmissionStatus.Duplicate));
            var range = sequencer.ReadRange(1, 2); range.Commands[0].Command.CommandId = "改写读副本";
            Assert.That(sequencer.ReadRange(1, 2).Commands.Select(c => c.Command.CommandId), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(sequencer.LastSequencedSequence, Is.EqualTo(2));
            Rejects(() => sequencer.RegisterWindow(Window("main", 2)));
        }

        [Test]
        public void TwoRealSessionsApplySameLogOnceWithReorderAndLocalProjection()
        {
            var log = ThreeCommands();
            var first = Session(out var firstHandler); var second = Session(out var secondHandler);
            var host = Replica(first); var client = Replica(second, GameStateViewer.Player(1));
            foreach (var command in log.Commands) Assert.That(host.ApplyOrdered(command).Status, Is.EqualTo(OperationReplicaStatus.Applied));
            Assert.That(client.ApplyOrdered(log.Commands[2]).Status, Is.EqualTo(OperationReplicaStatus.Buffered));
            log.Commands[2].Command.Parameters[0].Value = "调用者改写";
            Assert.That(client.ApplyOrdered(log.Commands[0]).Status, Is.EqualTo(OperationReplicaStatus.Applied));
            var drained = client.ApplyOrdered(log.Commands[1]);
            Assert.That(drained.Outcomes.Select(o => o.GlobalCommandSequence), Is.EqualTo(new long[] { 2, 3 }));
            Assert.That(drained.Status, Is.EqualTo(OperationReplicaStatus.Applied));
            var repeated = client.ApplyOrdered(log.Commands[0]);
            Assert.That(repeated.Status, Is.EqualTo(OperationReplicaStatus.Duplicate));
            Assert.That(repeated.RuleResult, Is.SameAs(client.GetAppliedRuleResult(1)));
            Assert.That(firstHandler.Calls, Is.EqualTo(3)); Assert.That(secondHandler.Calls, Is.EqualTo(3));
            Assert.That(first.State.Round, Is.EqualTo(4)); Assert.That(second.State.Round, Is.EqualTo(4));
            Assert.That(first.State.EffectRuntime.StateRevision, Is.EqualTo(3));
            Assert.That(GameStateCloneService.AreEquivalent(first.State, second.State), Is.True);
            Assert.That(second.View.Players.Single(p => p.PlayerId == 2).HandCardIds, Is.Empty);
            Assert.That(second.State.FindPlayer(2).HandCardIds, Is.EqualTo(new[] { "private-two" }));
            var prefix = client.CreateValidationPrefix();
            Assert.That(prefix.ValidatedThroughSequence, Is.EqualTo(3)); Assert.That(prefix.PrefixDigest, Is.EqualTo(log.PrefixDigest));
            Assert.That(prefix.PrefixDigest, Is.EqualTo(host.CreateValidationPrefix().PrefixDigest));
            prefix.ValidatedThroughSequence = 100; prefix.Outcomes[0].Reason = "调用者改写";
            Assert.That(client.LastAppliedSequence, Is.EqualTo(3));
            Assert.That(client.CreateValidationPrefix().Outcomes[0].Reason, Is.Empty);
        }

        [Test]
        public void StrictRevisionAndPendingChoiceFailuresStopWithOriginalResult()
        {
            var sequencer = Sequencer(Window("main", 1));
            var one = sequencer.AdmitProposal(1, Proposal(1, 1, "one", revision: 0)).OrderedCommand;
            var stale = sequencer.AdmitProposal(1, Proposal(1, 2, "stale", revision: 0)).OrderedCommand;
            var three = sequencer.AdmitProposal(1, Proposal(1, 3, "three", revision: 2)).OrderedCommand;
            var session = Session(out var handler); var replica = Replica(session);
            replica.ApplyOrdered(three); replica.ApplyOrdered(one);
            var failure = replica.ApplyOrdered(stale);
            Assert.That(failure.Status, Is.EqualTo(OperationReplicaStatus.Stopped));
            Assert.That(failure.RuleResult, Is.SameAs(handler.LastResult));
            Assert.That(replica.FailureResult, Is.SameAs(failure.RuleResult));
            Assert.That(failure.RuleResult.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(failure.Reason, Is.EqualTo("严格版本拒绝"));
            Assert.That(replica.FailureCommand.Command.Parameters[0].Value, Is.EqualTo("0"));
            Assert.That(replica.LastAppliedSequence, Is.EqualTo(1)); Assert.That(replica.BufferedCommandCount, Is.EqualTo(1));
            Assert.That(replica.CreateValidationPrefix().Outcomes.Count, Is.EqualTo(1));
            Assert.That(replica.ApplyOrdered(three).Status, Is.EqualTo(OperationReplicaStatus.Stopped));
            Assert.That(handler.Calls, Is.EqualTo(2));
            var pendingSession = Session(out var pendingHandler);
            pendingSession.State.PendingChoice = new PendingChoiceState { ChoiceId = "required-child", PlayerId = 1,
                ChoiceType = "必要选择", CardId = "private-one", OptionIds = new List<string> { "answer" } };
            var choice = pendingSession.State.PendingChoice; var blocked = Replica(pendingSession);
            var pendingFailure = blocked.ApplyOrdered(one);
            Assert.That(pendingFailure.RuleResult.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.PendingChoiceRequired));
            Assert.That(pendingFailure.RuleResult, Is.SameAs(blocked.FailureResult));
            Assert.That(pendingSession.State.PendingChoice, Is.SameAs(choice));
            Assert.That(blocked.LastAppliedSequence, Is.EqualTo(0)); Assert.That(pendingHandler.Calls, Is.EqualTo(0));
        }

        [Test]
        public void ReplicaRejectsForeignIdentityAndBoundsGapsWithoutLosingBufferedRecords()
        {
            var log = ThreeCommands(); var session = Session(out var handler);
            var replica = new DeterministicCommandReplica(session, 1, 1, 1); replica.RegisterWindow(Window("main", 1));
            foreach (string foreign in new[] { "game", "epoch", "members" })
            {
                var command = ThreeCommands().Commands[0];
                if (foreign == "game") command.GameId = "foreign";
                if (foreign == "epoch") command.SimulationEpoch = 2;
                if (foreign == "members") command.MembershipVersion = 2;
                Assert.That(replica.ApplyOrdered(command).Status, Is.EqualTo(OperationReplicaStatus.Rejected));
            }
            Assert.That(handler.Calls, Is.EqualTo(0));
            Assert.That(replica.ApplyOrdered(log.Commands[2]).ExpectedGlobalSequence, Is.EqualTo(1));
            Assert.That(replica.ApplyOrdered(log.Commands[1]).Status, Is.EqualTo(OperationReplicaStatus.Backpressure));
            Assert.That(replica.BufferedCommandCount, Is.EqualTo(1));
            replica.ApplyOrdered(log.Commands[0]); var drained = replica.ApplyOrdered(log.Commands[1]);
            Assert.That(drained.Status, Is.EqualTo(OperationReplicaStatus.Applied)); Assert.That(handler.Calls, Is.EqualTo(3));
            var conflicting = ThreeCommands().Commands[0]; conflicting.Command.SourceId = "冲突"; Seal(conflicting);
            Assert.That(replica.ApplyOrdered(conflicting).ErrorCode, Is.EqualTo("ordered_sequence_conflict"));
            Assert.That(replica.IsStopped, Is.True); Assert.That(handler.Calls, Is.EqualTo(3));
            var malformed = ThreeCommands().Commands[0]; malformed.Command = null;
            Assert.That(Replica(Session(out _)).ApplyOrdered(malformed).Status, Is.EqualTo(OperationReplicaStatus.Stopped));
            var limitedSession = Session(out var limitedHandler);
            var limited = new DeterministicCommandReplica(limitedSession, 1, 1, maximumAppliedCommands: 1);
            limited.RegisterWindow(Window("main", 1)); limited.ApplyOrdered(log.Commands[0]);
            Assert.That(limited.ApplyOrdered(log.Commands[1]).Status, Is.EqualTo(OperationReplicaStatus.Backpressure));
            Assert.That(limited.LastAppliedSequence, Is.EqualTo(1)); Assert.That(limitedHandler.Calls, Is.EqualTo(1));
            Assert.That(limited.GetAppliedRuleResult(1), Is.SameAs(limitedHandler.LastResult));
        }

        [Test]
        public void ReentrantApplyCannotSubmitSameCommandTwice()
        {
            var command = ThreeCommands().Commands[0]; var session = Session(out var handler); var replica = Replica(session);
            OperationCommandReplicaResult nested = null;
            handler.OnHandle = () => nested = replica.ApplyOrdered(command);
            var applied = replica.ApplyOrdered(command);
            Assert.That(nested.Status, Is.EqualTo(OperationReplicaStatus.Backpressure));
            Assert.That(nested.ErrorCode, Is.EqualTo("replica_apply_in_progress"));
            Assert.That(applied.Status, Is.EqualTo(OperationReplicaStatus.Applied));
            Assert.That(applied.RuleResult, Is.SameAs(handler.LastResult));
            applied.Outcomes[0].CommandId = "改写结果";
            Assert.That(replica.CreateValidationPrefix().Outcomes[0].CommandId, Is.EqualTo("one"));
            Assert.That(replica.ApplyOrdered(command).Status, Is.EqualTo(OperationReplicaStatus.Duplicate));
            Assert.That(handler.Calls, Is.EqualTo(1));
        }

        [Test]
        public void DeclaredBasesAndNewWindowsRemainSeparateFromStateTurnAndReceivedPrefixes()
        {
            var initial = ThreeCommands(); var session = Session(out var handler); var prior = Replica(session);
            prior.ApplyOrdered(initial.Commands[0]); prior.ApplyOrdered(initial.Commands[1]);
            string basePrefix = prior.CreateValidationPrefix().PrefixDigest;
            var sequencer = new OrderedCommandSequencer("ordering-match", 2, 1, baseGlobalSequence: 2, baseCommandPrefixDigest: basePrefix);
            var replica = new DeterministicCommandReplica(session, 2, 1, baseGlobalSequence: 2, baseCommandPrefixDigest: basePrefix);
            var resumedFacts = Window("main", 1); resumedFacts.SimulationEpoch = 2;
            resumedFacts.OpenedAtGlobalSequence = 2; resumedFacts.BaseWindowSequence = 2; resumedFacts.PrefixDigest = basePrefix;
            sequencer.RegisterWindow(resumedFacts); replica.RegisterWindow(resumedFacts);
            var proposal = Proposal(1, 1, "new-epoch", revision: 2); proposal.SimulationEpoch = 2; proposal.ObservedGlobalSequence = 2;
            var ordered = sequencer.AdmitProposal(1, proposal).OrderedCommand;
            Assert.That(ordered.GlobalCommandSequence, Is.EqualTo(3)); Assert.That(ordered.WindowCommandSequence, Is.EqualTo(3));
            Assert.That(sequencer.ReadWindowProgress("main").CommandCount, Is.EqualTo(3));
            Assert.That(replica.ApplyOrdered(ordered).Status, Is.EqualTo(OperationReplicaStatus.Applied));
            Assert.That(replica.LastAppliedSequence, Is.EqualTo(3));
            var window = Window("parallel", 2); window.SimulationEpoch = 2; window.OpenedAtGlobalSequence = 3;
            sequencer.RegisterWindow(window); replica.RegisterWindow(window);
            var other = Proposal(2, 1, "other-player", "parallel", 3); other.SimulationEpoch = 2; other.ObservedGlobalSequence = 3;
            var next = sequencer.AdmitProposal(2, other).OrderedCommand;
            Assert.That(next.WindowCommandSequence, Is.EqualTo(1));
            Assert.That(replica.ApplyOrdered(next).Status, Is.EqualTo(OperationReplicaStatus.Applied));
            Assert.That(session.State.CurrentPlayerId, Is.EqualTo(1)); Assert.That(handler.Calls, Is.EqualTo(4));
            Assert.That(replica.CreateValidationPrefix().PrefixDigest, Is.EqualTo(sequencer.PrefixDigest));
            Assert.That(sequencer.ReadRange(2, 1).ErrorCode, Is.EqualTo("range_before_base"));
            Assert.That(replica.ApplyOrdered(initial.Commands[1]).Status, Is.EqualTo(OperationReplicaStatus.Rejected));
            Assert.That(replica.GetAppliedRuleResult(2), Is.Null);
            var missingBase = Window("missing-base", 1); missingBase.SimulationEpoch = 2; missingBase.BaseWindowSequence = 1;
            Rejects(() => sequencer.RegisterWindow(missingBase));
        }

        private static OperationWindowDescriptorDto Window(string id, params int[] players) => new OperationWindowDescriptorDto
        {
            GameId = "ordering-match", SimulationEpoch = 1, MembershipVersion = 1, OperationWindowId = id, WindowGeneration = 1,
            AuthorizedPlayerIds = new List<int>(players), RequiredReplicaIds = new List<string> { "host", "client" },
            WindowContractDigest = "externally-supplied-window-contract"
        };

        private static OrderedCommandSequencer Sequencer(params OperationWindowDescriptorDto[] windows)
        {
            var sequencer = new OrderedCommandSequencer("ordering-match", 1, 1);
            foreach (var window in windows) sequencer.RegisterWindow(window);
            return sequencer;
        }

        private static OperationCommandProposalDto Proposal(int player, long sequence, string id, string window = "main", int revision = 0) =>
            new OperationCommandProposalDto { GameId = "ordering-match", SimulationEpoch = 1, MembershipVersion = 1,
                ClientSubmissionSequence = sequence, ObservedStateRevision = revision, OperationWindowId = window, WindowGeneration = 1,
                Command = new GameCommandDto { CommandId = id, Kind = GameCommandKind.MoveCity, PlayerId = player,
                    Parameters = new List<GameCommandParameterDto> { new GameCommandParameterDto { Key = "expectedRevision", Value = revision.ToString(CultureInfo.InvariantCulture) } } } };

        private static OperationCommandRangeDto ThreeCommands()
        {
            var sequencer = Sequencer(Window("main", 1));
            sequencer.AdmitProposal(1, Proposal(1, 1, "one", revision: 0));
            sequencer.AdmitProposal(1, Proposal(1, 2, "two", revision: 1));
            sequencer.AdmitProposal(1, Proposal(1, 3, "three", revision: 2));
            return sequencer.ReadRange(1, 3);
        }

        private static void Seal(OrderedOperationCommandDto command)
        {
            command.ProposalDigest = OperationCommandCanonicalizer.CanonicalProposalDigest(command);
            command.OrderedCommandDigest = OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(command);
        }

        private static GameSession Session(out CountingHandler handler)
        {
            var state = new GameState { GameId = "ordering-match", EntitySchemaVersion = 0, Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1, StartPlayerId = 1, Round = 1, LaunchPlayerOrder = new List<int> { 1, 2 } };
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "本人", HandCardIds = new List<string> { "private-one" } });
            state.Players.Add(new PlayerState { PlayerId = 2, Name = "其他玩家", HandCardIds = new List<string> { "private-two" } });
            var session = new GameSession(state); handler = new CountingHandler(); session.RegisterHandler(handler); return session;
        }

        private static DeterministicCommandReplica Replica(GameSession session, GameStateViewer? viewer = null)
        {
            var replica = new DeterministicCommandReplica(session, 1, 1, projectionViewer: viewer);
            replica.RegisterWindow(Window("main", 1)); return replica;
        }

        private static void Rejects(Action action)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Assert.That(rejected, Is.True);
        }

        // 只测真实会话事务、版本拒绝与提交次数；不借受控处理器宣称玩法整局通过。
        private sealed class CountingHandler : IGameCommandHandler
        {
            internal int Calls; internal CommandResult LastResult; internal Action OnHandle;
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.MoveCity;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                Calls++; OnHandle?.Invoke();
                if (command.Parameters["expectedRevision"] != state.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture))
                    return LastResult = CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.StateRevisionMismatch, "严格版本拒绝"));
                state.Round++;
                return LastResult = CommandResult.SuccessResult(new List<GameEvent>(), "协议会话检查");
            }
        }
    }
}
