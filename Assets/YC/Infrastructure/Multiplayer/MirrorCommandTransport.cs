using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    public struct SubmitCommandMessage : NetworkMessage { public string Json; }
    public struct AcceptedCommandMessage : NetworkMessage { public string Json; }
    public struct RejectedCommandMessage : NetworkMessage { public string Json; }
    public struct InitialStateRequestMessage : NetworkMessage { }
    public struct InitialStateMessage : NetworkMessage { public string Json; public string ProtocolVersion; }
    public struct InitialStateAppliedMessage : NetworkMessage { public int PlayerId; }

    [DefaultExecutionOrder(-23000)]
    public sealed class MirrorCommandTransport : MonoBehaviour, INetworkCommandTransport
    {
        private readonly SteamIdentityBindingRegistry bindings = new SteamIdentityBindingRegistry();
        private AuthoritativeCommandDispatcher dispatcher;
        private GameSession session;
        private LaunchMode mode;
        private bool initialized;
        private bool awaitingInitialState;
        private float nextInitialStateRequestTime;
        private string pendingCommandId;
        private float commandConfirmationDeadline;
        private const float CommandConfirmationTimeout = 10f;
        private int localPlayerId;
        private IList<PlayerSeat> launchSeats;

        public event Action<ConfirmedGameCommandDto> ConfirmedCommandApplied;
        public event Action<InitialGameStateDto> InitialStateApplied;
        public event Action<RejectedGameCommandDto> CommandRejected;
        public static MirrorCommandTransport Instance { get; private set; }

        public bool HasCompletedSettlement => initialized && !awaitingInitialState && session != null &&
            RoundTrackRule.IsFinalState(session.State) &&
            session.State.FinalScoring != null && session.State.FinalScoring.IsResolved;

        public bool CanDetachCompletedSession
        {
            get
            {
                if (!HasCompletedSettlement) return false;
                if (mode != LaunchMode.Host) return true;
                // 客户端仅在成功应用最终结果后主动断开；房主必须继续发送/补同步，
                // 不能在最后一条可靠消息仍在发送队列时关闭服务端。
                foreach (var connection in NetworkServer.connections.Values)
                    if (connection != null && !(connection is LocalConnectionToClient)) return false;
                return true;
            }
        }


        public static MirrorCommandTransport Ensure()
        {
            if (Instance == null)
            {
                throw new InvalidOperationException(
                    "缺少预接线的 MirrorCommandTransport。请重建 NetworkRuntimeRoot Prefab 并确认 StartScene 接线完整。");
            }

            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                gameObject.SetActive(false);
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        public void Initialize(GameSession session, LaunchMode launchMode, int playerId, IList<PlayerSeat> seats)
        {
            Shutdown();
            this.session = session;
            mode = launchMode;
            localPlayerId = playerId;
            launchSeats = seats;
            if (launchSeats != null)
                for (var i = 0; i < launchSeats.Count; i++) launchSeats[i].GameStateSynchronized = false;
            dispatcher = new AuthoritativeCommandDispatcher(session, mode == LaunchMode.Client);
            if (mode == LaunchMode.Host)
            {
                var lobbySeats = new List<KeyValuePair<int, ulong>>();
                if (seats != null) foreach (var seat in seats) lobbySeats.Add(new KeyValuePair<int, ulong>(seat.PlayerId, seat.SteamId));
                bindings.ReplaceLobbySeats(lobbySeats);
                dispatcher.CommandAccepted += BroadcastAccepted;
                dispatcher.CommandRejected += SendRejected;
                NetworkServer.OnConnectedEvent += OnServerConnected;
                NetworkServer.OnDisconnectedEvent += OnServerDisconnected;
                NetworkServer.RegisterHandler<SubmitCommandMessage>(OnSubmit, false);
                NetworkServer.RegisterHandler<InitialStateRequestMessage>(OnInitialStateRequest, false);
                NetworkServer.RegisterHandler<InitialStateAppliedMessage>(OnInitialStateApplied, false);
                foreach (var connection in NetworkServer.connections.Values)
                    OnServerConnected(connection);
            }
            else if (mode == LaunchMode.Client)
            {
                NetworkClient.RegisterHandler<AcceptedCommandMessage>(OnAccepted, false);
                NetworkClient.RegisterHandler<RejectedCommandMessage>(OnRejected, false);
                NetworkClient.RegisterHandler<InitialStateMessage>(OnInitialState, false);
                NetworkClient.OnConnectedEvent += OnLocalClientConnected;
                awaitingInitialState = true;
                RequestInitialState();
            }
            initialized = true;
        }

        private void Update()
        {
            if (!initialized || mode != LaunchMode.Client || !NetworkClient.isConnected)
                return;
            if (!awaitingInitialState && !string.IsNullOrEmpty(pendingCommandId) &&
                Time.unscaledTime >= commandConfirmationDeadline)
            {
                // 只补同步状态，不重发原命令，避免重复支付或重复结算。
                awaitingInitialState = true;
                nextInitialStateRequestTime = 0f;
            }
            if (!awaitingInitialState || Time.unscaledTime < nextInitialStateRequestTime) return;
            RequestInitialState();
        }

        public CommandResult SubmitOrSend(GameCommand command, out bool appliedLocally)
        {
            appliedLocally = false;
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (!initialized || dispatcher == null) return Invalid("Mirror 命令传输尚未初始化。");
            var dto = GameCommandDto.FromCommand(command);
            if (mode == LaunchMode.Host)
            {
                var result = dispatcher.SubmitHostCommand(dto);
                appliedLocally = result.Succeeded;
                return result;
            }
            if (mode != LaunchMode.Client || !NetworkClient.isConnected) return dispatcher.RejectClientLocalSubmit(dto);
            if (awaitingInitialState || !dispatcher.IsInitialStateSynchronized)
                return Invalid("正在同步对局状态，请稍候。");
            if (!string.IsNullOrEmpty(pendingCommandId))
                return Invalid("上一条命令尚未确认，请等待同步结果。");
            pendingCommandId = dto.CommandId;
            commandConfirmationDeadline = Time.unscaledTime + CommandConfirmationTimeout;
            NetworkClient.Send(new SubmitCommandMessage { Json = JsonUtility.ToJson(dto) });
            return CommandResult.SuccessResult(new List<GameEvent>(), "命令已发送给房主。");
        }

        public void Shutdown()
        {
            if (dispatcher != null)
            {
                dispatcher.CommandAccepted -= BroadcastAccepted;
                dispatcher.CommandRejected -= SendRejected;
            }
            NetworkServer.OnConnectedEvent -= OnServerConnected;
            NetworkServer.OnDisconnectedEvent -= OnServerDisconnected;
            NetworkClient.OnConnectedEvent -= OnLocalClientConnected;
            if (NetworkServer.active)
            {
                NetworkServer.UnregisterHandler<SubmitCommandMessage>();
                NetworkServer.UnregisterHandler<InitialStateRequestMessage>();
                NetworkServer.UnregisterHandler<InitialStateAppliedMessage>();
            }
            if (NetworkClient.active)
            {
                NetworkClient.UnregisterHandler<AcceptedCommandMessage>();
                NetworkClient.UnregisterHandler<RejectedCommandMessage>();
                NetworkClient.UnregisterHandler<InitialStateMessage>();
            }
            dispatcher = null;
            session = null;
            initialized = false;
            awaitingInitialState = false;
            pendingCommandId = null;
            localPlayerId = -1;
            launchSeats = null;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Shutdown();
            Instance = null;
        }

        private void OnServerConnected(NetworkConnectionToClient connection)
        {
            if (connection == null || !NetworkServer.connections.ContainsKey(connection.connectionId)) return;
            ulong identity;
            if (IsLocalTestMode())
            {
                if (!TryGetExpectedIdentity(connection.connectionId, out identity))
                {
                    RejectConnection(connection, "拒绝与等待房间映射不一致的本地测试连接。");
                    return;
                }
            }
            else if (connection is LocalConnectionToClient)
            {
                identity = Steamworks.SteamUser.GetSteamID().m_SteamID;
            }
            else if (!SteamIdentityAddress.TryParse(connection.address, out identity))
            {
                RejectConnection(connection, "拒绝无法识别 SteamID 的连接：" + connection.address);
                return;
            }

            if (!RoomReadinessPolicy.TryResolveExpectedBinding(
                    launchSeats,
                    connection.connectionId,
                    identity,
                    out var expectedPlayerId) ||
                !bindings.TryBindConnection(connection.connectionId, identity, out var playerId) ||
                playerId != expectedPlayerId)
            {
                RejectConnection(connection, "拒绝与等待房间身份映射不一致的连接：" + connection.address);
                return;
            }
            dispatcher.RegisterClientPlayer((ulong)connection.connectionId, playerId);
            if (connection is LocalConnectionToClient)
            {
                MarkGameStateSynchronized(playerId, true);
                return;
            }
            SendInitialState(connection);
        }

        private void OnServerDisconnected(NetworkConnectionToClient connection)
        {
            if (connection == null) return;
            if (bindings.TryGetPlayer(connection.connectionId, out var playerId))
                MarkGameStateSynchronized(playerId, false);
            bindings.UnbindConnection(connection.connectionId);
        }

        private void OnSubmit(NetworkConnectionToClient connection, SubmitCommandMessage message)
        {
            var dto = JsonUtility.FromJson<GameCommandDto>(message.Json);
            if (!bindings.IsCommandOwner(connection.connectionId, dto == null ? -1 : dto.PlayerId))
            {
                var rejected = RejectedGameCommandDto.FromResult(dto, Invalid("连接身份与命令玩家不一致。"));
                connection.Send(new RejectedCommandMessage { Json = JsonUtility.ToJson(rejected) });
                return;
            }
            dispatcher.ReceiveClientCommand((ulong)connection.connectionId, dto);
        }

        private void OnInitialStateRequest(NetworkConnectionToClient connection, InitialStateRequestMessage message)
        {
            if (bindings.TryGetPlayer(connection.connectionId, out _)) SendInitialState(connection);
        }

        private void OnInitialStateApplied(NetworkConnectionToClient connection, InitialStateAppliedMessage message)
        {
            if (!bindings.IsCommandOwner(connection.connectionId, message.PlayerId)) return;
            MarkGameStateSynchronized(message.PlayerId, true);
        }

        private void SendInitialState(NetworkConnectionToClient connection) =>
            connection.Send(new InitialStateMessage { Json = JsonUtility.ToJson(dispatcher.CreateInitialStateSynchronization()), ProtocolVersion = SteamLobbyPolicy.ProtocolVersion });

        private void BroadcastAccepted(ConfirmedGameCommandDto confirmed)
        {
            var message = new AcceptedCommandMessage { Json = JsonUtility.ToJson(confirmed) };
            foreach (var connection in NetworkServer.connections.Values)
            {
                if (!(connection is LocalConnectionToClient)) connection.Send(message, Channels.Reliable);
            }
            ConfirmedCommandApplied?.Invoke(confirmed);
        }

        private void SendRejected(ulong recipient, RejectedGameCommandDto rejected)
        {
            if (NetworkServer.connections.TryGetValue((int)recipient, out var connection))
                connection.Send(new RejectedCommandMessage { Json = JsonUtility.ToJson(rejected) });
        }

        private void OnInitialState(InitialStateMessage message)
        {
            if (NetworkServer.active) return;
            if (message.ProtocolVersion != SteamLobbyPolicy.ProtocolVersion)
            {
                Debug.LogError("对局协议版本不兼容，请所有玩家使用同一构建。");
                NetworkClient.Disconnect();
                return;
            }
            var snapshot = JsonUtility.FromJson<InitialGameStateDto>(message.Json);
            if (snapshot?.State == null || snapshot.State.MapId != session.State.MapId ||
                snapshot.State.Players.Count != session.State.Players.Count)
            {
                Debug.LogError("权威开局地图或人数与当前房间不一致，已拒绝同步。");
                NetworkClient.Disconnect();
                return;
            }
            var result = dispatcher.ApplyInitialStateSynchronization(snapshot);
            if (result.Succeeded)
            {
                awaitingInitialState = false;
                pendingCommandId = null;
                MarkGameStateSynchronized(localPlayerId, true);
                if (NetworkClient.isConnected)
                    NetworkClient.Send(new InitialStateAppliedMessage { PlayerId = localPlayerId });
                InitialStateApplied?.Invoke(snapshot);
            }
        }

        private void OnAccepted(AcceptedCommandMessage message)
        {
            if (NetworkServer.active) return;
            var confirmed = JsonUtility.FromJson<ConfirmedGameCommandDto>(message.Json);
            var result = dispatcher.ApplyConfirmedCommand(confirmed);
            if (result.Succeeded)
            {
                if (confirmed.Command != null && confirmed.Command.CommandId == pendingCommandId)
                    pendingCommandId = null;
                ConfirmedCommandApplied?.Invoke(confirmed);
            }
            else
            {
                awaitingInitialState = true;
                RequestInitialState();
            }
        }

        private void OnRejected(RejectedCommandMessage message)
        {
            if (NetworkServer.active) return;
            var rejected = JsonUtility.FromJson<RejectedGameCommandDto>(message.Json);
            if (rejected != null && rejected.Command != null && rejected.Command.CommandId == pendingCommandId)
                pendingCommandId = null;
            CommandRejected?.Invoke(rejected);
        }

        private void RequestInitialState()
        {
            if (!NetworkClient.isConnected) return;
            NetworkClient.Send(new InitialStateRequestMessage());
            nextInitialStateRequestTime = Time.unscaledTime + 1f;
        }

        private void OnLocalClientConnected()
        {
            RequestInitialState();
        }

        private bool TryGetExpectedIdentity(int connectionId, out ulong identity)
        {
            identity = 0;
            if (launchSeats == null) return false;
            for (var i = 0; i < launchSeats.Count; i++)
            {
                var seat = launchSeats[i];
                if (seat == null || seat.NetworkClientId != (ulong)connectionId) continue;
                identity = seat.SteamId;
                return identity != 0;
            }
            return false;
        }

        private void MarkGameStateSynchronized(int playerId, bool synchronized)
        {
            if (launchSeats == null) return;
            for (var i = 0; i < launchSeats.Count; i++)
            {
                if (launchSeats[i] == null || launchSeats[i].PlayerId != playerId) continue;
                launchSeats[i].GameStateSynchronized = synchronized;
                return;
            }
        }

        private static void RejectConnection(NetworkConnectionToClient connection, string reason)
        {
            Debug.LogWarning(reason);
            connection.Disconnect();
        }

        private static bool IsLocalTestMode() =>
            MirrorNetworkRuntime.Instance != null && MirrorNetworkRuntime.Instance.IsLocalTestMode;

        private static CommandResult Invalid(string reason) =>
            CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidPlayer, reason));
    }
}
