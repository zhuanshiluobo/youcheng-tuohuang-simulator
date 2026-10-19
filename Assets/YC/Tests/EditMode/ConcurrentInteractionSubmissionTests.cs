using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    // 原提交消费者的隔离生命周期检查，不代替正常菜单/可见对局验收；本批只写不运行。
    public sealed class ConcurrentInteractionSubmissionTests
    {
        [TestCase(OperationAdmissionStatus.PendingValidation)]
        [TestCase(OperationAdmissionStatus.Accepted)]
        [TestCase(OperationAdmissionStatus.Duplicate)]
        public void NonterminalAdmissionDoesNotResolveOriginalPending(OperationAdmissionStatus status)
        {
            using var fixture = new ConsumerFixture();
            fixture.Deliver(status);
            Assert.That(fixture.PendingCount, Is.EqualTo(1));
            Assert.That(fixture.Resolutions, Is.Zero);
            Assert.That(fixture.Settlements, Is.Zero);
            fixture.Deliver(OperationAdmissionStatus.Rejected);
            Assert.That(fixture.PendingCount, Is.Zero);
            Assert.That(fixture.Resolutions, Is.EqualTo(1));
            Assert.That(fixture.Settlements, Is.EqualTo(1));
            Assert.That(fixture.RequiresFreshDecision, Is.True);
            fixture.Deliver(OperationAdmissionStatus.Rejected);
            Assert.That(fixture.Resolutions, Is.EqualTo(1), "重复终态不得二次释放原回执");
        }

        [Test]
        public void ProtocolFailureRetiresExactPendingWithoutPretendingApplied()
        {
            using var fixture = new ConsumerFixture();
            fixture.Fault();
            Assert.That(fixture.PendingCount, Is.Zero);
            Assert.That(fixture.Resolutions, Is.EqualTo(1));
            Assert.That(fixture.Accepted, Is.False);
            fixture.Fault();
            Assert.That(fixture.Resolutions, Is.EqualTo(1));
        }

        [Test]
        public void FailedRecoveryRetiresPendingEvenWhenUiWasAlreadyFrozen()
        {
            using var fixture = new ConsumerFixture();
            fixture.FreezeForRecovery(); fixture.Fault(2);
            Assert.That(fixture.PendingCount, Is.Zero);
            Assert.That(fixture.Resolutions, Is.EqualTo(1));
            Assert.That(fixture.Accepted, Is.False);
        }

        [TestCase(SimulationCheckpointEnvelope.InitialSimulation)]
        [TestCase(SimulationCheckpointEnvelope.SavedSimulation)]
        [TestCase(SimulationCheckpointEnvelope.ConfirmedSimulationRecovery)]
        public void RequestCapabilityIsExplicitAndCannotBeInferredFromContentHash(string purpose)
        {
            var legacy = new SimulationCheckpointEnvelope { GameId = "能力握手局", ContentPackHash = "相同内容",
                Purpose = purpose, SimulationEpoch = purpose == SimulationCheckpointEnvelope.InitialSimulation ? 1 : 2,
                SourceSimulationEpoch = purpose == SimulationCheckpointEnvelope.InitialSimulation ? 0 : 1,
                ProtocolVersion = purpose == SimulationCheckpointEnvelope.InitialSimulation ? SimulationCheckpointEnvelope.CurrentProtocolVersion :
                    purpose == SimulationCheckpointEnvelope.SavedSimulation ? SimulationCheckpointEnvelope.SavedProtocolVersion : SimulationCheckpointEnvelope.RecoveryProtocolVersion,
                ArchiveDigestVersion = purpose == SimulationCheckpointEnvelope.ConfirmedSimulationRecovery ? SimulationCheckpointEnvelope.RecoveryDigestVersion : SimulationCheckpointEnvelope.Utf8ArchiveDigestVersion,
                MembershipVersion = 1, CheckpointId = "capability" };
            var current = legacy.Snapshot(); current.RequestContractVersion = OperationCommandProtocol.RequestContractVersion;
            Assert.That(legacy.HasValidIdentity(), Is.True);
            Assert.That(current.HasValidIdentity(), Is.True);
            Assert.That(SimulationCheckpointEnvelope.SameIdentity(legacy, current), Is.False);
            current.RequestContractVersion++;
            Assert.That(current.HasValidIdentity(), Is.False);
        }

        [Test]
        public void PendingIntentKeepsCommandIdentityAcrossRevisionButRejectsChangedOwner()
        {
            var request = Request(14);
            var command = YC.Application.Interactions.EffectInteractionCommands.Answer(request, 1, new[] { "candidate" });
            var intent = new InteractionCommandIntent();
            Assert.That(intent.Begin("原局", request, command, false), Is.True);
            Assert.That(intent.IsPendingForRequest("原局", Request(18)), Is.True);
            var changed = Request(18); changed.OwnerEffectId = "其他节点";
            Assert.That(intent.IsPendingForRequest("原局", changed), Is.False);
            Assert.That(intent.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(command.Parameters["expectedRevision"], Is.EqualTo("14"));
            Assert.That(intent.Resolve("原局", command.CommandId, false, CommandErrorCode.StateRevisionMismatch, true), Is.True);
            Assert.That(intent.TryRebuild("原局", Request(18), string.Empty, out _), Is.False);
        }

        private static InteractionRequestProjection Request(int revision) => new InteractionRequestProjection {
            InteractionId = "原请求", SourceNodeId = "原节点", OwnerEffectId = "原节点", AnsweringPlayerId = 1,
            InteractionTypeId = "setup.enterprise.choose", PromptKey = "setup.enterprise.choose", Visibility = "owner",
            VisibleToViewer = true, Status = "open", CandidateSetId = "原候选集", CandidateSetVersion = 1,
            CandidateIds = new List<string> { "candidate" }, AnswerSchema = "candidate_ids",
            MinSelections = 1, MaxSelections = 1, StateRevision = revision };

        private sealed class ConsumerFixture : IDisposable
        {
            private readonly GameObject root;
            private readonly object controller, transport;
            private readonly Type controllerType, transportType;
            private readonly IDictionary owners, pending;
            private readonly string game = "待准入消费局";
            private readonly GameCommand command = new GameCommand { CommandId = "原命令", PlayerId = 1, Kind = GameCommandKind.AnswerInteraction };
            internal int Resolutions, Settlements;
            internal bool Accepted, RequiresFreshDecision;
            internal int PendingCount => pending.Count;

            internal ConsumerFixture()
            {
                transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
                Assert.That(transportType.GetProperty("Instance").GetValue(null), Is.Null, "隔离夹具不得接管既有运输实例");
                root = new GameObject("待准入隔离消费者"); root.SetActive(false);
                try
                {
                    var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                    var context = root.AddComponent(contextType);
                    contextType.GetField("Mode").SetValue(context, LaunchMode.Host);
                    contextType.GetField("GameId").SetValue(context, game);
                    contextType.GetField("LocalPlayerId").SetValue(context, 1);
                    var state = new GameState { GameId = game, ContentPackHash = "消费内容", Players = { new PlayerState { PlayerId = 1 } } };
                    var session = new GameSession(state);
                    var coordinator = new DeterministicSimulationCoordinator(session,
                        DeterministicSimulationSessionOptions.CreateInitial(true, 1, session, new[] { 1 }));
                    transport = root.AddComponent(transportType);
                    Field(transportType, "simulationCoordinator").SetValue(transport, coordinator);
                    controllerType = Type.GetType("YC.Presentation.CommandSubmissionController, Assembly-CSharp", true);
                    object consumer = null;
                    controller = Activator.CreateInstance(controllerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { session, context, 1, null, (Action)(() => { }), (Action<string>)(_ => { }),
                            (Action<string>)(_ => Settlements++), null,
                            (Action<string, string, bool, CommandErrorCode>)((receivedGame, id, accepted, error) => {
                                Assert.That(receivedGame, Is.EqualTo(game)); Assert.That(id, Is.EqualTo(command.CommandId));
                                Resolutions++; Accepted = accepted;
                                RequiresFreshDecision = (bool)controllerType.GetProperty("RequiresExplicitRefreshForCurrentResult").GetValue(consumer);
                            }) }, null);
                    consumer = controller;
                    Set("usesDeterministicSimulation", true); Set("simulationTransport", transport); Set("simulationCoordinator", coordinator);
                    Set("simulationLaunchMode", LaunchMode.Host); Set("simulationGameId", game); Set("simulationEpoch", 1L); Set("simulationMembershipVersion", 1);
                    owners = (IDictionary)controllerType.GetField("SimulationTransportOwners", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    owners.Add(transport, controller);
                    ((SubmittedInteractionCommands)Field(controllerType, "submittedCommands").GetValue(controller)).Record(game, command);
                    pending = (IDictionary)Field(controllerType, "unresolvedSimulationSubmissions").GetValue(controller);
                    pending.Add(command.CommandId, new KeyValuePair<string, int>(game, 1));
                }
                catch
                {
                    if (owners != null && transport != null) owners.Remove(transport);
                    (controller as IDisposable)?.Dispose();
                    UnityEngine.Object.DestroyImmediate(root);
                    throw;
                }
            }
            private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            private void Set(string name, object value) => Field(controllerType, name).SetValue(controller, value);
            internal void Deliver(OperationAdmissionStatus status) => controllerType.GetMethod("OnSimulationProposalRejected", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { new SimulationProposalResultDto { GameId = game, SimulationEpoch = 1, MembershipVersion = 1,
                    Proposal = new OperationCommandProposalDto { GameId = game, SimulationEpoch = 1, MembershipVersion = 1, Command = GameCommandDto.FromCommand(command) },
                    Status = status, ErrorCode = CommandErrorCode.StateRevisionMismatch } });
            internal void FreezeForRecovery() { Set("simulationStopped", true); Set("recoveryTargetEpoch", 2L); }
            internal void Fault(long epoch = 1) => controllerType.GetMethod("OnSimulationFaulted", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { new SimulationProtocolFaultDto { GameId = game, SimulationEpoch = epoch, MembershipVersion = 1,
                    FaultCode = "execution_stopped", ErrorCode = CommandErrorCode.UnknownCommand, RequiresRecovery = true } });
            public void Dispose()
            {
                owners.Remove(transport); ((IDisposable)controller).Dispose();
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
