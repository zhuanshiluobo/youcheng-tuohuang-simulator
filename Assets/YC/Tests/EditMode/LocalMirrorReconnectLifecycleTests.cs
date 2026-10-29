using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        public void AcceptedLocalReconnectKeepsRoomTicketAndPublishesTerminalOnlyAtDeadline()
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
                Assert.That(Read(runtimeType, runtime, "IsReconnectingLocalClient"), Is.False); Assert.That(terminal, Is.EqualTo(1)); Assert.That(disbanded, Is.EqualTo(1));
                Assert.That(Call(room, "GetCurrentRoom"), Is.Null);
                Assert.That(order, Is.EqualTo(new[] { "simulation_local_reconnect_timeout", "terminal" }));
            }
            finally
            {
                if (room != null) Call(room, "Dispose"); SetProperty(runtimeType, null, "Instance", previous); UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RecoveryWaitAcceptsOnlyOriginalLocalConnectionFaultAndEndsOnExhaustion()
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
                Set(transport, "initialized", true); Set(transport, "simulationOptions", options); Call(transport, "AttachSimulation", coordinator, false);
                Call(transport, "ConfigureConfirmedRecovery", new SimulationConfirmedRecoveryInstaller(session, new EffectRegistry(), GameStateViewer.Player(1), options));
                SimulationProtocolFaultDto observed = null;
                Subscribe(transport, "SimulationProtocolFaulted", (Action<SimulationProtocolFaultDto>)(value => observed = value));
                Subscribe(runtime, "LocalClientReconnectFailed", (Action<string>)(code => Call(transport, "OnLocalClientReconnectFailed", code)));
                Call(transport, "OnCheckpointClientDisconnected");
                Assert.That(observed.FaultCode, Is.EqualTo("simulation_connection_lost"));
                Assert.That(Read(transportType, transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.True); Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", observed), Is.True);
                var fault = new SimulationProtocolFaultDto { GameId = options.GameId, SimulationEpoch = 1, MembershipVersion = 1,
                    FaultCode = "rule_rejected", RequiresRecovery = true };
                Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.FaultCode = "simulation_connection_lost"; fault.GameId = "another-game"; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.GameId = options.GameId; fault.SimulationEpoch = 2; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.SimulationEpoch = 1; fault.MembershipVersion = 2; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                fault.MembershipVersion = 1; fault.RequiresRecovery = false; Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", fault), Is.False);
                string content = state.ContentPackHash; state.ContentPackHash = "changed-content";
                Assert.That(Read(transportType, transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.False); state.ContentPackHash = content;
                Set(runtime, "localReconnectDeadline", Time.unscaledTime - 1f); Set(runtime, "nextLocalReconnectTime", Time.unscaledTime - 1f);
                Call(runtime, "Update");
                Assert.That(Read(transportType, transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.False);
                Assert.That(Read(transportType, transport, "LocalSimulationConnectionRecoveryFailureCode"), Is.EqualTo("simulation_local_reconnect_timeout"));
                Assert.That(observed.FaultCode, Is.EqualTo("simulation_local_reconnect_timeout"));
                Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", observed), Is.False); Assert.That(coordinator.IsStopped, Is.True);
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
