using System;
using System.Collections;
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
    public sealed class SteamReconnectLifecycleTests
    {
        [Test]
        public void SteamDisconnectKeepsOriginalLobbySeatAndValidatesConnectedCallback()
        {
            using (var fixture = new Fixture())
            {
                Call(fixture.Runtime, "OnLocalNetworkDisconnected");
                Assert.That(Read(fixture.Runtime, "IsReconnectingClient"), Is.True);
                Assert.That(Read(fixture.Runtime, "HasClientReconnectIdentity"), Is.True);
                Assert.That(fixture.Leaves, Is.EqualTo(0)); Assert.That(fixture.Terminal, Is.EqualTo(0));
                var room = (RoomState)Call(fixture.Room, "GetCurrentRoom");
                Assert.That(room.GameId, Is.EqualTo("steam-original-game")); Assert.That(room.LocalPlayerId, Is.EqualTo(2));
                Assert.That(room.Seats.Select(p => p.SteamId).ToArray(), Is.EqualTo(new ulong[] { 101, 202 }));
                float deadline = (float)Get(fixture.Runtime, "localReconnectDeadline");
                Call(fixture.Runtime, "OnLocalNetworkDisconnected"); Assert.That(Call(fixture.Runtime, "TryBeginClientReconnect"), Is.True);
                Call(fixture.Room, "PublishRoom", room);
                Assert.That((float)Get(fixture.Runtime, "localReconnectDeadline"), Is.EqualTo(deadline));
                fixture.Owner = 0; fixture.Members = new ulong[0];
                Call(fixture.Room, "RefreshRoom"); Assert.That(Call(fixture.Room, "GetCurrentRoom"), Is.Not.Null);
                Assert.That(Call(fixture.Runtime, "TryBeginClientReconnect"), Is.True); Assert.That(fixture.Leaves, Is.EqualTo(0));
                fixture.Owner = 101; fixture.Members = new ulong[] { 101, 202 };
                Call(fixture.Runtime, "OnLocalNetworkConnected");
                Assert.That(fixture.Connected, Is.EqualTo(1)); Assert.That(Read(fixture.Runtime, "IsReconnectingClient"), Is.False);
                Assert.That(Get(fixture.Runtime, "steamReconnectHostId"), Is.EqualTo(101UL));
                Assert.That(Read(fixture.Runtime, "ClientReconnectGameId"), Is.EqualTo("steam-original-game"));
            }
            using (var fixture = new Fixture())
            {
                Call(fixture.Runtime, "OnLocalNetworkDisconnected"); fixture.Account = 303;
                Call(fixture.Runtime, "OnLocalNetworkConnected");
                Assert.That(fixture.Connected, Is.EqualTo(0)); Assert.That(fixture.Terminal, Is.EqualTo(1));
                Assert.That(fixture.Failure, Is.EqualTo("simulation_steam_reconnect_identity_lost"));
                Assert.That(Call(fixture.Runtime, "TryBeginClientReconnect"), Is.False);
            }
        }

        [Test]
        public void SteamExitDeadlineAndUnavailableIdentityCannotReviveOldSession()
        {
            using (var fixture = new Fixture())
            {
                Call(fixture.Runtime, "OnLocalNetworkDisconnected");
                Set(fixture.Runtime, "localReconnectDeadline", Time.unscaledTime - 1f);
                Set(fixture.Runtime, "nextLocalReconnectTime", Time.unscaledTime - 1f); Call(fixture.Runtime, "Update");
                Assert.That(fixture.Failure, Is.EqualTo("simulation_steam_reconnect_timeout"));
                Assert.That(fixture.Terminal, Is.EqualTo(1)); Assert.That(fixture.Leaves, Is.EqualTo(1));
                Assert.That(Read(fixture.Runtime, "HasClientReconnectIdentity"), Is.False);
                Call(fixture.Runtime, "OnLocalNetworkConnected"); Call(fixture.Runtime, "OnLocalNetworkDisconnected"); Call(fixture.Runtime, "Update");
                Assert.That(Call(fixture.Runtime, "TryBeginClientReconnect"), Is.False); Assert.That(fixture.Connected, Is.EqualTo(0));
                Assert.That(fixture.Terminal, Is.EqualTo(1));
            }
            using (var fixture = new Fixture())
            {
                Call(fixture.Runtime, "OnLocalNetworkDisconnected"); Call(fixture.Room, "Shutdown");
                Call(fixture.Runtime, "OnLocalNetworkConnected"); Call(fixture.Runtime, "OnLocalNetworkDisconnected");
                Assert.That(Call(fixture.Runtime, "TryBeginClientReconnect"), Is.False);
                Assert.That(fixture.Leaves, Is.EqualTo(1)); Assert.That(fixture.Connected, Is.EqualTo(0));
            }
            using (var fixture = new Fixture())
            {
                fixture.Ready = false; Call(fixture.Runtime, "OnLocalNetworkDisconnected");
                Assert.That(fixture.Terminal, Is.EqualTo(0)); Assert.That(fixture.Disbanded, Is.EqualTo(1));
                Assert.That(Call(fixture.Room, "GetCurrentRoom"), Is.Null); Assert.That(Read(fixture.Runtime, "HasClientReconnectIdentity"), Is.False);
            }
            using (var fixture = new Fixture())
            {
                Call(fixture.Runtime, "OnLocalNetworkDisconnected"); fixture.Owner = 999;
                Call(fixture.Runtime, "OnLocalNetworkConnected");
                Assert.That(fixture.Failure, Is.EqualTo("simulation_steam_host_changed")); Assert.That(fixture.Connected, Is.EqualTo(0));
            }
        }

        [Test]
        public void UnknownLobbyMembershipRetriesAuthenticationWithoutAnotherLobbyCallback()
        {
            using (var fixture = new Fixture(true))
            {
                var server = Target("Mirror.NetworkServer"); SetMember(server, null, "active", true);
                var connections = (IDictionary)ReadMember(server, null, "connections");
                object connection = CreateConnection(111); connections.Add(111, connection);
                using (var boundary = new AddressBoundary(fixture.Root, connection, 202))
                try
                {
                    fixture.Members = new ulong[0];
                    var info = Activator.CreateInstance(Target("YC.Infrastructure.Multiplayer.MirrorServerConnectionInfo"), new object[] { 111, "steam://202", false });
                    Call(fixture.Room, "OnServerClientConnected", info);
                    Assert.That(((IDictionary)Get(fixture.Room, "pendingSteamConnections")).Count, Is.EqualTo(1));
                    Assert.That(Get(fixture.Runtime, "serverIdentityRetryPending"), Is.EqualTo(true));
                    fixture.Members = new ulong[] { 101, 202 };
                    Set(fixture.Runtime, "nextServerIdentityRetryTime", Time.unscaledTime - 1f); Call(fixture.Runtime, "Update");
                    Assert.That(((IDictionary)Get(fixture.Room, "pendingSteamConnections")).Count, Is.EqualTo(0));
                    var room = (RoomState)Call(fixture.Room, "GetCurrentRoom"); var restored = room.Seats.Single(p => p.SteamId == 202);
                    Assert.That(restored.PlayerId, Is.EqualTo(2)); Assert.That(restored.NetworkClientId, Is.EqualTo(111UL));
                    Assert.That(restored.IdentityVerified && restored.TransportConnected, Is.True);
                    Assert.That(room.Seats.Count, Is.EqualTo(2));
                }
                finally { connections.Remove(111); SetMember(server, null, "active", false); }
            }
        }

        [Test]
        public void SteamTransportRecoversOnlyOriginalSessionAndBindsAuthenticatedNewConnection()
        {
            var group = typeof(ConfirmedSimulationRecoveryTests).GetMethod("CreateGroup", Statics).Invoke(null, new object[] { false });
            var sessions = (GameSession[])Get(group, "Sessions"); var registries = (EffectRegistry[])Get(group, "Registries");
            var coordinators = (DeterministicSimulationCoordinator[])Get(group, "Coordinators");
            using (var fixture = new Fixture(false, sessions[1].State.GameId, true))
            {
                object transport = fixture.Root.AddComponent(Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
                ConfigureTransport(transport, fixture, sessions[1], registries[1], coordinators[1], false);
                try
                {
                    SimulationProtocolFaultDto observed = null;
                    Subscribe(transport, "SimulationProtocolFaulted", (Action<SimulationProtocolFaultDto>)(fault => observed = fault));
                    Call(transport, "OnCheckpointClientDisconnected");
                    Assert.That(observed.FaultCode, Is.EqualTo("simulation_connection_lost"));
                    Assert.That(Read(transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.True);
                    Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", observed), Is.True);
                    string game = observed.GameId; observed.GameId = "another-steam-game";
                    Assert.That(Call(transport, "CanAwaitLocalSimulationConnectionRecovery", observed), Is.False); observed.GameId = game;
                    fixture.Account = 303; Assert.That(Read(transport, "IsAwaitingLocalSimulationConnectionRecovery"), Is.False); fixture.Account = 202;
                    Call(transport, "OnLocalClientConnected");
                    Assert.That(Read(transport, "IsRecoveringConfirmedPrefix"), Is.True);
                    Assert.That(Get(transport, "simulationCoordinator"), Is.SameAs(coordinators[1]));
                    Assert.That(sessions[1].State.GameId, Is.EqualTo(game));
                }
                finally { Call(transport, "Shutdown"); }
            }
            using (var fixture = new Fixture(true, sessions[0].State.GameId, true))
            {
                object transport = fixture.Root.AddComponent(Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
                ConfigureTransport(transport, fixture, sessions[0], registries[0], coordinators[0], true);
                var provider = Target("YC.Infrastructure.Multiplayer.OnlineRoomServiceProvider");
                string activeName = provider.GetField("active", Statics) != null ? "active" : "Active";
                object previous = ReadMember(provider, null, activeName); SetMember(provider, null, activeName, fixture.Room);
                var server = Target("Mirror.NetworkServer"); var connections = (IDictionary)ReadMember(server, null, "connections");
                object connection = CreateConnection(111);
                using (var boundary = new AddressBoundary(fixture.Root, connection, 202))
                {
                    SetMember(server, null, "active", true); connections.Add(111, connection);
                    try
                    {
                        Call(transport, "OnServerConnected", connection);
                        Assert.That(((IDictionary)Get(transport, "pendingLocalBindings")).Count, Is.EqualTo(1));
                        object info = Activator.CreateInstance(Target("YC.Infrastructure.Multiplayer.MirrorServerConnectionInfo"), new object[] { 111, "steam://202", false });
                        Call(fixture.Room, "OnServerClientConnected", info); Call(transport, "RefreshPendingLocalBindings");
                        Assert.That(((IDictionary)Get(transport, "pendingLocalBindings")).Count, Is.EqualTo(0));
                        object bindings = Get(transport, "bindings"); object[] args = { 111, 0 };
                        Assert.That(Call(bindings, "TryGetPlayer", args), Is.True); Assert.That(args[1], Is.EqualTo(1));
                        Call(transport, "OnServerDisconnected", connection);
                        Assert.That(coordinators[0].IsRecoveringConfirmedPrefix, Is.True);
                        Call(transport, "RequestConfirmedRecovery"); Assert.That(Read(transport, "IsRecoveringConfirmedPrefix"), Is.True);
                    }
                    finally { connections.Remove(111); SetMember(server, null, "active", false); SetMember(provider, null, activeName, previous); Call(transport, "Shutdown"); }
                }
            }
        }

        [Test]
        public void CompletedSteamSessionDoesNotStartAnotherReconnectLoop()
        {
            using (var fixture = new Fixture())
            {
                var transportType = Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport"); object previous = ReadMember(transportType, null, "Instance");
                object transport = fixture.Root.AddComponent(transportType); SetMember(transportType, null, "Instance", transport);
                var state = new GameState { GameId = "steam-original-game", ContentPackHash = "ended-content", Phase = GamePhase.GameOver,
                    FinalScoring = new FinalScoringState { IsResolved = true } };
                Set(transport, "session", new GameSession(state)); Set(transport, "initialized", true);
                try
                {
                    Assert.That(Read(transport, "HasCompletedSettlement"), Is.True);
                    Call(fixture.Runtime, "OnLocalNetworkDisconnected");
                    Assert.That(Read(fixture.Runtime, "IsReconnectingClient"), Is.False);
                    Assert.That(Read(fixture.Runtime, "HasClientReconnectIdentity"), Is.False);
                    Assert.That(fixture.Disbanded, Is.EqualTo(1)); Assert.That(fixture.Leaves, Is.EqualTo(1));
                    Call(fixture.Runtime, "OnLocalNetworkConnected"); Assert.That(fixture.Connected, Is.EqualTo(0));
                }
                finally { Call(transport, "Shutdown"); SetMember(transportType, null, "Instance", previous); }
            }
        }

        private static void ConfigureTransport(object transport, Fixture fixture, GameSession session, EffectRegistry registry,
            DeterministicSimulationCoordinator coordinator, bool host)
        {
            var options = coordinator.SessionOptions; var room = (RoomState)Call(fixture.Room, "GetCurrentRoom");
            Call(transport, "InitializeDeterministicSimulation", session, host ? LaunchMode.Host : LaunchMode.Client, options.LocalPlayerId,
                room.Seats.ToList(), options, host ? null : new InitialSimulationCheckpointInstaller(session, registry, GameStateViewer.Player(options.LocalPlayerId)),
                host ? (Func<string>)(() => HostRecoveryService.SerializeArchive(session.CreateHostArchive())) : null);
            Call(transport, "AttachSimulation", coordinator, false);
            Call(transport, "ConfigureConfirmedRecovery", new SimulationConfirmedRecoveryInstaller(session, registry, GameStateViewer.Player(options.LocalPlayerId), options));
        }

        // 仅隔离 Steam socket 地址读取；认证、冻结席位绑定和恢复方法均为实际生产对象。
        private sealed class AddressBoundary : IDisposable
        {
            private readonly Type transportType;
            private readonly object previousTransport;
            internal AddressBoundary(GameObject root, object connection, ulong steamId)
            {
                var address = connection.GetType().GetField("address", Members);
                if (address != null && !address.IsInitOnly) address.SetValue(connection, "steam://" + steamId);
                transportType = Target("Mirror.Transport"); previousTransport = ReadMember(transportType, null, "active");
                SetMember(transportType, null, "active", root.AddComponent(Target("Mirror.FizzySteam.FizzySteamworks")));
            }
            public void Dispose()
            { SetMember(transportType, null, "active", previousTransport); }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly GameObject Root;
            internal readonly object Runtime, Room;
            private readonly object previous;
            internal ulong Account = 202, Owner = 101;
            internal ulong[] Members = new ulong[] { 101, 202 };
            internal bool Ready = true;
            internal int Leaves, Terminal, Connected, Disbanded;
            internal string Failure;
            internal Fixture(bool host = false, string gameId = "steam-original-game", bool formal = false)
            {
                Assert.That((bool)ReadMember(Target("Mirror.NetworkServer"), null, "active") ||
                    (bool)ReadMember(Target("Mirror.NetworkClient"), null, "isConnected"), Is.False);
                var runtimeType = Target("YC.Infrastructure.Multiplayer.MirrorNetworkRuntime"); previous = ReadMember(runtimeType, null, "Instance");
                Root = new GameObject("SteamReconnectLifecycle"); Root.SetActive(false); Runtime = Root.AddComponent(runtimeType);
                SetMember(runtimeType, null, "Instance", Runtime);
                if (host) Account = 101;
                if (formal) Members = new ulong[] { 101, 202, 303 };
                Set(Runtime, "steamReadyProbe", (Func<bool>)(() => Ready)); Set(Runtime, "steamAccountProbe", (Func<ulong>)(() => Account));
                Set(Runtime, "steamOwnerProbe", (Func<ulong, ulong>)(_ => Owner)); Set(Runtime, "steamMemberCountProbe", (Func<ulong, int>)(_ => Members.Length));
                Set(Runtime, "steamMemberProbe", (Func<ulong, int, ulong>)((_, i) => Members[i]));
                Room = Activator.CreateInstance(Target("YC.Infrastructure.Multiplayer.SteamRoomService"));
                Set(Room, "steamReadyProbe", (Func<bool>)(() => Ready)); Set(Room, "steamAccountProbe", (Func<ulong>)(() => Account));
                Set(Room, "steamOwnerProbe", (Func<ulong, ulong>)(_ => Owner)); Set(Room, "steamMemberCountProbe", (Func<ulong, int>)(_ => Members.Length));
                Set(Room, "steamMemberProbe", (Func<ulong, int, ulong>)((_, i) => Members[i])); Set(Room, "steamLeaveLobbyProbe", (Action<ulong>)(_ => Leaves++));
                var data = new Dictionary<string, string>();
                Set(Room, "steamDataProbe", (Func<string, string>)(key => data.TryGetValue(key, out var value) ? value : ""));
                Set(Room, "steamWriteDataProbe", (Action<string, string>)((key, value) => data[key] = value));
                Set(Room, "lobbyId", Activator.CreateInstance(Target("Steamworks.CSteamID"), new object[] { 109775241000010001UL }));
                Set(Room, "originalHostSteamId", 101UL); Set(Room, "acceptNetworkEvents", true); Set(Room, "isHost", host); Set(Room, "authoritySnapshotReceived", !host);
                Call(Room, "SubscribeToRuntime", Runtime);
                Subscribe(Runtime, "LocalClientConnected", (Action)(() => Connected++)); Subscribe(Runtime, "ClientDisconnected", (Action)(() => Terminal++));
                Subscribe(Runtime, "LocalClientReconnectFailed", (Action<string>)(code => Failure = code)); Subscribe(Room, "RoomDisbanded", (Action)(() => Disbanded++));
                var room = new RoomState { RoomId = "109775241000010001", GameId = gameId, HasStarted = true, HostPlayerId = formal ? 3 : 1, LocalPlayerId = formal ? (host ? 3 : 1) : (host ? 1 : 2) };
                int index = 0;
                foreach (int player in formal ? new[] { 1, 2, 3 } : new[] { 1, 2 })
                {
                    room.SeatSlots[index].Status = "occupied";
                    room.SeatSlots[index].Occupant = new PlayerSeat { PlayerId = player, Color = RoomConfigurationService.GetSeatColor(index),
                        SteamId = formal ? (player == 3 ? 101UL : player == 1 ? 202UL : 303UL) : (player == 1 ? 101UL : 202UL), LobbyMemberPresent = true, IsReady = true };
                    index++;
                }
                room.Configuration.PlayerCount = index; room.Configuration.ContentPackHash = "reconnect-content";
                room.RandomReady = true; room.RandomAlgorithmVersion = GameplayRandomRuntime.AlgorithmVersion;
                room.RandomRuntime = GameplayRandomRuntime.Create(gameId); room.LaunchPlayerOrder = room.Seats.Select(p => p.PlayerId).ToList();
                Assert.That(RoomStateSerializer.TryValidate(room, out var reason), Is.True, reason);
                Call(Room, "PublishRoom", room);
            }
            public void Dispose()
            {
                Call(Room, "Dispose"); SetMember(Target("YC.Infrastructure.Multiplayer.MirrorNetworkRuntime"), null, "Instance", previous); UnityEngine.Object.DestroyImmediate(Root);
            }
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Target(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null);
        private static object Get(object value, string name) => value.GetType().GetField(name, Members).GetValue(value);
        private static void Set(object value, string name, object data) => value.GetType().GetField(name, Members).SetValue(value, data);
        private static object Read(object value, string name) => ReadMember(value.GetType(), value, name);
        private static object ReadMember(Type type, object value, string name)
        { var flags = value == null ? Statics : Members; var p = type.GetProperty(name, flags); return p != null ? p.GetValue(value) : type.GetField(name, flags).GetValue(value); }
        private static void SetMember(Type type, object value, string name, object data)
        { var flags = value == null ? Statics : Members; var p = type.GetProperty(name, flags); if (p != null) p.GetSetMethod(true).Invoke(value, new[] { data }); else type.GetField(name, flags).SetValue(value, data); }
        private static void Subscribe(object value, string name, Delegate handler) => value.GetType().GetEvent(name, Members).AddEventHandler(value, handler);
        private static object Call(object value, string name, params object[] args)
        { try { return value.GetType().GetMethods(Members).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; } }
        private static object CreateConnection(int id)
        {
            var type = Target("Mirror.NetworkConnectionToClient"); var ctor = type.GetConstructor(new[] { typeof(int), typeof(string) });
            if (ctor != null) return ctor.Invoke(new object[] { id, "steam://202" });
            ctor = type.GetConstructor(new[] { typeof(int) });
            if (ctor != null) return ctor.Invoke(new object[] { id });
            object value = Activator.CreateInstance(type); SetMember(type, value, "connectionId", id); return value;
        }
    }
}
