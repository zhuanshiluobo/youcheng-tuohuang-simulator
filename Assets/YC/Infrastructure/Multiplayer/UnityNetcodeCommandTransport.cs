using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace YC.Infrastructure.Multiplayer
{
    public sealed class UnityNetcodeCommandTransport : MonoBehaviour
    {
        private const string SubmitMessageName = "YC.GameCommand.Submit";
        private const string AcceptedMessageName = "YC.GameCommand.Accepted";
        private const string RejectedMessageName = "YC.GameCommand.Rejected";
        private const string InitialStateMessageName = "YC.GameState.Initial";
        private const string InitialStateRequestMessageName = "YC.GameState.Initial.Request";
        private const float InitialStateRequestRetrySeconds = 1f;

        private readonly List<ulong> clientBroadcastTargets = new List<ulong>();
        private readonly List<PlayerSeat> hostSeats = new List<PlayerSeat>();
        private readonly Dictionary<ulong, int> hostPlayerIdsByClientId = new Dictionary<ulong, int>();
        private readonly Dictionary<int, ulong> hostClientIdsByPlayerId = new Dictionary<int, ulong>();

        private AuthoritativeCommandDispatcher dispatcher;
        private NetworkManager networkManager;
        private LaunchMode mode;
        private int localPlayerId;
        private bool initialized;
        private float nextInitialStateRequestTime;

        public event Action<ConfirmedGameCommandDto> ConfirmedCommandApplied;
        public event Action<InitialGameStateDto> InitialStateApplied;
        public event Action<RejectedGameCommandDto> CommandRejected;

        public static UnityNetcodeCommandTransport Ensure()
        {
            var existing = FindObjectOfType<UnityNetcodeCommandTransport>();
            if (existing != null)
            {
                return existing;
            }

            var go = new GameObject("UnityNetcodeCommandTransport");
            if (UnityEngine.Application.isPlaying)
            {
                DontDestroyOnLoad(go);
            }

            return go.AddComponent<UnityNetcodeCommandTransport>();
        }

        public void Initialize(GameSession session, LaunchMode launchMode, int playerId, IList<PlayerSeat> seats)
        {
            Shutdown();

            networkManager = NetworkManager.Singleton;
            if (networkManager == null)
            {
                UnityEngine.Debug.LogWarning("Network command transport is unavailable because NetworkManager.Singleton is null.");
                return;
            }

            mode = launchMode;
            localPlayerId = playerId;
            dispatcher = new AuthoritativeCommandDispatcher(session, mode == LaunchMode.Client);
            CacheHostSeats(seats);

            if (mode == LaunchMode.Host)
            {
                RegisterHostClientMappings(hostSeats);
                dispatcher.CommandAccepted += BroadcastAcceptedCommand;
                dispatcher.CommandRejected += SendRejectedCommand;
                networkManager.OnClientConnectedCallback += OnClientConnected;
                networkManager.CustomMessagingManager.RegisterNamedMessageHandler(SubmitMessageName, OnSubmitMessage);
                networkManager.CustomMessagingManager.RegisterNamedMessageHandler(InitialStateRequestMessageName, OnInitialStateRequestMessage);
                BroadcastInitialState();
            }
            else if (mode == LaunchMode.Client)
            {
                networkManager.CustomMessagingManager.RegisterNamedMessageHandler(InitialStateMessageName, OnInitialStateMessage);
                networkManager.CustomMessagingManager.RegisterNamedMessageHandler(AcceptedMessageName, OnAcceptedMessage);
                networkManager.CustomMessagingManager.RegisterNamedMessageHandler(RejectedMessageName, OnRejectedMessage);
                RequestInitialState();
            }

            initialized = true;
        }

        public CommandResult SubmitOrSend(GameCommand command, out bool appliedLocally)
        {
            appliedLocally = false;
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (!initialized || dispatcher == null || networkManager == null)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Network command transport is not initialized."));
            }

            var dto = GameCommandDto.FromCommand(command);
            if (mode == LaunchMode.Host)
            {
                var result = dispatcher.SubmitHostCommand(dto);
                appliedLocally = result.Succeeded;
                return result;
            }

            if (mode != LaunchMode.Client)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Network command transport can only submit commands in Host or Client mode."));
            }

            if (!networkManager.IsClient || networkManager.IsServer)
            {
                return dispatcher.RejectClientLocalSubmit(dto);
            }

            if (!dispatcher.IsInitialStateSynchronized)
            {
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "Client initial game state has not synchronized with Host."));
            }

            SendMessageToHost(dto);
            return CommandResult.SuccessResult(new List<GameEvent>(), "Command sent to Host.");
        }

        public void Shutdown()
        {
            if (networkManager != null && networkManager.CustomMessagingManager != null)
            {
                networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(SubmitMessageName);
                networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(AcceptedMessageName);
                networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(RejectedMessageName);
                networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(InitialStateMessageName);
                networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(InitialStateRequestMessageName);
                networkManager.OnClientConnectedCallback -= OnClientConnected;
            }

            if (dispatcher != null)
            {
                dispatcher.CommandAccepted -= BroadcastAcceptedCommand;
                dispatcher.CommandRejected -= SendRejectedCommand;
            }

            dispatcher = null;
            networkManager = null;
            hostSeats.Clear();
            hostPlayerIdsByClientId.Clear();
            hostClientIdsByPlayerId.Clear();
            initialized = false;
            nextInitialStateRequestTime = 0f;
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void Update()
        {
            if (!initialized ||
                mode != LaunchMode.Client ||
                dispatcher == null ||
                dispatcher.IsInitialStateSynchronized ||
                Time.unscaledTime < nextInitialStateRequestTime)
            {
                return;
            }

            RequestInitialState();
        }

        private void OnClientConnected(ulong clientId)
        {
            RegisterHostClientMappings(hostSeats);
            SendInitialStateToClient(clientId);
        }

        private void CacheHostSeats(IList<PlayerSeat> seats)
        {
            hostSeats.Clear();
            if (seats == null)
            {
                return;
            }

            for (var i = 0; i < seats.Count; i++)
            {
                var seat = seats[i];
                hostSeats.Add(new PlayerSeat
                {
                    PlayerId = seat.PlayerId,
                    NetcodeClientId = seat.NetcodeClientId,
                    PlayerName = seat.PlayerName,
                    Color = seat.Color,
                    IsReady = seat.IsReady
                });
            }
        }

        private void RegisterHostClientMappings(IList<PlayerSeat> seats)
        {
            if (dispatcher == null || networkManager == null || !networkManager.IsServer)
            {
                return;
            }

            dispatcher.RegisterClientPlayer(networkManager.LocalClientId, localPlayerId);
            hostPlayerIdsByClientId[networkManager.LocalClientId] = localPlayerId;
            hostClientIdsByPlayerId[localPlayerId] = networkManager.LocalClientId;

            if (seats == null)
            {
                return;
            }

            for (var i = 0; i < seats.Count; i++)
            {
                var seat = seats[i];
                if (seat.NetcodeClientId != 0 || seat.PlayerId == localPlayerId)
                {
                    dispatcher.RegisterClientPlayer(seat.NetcodeClientId, seat.PlayerId);
                    hostPlayerIdsByClientId[seat.NetcodeClientId] = seat.PlayerId;
                    hostClientIdsByPlayerId[seat.PlayerId] = seat.NetcodeClientId;
                }
            }
        }

        private void RegisterRequestedClientPlayer(ulong clientId, int playerId)
        {
            if (dispatcher == null || networkManager == null || !networkManager.IsServer || playerId <= 0)
            {
                return;
            }

            if (!IsSeatPlayer(playerId) || playerId == localPlayerId)
            {
                UnityEngine.Debug.LogWarning("Rejected network player mapping request: clientId=" + clientId + ", playerId=" + playerId);
                return;
            }

            int existingPlayerId;
            if (hostPlayerIdsByClientId.TryGetValue(clientId, out existingPlayerId))
            {
                if (existingPlayerId != playerId)
                {
                    UnityEngine.Debug.LogWarning("Rejected network player remap request: clientId=" + clientId + ", existingPlayerId=" + existingPlayerId + ", requestedPlayerId=" + playerId);
                }

                return;
            }

            ulong existingClientId;
            if (hostClientIdsByPlayerId.TryGetValue(playerId, out existingClientId) && existingClientId != clientId)
            {
                UnityEngine.Debug.LogWarning("Rejected duplicate network player mapping request: playerId=" + playerId + ", existingClientId=" + existingClientId + ", requestedClientId=" + clientId);
                return;
            }

            dispatcher.RegisterClientPlayer(clientId, playerId);
            hostPlayerIdsByClientId[clientId] = playerId;
            hostClientIdsByPlayerId[playerId] = clientId;
            UnityEngine.Debug.Log("Registered network player mapping: clientId=" + clientId + ", playerId=" + playerId);
        }

        private bool IsSeatPlayer(int playerId)
        {
            for (var i = 0; i < hostSeats.Count; i++)
            {
                if (hostSeats[i].PlayerId == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnSubmitMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (dispatcher == null)
            {
                return;
            }

            var dto = ReadJson<GameCommandDto>(reader);
            RegisterRequestedClientPlayer(senderClientId, dto == null ? -1 : dto.PlayerId);
            dispatcher.ReceiveClientCommand(senderClientId, dto);
        }

        private void OnInitialStateRequestMessage(ulong senderClientId, FastBufferReader reader)
        {
            var request = ReadJson<InitialStateRequestDto>(reader);
            RegisterRequestedClientPlayer(senderClientId, request == null ? -1 : request.PlayerId);
            SendInitialStateToClient(senderClientId);
        }

        private void OnInitialStateMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (dispatcher == null)
            {
                return;
            }

            var snapshot = ReadJson<InitialGameStateDto>(reader);
            var result = dispatcher.ApplyInitialStateSynchronization(snapshot);
            if (!result.Succeeded)
            {
                UnityEngine.Debug.LogWarning("Failed to apply initial game state: " + result.Validation.Reason);
                return;
            }

            var handler = InitialStateApplied;
            if (handler != null)
            {
                handler(snapshot);
            }
        }

        private void OnAcceptedMessage(ulong senderClientId, FastBufferReader reader)
        {
            if (dispatcher == null)
            {
                return;
            }

            var confirmed = ReadJson<ConfirmedGameCommandDto>(reader);
            var result = dispatcher.ApplyConfirmedCommand(confirmed);
            if (!result.Succeeded)
            {
                UnityEngine.Debug.LogWarning("Failed to apply confirmed command: " + result.Validation.Reason);
                return;
            }

            var handler = ConfirmedCommandApplied;
            if (handler != null)
            {
                handler(confirmed);
            }
        }

        private void OnRejectedMessage(ulong senderClientId, FastBufferReader reader)
        {
            var rejected = ReadJson<RejectedGameCommandDto>(reader);
            var handler = CommandRejected;
            if (handler != null)
            {
                handler(rejected);
            }
        }

        private void SendMessageToHost(GameCommandDto dto)
        {
            var json = JsonUtility.ToJson(dto);
            using (var writer = CreateWriter(json))
            {
                networkManager.CustomMessagingManager.SendNamedMessage(
                    SubmitMessageName,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
        }

        private void RequestInitialState()
        {
            if (networkManager == null || !networkManager.IsClient || networkManager.IsServer)
            {
                return;
            }

            nextInitialStateRequestTime = Time.unscaledTime + InitialStateRequestRetrySeconds;

            var request = new InitialStateRequestDto
            {
                PlayerId = localPlayerId
            };

            using (var writer = CreateWriter(JsonUtility.ToJson(request)))
            {
                networkManager.CustomMessagingManager.SendNamedMessage(
                    InitialStateRequestMessageName,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
        }

        private void BroadcastInitialState()
        {
            if (networkManager == null || !networkManager.IsServer)
            {
                return;
            }

            clientBroadcastTargets.Clear();
            for (var i = 0; i < networkManager.ConnectedClientsIds.Count; i++)
            {
                var clientId = networkManager.ConnectedClientsIds[i];
                if (clientId != networkManager.LocalClientId)
                {
                    clientBroadcastTargets.Add(clientId);
                }
            }

            for (var i = 0; i < clientBroadcastTargets.Count; i++)
            {
                SendInitialStateToClient(clientBroadcastTargets[i]);
            }
        }

        private void SendInitialStateToClient(ulong clientId)
        {
            if (dispatcher == null || networkManager == null || !networkManager.IsServer || clientId == networkManager.LocalClientId)
            {
                return;
            }

            var json = JsonUtility.ToJson(dispatcher.CreateInitialStateSynchronization());
            using (var writer = CreateWriter(json))
            {
                networkManager.CustomMessagingManager.SendNamedMessage(
                    InitialStateMessageName,
                    clientId,
                    writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void BroadcastAcceptedCommand(ConfirmedGameCommandDto confirmed)
        {
            RaiseConfirmedCommandApplied(confirmed);

            if (networkManager == null || !networkManager.IsServer)
            {
                return;
            }

            clientBroadcastTargets.Clear();
            for (var i = 0; i < networkManager.ConnectedClientsIds.Count; i++)
            {
                var clientId = networkManager.ConnectedClientsIds[i];
                if (clientId != networkManager.LocalClientId)
                {
                    clientBroadcastTargets.Add(clientId);
                }
            }

            if (clientBroadcastTargets.Count == 0)
            {
                return;
            }

            var json = JsonUtility.ToJson(confirmed);
            using (var writer = CreateWriter(json))
            {
                networkManager.CustomMessagingManager.SendNamedMessage(
                    AcceptedMessageName,
                    clientBroadcastTargets,
                    writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void RaiseConfirmedCommandApplied(ConfirmedGameCommandDto confirmed)
        {
            var handler = ConfirmedCommandApplied;
            if (handler != null)
            {
                handler(confirmed);
            }
        }

        private void SendRejectedCommand(ulong recipientClientId, RejectedGameCommandDto rejected)
        {
            if (networkManager == null || !networkManager.IsServer)
            {
                return;
            }

            if (recipientClientId == networkManager.LocalClientId)
            {
                var localHandler = CommandRejected;
                if (localHandler != null)
                {
                    localHandler(rejected);
                }

                return;
            }

            var json = JsonUtility.ToJson(rejected);
            using (var writer = CreateWriter(json))
            {
                networkManager.CustomMessagingManager.SendNamedMessage(
                    RejectedMessageName,
                    recipientClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
        }

        private static FastBufferWriter CreateWriter(string json)
        {
            var capacity = Math.Max(128, json.Length * 2 + 16);
            var writer = new FastBufferWriter(capacity, Allocator.Temp, capacity);
            writer.WriteValueSafe(json);
            return writer;
        }

        private static T ReadJson<T>(FastBufferReader reader)
        {
            string json;
            reader.ReadValueSafe(out json);
            return JsonUtility.FromJson<T>(json);
        }

        [Serializable]
        private sealed class InitialStateRequestDto
        {
            public int PlayerId;
        }
    }
}
