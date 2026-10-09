using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    public struct SubmitCommandMessage : NetworkMessage { public string Json; }
    public struct RejectedCommandMessage : NetworkMessage { public string Json; }
    public struct InitialStateRequestMessage : NetworkMessage { public int RequestId; }
    public struct StateSnapshotAcknowledgmentMessage : NetworkMessage { public int TransferId; public int NextOffset; }
    public struct StateSnapshotChunkMessage : NetworkMessage
    {
        public int TransferId;
        public bool IsInitial;
        public string ProtocolVersion;
        public int TotalBytes;
        public int Offset;
        public byte[] Payload;
    }
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
        private readonly Dictionary<int, StateViewHistoryTracker> sentHistory = new Dictionary<int, StateViewHistoryTracker>();
        private readonly StateViewHistoryTracker receivedHistory = new StateViewHistoryTracker();
        private readonly SnapshotChunkBuffer snapshotBuffer = new SnapshotChunkBuffer();
        private readonly Dictionary<int, SnapshotSendQueue> snapshotSendQueues = new Dictionary<int, SnapshotSendQueue>();
        private readonly Dictionary<int, int> initialRequestIds = new Dictionary<int, int>();
        private int nextTransferId = 1;
        private int initialStateRequestId = 1;
        private int latestReceivedTransferId;
        private int completedTransferId;
        private int completedTransferBytes;
        private bool completedTransferIsInitial;
        private float snapshotReceiveDeadline;
        private const float SnapshotProgressTimeout = 10f;
        private const int PreferredChunkBytes = 16 * 1024;

        [Serializable]
        private sealed class SnapshotPayload
        {
            // 初始快照使用下一确认序号，命令快照使用本次确认序号。
            public int Sequence;
            public GameCommandDto Command;
            public GameStateView View;
            public StateViewHistoryDelta History;
        }

        private sealed class PendingSnapshot
        {
            public int TransferId;
            public bool IsInitial;
            public byte[] Bytes;
            public SnapshotSendWindow Window;
        }

        private sealed class SnapshotSendQueue
        {
            public NetworkConnectionToClient Connection;
            public readonly Queue<PendingSnapshot> Pending = new Queue<PendingSnapshot>();
        }

        public int LastFullSnapshotJsonBytes { get; private set; }
        public int LastSnapshotJsonBytes { get; private set; }
        public int LastSnapshotMessageBytes { get; private set; }
        public int LastSnapshotLargestMessageBytes { get; private set; }
        public int LastSnapshotChunkCount { get; private set; }

        public event Action<ConfirmedGameStateViewDto> ConfirmedStateViewApplied;
        public event Action<InitialGameStateViewDto> InitialStateViewApplied;
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
                NetworkServer.RegisterHandler<StateSnapshotAcknowledgmentMessage>(OnSnapshotAcknowledgment, false);
                foreach (var connection in NetworkServer.connections.Values)
                    OnServerConnected(connection);
            }
            else if (mode == LaunchMode.Client)
            {
                NetworkClient.RegisterHandler<RejectedCommandMessage>(OnRejected, false);
                NetworkClient.RegisterHandler<StateSnapshotChunkMessage>(OnSnapshotChunk, false);
                NetworkClient.OnConnectedEvent += OnLocalClientConnected;
                NetworkClient.OnDisconnectedEvent += OnLocalClientDisconnected;
                awaitingInitialState = true;
                RequestInitialState();
            }
            initialized = true;
        }

        private void Update()
        {
            if (initialized && mode == LaunchMode.Host)
            {
                PumpSnapshotSends(Time.unscaledTime);
                return;
            }
            if (!initialized || mode != LaunchMode.Client || !NetworkClient.isConnected)
                return;
            if (snapshotBuffer.IsReceiving && Time.unscaledTime >= snapshotReceiveDeadline)
            {
                // 保留有效前缀，发送端会从累计确认位置重试；大快照不重新从零开始。
                AcknowledgeSnapshot(snapshotBuffer.TransferId, snapshotBuffer.ReceivedBytes);
                snapshotReceiveDeadline = Time.unscaledTime + SnapshotProgressTimeout;
            }
            if (!awaitingInitialState && !string.IsNullOrEmpty(pendingCommandId) &&
                !snapshotBuffer.IsReceiving && Time.unscaledTime >= commandConfirmationDeadline)
            {
                // 只补同步状态，不重发原命令，避免重复支付或重复结算。
                BeginStateResynchronization("命令确认超时。");
                return;
            }
            if (!awaitingInitialState || snapshotBuffer.IsReceiving || Time.unscaledTime < nextInitialStateRequestTime) return;
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
            NetworkClient.OnDisconnectedEvent -= OnLocalClientDisconnected;
            if (NetworkServer.active)
            {
                NetworkServer.UnregisterHandler<SubmitCommandMessage>();
                NetworkServer.UnregisterHandler<InitialStateRequestMessage>();
                NetworkServer.UnregisterHandler<InitialStateAppliedMessage>();
                NetworkServer.UnregisterHandler<StateSnapshotAcknowledgmentMessage>();
            }
            if (NetworkClient.active)
            {
                NetworkClient.UnregisterHandler<RejectedCommandMessage>();
                NetworkClient.UnregisterHandler<StateSnapshotChunkMessage>();
            }
            dispatcher = null;
            session = null;
            initialized = false;
            awaitingInitialState = false;
            pendingCommandId = null;
            localPlayerId = -1;
            launchSeats = null;
            sentHistory.Clear();
            snapshotSendQueues.Clear();
            initialRequestIds.Clear();
            receivedHistory.Reset();
            snapshotBuffer.Reset();
            nextTransferId = 1;
            initialStateRequestId = 1;
            latestReceivedTransferId = 0;
            completedTransferId = 0;
            completedTransferBytes = 0;
            nextInitialStateRequestTime = 0f;
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
            sentHistory.Remove(connection.connectionId);
            snapshotSendQueues.Remove(connection.connectionId);
            initialRequestIds.Remove(connection.connectionId);
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
            if (!bindings.TryGetPlayer(connection.connectionId, out _) || message.RequestId <= 0) return;
            if (initialRequestIds.TryGetValue(connection.connectionId, out var previousId) &&
                message.RequestId <= previousId) return;
            initialRequestIds[connection.connectionId] = message.RequestId;
            SendInitialState(connection);
        }

        private void OnSnapshotAcknowledgment(NetworkConnectionToClient connection, StateSnapshotAcknowledgmentMessage message)
        {
            if (!snapshotSendQueues.TryGetValue(connection.connectionId, out var queue) ||
                queue.Pending.Count == 0) return;
            PendingSnapshot snapshot = queue.Pending.Peek();
            if (snapshot.TransferId != message.TransferId ||
                !snapshot.Window.TryAcknowledge(message.NextOffset, Time.unscaledTime)) return;
            if (!snapshot.Window.IsComplete) return;
            // 末块只在客户端完整应用后确认，也可补偿单独就绪通知的丢失。
            if (snapshot.IsInitial && bindings.TryGetPlayer(connection.connectionId, out var playerId))
                MarkGameStateSynchronized(playerId, true);
            queue.Pending.Dequeue();
        }

        private void OnInitialStateApplied(NetworkConnectionToClient connection, InitialStateAppliedMessage message)
        {
            if (!bindings.IsCommandOwner(connection.connectionId, message.PlayerId)) return;
            MarkGameStateSynchronized(message.PlayerId, true);
        }

        private void SendInitialState(NetworkConnectionToClient connection)
        {
            int playerId;
            if (!bindings.TryGetPlayer(connection.connectionId, out playerId)) return;
            InitialGameStateViewDto view = dispatcher.CreateInitialStateViewSynchronization(playerId);
            SendSnapshot(connection, playerId, new SnapshotPayload
            {
                Sequence = view.NextConfirmedSequence,
                View = view.View
            }, true, JsonUtility.ToJson(view));
        }

        private void BroadcastAccepted(ConfirmedGameCommandDto confirmed)
        {
            foreach (var connection in NetworkServer.connections.Values)
            {
                if (connection is LocalConnectionToClient) continue;
                int playerId;
                if (!bindings.TryGetPlayer(connection.connectionId, out playerId)) continue;
                // 每个连接单独投影；绝不先序列化 confirmed.State 再由客户端隐藏。
                ConfirmedGameStateViewDto view = dispatcher.CreateConfirmedStateViewSynchronization(
                    confirmed,
                    GameStateViewer.Player(playerId));
                SendSnapshot(connection, playerId, new SnapshotPayload
                {
                    Sequence = view.Sequence,
                    Command = view.Command,
                    View = view.View
                }, false, JsonUtility.ToJson(view));
            }
            if (confirmed != null)
            {
                ConfirmedStateViewApplied?.Invoke(
                    dispatcher.CreateConfirmedStateViewSynchronization(confirmed, GameStateViewer.Host));
            }
        }

        private void SendRejected(ulong recipient, RejectedGameCommandDto rejected)
        {
            if (NetworkServer.connections.TryGetValue((int)recipient, out var connection))
                connection.Send(new RejectedCommandMessage { Json = JsonUtility.ToJson(rejected) });
        }

        private void SendSnapshot(NetworkConnectionToClient connection, int playerId, SnapshotPayload snapshot,
            bool isInitial, string fullViewJson)
        {
            if (isInitial)
            {
                // 新补同步代次取最新完整基线，取消旧排队确认时必须同时重置历史游标。
                snapshotSendQueues.Remove(connection.connectionId);
                sentHistory.Remove(connection.connectionId);
            }
            if (!sentHistory.TryGetValue(connection.connectionId, out var history))
            {
                history = new StateViewHistoryTracker();
                sentHistory.Add(connection.connectionId, history);
            }
            LastFullSnapshotJsonBytes = Encoding.UTF8.GetByteCount(fullViewJson);
            snapshot.History = history.CreateDelta(snapshot.View, isInitial);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(snapshot));
            LastSnapshotJsonBytes = bytes.Length;
            LastSnapshotMessageBytes = 0;
            LastSnapshotLargestMessageBytes = 0;
            LastSnapshotChunkCount = 0;
            if (bytes.Length > SnapshotChunkBuffer.MaxSnapshotBytes)
            {
                sentHistory.Remove(connection.connectionId);
                Debug.LogError("联机快照超过重组容量：" + bytes.Length + " 字节。");
                return;
            }

            var chunk = new StateSnapshotChunkMessage
            {
                TransferId = nextTransferId++,
                IsInitial = isInitial,
                ProtocolVersion = SteamLobbyPolicy.ProtocolVersion,
                TotalBytes = bytes.Length,
                Payload = Array.Empty<byte>()
            };
            int maxMessageBytes = NetworkMessages.MaxMessageSize(Channels.Reliable);
            int chunkBytes;
            using (NetworkWriterPooled writer = NetworkWriterPool.Get())
            {
                NetworkMessages.Pack(chunk, writer);
                // 空数组长度前缀仅占一字节，为实际数组的变长前缀再预留八字节。
                chunkBytes = Math.Min(PreferredChunkBytes, maxMessageBytes - writer.Position - 8);
            }
            if (chunkBytes <= 0)
            {
                sentHistory.Remove(connection.connectionId);
                Debug.LogError("当前传输组件的消息容量不足以发送快照分块。");
                return;
            }
            for (int offset = 0; offset < bytes.Length; offset += chunkBytes)
            {
                int count = Math.Min(chunkBytes, bytes.Length - offset);
                chunk.Offset = offset;
                chunk.Payload = new byte[count];
                Buffer.BlockCopy(bytes, offset, chunk.Payload, 0, count);
                using (NetworkWriterPooled writer = NetworkWriterPool.Get())
                {
                    NetworkMessages.Pack(chunk, writer);
                    LastSnapshotMessageBytes += writer.Position;
                    LastSnapshotLargestMessageBytes = Math.Max(LastSnapshotLargestMessageBytes, writer.Position);
                }
                LastSnapshotChunkCount++;
            }
            if (!snapshotSendQueues.TryGetValue(connection.connectionId, out var queue))
            {
                queue = new SnapshotSendQueue { Connection = connection };
                snapshotSendQueues.Add(connection.connectionId, queue);
            }
            queue.Pending.Enqueue(new PendingSnapshot
            {
                TransferId = chunk.TransferId,
                IsInitial = isInitial,
                Bytes = bytes,
                Window = new SnapshotSendWindow(bytes.Length, chunkBytes)
            });
            if (Debug.isDebugBuild || UnityEngine.Application.isEditor)
                Debug.Log($"[联机快照] 类型={(isInitial ? "初始/补同步" : "确认")} 玩家={playerId} 序号={snapshot.Sequence} " +
                    $"完整JSON={LastFullSnapshotJsonBytes}B 传输JSON={LastSnapshotJsonBytes}B " +
                    $"Mirror消息合计={LastSnapshotMessageBytes}B 单块最大={LastSnapshotLargestMessageBytes}B " +
                    $"消息上限={maxMessageBytes}B 分块={LastSnapshotChunkCount} 未确认窗口={SnapshotSendWindow.MaxInFlightBytes}B " +
                    $"历史重置={snapshot.History.Reset} 事件={snapshot.View.Events.Count} 日志={snapshot.View.Logs.Count}");
        }

        private void PumpSnapshotSends(float now)
        {
            foreach (SnapshotSendQueue queue in snapshotSendQueues.Values)
            {
                if (queue.Pending.Count == 0) continue;
                PendingSnapshot snapshot = queue.Pending.Peek();
                foreach (SnapshotChunkRange range in snapshot.Window.GetChunks(now))
                {
                    var payload = new byte[range.Count];
                    Buffer.BlockCopy(snapshot.Bytes, range.Offset, payload, 0, range.Count);
                    queue.Connection.Send(new StateSnapshotChunkMessage
                    {
                        TransferId = snapshot.TransferId,
                        IsInitial = snapshot.IsInitial,
                        ProtocolVersion = SteamLobbyPolicy.ProtocolVersion,
                        TotalBytes = snapshot.Bytes.Length,
                        Offset = range.Offset,
                        Payload = payload
                    }, Channels.Reliable);
                }
            }
        }

        private void OnSnapshotChunk(StateSnapshotChunkMessage message)
        {
            if (NetworkServer.active || dispatcher == null) return;
            if (message.ProtocolVersion != SteamLobbyPolicy.ProtocolVersion)
            {
                Debug.LogError("对局协议版本不兼容，请所有玩家使用同一构建。");
                NetworkClient.Disconnect();
                return;
            }
            if (message.TransferId == completedTransferId && message.TotalBytes == completedTransferBytes &&
                message.IsInitial == completedTransferIsInitial)
            {
                // 末次确认可能丢失，重传仅补确认，不再次应用历史或触发结算事件。
                AcknowledgeSnapshot(message.TransferId, message.TotalBytes);
                return;
            }
            if (message.TransferId < latestReceivedTransferId) return;
            // 补同步必须建立完整基线，期间跳过排队中的旧确认快照。
            if (awaitingInitialState && !message.IsInitial) return;
            if (message.TransferId > latestReceivedTransferId)
            {
                if (snapshotBuffer.IsReceiving && !message.IsInitial)
                {
                    BeginStateResynchronization("新确认快照早于旧快照完成。");
                    return;
                }
                // 新初始快照可替代被取消的旧传输；旧尾块和旧确认会被传输 ID 过滤。
                snapshotBuffer.Reset();
                latestReceivedTransferId = message.TransferId;
            }
            int previousBytes = snapshotBuffer.ReceivedBytes;
            if (!snapshotBuffer.TryAppend(message.TransferId, message.IsInitial, message.TotalBytes,
                    message.Offset, message.Payload, out var completed))
            {
                BeginStateResynchronization("快照分块不完整或顺序不一致。");
                return;
            }
            if (completed == null)
            {
                if (snapshotBuffer.ReceivedBytes > previousBytes)
                {
                    snapshotReceiveDeadline = Time.unscaledTime + SnapshotProgressTimeout;
                    commandConfirmationDeadline = Time.unscaledTime + CommandConfirmationTimeout;
                }
                AcknowledgeSnapshot(message.TransferId, snapshotBuffer.ReceivedBytes);
                return;
            }
            SnapshotPayload payload;
            try
            {
                payload = JsonUtility.FromJson<SnapshotPayload>(Encoding.UTF8.GetString(completed));
            }
            catch (ArgumentException)
            {
                BeginStateResynchronization("快照数据无法解析。");
                return;
            }
            if (payload?.View == null || payload.History == null)
            {
                BeginStateResynchronization("快照缺少状态或历史序号。");
                return;
            }
            if (message.IsInitial) OnInitialState(payload, message);
            else OnAccepted(payload, message);
        }

        private void OnInitialState(SnapshotPayload payload, StateSnapshotChunkMessage message)
        {
            var snapshot = new InitialGameStateViewDto { NextConfirmedSequence = payload.Sequence, View = payload.View };
            if (snapshot?.View == null || snapshot.View.MapId != session.State.MapId ||
                snapshot.View.Players.Count != session.State.Players.Count)
            {
                Debug.LogError("权威开局地图或人数与当前房间不一致，已拒绝同步。");
                NetworkClient.Disconnect();
                return;
            }
            if (!payload.History.Reset || !receivedHistory.TryRestore(snapshot.View, payload.History))
            {
                BeginStateResynchronization("初始快照的历史序号不一致。");
                return;
            }
            var result = dispatcher.ApplyInitialStateViewSynchronization(snapshot);
            if (result.Succeeded)
            {
                CompleteSnapshotReception(message);
                awaitingInitialState = false;
                pendingCommandId = null;
                MarkGameStateSynchronized(localPlayerId, true);
                if (NetworkClient.isConnected)
                    NetworkClient.Send(new InitialStateAppliedMessage { PlayerId = localPlayerId });
                InitialStateViewApplied?.Invoke(snapshot);
            }
            else BeginStateResynchronization("初始快照无法应用。");
        }

        private void OnAccepted(SnapshotPayload payload, StateSnapshotChunkMessage message)
        {
            var confirmed = new ConfirmedGameStateViewDto
            {
                Sequence = payload.Sequence,
                Command = payload.Command,
                View = payload.View
            };
            if (!receivedHistory.TryRestore(confirmed.View, payload.History))
            {
                BeginStateResynchronization("确认快照的历史序号不一致。");
                return;
            }
            var result = dispatcher.ApplyConfirmedStateViewSynchronization(confirmed);
            if (result.Succeeded)
            {
                CompleteSnapshotReception(message);
                if (confirmed.Command != null && confirmed.Command.CommandId == pendingCommandId)
                    pendingCommandId = null;
                ConfirmedStateViewApplied?.Invoke(confirmed);
            }
            else
            {
                BeginStateResynchronization("确认快照的命令序号不连续。");
            }
        }

        private void AcknowledgeSnapshot(int transferId, int nextOffset)
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new StateSnapshotAcknowledgmentMessage
                {
                    TransferId = transferId,
                    NextOffset = nextOffset
                }, Channels.Reliable);
        }

        private void CompleteSnapshotReception(StateSnapshotChunkMessage message)
        {
            completedTransferId = message.TransferId;
            completedTransferBytes = message.TotalBytes;
            completedTransferIsInitial = message.IsInitial;
            commandConfirmationDeadline = Time.unscaledTime + CommandConfirmationTimeout;
            AcknowledgeSnapshot(message.TransferId, message.TotalBytes);
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
            NetworkClient.Send(new InitialStateRequestMessage { RequestId = initialStateRequestId });
            nextInitialStateRequestTime = Time.unscaledTime + SnapshotProgressTimeout;
        }

        private void OnLocalClientConnected()
        {
            OnLocalClientDisconnected();
            awaitingInitialState = true;
            initialStateRequestId++;
            RequestInitialState();
        }

        private void OnLocalClientDisconnected()
        {
            bool completedSettlement = HasCompletedSettlement;
            snapshotBuffer.Reset();
            receivedHistory.Reset();
            latestReceivedTransferId = 0;
            completedTransferId = 0;
            completedTransferBytes = 0;
            // 已完整应用的终局结果仍应允许安全脱离，避免断线回调顺序影响结果界面。
            awaitingInitialState = !completedSettlement;
            pendingCommandId = null;
            MarkGameStateSynchronized(localPlayerId, false);
        }

        private void BeginStateResynchronization(string reason)
        {
            bool wasAwaiting = awaitingInitialState;
            Debug.LogWarning("正在补同步对局状态：" + reason);
            snapshotBuffer.Reset();
            receivedHistory.Reset();
            awaitingInitialState = true;
            initialStateRequestId++;
            MarkGameStateSynchronized(localPlayerId, false);
            if (!wasAwaiting || Time.unscaledTime >= nextInitialStateRequestTime)
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
