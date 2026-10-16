using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
#if YC_SIMULATION_OFFLINE
using Assert = YC.SimulationOffline.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class ConfirmedSimulationRecoveryTests
    {
        [Test]
        public void SaveCaptureDoesNotFreezeLiveSessionAndColdResumeContinuesOriginalSequence()
        {
            var group = CreateGroupWithColors(false, true); var host = group.Coordinators[0];
            string original = RuleStateDigestService.Compute(group.Sessions[0].State);
            var permissions = host.ReadWindowPermissions().Select(w => w.CanSubmitForLocalPlayer).ToArray();
            Assert.That(host.TryCreateSaveCheckpoint(out _, out var capture), Is.True, capture.Reason);
            Assert.That(host.IsRecoveringConfirmedPrefix, Is.False);
            Assert.That(host.ReadWindowPermissions().Select(w => w.CanSubmitForLocalPlayer), Is.EqualTo(permissions));
            Assert.That(RuleStateDigestService.Compute(group.Sessions[0].State), Is.EqualTo(original));
            var first = AdmitCurrent(group, "saved-first-choice");
            Assert.That(host.TryCreateSaveCheckpoint(out _, out capture), Is.False);
            Assert.That(host.Pump(1), Is.EqualTo(1));
            Assert.That(host.TryCreateSaveCheckpoint(out var checkpoint, out capture), Is.True, capture.Reason);
            Assert.That(checkpoint.Baseline.ConfirmedGlobalSequence, Is.EqualTo(1));
            var save = MatchSaveResumeTests.FromCheckpoint(checkpoint, 3);
            var restored = MatchSaveResumeTests.Restore(save);
            Assert.That(RuleStateDigestService.Compute(restored.State), Is.EqualTo(RuleStateDigestService.Compute(group.Sessions[0].State)));
            var cold = DeterministicSimulationCoordinatorTests.CreateFormalSessions(out var registries, true);
            group.Sessions[0] = restored;
            group.Coordinators[0] = DeterministicSimulationCoordinator.FromConfirmedRecovery(restored,
                MatchSaveData.OptionsFor(checkpoint.Baseline, true, 3), checkpoint.Baseline, restored.State);
            group.Coordinators[0].CompleteConfirmedRecovery(true);
            for (int i = 1; i < 3; i++)
            {
                var installer = new SavedSimulationCheckpointInstaller(cold[i], registries[i], GameStateViewer.Player(i),
                    MatchSaveData.OptionsFor(checkpoint.Baseline, false, i), checkpoint.Baseline.CheckpointId, checkpoint.Baseline.MetadataDigest);
                var identity = new SimulationCheckpointEnvelope { ProtocolVersion = SimulationCheckpointEnvelope.SavedProtocolVersion,
                    Purpose = SimulationCheckpointEnvelope.SavedSimulation, GameId = save.GameId, ContentPackHash = save.ContentHash,
                    SimulationEpoch = 2, SourceSimulationEpoch = 1, MembershipVersion = 1,
                    GlobalCommandSequence = 1, CheckpointId = checkpoint.Baseline.CheckpointId };
                var channelType = Type.GetType("YC.Infrastructure.Multiplayer.SimulationCheckpointChannel, Assembly-CSharp", true);
                var channel = Activator.CreateInstance(channelType, identity, installer, null, new[] { 1, 2, 3 });
                channelType.GetMethod("BindClientConnection").Invoke(channel, new object[] { "saved-peer" });
                object receipt = null;
                foreach (var part in SimulationCheckpointTransfer.Encode(identity, save.SimulationCheckpointJson).Split())
                    receipt = channelType.GetMethod("ReceivePart").Invoke(channel, new object[] { "saved-peer", part.Envelope, part.Offset, part.Payload });
                Assert.That(receipt.GetType().GetProperty("Status").GetValue(receipt).ToString(), Is.EqualTo("Installed"));
                var state = cold[i].State;
                Assert.That(installer.TryInstall(checkpoint.Baseline.CheckpointId, save.SimulationCheckpointJson, out var result), Is.True);
                Assert.That(result.AlreadyInstalled, Is.True);
                Assert.That(ReferenceEquals(state, cold[i].State), Is.True);
                group.Sessions[i] = cold[i]; group.Coordinators[i] = installer.Coordinator;
                installer.Coordinator.CompleteConfirmedRecovery(true);
            }
            Seal(group, 1);
            var next = AdmitCurrent(group, "saved-next-choice");
            Assert.That(next.GlobalCommandSequence, Is.EqualTo(2));
            Assert.That(next.SimulationEpoch, Is.EqualTo(2));
            foreach (var replica in group.Coordinators)
            {
                replica.ReceiveOrdered(next); Assert.That(replica.Pump(1), Is.EqualTo(1));
                Assert.That(replica.ReceiveOrdered(first).Status, Is.EqualTo(OperationReplicaStatus.Rejected));
                Assert.That(replica.ReceiveOrdered(next).Status, Is.EqualTo(OperationReplicaStatus.Duplicate));
                Assert.That(replica.Pump(1), Is.EqualTo(0));
            }
            Assert.That(group.Sessions.Select(s => RuleStateDigestService.Compute(s.State)).Distinct().Count(), Is.EqualTo(1));
        }

        [Test]
        public void ProtocolFactFailureExportsOnlyOriginalConfirmedPrefixAndKeepsTail()
        {
            var group = CreateGroup(true); var host = group.Coordinators[0];
            var installer = new SimulationConfirmedRecoveryInstaller(group.Sessions[0], group.Registries[0], GameStateViewer.Player(3), host.SessionOptions);
            string before = RuleStateDigestService.Compute(group.Sessions[0].State);
            var ordered = AdmitCurrent(group, "fact-failure");
            Assert.That(host.Pump(1), Is.EqualTo(1)); Assert.That(host.IsStopped, Is.True);
            Assert.That(host.LastAppliedSequence, Is.EqualTo(1)); Assert.That(host.ValidatedThroughSequence, Is.EqualTo(0));
            var prefix = host.ReadLocalValidationPrefix();
            Assert.That(prefix.ValidatedThroughSequence, Is.EqualTo(0)); Assert.That(prefix.Outcomes.Count, Is.EqualTo(0));
            Assert.That(prefix.PrefixDigest, Is.EqualTo(OperationCommandCanonicalizer.EmptyPrefixDigest));
            Assert.That(host.ReadPendingTail().Single().OrderedCommandDigest, Is.EqualTo(ordered.OrderedCommandDigest));
            host.BeginConfirmedRecovery();
            Assert.That(host.TryCreateConfirmedRecoveryCheckpoint(0, 2, out var checkpoint, out _), Is.True);
            Assert.That(checkpoint.Baseline.ConfirmedCommands.Count, Is.EqualTo(0));
            Assert.That(RuleStateDigestService.Compute(checkpoint.Archive.Snapshot.State), Is.EqualTo(before));
            Assert.That(host.TryCreateConfirmedRecoveryCheckpoint(1, 2, out _, out var notRetained), Is.False);
            Assert.That(notRetained.ErrorCode, Is.EqualTo("checkpoint_not_retained"));
            var notice = Notice(checkpoint, false);
            Assert.That(installer.TryPrepare(notice, SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint), out var prepared, out _), Is.True);
            Assert.That(installer.TryCommit(prepared, out _), Is.True); prepared.Coordinator.CompleteConfirmedRecovery(false);
            Assert.That(prepared.Coordinator.IsStopped, Is.True);
            Assert.That(prepared.Coordinator.ReadWindowPermissions().Any(p => p.CanSubmitForLocalPlayer), Is.False);
        }

        [Test]
        public void PackageRestoresWindowsSubmissionCountersAndConsumedCommandsWithoutHistoricalSuccess()
        {
            var group = CreateGroup(false); var originalHost = group.Coordinators[0];
            var first = AdmitCurrent(group, "confirmed-choice"); Assert.That(originalHost.Pump(1), Is.EqualTo(1));
            originalHost.BeginConfirmedRecovery();
            Assert.That(originalHost.TryCreateConfirmedRecoveryCheckpoint(1, 2, out var checkpoint, out _), Is.True);
            string json = SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint);
            var identity = new SimulationCheckpointEnvelope { ProtocolVersion = 2, Purpose = SimulationCheckpointEnvelope.ConfirmedSimulationRecovery,
                GameId = checkpoint.Baseline.GameId, SourceSimulationEpoch = 1, SimulationEpoch = 2, MembershipVersion = 1,
                CheckpointId = checkpoint.Baseline.CheckpointId, GlobalCommandSequence = 1, ContentPackHash = checkpoint.Baseline.ContentPackHash,
                ArchiveDigestVersion = SimulationCheckpointEnvelope.RecoveryDigestVersion };
            var transfer = SimulationCheckpointTransfer.Encode(identity, json); var assembler = new SimulationCheckpointTransfer.Assembler(identity); string decoded = null;
            foreach (var part in transfer.Split()) Assert.That(assembler.TryAppend(part.Envelope, part.Offset, part.Payload, out decoded, out _), Is.True);
            Assert.That(decoded, Is.EqualTo(json));
            int delivered = 0;
            for (int i = 0; i < 3; i++)
            {
                int player = i == 0 ? 3 : i;
                var installer = new SimulationConfirmedRecoveryInstaller(group.Sessions[i], group.Registries[i], GameStateViewer.Player(player), group.Coordinators[i].SessionOptions);
                Assert.That(installer.TryPrepare(Notice(checkpoint, true), decoded, out var prepared, out _), Is.True);
                Assert.That(installer.TryCommit(prepared, out _), Is.True);
                var published = group.Sessions[i].State;
                Assert.That(installer.TryCommit(prepared, out _), Is.True); Assert.That(ReferenceEquals(published, group.Sessions[i].State), Is.True);
                group.Coordinators[i] = prepared.Coordinator; prepared.Coordinator.SimulationCommandApplied += _ => delivered++;
                prepared.Coordinator.CompleteConfirmedRecovery(true);
            }
            Assert.That(delivered, Is.EqualTo(0));
            Assert.That(group.Sessions.Select(s => RuleStateDigestService.Compute(s.State)).Distinct().Count(), Is.EqualTo(1));
            Seal(group, 1);
            int actor = group.Sessions[0].State.CurrentPlayerId; int index = actor == 3 ? 0 : actor;
            var duplicateChoice = DeterministicSimulationCoordinatorTests.CreateCurrentAnswer(group.Sessions[index], actor, first.Command.CommandId);
            Assert.That(group.Coordinators[index].TryCreateLocalProposal(duplicateChoice, out var duplicateProposal, out _), Is.True);
            var refused = group.Coordinators[0].AdmitProposal(actor, duplicateProposal);
            Assert.That(refused.Status, Is.EqualTo(OperationAdmissionStatus.Rejected)); group.Coordinators[index].ReceiveProposalResult(refused);
            Assert.That(group.Coordinators[0].LastSequencedSequence, Is.EqualTo(1)); Assert.That(group.Coordinators[0].IsStopped, Is.False);
            var stale = DeterministicSimulationCoordinatorTests.CreateCurrentAnswer(group.Sessions[index], actor, "stale-choice");
            stale.Parameters["expectedRevision"] = (group.Sessions[index].State.EffectRuntime.StateRevision - 1).ToString();
            Assert.That(group.Coordinators[index].TryCreateLocalProposal(stale, out _, out var validation), Is.False);
            Assert.That(validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            var next = AdmitCurrent(group, "new-explicit-choice");
            Assert.That(next.GlobalCommandSequence, Is.EqualTo(2)); Assert.That(next.SimulationEpoch, Is.EqualTo(2));
            Assert.That(group.Coordinators[0].Pump(1), Is.EqualTo(1)); Assert.That(delivered, Is.EqualTo(1));
            Assert.That(group.Coordinators[0].ReceiveOrdered(first).Status, Is.EqualTo(OperationReplicaStatus.Rejected));
            Assert.That(group.Coordinators[0].ValidatedThroughSequence, Is.EqualTo(2));
        }

        [Test]
        public void InvalidAndStalePackagesCannotPartiallyPublishSessionOrReplaySuccess()
        {
            var group = CreateGroup(false); var host = group.Coordinators[0]; host.BeginConfirmedRecovery();
            Assert.That(host.TryCreateConfirmedRecoveryCheckpoint(0, 2, out var checkpoint, out _), Is.True);
            var notice = Notice(checkpoint, true); string json = SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint);
            var session = group.Sessions[1]; var state = session.State; var view = session.View;
            var installer = new SimulationConfirmedRecoveryInstaller(session, group.Registries[1], GameStateViewer.Player(1), group.Coordinators[1].SessionOptions);
            var broken = JObject.Parse(json); ((JObject)broken["Continuation"]).Remove("HasPendingActionLog");
            Assert.That(installer.TryPrepare(notice, broken.ToString(), out _, out _), Is.False);
            Assert.That(ReferenceEquals(state, session.State), Is.True); Assert.That(ReferenceEquals(view, session.View), Is.True);
            Assert.That(installer.TryPrepare(notice, json, out var prepared, out _), Is.True);
            Assert.That(installer.TryPrepare(notice, json, out var repeated, out _), Is.True); Assert.That(ReferenceEquals(prepared, repeated), Is.True);
            var changed = JObject.Parse(json); changed["Archive"]["Snapshot"]["State"]["Logs"] = new JArray();
            changed["Continuation"]["Message"] = "冲突包";
            Assert.That(installer.TryPrepare(notice, changed.ToString(), out _, out _), Is.False);
            var command = DeterministicSimulationCoordinatorTests.CreateCurrentAnswer(session, session.State.CurrentPlayerId, "post-prepare-original");
            Assert.That(session.Submit(command).Succeeded, Is.True);
            string current = RuleStateDigestService.Compute(session.State);
            Assert.That(installer.TryCommit(prepared, out _), Is.False);
            Assert.That(RuleStateDigestService.Compute(session.State), Is.EqualTo(current));
            Assert.That(group.Coordinators[1].SessionOptions.SimulationEpoch, Is.EqualTo(1));
        }

        public static SimulationRecoveryNoticeDto Notice(SimulationRecoveryCheckpointDto checkpoint, bool resumeAllowed) => new SimulationRecoveryNoticeDto {
            GameId = checkpoint.Baseline.GameId, SimulationEpoch = checkpoint.Baseline.SimulationEpoch, MembershipVersion = checkpoint.Baseline.MembershipVersion,
            SourceSimulationEpoch = checkpoint.Baseline.SourceSimulationEpoch, CheckpointId = checkpoint.Baseline.CheckpointId,
            ConfirmedGlobalSequence = checkpoint.Baseline.ConfirmedGlobalSequence, MetadataDigest = checkpoint.Baseline.MetadataDigest,
            Phase = SimulationRecoveryPhase.Prepare, ResumeAllowed = resumeAllowed };

        private sealed class Group
        { internal GameSession[] Sessions; internal EffectRegistry[] Registries; internal DeterministicSimulationCoordinator[] Coordinators; }

        private static Group CreateGroup(bool failNextBoundary) => CreateGroupWithColors(failNextBoundary, false);

        private static Group CreateGroupWithColors(bool failNextBoundary, bool uniqueSeatColors)
        {
            var sessions = DeterministicSimulationCoordinatorTests.CreateFormalSessions(out var registries, uniqueSeatColors);
            string archive = HostRecoveryService.SerializeArchive(sessions[0].CreateHostArchive());
            for (int i = 1; i < 3; i++)
            {
                bool installed = new InitialSimulationCheckpointInstaller(sessions[i], registries[i], GameStateViewer.Player(i))
                    .TryInstall("recovery-test.initial", archive, out var result);
                var raw = JObject.Parse(archive)["Snapshot"]["State"];
                Assert.That(installed, Is.True, result.FaultCode + ":" + result.Diagnostic + ";实体=" + raw["EntitySchemaVersion"] +
                    ";运行时=" + raw["EffectRuntime"]?["SchemaVersion"] + ";集合=" + string.Join(",", ((JObject)raw["ItemZones"]).Properties().Select(p => p.Name)));
            }
            var group = new Group { Sessions = sessions, Registries = registries, Coordinators = new DeterministicSimulationCoordinator[3] };
            for (int i = 0; i < 3; i++)
            {
                var options = DeterministicSimulationSessionOptions.CreateInitial(i == 0, i == 0 ? 3 : i, sessions[i], new[] { 1, 2, 3 });
                if (i == 0 && failNextBoundary) options.MaximumBoundaryFacts = 1;
                group.Coordinators[i] = new DeterministicSimulationCoordinator(sessions[i], options); group.Coordinators[i].Begin();
                Assert.That(group.Coordinators[i].IsStopped, Is.False);
            }
            Seal(group, 0); return group;
        }

        private static void Seal(Group group, long fence)
        {
            var host = group.Coordinators[0]; var boundary = host.ReadBoundary(fence);
            Assert.That(boundary, Is.Not.Null);
            SimulationWindowPermitDto permit = null; Action<SimulationWindowPermitDto> observed = value => permit = value; host.WindowPermitProduced += observed;
            try
            {
                for (int i = 1; i < 3; i++)
                {
                    Assert.That(group.Coordinators[i].ReceiveBoundary(boundary), Is.True);
                    Assert.That(host.ReceiveWindowReady(i, new SimulationWindowReadyDto { GameId = boundary.GameId, SimulationEpoch = boundary.SimulationEpoch,
                        MembershipVersion = boundary.MembershipVersion, PlayerId = i, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(i),
                        Boundary = group.Coordinators[i].ReadBoundary(fence) }), Is.True);
                }
                if (permit != null) foreach (var client in group.Coordinators.Skip(1)) Assert.That(client.ReceiveWindowPermit(permit), Is.True);
                Assert.That(host.IsBoundaryPermitted(fence), Is.True);
            }
            finally { host.WindowPermitProduced -= observed; }
        }

        private static OrderedOperationCommandDto AdmitCurrent(Group group, string id)
        {
            int player = group.Sessions[0].State.CurrentPlayerId; int index = player == 3 ? 0 : player;
            var command = DeterministicSimulationCoordinatorTests.CreateCurrentAnswer(group.Sessions[index], player, id);
            Assert.That(group.Coordinators[index].TryCreateLocalProposal(command, out var proposal, out _), Is.True);
            var accepted = group.Coordinators[0].AdmitProposal(player, proposal);
            Assert.That(accepted.Status, Is.EqualTo(OperationAdmissionStatus.Accepted)); return accepted.OrderedCommand;
        }
    }
}
