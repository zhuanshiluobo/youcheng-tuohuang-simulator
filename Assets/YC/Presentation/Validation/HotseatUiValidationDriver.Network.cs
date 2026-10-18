#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.PlayerJourney;
using YC.Presentation.Workflows;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private bool NetworkMode => selectedCase == "network-full-game";
        private string networkRoot, networkRole;
        private readonly string[] networkRoles = { "host", "client2", "client3", "client4" };
        private FileStream networkLease;
        private MirrorCommandTransport networkTransport;
        private CommandSubmissionController networkSubmissionObserver;
        private string networkActiveStepId, networkReentryFromStep;
        private int networkStepOrdinal;
        private readonly HashSet<string> networkRetiredSteps = new HashSet<string>();
        private string networkFault;
        private bool networkTerminal, networkExported;
        private bool networkTerminalStateExported, networkTerminalBoundaryExported, networkTerminalAuditCompleted;
        private string networkBoundGameId, networkBoundContentHash, networkTerminalStrictFailure;
        private int networkBoundPlayerId;
        private NetworkConfirmedObservation networkLastConfirmation, networkInitialConfirmation;
        private SimulationProtocolFaultDto networkAwaitedConnectionFault;
        private SimulationRecoveryNoticeDto networkRecoveryNotice;
        private DeterministicSimulationSessionOptions networkRecoverySource;
        private DeterministicSimulationCoordinator networkInstalledRecovery;
        private long networkRecoveryConfirmedBefore;
        private bool networkRecoveryPaused;
        private float networkRecoveryPauseBegan, networkRecoveryPausedSeconds;
        // 沿用原有具体步骤等待预算；这是单次恢复等待，不是整局总时限。
        private const float NetworkRecoveryStepTimeout = 120f;
        private readonly HashSet<string> networkRecoveringPeers = new HashSet<string>();
        private long networkBarrierSequence;
        private long networkProgressSequence = -1;
        private float networkLastProgressAt;
        private readonly Dictionary<string, SimulationCommandAppliedDto> networkApplied = new Dictionary<string, SimulationCommandAppliedDto>();
        private readonly List<SimulationCommandAppliedDto> networkReceivedApplied = new List<SimulationCommandAppliedDto>();
        private readonly Dictionary<string, NetworkCommandRecord> networkCommands = new Dictionary<string, NetworkCommandRecord>();

        private sealed class NetworkConfirmedObservation
        {
            public string AtUtc, Source;
            public int Frame, Revision, Round;
            public GamePhase Phase;
            public long Applied, Validated;
            public bool ResolvedTerminal;
            public DeterministicSimulationSessionOptions Session;
            public SimulationBoundaryDto Boundary;
        }
        private sealed class NetworkTerminalReceipts
        {
            public string GameId, ContentHash;
            public int PlayerId;
            public long InitialSequence, FinalSequence;
            public SimulationBoundaryDto Boundary;
            public List<SimulationCommandAppliedDto> Receipts;
            public long[] MissingSequences, DuplicateSequences;
            public string[] UnresolvedOriginalCommands, RejectedOriginalCommands;
            public bool Complete, RecoveryObservationPending, OriginalPayloadsValid;
        }

        private bool NetworkHasResolvedTerminal() => session != null && State.Round == State.MaxRounds && State.MaxRounds == 8 &&
            (State.Phase == GamePhase.GameOver || State.Phase == GamePhase.FinalScoring) && State.FinalScoring?.IsResolved == true &&
            State.Players.Count == 4 && State.FinalScoring.PlayerScores.Count == State.Players.Count;

        private bool NetworkSameBoundSession(DeterministicSimulationSessionOptions options) => options != null &&
            options.GameId == networkBoundGameId && options.ContentPackHash == networkBoundContentHash &&
            options.LocalPlayerId == networkBoundPlayerId && State.GameId == networkBoundGameId &&
            State.ContentPackHash == networkBoundContentHash && LocalPlayer == networkBoundPlayerId &&
            options.SimulationEpoch > 0 && options.MembershipVersion > 0 && options.FrozenPlayerIds != null &&
            options.FrozenPlayerIds.OrderBy(id => id).SequenceEqual(State.Players.Select(player => player.PlayerId).OrderBy(id => id));

        private void NetworkRememberConfirmation(string source)
        {
            var coordinator = networkTransport?.SimulationCoordinator;
            if (coordinator == null) return;
            var options = coordinator.SessionOptions;
            if (!NetworkSameBoundSession(options)) { networkFault = "本机确认事实的对局、内容或玩家身份改变"; return; }
            long applied = coordinator.LastAppliedSequence, validated = coordinator.ValidatedThroughSequence;
            // 只保存本机真实读到且已确认的完整当前边界，不把尚未确认的状态配到旧前缀上。
            if (coordinator.IsStopped || coordinator.RequiresRecovery || applied != validated) return;
            var previous = networkLastConfirmation;
            bool terminal = NetworkHasResolvedTerminal();
            if (previous != null && previous.Validated == validated && previous.Session.SimulationEpoch == options.SimulationEpoch &&
                previous.Revision == State.EffectRuntime.StateRevision && previous.ResolvedTerminal == terminal) return;
            var boundary = coordinator.ReadBoundary(validated);
            if (boundary == null) return;
            if (boundary.ProtocolVersion != OperationCommandProtocol.Version || boundary.GameId != options.GameId ||
                boundary.SimulationEpoch != options.SimulationEpoch || boundary.MembershipVersion != options.MembershipVersion ||
                boundary.GlobalFenceSequence != validated || string.IsNullOrEmpty(boundary.CommandPrefixDigest) ||
                boundary.RuleStateDigestVersion != RuleStateDigestService.DigestVersion || string.IsNullOrEmpty(boundary.RuleStateDigest) ||
                string.IsNullOrEmpty(boundary.FactDigest))
            { networkFault = "本机实际确认边界与当前会话身份不匹配"; return; }
            if (previous != null && (validated < previous.Validated || options.SimulationEpoch < previous.Session.SimulationEpoch))
            { networkFault = "本机最后确认事实发生倒退，不能替换已记录边界"; return; }
            networkLastConfirmation = new NetworkConfirmedObservation { AtUtc = DateTime.UtcNow.ToString("o"), Source = source,
                Frame = Time.frameCount, Revision = State.EffectRuntime.StateRevision, Round = State.Round, Phase = State.Phase,
                Applied = applied, Validated = validated,
                ResolvedTerminal = terminal, Session = options.Snapshot(), Boundary = boundary };
            Write("本机最后确认事实.json", networkLastConfirmation);
        }

        private bool NetworkStopResolvedTerminalStep(Stack<IEnumerator> stack)
        {
            if (!NetworkHasResolvedTerminal() || networkActiveStepId == null ||
                !stack.Any(value => value is NetworkStepBoundary boundary && boundary.Id == networkActiveStepId)) return false;
            string stepId = networkActiveStepId;
            Append("终局停止原行动观察.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                StepId = stepId, State.GameId, State.Round, State.Phase, Revision = State.EffectRuntime.StateRevision,
                PendingOriginalCommands = networkCommands.Values.Where(value => value.Result == null).Select(value => value.Command.CommandId).ToArray(),
                OriginalCommandsRetiredOrCompletedByThisObservation = false,
                Reason = "真实四人终局已解析；停止旧行动的后续UI断言，原命令和缺失回执留给终局严格核验，不再申请下一行动" });
            NetworkDiscardStep(stack, stepId);
            networkActiveStepId = null;
            return true;
        }

        private sealed class NetworkCommandRecord
        {
            public string Stage;
            public int Frame, BeforeRevision, AfterRevision, LocalPlayerAfter;
            public long GlobalCommandSequence;
            public long SimulationEpoch;
            public string StepId;
            public bool RetiredByRecovery;
            public GameCommand Command;
            public WorkflowSubmissionResult Result;
        }
        private sealed class NetworkPeerStatus
        {
            public string Role, GameId, ContentHash, Phase, Stage;
            public int PlayerId, Round, Revision;
            public long Applied, Validated;
            public bool Terminal, Exported, TerminalAuditCompleted;
            public bool CanSubmit;
            public int InteractiveCards;
            public bool RecoveryPending;
            public SimulationProtocolFaultDto ConnectionInterruption;
            public SimulationRecoveryNoticeDto RecoveryNotice;
            public long SimulationEpoch;
            public int MembershipVersion;
        }
        private sealed class NetworkRoomCode { public string RoomCode; }
        private sealed class NetworkStepBoundary : IEnumerator, IDisposable
        {
            internal readonly string Id;
            private readonly IEnumerator body;
            internal NetworkStepBoundary(string id, IEnumerator body) { Id = id; this.body = body; }
            public object Current => body.Current;
            public bool MoveNext() => body.MoveNext();
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => (body as IDisposable)?.Dispose();
        }
        private IEnumerator NetworkWrapStep(IEnumerator body)
        {
            networkActiveStepId = networkRole + ":step:" + (++networkStepOrdinal);
            if (networkReentryFromStep != null)
            {
                Append("恢复后独立决策.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"),
                    Frame = Time.frameCount,
                    RetiredStepId = networkReentryFromStep, NewStepId = networkActiveStepId,
                    Current = ObserveSafely(), OriginalChoiceReused = false });
                networkReentryFromStep = null;
            }
            return new NetworkStepBoundary(networkActiveStepId, body);
        }
        private static void NetworkDiscardStep(Stack<IEnumerator> stack, string stepId)
        {
            if (!stack.Any(value => value is NetworkStepBoundary boundary && boundary.Id == stepId))
                throw new InvalidOperationException("原驱动步骤不在当前协程栈，不能越过边界退役");
            while (stack.Count > 0)
            {
                var value = stack.Pop(); (value as IDisposable)?.Dispose();
                if (value is NetworkStepBoundary boundary && boundary.Id == stepId) return;
            }
        }
        private bool NetworkRetireResolvedStep(Stack<IEnumerator> stack)
        {
            if (networkActiveStepId == null || !networkRetiredSteps.Contains(networkActiveStepId)) return false;
            string retired = networkActiveStepId;
            NetworkDiscardStep(stack, retired);
            Append("驱动步骤恢复退役.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"),
                Frame = Time.frameCount,
                StepId = retired, Stage = stage,
                OriginalCommandIds = networkCommands.Values.Where(value => value.StepId == retired && value.RetiredByRecovery)
                    .Select(value => value.Command.CommandId).ToArray(), OldContinuationExecuted = false });
            // 只丢弃验收器的旧选择缓存；生产UI保留自己的草稿和正式刷新行为。
            preparedMainAction = null; pendingMoveCompletion = null; paymentSourceContinuation = null;
            explorationPlayPlan = null; lastStylePlacement = null; stylePlacementCache.Clear(); stylePlacementRevision = -1;
            paymentSourceAttempts.Clear(); attemptedBuildPreparations.Clear();
            networkActiveStepId = null; networkReentryFromStep = retired;
            return true;
        }
        private string NetworkPath(string name) => Path.Combine(networkRoot, name);
        private T NetworkRead<T>(string name) where T : class
        {
            try
            {
                using var stream = new FileStream(NetworkPath(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
            }
            catch (IOException) { return null; }
            catch (JsonException) { return null; }
        }
        private void NetworkCheck()
        {
            Require(string.IsNullOrEmpty(networkFault), "联机真实协议故障：" + networkFault);
            Require(!File.Exists(NetworkPath("首个实例失败.json")), "其他实例已保留首败；停止操作，采集本实例现场");
            Require(string.IsNullOrEmpty(networkTransport?.LocalSimulationConnectionRecoveryFailureCode),
                "正式恢复失败：" + networkTransport?.LocalSimulationConnectionRecoveryFailureCode);
        }
        private static float NetworkActiveClock(float now, float previousPauses, bool paused, float pauseBegan) =>
            now - previousPauses - (paused ? now - pauseBegan : 0f);
        private float NetworkActiveTime => NetworkActiveClock(Time.realtimeSinceStartup,
            networkRecoveryPausedSeconds, networkRecoveryPaused, networkRecoveryPauseBegan);
        private static bool NetworkRecoveryFaultCanWait(SimulationProtocolFaultDto fault,
            DeterministicSimulationSessionOptions source, bool officiallyAccepted) => officiallyAccepted && fault != null && source != null &&
            fault.ProtocolVersion == OperationCommandProtocol.Version && fault.FaultCode == "simulation_connection_lost" && fault.RequiresRecovery &&
            fault.GameId == source.GameId && fault.SimulationEpoch == source.SimulationEpoch && fault.MembershipVersion == source.MembershipVersion;
        private static bool NetworkRecoveryNoticeMatches(SimulationRecoveryNoticeDto notice, DeterministicSimulationSessionOptions source) =>
            notice != null && source != null && notice.ProtocolVersion == OperationCommandProtocol.Version && notice.GameId == source.GameId &&
            source.SimulationEpoch < long.MaxValue && notice.SourceSimulationEpoch == source.SimulationEpoch &&
            notice.SimulationEpoch == source.SimulationEpoch + 1 && notice.MembershipVersion == source.MembershipVersion;
        private static bool NetworkRecoveryInstalledIdentityMatches(DeterministicSimulationSessionOptions source,
            SimulationRecoveryNoticeDto notice, DeterministicSimulationSessionOptions installed) =>
            NetworkRecoveryNoticeMatches(notice, source) && installed != null &&
            installed.GameId == source.GameId && installed.ContentPackHash == source.ContentPackHash &&
            installed.SimulationEpoch == notice.SimulationEpoch && installed.MembershipVersion == source.MembershipVersion &&
            installed.LocalPlayerId == source.LocalPlayerId && installed.IsHost == source.IsHost;
        private static bool NetworkConfirmedRecoveryCanResume(DeterministicSimulationSessionOptions source, SimulationRecoveryNoticeDto notice,
            DeterministicSimulationSessionOptions installed, bool completedEventMatches, bool awaitingConnection, bool recoveringPrefix,
            bool synchronizing, bool stopped, bool requiresRecovery, bool submissionReady, long applied, long validated,
            long confirmedBefore, SimulationBoundaryDto boundary) =>
            NetworkRecoveryInstalledIdentityMatches(source, notice, installed) && completedEventMatches &&
            installed.CheckpointGlobalSequence >= confirmedBefore && installed.CheckpointGlobalSequence >= notice.ConfirmedGlobalSequence &&
            !awaitingConnection && !recoveringPrefix && !synchronizing && !stopped && !requiresRecovery && submissionReady &&
            applied == validated && validated >= installed.CheckpointGlobalSequence && boundary != null &&
            boundary.ProtocolVersion == OperationCommandProtocol.Version && boundary.GameId == installed.GameId &&
            boundary.SimulationEpoch == installed.SimulationEpoch && boundary.MembershipVersion == installed.MembershipVersion &&
            boundary.GlobalFenceSequence == validated;
        private void NetworkCaptureRecoverySource()
        {
            if (networkRecoverySource != null) return;
            networkRecoverySource = networkTransport.SimulationCoordinator.SessionOptions;
            networkRecoveryConfirmedBefore = networkTransport.SimulationCoordinator.ValidatedThroughSequence;
        }
        private bool NetworkPeerAwaitingRecovery()
        {
            var options = networkTransport.SimulationCoordinator?.SessionOptions;
            if (options == null) return false;
            bool waiting = false;
            foreach (string role in networkRoles.Where(value => value != networkRole))
            {
                var peer = NetworkRead<NetworkPeerStatus>("状态-" + role + ".json");
                // File.WriteAllText发布中可能暂时读不到完整JSON；不能因此解除已观察到的正式等待。
                if (peer == null) { waiting |= networkRecoveringPeers.Contains(role); continue; }
                if (!peer.RecoveryPending && !networkRecoveringPeers.Contains(role)) continue;
                Require(peer.GameId == options.GameId && peer.ContentHash == options.ContentPackHash &&
                    peer.MembershipVersion == options.MembershipVersion &&
                    (peer.SimulationEpoch == options.SimulationEpoch || peer.SimulationEpoch == options.SimulationEpoch + 1 ||
                     peer.SimulationEpoch + 1 == options.SimulationEpoch), "其他实例恢复等待身份不匹配，不能以其状态暂停或继续本局");
                // 仅暂停本机原控件；恢复与其期限仍由该实例的正式生产入口处理。
                if (peer.RecoveryPending) { networkRecoveringPeers.Add(role); waiting = true; }
                else networkRecoveringPeers.Remove(role);
            }
            return waiting;
        }
        private bool NetworkPauseForRecovery()
        {
            NetworkCheck();
            // 终局脱离是产品的正常生命周期；不把它当作恢复成功，也不清除尚未完成的恢复观察。
            if (NetworkHasResolvedTerminal() && networkTransport?.SimulationCoordinator == null)
            { NetworkPublish(); return false; }
            if (networkInstalledRecovery != null)
            {
                var coordinator = networkTransport.SimulationCoordinator;
                Require(ReferenceEquals(coordinator, networkInstalledRecovery) && coordinator != null &&
                    !coordinator.IsStopped && !coordinator.RequiresRecovery, "正式恢复完成通知没有可继续的同一协调器");
                if (NetworkConfirmedRecoveryCanResume(networkRecoverySource, networkRecoveryNotice, coordinator.SessionOptions,
                    true, networkTransport.IsAwaitingLocalSimulationConnectionRecovery, networkTransport.IsRecoveringConfirmedPrefix,
                    networkTransport.IsSynchronizing, coordinator.IsStopped, coordinator.RequiresRecovery,
                    (Read(mobile, "commandSubmission") as CommandSubmissionController)?.IsSimulationReady == true,
                    coordinator.LastAppliedSequence, coordinator.ValidatedThroughSequence, networkRecoveryConfirmedBefore,
                    coordinator.ReadBoundary(coordinator.ValidatedThroughSequence)))
                {
                    Append("联机正式恢复完成.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                        Role = networkRole, Source = networkRecoverySource, Notice = networkRecoveryNotice,
                        Installed = coordinator.SessionOptions, coordinator.LastAppliedSequence, coordinator.ValidatedThroughSequence,
                        Boundary = coordinator.ReadBoundary(coordinator.ValidatedThroughSequence),
                        CheckpointBoundary = coordinator.ReadBoundary(coordinator.SessionOptions.CheckpointGlobalSequence),
                        PendingOriginalCommands = networkCommands.Values.Where(value => value.Result == null).Select(value => value.Command.CommandId).ToArray(),
                        OriginalCommandsResubmitted = false });
                    networkAwaitedConnectionFault = null; networkRecoveryNotice = null;
                    networkRecoverySource = null; networkInstalledRecovery = null;
                }
            }
            bool local = networkAwaitedConnectionFault != null || networkRecoveryNotice != null;
            if (local)
            {
                var options = networkTransport.SimulationCoordinator?.SessionOptions;
                Require(options != null && networkRecoverySource != null && State.GameId == networkRecoverySource.GameId &&
                    State.ContentPackHash == networkRecoverySource.ContentPackHash && options.GameId == networkRecoverySource.GameId &&
                    options.ContentPackHash == networkRecoverySource.ContentPackHash && options.LocalPlayerId == networkRecoverySource.LocalPlayerId &&
                    options.MembershipVersion == networkRecoverySource.MembershipVersion &&
                    (options.SimulationEpoch == networkRecoverySource.SimulationEpoch || options.SimulationEpoch == networkRecoverySource.SimulationEpoch + 1),
                    "正式恢复等待期间身份发生改变，不能继续原对局");
            }
            bool peer = NetworkPeerAwaitingRecovery();
            if (local || peer)
            {
                if (!networkRecoveryPaused)
                {
                    networkRecoveryPaused = true; networkRecoveryPauseBegan = Time.realtimeSinceStartup;
                    Append("联机恢复等待.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                        Role = networkRole, Stage = stage, Phase = "暂停原控件", Local = local, Peer = peer,
                        StepTimeoutSeconds = NetworkRecoveryStepTimeout, NoGameDurationLimit = true });
                }
                if (Time.realtimeSinceStartup - networkRecoveryPauseBegan >= NetworkRecoveryStepTimeout)
                {
                    string phase = networkInstalledRecovery != null ? "已收到安装事件，等待确认边界和消费者就绪" :
                        networkRecoveryNotice != null ? "等待确认前缀安装完成事件" :
                        networkAwaitedConnectionFault != null ? "正式接纳中断后，等待确认前缀恢复开始" : "等待其他实例完成正式恢复观察";
                    Write("恢复验证等待未完成.json", new { AtUtc = DateTime.UtcNow.ToString("o"), Role = networkRole,
                        Stage = stage, Phase = phase, ObservationSeconds = NetworkRecoveryStepTimeout,
                        DriverObservationOnly = true, ProductRecoveryTimeout = false,
                        Fault = networkAwaitedConnectionFault, Notice = networkRecoveryNotice,
                        AwaitingConnection = networkTransport.IsAwaitingLocalSimulationConnectionRecovery,
                        RecoveringPrefix = networkTransport.IsRecoveringConfirmedPrefix, networkTransport.IsSynchronizing,
                        networkTransport.LocalSimulationConnectionRecoveryFailureCode,
                        PendingOriginalCommands = networkCommands.Values.Where(value => value.Result == null).Select(value => value.Command.CommandId).ToArray() });
                    throw new InvalidOperationException("恢复验证等待未完成：" + phase +
                        "；120秒具体步骤观察边界已耗尽，不代表产品全恢复超时；未重发命令或替换状态");
                }
                if (networkAwaitedConnectionFault != null && networkRecoveryNotice == null)
                    Require(networkTransport.CanAwaitLocalSimulationConnectionRecovery(networkAwaitedConnectionFault),
                        "原断线的正式恢复接纳已丢失，且未进入确认前缀恢复；不能把等待标志消失当作恢复完成");
                NetworkPublish();
                return true;
            }
            if (networkRecoveryPaused)
            {
                networkRecoveryPausedSeconds += Time.realtimeSinceStartup - networkRecoveryPauseBegan;
                networkRecoveryPaused = false;
                Append("联机恢复等待.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                    Role = networkRole, Stage = stage, Phase = "原等待接续", OriginalCommandsResubmitted = false });
                NetworkPublish();
            }
            return false;
        }
        private IEnumerator NetworkUntil(Func<bool> condition, string description, float timeout = 120f)
        {
            float began = NetworkActiveTime;
            while (!condition())
            {
                NetworkCheck();
                Require(NetworkActiveTime - began < timeout, description + "超时，未调用服务API或注入状态绕过");
                if (session != null) NetworkPublish();
                yield return new WaitForSecondsRealtime(.2f);
            }
            yield return Settle();
        }
        private IEnumerator NetworkRoomFlow(StartMenuView menu)
        {
            networkRoot = Argument("--yc-network-ui-root=");
            networkRole = Argument("--yc-network-ui-role=");
            Require(Path.IsPathRooted(networkRoot ?? "") && Directory.Exists(networkRoot) && networkRoles.Contains(networkRole), "联机验收输出及角色参数不完整");
            Require(LocalMirrorTestMode.IsEnabled && !Environment.GetCommandLineArgs().Any(arg => arg.StartsWith("--yc-dev-local-mirror", StringComparison.Ordinal)),
                "只选本地Mirror后端，不启用绕过菜单的自动建房/加入入口");
            if (networkRole == "host")
            {
                stage = "联机房主正常菜单建房";
                yield return Press(menu.StartGameButton, "联机房主原创建房间按钮");
                yield return NetworkUntil(() => menu.RoomPanel.gameObject.activeInHierarchy, "原房间页面出现");
                var room = menu.RoomPanel;
                var options = (StartMenuRoomPanelView.ContentOption[])Read(room, "expansions");
                foreach (string id in new[] { "enterprise", "infected" })
                {
                    var option = options.Single(value => value.Id == id);
                    if (!option.Selection.gameObject.activeSelf) yield return Press(option.Button, "联机房间启用" + id);
                }
                Require(room.DisplayedRoom?.Configuration.MapId == "map-four-players", "原菜单没有建立四人地图房间");
                yield return Capture("联机房主真实房间及感染者配置");
                File.WriteAllText(NetworkPath("正式房间号.json"), JsonConvert.SerializeObject(new NetworkRoomCode { RoomCode = room.DisplayedRoom.RoomId }));
                yield return NetworkUntil(() => room.CanStartCurrentRoom && room.StartButton.IsInteractable(), "四名真实成员连接、身份验证及准备", 180f);
                Require(room.DisplayedRoom.Seats.Count == 4, "房间未到齐四名真实成员");
                yield return Capture("四名联机成员原房间准备完成");
                yield return Press(room.StartButton, "房主原开始游戏按钮");
            }
            else
            {
                stage = "联机成员正常菜单加入";
                yield return NetworkUntil(() => NetworkRead<NetworkRoomCode>("正式房间号.json") != null, "等待房主页面显示正式房间号");
                string code = NetworkRead<NetworkRoomCode>("正式房间号.json").RoomCode;
                int separator = code.LastIndexOf(':');
                Require(separator > 0, "正式本地房间号没有端口");
                code = "127.0.0.1" + code.Substring(separator);
                yield return Press(menu.OnlineModeButton, "成员原加入房间按钮");
                var input = menu.JoinPanel.RoomCodeInput;
                yield return PointerSequence(input.gameObject, "原房间号输入框取得焦点");
                input.ProcessEvent(Event.KeyboardEvent("^a"));
                foreach (char value in code) input.ProcessEvent(new Event { type = EventType.KeyDown, character = value, keyCode = KeyCode.None });
                Require(input.text == code, "原输入框字符事件未产生完整房间号");
                Append("控件事件.jsonl", new { Stage = stage, Input = "原InputField.ProcessEvent字符输入，非直接改text", RoomCode = code });
                yield return Press(menu.JoinPanel.JoinButton, "成员原连接房间按钮");
                yield return NetworkUntil(() => menu.RoomPanel.gameObject.activeInHierarchy && menu.RoomPanel.StartButton.IsInteractable(), "成员正常进入并通过房间门禁");
                yield return Capture("联机成员原房间准备前");
                yield return Press(menu.RoomPanel.StartButton, "成员原准备按钮");
                yield return NetworkUntil(() => FindObjectOfType<MobileCityInteractionController>() != null, "等待房主原按钮开局", 180f);
            }
        }
        private IEnumerator NetworkBindGameplay()
        {
            networkBoundGameId = State.GameId; networkBoundContentHash = State.ContentPackHash; networkBoundPlayerId = LocalPlayer;
            networkTransport = FindObjectOfType<MirrorCommandTransport>();
            Require(networkTransport != null, "正式联网场景没有原Mirror传输组件，不由驱动代建");
            networkTransport.SimulationCommandApplied += NetworkOnApplied;
            networkTransport.SimulationProposalRejected += NetworkOnRejected;
            networkTransport.SimulationProtocolFaulted += NetworkOnFault;
            networkTransport.CommandValidationPrefixAdvanced += NetworkOnPrefix;
            networkTransport.SimulationRecoveryStarted += NetworkOnRecoveryStarted;
            networkTransport.SimulationRecoveryCompleted += NetworkOnRecoveryCompleted;
            networkSubmissionObserver = Read(mobile, "commandSubmission") as CommandSubmissionController;
            Require(networkSubmissionObserver != null, "缺少正式准确回执观察源");
            networkSubmissionObserver.SubmissionResolutionObserved += NetworkOnResolutionObserved;
            yield return NetworkUntil(() => networkTransport.SimulationCoordinator != null &&
                (Read(mobile, "commandSubmission") as CommandSubmissionController)?.IsSimulationReady == true,
                "确定性联机初始检查点及本人投影就绪");
            NetworkRememberConfirmation("本机正式初始检查点就绪");
            Require(networkLastConfirmation != null, "正式初始检查点缺少本机同局确认边界，不能默认序列或借用其他实例");
            if (networkInitialConfirmation == null) networkInitialConfirmation = networkLastConfirmation;
            NetworkPublish();
            yield return NetworkUntil(() => networkRoles.All(role => NetworkRead<NetworkPeerStatus>("状态-" + role + ".json")?.GameId == State.GameId), "四个独立实例进入同一正式GameId");
            Write("联机运行身份.json", new { Role = networkRole, LocalPlayer, State.GameId, State.ContentPackHash,
                Mode = GameLaunchContext.Instance.Mode.ToString(), ExpectedPlayers = 4, NormalMenuFlow = true,
                FixtureState = false, DirectServiceCalls = false, DirectSubmission = false });
        }
        private void NetworkPublish()
        {
            if (session == null) return;
            var coordinator = networkTransport?.SimulationCoordinator;
            NetworkRememberConfirmation("本机状态发布时实际读取");
            var confirmation = networkLastConfirmation;
            if (networkInitialConfirmation == null && confirmation != null &&
                (Read(mobile, "commandSubmission") as CommandSubmissionController)?.IsSimulationReady == true)
                networkInitialConfirmation = confirmation;
            // 对外发布本人已进入对局之前先固定本机初始边界，避免把后续动作配成读档起点。
            if (networkInitialConfirmation == null) return;
            // 仅真实终局允许引用本机已观察的同局确认事实；非终局仍等待正式恢复或由原许可入口报告缺失。
            bool detachedTerminal = coordinator == null && NetworkHasResolvedTerminal() && confirmation?.ResolvedTerminal == true &&
                NetworkSameBoundSession(confirmation.Session) && confirmation.Revision == State.EffectRuntime.StateRevision;
            if (coordinator == null && !detachedTerminal) return;
            var options = coordinator != null ? coordinator.SessionOptions : confirmation.Session;
            File.WriteAllText(NetworkPath("状态-" + networkRole + ".json"), JsonConvert.SerializeObject(new NetworkPeerStatus {
                Role = networkRole, GameId = State.GameId, ContentHash = State.ContentPackHash, PlayerId = LocalPlayer,
                Round = State.Round, Phase = State.Phase.ToString(), Stage = stage, Revision = State.EffectRuntime.StateRevision,
                Applied = coordinator != null ? coordinator.LastAppliedSequence : confirmation.Applied,
                Validated = coordinator != null ? coordinator.ValidatedThroughSequence : confirmation.Validated,
                Terminal = networkTerminal, Exported = networkExported, TerminalAuditCompleted = networkTerminalAuditCompleted,
                CanSubmit = (Read(mobile, "commandSubmission") as CommandSubmissionController)?.CanSubmitForLocalPlayer == true,
                InteractiveCards = NetworkInteractiveCardCount(),
                RecoveryPending = networkAwaitedConnectionFault != null || networkRecoveryNotice != null,
                ConnectionInterruption = networkAwaitedConnectionFault, RecoveryNotice = networkRecoveryNotice,
                SimulationEpoch = options.SimulationEpoch, MembershipVersion = options.MembershipVersion }));
        }
        private bool NetworkTryBeginStep()
        {
            if (NetworkHasResolvedTerminal()) return false;
            NetworkCheck(); NetworkPublish();
            var coordinator = networkTransport?.SimulationCoordinator;
            Require(coordinator != null, "非终局缺少正式联机协调器；不能使用缓存边界申请行动许可");
            long sequence = coordinator.LastAppliedSequence;
            if (sequence != networkProgressSequence)
            { networkProgressSequence = sequence; networkLastProgressAt = NetworkActiveTime; }
            if (NetworkActiveTime - networkLastProgressAt > 90f)
            {
                Write("联机停滞原件.json", new { State, LocalPlayer, Sequence = sequence,
                    Boundary = coordinator.ReadBoundary(coordinator.ValidatedThroughSequence),
                    Peers = networkRoles.Select(role => NetworkRead<NetworkPeerStatus>("状态-" + role + ".json")).ToArray(),
                    CurrentUi = ObserveSafely(), Reason = "90秒无任何正式Applied命令；不绕过原控件，不强制刷新" });
                throw new InvalidOperationException("联机90秒没有任何正式命令推进；已保存四端许可、本人实际控件及完整规则状态");
            }
            if ((Read(mobile, "commandSubmission") as CommandSubmissionController)?.CanSubmitForLocalPlayer != true) return false;
            // 盖牌由专门角色页管理，不经过通用路由；只交给实际可点的本人原牌面控件。
            if (State.Phase == GamePhase.CharacterCover && NetworkInteractiveCardCount() == 0) return false;
            try { networkLease = new FileStream(NetworkPath("原控件步骤租约"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return false; }
            byte[] owner = Encoding.UTF8.GetBytes(networkRole + " " + System.Diagnostics.Process.GetCurrentProcess().Id + " " + stage);
            networkLease.SetLength(0); networkLease.Write(owner, 0, owner.Length); networkLease.Flush();
            return true;
        }
        private void NetworkEndStep() { networkLease?.Dispose(); networkLease = null; networkActiveStepId = null; NetworkPublish(); }
        private int NetworkInteractiveCardCount() => FindObjectsOfType<PlayerAutomationId>().Count(value =>
            value.Id.StartsWith("selection.card.", StringComparison.Ordinal) && value.GetComponent<Button>()?.IsInteractable() == true);
        private void NetworkOnApplied(SimulationCommandAppliedDto applied)
        {
            if (applied?.Command == null) return;
            Append("四端全局应用回执.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, Role = networkRole, Applied = applied });
            networkReceivedApplied.Add(applied);
            networkApplied[applied.Command.CommandId] = applied;
            NetworkResolve(applied.Command.CommandId);
            NetworkPublish();
        }
        private void NetworkOnPrefix(CommandValidationPrefixDto prefix)
        { Append("四端确认前缀.jsonl", new { Frame = Time.frameCount, Role = networkRole, Prefix = prefix }); NetworkPublish(); }
        private void NetworkOnFault(SimulationProtocolFaultDto fault)
        {
            Append("联机真实协议故障.jsonl", fault);
            var source = networkRecoverySource ?? networkTransport?.SimulationCoordinator?.SessionOptions;
            if (NetworkRecoveryFaultCanWait(fault, source, networkTransport?.CanAwaitLocalSimulationConnectionRecovery(fault) == true))
            {
                NetworkCaptureRecoverySource();
                networkAwaitedConnectionFault = JsonConvert.DeserializeObject<SimulationProtocolFaultDto>(JsonConvert.SerializeObject(fault));
                var interruption = new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                    Role = networkRole, Stage = stage, Fault = fault, FormallyAccepted = true,
                    PendingOriginalCommands = networkCommands.Values.Where(value => value.Result == null).Select(value => value.Command).ToArray() };
                Append("联机原始中断.jsonl", interruption);
                NetworkPreserveFirstInterruption(interruption);
                NetworkPublish();
                return;
            }
            networkFault = JsonConvert.SerializeObject(fault);
        }
        private void NetworkPreserveFirstInterruption(object interruption)
        {
            foreach (string path in new[] { Path.Combine(output, "首次联机中断.json"), NetworkPath("首个实例中断.json") })
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream);
                    writer.Write(JsonConvert.SerializeObject(interruption));
                }
                catch (IOException) when (File.Exists(path)) { }
            }
        }
        private void NetworkOnRecoveryStarted(SimulationRecoveryNoticeDto notice)
        {
            Append("联机正式恢复事件.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Role = networkRole, Event = "Started", Notice = notice });
            var source = networkRecoverySource ?? networkTransport.SimulationCoordinator?.SessionOptions;
            if (!NetworkRecoveryNoticeMatches(notice, source) || source.GameId != State.GameId || source.ContentPackHash != State.ContentPackHash)
            { networkFault = "正式恢复开始通知身份不匹配：" + JsonConvert.SerializeObject(notice); return; }
            NetworkCaptureRecoverySource();
            networkRecoveryNotice = JsonConvert.DeserializeObject<SimulationRecoveryNoticeDto>(JsonConvert.SerializeObject(notice));
            NetworkPublish();
        }
        private void NetworkOnRecoveryCompleted(DeterministicSimulationCoordinator coordinator)
        {
            Append("联机正式恢复事件.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Role = networkRole,
                Event = "Installed", Installed = coordinator?.SessionOptions, Stopped = coordinator?.IsStopped,
                RequiresRecovery = coordinator?.RequiresRecovery });
            if (coordinator == null || !ReferenceEquals(networkTransport.SimulationCoordinator, coordinator) ||
                !NetworkRecoveryInstalledIdentityMatches(networkRecoverySource, networkRecoveryNotice, coordinator.SessionOptions))
            { networkFault = "正式恢复完成通知缺少同一次已接纳的确认前缀恢复"; return; }
            networkInstalledRecovery = coordinator;
            NetworkRememberConfirmation("本机正式恢复安装完成回调");
        }
        private void NetworkOnRejected(SimulationProposalResultDto rejected)
        {
            Append("联机提案回执.jsonl", rejected);
            if (rejected?.Proposal?.Command == null || rejected.Status == OperationAdmissionStatus.Accepted || rejected.Status == OperationAdmissionStatus.Duplicate) return;
            string id = rejected.Proposal.Command.CommandId;
            if (networkCommands.TryGetValue(id, out var record) && record.Result == null)
            {
                record.Result = new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(rejected.ErrorCode, rejected.Reason)), false);
                record.AfterRevision = State.EffectRuntime.StateRevision;
                Append("命令与准确回执.jsonl", record);
            }
            networkFault = "正式提案被拒绝：" + rejected.Reason;
        }
        private static bool NetworkCanRetireForRecovery(GameCommand original, bool pending, long originalEpoch,
            string originalStep, string activeStep, string gameId, string commandId, bool accepted, CommandErrorCode errorCode, bool requiresRefresh,
            DeterministicSimulationSessionOptions source, SimulationRecoveryNoticeDto notice, DeterministicSimulationSessionOptions installed) =>
            original != null && pending && !string.IsNullOrEmpty(originalStep) && originalStep == activeStep &&
            source != null && originalEpoch == source.SimulationEpoch && original.PlayerId == source.LocalPlayerId &&
            gameId == source.GameId && commandId == original.CommandId && !accepted &&
            errorCode == CommandErrorCode.StateRevisionMismatch && requiresRefresh &&
            NetworkRecoveryInstalledIdentityMatches(source, notice, installed);
        private void NetworkOnResolutionObserved(string gameId, string commandId, bool accepted, CommandErrorCode errorCode, bool requiresRefresh)
        {
            try
            {
                var callback = new { GameId = gameId, CommandId = commandId, Accepted = accepted,
                    ErrorCode = errorCode, ErrorName = errorCode.ToString(), RequiresRefresh = requiresRefresh };
                Append("原命令准确回执观察.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Callback = callback });
                if (accepted || !networkCommands.TryGetValue(commandId, out var record)) return;
                var installed = networkTransport.SimulationCoordinator?.SessionOptions;
                if (NetworkCanRetireForRecovery(record.Command, record.Result == null, record.SimulationEpoch,
                    record.StepId, networkActiveStepId, gameId, commandId, accepted, errorCode, requiresRefresh,
                    networkRecoverySource, networkRecoveryNotice, installed))
                {
                    record.RetiredByRecovery = true; networkRetiredSteps.Add(record.StepId);
                    Append("恢复退役原命令.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, record.StepId,
                        Original = record.Command, record.SimulationEpoch, Callback = callback,
                        Source = networkRecoverySource, Notice = networkRecoveryNotice, Installed = installed,
                        AppliedLocally = false, RuleResult = (object)null });
                    return;
                }
                networkFault = "准确回执真实拒绝，非同次正式恢复退役：" + JsonConvert.SerializeObject(callback);
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            { networkFault = "验收准确回执观察异常：" + exception; }
        }
        private void NetworkObserveCommand(GameCommand command, WorkflowSubmissionResult immediate, int before)
        {
            var record = new NetworkCommandRecord { Stage = stage, Frame = Time.frameCount, Command = command,
                BeforeRevision = before, LocalPlayerAfter = LocalPlayer, StepId = networkActiveStepId,
                SimulationEpoch = networkTransport.SimulationCoordinator.SessionOptions.SimulationEpoch };
            commands.Add(record);
            Require(!networkCommands.ContainsKey(command.CommandId), "原UI重复提交相同CommandId，保留原件");
            networkCommands.Add(command.CommandId, record);
            Append("本机原控件提案.jsonl", new { record.Stage, record.Frame, record.StepId, record.SimulationEpoch,
                Command = command, Immediate = immediate, BeforeRevision = before });
            if (!immediate.CommandResult.Succeeded)
            {
                record.Result = immediate; record.AfterRevision = State.EffectRuntime.StateRevision;
                Append("命令与准确回执.jsonl", record);
            }
            else NetworkResolve(command.CommandId);
        }
        private void NetworkResolve(string commandId)
        {
            if (!networkCommands.TryGetValue(commandId, out var record) || record.Result != null || !networkApplied.TryGetValue(commandId, out var applied)) return;
            if (record.RetiredByRecovery) { networkFault = "已准确退役的原命令又收到Applied，需保留原件核对，不能改写旧拒绝"; return; }
            record.Result = NetworkExactAppliedResult(record.Command, applied, State.GameId);
            Require(record.Result != null, "准确回执没有匹配原命令身份，不能解除待决或重发原答案");
            record.AfterRevision = State.EffectRuntime.StateRevision; record.GlobalCommandSequence = applied.GlobalCommandSequence;
            Append("命令与准确回执.jsonl", record);
            RecordPlayCommand(record.Command, record.Result);
        }
        private static WorkflowSubmissionResult NetworkExactAppliedResult(GameCommand original, SimulationCommandAppliedDto applied, string gameId)
        {
            if (original == null || applied?.Command == null || applied.RuleResult == null ||
                applied.ProtocolVersion != OperationCommandProtocol.Version || applied.GameId != gameId ||
                applied.Command.CommandId != original.CommandId || applied.Command.PlayerId != original.PlayerId ||
                applied.Command.Kind != original.Kind) return null;
            return new WorkflowSubmissionResult(applied.RuleResult, applied.RuleResult.Succeeded);
        }
        private IEnumerator NetworkAwaitCommands()
        {
            if (!NetworkMode || networkTransport == null) yield break;
            if (networkCommands.Values.Any(record => record.Result == null && !record.RetiredByRecovery))
                yield return NetworkUntil(() => networkCommands.Values.All(record => record.Result != null || record.RetiredByRecovery), "原命令真实联机Applied或准确恢复退役回执", 45f);
            // Guarded在恢复就绪后会先弹出整个旧步骤，不能执行此处之后的旧选择断言。
            if (networkActiveStepId != null && networkRetiredSteps.Contains(networkActiveStepId)) yield break;
            var rejected = networkCommands.Values.FirstOrDefault(record => record.Result != null &&
                (!record.Result.CommandResult.Succeeded || !record.Result.AppliedLocally));
            Require(rejected == null, "原控件命令真实拒绝：" + rejected?.Result.CommandResult.Validation?.Reason);
            var coordinator = networkTransport.SimulationCoordinator;
            Require(coordinator != null, "原命令等待缺少正式协调器；真实终局应进入独立导出，不把缺失回执当作已完成");
            long target = coordinator.LastAppliedSequence;
            if (target <= networkBarrierSequence) yield break;
            yield return NetworkUntil(() => networkRoles.All(role => {
                var peer = NetworkRead<NetworkPeerStatus>("状态-" + role + ".json");
                return peer?.GameId == State.GameId && peer.Applied >= target && peer.Validated >= target;
            }), "四端应用及确认前缀收齐到" + target, 60f);
            networkBarrierSequence = target;
        }
        private IEnumerator NetworkTerminalBarrier()
        {
            Require(NetworkHasResolvedTerminal(), "没有真实八回合四人计分，不得导出为终局");
            networkTerminal = true; NetworkPublish();
            Write("联机正式终局规则状态.json", State);
            networkTerminalStateExported = true;
            var confirmation = networkLastConfirmation;
            bool identityMatches = confirmation?.ResolvedTerminal == true && NetworkSameBoundSession(confirmation.Session) &&
                confirmation.Revision == State.EffectRuntime.StateRevision && confirmation.Applied == confirmation.Validated &&
                confirmation.Boundary != null && networkInitialConfirmation != null &&
                confirmation.Validated >= networkInitialConfirmation.Validated;
            // 摘要只核验当前正式State是否仍对应本机缓存边界，绝不用来创建或替换确认边界。
            string actualDigest = identityMatches ? RuleStateDigestService.Compute(State) : null;
            bool confirmedStateMatches = identityMatches && actualDigest == confirmation.Boundary.RuleStateDigest;
            Write("终局确认事实核验.json", new { State.GameId, State.ContentPackHash, LocalPlayer, State.Round, State.Phase,
                Revision = State.EffectRuntime.StateRevision, LastObservedConfirmation = confirmation,
                CoordinatorReleased = networkTransport?.SimulationCoordinator == null, ActualRuleStateDigest = actualDigest,
                IdentityMatches = identityMatches, ConfirmedStateMatches = confirmedStateMatches,
                BoundaryRecreated = false, MissingBoundarySubstituted = false });
            Require(confirmedStateMatches, "真实终局已导出，但缺少匹配当前状态的本机同局已确认边界；不默认0、不借用其他实例");
            Write("联机终局确认边界.json", confirmation.Boundary);
            networkTerminalBoundaryExported = true;
            var receiptEvidence = NetworkReadOwnTerminalReceipts(confirmation);
            Write("联机终局原回执清单.json", receiptEvidence);
            yield return Capture("本实例八回合联机终局");
            networkExported = true; NetworkPublish();
            yield return NetworkUntil(() => networkRoles.All(role => {
                var peer = NetworkRead<NetworkPeerStatus>("状态-" + role + ".json");
                return peer?.GameId == networkBoundGameId && peer.ContentHash == networkBoundContentHash && peer.Terminal && peer.Exported;
            }), "四个实例同局正式终局证据落盘", 60f);
            var peers = networkRoles.ToDictionary(role => role,
                role => NetworkRead<NetworkTerminalReceipts>(Path.Combine(role, "联机终局原回执清单.json")));
            bool identitiesMatch = peers.Values.All(peer => peer != null && peer.GameId == networkBoundGameId &&
                peer.ContentHash == networkBoundContentHash && peer.InitialSequence == receiptEvidence.InitialSequence &&
                peer.FinalSequence == receiptEvidence.FinalSequence) &&
                peers.Values.Select(peer => peer.PlayerId).OrderBy(id => id)
                    .SequenceEqual(networkInitialConfirmation.Session.FrozenPlayerIds.OrderBy(id => id));
            bool boundariesEqual = identitiesMatch && peers.Values.All(peer => peer.Boundary != null && JToken.DeepEquals(
                JToken.FromObject(peer.Boundary), JToken.FromObject(receiptEvidence.Boundary)));
            bool receiptsEqual = identitiesMatch && peers.Values.All(peer => peer.Receipts != null && JToken.DeepEquals(
                JToken.FromObject(peer.Receipts), JToken.FromObject(receiptEvidence.Receipts)));
            bool receiptsComplete = identitiesMatch && peers.Values.All(peer => peer.Complete);
            bool strictPassed = boundariesEqual && receiptsEqual && receiptsComplete;
            Write("终局导出与严格联机核验.json", new { State.GameId, State.ContentPackHash, LocalPlayer,
                ResolvedTerminalObserved = true, StateExported = networkTerminalStateExported,
                ConfirmationBoundaryExported = networkTerminalBoundaryExported, AutomaticExportCompleted = networkExported,
                LastObservedConfirmation = confirmation, OwnOriginalReceipts = receiptEvidence,
                PeerEvidenceUsedOnlyForComparison = true, PeerOriginalEvidence = peers,
                FourSessionIdentitiesMatch = identitiesMatch, FourConfirmationBoundariesEqual = boundariesEqual,
                FourOriginalFullPayloadsEqualIncludingEventId = receiptsEqual, FourOriginalReceiptStreamsComplete = receiptsComplete,
                StrictNetworkPassed = strictPassed, MissingReceiptsSynthesized = false,
                Boundary = "真实终局、同局最后确认边界、原回执缺口及自动导出分别记录；恢复安装确认前缀不补齐原Applied回执。" });
            if (!strictPassed) networkTerminalStrictFailure = "真实终局已自动导出，但原回执缺失/待决、载荷或四端确认边界不一致；严格联机验收未通过";
            networkTerminalAuditCompleted = true; NetworkPublish();
            // 各端先独立写全核验，再允许首个失败报告阻止其他操作；不让缺口消失在收尾竞态中。
            yield return NetworkUntil(() => networkRoles.All(role => {
                var peer = NetworkRead<NetworkPeerStatus>("状态-" + role + ".json");
                return peer?.GameId == networkBoundGameId && peer.ContentHash == networkBoundContentHash && peer.TerminalAuditCompleted;
            }), "四个实例独立终局核验落盘", 60f);
        }
        private NetworkTerminalReceipts NetworkReadOwnTerminalReceipts(NetworkConfirmedObservation confirmation)
        {
            long initial = networkInitialConfirmation.Validated, final = confirmation.Validated;
            var receipts = networkReceivedApplied.OrderBy(value => value.GlobalCommandSequence).ToList();
            var observedSequences = new HashSet<long>(receipts.Select(value => value.GlobalCommandSequence));
            var missing = new List<long>();
            for (long sequence = initial; sequence < final;) if (!observedSequences.Contains(++sequence)) missing.Add(sequence);
            var duplicates = receipts.GroupBy(value => value.GlobalCommandSequence).Where(group => group.Count() != 1).Select(group => group.Key).ToArray();
            var pending = networkCommands.Values.Where(value => value.Result == null && !value.RetiredByRecovery).Select(value => value.Command.CommandId).ToArray();
            var rejected = networkCommands.Values.Where(value => value.Result != null &&
                (!value.Result.CommandResult.Succeeded || !value.Result.AppliedLocally)).Select(value => value.Command.CommandId).ToArray();
            bool payloadsValid = receipts.All(value => value.ProtocolVersion == OperationCommandProtocol.Version && value.GameId == networkBoundGameId &&
                value.SimulationEpoch >= networkInitialConfirmation.Session.SimulationEpoch && value.SimulationEpoch <= confirmation.Session.SimulationEpoch &&
                value.MembershipVersion == confirmation.Session.MembershipVersion && value.GlobalCommandSequence > initial && value.GlobalCommandSequence <= final &&
                value.Command != null && value.RuleResult?.Succeeded == true);
            bool recoveryPending = networkAwaitedConnectionFault != null || networkRecoveryNotice != null || networkInstalledRecovery != null;
            return new NetworkTerminalReceipts { GameId = networkBoundGameId, ContentHash = networkBoundContentHash, PlayerId = networkBoundPlayerId,
                InitialSequence = initial, FinalSequence = final, Boundary = confirmation.Boundary, Receipts = receipts,
                MissingSequences = missing.ToArray(), DuplicateSequences = duplicates, UnresolvedOriginalCommands = pending,
                RejectedOriginalCommands = rejected, OriginalPayloadsValid = payloadsValid, RecoveryObservationPending = recoveryPending,
                Complete = payloadsValid && missing.Count == 0 && duplicates.Length == 0 && pending.Length == 0 && rejected.Length == 0 && !recoveryPending };
        }
        private void NetworkAfterReport(Exception failure)
        {
            if (string.IsNullOrEmpty(networkRoot)) { UnityEngine.Application.Quit(2); return; }
            NetworkEndStep();
            if (networkSubmissionObserver != null) networkSubmissionObserver.SubmissionResolutionObserved -= NetworkOnResolutionObserved;
            if (failure != null) NetworkPreserveFirstFailure(failure);
            if (networkTransport != null)
            {
                networkTransport.SimulationCommandApplied -= NetworkOnApplied;
                networkTransport.SimulationProposalRejected -= NetworkOnRejected;
                networkTransport.SimulationProtocolFaulted -= NetworkOnFault;
                networkTransport.CommandValidationPrefixAdvanced -= NetworkOnPrefix;
                networkTransport.SimulationRecoveryStarted -= NetworkOnRecoveryStarted;
                networkTransport.SimulationRecoveryCompleted -= NetworkOnRecoveryCompleted;
            }
            File.WriteAllText(NetworkPath("报告完成-" + networkRole + ".json"), JsonConvert.SerializeObject(new { Passed = failure == null, Output = output }));
            StartCoroutine(NetworkQuitAfterPeers(failure == null ? 0 : 2));
        }
        private void NetworkPreserveFirstFailure(Exception failure)
        {
            if (!string.IsNullOrEmpty(networkRoot))
            {
                try
                {
                    using var stream = new FileStream(NetworkPath("首个实例失败.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream);
                    writer.Write(JsonConvert.SerializeObject(new { Role = networkRole, Stage = stage, Error = failure.ToString(), Output = output }));
                }
                catch (IOException) { }
            }
        }
        private IEnumerator NetworkQuitAfterPeers(int code)
        {
            float began = Time.realtimeSinceStartup;
            while (!networkRoles.All(role => File.Exists(NetworkPath("报告完成-" + role + ".json"))) && Time.realtimeSinceStartup - began < 25f)
                yield return new WaitForSecondsRealtime(.2f);
            // 退出受产品保存门禁约束；一次请求被拦截后保留现场，不循环触发保存失败弹窗。
            Write("正常退出请求.json", new { AtUtc = DateTime.UtcNow.ToString("o"), ExitCode = code, RequestCount = 1,
                ResolvedTerminalObserved = NetworkHasResolvedTerminal(), AutomaticRetry = false });
            UnityEngine.Application.Quit(code);
            yield return new WaitForSecondsRealtime(5f);
            Write("正常退出尚未完成.json", new { AtUtc = DateTime.UtcNow.ToString("o"), ExitCode = code,
                RequestCount = 1, AutomaticRetry = false, WindowPreserved = true, ForcedQuit = false });
        }
    }
}
#endif
