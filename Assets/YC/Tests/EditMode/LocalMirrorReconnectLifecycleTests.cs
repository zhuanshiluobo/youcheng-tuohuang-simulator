using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
#if YC_SIMULATION_OFFLINE
using Assert = YC.SimulationOffline.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class LocalMirrorReconnectLifecycleTests
    {
        [Test]
        public void ConnectionStatusDoesNotTreatStaleRoomSnapshotAsLiveAfterLocalDisconnect()
        {
            var players = Enumerable.Range(1, 4).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "玩家" + id,
                SteamId = (ulong)(100 + id), LobbyMemberPresent = true, TransportConnected = true, IdentityVerified = true }).ToList();
            var room = new RoomState { GameId = "connection-fixture", LocalPlayerId = 2, HostPlayerId = 1, HasStarted = true };
            for (int i = 0; i < players.Count; i++)
            { room.SeatSlots[i].Status = "occupied"; room.SeatSlots[i].Occupant = players[i].Clone(); }
            room.SeatSlots[2].Occupant.TransportConnected = false;
            room.SeatSlots[3].Occupant.IdentityVerified = false;
            var online = ConnectionStatusProjection.Read(LaunchMode.Client, 2, room.GameId, players, room, true, false, false);
            Assert.That(online.Select(item => item.Status), Is.EqualTo(new[] { PlayerConnectionStatus.Connected, PlayerConnectionStatus.Connected,
                PlayerConnectionStatus.Disconnected, PlayerConnectionStatus.Verifying }));
            var offline = ConnectionStatusProjection.Read(LaunchMode.Client, 2, room.GameId, players, room, false, true, false);
            Assert.That(offline.Select(item => item.Status), Is.EqualTo(new[] { PlayerConnectionStatus.Unknown, PlayerConnectionStatus.Reconnecting,
                PlayerConnectionStatus.Unknown, PlayerConnectionStatus.Unknown }));
            var synchronizing = ConnectionStatusProjection.Read(LaunchMode.Client, 2, room.GameId, players, room, true, false, true);
            Assert.That(synchronizing[1].Status, Is.EqualTo(PlayerConnectionStatus.Synchronizing));
            var wrongGame = ConnectionStatusProjection.Read(LaunchMode.Client, 2, "different-game", players, room, true, false, false);
            Assert.That(wrongGame.Where(item => item.PlayerId != 2).All(item => item.Status == PlayerConnectionStatus.Unknown), Is.True);
            room.SeatSlots[0].Occupant.SteamId++;
            var wrongIdentity = ConnectionStatusProjection.Read(LaunchMode.Client, 2, room.GameId, players, room, true, false, false);
            Assert.That(wrongIdentity[0].Status, Is.EqualTo(PlayerConnectionStatus.Unknown));
            Assert.That(ConnectionStatusProjection.Read(LaunchMode.Local, 1, room.GameId, players, room, true, false, false), Is.Empty);
        }

        [Test]
        public void SavedCheckpointHostHandlerWaitsForProgressBeforeSendingMore()
        {
            Assert.That(NetworkIsActive(), Is.False);
            var transportType = Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport");
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", Statics).Invoke(null, null);
            var connectionType = Target("Mirror.NetworkConnectionToClient");
            var requestType = Target("YC.Infrastructure.Multiplayer.SimulationCheckpointRequestMessage");
            var installedType = Target("YC.Infrastructure.Multiplayer.SimulationCheckpointInstalledMessage");
            var activeTransport = Target("Mirror.Transport").GetField("active", Statics);
            var previousTransport = activeTransport.GetValue(null);
            var connections = (IDictionary)Read(Target("Mirror.NetworkServer"), null, "connections");
            var root = new GameObject("SavedCheckpointBoundedHostFixture"); root.SetActive(false);
            var transport = root.AddComponent(transportType);
            var connection = Activator.CreateInstance(connectionType, 117, "localhost");
            try
            {
                activeTransport.SetValue(null, root.AddComponent(Target("Mirror.TelepathyTransport")));
                var identity = SimulationCheckpointTransferTests.InitialIdentity;
                identity.Purpose = SimulationCheckpointEnvelope.SavedSimulation;
                identity.ProtocolVersion = SimulationCheckpointEnvelope.SavedProtocolVersion;
                identity.SourceSimulationEpoch = 1; identity.SimulationEpoch = 2; identity.CheckpointId = "saved-fixture";
                var session = SimulationCheckpointTransferTests.InitialHostSession;
                var options = DeterministicSimulationSessionOptions.CreateInitial(true, 1, session, new[] { 1, 2, 3 });
                options.SimulationEpoch = 2;
                var channel = Activator.CreateInstance(Target("YC.Infrastructure.Multiplayer.SimulationCheckpointChannel"), identity, null,
                    (Func<string>)(() => SimulationCheckpointTransferTests.LargeTransportFixture()), new[] { 1, 2, 3 });
                Set(transport, "session", session); Set(transport, "mode", LaunchMode.Host); Set(transport, "initialized", true);
                Set(transport, "simulationOptions", options); Set(transport, "checkpointChannel", channel);
                var bindings = (SteamIdentityBindingRegistry)Get(transport, "bindings");
                bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(2, 222) });
                Assert.That(bindings.TryBindConnection(117, 222, out var player), Is.True); Assert.That(player, Is.EqualTo(2));
                connections.Add(117, connection);
                var request = Activator.CreateInstance(requestType);
                requestType.GetField("PlayerId").SetValue(request, 2);
                requestType.GetField("ConnectionToken").SetValue(request, "saved-peer-token");
                var outbound = (IDictionary)Get(transport, "checkpointOutbound");

                // 旧客户端未声明窗口能力时，只能得到版本不兼容提示，不能触发无限分片。
                requestType.GetField("EnvelopeJson").SetValue(request, JsonConvert.SerializeObject(identity));
                Call(transport, "OnCheckpointRequest", connection, request);
                Assert.That(outbound.Count, Is.Zero);

                // 旧版分片能力不足以支持安全交接，也不能加入当前对局。
                identity.TransportFlowVersion = SimulationCheckpointEnvelope.BoundedTransferVersion - 1;
                requestType.GetField("EnvelopeJson").SetValue(request, JsonConvert.SerializeObject(identity));
                Call(transport, "OnCheckpointRequest", connection, request);
                Assert.That(outbound.Count, Is.Zero);

                identity.TransportFlowVersion = SimulationCheckpointEnvelope.BoundedTransferVersion;
                requestType.GetField("EnvelopeJson").SetValue(request, JsonConvert.SerializeObject(identity));
                Call(transport, "OnCheckpointRequest", connection, request);
                Assert.That(outbound.Count, Is.EqualTo(1));
                var pending = outbound[117];
                var window = (SimulationCheckpointSendWindow)Get(pending, "Window");
                var envelope = (SimulationCheckpointEnvelope)Get(pending, "Envelope");
                Assert.That(window.TotalBytes, Is.GreaterThan(524288));
                Assert.That(window.SentBytes, Is.EqualTo(SimulationCheckpointSendWindow.MaximumInFlightBytes));
                Call(transport, "OnCheckpointRequest", connection, request);
                Assert.That(window.SentBytes, Is.EqualTo(SimulationCheckpointSendWindow.MaximumInFlightBytes));

                var installed = Activator.CreateInstance(installedType);
                installedType.GetField("PlayerId").SetValue(installed, 2);
                installedType.GetField("ConnectionToken").SetValue(installed, "saved-peer-token");
                installedType.GetField("EnvelopeJson").SetValue(installed, JsonConvert.SerializeObject(envelope));
                Call(transport, "OnCheckpointInstalled", connection, installed);
                Assert.That(Call(channel, "IsPeerInstalled", 2), Is.False, "不能提前确认尚未发送完的存档。");

                while (window.SentBytes < window.TotalBytes)
                {
                    identity.TransportReceivedBytes = window.SentBytes;
                    requestType.GetField("EnvelopeJson").SetValue(request, JsonConvert.SerializeObject(identity));
                    Call(transport, "OnCheckpointRequest", connection, request);
                    Assert.That(window.SentBytes - window.AcknowledgedBytes,
                        Is.LessThanOrEqualTo(SimulationCheckpointSendWindow.MaximumInFlightBytes));
                }
                Call(transport, "OnCheckpointInstalled", connection, installed);
                Assert.That(Call(channel, "IsPeerInstalled", 2), Is.True);
                Assert.That(outbound.Count, Is.Zero);
            }
            finally
            {
                connections.Remove(117); Call(transport, "Shutdown");
                activeTransport.SetValue(null, previousTransport); UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ConfirmedRecoveryHostUsesAcknowledgedWindowAndWaitsForPreparedPeer()
        {
            Assert.That(NetworkIsActive(), Is.False);
            var transportType = Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport");
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", Statics).Invoke(null, null);
            var activeTransport = Target("Mirror.Transport").GetField("active", Statics);
            var previousTransport = activeTransport.GetValue(null);
            var connections = (IDictionary)Read(Target("Mirror.NetworkServer"), null, "connections");
            var root = new GameObject("ConfirmedRecoveryBoundedHostFixture"); root.SetActive(false);
            var transport = root.AddComponent(transportType);
            var connection = Activator.CreateInstance(Target("Mirror.NetworkConnectionToClient"), 118, "localhost");
            try
            {
                activeTransport.SetValue(null, root.AddComponent(Target("Mirror.TelepathyTransport")));
                var session = SimulationCheckpointTransferTests.InitialHostSession;
                var options = DeterministicSimulationSessionOptions.CreateInitial(true, 1, session, new[] { 1, 2, 3 });
                var coordinator = new DeterministicSimulationCoordinator(session, options);
                Set(transport, "session", session); Set(transport, "mode", LaunchMode.Host); Set(transport, "initialized", true);
                Set(transport, "simulationOptions", options); Set(transport, "simulationCoordinator", coordinator);
                var bindings = (SteamIdentityBindingRegistry)Get(transport, "bindings");
                bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(2, 222) });
                Assert.That(bindings.TryBindConnection(118, 222, out _), Is.True);
                connections.Add(118, connection);
                ((IDictionary)Get(transport, "checkpointConnectionTokens"))[118] = "recovery-peer-token";
                var notice = new SimulationRecoveryNoticeDto { GameId = options.GameId, SimulationEpoch = 2, SourceSimulationEpoch = 1,
                    MembershipVersion = 1, CheckpointId = "recovery-fixture", ConfirmedGlobalSequence = 0, MetadataDigest = "fixture-metadata",
                    ResumeAllowed = true, CheckpointFlowVersion = SimulationCheckpointEnvelope.BoundedTransferVersion, Phase = SimulationRecoveryPhase.Prepare };
                var identity = new SimulationCheckpointEnvelope { GameId = options.GameId, ContentPackHash = options.ContentPackHash,
                    ProtocolVersion = SimulationCheckpointEnvelope.RecoveryProtocolVersion, Purpose = SimulationCheckpointEnvelope.ConfirmedSimulationRecovery,
                    SourceSimulationEpoch = 1, SimulationEpoch = 2, MembershipVersion = 1, CheckpointId = notice.CheckpointId,
                    ArchiveDigestVersion = SimulationCheckpointEnvelope.RecoveryDigestVersion };
                var transfer = SimulationCheckpointTransfer.Encode(identity, SimulationCheckpointTransferTests.LargeTransportFixture());
                Set(transport, "recoveryNotice", notice); Set(transport, "recoveryTransfer", transfer);
                var outbound = (IDictionary)Get(transport, "checkpointOutbound");
                Call(transport, "SendRecoveryPackage", connection);
                Assert.That(outbound.Count, Is.Zero, "未协商窗口能力时不能发送恢复包。");
                ((HashSet<int>)Get(transport, "checkpointFlowPeers")).Add(118);
                Call(transport, "SendRecoveryPackage", connection);
                var window = (SimulationCheckpointSendWindow)Get(outbound[118], "Window");
                Assert.That(window.TotalBytes, Is.GreaterThan(524288));
                Assert.That(window.SentBytes, Is.EqualTo(SimulationCheckpointSendWindow.MaximumInFlightBytes));

                void SendControl(SimulationRecoveryPhase phase, int received)
                {
                    var payload = new SimulationRecoveryNoticeDto { GameId = notice.GameId, SimulationEpoch = notice.SimulationEpoch,
                        SourceSimulationEpoch = notice.SourceSimulationEpoch, MembershipVersion = notice.MembershipVersion,
                        CheckpointId = notice.CheckpointId, ConfirmedGlobalSequence = notice.ConfirmedGlobalSequence,
                        MetadataDigest = notice.MetadataDigest, ResumeAllowed = notice.ResumeAllowed, CheckpointFlowVersion = notice.CheckpointFlowVersion,
                        CheckpointReceivedBytes = received, Phase = phase };
                    var parts = (IEnumerable)Call(transport, "ProtocolParts", "confirmed-recovery", payload, "recovery-peer-token");
                    foreach (var part in parts) Call(transport, "ReceiveRecoveryClientControl", connection, part);
                }
                var prepared = (HashSet<int>)Get(transport, "recoveryPrepared");
                SendControl(SimulationRecoveryPhase.Prepared, 0);
                Assert.That(prepared.Contains(2), Is.False, "发送未完成时不能进入已准备状态。");
                while (window.SentBytes < window.TotalBytes)
                {
                    SendControl(SimulationRecoveryPhase.Receiving, window.SentBytes);
                    Assert.That(window.SentBytes - window.AcknowledgedBytes,
                        Is.LessThanOrEqualTo(SimulationCheckpointSendWindow.MaximumInFlightBytes));
                }
                SendControl(SimulationRecoveryPhase.Prepared, 0);
                Assert.That(prepared.Contains(2), Is.True);
                Assert.That(outbound.Count, Is.Zero);
                Call(transport, "SendRecoveryPackage", connection);
                Assert.That(outbound.Count, Is.Zero, "重复请求不能重启已准备的传输。");
                Assert.That(Get(transport, "recoveryCommitIssued"), Is.False, "其他成员尚未准备时不能提交恢复。");
            }
            finally
            {
                connections.Remove(118); Call(transport, "Shutdown");
                activeTransport.SetValue(null, previousTransport); UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void EarlyCheckpointRequestIsConsumedUntilRealHostHandlerTakesOver()
        {
            Assert.That(NetworkIsActive(), Is.False);
            var transportType = Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport");
            var serverType = Target("Mirror.NetworkServer");
            var messageType = Target("YC.Infrastructure.Multiplayer.SimulationCheckpointRequestMessage");
            var connectionType = Target("Mirror.NetworkConnectionToClient");
            var readerType = Target("Mirror.NetworkReader");
            var writerType = Target("Mirror.NetworkWriter");
            var idType = Target("Mirror.NetworkMessageId`1").MakeGenericType(messageType);
            var id = idType.GetField("Id", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var handlers = (IDictionary)serverType.GetField("handlers", Statics).GetValue(null);
            bool hadHandler = handlers.Contains(id);
            var previousHandler = hadHandler ? handlers[id] : null;
            var root = new GameObject("EarlyCheckpointRequestFixture"); root.SetActive(false);
            var transport = root.AddComponent(transportType);
            try
            {
                transportType.GetMethod("RegisterEarlyCheckpointHandler", Statics).Invoke(null, null);
                var message = Activator.CreateInstance(messageType);
                messageType.GetField("EnvelopeJson").SetValue(message, "fixture");
                messageType.GetField("PlayerId").SetValue(message, 2);
                messageType.GetField("ConnectionToken").SetValue(message, "fixture-token");
                var writer = Activator.CreateInstance(writerType);
                var pack = Target("Mirror.NetworkMessages").GetMethods(Statics).Single(method => method.Name == "Pack" && method.IsGenericMethodDefinition)
                    .MakeGenericMethod(messageType);
                pack.Invoke(null, new[] { message, writer });
                var bytes = writerType.GetMethod("ToArraySegment").Invoke(writer, null);
                var connection = Activator.CreateInstance(connectionType, 17, "localhost");
                var unpack = serverType.GetMethod("UnpackAndInvoke", Statics);
                bool Dispatch() => (bool)unpack.Invoke(null, new[] { connection, Activator.CreateInstance(readerType, bytes), (object)0 });
                Assert.That(Dispatch(), Is.True, "房主尚未装配场景时，早到的请求必须被识别，不能按未知消息断开。");

                var callbackType = typeof(Action<,>).MakeGenericType(connectionType, messageType);
                var callback = Delegate.CreateDelegate(callbackType, transport, transportType.GetMethod("OnCheckpointRequest", Members));
                var replace = serverType.GetMethods(Statics).Single(method => method.Name == "ReplaceHandler" && method.IsGenericMethodDefinition &&
                    method.GetParameters()[0].ParameterType.IsGenericType &&
                    method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Action<,>)).MakeGenericMethod(messageType);
                replace.Invoke(null, new object[] { callback, false });
                Assert.That(Dispatch(), Is.True, "正式处理器接管后仍必须识别相同协议消息。");
            }
            finally
            {
                if (hadHandler) handlers[id] = previousHandler; else handlers.Remove(id);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ShutdownAllowsSavedSeatsToReconnectWithReassignedConnectionIds()
        {
            // 隔离生命周期回归：模拟原房间退出后，续局成员按不同顺序重新连接。
            Assert.That(NetworkIsActive(), Is.False);
            var root = new GameObject("ResumeConnectionBindings"); root.SetActive(false);
            var transport = root.AddComponent(Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
            try
            {
                var bindings = (SteamIdentityBindingRegistry)Get(transport, "bindings");
                var seats = new[] { new KeyValuePair<int, ulong>(2, 222),
                    new KeyValuePair<int, ulong>(3, 333), new KeyValuePair<int, ulong>(4, 444) };
                bindings.ReplaceLobbySeats(seats);
                Assert.That(bindings.TryBindConnection(1, 222, out _), Is.True);
                Assert.That(bindings.TryBindConnection(2, 333, out _), Is.True);
                Assert.That(bindings.TryBindConnection(3, 444, out _), Is.True);
                // 主动退出会先退订断开事件，因此不能依赖逐个断开回调清理。
                Call(transport, "Shutdown");
                bindings.ReplaceLobbySeats(seats);
                Assert.That(bindings.TryBindConnection(1, 444, out var fourth), Is.True);
                Assert.That(fourth, Is.EqualTo(4));
                Assert.That(bindings.TryBindConnection(2, 333, out var third), Is.True);
                Assert.That(third, Is.EqualTo(3));
                Assert.That(bindings.TryBindConnection(3, 222, out var second), Is.True);
                Assert.That(second, Is.EqualTo(2));
                Assert.That(bindings.TryBindConnection(4, 999, out _), Is.False, "未知身份仍须拒绝。");
                Assert.That(bindings.TryBindConnection(5, 444, out _), Is.False, "同一身份不能占用两条连接。");
            }
            finally { Call(transport, "Shutdown"); UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void AcceptedLocalReconnectKeepsRoomTicketAfterDeadlineAndAllowsExplicitRetry()
        {
            Assert.That(NetworkIsActive(), Is.False);
            var runtimeType = Target("YC.Infrastructure.Multiplayer.MirrorNetworkRuntime");
            var previous = Read(runtimeType, null, "Instance"); var root = new GameObject("ReconnectLifecycle"); root.SetActive(false);
            object runtime = root.AddComponent(runtimeType); object room = null;
            try
            {
                SetProperty(runtimeType, null, "Instance", runtime);
                SetProperty(runtimeType, runtime, "IsLocalTestMode", true);
                Call(runtime, "SetLocalClientIdentityTicket", "original-test-ticket");
                room = Activator.CreateInstance(Target("YC.Infrastructure.Multiplayer.LocalMirrorRoomService"));
                var inner = Get(room, "roomService");
                Set(inner, "currentRoom", new RoomState { RoomId = "local-reconnect-room", LocalPlayerId = 2, HasStarted = true, GameId = "same-started-game" });
                Set(inner, "localIdentityTicket", "original-test-ticket");
                Set(room, "acceptNetworkEvents", true); Set(room, "isHost", false);
                Call(room, "SubscribeToRuntime", runtime);
                int physical = 0, terminal = 0, disbanded = 0;
                var order = new List<string>();
                Subscribe(runtime, "LocalClientDisconnected", (Action)(() => physical++));
                Subscribe(runtime, "LocalClientReconnectFailed", (Action<string>)(code => order.Add(code)));
                Subscribe(runtime, "ClientDisconnected", (Action)(() => { terminal++; order.Add("terminal"); }));
                Subscribe(room, "RoomDisbanded", (Action)(() => disbanded++));
                float beforeDisconnect = Time.unscaledTime;
                Call(runtime, "OnLocalNetworkDisconnected");
                float afterDisconnect = Time.unscaledTime;
                Assert.That(physical, Is.EqualTo(1)); Assert.That(terminal, Is.EqualTo(0)); Assert.That(disbanded, Is.EqualTo(0));
                Assert.That(Read(runtimeType, runtime, "IsReconnectingLocalClient"), Is.True); Assert.That(Read(runtimeType, runtime, "HasLocalClientIdentityTicket"), Is.True);
                Assert.That(((RoomState)Call(room, "GetCurrentRoom")).GameId, Is.EqualTo("same-started-game"));
                Assert.That(((NetworkRoomService)inner).LocalIdentityTicket, Is.EqualTo("original-test-ticket"));
                float deadline = (float)Get(runtime, "localReconnectDeadline");
                Assert.That(deadline, Is.InRange(beforeDisconnect + 60f, afterDisconnect + 60f));
                Call(runtime, "OnLocalNetworkDisconnected"); Assert.That(physical, Is.EqualTo(1));
                Assert.That(Call(runtime, "TryBeginLocalClientReconnect"), Is.True); Assert.That((float)Get(runtime, "localReconnectDeadline"), Is.EqualTo(deadline));
                Set(runtime, "localReconnectDeadline", Time.unscaledTime - 1f); Set(runtime, "nextLocalReconnectTime", Time.unscaledTime - 1f);
                Call(runtime, "Update");
                Assert.That(Read(runtimeType, runtime, "IsReconnectingLocalClient"), Is.False);
                Assert.That(Read(runtimeType, runtime, "IsClientReconnectWaitingForRetry"), Is.True);
                Assert.That(terminal, Is.Zero); Assert.That(disbanded, Is.Zero);
                Assert.That(((RoomState)Call(room, "GetCurrentRoom")).GameId, Is.EqualTo("same-started-game"));
                Assert.That(order, Is.Empty);
                Assert.That(Call(runtime, "TryBeginLocalClientReconnect"), Is.True);
                Assert.That(Read(runtimeType, runtime, "IsReconnectingLocalClient"), Is.False, "重复断线回调不能自动重启已暂停的重试。");
                Assert.That(Call(runtime, "TryRequestClientReconnect"), Is.True);
                Assert.That(Read(runtimeType, runtime, "IsReconnectingLocalClient"), Is.True);
            }
            finally
            {
                if (room != null) Call(room, "Dispose"); SetProperty(runtimeType, null, "Instance", previous); UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RecoveryWaitAcceptsOnlyOriginalLocalConnectionFaultAndSurvivesTimeout()
        {
            Assert.That(NetworkIsActive(), Is.False);
            var runtimeType = Target("YC.Infrastructure.Multiplayer.MirrorNetworkRuntime");
            var transportType = Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport");
            var previous = Read(runtimeType, null, "Instance"); var root = new GameObject("RecoveryFaultClassification"); root.SetActive(false);
            object runtime = root.AddComponent(runtimeType); object transport = root.AddComponent(transportType);
            try
            {
                SetProperty(runtimeType, null, "Instance", runtime); SetProperty(runtimeType, runtime, "IsLocalTestMode", true);
                Call(runtime, "SetLocalClientIdentityTicket", "original-bound-ticket");
                var state = new GameState { GameId = "local-reconnect-match", ContentPackHash = "reconnect-content", Phase = GamePhase.ActionRound1, Round = 1, CurrentPlayerId = 1 };
                state.Players.Add(new PlayerState { PlayerId = 1 });
                var session = new GameSession(state); var options = DeterministicSimulationSessionOptions.CreateInitial(false, 1, session, new[] { 1 });
                var coordinator = new DeterministicSimulationCoordinator(session, options); coordinator.Begin(); Assert.That(coordinator.IsStopped, Is.False);
                Set(transport, "session", session); Set(transport, "mode", LaunchMode.Client); Set(transport, "localPlayerId", 1);
                Set(transport, "initialized", true); Set(transport, "simulationOptions", options);
                var identity = new SimulationCheckpointEnvelope { GameId = options.GameId, ContentPackHash = options.ContentPackHash,
                    SimulationEpoch = 1, MembershipVersion = 1, CheckpointId = "fixture.initial" };
                var channel = Activator.CreateInstance(Target("YC.Infrastructure.Multiplayer.SimulationCheckpointChannel"), identity,
                    new InitialSimulationCheckpointInstaller(session, new EffectRegistry(), GameStateViewer.Player(1)), null, new[] { 1 });
                Set(transport, "checkpointChannel", channel);
                Call(transport, "AttachSimulation", coordinator, false);
                WaitForBackground(coordinator, () => coordinator.TryReadPublishedSnapshot(out _));
                Call(transport, "ConfigureConfirmedRecovery", new SimulationConfirmedRecoveryInstaller(session, new EffectRegistry(), GameStateViewer.Player(1), options));
                SimulationProtocolFaultDto observed = null;
                Subscribe(transport, "SimulationProtocolFaulted", (Action<SimulationProtocolFaultDto>)(value => observed = value));
                Subscribe(runtime, "LocalClientReconnectFailed", (Action<string>)(code => Call(transport, "OnLocalClientReconnectFailed", code)));
                Call(transport, "OnCheckpointClientDisconnected");
                WaitForBackground(coordinator, () => observed != null);
                Assert.That(observed.FaultCode, Is.EqualTo("simulation_connection_lost"));
                Assert.That(Read(transportType, transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.True); Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", observed), Is.True);
                var fault = new SimulationProtocolFaultDto { GameId = options.GameId, SimulationEpoch = 1, MembershipVersion = 1,
                    FaultCode = "rule_rejected", RequiresRecovery = true };
                Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.FaultCode = "simulation_connection_lost"; fault.GameId = "another-game"; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.GameId = options.GameId; fault.SimulationEpoch = 2; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.SimulationEpoch = 1; fault.MembershipVersion = 2; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.MembershipVersion = 1; fault.RequiresRecovery = false; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                // 后台模拟发布独立的可读快照；不能再修改启动前的旧对象假装改变当前内容身份。
                var publishedState = session.State;
                string content = publishedState.ContentPackHash; publishedState.ContentPackHash = "changed-content";
                Assert.That(Read(transportType, transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.False); publishedState.ContentPackHash = content;
                Set(runtime, "localReconnectDeadline", Time.unscaledTime - 1f); Set(runtime, "nextLocalReconnectTime", Time.unscaledTime - 1f);
                Call(runtime, "Update");
                Assert.That(Read(transportType, transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.True);
                Assert.That(Read(transportType, transport, "LocalSimulationConnectionRecoveryFailureCode"), Is.EqualTo(string.Empty));
                Assert.That(observed.FaultCode, Is.EqualTo("simulation_connection_lost"));
                Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", observed), Is.True);
                Assert.That(Read(transportType, transport, "CanRequestLocalConnectionRecovery"), Is.True);
                Assert.That(Call(transport, "TryRequestLocalConnectionRecovery"), Is.True);
            }
            finally
            {
                Call(transport, "Shutdown"); SetProperty(runtimeType, null, "Instance", previous); UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Target(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).First(type => type != null);
        private static bool NetworkIsActive() => (bool)Read(Target("Mirror.NetworkServer"), null, "active") || (bool)Read(Target("Mirror.NetworkClient"), null, "isConnected");
        private static void WaitForBackground(DeterministicSimulationCoordinator coordinator, Func<bool> condition)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                coordinator.DrainMainThreadNotifications(128);
                if (condition()) return;
                if (coordinator.BackgroundExecutionException != null) throw coordinator.BackgroundExecutionException;
                System.Threading.Thread.Sleep(5);
            }
            Assert.Fail("等待隔离夹具的后台快照或协议通知超时。");
        }
        private static object Read(Type type, object value, string name)
        {
            var flags = value == null ? Statics : Members;
            var property = type.GetProperty(name, flags);
            return property != null ? property.GetValue(value) : type.GetField(name, flags).GetValue(value);
        }
        private static object Get(object value, string name) => value.GetType().GetField(name, Members).GetValue(value);
        private static void Set(object value, string name, object data) => value.GetType().GetField(name, Members).SetValue(value, data);
        private static void SetProperty(Type type, object value, string name, object data) => type.GetProperty(name, value == null ? Statics : Members).GetSetMethod(true).Invoke(value, new[] { data });
        private static void Subscribe(object value, string name, Delegate handler) => value.GetType().GetEvent(name, Members).AddEventHandler(value, handler);
        private static object Call(object value, string name, params object[] arguments)
        {
            try { return value.GetType().GetMethods(Members).Single(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(value, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
    }
}
