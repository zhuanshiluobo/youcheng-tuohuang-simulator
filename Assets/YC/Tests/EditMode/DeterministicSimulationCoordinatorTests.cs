using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
#if YC_SIMULATION_OFFLINE
using Assert = YC.SimulationOffline.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class DeterministicSimulationCoordinatorTests
    {
        public static GameSession[] CreateFormalSessions(out EffectRegistry[] registries, bool uniqueSeatColors = false)
        {
            var fixture = new FormalFixture(false, uniqueSeatColors); var first = fixture.Build(LaunchMode.Client, 1, true); var second = fixture.Build(LaunchMode.Client, 2, true);
            registries = new[] { fixture.Host.Registry, first.Registry, second.Registry };
            return new[] { fixture.Host.Session, first.Session, second.Session };
        }
        public static GameCommand CreateCurrentAnswer(GameSession session, int player, string commandId) => Answer(session, player, commandId);

        [Test]
        public void CompleteInitialArchiveStartsThreeRealReplicasAndMissingLogIsAppliedOnce()
        {
            var fixture = new FormalFixture(false); var group = fixture.Group();
            Assert.That(group.Coordinators.All(c => !c.ReadWindowPermissions().Any(w => w.CanSubmitForLocalPlayer)), Is.True);
            group.Seal(0, false);
            Assert.That(group.Coordinators[1].ReadWindowPermissions().Any(w => w.CanSubmitForLocalPlayer), Is.False);
            group.Seal(0, true);
            var player = group.Sessions[0].State.CurrentPlayerId;
            int actor = group.Index(player);
            var command = Answer(group.Sessions[actor], player, "initial-placement");
            Assert.That(group.Coordinators[actor].TryCreateLocalProposal(command, out var proposal, out _), Is.True);
            var admitted = group.Host.AdmitProposal(player, proposal);
            Assert.That(admitted.Status, Is.EqualTo(OperationAdmissionStatus.Accepted)); Assert.That(group.Host.LastAppliedSequence, Is.EqualTo(0));
            Assert.That(group.Coordinators[actor].ReceiveOrdered(admitted.OrderedCommand).Status, Is.EqualTo(OperationReplicaStatus.Buffered));
            Assert.That(group.Coordinators[actor].Pump(1), Is.EqualTo(1));
            Assert.That(group.Host.Pump(1), Is.EqualTo(1));
            int lagging = Enumerable.Range(1, 2).First(i => i != actor);
            var range = group.Host.ReadMissingRange(1, 4); Assert.That(range.Commands.Single().Command.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(group.Coordinators[lagging].ReceiveOrdered(range.Commands.Single()).Status, Is.EqualTo(OperationReplicaStatus.Buffered));
            Assert.That(group.Coordinators[lagging].Pump(1), Is.EqualTo(1));
            foreach (var coordinator in group.Coordinators)
            {
                var before = RuleStateDigestService.Compute(group.Sessions[Array.IndexOf(group.Coordinators, coordinator)].State);
                Assert.That(coordinator.ReceiveOrdered(range.Commands.Single()).Status, Is.EqualTo(OperationReplicaStatus.Duplicate));
                Assert.That(coordinator.Pump(8), Is.EqualTo(0));
                Assert.That(RuleStateDigestService.Compute(group.Sessions[Array.IndexOf(group.Coordinators, coordinator)].State), Is.EqualTo(before));
            }
            Assert.That(group.Sessions.Select(s => RuleStateDigestService.Compute(s.State)).Distinct().Count(), Is.EqualTo(1));
            Assert.That(group.Coordinators[1].ReceiveValidationPrefix(group.Prefixes.Last()), Is.True);
            Assert.That(group.Coordinators[1].ValidatedThroughSequence, Is.EqualTo(1));
            Assert.That(group.Coordinators.All(c => !c.IsStopped), Is.True);
            Assert.That(fixture.Installations, Is.EqualTo(2));
        }

        [Test]
        public void SameActorContinuesBeforeHostPumpAndChangingActorWaitsForExactFence()
        {
            var group = ActionGroup(); group.Seal(0, true); var actor = group.Coordinators[1];
            var first = Answer(group.Sessions[1], 1, "first-answer");
            Assert.That(actor.TryCreateLocalProposal(first, out var firstProposal, out _), Is.True);
            var admission1 = group.Host.AdmitProposal(1, firstProposal); Assert.That(admission1.Status, Is.EqualTo(OperationAdmissionStatus.Accepted), admission1.Reason);
            var ordered1 = admission1.OrderedCommand;
            actor.ReceiveOrdered(ordered1); int firstApplied = actor.Pump(1); Assert.That(firstApplied, Is.EqualTo(1), actor.LastFault?.FaultCode + ": " + actor.LastFault?.Diagnostic); Assert.That(group.Host.LastAppliedSequence, Is.EqualTo(0));
            Assert.That(actor.ValidatedThroughSequence, Is.EqualTo(0)); Assert.That(actor.HasPendingLocalApplication, Is.False);
            var second = Answer(group.Sessions[1], 1, "second-answer");
            Assert.That(actor.TryCreateLocalProposal(second, out var secondProposal, out _), Is.True);
            Assert.That(secondProposal.ObservedGlobalSequence, Is.EqualTo(1));
            var admitted2 = group.Host.AdmitProposal(1, secondProposal); Assert.That(admitted2.Status, Is.EqualTo(OperationAdmissionStatus.Accepted));
            var ordered2 = admitted2.OrderedCommand; actor.ReceiveOrdered(ordered2); Assert.That(actor.Pump(1), Is.EqualTo(1));
            Assert.That(actor.ReadBoundary(1), Is.Null); Assert.That(actor.ReadBoundary(2), Is.Null);
            Assert.That(group.Host.LastAppliedSequence, Is.EqualTo(0));
            var end = new GameCommand { CommandId = "finish-action", Kind = GameCommandKind.EndAction, PlayerId = 1 };
            Assert.That(actor.TryCreateLocalProposal(end, out var endProposal, out _), Is.True);
            var ordered3 = group.Host.AdmitProposal(1, endProposal).OrderedCommand;
            actor.ReceiveOrdered(ordered3); Assert.That(actor.Pump(1), Is.EqualTo(1));
            Assert.That(actor.ReadBoundary(3), Is.Not.Null);
            Assert.That(actor.ReadWindowPermissions().Any(w => w.Window.AuthorizedPlayerIds.Contains(2) && w.IsPermitted), Is.False);
            Assert.That(group.Host.AdmitProposal(1, firstProposal).Status, Is.EqualTo(OperationAdmissionStatus.Duplicate));
            Assert.That(group.Host.LastSequencedSequence, Is.EqualTo(3));
            int missing = 0; group.Coordinators[2].MissingRangeRequested += _ => missing++;
            Assert.That(group.Coordinators[2].ReceiveOrdered(ordered3).Status, Is.EqualTo(OperationReplicaStatus.Buffered));
            Assert.That(group.Coordinators[2].Pump(8), Is.EqualTo(0)); Assert.That(missing, Is.EqualTo(1));
            foreach (var command in group.Host.ReadMissingRange(1, 4).Commands) group.Coordinators[2].ReceiveOrdered(command);
            Assert.That(group.Coordinators[2].Pump(1), Is.EqualTo(1)); Assert.That(group.Coordinators[2].LastAppliedSequence, Is.EqualTo(1));
            Assert.That(group.Coordinators[2].Pump(8), Is.EqualTo(2));
            Assert.That(group.Host.Pump(1), Is.EqualTo(1)); Assert.That(group.Host.LastAppliedSequence, Is.EqualTo(1));
            Assert.That(group.Host.Pump(8), Is.EqualTo(2));
            var closed = group.Host.ReadBoundary(3).Windows.Single(w => w.IsClosed);
            Assert.That(closed.FinalCommandSequence, Is.EqualTo(3)); Assert.That(closed.CommandCount, Is.EqualTo(3));
            group.Seal(3, false);
            Assert.That(group.Coordinators[2].ReadWindowPermissions().Any(w => w.CanSubmitForLocalPlayer), Is.False);
            group.Seal(3, true);
            Assert.That(group.Coordinators[2].ReadWindowPermissions().Any(w => w.CanSubmitForLocalPlayer), Is.True);
            Assert.That(group.Sessions.Select(s => RuleStateDigestService.Compute(s.State)).Distinct().Count(), Is.EqualTo(1));
            var saved = actor.ReadBoundary(3).FactDigest;
            Assert.That(actor.ReceiveBoundary(group.Host.ReadBoundary(3)), Is.True); Assert.That(actor.ReadBoundary(3).FactDigest, Is.EqualTo(saved));
            Assert.That(actor.ReceiveValidationPrefix(group.Prefixes.Last()), Is.True); Assert.That(actor.ValidatedThroughSequence, Is.EqualTo(3));
            var currentDigest = RuleStateDigestService.Compute(group.Sessions[1].State);
            Assert.That(actor.ReceiveBoundary(group.Host.ReadBoundary(0)), Is.True);
            Assert.That(RuleStateDigestService.Compute(group.Sessions[1].State), Is.EqualTo(currentDigest));
            Assert.That(actor.ReadBoundary(0).RuleStateDigest, Is.Not.EqualTo(currentDigest));
            Assert.That(group.Coordinators.All(c => !c.IsStopped), Is.True);
        }

        [Test]
        public void ConcurrentEnterpriseRequestsRefreshBeforeSequencingAndCompletedActorLosesEligibility()
        {
            var fixture = new FormalFixture(true); var group = fixture.Group(); group.Seal(0, true);
            var first = Answer(group.Sessions[1], 1, "enterprise-one"); var competing = Answer(group.Sessions[2], 2, "enterprise-old-choice");
            Assert.That(group.Coordinators[1].TryCreateLocalProposal(first, out var proposal1, out _), Is.True);
            Assert.That(group.Coordinators[2].TryCreateLocalProposal(competing, out var stale, out _), Is.True);
            string original = JsonConvert.SerializeObject(stale);
            var one = group.Host.AdmitProposal(1, proposal1); var rejected = group.Host.AdmitProposal(2, stale);
            Assert.That(one.Status, Is.EqualTo(OperationAdmissionStatus.Accepted)); Assert.That(rejected.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(rejected.RequiresRefresh, Is.True); Assert.That(rejected.OrderedCommand, Is.Null); Assert.That(rejected.ExpectedClientSubmissionSequence, Is.EqualTo(1));
            Assert.That(group.Host.LastSequencedSequence, Is.EqualTo(1)); Assert.That(JsonConvert.SerializeObject(stale), Is.EqualTo(original));
            group.Coordinators[2].ReceiveProposalResult(rejected); Assert.That(group.Coordinators[2].HasPendingLocalApplication, Is.False);
            foreach (var coordinator in group.Coordinators.Skip(1)) { coordinator.ReceiveOrdered(one.OrderedCommand); Assert.That(coordinator.Pump(1), Is.EqualTo(1)); }
            Assert.That(group.Host.LastAppliedSequence, Is.EqualTo(0));
            var completed = group.Coordinators[1].ReadWindowPermissions().Single(w => w.Window.OperationWindowId == proposal1.OperationWindowId);
            Assert.That(completed.Window.AuthorizedPlayerIds.Contains(1), Is.True); Assert.That(completed.AvailablePlayerIds.Contains(1), Is.False);
            Assert.That(completed.AvailablePlayerIds.Contains(2), Is.True); Assert.That(completed.IsPermitted, Is.True);
            var fresh = Answer(group.Sessions[2], 2, "enterprise-fresh-choice");
            Assert.That(group.Coordinators[2].TryCreateLocalProposal(fresh, out var freshProposal, out _), Is.True);
            Assert.That(freshProposal.ClientSubmissionSequence, Is.EqualTo(1)); Assert.That(freshProposal.Command.CommandId, Is.Not.EqualTo(stale.Command.CommandId));
            var two = group.Host.AdmitProposal(2, freshProposal); Assert.That(two.Status, Is.EqualTo(OperationAdmissionStatus.Accepted));
            Assert.That(group.Host.AdmitProposal(1, proposal1).Status, Is.EqualTo(OperationAdmissionStatus.Duplicate));
            Assert.That(group.Host.Pump(8), Is.EqualTo(2));
            foreach (var coordinator in group.Coordinators.Skip(1)) { coordinator.ReceiveOrdered(two.OrderedCommand); Assert.That(coordinator.Pump(1), Is.EqualTo(1)); }
            Assert.That(group.Host.ReadMissingRange(1, 4).Commands.Count, Is.EqualTo(2));
            Assert.That(group.Sessions.Select(s => RuleStateDigestService.Compute(s.State)).Distinct().Count(), Is.EqualTo(1));
            Assert.That(group.Coordinators.All(c => !c.IsStopped), Is.True);
        }

        [Test]
        public void RequiredChildBusinessFailureAndConflictingLogStopWithoutInitialRollback()
        {
            var group = ActionGroup(); group.Seal(0, true);
            Assert.That(group.Sessions[1].Submit(new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1, CommandId = "required-child-check" }).Succeeded, Is.False);
            var bad = Answer(group.Sessions[1], 1, "invalid-actual-answer"); bad.Parameters["answerValue"] = "not-a-boolean";
            Assert.That(group.Coordinators[1].TryCreateLocalProposal(bad, out var proposal, out _), Is.True);
            var ordered = group.Host.AdmitProposal(1, proposal).OrderedCommand;
            var foreign = JsonConvert.DeserializeObject<OrderedOperationCommandDto>(JsonConvert.SerializeObject(ordered)); foreign.SimulationEpoch++;
            Assert.That(group.Coordinators[2].ReceiveOrdered(foreign).Status, Is.EqualTo(OperationReplicaStatus.Rejected)); Assert.That(group.Coordinators[2].IsStopped, Is.False);
            int failedEvents = 0; CommandResult actual = null;
            group.Host.SimulationCommandApplied += value => { actual = value.RuleResult; failedEvents++; };
            var before = RuleStateDigestService.Compute(group.Sessions[0].State);
            Assert.That(group.Host.Pump(8), Is.EqualTo(0)); Assert.That(group.Host.IsStopped, Is.True); Assert.That(group.Host.RequiresRecovery, Is.True);
            Assert.That(failedEvents, Is.EqualTo(1)); Assert.That(actual.Succeeded, Is.False); Assert.That(group.Host.LastFault.LocalRuleResult, Is.SameAs(actual));
            Assert.That(group.Host.LastFault.Command.CommandId, Is.EqualTo(bad.CommandId)); Assert.That(group.Host.LastFault.ErrorCode, Is.EqualTo(actual.Validation.ErrorCode));
            group.Host.LastFault.Command.CommandId = "caller-mutated-copy";
            Assert.That(group.Host.LastFault.Command.CommandId, Is.EqualTo(bad.CommandId));
            Assert.That(group.Host.LastAppliedSequence, Is.EqualTo(0)); Assert.That(group.Host.ValidatedThroughSequence, Is.EqualTo(0));
            Assert.That(group.Host.ReadPendingTail().Single().Command.CommandId, Is.EqualTo(bad.CommandId));
            Assert.That(group.Host.ReadMissingRange(1, 4).Commands.Single().OrderedCommandDigest, Is.EqualTo(ordered.OrderedCommandDigest));
            Assert.That(RuleStateDigestService.Compute(group.Sessions[0].State), Is.EqualTo(before));
            Assert.That(group.Host.Pump(8), Is.EqualTo(0)); Assert.That(failedEvents, Is.EqualTo(1));
            Assert.That(group.Host.ReceiveOrdered(ordered).Status, Is.EqualTo(OperationReplicaStatus.Stopped));
            var conflict = JsonConvert.DeserializeObject<OrderedOperationCommandDto>(JsonConvert.SerializeObject(ordered)); conflict.Command.CommandId = "conflicting-original";
            conflict.ProposalDigest = OperationCommandCanonicalizer.CanonicalProposalDigest(conflict); conflict.OrderedCommandDigest = OperationCommandCanonicalizer.CanonicalOrderedCommandDigest(conflict);
            group.Coordinators[2].ReceiveOrdered(ordered);
            Assert.That(group.Coordinators[2].ReceiveOrdered(conflict).Status, Is.EqualTo(OperationReplicaStatus.Stopped));
            Assert.That(group.Coordinators[2].ReadPendingTail().Single().Command.CommandId, Is.EqualTo(bad.CommandId));
            var readiness = ActionGroup(); readiness.Seal(0, false);
            var original = readiness.Coordinators[2].ReadBoundary(0);
            original.RuleStateDigest = new string('0', 64); original.FactDigest = OperationCommandCanonicalizer.CanonicalBoundaryDigest(original);
            Assert.That(readiness.Host.ReceiveWindowReady(2, new SimulationWindowReadyDto { GameId = original.GameId, SimulationEpoch = 1, MembershipVersion = 1,
                PlayerId = 2, ReplicaId = "player.2", Boundary = original }), Is.False);
            Assert.That(readiness.Host.RequiresRecovery, Is.True);
            Assert.That(readiness.Host.ReadWindowPermissions().Any(window => window.IsPermitted), Is.False);
        }

        private static GameCommand Answer(GameSession session, int player, string commandId)
        {
            var request = session.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.AnsweringPlayerId == player)
                .OrderByDescending(r => r.InteractionTypeId == "test.simulation.answer").First();
            var command = new GameCommand { CommandId = commandId, PlayerId = player, Kind = GameCommandKind.AnswerInteraction };
            command.Parameters["interactionId"] = request.GetStableInteractionId(); command.Parameters["expectedRevision"] = (session.State.EffectRuntime.StateRevision).ToString(CultureInfo.InvariantCulture);
            if (request.AnswerSchema == "boolean_accept") command.Parameters["answerValue"] = "true";
            else command.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            return command;
        }

        private static Group ActionGroup()
        {
            var state = new GameState { GameId = "same-actor-match", Phase = GamePhase.ActionRound1, Round = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                UseSeatTurnOrder = true, LaunchPlayerOrder = new List<int> { 1, 2, 3 }, MapId = StaticMapDefinitions.FourPlayerMapId };
            for (int player = 1; player <= 3; player++) state.Players.Add(new PlayerState { PlayerId = player, CityLocationId = "B-01" });
            var registry = new EffectRegistry(); var round = new RoundExecutionService(registry);
            Assert.That(round.PrepareActionWindowForSmoke(state, 1).IsValid, Is.True);
            round.MarkMainActionComplete(state, 1);
            registry.Register(new EffectRegistration("test.simulation.required_input", ctx => {
                if (ctx.GetLatestInteractionAnswer() != null && ctx.Node.FlowStage == "second") return EffectStepResult.Completed();
                return EffectStepResult.Continue(ctx.Node.FlowStage == "" ? "first" : "second").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "test.simulation.answer", AnsweringPlayerId = 1, AnswerSchema = "boolean_accept" });
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = round.CreateCommandExecutor(state);
            Assert.That(engine.TryCreatePlayerActionEffect(1, EffectSpec.Create("test.simulation.required_input", playerId: 1), "1", "test.simulation", out _), Is.True);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
#if YC_SIMULATION_OFFLINE
            Console.WriteLine(JsonConvert.SerializeObject(new { FixtureRevision = state.EffectRuntime.StateRevision, Requests = state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open").Select(r => new { r.InteractionTypeId, r.StateRevision, r.AnsweringPlayerId }) }));
#endif
            var sessions = Enumerable.Range(0, 3).Select(_ => new GameSession(GameStateCloneService.DeepClone(state))).ToArray();
            foreach (var session in sessions) {
                session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
                session.RegisterHandler(new EndActionCommandHandler(new RoundAdvanceService(round), new FinalScoringService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap())), registry, round)); }
            return new Group(sessions);
        }

        private sealed class Group
        {
            internal readonly GameSession[] Sessions;
            internal readonly DeterministicSimulationCoordinator[] Coordinators;
            internal readonly List<CommandValidationPrefixDto> Prefixes = new List<CommandValidationPrefixDto>();
            internal DeterministicSimulationCoordinator Host => Coordinators[0];
            internal int Index(int player) => player == 3 ? 0 : player;
            internal Group(GameSession[] sessions)
            {
                Sessions = sessions;
                Coordinators = Enumerable.Range(0, 3).Select(i => new DeterministicSimulationCoordinator(sessions[i],
                    DeterministicSimulationSessionOptions.CreateInitial(i == 0, i == 0 ? 3 : i, sessions[i], new[] { 1, 2, 3 }))).ToArray();
                foreach (var coordinator in Coordinators) coordinator.Begin();
                Host.WindowPermitProduced += permit => { foreach (var client in Coordinators.Skip(1)) client.ReceiveWindowPermit(permit); };
                Host.CommandValidationPrefixAdvanced += prefix => Prefixes.Add(prefix);
            }
            internal void Seal(long fence, bool all)
            {
                var boundary = Host.ReadBoundary(fence); Assert.That(boundary, Is.Not.Null);
                for (int i = 1; i <= (all ? 2 : 1); i++)
                {
                    Assert.That(Coordinators[i].ReceiveBoundary(boundary), Is.True);
                    Assert.That(Host.ReceiveWindowReady(i, new SimulationWindowReadyDto { GameId = boundary.GameId, SimulationEpoch = 1, MembershipVersion = 1,
                        PlayerId = i, ReplicaId = DeterministicSimulationSessionOptions.ReplicaIdForPlayer(i), Boundary = Coordinators[i].ReadBoundary(fence) }), Is.True);
                }
            }
        }

        private sealed class Built { internal GameSession Session; internal EffectRegistry Registry; }
        private sealed class FormalFixture
        {
            private readonly string gameId = "formal-simulation-match";
            private readonly GameRuleConfiguration configuration;
            private readonly List<PlayerSeat> seats = Enumerable.Range(1, 3).Select(i => new PlayerSeat { PlayerId = i, PlayerName = "玩家" + i }).ToList();
            private readonly List<int> order = new List<int> { 1, 2, 3 };
            private readonly GameplayRandomState random;
            internal readonly Built Host;
            internal int Installations;
            internal FormalFixture(bool enterprise, bool uniqueSeatColors = false)
            {
                if (uniqueSeatColors) foreach (var seat in seats) seat.Color = RoomConfigurationService.GetSeatColor(seat.PlayerId - 1);
                configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
                    EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
                random = GameplayRandomRuntime.Create(gameId);
                Host = Build(LaunchMode.Host, 3, false); configuration.ContentPackHash = Host.Session.State.ContentPackHash;
            }
            internal Group Group()
            {
                var archive = HostRecoveryService.SerializeArchive(Host.Session.CreateHostArchive());
                var identity = new SimulationCheckpointEnvelope { GameId = gameId, SimulationEpoch = 1, MembershipVersion = 1, CheckpointId = "formal.initial", ContentPackHash = configuration.ContentPackHash };
                var transfer = SimulationCheckpointTransfer.Encode(identity, archive);
                var sessions = new[] { Host.Session, Build(LaunchMode.Client, 1, true).Session, Build(LaunchMode.Client, 2, true).Session };
                for (int i = 1; i <= 2; i++)
                {
                    var built = Build(LaunchMode.Client, i, true); sessions[i] = built.Session;
                    var channelType = Type.GetType("YC.Infrastructure.Multiplayer.SimulationCheckpointChannel, Assembly-CSharp", true);
                    var channel = Activator.CreateInstance(channelType, identity, new InitialSimulationCheckpointInstaller(built.Session, built.Registry, GameStateViewer.Player(i)), null, new[] { 1, 2, 3 });
                    channelType.GetMethod("BindClientConnection").Invoke(channel, new object[] { "formal-peer" });
                    object result = null;
                    foreach (var part in transfer.Split()) result = channelType.GetMethod("ReceivePart").Invoke(channel, new object[] { "formal-peer", part.Envelope, part.Offset, part.Payload });
                    Assert.That(result.GetType().GetProperty("Status").GetValue(result).ToString(), Is.EqualTo("Installed")); Installations++;
                    Assert.That(RuleStateDigestService.Compute(built.Session.State), Is.EqualTo(RuleStateDigestService.Compute(Host.Session.State)));
                }
                return new Group(sessions);
            }
            internal Built Build(LaunchMode mode, int localPlayer, bool defer)
            {
                var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var property = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static); var previous = property.GetValue(null); var setter = property.GetSetMethod(true);
                GameObject host = null;
                try
                {
                    setter.Invoke(null, new object[] { null }); host = new GameObject("SimulationSessionCheck"); var context = host.AddComponent(contextType);
                    contextType.GetMethods().Single(method => method.Name == "Configure" && method.GetParameters().Length == 9).Invoke(context,
                        new object[] { mode, localPlayer, "formal-simulation-room", seats, configuration, order, gameId, 42, random.Clone() });
                    var method = defer ? bootstrapper.GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { contextType, typeof(bool), typeof(bool), typeof(bool) }, null) : bootstrapper.GetMethod("Build", BindingFlags.Public | BindingFlags.Static);
                    var result = method.Invoke(null, defer ? new object[] { context, false, false, true } : new object[] { context, false, false });
                    return new Built { Session = (GameSession)result.GetType().GetField("Session").GetValue(result), Registry = (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result) };
                }
                finally { if (host != null) UnityEngine.Object.DestroyImmediate(host); setter.Invoke(null, new[] { previous }); }
            }
        }
    }
}
