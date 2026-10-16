using System;
using System.Collections.Generic;
using System.Linq;
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
    public struct SubmitCommandMessage : NetworkMessage { public string Json; }
    public struct AcceptedCommandMessage : NetworkMessage { public string Json; public string ConnectionToken; }
    public struct RejectedCommandMessage : NetworkMessage { public string Json; public string GameId; public string ConnectionToken; public int StateRevision; }
    public struct InitialStateRequestMessage : NetworkMessage { public string ConnectionToken; }
    public struct InitialStateMessage : NetworkMessage { public string Json; public string ProtocolVersion; public string ConnectionToken; }
    public struct InitialStateAppliedMessage : NetworkMessage { public int PlayerId; public string ConnectionToken; }
    public struct CommandOutcomeRequestMessage : NetworkMessage
    { public string GameId; public int PlayerId; public string CommandId; public string ConnectionToken; }
    public struct CommandOutcomeMessage : NetworkMessage { public string Json; public string ConnectionToken; }
    public struct GameStateViewPartMessage : NetworkMessage
    {
        public long TransferId;
        public bool IsInitial;
        public int TotalBytes, Offset;
        public string ProtocolVersion;
        public string ConnectionToken;
        public byte[] Payload;
    }

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

    /// <summary>仅供本地观察确认接收与同步分支，不进入网络协议，不包含状态投影。</summary>
    public sealed class LocalCommandConfirmationDiagnostic
    {
        public string Branch { get; }
        public string GameId { get; }
        public string ReceivedGameId { get; }
        public string CommandId { get; }
        public string PendingCommandId { get; }
        public int? Sequence { get; }
        public int? NextExpectedSequence { get; }
        public bool? IsInitialStateSynchronized { get; }
        public bool? ExactPendingMatched { get; }
        public bool ApplyAttempted { get; }
        public bool? ApplySucceeded { get; }
        public string ErrorCode { get; }

        internal LocalCommandConfirmationDiagnostic(string branch, string gameId, string receivedGameId,
            string commandId, string pendingCommandId, int? sequence, int? nextExpectedSequence,
            bool? isInitialStateSynchronized, bool? exactPendingMatched, CommandResult applyResult)
        {
            Branch = branch;
            GameId = gameId;
            ReceivedGameId = receivedGameId;
            CommandId = commandId;
            PendingCommandId = pendingCommandId;
            Sequence = sequence;
            NextExpectedSequence = nextExpectedSequence;
            IsInitialStateSynchronized = isInitialStateSynchronized;
            ExactPendingMatched = exactPendingMatched;
            ApplyAttempted = applyResult != null;
            ApplySucceeded = applyResult?.Succeeded;
            ErrorCode = applyResult?.Validation?.ErrorCode.ToString();
        }
    }

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
        private string pendingGameId;
        private GameCommandDto pendingCommand;
        private CommandAuthorityOutcome recoveredOutcome;
        private string clientConnectionToken;
        private float nextCommandOutcomeRequestTime;
        private readonly Dictionary<int, string> clientConnectionTokens = new Dictionary<int, string>();
        private float commandConfirmationDeadline;
        private const float CommandConfirmationTimeout = 10f;
        private const float InitialStateRequestInterval = 5f;
        private int localPlayerId;
        private IList<PlayerSeat> launchSeats;
        private readonly Dictionary<int, float> pendingLocalBindings = new Dictionary<int, float>();
        private readonly GameStateViewTransfer.Assembler viewAssembler = new GameStateViewTransfer.Assembler();
        private long nextViewTransfer;
        private SimulationCheckpointChannel checkpointChannel;
        private NetworkConnection checkpointHostConnection;
        private float nextCheckpointRequestTime;
        private bool checkpointInstallationSent;
        private readonly Dictionary<int, string> checkpointConnectionTokens = new Dictionary<int, string>();
        private const int MaximumCheckpointEnvelopeCharacters = 4096;
        private DeterministicSimulationSessionOptions simulationOptions;
        private DeterministicSimulationCoordinator simulationCoordinator;
        private long nextSimulationTransfer;
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
            session?.State.GameId == localConnectionRecoveryGameId && session.State.ContentPackHash == simulationOptions.ContentPackHash && simulationOptions.GameId == localConnectionRecoveryGameId &&
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

        public event Action<ConfirmedGameStateViewDto> ConfirmedStateViewApplied;
        /// <summary>真实本地pending已获确认，但对应旧投影已被同步状态覆盖；只通知，不重放视图。</summary>
        public event Action<ConfirmedGameStateViewDto> CoveredCommandAcknowledged;
        public event Action<LocalCommandConfirmationDiagnostic> CommandConfirmationObserved;
        public event Action<InitialGameStateViewDto> InitialStateViewApplied;
        public event Action<RejectedGameCommandDto> CommandRejected;
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
        public static MirrorCommandTransport Instance { get; private set; }
        public bool IsSynchronizing => simulationOptions != null ? IsAwaitingLocalSimulationConnectionRecovery || IsRecoveringConfirmedPrefix || simulationCoordinator == null || simulationCoordinator.IsStopped || simulationCoordinator.IsRecoveringConfirmedPrefix || simulationCoordinator.HasPendingLocalApplication :
            checkpointChannel != null ? !checkpointChannel.IsInstalled || checkpointChannel.IsStopped :
            awaitingInitialState || !string.IsNullOrEmpty(pendingCommandId);
        public bool IsInitialSimulationCheckpointConfigured => checkpointChannel != null;
        public bool HasInstalledInitialSimulationCheckpoint => checkpointChannel != null && checkpointChannel.IsInstalled && !checkpointChannel.IsStopped;
        public bool IsInitialSimulationCheckpointInstalledByPeer(int playerId) => checkpointChannel != null && checkpointChannel.IsPeerInstalled(playerId);

        public void InitializeDeterministicSimulation(GameSession session, LaunchMode launchMode, int playerId, IList<PlayerSeat> seats,
            DeterministicSimulationSessionOptions options, InitialSimulationCheckpointInstaller installer, Func<string> createHostArchive)
        {
            if (session == null || options == null || seats == null || seats.Any(seat => seat == null) ||
                launchMode != LaunchMode.Host && launchMode != LaunchMode.Client || options.IsHost != (launchMode == LaunchMode.Host) || options.LocalPlayerId != playerId ||
                options.GameId != session.State.GameId || options.ContentPackHash != session.State.ContentPackHash || options.SimulationEpoch != 1 || options.MembershipVersion != 1 ||
                options.CheckpointGlobalSequence != 0 || options.CheckpointPrefixDigest != OperationCommandCanonicalizer.EmptyPrefixDigest || options.FrozenPlayerIds == null ||
                !seats.Select(seat => seat.PlayerId).OrderBy(id => id).SequenceEqual(options.FrozenPlayerIds.OrderBy(id => id)) ||
                launchMode == LaunchMode.Host && (installer != null || createHostArchive == null) || launchMode == LaunchMode.Client && (installer == null || createHostArchive != null))
                throw new ArgumentException("simulation_transport_configuration_invalid");
            // 构造仅核配置，不 Begin/Submit/投影；任何旧注册或请求发生前先选定模式。
            var prepared = new DeterministicSimulationCoordinator(session, options);
            var identity = new SimulationCheckpointEnvelope { GameId = options.GameId, ContentPackHash = options.ContentPackHash,
                SimulationEpoch = 1, MembershipVersion = 1, GlobalCommandSequence = 0,
                CheckpointId = "initial." + SimulationIdentityHash(JsonConvert.SerializeObject(new {
                    options.GameId, options.ContentPackHash, options.SimulationEpoch, options.MembershipVersion, Players = options.FrozenPlayerIds.OrderBy(id => id).ToArray() })) };
            var channel = new SimulationCheckpointChannel(identity, installer, createHostArchive, options.FrozenPlayerIds);
            Shutdown();
            this.session = session; mode = launchMode; localPlayerId = playerId; launchSeats = seats;
            simulationOptions = options.Snapshot(); checkpointChannel = channel; initialized = true; awaitingInitialState = false;
            foreach (var seat in seats) seat.GameStateSynchronized = false;
            if (mode == LaunchMode.Host)
            {
                bindings.ReplaceLobbySeats(seats.Select(seat => new KeyValuePair<int, ulong>(seat.PlayerId, seat.SteamId)));
                NetworkServer.OnConnectedEvent += OnServerConnected; NetworkServer.OnDisconnectedEvent += OnServerDisconnected;
                NetworkServer.RegisterHandler<SimulationCheckpointRequestMessage>(OnCheckpointRequest, false);
                NetworkServer.RegisterHandler<SimulationCheckpointInstalledMessage>(OnCheckpointInstalled, false);
                NetworkServer.RegisterHandler<SimulationCheckpointErrorMessage>(OnCheckpointPeerError, false);
                NetworkServer.RegisterHandler<SimulationProtocolPartMessage>(OnSimulationClientPart, false);
                foreach (var connection in NetworkServer.connections.Values) OnServerConnected(connection);
                AttachSimulation(prepared);
                if (!channel.TryPrepareHostArchive(out var fault)) { PublishCheckpointFault(fault); return; }
                prepared.Begin();
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

        /// <summary>Initialize之后、网络分发前显式调用。只接初始完整检查点，尚不启用新命令入口。</summary>
        public void ConfigureInitialSimulationCheckpoint(SimulationCheckpointEnvelope identity,
            InitialSimulationCheckpointInstaller installer, Func<string> createHostArchive)
        {
            if (!initialized || session == null || checkpointChannel != null || !string.IsNullOrEmpty(pendingCommandId) ||
                identity == null || identity.GameId != session.State.GameId || identity.ContentPackHash != session.State.ContentPackHash ||
                identity.EncodedByteCount != 0 || identity.DecodedByteCount != 0 ||
                mode == LaunchMode.Host && (installer != null || createHostArchive == null) ||
                mode == LaunchMode.Client && (installer == null || createHostArchive != null || dispatcher.IsInitialStateSynchronized) ||
                mode != LaunchMode.Host && mode != LaunchMode.Client)
                throw new InvalidOperationException("checkpoint_transport_configuration_invalid");
            checkpointChannel = new SimulationCheckpointChannel(identity, installer, createHostArchive,
                launchSeats == null ? null : new List<int>(EnumerateLaunchPlayerIds()));
            viewAssembler.Reset(); awaitingInitialState = false; nextCheckpointRequestTime = 0f;
            if (mode == LaunchMode.Host)
            {
                NetworkServer.RegisterHandler<SimulationCheckpointRequestMessage>(OnCheckpointRequest, false);
                NetworkServer.RegisterHandler<SimulationCheckpointInstalledMessage>(OnCheckpointInstalled, false);
                NetworkServer.RegisterHandler<SimulationCheckpointErrorMessage>(OnCheckpointPeerError, false);
            }
            else
            {
                RegisterCheckpointClientMessages(); BindCheckpointHost(); RequestCheckpoint();
            }
        }

        private IEnumerable<int> EnumerateLaunchPlayerIds()
        { foreach (var seat in launchSeats) if (seat != null) yield return seat.PlayerId; }

        public bool HasCompletedSettlement => initialized && !awaitingInitialState && session != null &&
            RoundTrackRule.IsFinalState(session.State) &&
            session.State.FinalScoring != null && session.State.FinalScoring.IsResolved;

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
                NetworkServer.RegisterHandler<CommandOutcomeRequestMessage>(OnCommandOutcomeRequest, false);
                foreach (var connection in NetworkServer.connections.Values)
                    OnServerConnected(connection);
            }
            else if (mode == LaunchMode.Client)
            {
                RegisterClientMessages();
                if (MirrorNetworkRuntime.Instance != null)
                    MirrorNetworkRuntime.Instance.LocalClientRestarted += RegisterClientMessages;
                RequestInitialState();
            }
            initialized = true;
        }

        private void Update()
        {
            if (initialized && mode == LaunchMode.Host) RefreshPendingLocalBindings();
            if (checkpointChannel != null)
            {
                if (!IsCheckpointSessionCurrent()) return;
                if (simulationOptions != null) simulationCoordinator?.Pump(simulationOptions.PumpCommandsPerFrame);
                PollConfirmedRecovery();
                if (mode == LaunchMode.Client && NetworkClient.isConnected && IsRecoveringConfirmedPrefix && !recoveryFailed && confirmedRecoveryRequestPending)
                    RequestConfirmedRecovery();
                if (mode == LaunchMode.Client && NetworkClient.isConnected && !checkpointInstallationSent && !checkpointChannel.IsStopped)
                    RequestCheckpoint();
                return;
            }
            if (!initialized || mode != LaunchMode.Client || !NetworkClient.isConnected)
                return;
            RequestCommandOutcome();
            if (!awaitingInitialState && !string.IsNullOrEmpty(pendingCommandId) &&
                Time.unscaledTime >= commandConfirmationDeadline)
            {
                // 只补同步状态，不重发原命令，避免重复支付或重复结算。
                awaitingInitialState = true;
            }
            if (!awaitingInitialState || Time.unscaledTime < nextInitialStateRequestTime) return;
            RequestInitialState();
        }

        public CommandResult SubmitOrSend(GameCommand command, out bool appliedLocally)
        {
            appliedLocally = false;
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (simulationOptions != null)
            {
                if (simulationCoordinator == null || mode == LaunchMode.Client && !NetworkClient.isConnected) return Invalid("完整模拟尚未就绪。");
                if (!simulationCoordinator.TryCreateLocalProposal(command, out var proposal, out var validation)) return CommandResult.Invalid(validation);
                if (mode == LaunchMode.Host)
                {
                    var result = simulationCoordinator.AdmitProposal(localPlayerId, proposal); simulationCoordinator.ReceiveProposalResult(result);
                    if (result.Status != OperationAdmissionStatus.Accepted && result.Status != OperationAdmissionStatus.Duplicate) return CommandResult.Invalid(new ValidationResult(false, result.ErrorCode, result.Reason));
                }
                else SendSimulationClient("proposal", proposal);
                return CommandResult.SuccessResult(new List<GameEvent>(), "命令已进入编序队列，等待本地执行。");
            }
            if (checkpointChannel != null) return Invalid("完整检查点通道已配置，新模拟命令入口尚未接通。");
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
            if (dto.PlayerId != localPlayerId) return Invalid("只能提交本人的命令。");
            pendingCommandId = dto.CommandId;
            pendingGameId = session.View?.GameId ?? session.State.GameId;
            pendingCommand = dto;
            recoveredOutcome = null;
            nextCommandOutcomeRequestTime = Time.unscaledTime + InitialStateRequestInterval;
            commandConfirmationDeadline = Time.unscaledTime + CommandConfirmationTimeout;
            NetworkClient.Send(new SubmitCommandMessage { Json = JsonUtility.ToJson(dto) });
            return CommandResult.SuccessResult(new List<GameEvent>(), "命令已发送给房主。");
        }

        public void Shutdown()
        {
            DetachSimulation(); simulationOptions = null; protocolPeers.Clear(); protocolHost.Reset(); nextSimulationTransfer = 0;
            recoveryInstaller = null; recoveryNotice = null; preparedRecovery = null; recoveryTransfer = null; recoveryAssembler = null; receivedRecoveryEnvelope = null; retiredSimulation = null;
            recoveryPrepared.Clear(); recoveryInstalled.Clear(); recoveryRequested = recoveryFinished = recoveryFailed = recoveryCommitIssued = false;
            localConnectionRecoveryPending = false; localConnectionRecoveryGameId = null; localConnectionRecoveryEpoch = 0; localConnectionRecoveryMembership = 0;
            LocalSimulationConnectionRecoveryFailureCode = string.Empty; nextConfirmedRecoveryRequestTime = 0f;
            confirmedRecoveryRequestPending = false;
            checkpointChannel?.Stop(); checkpointChannel = null; checkpointHostConnection = null;
            checkpointConnectionTokens.Clear(); nextCheckpointRequestTime = 0f;
            checkpointInstallationSent = false;
            viewAssembler.Reset();
            pendingLocalBindings.Clear();
            clientConnectionTokens.Clear();
            if (MirrorNetworkRuntime.Instance != null)
            {
                MirrorNetworkRuntime.Instance.LocalClientRestarted -= RegisterClientMessages;
                MirrorNetworkRuntime.Instance.LocalClientReconnectFailed -= OnLocalClientReconnectFailed;
            }
            if (dispatcher != null)
            {
                dispatcher.CommandAccepted -= BroadcastAccepted;
                dispatcher.CommandRejected -= SendRejected;
            }
            NetworkServer.OnConnectedEvent -= OnServerConnected;
            NetworkServer.OnDisconnectedEvent -= OnServerDisconnected;
            NetworkClient.OnConnectedEvent -= OnLocalClientConnected;
            NetworkClient.OnDisconnectedEvent -= OnCheckpointClientDisconnected;
            if (NetworkServer.active)
            {
                NetworkServer.UnregisterHandler<SubmitCommandMessage>();
                NetworkServer.UnregisterHandler<InitialStateRequestMessage>();
                NetworkServer.UnregisterHandler<InitialStateAppliedMessage>();
                NetworkServer.UnregisterHandler<CommandOutcomeRequestMessage>();
                NetworkServer.UnregisterHandler<SimulationCheckpointRequestMessage>();
                NetworkServer.UnregisterHandler<SimulationCheckpointInstalledMessage>();
                NetworkServer.UnregisterHandler<SimulationCheckpointErrorMessage>();
                NetworkServer.UnregisterHandler<SimulationProtocolPartMessage>();
            }
            if (NetworkClient.active)
            {
                NetworkClient.UnregisterHandler<AcceptedCommandMessage>();
                NetworkClient.UnregisterHandler<RejectedCommandMessage>();
                NetworkClient.UnregisterHandler<InitialStateMessage>();
                NetworkClient.UnregisterHandler<GameStateViewPartMessage>();
                NetworkClient.UnregisterHandler<CommandOutcomeMessage>();
                NetworkClient.UnregisterHandler<SimulationCheckpointPartMessage>();
                NetworkClient.UnregisterHandler<SimulationCheckpointErrorMessage>();
                NetworkClient.UnregisterHandler<SimulationProtocolPartMessage>();
            }
            ObserveCommandConfirmation("shutdown_pending_cleared");
            dispatcher = null;
            session = null;
            initialized = false;
            awaitingInitialState = false;
            nextInitialStateRequestTime = 0f;
            ClearPendingCommand();
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
            dispatcher?.RegisterClientPlayer((ulong)connection.connectionId, playerId);
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
            pendingLocalBindings.Remove(connection.connectionId);
            clientConnectionTokens.Remove(connection.connectionId);
            dispatcher?.UnregisterClientPlayer((ulong)connection.connectionId);
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

        private void OnSubmit(NetworkConnectionToClient connection, SubmitCommandMessage message)
        {
            if (checkpointChannel != null) return;
            var dto = JsonUtility.FromJson<GameCommandDto>(message.Json);
            if (!bindings.IsCommandOwner(connection.connectionId, dto == null ? -1 : dto.PlayerId))
            {
                var rejected = RejectedGameCommandDto.FromResult(dto, Invalid("连接身份与命令玩家不一致。"));
                SendRejected((ulong)connection.connectionId, rejected);
                return;
            }
            dispatcher.ReceiveClientCommand((ulong)connection.connectionId, dto);
        }

        private void OnInitialStateRequest(NetworkConnectionToClient connection, InitialStateRequestMessage message)
        {
            if (checkpointChannel != null) return;
            if (!bindings.TryGetPlayer(connection.connectionId, out _) || string.IsNullOrEmpty(message.ConnectionToken) ||
                message.ConnectionToken.Length > 64) return;
            clientConnectionTokens[connection.connectionId] = message.ConnectionToken;
            SendInitialState(connection);
        }

        private void OnInitialStateApplied(NetworkConnectionToClient connection, InitialStateAppliedMessage message)
        {
            if (checkpointChannel != null) return;
            if (!bindings.IsCommandOwner(connection.connectionId, message.PlayerId) ||
                !IsConnectionTokenCurrent(connection.connectionId, message.ConnectionToken)) return;
            MarkGameStateSynchronized(message.PlayerId, true);
        }

        private bool IsConnectionTokenCurrent(int connectionId, string token) => !string.IsNullOrEmpty(token) &&
            clientConnectionTokens.TryGetValue(connectionId, out var current) && current == token;

        private void OnCommandOutcomeRequest(NetworkConnectionToClient connection, CommandOutcomeRequestMessage message)
        {
            if (checkpointChannel != null) return;
            if (connection == null || !bindings.IsCommandOwner(connection.connectionId, message.PlayerId) ||
                !IsConnectionTokenCurrent(connection.connectionId, message.ConnectionToken) ||
                !string.Equals(message.GameId, session.State.GameId, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(message.CommandId) || message.CommandId.Length > 128) return;
            if (!dispatcher.TryGetCommandOutcome((ulong)connection.connectionId, message.GameId, message.PlayerId,
                    message.CommandId, out var outcome))
                outcome = new CommandAuthorityOutcome { GameId = message.GameId, PlayerId = message.PlayerId,
                    CommandId = message.CommandId, Outcome = CommandAuthorityOutcomeKind.Unknown };
            connection.Send(new CommandOutcomeMessage { Json = JsonConvert.SerializeObject(outcome), ConnectionToken = message.ConnectionToken });
        }

        private void SendInitialState(NetworkConnectionToClient connection)
        {
            if (checkpointChannel != null) return;
            int playerId;
            if (!bindings.TryGetPlayer(connection.connectionId, out playerId)) return;
            InitialGameStateViewDto view = dispatcher.CreateInitialStateViewSynchronization(playerId);
            SendStateView(connection, view, true);
        }

        private void BroadcastAccepted(ConfirmedGameCommandDto confirmed)
        {
            if (checkpointChannel != null) return;
            foreach (var connection in NetworkServer.connections.Values)
            {
                if (connection is LocalConnectionToClient) continue;
                int playerId;
                if (!bindings.TryGetPlayer(connection.connectionId, out playerId)) continue;
                // 每个连接单独投影；绝不先序列化 confirmed.State 再由客户端隐藏。
                ConfirmedGameStateViewDto view = dispatcher.CreateConfirmedStateViewSynchronization(
                    confirmed,
                    GameStateViewer.Player(playerId));
                SendStateView(connection, view, false);
            }
            if (confirmed != null)
            {
                ConfirmedStateViewApplied?.Invoke(
                    dispatcher.CreateConfirmedStateViewSynchronization(confirmed, GameStateViewer.Host));
            }
        }

        private void SendRejected(ulong recipient, RejectedGameCommandDto rejected)
        {
            if (NetworkServer.connections.TryGetValue((int)recipient, out var connection) &&
                clientConnectionTokens.TryGetValue((int)recipient, out var token))
                connection.Send(new RejectedCommandMessage { Json = JsonUtility.ToJson(rejected),
                    GameId = session.State.GameId, ConnectionToken = token,
                    StateRevision = session.State.EffectRuntime?.StateRevision ?? 0 });
        }

        private void SendStateView(NetworkConnectionToClient connection, object view, bool initial)
        {
            if (!clientConnectionTokens.TryGetValue(connection.connectionId, out var token)) return;
            var bytes = GameStateViewTransfer.Encode(JsonConvert.SerializeObject(view));
            long transfer = ++nextViewTransfer;
            foreach (var part in GameStateViewTransfer.Split(bytes))
            {
                var payload = new byte[part.Count];
                Buffer.BlockCopy(part.Array, part.Offset, payload, 0, part.Count);
                connection.Send(new GameStateViewPartMessage { TransferId = transfer, IsInitial = initial,
                    ConnectionToken = token,
                    TotalBytes = bytes.Length, Offset = part.Offset, ProtocolVersion = SteamLobbyPolicy.ProtocolVersion,
                    Payload = payload }, Channels.Reliable);
            }
        }

        private void OnStateViewPart(GameStateViewPartMessage message)
        {
            if (checkpointChannel != null) return;
            if (NetworkServer.active) return;
            if (!IsCurrentClientConnection(message.ConnectionToken)) return;
            if (message.ProtocolVersion != SteamLobbyPolicy.ProtocolVersion ||
                !viewAssembler.TryAppend(message.TransferId, message.IsInitial, message.TotalBytes,
                    message.Offset, message.Payload, out var json))
            { Debug.LogError("state_view_transfer_invalid"); NetworkClient.Disconnect(); return; }
            if (json == null) return;
            if (message.IsInitial) OnInitialState(new InitialStateMessage { Json = json, ProtocolVersion = message.ProtocolVersion, ConnectionToken = message.ConnectionToken });
            else OnAccepted(new AcceptedCommandMessage { Json = json, ConnectionToken = message.ConnectionToken });
        }

        private void OnInitialState(InitialStateMessage message)
        {
            if (checkpointChannel != null) return;
            if (NetworkServer.active) return;
            if (!IsCurrentClientConnection(message.ConnectionToken)) return;
            if (message.ProtocolVersion != SteamLobbyPolicy.ProtocolVersion)
            {
                Debug.LogError("对局协议版本不兼容，请所有玩家使用同一构建。");
                NetworkClient.Disconnect();
                return;
            }
            if (!GameStateViewProtocol.TryReadInitial(message.Json, session.State.RuleConfiguration, session.State.LaunchPlayerOrder, out var snapshot))
            { Debug.LogError("room_protocol_mismatch"); NetworkClient.Disconnect(); return; }
            if (snapshot?.View == null || string.IsNullOrEmpty(session.State.GameId) ||
                snapshot.View.GameId != session.State.GameId || session.State.RandomRuntime == null ||
                snapshot.View.MapId != session.State.MapId ||
                snapshot.View.Players.Count != session.State.Players.Count ||
                snapshot.View.ViewerRole != GameStateViewerRole.Player || snapshot.View.ViewerPlayerId != localPlayerId ||
                !string.IsNullOrEmpty(pendingGameId) && snapshot.View.GameId != pendingGameId)
            {
                Debug.LogError("权威开局地图或人数与当前房间不一致，已拒绝同步。");
                NetworkClient.Disconnect();
                return;
            }
            var result = dispatcher.ApplyInitialStateViewSynchronization(snapshot);
            ObserveCommandConfirmation("initial_apply_result", snapshot.View.GameId, applyResult: result);
            if (result.Succeeded)
            {
                // 快照可能在本地命令发送前已排入可靠队列，不能把它当作该命令的确认。
                awaitingInitialState = false;
                MarkGameStateSynchronized(localPlayerId, true);
                if (NetworkClient.isConnected)
                    NetworkClient.Send(new InitialStateAppliedMessage { PlayerId = localPlayerId, ConnectionToken = clientConnectionToken });
                InitialStateViewApplied?.Invoke(snapshot);
                commandConfirmationDeadline = Time.unscaledTime + CommandConfirmationTimeout;
                nextCommandOutcomeRequestTime = 0f;
                TryCompleteRecoveredOutcome();
                RequestCommandOutcome();
            }
        }

        private void OnAccepted(AcceptedCommandMessage message)
        {
            if (checkpointChannel != null) return;
            if (NetworkServer.active) return;
            if (!IsCurrentClientConnection(message.ConnectionToken)) return;
            if (!GameStateViewProtocol.TryReadConfirmed(message.Json, session.State.RuleConfiguration, session.State.LaunchPlayerOrder, out var confirmed))
            { Debug.LogError("room_protocol_mismatch"); NetworkClient.Disconnect(); return; }
            if (!IsCurrentClientView(confirmed.View)) return;
            if (confirmed.Command != null && confirmed.Command.PlayerId != localPlayerId) return;
            bool acknowledgedPendingCommand = MatchesPending(confirmed.View.GameId,
                confirmed.Command?.PlayerId ?? -1, confirmed.Command?.CommandId);
            ObserveCommandConfirmation("accepted_received", confirmed.View.GameId,
                confirmed.Command?.CommandId, confirmed.Sequence, acknowledgedPendingCommand);
            // 较新的完整快照可能已经覆盖此确认；重复确认不应再触发补同步。
            if (confirmed.Sequence < dispatcher.NextExpectedConfirmedSequence)
            {
                if (acknowledgedPendingCommand && dispatcher.IsInitialStateSynchronized)
                {
                    recoveredOutcome = AcceptedOutcome(confirmed);
                    TryCompleteRecoveredOutcome();
                }
                return;
            }
            var result = dispatcher.ApplyConfirmedStateViewSynchronization(confirmed);
            ObserveCommandConfirmation("accepted_apply_result", confirmed.View.GameId,
                confirmed.Command?.CommandId, confirmed.Sequence, acknowledgedPendingCommand, result);
            if (result.Succeeded)
            {
                if (acknowledgedPendingCommand) ClearPendingCommand();
                awaitingInitialState = false;
                ConfirmedStateViewApplied?.Invoke(confirmed);
                TryCompleteRecoveredOutcome();
            }
            else
            {
                if (acknowledgedPendingCommand) recoveredOutcome = AcceptedOutcome(confirmed);
                awaitingInitialState = true;
                RequestInitialState();
            }
        }

        private void OnRejected(RejectedCommandMessage message)
        {
            if (checkpointChannel != null) return;
            if (NetworkServer.active) return;
            if (!IsCurrentClientConnection(message.ConnectionToken)) return;
            var rejected = JsonConvert.DeserializeObject<RejectedGameCommandDto>(message.Json);
            if (rejected?.Command == null || !MatchesPending(message.GameId, rejected.Command.PlayerId, rejected.Command.CommandId)) return;
            recoveredOutcome = new CommandAuthorityOutcome { GameId = message.GameId, PlayerId = rejected.Command.PlayerId,
                CommandId = rejected.Command.CommandId, Outcome = CommandAuthorityOutcomeKind.Rejected,
                StateRevision = message.StateRevision, ErrorCode = rejected.ErrorCode, Reason = rejected.Reason };
            TryCompleteRecoveredOutcome();
        }

        private bool IsCurrentClientConnection(string token) => !string.IsNullOrEmpty(clientConnectionToken) &&
            string.Equals(token, clientConnectionToken, StringComparison.Ordinal);

        private bool IsCurrentClientView(GameStateView view) => view != null &&
            view.GameId == (session.View?.GameId ?? session.State.GameId) &&
            view.ViewerRole == GameStateViewerRole.Player && view.ViewerPlayerId == localPlayerId;

        private bool MatchesPending(string gameId, int playerId, string commandId) => pendingCommand != null &&
            !string.IsNullOrEmpty(pendingCommandId) && !string.IsNullOrEmpty(commandId) && playerId == localPlayerId &&
            pendingCommand.PlayerId == playerId && string.Equals(commandId, pendingCommandId, StringComparison.Ordinal) &&
            string.Equals(gameId, pendingGameId, StringComparison.Ordinal);

        private static CommandAuthorityOutcome AcceptedOutcome(ConfirmedGameStateViewDto confirmed) =>
            new CommandAuthorityOutcome { GameId = confirmed.View.GameId, PlayerId = confirmed.Command.PlayerId,
                CommandId = confirmed.Command.CommandId, Outcome = CommandAuthorityOutcomeKind.Accepted,
                AcceptedSequence = confirmed.Sequence, StateRevision = confirmed.View.StateRevision };

        private void OnCommandOutcome(CommandOutcomeMessage message)
        {
            if (checkpointChannel != null) return;
            if (NetworkServer.active || !IsCurrentClientConnection(message.ConnectionToken)) return;
            var outcome = JsonConvert.DeserializeObject<CommandAuthorityOutcome>(message.Json);
            if (outcome == null || outcome.ProtocolVersion != CommandAuthorityOutcome.CurrentProtocolVersion ||
                !MatchesPending(outcome.GameId, outcome.PlayerId, outcome.CommandId)) return;
            if (outcome.Outcome == CommandAuthorityOutcomeKind.Unknown)
            {
                ObserveCommandConfirmation("command_result_unknown", outcome.GameId, outcome.CommandId, exactPendingMatched: true);
                return;
            }
            if (outcome.StateRevision < 0 || outcome.Outcome != CommandAuthorityOutcomeKind.Accepted &&
                outcome.Outcome != CommandAuthorityOutcomeKind.Rejected ||
                outcome.Outcome == CommandAuthorityOutcomeKind.Accepted && outcome.AcceptedSequence <= 0) return;
            recoveredOutcome = outcome;
            TryCompleteRecoveredOutcome();
        }

        private void TryCompleteRecoveredOutcome()
        {
            var outcome = recoveredOutcome;
            if (outcome == null || awaitingInitialState || !dispatcher.IsInitialStateSynchronized ||
                !MatchesPending(outcome.GameId, outcome.PlayerId, outcome.CommandId) || !IsCurrentClientView(session.View)) return;
            if (session.View.StateRevision < outcome.StateRevision ||
                outcome.Outcome == CommandAuthorityOutcomeKind.Accepted && dispatcher.NextExpectedConfirmedSequence <= outcome.AcceptedSequence)
            {
                awaitingInitialState = true;
                RequestInitialState();
                return;
            }
            var command = pendingCommand;
            ObserveCommandConfirmation(outcome.Outcome == CommandAuthorityOutcomeKind.Accepted ? "command_result_accepted" : "command_result_rejected",
                outcome.GameId, outcome.CommandId, outcome.AcceptedSequence, true);
            ClearPendingCommand();
            awaitingInitialState = false;
            if (outcome.Outcome == CommandAuthorityOutcomeKind.Accepted)
                CoveredCommandAcknowledged?.Invoke(new ConfirmedGameStateViewDto {
                    Sequence = outcome.AcceptedSequence, Command = command, View = session.View });
            else
                CommandRejected?.Invoke(new RejectedGameCommandDto { Command = command,
                    ErrorCode = outcome.ErrorCode, Reason = outcome.Reason });
        }

        private void ClearPendingCommand()
        {
            pendingCommandId = null;
            pendingGameId = null;
            pendingCommand = null;
            recoveredOutcome = null;
            nextCommandOutcomeRequestTime = 0f;
        }

        private void RequestCommandOutcome()
        {
            if (!NetworkClient.isConnected || awaitingInitialState || pendingCommand == null ||
                !dispatcher.IsInitialStateSynchronized) return;
            float now = ReadUnscaledTime();
            if (now < nextCommandOutcomeRequestTime) return;
            NetworkClient.Send(new CommandOutcomeRequestMessage { GameId = pendingGameId, PlayerId = localPlayerId,
                CommandId = pendingCommandId, ConnectionToken = clientConnectionToken });
            nextCommandOutcomeRequestTime = now + InitialStateRequestInterval;
            ObserveCommandConfirmation("command_result_requested", pendingGameId, pendingCommandId);
        }

        private void RequestInitialState()
        {
            if (checkpointChannel != null) return;
            if (!NetworkClient.isConnected) return;
            float now = ReadUnscaledTime();
            if (now < nextInitialStateRequestTime) return;
            NetworkClient.Send(new InitialStateRequestMessage { ConnectionToken = clientConnectionToken });
            nextInitialStateRequestTime = now + InitialStateRequestInterval;
        }

        // 未连接时不进入Unity原生时钟；实际连接的节流继续使用同一unscaledTime。
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static float ReadUnscaledTime() => Time.unscaledTime;

        private void OnLocalClientConnected()
        {
            if (checkpointChannel != null)
            {
                clientConnectionToken = Guid.NewGuid().ToString("N"); nextCheckpointRequestTime = 0f;
                nextConfirmedRecoveryRequestTime = 0f;
                confirmedRecoveryRequestPending = simulationOptions != null && simulationCoordinator != null;
                protocolHost.Reset(); ResetConfirmedRecoveryReceive();
                if (simulationOptions != null && simulationCoordinator != null)
                { checkpointHostConnection = NetworkClient.connection; RequestConfirmedRecovery(); }
                else { BindCheckpointHost(); RequestCheckpoint(); }
                return;
            }
            // 新连接先同步，再按同局本人命令身份只读查回结果；未知业务命令不重发。
            ObserveCommandConfirmation("connection_pending_retained");
            clientConnectionToken = Guid.NewGuid().ToString("N");
            viewAssembler.Reset();
            awaitingInitialState = true;
            nextInitialStateRequestTime = 0f;
            nextCommandOutcomeRequestTime = 0f;
            RequestInitialState();
        }

        private void ObserveCommandConfirmation(string branch, string receivedGameId = null,
            string commandId = null, int? sequence = null, bool? exactPendingMatched = null,
            CommandResult applyResult = null)
        {
            var observers = CommandConfirmationObserved;
            if (observers == null) return;
            var diagnostic = new LocalCommandConfirmationDiagnostic(branch,
                session?.View?.GameId ?? session?.State?.GameId, receivedGameId, commandId, pendingCommandId,
                sequence, dispatcher?.NextExpectedConfirmedSequence, dispatcher?.IsInitialStateSynchronized,
                exactPendingMatched, applyResult);
            foreach (Action<LocalCommandConfirmationDiagnostic> observer in observers.GetInvocationList())
            {
                try { observer(diagnostic); }
                catch (Exception error) { Debug.LogException(error); }
            }
        }

        private void RegisterClientMessages()
        {
            if (mode != LaunchMode.Client) return;
            if (checkpointChannel != null)
            {
                RegisterCheckpointClientMessages();
                NetworkClient.OnConnectedEvent -= OnLocalClientConnected;
                NetworkClient.OnConnectedEvent += OnLocalClientConnected;
                clientConnectionToken = Guid.NewGuid().ToString("N"); nextCheckpointRequestTime = 0f;
                nextConfirmedRecoveryRequestTime = 0f;
                if (simulationOptions != null && simulationCoordinator != null)
                {
                    checkpointHostConnection = NetworkClient.connection; checkpointInstallationSent = false;
                    protocolHost.Reset(); ResetConfirmedRecoveryReceive();
                    if (NetworkClient.isConnected) OnLocalClientConnected();
                }
                else BindCheckpointHost();
                return;
            }
            viewAssembler.Reset();
            NetworkClient.RegisterHandler<GameStateViewPartMessage>(OnStateViewPart, false);
            NetworkClient.RegisterHandler<AcceptedCommandMessage>(OnAccepted, false);
            NetworkClient.RegisterHandler<RejectedCommandMessage>(OnRejected, false);
            NetworkClient.RegisterHandler<InitialStateMessage>(OnInitialState, false);
            NetworkClient.RegisterHandler<CommandOutcomeMessage>(OnCommandOutcome, false);
            NetworkClient.OnConnectedEvent -= OnLocalClientConnected;
            NetworkClient.OnConnectedEvent += OnLocalClientConnected;
            awaitingInitialState = true;
            clientConnectionToken = Guid.NewGuid().ToString("N");
            nextInitialStateRequestTime = 0f;
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
                session.State.GameId != simulationOptions.GameId || session.State.ContentPackHash != simulationOptions.ContentPackHash || fault != null && fault.FaultCode != "simulation_connection_lost" ||
                !runtime.TryBeginClientReconnect()) return false;
            if (!localConnectionRecoveryPending)
            {
                localConnectionRecoveryGameId = simulationOptions.GameId; localConnectionRecoveryEpoch = simulationOptions.SimulationEpoch;
                localConnectionRecoveryMembership = simulationOptions.MembershipVersion;
            }
            localConnectionRecoveryPending = true; LocalSimulationConnectionRecoveryFailureCode = string.Empty;
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
            if (session != null && session.State.GameId == checkpointChannel.Identity.GameId &&
                session.State.ContentPackHash == checkpointChannel.Identity.ContentPackHash) return true;
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
            nextCheckpointRequestTime = now + InitialStateRequestInterval;
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
            if (simulationOptions != null && (simulationOptions.SimulationEpoch != 1 || recoveryRequested || simulationCoordinator?.ValidatedThroughSequence > 0)) return;
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
                if (simulationOptions != null && simulationCoordinator == null)
                {
                    var coordinator = new DeterministicSimulationCoordinator(session, simulationOptions);
                    AttachSimulation(coordinator); coordinator.Begin();
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

        private void AttachSimulation(DeterministicSimulationCoordinator coordinator, bool initialReady = true)
        {
            simulationCoordinator = coordinator;
            Action<SimulationCommandAppliedDto> applied = value => { if (simulationCoordinator == coordinator) OnSimulationApplied(value); };
            Action<CommandValidationPrefixDto> validated = value => { if (simulationCoordinator == coordinator) OnSimulationValidated(value); };
            Action<SimulationWindowPermissionDto> permission = value => { if (simulationCoordinator == coordinator) OnSimulationPermission(value); };
            Action<SimulationProtocolFaultDto> fault = value => { if (simulationCoordinator == coordinator) OnSimulationFault(value); };
            Action<SimulationProposalResultDto> rejected = value => { if (simulationCoordinator == coordinator) OnSimulationProposalRejected(value); };
            Action<OrderedOperationCommandDto> ordered = value => { if (simulationCoordinator == coordinator) BroadcastSimulationOrdered(value); };
            Action<SimulationBoundaryDto> boundary = value => { if (simulationCoordinator == coordinator) BroadcastSimulationBoundary(value); };
            Action<SimulationWindowReadyDto> ready = value => { if (simulationCoordinator == coordinator) SendSimulationReady(value); };
            Action<SimulationWindowPermitDto> permit = value => { if (simulationCoordinator == coordinator) BroadcastSimulationPermit(value); };
            Action<SimulationMissingRangeRequestDto> missing = value => { if (simulationCoordinator == coordinator) SendSimulationMissing(value); };
            coordinator.SimulationCommandApplied += applied; coordinator.CommandValidationPrefixAdvanced += validated;
            coordinator.OperationWindowPermissionChanged += permission; coordinator.SimulationProtocolFaulted += fault;
            coordinator.SimulationProposalRejected += rejected; coordinator.OrderedCommandProduced += ordered;
            coordinator.BoundaryProduced += boundary; coordinator.WindowReadyProduced += ready;
            coordinator.WindowPermitProduced += permit; coordinator.MissingRangeRequested += missing;
            detachSimulationEvents = () => {
                coordinator.SimulationCommandApplied -= applied; coordinator.CommandValidationPrefixAdvanced -= validated;
                coordinator.OperationWindowPermissionChanged -= permission; coordinator.SimulationProtocolFaulted -= fault;
                coordinator.SimulationProposalRejected -= rejected; coordinator.OrderedCommandProduced -= ordered;
                coordinator.BoundaryProduced -= boundary; coordinator.WindowReadyProduced -= ready;
                coordinator.WindowPermitProduced -= permit; coordinator.MissingRangeRequested -= missing;
            };
            if (initialReady) DeterministicSimulationReady?.Invoke(coordinator);
        }

        private void DetachSimulation()
        {
            var coordinator = simulationCoordinator; if (coordinator == null) return;
            coordinator.Stop();
            detachSimulationEvents?.Invoke(); detachSimulationEvents = null;
            simulationCoordinator = null;
        }

        private void OnSimulationApplied(SimulationCommandAppliedDto applied)
        { if (applied != null && CurrentSimulationIdentity(applied.GameId, applied.SimulationEpoch, applied.MembershipVersion)) SimulationCommandApplied?.Invoke(applied); }
        private bool CurrentSimulationIdentity(string gameId, long epoch, int membership) => simulationOptions != null &&
            gameId == simulationOptions.GameId && epoch == simulationOptions.SimulationEpoch && membership == simulationOptions.MembershipVersion;
        private void OnSimulationPermission(SimulationWindowPermissionDto permission) => OperationWindowPermissionChanged?.Invoke(permission);
        private void OnSimulationProposalRejected(SimulationProposalResultDto result) => SimulationProposalRejected?.Invoke(result);
        private void OnSimulationValidated(CommandValidationPrefixDto prefix)
        { if (mode == LaunchMode.Host) BroadcastSimulation("validation-prefix", prefix); CommandValidationPrefixAdvanced?.Invoke(prefix); }
        private void OnSimulationFault(SimulationProtocolFaultDto fault)
        { if (mode == LaunchMode.Host) BroadcastSimulation("stop", fault); SimulationProtocolFaulted?.Invoke(fault); }
        private void BroadcastSimulationOrdered(OrderedOperationCommandDto ordered) => BroadcastSimulation("ordered", ordered);
        private void BroadcastSimulationBoundary(SimulationBoundaryDto boundary) => BroadcastSimulation("boundary", boundary);
        private void BroadcastSimulationPermit(SimulationWindowPermitDto permit) => BroadcastSimulation("window-permit", permit);
        private void SendSimulationReady(SimulationWindowReadyDto ready) => SendSimulationClient("window-ready", ready);
        private void SendSimulationMissing(SimulationMissingRangeRequestDto request) => SendSimulationClient("missing-range", request);

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
            byte[] bytes = GameStateViewTransfer.Encode(JsonConvert.SerializeObject(payload));
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
            if (part.Purpose == "confirmed-recovery") { ReceiveRecoveryClientControl(connection, part); return; }
            if (simulationCoordinator == null || simulationCoordinator.IsStopped || mode != LaunchMode.Host || !ValidProtocolHeader(part) ||
                !bindings.TryGetPlayer(connection == null ? -1 : connection.connectionId, out var playerId) || !IsBoundCheckpointClient(connection, playerId) ||
                !checkpointChannel.IsPeerInstalled(playerId) && !(recoveryFinished && recoveryInstalled.Contains(playerId)) || !checkpointConnectionTokens.TryGetValue(connection.connectionId, out var token) || token != part.ConnectionToken ||
                part.Purpose != "proposal" && part.Purpose != "window-ready" && part.Purpose != "missing-range") return;
            if (!protocolPeers.TryGetValue(connection.connectionId, out var assembler)) { assembler = new ProtocolAssembly(); protocolPeers.Add(connection.connectionId, assembler); }
            if (!assembler.Append(part, out var json)) { ProtocolFault("simulation_frame_invalid", "规定副本的协议分片冲突或无效。"); return; }
            if (json == null) return;
            try
            {
                if (part.Purpose == "proposal")
                {
                    var result = simulationCoordinator.AdmitProposal(playerId, JsonConvert.DeserializeObject<OperationCommandProposalDto>(json));
                    SendSimulationHost(connection, "proposal-result", result);
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
            if (part.Purpose == "confirmed-recovery") { ReceiveRecoveryHostControl(part); return; }
            if (simulationCoordinator == null || simulationCoordinator.IsStopped || mode != LaunchMode.Client || !ValidProtocolHeader(part) || !IsBoundCheckpointHost(part.ConnectionToken) ||
                part.Purpose != "ordered" && part.Purpose != "proposal-result" && part.Purpose != "boundary" && part.Purpose != "window-permit" &&
                part.Purpose != "validation-prefix" && part.Purpose != "range" && part.Purpose != "stop") return;
            if (!protocolHost.Append(part, out var json)) { ProtocolFault("simulation_frame_invalid", "Host协议分片冲突或无效。"); return; }
            if (json == null) return;
            try
            {
                switch (part.Purpose)
                {
                    case "ordered": simulationCoordinator.ReceiveOrdered(JsonConvert.DeserializeObject<OrderedOperationCommandDto>(json)); break;
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
                        foreach (var command in range.Commands) simulationCoordinator.ReceiveOrdered(command);
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
                nextConfirmedRecoveryRequestTime = now + InitialStateRequestInterval;
                var request = new SimulationRecoveryNoticeDto { GameId = simulationOptions.GameId, SimulationEpoch = simulationOptions.SimulationEpoch + 1,
                    SourceSimulationEpoch = simulationOptions.SimulationEpoch, MembershipVersion = simulationOptions.MembershipVersion, Phase = SimulationRecoveryPhase.Request };
                SendRecoveryClient(request);
            }
        }

        private void PollConfirmedRecovery()
        {
            if (!IsRecoveringConfirmedPrefix || recoveryFailed || mode != LaunchMode.Host || recoveryNotice != null) return;
            if (!simulationCoordinator.IsStopped && simulationCoordinator.ReadPendingTail().Count != 0) return;
            try
            {
                if (!simulationCoordinator.TryCreateConfirmedRecoveryCheckpoint(simulationCoordinator.ValidatedThroughSequence,
                    simulationOptions.SimulationEpoch + 1, out var checkpoint, out var result)) throw new InvalidOperationException(result.Reason);
                var b = checkpoint.Baseline;
                recoveryNotice = new SimulationRecoveryNoticeDto { GameId = b.GameId, SimulationEpoch = b.SimulationEpoch, MembershipVersion = b.MembershipVersion,
                    SourceSimulationEpoch = b.SourceSimulationEpoch, CheckpointId = b.CheckpointId, ConfirmedGlobalSequence = b.ConfirmedGlobalSequence,
                    MetadataDigest = b.MetadataDigest, Phase = SimulationRecoveryPhase.Prepare, ResumeAllowed = !simulationCoordinator.IsStopped };
                string json = SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint);
                var envelope = RecoveryEnvelope(recoveryNotice); envelope.ContentPackHash = simulationOptions.ContentPackHash;
                recoveryTransfer = SimulationCheckpointTransfer.Encode(envelope, json);
                if (!recoveryInstaller.TryPrepare(recoveryNotice, json, out preparedRecovery, out result)) throw new InvalidOperationException(result.Reason);
                recoveryPrepared.Add(localPlayerId);
                foreach (var connection in NetworkServer.connections.Values.Where(c => !(c is LocalConnectionToClient)).ToArray()) SendRecoveryPackage(connection);
                TryCommitAllRecoveredMembers();
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException)) { FailConfirmedRecovery("recovery_host_prepare_failed", exception.Message); }
        }

        private static SimulationCheckpointEnvelope RecoveryEnvelope(SimulationRecoveryNoticeDto notice) => new SimulationCheckpointEnvelope {
            ProtocolVersion = SimulationCheckpointEnvelope.RecoveryProtocolVersion, Purpose = SimulationCheckpointEnvelope.ConfirmedSimulationRecovery,
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
                    { recoveryFinished = true; localConnectionRecoveryPending = false; simulationCoordinator.CompleteConfirmedRecovery(notice.ResumeAllowed); SimulationRecoveryCompleted?.Invoke(simulationCoordinator); }
                    else if (notice.Phase == SimulationRecoveryPhase.Failed) FailConfirmedRecovery(notice.ErrorCode, notice.Reason);
                }
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException)) { FailConfirmedRecovery("recovery_control_invalid", exception.Message); }
        }

        private void ReceiveConfirmedRecoveryPart(SimulationCheckpointPartMessage part, SimulationCheckpointEnvelope envelope)
        {
            if (mode != LaunchMode.Client || recoveryNotice == null || recoveryFailed || !IsBoundCheckpointHost(part.ConnectionToken)) return;
            if (preparedRecovery != null)
            { if (!SimulationCheckpointEnvelope.SamePayload(receivedRecoveryEnvelope, envelope)) FailConfirmedRecovery("recovery_payload_conflict", "重复恢复包与已准备原包冲突。"); return; }
            if (recoveryAssembler == null) return;
            if (!recoveryAssembler.TryAppend(envelope, part.Offset, part.Payload, out var json, out var fault)) { FailConfirmedRecovery(fault, "恢复检查点组装失败。"); return; }
            if (json == null) return;
            receivedRecoveryEnvelope = envelope.Snapshot();
            if (!recoveryInstaller.TryPrepare(recoveryNotice, json, out preparedRecovery, out var result)) { FailConfirmedRecovery(result.ErrorCode, result.Reason); return; }
            SendRecoveryClient(RecoveryNotice(SimulationRecoveryPhase.Prepared));
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
            recoveryFinished = true; simulationCoordinator.CompleteConfirmedRecovery(recoveryNotice.ResumeAllowed);
            BroadcastRecovery(SimulationRecoveryPhase.Installed); SimulationRecoveryCompleted?.Invoke(simulationCoordinator);
        }

        private void FailConfirmedRecovery(string code, string reason)
        {
            if (recoveryFailed) return; recoveryFailed = true; simulationCoordinator?.Stop();
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
