using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    public struct SimulationCheckpointRequestMessage : NetworkMessage
    { public string EnvelopeJson; public int PlayerId; public string ConnectionToken; }
    public struct SimulationCheckpointPartMessage : NetworkMessage
    { public string EnvelopeJson; public int Offset; public byte[] Payload; public string ConnectionToken; }
    public struct SimulationCheckpointInstalledMessage : NetworkMessage
    { public string EnvelopeJson; public int PlayerId; public string ConnectionToken; }
    public struct SimulationCheckpointErrorMessage : NetworkMessage
    { public string EnvelopeJson; public int PlayerId; public string FaultCode; public string ConnectionToken; }
    public struct SimulationProtocolPartMessage : NetworkMessage
    {
        public int ProtocolVersion;
        public long TransferId;
        public string Purpose, GameId, ConnectionToken;
        public long SimulationEpoch;
        public int MembershipVersion, TotalBytes, Offset;
        public byte[] Payload;
    }

    [DefaultExecutionOrder(-23000)]
    public sealed partial class MirrorCommandTransport : MonoBehaviour, INetworkCommandTransport
    {
        private readonly SteamIdentityBindingRegistry bindings = new SteamIdentityBindingRegistry();
        private GameSession session;
        private LaunchMode mode;
        private bool initialized;
        private string clientConnectionToken;
        private const float CheckpointRequestInterval = 5f;
        private int localPlayerId;
        private IList<PlayerSeat> launchSeats;
        private readonly Dictionary<int, float> pendingLocalBindings = new Dictionary<int, float>();
        private SimulationCheckpointChannel checkpointChannel;
        private NetworkConnection checkpointHostConnection;
        private float nextCheckpointRequestTime;
        private bool checkpointInstallationSent;
        private Func<DeterministicSimulationCoordinator> savedCoordinatorFactory;
        private readonly Dictionary<int, string> checkpointConnectionTokens = new Dictionary<int, string>();
        private const int MaximumCheckpointEnvelopeCharacters = 4096;
        private DeterministicSimulationSessionOptions simulationOptions;
        private DeterministicSimulationCoordinator simulationCoordinator;
        private NetworkPerformanceReplayCapture replayCapture;
        private long nextSimulationTransfer;
        private long transportGeneration;
        private bool initialReadyPending;
        private bool recoveryPreparationPending, recoveryCompletionPending;
        private Exception observedExecutionFailure;
        private long observationOrderedThrough;
        private readonly Dictionary<int, ProtocolAssembly> protocolPeers = new Dictionary<int, ProtocolAssembly>();
        private readonly ProtocolAssembly protocolHost = new ProtocolAssembly();
        private SimulationConfirmedRecoveryInstaller recoveryInstaller;
        private SimulationRecoveryNoticeDto recoveryNotice;
        private PreparedSimulationRecovery preparedRecovery;
        private SimulationCheckpointEncodedTransfer recoveryTransfer;
        private SimulationCheckpointTransfer.Assembler recoveryAssembler;
        private SimulationCheckpointEnvelope receivedRecoveryEnvelope;
        private DeterministicSimulationCoordinator retiredSimulation;
        private readonly HashSet<int> recoveryPrepared = new HashSet<int>();
        private readonly HashSet<int> recoveryInstalled = new HashSet<int>();
        private bool recoveryRequested, recoveryFinished, recoveryFailed, recoveryCommitIssued;
        private float nextConfirmedRecoveryRequestTime;
        private bool confirmedRecoveryRequestPending;
        private bool localConnectionRecoveryPending;
        private string localConnectionRecoveryGameId;
        private long localConnectionRecoveryEpoch;
        private int localConnectionRecoveryMembership;
        public string LocalSimulationConnectionRecoveryFailureCode { get; private set; } = string.Empty;
        public bool IsAwaitingLocalSimulationConnectionRecovery => initialized && mode == LaunchMode.Client && localConnectionRecoveryPending && !recoveryFailed &&
            MirrorNetworkRuntime.Instance != null && MirrorNetworkRuntime.Instance.HasClientReconnectIdentity &&
            (MirrorNetworkRuntime.Instance.IsLocalTestMode || MirrorNetworkRuntime.Instance.ClientReconnectGameId == localConnectionRecoveryGameId) &&
            string.IsNullOrEmpty(LocalSimulationConnectionRecoveryFailureCode) && simulationOptions != null &&
            session != null && simulationOptions.GameId == localConnectionRecoveryGameId &&
            simulationOptions.MembershipVersion == localConnectionRecoveryMembership &&
            (simulationOptions.SimulationEpoch == localConnectionRecoveryEpoch || simulationOptions.SimulationEpoch == localConnectionRecoveryEpoch + 1);
        private Action detachSimulationEvents;
        private sealed class ProtocolAssembly
        {
            private readonly GameStateViewTransfer.Assembler assembler = new GameStateViewTransfer.Assembler();
            private SimulationProtocolPartMessage header;
            private bool assembling;
            internal void Reset() { assembling = false; assembler.Reset(); }
            internal bool Append(SimulationProtocolPartMessage part, out string json)
            {
                json = null;
                if (!assembling) { if (part.Offset != 0) return false; header = part; assembling = true; }
                if (header.Purpose != part.Purpose || header.GameId != part.GameId || header.ConnectionToken != part.ConnectionToken ||
                    header.SimulationEpoch != part.SimulationEpoch || header.MembershipVersion != part.MembershipVersion || header.ProtocolVersion != part.ProtocolVersion)
                { Reset(); return false; }
                if (!assembler.TryAppend(part.TransferId, false, part.TotalBytes, part.Offset, part.Payload, out json)) { Reset(); return false; }
                if (json != null) assembling = false;
                return true;
            }
        }

        public event Action<SimulationCheckpointChannelResult> InitialSimulationCheckpointReceived;
        public event Action<int, SimulationCheckpointEnvelope> SimulationCheckpointInstalledByPeer;
        public event Action<string> SimulationCheckpointTransportFailed;
        public event Action<int, string> SimulationCheckpointPeerFailed;
        public event Action<DeterministicSimulationCoordinator> DeterministicSimulationReady;
        public event Action<SimulationCommandAppliedDto> SimulationCommandApplied;
        public event Action<CommandValidationPrefixDto> CommandValidationPrefixAdvanced;
        public event Action<SimulationWindowPermissionDto> OperationWindowPermissionChanged;
        public event Action<SimulationProtocolFaultDto> SimulationProtocolFaulted;
        public event Action<SimulationProposalResultDto> SimulationProposalRejected;
        public event Action<SimulationRecoveryNoticeDto> SimulationRecoveryStarted;
        public event Action<DeterministicSimulationCoordinator> SimulationRecoveryCompleted;
        public bool IsRecoveringConfirmedPrefix => recoveryRequested && !recoveryFinished;
        private DeterministicSimulationCoordinator RecoverySource => !recoveryRequested ? null :
            recoveryInstalled.Contains(localPlayerId) ? retiredSimulation : simulationCoordinator;
        public SimulationProtocolFaultDto ReadRecoverySourceFault() => RecoverySource?.LastFault;
        public IReadOnlyList<OrderedOperationCommandDto> ReadRecoverySourceTail() => RecoverySource?.ReadPendingTail() ?? Array.Empty<OrderedOperationCommandDto>();
        public OperationCommandProposalDto ReadRecoverySourceLocalProposal() => RecoverySource?.ReadPendingLocalProposal();
        public DeterministicSimulationCoordinator SimulationCoordinator => simulationCoordinator;
        internal NetworkExecutionObservation.Context ReadObservationContext(string commandId = null, long sequence = -1) =>
            !NetworkExecutionObservation.Enabled ? default : new NetworkExecutionObservation.Context {
                GameId = simulationOptions?.GameId, Mode = mode.ToString(), PlayerId = localPlayerId,
                SimulationEpoch = simulationOptions?.SimulationEpoch ?? 0, MembershipVersion = simulationOptions?.MembershipVersion ?? 0,
                CommandId = commandId, Sequence = sequence >= 0 ? sequence : simulationCoordinator?.LastAppliedSequence ?? 0,
                Frame = Time.frameCount, Revision = ReadPublishedRevision(), Slot = -1 };
        private int ReadPublishedRevision() => simulationCoordinator != null && simulationCoordinator.TryReadPublishedSnapshot(out var snapshot)
            ? snapshot.StateRevision : 0;
        public static MirrorCommandTransport Instance { get; private set; }
        public bool IsSynchronizing => initialized && (IsAwaitingLocalSimulationConnectionRecovery || IsRecoveringConfirmedPrefix ||
            simulationCoordinator == null || simulationCoordinator.IsStopped || simulationCoordinator.IsRecoveringConfirmedPrefix || simulationCoordinator.HasPendingLocalApplication);
        public bool IsInitialSimulationCheckpointConfigured => checkpointChannel != null;
        public bool HasInstalledInitialSimulationCheckpoint => checkpointChannel != null && checkpointChannel.IsInstalled && !checkpointChannel.IsStopped;
        public bool IsInitialSimulationCheckpointInstalledByPeer(int playerId) => checkpointChannel != null && checkpointChannel.IsPeerInstalled(playerId);
        // 仅供加载页观察；成功接收/安装与规则边界就绪仍由原协议判定。
        public string InitialLoadingGameId => simulationOptions?.GameId;
        public int InitialLoadingReceivedBytes => checkpointChannel?.ReceivedBytes ?? 0;
        public int InitialLoadingTotalBytes => checkpointChannel?.ExpectedBytes ?? 0;
        public bool IsInitialLoadingStateInstalled => mode == LaunchMode.Host
            ? checkpointChannel?.IsHostArchivePrepared == true : HasInstalledInitialSimulationCheckpoint;
        public int InitialLoadingPlayerCount => simulationOptions?.FrozenPlayerIds?.Count ?? 0;
        public int InitialLoadingReadyPlayerCount => simulationOptions == null ? 0 :
            simulationOptions.FrozenPlayerIds.Count(id => id == localPlayerId
                ? IsInitialLoadingStateInstalled : checkpointChannel?.IsPeerInstalled(id) == true);
        public string InitialLoadingFailure { get; private set; }

        public void InitializeDeterministicSimulation(GameSession session, LaunchMode launchMode, int playerId, IList<PlayerSeat> seats,
            DeterministicSimulationSessionOptions options, InitialSimulationCheckpointInstaller installer, Func<string> createHostArchive)
            => InitializeDeterministicSimulation(session, launchMode, playerId, seats, options, installer, createHostArchive, null, null);

        public void InitializeDeterministicSimulation(GameSession session, LaunchMode launchMode, int playerId, IList<PlayerSeat> seats,
            DeterministicSimulationSessionOptions options, ISimulationCheckpointInstaller installer, Func<string> createHostArchive,
            SimulationCheckpointEnvelope savedIdentity, Func<DeterministicSimulationCoordinator> savedCoordinator)
        {
            if (session == null || options == null || seats == null || seats.Any(seat => seat == null) ||
                launchMode != LaunchMode.Host && launchMode != LaunchMode.Client || options.IsHost != (launchMode == LaunchMode.Host) || options.LocalPlayerId != playerId ||
                options.GameId != session.State.GameId || options.ContentPackHash != session.State.ContentPackHash ||
                (savedIdentity == null ? options.SimulationEpoch != 1 || options.MembershipVersion != 1 || options.CheckpointGlobalSequence != 0 ||
                    options.CheckpointPrefixDigest != OperationCommandCanonicalizer.EmptyPrefixDigest || savedCoordinator != null :
                    !savedIdentity.HasValidIdentity() || savedIdentity.Purpose != SimulationCheckpointEnvelope.SavedSimulation || savedCoordinator == null ||
                    savedIdentity.GameId != options.GameId || savedIdentity.ContentPackHash != options.ContentPackHash ||
                    savedIdentity.SimulationEpoch != options.SimulationEpoch || savedIdentity.MembershipVersion != options.MembershipVersion ||
                    savedIdentity.GlobalCommandSequence != options.CheckpointGlobalSequence) || options.FrozenPlayerIds == null ||
                !seats.Select(seat => seat.PlayerId).OrderBy(id => id).SequenceEqual(options.FrozenPlayerIds.OrderBy(id => id)) ||
                launchMode == LaunchMode.Host && (installer != null || createHostArchive == null) || launchMode == LaunchMode.Client && (installer == null || createHostArchive != null))
                throw new ArgumentException("simulation_transport_configuration_invalid");
            // 构造仅核配置，不 Begin/Submit/投影；通过校验后再注册确定性模拟通道。
            var prepared = savedCoordinator != null && launchMode == LaunchMode.Host ? savedCoordinator() : new DeterministicSimulationCoordinator(session, options);
            var identity = savedIdentity?.Snapshot() ?? new SimulationCheckpointEnvelope { GameId = options.GameId, ContentPackHash = options.ContentPackHash,
                SimulationEpoch = 1, MembershipVersion = 1, GlobalCommandSequence = 0,
                CheckpointId = "initial." + SimulationIdentityHash(JsonConvert.SerializeObject(new {
                    options.GameId, options.ContentPackHash, options.SimulationEpoch, options.MembershipVersion,
                    RequestContractVersion = OperationCommandProtocol.RequestContractVersion, WindowExecutionVersion = WindowExecutionGrant.Version, Players = options.FrozenPlayerIds.OrderBy(id => id).ToArray() })) };
            identity.RequestContractVersion = OperationCommandProtocol.RequestContractVersion;
            identity.WindowExecutionVersion = WindowExecutionGrant.Version;
            identity.ProtocolVersion = savedIdentity == null ? SimulationCheckpointEnvelope.WindowInitialProtocolVersion : SimulationCheckpointEnvelope.WindowSavedProtocolVersion;
            var replay = NetworkExecutionObservation.CreateReplayCapture(options);
            var channel = new SimulationCheckpointChannel(identity, replay == null || installer == null ? installer : replay.WrapInstaller(installer),
                replay == null || createHostArchive == null ? createHostArchive : replay.WrapInitialFactory(createHostArchive), options.FrozenPlayerIds);
            Shutdown();
            replayCapture = replay;
            savedCoordinatorFactory = savedCoordinator;
            this.session = session; mode = launchMode; localPlayerId = playerId; launchSeats = seats;
            simulationOptions = options.Snapshot(); checkpointChannel = channel; initialized = true;
            foreach (var seat in seats) seat.GameStateSynchronized = false;
            if (mode == LaunchMode.Host)
            {
                bindings.ReplaceLobbySeats(seats.Select(seat => new KeyValuePair<int, ulong>(seat.PlayerId, seat.SteamId)));
                NetworkServer.OnConnectedEvent += OnServerConnected; NetworkServer.OnDisconnectedEvent += OnServerDisconnected;
                NetworkServer.RegisterHandler<SimulationCheckpointRequestMessage>(OnCheckpointRequest, false);
                NetworkServer.RegisterHandler<SimulationCheckpointInstalledMessage>(OnCheckpointInstalled, false);
                NetworkServer.RegisterHandler<SimulationCheckpointErrorMessage>(OnCheckpointPeerError, false);
                NetworkServer.RegisterHandler<SimulationProtocolPartMessage>(OnSimulationClientPart, false);
                // 身份校验可能同步断开连接，不能遍历会被断开回调修改的实时字典。
                foreach (var connection in NetworkServer.connections.Values.ToArray()) OnServerConnected(connection);
                if (!channel.TryPrepareHostArchive(out var fault)) { PublishCheckpointFault(fault); return; }
                replayCapture?.CaptureInitial(channel.Identity);
                AttachSimulation(prepared);
                prepared.Begin();
                if (savedIdentity != null) prepared.CompleteConfirmedRecovery(true);
            }
            else
            {
                RegisterClientMessages();
                if (MirrorNetworkRuntime.Instance != null)
                {
                    MirrorNetworkRuntime.Instance.LocalClientRestarted += RegisterClientMessages;
                    MirrorNetworkRuntime.Instance.LocalClientReconnectFailed += OnLocalClientReconnectFailed;
                }
                RequestCheckpoint();
            }
        }

        public bool HasCompletedSettlement => initialized && simulationCoordinator != null &&
            simulationCoordinator.TryReadPublishedSnapshot(out var snapshot) && snapshot.FinalScoringResolved &&
            (snapshot.Phase == GamePhase.FinalScoring || snapshot.Phase == GamePhase.GameOver);

        public bool CanDetachCompletedSession
        {
            get
            {
                if (!HasCompletedSettlement) return false;
                if (simulationOptions != null && (simulationCoordinator == null || simulationCoordinator.ValidatedThroughSequence < simulationCoordinator.LastAppliedSequence ||
                    !simulationCoordinator.IsBoundaryPermitted(simulationCoordinator.LastAppliedSequence))) return false;
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

        private void Update()
        {
            if (!initialized || checkpointChannel == null || !IsCheckpointSessionCurrent()) return;
            if (mode == LaunchMode.Host) RefreshPendingLocalBindings();
            try
            {
                using (simulationCoordinator?.BeginMainThreadWaitStage() ?? default)
                    simulationCoordinator?.DrainMainThreadNotifications(simulationOptions.PumpCommandsPerFrame);
                if (Time.unscaledTime >= nextWindowExecutionRetry)
                { nextWindowExecutionRetry = Time.unscaledTime + 1f; simulationCoordinator?.RetryWindowExecutionReplication(); }
                PublishSimulationReady();
                PublishRecoveryCompleted();
                ObserveBackgroundFailure();
            }
            catch (Exception error)
            {
                // 已提交的原结果不可因消费者刷新异常被重新执行；记录主线程消费故障。
                Debug.LogException(error, this);
            }
            using (NetworkExecutionObservation.Begin("recovery.poll", ReadObservationContext(), false)) PollConfirmedRecovery();
            if (mode == LaunchMode.Client && NetworkClient.isConnected && IsRecoveringConfirmedPrefix && !recoveryFailed && confirmedRecoveryRequestPending)
                RequestConfirmedRecovery();
            if (mode == LaunchMode.Client && NetworkClient.isConnected && !checkpointInstallationSent && !checkpointChannel.IsStopped)
                RequestCheckpoint();
        }

        public CommandResult SubmitOrSend(GameCommand command, out bool appliedLocally)
        {
            appliedLocally = false;
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (simulationOptions != null)
            {
                if (simulationCoordinator == null || mode == LaunchMode.Client && !NetworkClient.isConnected) return Invalid("完整模拟尚未就绪。");
                if (simulationCoordinator.TrySubmitWindowExecution(command, out var localResult)) return localResult;
                if (!simulationCoordinator.TryCreateLocalProposal(command, out var proposal, out var validation)) return CommandResult.Invalid(validation);
                if (mode == LaunchMode.Host)
                {
                    using var admission = NetworkExecutionObservation.Begin("simulation.local_admission", ReadObservationContext(command.CommandId), true, session.ExecutionTimings);
                    var result = simulationCoordinator.AdmitProposal(localPlayerId, proposal); simulationCoordinator.ReceiveProposalResult(result);
                    if (result.Status != OperationAdmissionStatus.Accepted && result.Status != OperationAdmissionStatus.Duplicate &&
                        result.Status != OperationAdmissionStatus.PendingValidation) return CommandResult.Invalid(new ValidationResult(false, result.ErrorCode, result.Reason));
                }
                else SendSimulationClient("proposal", proposal);
                return CommandResult.SuccessResult(new List<GameEvent>(), "命令已进入编序队列，等待本地执行。");
            }
            return Invalid("确定性模拟传输尚未初始化。");
        }

        public void Shutdown()
        {
            InitialLoadingFailure = null;
            NetworkExecutionObservation.Event("simulation.shutdown", ReadObservationContext());
            transportGeneration++; recoveryPreparationPending = recoveryCompletionPending = false;
            DetachSimulation(); simulationOptions = null; protocolPeers.Clear(); protocolHost.Reset(); nextSimulationTransfer = 0;
            recoveryInstaller = null; recoveryNotice = null; preparedRecovery = null; recoveryTransfer = null; recoveryAssembler = null; receivedRecoveryEnvelope = null; retiredSimulation = null;
            recoveryPrepared.Clear(); recoveryInstalled.Clear(); recoveryRequested = recoveryFinished = recoveryFailed = recoveryCommitIssued = false;
            localConnectionRecoveryPending = false; localConnectionRecoveryGameId = null; localConnectionRecoveryEpoch = 0; localConnectionRecoveryMembership = 0;
            LocalSimulationConnectionRecoveryFailureCode = string.Empty; nextConfirmedRecoveryRequestTime = 0f;
            confirmedRecoveryRequestPending = false;
            checkpointChannel?.Stop(); checkpointChannel = null; checkpointHostConnection = null;
            checkpointConnectionTokens.Clear(); nextCheckpointRequestTime = 0f;
            checkpointInstallationSent = false;
            savedCoordinatorFactory = null;
            pendingLocalBindings.Clear();
            // 房间关闭后连接号会重用；相同 Steam 席位也不能沿用上一局的连接绑定。
            bindings.Clear();
            if (MirrorNetworkRuntime.Instance != null)
            {
                MirrorNetworkRuntime.Instance.LocalClientRestarted -= RegisterClientMessages;
                MirrorNetworkRuntime.Instance.LocalClientReconnectFailed -= OnLocalClientReconnectFailed;
            }
            NetworkServer.OnConnectedEvent -= OnServerConnected;
            NetworkServer.OnDisconnectedEvent -= OnServerDisconnected;
            NetworkClient.OnConnectedEvent -= OnLocalClientConnected;
            NetworkClient.OnDisconnectedEvent -= OnCheckpointClientDisconnected;
            if (NetworkServer.active)
            {
                NetworkServer.UnregisterHandler<SimulationCheckpointRequestMessage>();
                NetworkServer.UnregisterHandler<SimulationCheckpointInstalledMessage>();
                NetworkServer.UnregisterHandler<SimulationCheckpointErrorMessage>();
                NetworkServer.UnregisterHandler<SimulationProtocolPartMessage>();
            }
            if (NetworkClient.active)
            {
                NetworkClient.UnregisterHandler<SimulationCheckpointPartMessage>();
                NetworkClient.UnregisterHandler<SimulationCheckpointErrorMessage>();
                NetworkClient.UnregisterHandler<SimulationProtocolPartMessage>();
            }
            replayCapture?.CompleteAfter(simulationCoordinator?.ExecutionStopped ?? System.Threading.Tasks.Task.CompletedTask, "transport_shutdown");
            replayCapture = null;
            session = null;
            initialized = false;
            clientConnectionToken = null;
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
                if (!TryGetExpectedIdentity(connection.connectionId, out identity) ||
                    !RoomReadinessPolicy.TryResolveExpectedBinding(launchSeats, connection.connectionId, identity, out _))
                {
                    // 新连接必须先用仍存活的房间身份票据认证；连接号不是玩家身份。
                    if (!(connection is LocalConnectionToClient) && !pendingLocalBindings.ContainsKey(connection.connectionId))
                    {
                        pendingLocalBindings.Add(connection.connectionId, Time.unscaledTime + 60f);
                        Debug.Log("[LocalMirrorLifecycle] 本地连接等待原房间身份认证；连接=" + connection.connectionId);
                    }
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
                    out var expectedPlayerId))
            {
                if (!(connection is LocalConnectionToClient) && simulationOptions != null && launchSeats != null &&
                    launchSeats.Any(seat => seat != null && seat.SteamId == identity && simulationOptions.FrozenPlayerIds.Contains(seat.PlayerId)))
                {
                    if (!pendingLocalBindings.ContainsKey(connection.connectionId))
                    {
                        pendingLocalBindings.Add(connection.connectionId, Time.unscaledTime + 60f);
                        Debug.Log("[ClientReconnectLifecycle] 原冻结身份等待房间认证更新；连接=" + connection.connectionId);
                    }
                    return;
                }
                RejectConnection(connection, "拒绝与等待房间身份映射不一致的连接：" + connection.address);
                return;
            }
            if (!bindings.TryBindConnection(connection.connectionId, identity, out var playerId) ||
                playerId != expectedPlayerId)
            {
                RejectConnection(connection, "拒绝与等待房间身份映射不一致的连接：" + connection.address);
                return;
            }
            if (connection is LocalConnectionToClient)
            {
                MarkGameStateSynchronized(playerId, true);
                return;
            }
        }

        private void OnServerDisconnected(NetworkConnectionToClient connection)
        {
            if (connection == null) return;
            pendingLocalBindings.Remove(connection.connectionId);
            if (bindings.TryGetPlayer(connection.connectionId, out var playerId))
            {
                MarkGameStateSynchronized(playerId, false);
                checkpointChannel?.ForgetPeer(playerId);
                recoveryPrepared.Remove(playerId); recoveryInstalled.Remove(playerId);
            }
            checkpointConnectionTokens.Remove(connection.connectionId);
            protocolPeers.Remove(connection.connectionId);
            if (simulationCoordinator != null && playerId > 0)
            {
                simulationCoordinator.PeerDisconnected(playerId);
                if (simulationOptions.FrozenPlayerIds.Contains(playerId) && !HasCompletedSettlement)
                {
                    simulationCoordinator.BeginConfirmedRecovery();
                    Debug.LogWarning("[LocalMirrorLifecycle] 规定成员断开，冻结原窗口许可；玩家=" + playerId + "，局=" + simulationOptions.GameId + "，代次=" + simulationOptions.SimulationEpoch);
                }
            }
            bindings.UnbindConnection(connection.connectionId);
        }

        private static float ReadUnscaledTime() => Time.unscaledTime;

        private bool IsCurrentClientConnection(string token) => !string.IsNullOrEmpty(clientConnectionToken) &&
            string.Equals(token, clientConnectionToken, StringComparison.Ordinal);

        private void OnLocalClientConnected()
        {
            if (checkpointChannel == null) return;
            clientConnectionToken = Guid.NewGuid().ToString("N"); nextCheckpointRequestTime = 0f;
            nextConfirmedRecoveryRequestTime = 0f;
            confirmedRecoveryRequestPending = simulationCoordinator != null;
            protocolHost.Reset(); ResetConfirmedRecoveryReceive();
            if (simulationCoordinator != null)
            { checkpointHostConnection = NetworkClient.connection; RequestConfirmedRecovery(); }
            else { BindCheckpointHost(); RequestCheckpoint(); }
        }

        private void RegisterClientMessages()
        {
            if (mode != LaunchMode.Client || checkpointChannel == null) return;
            RegisterCheckpointClientMessages();
            NetworkClient.OnConnectedEvent -= OnLocalClientConnected;
            NetworkClient.OnConnectedEvent += OnLocalClientConnected;
            clientConnectionToken = Guid.NewGuid().ToString("N"); nextCheckpointRequestTime = 0f;
            nextConfirmedRecoveryRequestTime = 0f;
            if (simulationCoordinator != null)
            {
                checkpointHostConnection = NetworkClient.connection; checkpointInstallationSent = false;
                protocolHost.Reset(); ResetConfirmedRecoveryReceive();
                if (NetworkClient.isConnected) OnLocalClientConnected();
            }
            else BindCheckpointHost();
        }

        private void RegisterCheckpointClientMessages()
        {
            NetworkClient.RegisterHandler<SimulationCheckpointPartMessage>(OnCheckpointPart, false);
            NetworkClient.RegisterHandler<SimulationCheckpointErrorMessage>(OnCheckpointError, false);
            NetworkClient.OnDisconnectedEvent -= OnCheckpointClientDisconnected;
            NetworkClient.OnDisconnectedEvent += OnCheckpointClientDisconnected;
            if (simulationOptions != null) NetworkClient.RegisterHandler<SimulationProtocolPartMessage>(OnSimulationHostPart, false);
        }

        private void BindCheckpointHost()
        {
            if (checkpointChannel == null || mode != LaunchMode.Client || checkpointChannel.IsStopped) return;
            checkpointHostConnection = NetworkClient.connection; checkpointInstallationSent = false;
            checkpointChannel.BindClientConnection(clientConnectionToken);
        }

        private void OnCheckpointClientDisconnected()
        {
            NetworkExecutionObservation.Event("recovery.connection_lost_callback", ReadObservationContext());
            checkpointHostConnection = null; checkpointInstallationSent = false;
            confirmedRecoveryRequestPending = simulationOptions != null && simulationCoordinator != null;
            checkpointChannel?.ResetReceive();
            protocolHost.Reset();
            ResetConfirmedRecoveryReceive();
            if (simulationOptions != null && simulationCoordinator != null)
            {
                TryAcceptLocalSimulationConnectionRecovery();
                ProtocolFault("simulation_connection_lost", "连接断开，需要原日志接续或运行中恢复，不能重新安装初始状态。");
            }
        }

        private void ResetConfirmedRecoveryReceive()
        {
            if (!IsRecoveringConfirmedPrefix || recoveryNotice == null || preparedRecovery != null) return;
            var envelope = RecoveryEnvelope(recoveryNotice); envelope.ContentPackHash = simulationOptions.ContentPackHash;
            recoveryAssembler = new SimulationCheckpointTransfer.Assembler(envelope); receivedRecoveryEnvelope = null;
        }

        private bool TryAcceptLocalSimulationConnectionRecovery()
        {
            var runtime = MirrorNetworkRuntime.Instance;
            var fault = simulationCoordinator?.LastFault;
            if (!initialized || mode != LaunchMode.Client || simulationOptions == null || simulationCoordinator == null || session == null ||
                HasCompletedSettlement || recoveryInstaller == null || !recoveryInstaller.IsBoundTo(session, simulationOptions) || recoveryFailed ||
                runtime == null || NetworkServer.active || !runtime.HasClientReconnectIdentity ||
                !runtime.IsLocalTestMode && runtime.ClientReconnectGameId != simulationOptions.GameId ||
                fault != null && fault.FaultCode != "simulation_connection_lost" ||
                !runtime.TryBeginClientReconnect()) return false;
            if (!localConnectionRecoveryPending)
            {
                localConnectionRecoveryGameId = simulationOptions.GameId; localConnectionRecoveryEpoch = simulationOptions.SimulationEpoch;
                localConnectionRecoveryMembership = simulationOptions.MembershipVersion;
            }
            localConnectionRecoveryPending = true; LocalSimulationConnectionRecoveryFailureCode = string.Empty;
            NetworkExecutionObservation.Event("recovery.connection_wait_accepted", ReadObservationContext());
            Debug.LogWarning("[ClientReconnectLifecycle] 正式会话断线已接纳等待；局=" + localConnectionRecoveryGameId + "，玩家=" + localPlayerId + "，代次=" + localConnectionRecoveryEpoch);
            return true;
        }

        public bool CanAwaitLocalSimulationConnectionRecovery(SimulationProtocolFaultDto fault) => IsAwaitingLocalSimulationConnectionRecovery &&
            fault != null && fault.ProtocolVersion == OperationCommandProtocol.Version && fault.FaultCode == "simulation_connection_lost" && fault.RequiresRecovery &&
            fault.GameId == localConnectionRecoveryGameId && fault.SimulationEpoch == localConnectionRecoveryEpoch && fault.MembershipVersion == localConnectionRecoveryMembership;

        private void OnLocalClientReconnectFailed(string code)
        {
            if (!initialized || !localConnectionRecoveryPending) return;
            localConnectionRecoveryPending = false; LocalSimulationConnectionRecoveryFailureCode = code;
            FailConfirmedRecovery(code, "原身份回连已终止，确认前缀尚未恢复。");
        }

        private bool IsCheckpointSessionCurrent()
        {
            if (checkpointChannel == null || checkpointChannel.IsStopped) return false;
            if (session != null && simulationOptions?.GameId == checkpointChannel.Identity.GameId &&
                simulationOptions.ContentPackHash == checkpointChannel.Identity.ContentPackHash) return true;
            checkpointChannel.Stop(); checkpointHostConnection = null; checkpointConnectionTokens.Clear();
            PublishCheckpointFault("checkpoint_session_changed"); return false;
        }

        private void RequestCheckpoint()
        {
            if (simulationOptions != null && simulationCoordinator != null) return;
            if (!IsCheckpointSessionCurrent() || mode != LaunchMode.Client || !NetworkClient.isConnected || checkpointInstallationSent) return;
            if (checkpointHostConnection == null) BindCheckpointHost();
            if (!ReferenceEquals(checkpointHostConnection, NetworkClient.connection)) return;
            float now = ReadUnscaledTime(); if (now < nextCheckpointRequestTime) return;
            NetworkClient.Send(new SimulationCheckpointRequestMessage { EnvelopeJson = JsonConvert.SerializeObject(checkpointChannel.Identity),
                PlayerId = localPlayerId, ConnectionToken = clientConnectionToken }, Channels.Reliable);
            nextCheckpointRequestTime = now + CheckpointRequestInterval;
        }

        private bool IsBoundCheckpointClient(NetworkConnectionToClient connection, int playerId) =>
            mode == LaunchMode.Host && connection != null && NetworkServer.connections.TryGetValue(connection.connectionId, out var current) &&
            ReferenceEquals(current, connection) && bindings.IsCommandOwner(connection.connectionId, playerId);

        private bool IsBoundCheckpointHost(string token) => mode == LaunchMode.Client && NetworkClient.isConnected && !NetworkServer.active &&
            checkpointHostConnection != null && ReferenceEquals(checkpointHostConnection, NetworkClient.connection) && IsCurrentClientConnection(token);

        private static bool TryReadCheckpointEnvelope(string json, out SimulationCheckpointEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrEmpty(json) || json.Length > MaximumCheckpointEnvelopeCharacters) return false;
            try { envelope = JsonConvert.DeserializeObject<SimulationCheckpointEnvelope>(json); return envelope != null && envelope.HasValidIdentity(); }
            catch (JsonException) { return false; }
        }

        private void OnCheckpointRequest(NetworkConnectionToClient connection, SimulationCheckpointRequestMessage message)
        {
            if (simulationOptions != null && (checkpointChannel == null || simulationOptions.SimulationEpoch != checkpointChannel.Identity.SimulationEpoch || recoveryRequested ||
                simulationCoordinator?.ValidatedThroughSequence > simulationOptions.CheckpointGlobalSequence)) return;
            if (!IsCheckpointSessionCurrent() || !IsBoundCheckpointClient(connection, message.PlayerId) ||
                string.IsNullOrEmpty(message.ConnectionToken) || message.ConnectionToken.Length > 64 ||
                !TryReadCheckpointEnvelope(message.EnvelopeJson, out var request)) return;
            if (!checkpointChannel.TryCreateHostTransfer(message.PlayerId, message.ConnectionToken, request, out var transfer, out var fault))
            {
                connection.Send(new SimulationCheckpointErrorMessage { EnvelopeJson = JsonConvert.SerializeObject(checkpointChannel.Identity),
                    ConnectionToken = message.ConnectionToken, FaultCode = fault.Length <= 512 ? fault : fault.Substring(0, 512) }, Channels.Reliable);
                return;
            }
            checkpointConnectionTokens[connection.connectionId] = message.ConnectionToken;
            string envelopeJson = JsonConvert.SerializeObject(transfer.Envelope);
            foreach (var part in transfer.Split())
                connection.Send(new SimulationCheckpointPartMessage { EnvelopeJson = envelopeJson, Offset = part.Offset,
                    Payload = part.Payload, ConnectionToken = message.ConnectionToken }, Channels.Reliable);
        }

        private void OnCheckpointPart(SimulationCheckpointPartMessage message)
        {
            if (TryReadCheckpointEnvelope(message.EnvelopeJson, out var recoveryEnvelope) && recoveryEnvelope.Purpose == SimulationCheckpointEnvelope.ConfirmedSimulationRecovery)
            { ReceiveConfirmedRecoveryPart(message, recoveryEnvelope); return; }
            if (!IsCheckpointSessionCurrent() || !IsBoundCheckpointHost(message.ConnectionToken)) return;
            if (!TryReadCheckpointEnvelope(message.EnvelopeJson, out var envelope))
            { checkpointChannel.ResetReceive(); PublishCheckpointFault("checkpoint_envelope_invalid", true); return; }
            SimulationCheckpointChannelResult result;
            try { result = checkpointChannel.ReceivePart(message.ConnectionToken, envelope, message.Offset, message.Payload); }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            { checkpointChannel.Stop(); PublishCheckpointFault("checkpoint_install_exception:" + exception.Message, true); return; }
            if (result.Status == SimulationCheckpointReceiveStatus.Buffered) return;
            if (result.Status == SimulationCheckpointReceiveStatus.Installed || result.Status == SimulationCheckpointReceiveStatus.AlreadyInstalled)
            {
                NetworkClient.Send(new SimulationCheckpointInstalledMessage { EnvelopeJson = JsonConvert.SerializeObject(result.Envelope),
                    PlayerId = localPlayerId, ConnectionToken = clientConnectionToken }, Channels.Reliable);
                checkpointInstallationSent = true;
                replayCapture?.CaptureInitial(result.Envelope);
                if (simulationOptions != null && simulationCoordinator == null)
                {
                    var coordinator = savedCoordinatorFactory != null ? savedCoordinatorFactory() : new DeterministicSimulationCoordinator(session, simulationOptions);
                    AttachSimulation(coordinator); coordinator.Begin();
                    if (savedCoordinatorFactory != null) coordinator.CompleteConfirmedRecovery(true);
                }
                if (result.Status == SimulationCheckpointReceiveStatus.Installed)
                {
                    foreach (Action<SimulationCheckpointChannelResult> observer in InitialSimulationCheckpointReceived?.GetInvocationList() ?? Array.Empty<Delegate>())
                        try { observer(result); } catch (Exception exception) { Debug.LogException(exception); }
                }
            }
            else PublishCheckpointFault(result.FaultCode, true);
        }

        private void OnCheckpointInstalled(NetworkConnectionToClient connection, SimulationCheckpointInstalledMessage message)
        {
            if (!IsCheckpointSessionCurrent() || !IsBoundCheckpointClient(connection, message.PlayerId) ||
                !checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token) || token != message.ConnectionToken ||
                !TryReadCheckpointEnvelope(message.EnvelopeJson, out var envelope) ||
                !checkpointChannel.TryConfirmInstalled(message.PlayerId, message.ConnectionToken, envelope, out var duplicate, out _) || duplicate) return;
            foreach (Action<int, SimulationCheckpointEnvelope> observer in SimulationCheckpointInstalledByPeer?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { observer(message.PlayerId, envelope.Snapshot()); } catch (Exception exception) { Debug.LogException(exception); }
            if (simulationCoordinator != null)
            {
                var boundary = simulationCoordinator.ReadBoundary(simulationOptions.CheckpointGlobalSequence);
                if (boundary != null) SendSimulationHost(connection, "boundary", boundary);
            }
        }

        private void OnCheckpointError(SimulationCheckpointErrorMessage message)
        {
            if (!IsCheckpointSessionCurrent() || !IsBoundCheckpointHost(message.ConnectionToken) ||
                !TryReadCheckpointEnvelope(message.EnvelopeJson, out var envelope) ||
                !SimulationCheckpointEnvelope.SameIdentity(checkpointChannel.Identity, envelope) ||
                string.IsNullOrEmpty(message.FaultCode) || message.FaultCode.Length > 512) return;
            checkpointChannel.Stop(); PublishCheckpointFault(message.FaultCode);
        }

        private void OnCheckpointPeerError(NetworkConnectionToClient connection, SimulationCheckpointErrorMessage message)
        {
            if (!IsCheckpointSessionCurrent() || !IsBoundCheckpointClient(connection, message.PlayerId) ||
                !checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token) || token != message.ConnectionToken ||
                !TryReadCheckpointEnvelope(message.EnvelopeJson, out var envelope) ||
                !SimulationCheckpointEnvelope.SameIdentity(checkpointChannel.Identity, envelope) ||
                string.IsNullOrEmpty(message.FaultCode) || message.FaultCode.Length > 512) return;
            checkpointChannel.ForgetPeer(message.PlayerId); checkpointConnectionTokens.Remove(connection.connectionId);
            if (simulationOptions != null) ProtocolFault("checkpoint_peer_failed", "规定副本初始安装失败：" + message.FaultCode);
            foreach (Action<int, string> observer in SimulationCheckpointPeerFailed?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { observer(message.PlayerId, message.FaultCode); } catch (Exception exception) { Debug.LogException(exception); }
        }

        private void PublishCheckpointFault(string fault, bool notifyHost = false)
        {
            InitialLoadingFailure = fault;
            if (simulationOptions != null) ProtocolFault("checkpoint_transport_failed", fault);
            if (notifyHost && checkpointChannel != null && IsBoundCheckpointHost(clientConnectionToken))
                NetworkClient.Send(new SimulationCheckpointErrorMessage { EnvelopeJson = JsonConvert.SerializeObject(checkpointChannel.Identity),
                    PlayerId = localPlayerId, ConnectionToken = clientConnectionToken,
                    FaultCode = fault.Length <= 512 ? fault : fault.Substring(0, 512) }, Channels.Reliable);
            foreach (Action<string> observer in SimulationCheckpointTransportFailed?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { observer(fault); } catch (Exception exception) { Debug.LogException(exception); }
        }

        private static string SimulationIdentityHash(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(new System.Text.UTF8Encoding(false, true).GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        public event Action<SimulationWindowSavePoint> AutomaticSavePointConfirmed;
        private void AttachSimulation(DeterministicSimulationCoordinator coordinator, bool initialReady = true)
        {
            if (NetworkExecutionObservation.Enabled) session.ExecutionTimings.Enabled = true;
            observationOrderedThrough = 0;
            simulationCoordinator = coordinator;
            if (replayCapture != null) coordinator.ConfigurePerformanceReplayCapture(replayCapture.RecordApply, NetworkExecutionObservation.ReplayTargetSequence);
            if (NetworkExecutionObservation.Enabled) coordinator.ConfigureWaitStageObservation(ObserveWaitStage);
            observedExecutionFailure = null;
            initialReadyPending = initialReady;
            Action<SimulationCommandAppliedDto> applied = value => { if (simulationCoordinator == coordinator) OnSimulationApplied(value); };
            Action<SimulationWindowSavePoint> autoSave = value => { if (simulationCoordinator == coordinator && mode == LaunchMode.Host) AutomaticSavePointConfirmed?.Invoke(value); };
            Action<CommandValidationPrefixDto> validated = value => { if (simulationCoordinator == coordinator) OnSimulationValidated(value); };
            Action<SimulationWindowPermissionDto> permission = value => { if (simulationCoordinator == coordinator) OnSimulationPermission(value); };
            Action<SimulationProtocolFaultDto> fault = value => { if (simulationCoordinator == coordinator) OnSimulationFault(value); };
            Action<SimulationProposalResultDto> rejected = value => { if (simulationCoordinator == coordinator) OnSimulationProposalRejected(value); };
            Action<SimulationProposalResultDto> finalAdmission = value => { if (simulationCoordinator == coordinator) SendFinalSimulationProposalResult(value); };
            Action<OrderedOperationCommandDto> ordered = value => { if (simulationCoordinator == coordinator) BroadcastSimulationOrdered(value); };
            Action<SimulationBoundaryDto> boundary = value => { if (simulationCoordinator == coordinator) BroadcastSimulationBoundary(value); };
            Action<SimulationWindowReadyDto> ready = value => { if (simulationCoordinator == coordinator) SendSimulationReady(value); };
            Action<SimulationWindowPermitDto> permit = value => { if (simulationCoordinator == coordinator) BroadcastSimulationPermit(value); };
            Action<SimulationMissingRangeRequestDto> missing = value => { if (simulationCoordinator == coordinator) SendSimulationMissing(value); };
            Action<WindowExecutionGrant> streamGrant = value => { if (simulationCoordinator == coordinator) BroadcastSimulation("stream-grant", value); };
            Action<WindowExecutionRecord> streamRecord = value => { if (simulationCoordinator == coordinator) SendWindowExecution("stream-record", value); };
            Action<WindowExecutionSeal> streamSeal = value => { if (simulationCoordinator == coordinator) SendWindowExecution("stream-seal", value); };
            Action<WindowExecutionReadyMessage> streamReady = value => { if (simulationCoordinator == coordinator) SendSimulationClient("stream-ready", value); };
            Action<WindowExecutionConfirmation> streamConfirmed = value => { if (simulationCoordinator == coordinator) BroadcastSimulation("stream-confirmed", value); };
            Action<WindowExecutionRangeMessage> streamMissing = value => { if (simulationCoordinator == coordinator) SendWindowExecutionRangeControl("stream-missing", value); };
            Action<WindowExecutionRangeMessage> streamRange = value => { if (simulationCoordinator == coordinator) SendWindowExecutionRangeControl("stream-range", value); };
            coordinator.SimulationCommandApplied += applied; coordinator.CommandValidationPrefixAdvanced += validated;
            coordinator.AutomaticSavePointConfirmed += autoSave;
            coordinator.OperationWindowPermissionChanged += permission; coordinator.SimulationProtocolFaulted += fault;
            coordinator.SimulationProposalRejected += rejected; coordinator.OrderedCommandProduced += ordered;
            coordinator.SimulationProposalResultProduced += finalAdmission;
            coordinator.BoundaryProduced += boundary; coordinator.WindowReadyProduced += ready;
            coordinator.WindowPermitProduced += permit; coordinator.MissingRangeRequested += missing;
            coordinator.WindowExecutionGranted += streamGrant; coordinator.WindowExecutionRecordProduced += streamRecord;
            coordinator.WindowExecutionSealProduced += streamSeal; coordinator.WindowExecutionReadyProduced += streamReady;
            coordinator.WindowExecutionConfirmed += streamConfirmed; coordinator.WindowExecutionMissingRequested += streamMissing; coordinator.WindowExecutionRangeProduced += streamRange;
            detachSimulationEvents = () => {
                coordinator.ConfigureWaitStageObservation(null);
                coordinator.ConfigurePerformanceReplayCapture(null);
                coordinator.SimulationCommandApplied -= applied; coordinator.CommandValidationPrefixAdvanced -= validated;
                coordinator.AutomaticSavePointConfirmed -= autoSave;
                coordinator.OperationWindowPermissionChanged -= permission; coordinator.SimulationProtocolFaulted -= fault;
                coordinator.SimulationProposalRejected -= rejected; coordinator.OrderedCommandProduced -= ordered;
                coordinator.SimulationProposalResultProduced -= finalAdmission;
                coordinator.BoundaryProduced -= boundary; coordinator.WindowReadyProduced -= ready;
                coordinator.WindowPermitProduced -= permit; coordinator.MissingRangeRequested -= missing;
                coordinator.WindowExecutionGranted -= streamGrant; coordinator.WindowExecutionRecordProduced -= streamRecord;
                coordinator.WindowExecutionSealProduced -= streamSeal; coordinator.WindowExecutionReadyProduced -= streamReady;
                coordinator.WindowExecutionConfirmed -= streamConfirmed; coordinator.WindowExecutionMissingRequested -= streamMissing; coordinator.WindowExecutionRangeProduced -= streamRange;
            };
            coordinator.ConfigureRequestContractValidation(checkpointChannel.Identity.RequestContractVersion);
            coordinator.ConfigureWindowExecution(checkpointChannel.Identity.WindowExecutionVersion);
            coordinator.StartBackgroundExecution();
        }

        private static void ObserveWaitStage(DeterministicSimulationCoordinator.WaitStageObservation value)
        {
            // worker回调只使用捕获的标量，不能读取Unity Time/主线程投影或序列化状态。
            if (!NetworkExecutionObservation.Enabled) return;
            var context = new NetworkExecutionObservation.Context { GameId = value.GameId,
                Mode = value.IsHost ? "Host" : "Client", CommandId = value.CommandId, PlayerId = value.PlayerId,
                SimulationEpoch = value.SimulationEpoch, MembershipVersion = value.MembershipVersion,
                Sequence = value.Sequence, Frame = -1, Slot = -1 };
            if (value.LoopGeneration != 0)
            {
                NetworkExecutionObservation.Event("wait." + value.Stage + "." + value.Edge, context,
                    "sample_tick=" + value.Tick + ";frequency=" + System.Diagnostics.Stopwatch.Frequency + ";loop=" + value.LoopGeneration +
                    ";work=" + value.WorkId + ";publication=" + value.PublicationVersion + ";commit=" + value.PublicationCommitVersion +
                    ";incoming=" + value.IncomingCount + ";outgoing=" + value.OutgoingCount + ";action_id=" + value.ActionIdentity +
                    ";frame_id=" + value.FrameIdentity + ";snapshot_id=" + value.SnapshotIdentity + ";ui_state_id=" + value.UiStateIdentity +
                    ";ui_view_id=" + value.UiViewIdentity + ";notifications=" + value.NotificationCount + ";players=" + value.PlayerCount +
                    ";wait_count=" + value.WaitCount + ";wait_ticks=" + value.WaitTicks + ";completed=" + value.Completed);
                return;
            }
            NetworkExecutionObservation.Event("wait." + value.Stage + "." + value.Edge, context,
                "tick=" + value.Tick + ";frequency=" + System.Diagnostics.Stopwatch.Frequency + ";worker=" + (value.IsWorker ? 1 : 0) +
                ";ordered_queue=" + value.OrderedQueueCount + ";known=" + value.KnownCount + ";pending_admissions=" + value.PendingAdmissions +
                ";published_version=" + value.PublishedVersion + ";published_sequence=" + value.PublishedSequence +
                ";visible_version=" + value.VisibleVersion + ";visible_sequence=" + value.VisibleSequence);
        }

        private void PublishSimulationReady()
        {
            if (!initialReadyPending || simulationCoordinator == null ||
                !simulationCoordinator.TryReadPublishedSnapshot(out var snapshot) || snapshot.GameId != simulationOptions.GameId) return;
            initialReadyPending = false;
            DeterministicSimulationReady?.Invoke(simulationCoordinator);
        }

        private void ObserveBackgroundFailure()
        {
            var failure = simulationCoordinator?.BackgroundExecutionException;
            if (failure == null || ReferenceEquals(failure, observedExecutionFailure)) return;
            observedExecutionFailure = failure;
            Debug.LogException(failure, this);
            // 若连故障快照也未能发表，仍交付准确的执行故障，不能静默停在未Ready。
            if (simulationCoordinator.LastFault == null)
                OnSimulationFault(new SimulationProtocolFaultDto { GameId = simulationOptions.GameId,
                    SimulationEpoch = simulationOptions.SimulationEpoch, MembershipVersion = simulationOptions.MembershipVersion,
                    FaultCode = "background_execution_exception", Diagnostic = failure.Message,
                    ErrorCode = CommandErrorCode.UnknownCommand, RequiresRecovery = true });
        }

        private void PublishRecoveryCompleted()
        {
            if (!recoveryCompletionPending || simulationCoordinator == null ||
                !simulationCoordinator.TryReadPublishedSnapshot(out var snapshot) || snapshot.IsRecoveringConfirmedPrefix ||
                snapshot.GameId != simulationOptions.GameId || snapshot.SimulationEpoch != simulationOptions.SimulationEpoch) return;
            recoveryCompletionPending = false;
            using (NetworkExecutionObservation.Begin("recovery.completed_notifications", ReadObservationContext()))
                SimulationRecoveryCompleted?.Invoke(simulationCoordinator);
        }

        private void DetachSimulation()
        {
            var coordinator = simulationCoordinator; if (coordinator == null) return;
            initialReadyPending = false;
            coordinator.RequestStop();
            detachSimulationEvents?.Invoke(); detachSimulationEvents = null;
            simulationCoordinator = null;
            replayCapture?.CompleteAfter(coordinator.ExecutionStopped, "session_exit");
            ObserveExecutionStop(coordinator, observedExecutionFailure);
        }

        private static async void ObserveExecutionStop(DeterministicSimulationCoordinator coordinator, Exception observed)
        {
            try
            {
                await coordinator.ExecutionStopped;
                var failure = coordinator.BackgroundExecutionException;
                if (failure != null && !ReferenceEquals(failure, observed)) Debug.LogException(failure);
            }
            catch (Exception error) { Debug.LogException(error); }
        }

        private void OnSimulationApplied(SimulationCommandAppliedDto applied)
        {
            PublishSimulationReady();
            if (applied == null || !CurrentSimulationIdentity(applied.GameId, applied.SimulationEpoch, applied.MembershipVersion)) return;
            using (NetworkExecutionObservation.Begin("simulation.applied_notifications",
                ReadObservationContext(applied.Command?.CommandId, applied.GlobalCommandSequence), true, session.ExecutionTimings))
                SimulationCommandApplied?.Invoke(applied);
            replayCapture?.ObserveApplied(applied.GlobalCommandSequence);
        }
        private bool CurrentSimulationIdentity(string gameId, long epoch, int membership) => simulationOptions != null &&
            gameId == simulationOptions.GameId && epoch == simulationOptions.SimulationEpoch && membership == simulationOptions.MembershipVersion;
        private void OnSimulationPermission(SimulationWindowPermissionDto permission)
        { PublishSimulationReady(); OperationWindowPermissionChanged?.Invoke(permission); }
        private void OnSimulationProposalRejected(SimulationProposalResultDto result)
        {
            if (result?.Status == OperationAdmissionStatus.PendingValidation) return;
            SimulationProposalRejected?.Invoke(result);
        }
        private void SendFinalSimulationProposalResult(SimulationProposalResultDto result)
        {
            simulationCoordinator?.RecordTransportWaitStage("admission.final", result?.Proposal?.Command?.CommandId,
                result?.OrderedCommand?.GlobalCommandSequence ?? 0);
            if (mode != LaunchMode.Host || result?.Proposal?.Command == null ||
                result.ProtocolVersion != OperationCommandProtocol.Version || result.Proposal.GameId != result.GameId ||
                result.Proposal.SimulationEpoch != result.SimulationEpoch || result.Proposal.MembershipVersion != result.MembershipVersion ||
                !CurrentSimulationIdentity(result.GameId, result.SimulationEpoch, result.MembershipVersion) ||
                result.Status == OperationAdmissionStatus.PendingValidation || result.Proposal.Command.PlayerId == localPlayerId) return;
            // 本机由B原ReceiveProposalResult交付；远端只发给原actor的当前已认证连接。
            foreach (var connection in NetworkServer.connections.Values.ToArray())
                if (connection != null && !(connection is LocalConnectionToClient) &&
                    bindings.TryGetPlayer(connection.connectionId, out var actor) && actor == result.Proposal.Command.PlayerId)
                    SendSimulationHost(connection, "proposal-result", result);
        }
        private void OnSimulationValidated(CommandValidationPrefixDto prefix)
        { if (mode == LaunchMode.Host) BroadcastSimulation("validation-prefix", prefix); CommandValidationPrefixAdvanced?.Invoke(prefix); }
        private void OnSimulationFault(SimulationProtocolFaultDto fault)
        { PublishSimulationReady(); if (mode == LaunchMode.Host) BroadcastSimulation("stop", fault); SimulationProtocolFaulted?.Invoke(fault); }
        private void BroadcastSimulationOrdered(OrderedOperationCommandDto ordered)
        {
            using (NetworkExecutionObservation.Begin("simulation.ordered_broadcast",
                ReadObservationContext(ordered.Command?.CommandId, ordered.GlobalCommandSequence))) BroadcastSimulation("ordered", ordered);
        }
        private void BroadcastSimulationBoundary(SimulationBoundaryDto boundary) => BroadcastSimulation("boundary", boundary);
        private void BroadcastSimulationPermit(SimulationWindowPermitDto permit) => BroadcastSimulation("window-permit", permit);
        private void SendSimulationReady(SimulationWindowReadyDto ready) => SendSimulationClient("window-ready", ready);
        private void SendSimulationMissing(SimulationMissingRangeRequestDto request) => SendSimulationClient("missing-range", request);

        private float nextWindowExecutionRetry;
        private void SendWindowExecution(string purpose, object value)
        { if (mode == LaunchMode.Host) BroadcastSimulation(purpose, value); else SendSimulationClient(purpose, value); }
        private void SendWindowExecutionRangeControl(string purpose, WindowExecutionRangeMessage value)
        {
            if (mode != LaunchMode.Host) { SendSimulationClient(purpose, value); return; }
            foreach (var connection in NetworkServer.connections.Values)
                if (!(connection is LocalConnectionToClient) && bindings.TryGetPlayer(connection.connectionId, out int player) && player == value.TargetPlayerId)
                    SendSimulationHost(connection, purpose, value);
        }

        private void ProtocolFault(string code, string diagnostic)
        {
            if (simulationOptions == null) return;
            var fault = new SimulationProtocolFaultDto { GameId = simulationOptions.GameId, SimulationEpoch = simulationOptions.SimulationEpoch,
                MembershipVersion = simulationOptions.MembershipVersion, FaultCode = code, Diagnostic = diagnostic, RequiresRecovery = true, ErrorCode = CommandErrorCode.UnknownCommand };
            if (simulationCoordinator != null) simulationCoordinator.ReceiveRemoteFault(fault);
            else { checkpointChannel?.Stop(); SimulationProtocolFaulted?.Invoke(fault); }
        }

        private bool ValidProtocolHeader(SimulationProtocolPartMessage part) => initialized && simulationOptions != null &&
            part.ProtocolVersion == OperationCommandProtocol.Version && part.GameId == simulationOptions.GameId && part.SimulationEpoch == simulationOptions.SimulationEpoch && part.MembershipVersion == simulationOptions.MembershipVersion &&
            part.TotalBytes > 0 && part.TotalBytes <= GameStateViewTransfer.MaximumBytes && part.Payload != null && part.Payload.Length > 0 && part.Payload.Length <= GameStateViewTransfer.ChunkBytes &&
            part.TransferId > 0 && part.Offset >= 0 && part.Offset <= part.TotalBytes - part.Payload.Length;

        private void SendSimulationClient(string purpose, object payload)
        {
            if (mode != LaunchMode.Client || !IsBoundCheckpointHost(clientConnectionToken) || !checkpointInstallationSent) return;
            try { foreach (var part in ProtocolParts(purpose, payload, clientConnectionToken)) NetworkClient.Send(part, Channels.Reliable); }
            catch (ArgumentException exception) { ProtocolFault("simulation_send_invalid", exception.Message); }
        }
        private void SendSimulationHost(NetworkConnectionToClient connection, string purpose, object payload)
        {
            if (mode != LaunchMode.Host || connection == null || !bindings.TryGetPlayer(connection.connectionId, out var playerId) || !IsBoundCheckpointClient(connection, playerId) ||
                !checkpointChannel.IsPeerInstalled(playerId) && !(recoveryFinished && recoveryInstalled.Contains(playerId)) || !checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token)) return;
            foreach (var part in ProtocolParts(purpose, payload, token)) connection.Send(part, Channels.Reliable);
        }
        private void BroadcastSimulation(string purpose, object payload)
        { foreach (var connection in NetworkServer.connections.Values) if (!(connection is LocalConnectionToClient)) SendSimulationHost(connection, purpose, payload); }
        private IEnumerable<SimulationProtocolPartMessage> ProtocolParts(string purpose, object payload, string token)
        {
            string json = JsonConvert.SerializeObject(payload);
            byte[] bytes = GameStateViewTransfer.Encode(json);
            replayCapture?.RecordProtocol(purpose, json, "encoded_for_send");
            long transfer = checked(++nextSimulationTransfer);
            foreach (var segment in GameStateViewTransfer.Split(bytes))
            {
                var part = new byte[segment.Count]; Buffer.BlockCopy(segment.Array, segment.Offset, part, 0, segment.Count);
                yield return new SimulationProtocolPartMessage { ProtocolVersion = OperationCommandProtocol.Version, TransferId = transfer, Purpose = purpose,
                    GameId = simulationOptions.GameId, SimulationEpoch = simulationOptions.SimulationEpoch, MembershipVersion = simulationOptions.MembershipVersion,
                    ConnectionToken = token, TotalBytes = bytes.Length, Offset = segment.Offset, Payload = part };
            }
        }

        private void OnSimulationClientPart(NetworkConnectionToClient connection, SimulationProtocolPartMessage part)
        {
            if (NetworkExecutionObservation.Enabled && part.Purpose == "proposal" && part.Offset == 0)
                NetworkExecutionObservation.Event("protocol.proposal_first_part", ReadObservationContext(), "transfer=" + part.TransferId + ";bytes=" + part.TotalBytes);
            if (part.Purpose == "confirmed-recovery") { ReceiveRecoveryClientControl(connection, part); return; }
            if (simulationCoordinator == null || simulationCoordinator.IsStopped || mode != LaunchMode.Host || !ValidProtocolHeader(part) ||
                !bindings.TryGetPlayer(connection == null ? -1 : connection.connectionId, out var playerId) || !IsBoundCheckpointClient(connection, playerId) ||
                !checkpointChannel.IsPeerInstalled(playerId) && !(recoveryFinished && recoveryInstalled.Contains(playerId)) || !checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token) || token != part.ConnectionToken ||
                part.Purpose != "proposal" && part.Purpose != "window-ready" && part.Purpose != "missing-range" && part.Purpose != "payment-source-query" &&
                part.Purpose != "stream-record" && part.Purpose != "stream-seal" && part.Purpose != "stream-ready" && part.Purpose != "stream-missing" && part.Purpose != "stream-range") return;
            if (!protocolPeers.TryGetValue(connection.connectionId, out var assembler)) { assembler = new ProtocolAssembly(); protocolPeers.Add(connection.connectionId, assembler); }
            if (!assembler.Append(part, out var json)) { ProtocolFault("simulation_frame_invalid", "规定副本的协议分片冲突或无效。"); return; }
            if (json == null) return;
            using var receivedObservation = NetworkExecutionObservation.Begin(NetworkExecutionObservation.Enabled ? "protocol.receive_" + part.Purpose : null,
                ReadObservationContext(), part.Purpose == "proposal");
            try
            {
                if (part.Purpose == "stream-record") simulationCoordinator.ReceiveWindowExecutionRecord(playerId, false, JsonConvert.DeserializeObject<WindowExecutionRecord>(json));
                else if (part.Purpose == "stream-seal") simulationCoordinator.ReceiveWindowExecutionSeal(playerId, false, JsonConvert.DeserializeObject<WindowExecutionSeal>(json));
                else if (part.Purpose == "stream-ready") simulationCoordinator.ReceiveWindowExecutionReady(playerId, JsonConvert.DeserializeObject<WindowExecutionReadyMessage>(json));
                else if (part.Purpose == "stream-missing") simulationCoordinator.ReceiveWindowExecutionMissing(playerId, false, JsonConvert.DeserializeObject<WindowExecutionRangeMessage>(json));
                else if (part.Purpose == "stream-range") simulationCoordinator.ReceiveWindowExecutionRange(playerId, false, JsonConvert.DeserializeObject<WindowExecutionRangeMessage>(json));
                else if (part.Purpose == "payment-source-query")
                {
                    ReceivePaymentSourceQuery(connection, playerId, token, json);
                }
                else if (part.Purpose == "proposal")
                {
                    var proposal = JsonConvert.DeserializeObject<OperationCommandProposalDto>(json);
                    var admission = NetworkExecutionObservation.Begin("simulation.admission", ReadObservationContext(proposal?.Command?.CommandId), true, session.ExecutionTimings);
                    try
                    {
                        var result = simulationCoordinator.AdmitProposal(playerId, proposal);
                        simulationCoordinator.RecordTransportWaitStage(result.Status == OperationAdmissionStatus.PendingValidation ? "admission.pending" : "admission.return",
                            proposal?.Command?.CommandId, result.OrderedCommand?.GlobalCommandSequence ?? 0);
                        admission.Context.Sequence = result.OrderedCommand?.GlobalCommandSequence ?? 0;
                        if (NetworkExecutionObservation.Enabled) admission.Detail = result.Status.ToString();
                        SendSimulationHost(connection, "proposal-result", result);
                    }
                    finally { admission.Dispose(); }
                }
                else if (part.Purpose == "window-ready") simulationCoordinator.ReceiveWindowReady(playerId, JsonConvert.DeserializeObject<SimulationWindowReadyDto>(json));
                else
                {
                    var request = JsonConvert.DeserializeObject<SimulationMissingRangeRequestDto>(json);
                    if (request == null || request.ProtocolVersion != OperationCommandProtocol.Version || request.GameId != simulationOptions.GameId || request.SimulationEpoch != simulationOptions.SimulationEpoch ||
                        request.MembershipVersion != simulationOptions.MembershipVersion || request.MaximumCount <= 0 || request.MaximumCount > 4) return;
                    int count = Math.Min(request.MaximumCount, simulationOptions.MaximumLogCommands); OperationCommandRangeDto range;
                    while (true)
                    {
                        range = simulationCoordinator.ReadMissingRange(request.FromSequence, count);
                        try { GameStateViewTransfer.Encode(JsonConvert.SerializeObject(range)); break; }
                        catch (ArgumentException) { if (count == 1) throw; count--; }
                    }
                    SendSimulationHost(connection, "range", range);
                }
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException)) { ProtocolFault("simulation_payload_invalid", exception.Message); }
        }

        private void OnSimulationHostPart(SimulationProtocolPartMessage part)
        {
            if (NetworkExecutionObservation.Enabled && part.Purpose == "ordered" && part.Offset == 0)
                NetworkExecutionObservation.Event("protocol.ordered_first_part", ReadObservationContext(), "transfer=" + part.TransferId + ";bytes=" + part.TotalBytes);
            if (part.Purpose == "confirmed-recovery") { ReceiveRecoveryHostControl(part); return; }
            if (simulationCoordinator == null || simulationCoordinator.IsStopped || mode != LaunchMode.Client || !ValidProtocolHeader(part) || !IsBoundCheckpointHost(part.ConnectionToken) ||
                part.Purpose != "ordered" && part.Purpose != "proposal-result" && part.Purpose != "boundary" && part.Purpose != "window-permit" &&
                part.Purpose != "validation-prefix" && part.Purpose != "range" && part.Purpose != "stop" && part.Purpose != "payment-source-page" &&
                part.Purpose != "stream-grant" && part.Purpose != "stream-record" && part.Purpose != "stream-seal" && part.Purpose != "stream-confirmed" && part.Purpose != "stream-missing" && part.Purpose != "stream-range") return;
            if (!protocolHost.Append(part, out var json)) { ProtocolFault("simulation_frame_invalid", "Host协议分片冲突或无效。"); return; }
            if (json == null) return;
            replayCapture?.RecordProtocol(part.Purpose, json, "received");
            try
            {
                switch (part.Purpose)
                {
                    case "stream-grant": simulationCoordinator.ReceiveWindowExecutionGrant(JsonConvert.DeserializeObject<WindowExecutionGrant>(json)); break;
                    case "stream-record": simulationCoordinator.ReceiveWindowExecutionRecord(0, true, JsonConvert.DeserializeObject<WindowExecutionRecord>(json)); break;
                    case "stream-seal": simulationCoordinator.ReceiveWindowExecutionSeal(0, true, JsonConvert.DeserializeObject<WindowExecutionSeal>(json)); break;
                    case "stream-confirmed": simulationCoordinator.ReceiveWindowExecutionConfirmation(JsonConvert.DeserializeObject<WindowExecutionConfirmation>(json)); break;
                    case "stream-missing": simulationCoordinator.ReceiveWindowExecutionMissing(0, true, JsonConvert.DeserializeObject<WindowExecutionRangeMessage>(json)); break;
                    case "stream-range": simulationCoordinator.ReceiveWindowExecutionRange(0, true, JsonConvert.DeserializeObject<WindowExecutionRangeMessage>(json)); break;
                    case "payment-source-page": ReceivePaymentSourcePage(json); break;
                    case "ordered":
                        var ordered = JsonConvert.DeserializeObject<OrderedOperationCommandDto>(json);
                        if (NetworkExecutionObservation.Enabled && ordered != null) observationOrderedThrough = Math.Max(observationOrderedThrough, ordered.GlobalCommandSequence);
                        using (NetworkExecutionObservation.Begin("simulation.ordered_received", ReadObservationContext(ordered?.Command?.CommandId, ordered?.GlobalCommandSequence ?? 0)))
                            simulationCoordinator.ReceiveOrdered(ordered);
                        break;
                    case "proposal-result": simulationCoordinator.ReceiveProposalResult(JsonConvert.DeserializeObject<SimulationProposalResultDto>(json)); break;
                    case "boundary": simulationCoordinator.ReceiveBoundary(JsonConvert.DeserializeObject<SimulationBoundaryDto>(json)); break;
                    case "window-permit": simulationCoordinator.ReceiveWindowPermit(JsonConvert.DeserializeObject<SimulationWindowPermitDto>(json)); break;
                    case "validation-prefix": simulationCoordinator.ReceiveValidationPrefix(JsonConvert.DeserializeObject<CommandValidationPrefixDto>(json)); break;
                    case "stop": simulationCoordinator.ReceiveRemoteFault(JsonConvert.DeserializeObject<SimulationProtocolFaultDto>(json)); break;
                    case "range":
                        var range = JsonConvert.DeserializeObject<OperationCommandRangeDto>(json);
                        if (range == null || range.GameId != simulationOptions.GameId || range.SimulationEpoch != simulationOptions.SimulationEpoch || range.MembershipVersion != simulationOptions.MembershipVersion ||
                            range.Commands == null || range.Commands.Count > 4) throw new ArgumentException("simulation_range_invalid");
                        if (range.ErrorCode == "range_before_base") { ProtocolFault("simulation_range_before_base", "所需原日志早于当前基线，需要恢复。"); break; }
                        foreach (var command in range.Commands)
                        {
                            if (NetworkExecutionObservation.Enabled && command != null) observationOrderedThrough = Math.Max(observationOrderedThrough, command.GlobalCommandSequence);
                            simulationCoordinator.ReceiveOrdered(command);
                        }
                        if (range.ThroughSequence > 0 && range.Commands.Count == 4) SendSimulationMissing(new SimulationMissingRangeRequestDto { GameId = simulationOptions.GameId,
                            SimulationEpoch = simulationOptions.SimulationEpoch, MembershipVersion = simulationOptions.MembershipVersion, FromSequence = range.ThroughSequence + 1 });
                        break;
                }
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException)) { ProtocolFault("simulation_payload_invalid", exception.Message); }
        }

        public void ConfigureConfirmedRecovery(SimulationConfirmedRecoveryInstaller installer)
        {
            if (!initialized || simulationOptions == null || session == null || IsRecoveringConfirmedPrefix) throw new InvalidOperationException("recovery_configuration_invalid");
            if (installer == null || !installer.IsBoundTo(session, simulationOptions)) throw new ArgumentException("recovery_installer_binding_mismatch");
            recoveryInstaller = installer ?? throw new ArgumentNullException(nameof(installer));
        }

        public void RequestConfirmedRecovery()
        {
            if (!initialized || simulationOptions == null || simulationCoordinator == null) return;
            if (recoveryInstaller == null) { ProtocolFault("recovery_not_configured", "实际会话尚未配置确认前缀恢复器。"); return; }
            if (!IsRecoveringConfirmedPrefix)
            {
                NetworkExecutionObservation.Event("recovery.request_started", ReadObservationContext());
                recoveryRequested = true; recoveryFinished = recoveryFailed = recoveryCommitIssued = false;
                recoveryNotice = null; preparedRecovery = null; recoveryTransfer = null; recoveryAssembler = null; receivedRecoveryEnvelope = null;
                recoveryPrepared.Clear(); recoveryInstalled.Clear();
                if (mode == LaunchMode.Client) confirmedRecoveryRequestPending = true;
                simulationCoordinator.BeginConfirmedRecovery();
                SimulationRecoveryStarted?.Invoke(new SimulationRecoveryNoticeDto { GameId = simulationOptions.GameId,
                    SimulationEpoch = simulationOptions.SimulationEpoch + 1, SourceSimulationEpoch = simulationOptions.SimulationEpoch,
                    MembershipVersion = simulationOptions.MembershipVersion, Phase = SimulationRecoveryPhase.Request });
            }
            if (mode == LaunchMode.Client && NetworkClient.isConnected)
            {
                if (checkpointHostConnection == null || !ReferenceEquals(checkpointHostConnection, NetworkClient.connection))
                    checkpointHostConnection = NetworkClient.connection;
                float now = ReadUnscaledTime(); if (now < nextConfirmedRecoveryRequestTime) return;
                nextConfirmedRecoveryRequestTime = now + CheckpointRequestInterval;
                var request = new SimulationRecoveryNoticeDto { GameId = simulationOptions.GameId, SimulationEpoch = simulationOptions.SimulationEpoch + 1,
                    SourceSimulationEpoch = simulationOptions.SimulationEpoch, MembershipVersion = simulationOptions.MembershipVersion, Phase = SimulationRecoveryPhase.Request };
                NetworkExecutionObservation.Event("recovery.request_sent", ReadObservationContext());
                SendRecoveryClient(request);
            }
        }

        private async void PollConfirmedRecovery()
        {
            if (!IsRecoveringConfirmedPrefix || recoveryFailed || recoveryPreparationPending || mode != LaunchMode.Host || recoveryNotice != null) return;
            var coordinator = simulationCoordinator;
            if (coordinator == null || !coordinator.IsStopped && coordinator.ReadPendingTail().Count != 0) return;
            long generation = transportGeneration;
            var installer = recoveryInstaller;
            recoveryPreparationPending = true;
            bool Current() => generation == transportGeneration && ReferenceEquals(coordinator, simulationCoordinator) &&
                ReferenceEquals(installer, recoveryInstaller) && IsRecoveringConfirmedPrefix && !recoveryFailed;
            try
            {
                var checkpoint = await coordinator.CreateConfirmedRecoveryCheckpointAsync(coordinator.ValidatedThroughSequence,
                    simulationOptions.SimulationEpoch + 1);
                if (!Current()) return;
                var b = checkpoint.Baseline;
                var notice = new SimulationRecoveryNoticeDto { GameId = b.GameId, SimulationEpoch = b.SimulationEpoch, MembershipVersion = b.MembershipVersion,
                    SourceSimulationEpoch = b.SourceSimulationEpoch, CheckpointId = b.CheckpointId, ConfirmedGlobalSequence = b.ConfirmedGlobalSequence,
                    MetadataDigest = b.MetadataDigest, Phase = SimulationRecoveryPhase.Prepare, ResumeAllowed = !coordinator.IsStopped };
                var envelope = RecoveryEnvelope(notice); envelope.ContentPackHash = simulationOptions.ContentPackHash;
                // 只读脱离活动会话的 checkpoint；JSON和分包准备不占用运输帧。
                string json = await Task.Run(() => SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint));
                var transfer = await Task.Run(() => SimulationCheckpointTransfer.Encode(envelope, json));
                if (!Current()) return;
                var prepared = await installer.PrepareAsync(notice, json, coordinator);
                if (!Current()) return;
                recoveryNotice = notice; recoveryTransfer = transfer; preparedRecovery = prepared;
                recoveryPrepared.Add(localPlayerId);
                foreach (var connection in NetworkServer.connections.Values.Where(c => !(c is LocalConnectionToClient)).ToArray()) SendRecoveryPackage(connection);
                TryCommitAllRecoveredMembers();
            }
            catch (Exception error)
            { if (Current()) FailConfirmedRecovery("recovery_host_prepare_failed", error.Message); }
            finally { if (generation == transportGeneration) recoveryPreparationPending = false; }
        }

        private SimulationCheckpointEnvelope RecoveryEnvelope(SimulationRecoveryNoticeDto notice) => new SimulationCheckpointEnvelope {
            ProtocolVersion = checkpointChannel?.Identity.WindowExecutionVersion == WindowExecutionGrant.Version ?
                SimulationCheckpointEnvelope.WindowRecoveryProtocolVersion : SimulationCheckpointEnvelope.RecoveryProtocolVersion, Purpose = SimulationCheckpointEnvelope.ConfirmedSimulationRecovery,
            RequestContractVersion = checkpointChannel?.Identity.RequestContractVersion ?? 0,
            WindowExecutionVersion = checkpointChannel?.Identity.WindowExecutionVersion ?? 0,
            GameId = notice.GameId, SourceSimulationEpoch = notice.SourceSimulationEpoch, SimulationEpoch = notice.SimulationEpoch,
            MembershipVersion = notice.MembershipVersion, CheckpointId = notice.CheckpointId, GlobalCommandSequence = notice.ConfirmedGlobalSequence,
            ArchiveDigestVersion = SimulationCheckpointEnvelope.RecoveryDigestVersion };

        private SimulationRecoveryNoticeDto RecoveryNotice(SimulationRecoveryPhase phase) => new SimulationRecoveryNoticeDto {
            GameId = recoveryNotice.GameId, SimulationEpoch = recoveryNotice.SimulationEpoch, MembershipVersion = recoveryNotice.MembershipVersion,
            SourceSimulationEpoch = recoveryNotice.SourceSimulationEpoch, CheckpointId = recoveryNotice.CheckpointId,
            ConfirmedGlobalSequence = recoveryNotice.ConfirmedGlobalSequence, MetadataDigest = recoveryNotice.MetadataDigest,
            ResumeAllowed = recoveryNotice.ResumeAllowed, Phase = phase };

        private void SendRecoveryClient(SimulationRecoveryNoticeDto notice)
        {
            if (!IsBoundCheckpointHost(clientConnectionToken)) return;
            foreach (var part in ProtocolParts("confirmed-recovery", notice, clientConnectionToken)) NetworkClient.Send(part, Channels.Reliable);
        }

        private void SendRecoveryHost(NetworkConnectionToClient connection, SimulationRecoveryNoticeDto notice)
        {
            if (connection == null || !bindings.TryGetPlayer(connection.connectionId, out var id) || !IsBoundCheckpointClient(connection, id) ||
                !simulationOptions.FrozenPlayerIds.Contains(id) || !checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token)) return;
            foreach (var part in ProtocolParts("confirmed-recovery", notice, token)) connection.Send(part, Channels.Reliable);
        }

        private void BroadcastRecovery(SimulationRecoveryPhase phase)
        { foreach (var connection in NetworkServer.connections.Values.Where(c => !(c is LocalConnectionToClient)).ToArray()) SendRecoveryHost(connection, RecoveryNotice(phase)); }

        private void SendRecoveryPackage(NetworkConnectionToClient connection)
        {
            if (recoveryTransfer == null || recoveryFailed || connection == null || !bindings.TryGetPlayer(connection.connectionId, out var id) ||
                !IsBoundCheckpointClient(connection, id) || !simulationOptions.FrozenPlayerIds.Contains(id) ||
                !checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token)) return;
            SendRecoveryHost(connection, RecoveryNotice(SimulationRecoveryPhase.Prepare));
            var envelope = recoveryTransfer.Envelope;
            string header = JsonConvert.SerializeObject(envelope);
            foreach (var part in recoveryTransfer.Split()) connection.Send(new SimulationCheckpointPartMessage {
                EnvelopeJson = header, Offset = part.Offset, Payload = part.Payload, ConnectionToken = token }, Channels.Reliable);
            if (recoveryCommitIssued) SendRecoveryHost(connection, RecoveryNotice(SimulationRecoveryPhase.Commit));
        }

        private bool ValidRecoveryHeader(SimulationProtocolPartMessage part) => initialized && simulationOptions != null &&
            part.ProtocolVersion == OperationCommandProtocol.Version && part.GameId == simulationOptions.GameId && part.MembershipVersion == simulationOptions.MembershipVersion &&
            (part.SimulationEpoch == simulationOptions.SimulationEpoch || recoveryNotice != null &&
                (part.SimulationEpoch == recoveryNotice.SourceSimulationEpoch || part.SimulationEpoch == recoveryNotice.SimulationEpoch)) &&
            part.TotalBytes > 0 && part.TotalBytes <= GameStateViewTransfer.MaximumBytes && part.Payload != null && part.Payload.Length > 0 &&
            part.Payload.Length <= GameStateViewTransfer.ChunkBytes && part.TransferId > 0 && part.Offset >= 0 && part.Offset <= part.TotalBytes - part.Payload.Length;

        private bool SameRecoveryNotice(SimulationRecoveryNoticeDto notice) => recoveryNotice != null && notice != null &&
            notice.ProtocolVersion == OperationCommandProtocol.Version && notice.GameId == recoveryNotice.GameId && notice.SimulationEpoch == recoveryNotice.SimulationEpoch &&
            notice.SourceSimulationEpoch == recoveryNotice.SourceSimulationEpoch && notice.MembershipVersion == recoveryNotice.MembershipVersion &&
            notice.CheckpointId == recoveryNotice.CheckpointId && notice.ConfirmedGlobalSequence == recoveryNotice.ConfirmedGlobalSequence &&
            notice.MetadataDigest == recoveryNotice.MetadataDigest && notice.ResumeAllowed == recoveryNotice.ResumeAllowed;

        private void ReceiveRecoveryClientControl(NetworkConnectionToClient connection, SimulationProtocolPartMessage part)
        {
            if (mode != LaunchMode.Host || simulationCoordinator == null || !ValidRecoveryHeader(part) ||
                !bindings.TryGetPlayer(connection == null ? -1 : connection.connectionId, out var player) || !IsBoundCheckpointClient(connection, player) ||
                !simulationOptions.FrozenPlayerIds.Contains(player) || string.IsNullOrEmpty(part.ConnectionToken) || part.ConnectionToken.Length > 64) return;
            if (!protocolPeers.TryGetValue(connection.connectionId, out var assembly)) { assembly = new ProtocolAssembly(); protocolPeers.Add(connection.connectionId, assembly); }
            if (!assembly.Append(part, out var json)) { FailConfirmedRecovery("recovery_control_invalid", "恢复控制分片冲突。"); return; }
            if (json == null) return;
            try
            {
                var notice = JsonConvert.DeserializeObject<SimulationRecoveryNoticeDto>(json);
                if (notice == null || notice.ProtocolVersion != OperationCommandProtocol.Version || notice.GameId != simulationOptions.GameId || notice.MembershipVersion != simulationOptions.MembershipVersion) return;
                NetworkExecutionObservation.Event("recovery.client_control_received", ReadObservationContext(null, notice.ConfirmedGlobalSequence),
                    NetworkExecutionObservation.Enabled ? notice.Phase + ";target_epoch=" + notice.SimulationEpoch : null);
                if (notice.Phase == SimulationRecoveryPhase.Request)
                {
                    if (notice.SourceSimulationEpoch <= 0 || notice.SourceSimulationEpoch >= long.MaxValue || notice.SimulationEpoch != notice.SourceSimulationEpoch + 1) return;
                    if (notice.SourceSimulationEpoch != simulationOptions.SimulationEpoch && (!IsRecoveringConfirmedPrefix || recoveryNotice == null || notice.SourceSimulationEpoch != recoveryNotice.SourceSimulationEpoch)) return;
                    checkpointConnectionTokens[connection.connectionId] = part.ConnectionToken;
                    Debug.Log("[LocalMirrorLifecycle] 已认证连接接纳确认前缀请求；玩家=" + player + "，连接=" + connection.connectionId + "，源代次=" + notice.SourceSimulationEpoch);
                    RequestConfirmedRecovery(); if (recoveryNotice != null) SendRecoveryPackage(connection); return;
                }
                if (!checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token) || token != part.ConnectionToken || !SameRecoveryNotice(notice) || recoveryFinished || recoveryFailed) return;
                if (notice.Phase == SimulationRecoveryPhase.Prepared) { recoveryPrepared.Add(player); TryCommitAllRecoveredMembers(); }
                else if (notice.Phase == SimulationRecoveryPhase.Installed && recoveryCommitIssued) { recoveryInstalled.Add(player); TryReleaseRecoveredMembers(); }
                else if (notice.Phase == SimulationRecoveryPhase.Failed) FailConfirmedRecovery(notice.ErrorCode, notice.Reason);
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException)) { FailConfirmedRecovery("recovery_control_invalid", exception.Message); }
        }

        private void ReceiveRecoveryHostControl(SimulationProtocolPartMessage part)
        {
            if (mode != LaunchMode.Client || simulationCoordinator == null || !ValidRecoveryHeader(part) || !IsBoundCheckpointHost(part.ConnectionToken)) return;
            if (!protocolHost.Append(part, out var json)) { FailConfirmedRecovery("recovery_control_invalid", "Host恢复控制分片冲突。"); return; }
            if (json == null) return;
            try
            {
                var notice = JsonConvert.DeserializeObject<SimulationRecoveryNoticeDto>(json);
                if (notice == null || notice.ProtocolVersion != OperationCommandProtocol.Version || notice.GameId != simulationOptions.GameId ||
                    notice.MembershipVersion != simulationOptions.MembershipVersion) return;
                NetworkExecutionObservation.Event("recovery.host_control_received", ReadObservationContext(null, notice.ConfirmedGlobalSequence),
                    NetworkExecutionObservation.Enabled ? notice.Phase + ";target_epoch=" + notice.SimulationEpoch : null);
                if (notice.Phase == SimulationRecoveryPhase.Prepare)
                {
                    confirmedRecoveryRequestPending = false;
                    if (SameRecoveryNotice(notice)) { if (preparedRecovery != null) SendRecoveryClient(RecoveryNotice(SimulationRecoveryPhase.Prepared)); return; }
                    if (IsRecoveringConfirmedPrefix && recoveryNotice != null) throw new ArgumentException("recovery_prepare_conflict");
                    if (notice.SourceSimulationEpoch != simulationOptions.SimulationEpoch || notice.SourceSimulationEpoch >= long.MaxValue ||
                        notice.SimulationEpoch != notice.SourceSimulationEpoch + 1 || recoveryInstaller == null) throw new ArgumentException("recovery_prepare_identity_invalid");
                    bool notify = !IsRecoveringConfirmedPrefix; recoveryRequested = true; recoveryFinished = recoveryFailed = recoveryCommitIssued = false;
                    recoveryPrepared.Clear(); recoveryInstalled.Clear(); preparedRecovery = null; recoveryNotice = notice; receivedRecoveryEnvelope = null;
                    simulationCoordinator.BeginConfirmedRecovery();
                    var envelope = RecoveryEnvelope(notice); envelope.ContentPackHash = simulationOptions.ContentPackHash;
                    recoveryAssembler = new SimulationCheckpointTransfer.Assembler(envelope);
                    if (notify) SimulationRecoveryStarted?.Invoke(RecoveryNotice(SimulationRecoveryPhase.Prepare));
                }
                else if (SameRecoveryNotice(notice) && !recoveryFailed)
                {
                    if (notice.Phase == SimulationRecoveryPhase.Commit) { recoveryCommitIssued = true; InstallLocalRecovery(); }
                    else if (notice.Phase == SimulationRecoveryPhase.Installed && recoveryInstalled.Contains(localPlayerId) && recoveryCommitIssued && !recoveryFinished)
                    {
                        recoveryFinished = true; localConnectionRecoveryPending = false; simulationCoordinator.CompleteConfirmedRecovery(notice.ResumeAllowed);
                        recoveryCompletionPending = true;
                    }
                    else if (notice.Phase == SimulationRecoveryPhase.Failed) FailConfirmedRecovery(notice.ErrorCode, notice.Reason);
                }
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException)) { FailConfirmedRecovery("recovery_control_invalid", exception.Message); }
        }

        private async void ReceiveConfirmedRecoveryPart(SimulationCheckpointPartMessage part, SimulationCheckpointEnvelope envelope)
        {
            if (mode != LaunchMode.Client || recoveryNotice == null || recoveryFailed || recoveryPreparationPending || !IsBoundCheckpointHost(part.ConnectionToken)) return;
            if (preparedRecovery != null)
            { if (!SimulationCheckpointEnvelope.SamePayload(receivedRecoveryEnvelope, envelope)) FailConfirmedRecovery("recovery_payload_conflict", "重复恢复包与已准备原包冲突。"); return; }
            if (recoveryAssembler == null) return;
            if (!recoveryAssembler.TryAppend(envelope, part.Offset, part.Payload, out var json, out var fault)) { FailConfirmedRecovery(fault, "恢复检查点组装失败。"); return; }
            if (json == null) return;
            receivedRecoveryEnvelope = envelope.Snapshot();
            long generation = transportGeneration;
            var coordinator = simulationCoordinator; var installer = recoveryInstaller; var notice = recoveryNotice;
            recoveryPreparationPending = true;
            bool Current() => generation == transportGeneration && ReferenceEquals(coordinator, simulationCoordinator) &&
                ReferenceEquals(notice, recoveryNotice) && ReferenceEquals(installer, recoveryInstaller) && !recoveryFailed;
            try
            {
                var prepared = await installer.PrepareAsync(notice, json, coordinator);
                if (!Current()) return;
                preparedRecovery = prepared;
                SendRecoveryClient(RecoveryNotice(SimulationRecoveryPhase.Prepared));
            }
            catch (Exception error) { if (Current()) FailConfirmedRecovery("recovery_client_prepare_failed", error.Message); }
            finally { if (generation == transportGeneration) recoveryPreparationPending = false; }
        }

        private void TryCommitAllRecoveredMembers()
        {
            if (mode != LaunchMode.Host || recoveryCommitIssued || recoveryFailed || recoveryNotice == null ||
                !simulationOptions.FrozenPlayerIds.All(recoveryPrepared.Contains)) return;
            recoveryCommitIssued = true; BroadcastRecovery(SimulationRecoveryPhase.Commit); InstallLocalRecovery(); TryReleaseRecoveredMembers();
        }

        private void InstallLocalRecovery()
        {
            if (preparedRecovery == null || !recoveryCommitIssued || recoveryFailed) { FailConfirmedRecovery("recovery_commit_without_prepare", "恢复尚未准备完成。"); return; }
            using var installObservation = NetworkExecutionObservation.Begin("recovery.install", ReadObservationContext(), true, session.ExecutionTimings);
            if (!recoveryInstalled.Contains(localPlayerId))
            {
                if (!recoveryInstaller.TryCommit(preparedRecovery, out var result)) { FailConfirmedRecovery(result.ErrorCode, result.Reason); return; }
                // 两次发布之间无消费者回调；旧实例仅保存失败/尾段原件，不再推进。
                retiredSimulation = simulationCoordinator; DetachSimulation(); simulationOptions = preparedRecovery.Coordinator.SessionOptions;
                AttachSimulation(preparedRecovery.Coordinator, false); checkpointInstallationSent = true;
                recoveryInstalled.Add(localPlayerId);
            }
            if (mode == LaunchMode.Client) SendRecoveryClient(RecoveryNotice(SimulationRecoveryPhase.Installed));
        }

        private void TryReleaseRecoveredMembers()
        {
            if (mode != LaunchMode.Host || recoveryFailed || recoveryFinished || !recoveryCommitIssued ||
                !simulationOptions.FrozenPlayerIds.All(recoveryInstalled.Contains)) return;
            using var releaseObservation = NetworkExecutionObservation.Begin("recovery.release", ReadObservationContext(), true, session.ExecutionTimings);
            recoveryFinished = true; simulationCoordinator.CompleteConfirmedRecovery(recoveryNotice.ResumeAllowed);
            BroadcastRecovery(SimulationRecoveryPhase.Installed);
            recoveryCompletionPending = true;
        }

        private void FailConfirmedRecovery(string code, string reason)
        {
            if (recoveryFailed) return; recoveryFailed = true; simulationCoordinator?.Stop();
            NetworkExecutionObservation.Event("recovery.failed", ReadObservationContext(), code);
            if (localConnectionRecoveryPending) { localConnectionRecoveryPending = false; LocalSimulationConnectionRecoveryFailureCode = code; }
            if (recoveryNotice != null)
            {
                var notice = RecoveryNotice(SimulationRecoveryPhase.Failed); notice.ErrorCode = code; notice.Reason = reason;
                if (mode == LaunchMode.Host) foreach (var c in NetworkServer.connections.Values.Where(c => !(c is LocalConnectionToClient)).ToArray()) SendRecoveryHost(c, notice);
                else SendRecoveryClient(notice);
            }
            SimulationProtocolFaulted?.Invoke(new SimulationProtocolFaultDto { GameId = simulationOptions?.GameId,
                SimulationEpoch = simulationOptions?.SimulationEpoch ?? 0, MembershipVersion = simulationOptions?.MembershipVersion ?? 0,
                FaultCode = code, Diagnostic = reason, RequiresRecovery = true, ErrorCode = CommandErrorCode.UnknownCommand });
        }

        private void RefreshPendingLocalBindings()
        {
            if (pendingLocalBindings.Count == 0) return;
            var room = OnlineRoomServiceProvider.GetActive()?.GetCurrentRoom();
            if (room == null || !room.HasStarted || launchSeats == null || simulationOptions != null && room.GameId != simulationOptions.GameId) return;
            // 仅更新已冻结玩家的认证连接事实，不能从重连消息增删玩家或更换身份。
            foreach (var expected in launchSeats)
            foreach (var current in room.Seats)
                if (current.PlayerId == expected.PlayerId && current.SteamId == expected.SteamId)
                {
                    expected.TransportConnected = current.TransportConnected;
                    expected.IdentityVerified = current.IdentityVerified;
                    expected.NetworkClientId = current.NetworkClientId;
                    expected.IsReady = current.IsReady;
                    expected.LobbyMemberPresent = current.LobbyMemberPresent;
                }
            foreach (var pair in new List<KeyValuePair<int, float>>(pendingLocalBindings))
            {
                if (!NetworkServer.connections.TryGetValue(pair.Key, out var connection))
                { pendingLocalBindings.Remove(pair.Key); continue; }
                if (TryGetExpectedIdentity(pair.Key, out var identity) &&
                    RoomReadinessPolicy.TryResolveExpectedBinding(launchSeats, pair.Key, identity, out _))
                {
                    pendingLocalBindings.Remove(pair.Key);
                    OnServerConnected(connection);
                }
                else if (Time.unscaledTime >= pair.Value)
                {
                    pendingLocalBindings.Remove(pair.Key);
                    RejectConnection(connection, "原席位重连身份认证超时。");
                }
            }
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
