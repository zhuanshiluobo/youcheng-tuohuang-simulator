#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
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
        private MirrorCommandTransport networkTransport;
        private CommandSubmissionController networkSubmissionObserver;
        private DeterministicSimulationCoordinator networkAdmissionObserver;
        private string networkAdmissionObservationKey;
        private string networkActiveStepId, networkReentryFromStep;
        private int networkStepOrdinal;
        private NetworkCommandRecord networkCompetitionAwaitingRefresh, networkCompetitionPreviousDecision;
        private string networkCompetitionWaitReason;
        private string networkObservedSaveFailure;
        private bool networkFirstSaveFailureCaptured;
        private readonly HashSet<string> networkRetiredSteps = new HashSet<string>();
        private string networkFault;
        private bool networkTerminal, networkExported;
        private bool networkTerminalStateExported, networkTerminalBoundaryExported, networkTerminalAuditCompleted;
        private string networkBoundGameId, networkBoundContentHash, networkTerminalStrictFailure;
        private int networkBoundPlayerId;
        private NetworkConfirmedObservation networkLastConfirmation, networkInitialConfirmation;
        private SimulationPublishedSnapshot networkLastReadablePublication;
        private int networkShortRoundLimit;
        private int networkOriginalCommandObservationSeconds = 45;
        private string networkPaymentRegressionTarget;
        private int networkPaymentRegressionPlayer = 3, networkPaymentRegressionRound = 6;
        private string networkPaymentRegressionMode;
        private bool networkDeclineRegressionEnabled, networkDeclineRegressionAfterRecorded, networkDeclineRegressionEndRecorded;
        private string networkDeclineRegressionSpecial, networkDeclineRegressionRequest, networkDeclineRegressionCondition;
        private long networkDeclineRegressionAnswerAt = -1;
        private readonly HashSet<string> networkDeclineRegressionNodeIds = new HashSet<string>();
        private bool networkPaymentRegressionInitialRecorded, networkPaymentRegressionConfirmed;
        private long networkPaymentRegressionPlacementObservedAt = -1;
        private readonly HashSet<string> networkPaymentRegressionCaptured = new HashSet<string>();
        private bool networkShortFrozen, networkShortExported, networkShortCompleted;
        private sealed class NetworkShortStopRequest { public string GameId; public int CompletedThroughRound; }

        private void NetworkConfigureShortRun()
        {
            networkPaymentRegressionTarget = Argument("--yc-network-payment-regression-target=");
            string regressionPlayer = Argument("--yc-network-payment-regression-player=");
            string regressionRound = Argument("--yc-network-payment-regression-round=");
            if (!string.IsNullOrEmpty(regressionPlayer)) Require(int.TryParse(regressionPlayer, out networkPaymentRegressionPlayer), "回归玩家参数无效");
            if (!string.IsNullOrEmpty(regressionRound)) Require(int.TryParse(regressionRound, out networkPaymentRegressionRound), "回归回合参数无效");
            networkPaymentRegressionMode = Argument("--yc-network-payment-regression-mode=") ?? BuildFacilityService.PaymentModeGold;
            networkDeclineRegressionEnabled = Argument("--yc-network-condition-decline-regression=") == "true";
            networkDeclineRegressionSpecial = Argument("--yc-network-condition-decline-special=");
            string observation = Argument("--yc-network-original-command-observation-seconds=");
            if (!string.IsNullOrEmpty(observation))
                Require(int.TryParse(observation, out networkOriginalCommandObservationSeconds) &&
                    networkOriginalCommandObservationSeconds >= 45 && networkOriginalCommandObservationSeconds <= 300,
                    "原命令补充观察时长必须在45至300秒内；45秒停滞仍记失败");
            string value = Argument("--yc-network-stop-after-round=");
            if (string.IsNullOrEmpty(value)) return;
            Require(int.TryParse(value, out networkShortRoundLimit) && (networkShortRoundLimit == 2 || networkShortRoundLimit == 3),
                "正常联机短局只允许显式完成两或三回合后取证，不能改变八回合规则");
        }
        private bool NetworkShortShouldStop()
        {
            if (networkShortRoundLimit == 0 || string.IsNullOrEmpty(networkBoundGameId)) return false;
            if (NetworkTryReadPublication(out var publication) && publication.Round > networkShortRoundLimit)
            {
                try
                {
                    using var stream = new FileStream(NetworkPath("计划停止请求.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream);
                    writer.Write(JsonConvert.SerializeObject(new NetworkShortStopRequest { GameId = networkBoundGameId, CompletedThroughRound = networkShortRoundLimit }));
                }
                catch (IOException) when (File.Exists(NetworkPath("计划停止请求.json"))) { }
            }
            var stop = NetworkRead<NetworkShortStopRequest>("计划停止请求.json");
            if (stop == null) return false;
            Require(stop.GameId == networkBoundGameId && stop.CompletedThroughRound == networkShortRoundLimit, "计划停止请求不属于本人正式同局短局");
            networkShortFrozen = true;
            return true;
        }
        private bool NetworkStopPlannedShortStep(Stack<IEnumerator> stack)
        {
            if (!NetworkShortShouldStop() || networkActiveStepId == null ||
                !stack.Any(value => value is NetworkStepBoundary boundary && boundary.Id == networkActiveStepId)) return false;
            string step = networkActiveStepId;
            Append("计划停止原观察步骤.jsonl", new { StepId = step, Reason = "第三回合之后计划取证，原命令结果不补造、不退役", Pending = networkCommands.Values.Where(v => v.Result == null).Select(v => v.Command.CommandId).ToArray() });
            NetworkDiscardStep(stack, step); networkActiveStepId = null;
            return true;
        }
        private IEnumerator NetworkShortStopBarrier()
        {
            networkShortFrozen = true; stage = "正常三回合短局计划收尾"; NetworkPublish();
            SimulationPublishedSnapshot stopPublication = null;
            yield return NetworkUntil(() => NetworkTryReadPublication(out stopPublication) && stopPublication.Round > networkShortRoundLimit &&
                !stopPublication.IsStopped && !stopPublication.RequiresRecovery && !stopPublication.HasPendingLocalApplication &&
                stopPublication.LastAppliedSequence == stopPublication.ValidatedThroughSequence &&
                stopPublication.ReadBoundary(stopPublication.ValidatedThroughSequence) != null &&
                networkTransport.SimulationCoordinator.IsBoundaryPermitted(stopPublication.ValidatedThroughSequence) &&
                networkCommands.Values.All(v => v.Result != null || v.RetiredByRecovery), "短局停止点本人真实执行、确认及边界许可就绪", settleAfter: false);
            Require(State.MaxRounds == 8 && !NetworkHasResolvedTerminal() && State.EffectRuntime.Status != EffectRuntimeStatus.PausedFault,
                "计划短局必须保持正式八回合规则及健康非终局");
            NetworkRememberConfirmation("正常三回合停止点", stopPublication);
            var confirmation = networkLastConfirmation;
            Require(confirmation != null && confirmation.Validated == stopPublication.ValidatedThroughSequence &&
                confirmation.Revision == stopPublication.StateRevision && confirmation.Boundary.RuleStateDigest == RuleStateDigestService.Compute(State), "短局当前正式State不对应真实确认边界");
            Write("联机短局正式规则状态.json", State); Write("联机短局确认边界.json", confirmation.Boundary);
            Write("联机短局初始边界.json", networkInitialConfirmation);
            Write("联机短局原回执清单.json", NetworkReadOwnTerminalReceipts(confirmation));
            yield return Capture("第三回合完成后的正常稳定边界");
            networkShortExported = true; NetworkPublish();
            yield return NetworkUntil(() => networkRoles.All(role => {
                var peer = NetworkRead<NetworkPeerStatus>("状态-" + role + ".json");
                return peer?.GameId == networkBoundGameId && peer.ShortFrozen && peer.ShortExported;
            }), "四端计划停止证据落盘", 60f);
            var peers = networkRoles.ToDictionary(role => role, role => NetworkRead<JObject>(Path.Combine(role, "联机短局原回执清单.json")));
            var own = peers[networkRole];
            // 原回执是证据JSON；CommandResult不提供JSON构造器，不反序列化为领域运行对象。
            bool strict = own != null && own["InitialSequence"]?.Type == JTokenType.Integer &&
                own["FinalSequence"]?.Type == JTokenType.Integer && own["Boundary"] is JObject && own["Receipts"] is JArray &&
                peers.Values.All(peer => peer != null && peer["InitialSequence"]?.Type == JTokenType.Integer &&
                peer["FinalSequence"]?.Type == JTokenType.Integer && peer["Boundary"] is JObject && peer["Receipts"] is JArray &&
                (string)peer["GameId"] == networkBoundGameId &&
                (string)peer["ContentHash"] == networkBoundContentHash &&
                (long?)peer["InitialSequence"] == (long?)own["InitialSequence"] &&
                (long?)peer["FinalSequence"] == (long?)own["FinalSequence"] && (bool?)peer["Complete"] == true &&
                JToken.DeepEquals(peer["Boundary"], own["Boundary"]) && JToken.DeepEquals(peer["Receipts"], own["Receipts"]));
            Write("正常三回合短局.json", new { State.GameId, State.ContentPackHash, State.Round, State.MaxRounds, State.Phase,
                CompletedThroughRound = networkShortRoundLimit, PlannedStop = true, GameOverClaimed = false, StrictPassed = strict,
                Initial = networkInitialConfirmation, Stop = confirmation, OriginalReceipts = peers,
                RuntimeStatus = State.EffectRuntime.Status, State.EffectRuntime.LastFaultCode, State.EffectRuntime.LastFaultMessage });
            Require(strict, "短局停止点四端原回执或真实确认边界不一致；不能当计划退出通过");
            networkShortCompleted = true;
        }
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
            public string[] NormalCompetitionRejectedCommands, UnexpectedRejectedCommands;
            public bool CompetitionContinuationsComplete;
            public bool Complete, RecoveryObservationPending, OriginalPayloadsValid;
        }

        // TryRead不读取session.State；首帧未发表及恢复新代次空隙均不能落回活动/启动临时状态。
        private bool NetworkTryReadPublication(out SimulationPublishedSnapshot publication)
        {
            publication = null;
            if (session == null || mobile == null) return false;
            var coordinator = networkTransport?.SimulationCoordinator;
            if (coordinator != null)
            {
                if (!coordinator.TryReadPublishedSnapshot(out publication)) return false;
                var options = coordinator.SessionOptions;
                if (publication.GameId != options.GameId || publication.ContentHash != options.ContentPackHash ||
                    publication.LocalPlayerId != options.LocalPlayerId || publication.LocalPlayerId != LocalPlayer ||
                    publication.SimulationEpoch != options.SimulationEpoch || publication.MembershipVersion != options.MembershipVersion)
                { networkFault = "正式发表版本与本机协调器身份不匹配"; publication = null; return false; }
            }
            else
            {
                // 产品已脱离的终局仅可继续读此前本机真实发表且已确认的同一版本。
                var last = networkLastReadablePublication;
                if (last == null || !last.FinalScoringResolved || networkLastConfirmation?.ResolvedTerminal != true ||
                    last.StateRevision != networkLastConfirmation.Revision ||
                    last.LastAppliedSequence != networkLastConfirmation.Applied ||
                    last.ValidatedThroughSequence != networkLastConfirmation.Validated ||
                    last.SimulationEpoch != networkLastConfirmation.Session.SimulationEpoch ||
                    last.MembershipVersion != networkLastConfirmation.Session.MembershipVersion) return false;
                publication = last;
            }
            if (!string.IsNullOrEmpty(networkBoundGameId) && (publication.GameId != networkBoundGameId ||
                publication.ContentHash != networkBoundContentHash || publication.LocalPlayerId != networkBoundPlayerId))
            { networkFault = "正式发表版本改变了已绑定的同局本人身份"; publication = null; return false; }
            var view = session.View;
            if (view == null || view.GameId != publication.GameId || view.ContentPackHash != publication.ContentHash ||
                view.StateRevision != publication.StateRevision || view.ViewerPlayerId != publication.LocalPlayerId) return false;
            networkLastReadablePublication = publication;
            return true;
        }

        private bool NetworkHasResolvedTerminal() => session != null && (!NetworkMode || NetworkTryReadPublication(out _)) &&
            State.Round == State.MaxRounds && State.MaxRounds == 8 &&
            (State.Phase == GamePhase.GameOver || State.Phase == GamePhase.FinalScoring) && State.FinalScoring?.IsResolved == true &&
            State.Players.Count == 4 && State.FinalScoring.PlayerScores.Count == State.Players.Count;

        private bool NetworkSameBoundSession(DeterministicSimulationSessionOptions options) => options != null && NetworkTryReadPublication(out _) &&
            options.GameId == networkBoundGameId && options.ContentPackHash == networkBoundContentHash &&
            options.LocalPlayerId == networkBoundPlayerId && State.GameId == networkBoundGameId &&
            State.ContentPackHash == networkBoundContentHash && LocalPlayer == networkBoundPlayerId &&
            options.SimulationEpoch > 0 && options.MembershipVersion > 0 && options.FrozenPlayerIds != null &&
            options.FrozenPlayerIds.OrderBy(id => id).SequenceEqual(State.Players.Select(player => player.PlayerId).OrderBy(id => id));

        private void NetworkRememberConfirmation(string source, SimulationPublishedSnapshot publication = null)
        {
            var coordinator = networkTransport?.SimulationCoordinator;
            if (coordinator == null || string.IsNullOrEmpty(networkBoundGameId) ||
                publication == null && !NetworkTryReadPublication(out publication)) return;
            var options = coordinator.SessionOptions;
            if (!NetworkSameBoundSession(options)) { networkFault = "本机确认事实的对局、内容或玩家身份改变"; return; }
            long applied = publication.LastAppliedSequence, validated = publication.ValidatedThroughSequence;
            // 只保存本机真实读到且已确认的完整当前边界，不把尚未确认的状态配到旧前缀上。
            if (publication.IsStopped || publication.RequiresRecovery || applied != validated) return;
            var previous = networkLastConfirmation;
            bool terminal = NetworkHasResolvedTerminal();
            if (previous != null && previous.Validated == validated && previous.Session.SimulationEpoch == options.SimulationEpoch &&
                previous.Revision == publication.StateRevision && previous.ResolvedTerminal == terminal) return;
            var boundary = publication.ReadBoundary(validated);
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
                Frame = Time.frameCount, Revision = publication.StateRevision, Round = publication.Round, Phase = publication.Phase,
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
            public bool RejectedByCompetition, CompetitionResolutionObserved, CompetitionOtherPlayerProgressObserved;
            public long ObservedGlobalSequence, ObservedValidatedSequence;
            public string SubmittedWindowId, ReconsideredFromCommandId;
            public int SubmittedWindowGeneration;
            public int CompetitionRefreshObservedFrame = -1, CompetitionRefreshObservedRevision;
            public int[] ConcurrentPlayerIds;
            public SimulationProposalResultDto CompetitionRejection;
            public GameCommand Command;
            public string TargetProfile;
            public OperationCommandProposalDto ActualProposal;
            public WorkflowSubmissionResult Result;
        }
        private sealed class NetworkPeerStatus
        {
            public string Role, GameId, ContentHash, Phase, Stage;
            public int PlayerId, Round, Revision;
            public long Applied, Validated;
            public bool Terminal, Exported, TerminalAuditCompleted, ShortFrozen, ShortExported;
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
        private bool NetworkRetireCompetitionStep(Stack<IEnumerator> stack)
        {
            if (networkActiveStepId == null) return false;
            var rejected = networkCommands.Values.SingleOrDefault(value => value.StepId == networkActiveStepId && value.RejectedByCompetition);
            if (rejected == null) return false;
            Require(rejected.CompetitionResolutionObserved, "正式并发拒绝未收到生产UI的准确刷新拒绝回执，不能结束旧步骤");
            NetworkDiscardStep(stack, networkActiveStepId);
            Append("并发拒绝结束旧步骤.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                rejected.StepId, Original = rejected.Command, Rejection = rejected.CompetitionRejection,
                OldContinuationExecuted = false, AppliedLocally = false, AutomaticResubmit = false });
            preparedMainAction = null; pendingMoveCompletion = null; paymentSourceContinuation = null;
            explorationPlayPlan = null; lastStylePlacement = null; stylePlacementCache.Clear(); stylePlacementRevision = -1;
            paymentSourceAttempts.Clear(); attemptedBuildPreparations.Clear();
            networkCompetitionAwaitingRefresh = rejected; networkActiveStepId = null;
            return true;
        }
        private bool NetworkCompetitionUiReady(SimulationPublishedSnapshot publication)
        {
            var rejected = networkCompetitionAwaitingRefresh;
            if (rejected == null) return true;
            networkCompetitionWaitReason = "等待本人正式新前缀、同版请求与生产提交可用，且无待决命令";
            var submission = Read(mobile, "commandSubmission") as CommandSubmissionController;
            if (publication.SimulationEpoch != rejected.SimulationEpoch || publication.IsStopped || publication.RequiresRecovery ||
                publication.HasPendingLocalApplication || publication.LastAppliedSequence < rejected.CompetitionRejection.HostSequencedThrough ||
                publication.StateRevision <= rejected.BeforeRevision || session.View?.StateRevision != publication.StateRevision ||
                submission?.IsSimulationReady != true || submission.CanSubmitForLocalPlayer != true ||
                Request != null && Request.StateRevision != publication.StateRevision ||
                !string.IsNullOrEmpty((string)Read(mobile, "pendingEffectCommandId")) ||
                !string.IsNullOrEmpty((string)Read(mobile, "characterUseCommandId"))) return false;
            if (!rejected.CompetitionOtherPlayerProgressObserved)
            {
                var progress = networkReceivedApplied.Where(value => value.GameId == networkBoundGameId &&
                    value.SimulationEpoch == rejected.SimulationEpoch && value.MembershipVersion == publication.MembershipVersion &&
                    value.GlobalCommandSequence > rejected.ObservedGlobalSequence &&
                    value.GlobalCommandSequence <= rejected.CompetitionRejection.HostSequencedThrough &&
                    value.Command != null && value.Command.PlayerId != networkBoundPlayerId && value.RuleResult?.Succeeded == true).ToArray();
                if (progress.Length == 0)
                {
                    networkCompetitionWaitReason = "等待新前缀中的他人原Applied通知，不以已发表快照代替原回执";
                    var observed = new HashSet<long>(networkReceivedApplied.Where(value => value.GameId == networkBoundGameId &&
                        value.SimulationEpoch == rejected.SimulationEpoch).Select(value => value.GlobalCommandSequence));
                    bool complete = true;
                    for (long sequence = rejected.ObservedGlobalSequence; sequence < rejected.CompetitionRejection.HostSequencedThrough;)
                        if (!observed.Contains(++sequence)) { complete = false; break; }
                    Require(!complete, "严格拒绝的原推进序列已齐，却全非他人命令，不能按正常竞争继续");
                    return false;
                }
                rejected.CompetitionOtherPlayerProgressObserved = true;
                Append("正常竞争拒绝的他人前缀推进证据.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                    rejected.StepId, RejectedOriginalCommand = rejected.Command, rejected.CompetitionRejection,
                    OtherPlayerOriginalApplied = progress, OriginalWindowAvailablePlayers = rejected.ConcurrentPlayerIds,
                    AcrossDifferentWindows = progress.Any(value => value.OperationWindowId != rejected.SubmittedWindowId),
                    FourReplicaBarrierAdded = false });
            }
            foreach (string field in new[] { "enterpriseInteraction", "characterAbilityInteraction", "routeResourceSelection" })
            {
                var intent = Read(Read(mobile, field), "confirmedIntent");
                networkCompetitionWaitReason = "等待原生产流程解除busy与刷新等待：" + field;
                if (Read(intent, "IsBusy") is bool busy && busy || Read(intent, "WaitingForRefresh") is bool waiting && waiting) return false;
            }
            // 正式投影已更新不表示本帧布局和射线目标已完成；等Unity正常帧推进，不强刷Canvas。
            networkCompetitionWaitReason = "等待正式刷新之后的下一正常Unity帧";
            if (rejected.CompetitionRefreshObservedFrame < 0 || rejected.CompetitionRefreshObservedRevision != publication.StateRevision)
            {
                rejected.CompetitionRefreshObservedFrame = Time.frameCount;
                rejected.CompetitionRefreshObservedRevision = publication.StateRevision;
                return false;
            }
            if (Time.frameCount <= rejected.CompetitionRefreshObservedFrame) return false;
            networkCompetitionWaitReason = "等待当前同版候选在真实EventSystem射线中可点击";
            if (!NetworkCompetitionPageAcceptsPointer(publication)) return false;
            Append("并发拒绝后正式刷新就绪.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                rejected.StepId, RejectedCommandId = rejected.Command.CommandId, rejected.CompetitionRejection.HostSequencedThrough,
                PublishedSequence = publication.LastAppliedSequence, publication.StateRevision, CurrentRequest = Request,
                ForcedRefresh = false, AutomaticResubmit = false });
            networkCompetitionPreviousDecision = rejected; networkCompetitionAwaitingRefresh = null;
            networkCompetitionWaitReason = null;
            return true;
        }
        private bool NetworkCompetitionPageAcceptsPointer(SimulationPublishedSnapshot publication)
        {
            var request = Request;
            if (request != null && (request.InteractionTypeId.Contains("enterprise") || request.InteractionTypeId.Contains("department")))
            {
                var dialog = FindObjectsOfType<EnterpriseSelectionDialogView>().FirstOrDefault(value => value.Draft.Projection?.RequestId == request.InteractionId &&
                    value.Draft.Projection.Revision == publication.StateRevision && !value.Draft.Projection.ReadOnly);
                if (dialog == null) return false;
                var boards = Read(dialog, "items") as System.Collections.IDictionary;
                foreach (var target in dialog.Draft.Projection.Targets.Where(dialog.Draft.IsAvailable))
                {
                    var targets = Read(boards?[target.BoardId], "targets") as System.Collections.IDictionary;
                    if (Read(targets?[target.Id], "button") is Button button && button.IsInteractable() && IsUiTargetVisible(button.gameObject)) return true;
                }
                return false;
            }
            if (State.Phase == GamePhase.CharacterCover)
                return FindObjectsOfType<PlayerAutomationId>().Any(value => value.Id.StartsWith("selection.card.", StringComparison.Ordinal) &&
                    value.GetComponent<Button>()?.IsInteractable() == true && IsUiTargetVisible(value.gameObject));
            return true;
        }
        private object NetworkPointerTargetObservation(GameObject target)
        {
            var canvases = target.GetComponentsInParent<Canvas>(true);
            var button = target.GetComponent<Button>();
            var currentTargets = FindObjectsOfType<EnterpriseSelectionDialogView>().SelectMany(dialog => {
                var boards = Read(dialog, "items") as System.Collections.IDictionary;
                var rows = new List<object>();
                if (boards != null) foreach (System.Collections.DictionaryEntry board in boards)
                {
                    var targets = Read(board.Value, "targets") as System.Collections.IDictionary;
                    if (targets != null) foreach (System.Collections.DictionaryEntry entry in targets)
                        if (Read(entry.Value, "button") is Button current)
                            rows.Add(new { BoardId = board.Key, TargetId = entry.Key, InstanceId = current.gameObject.GetInstanceID(),
                                SameTargetInstance = current.gameObject == target, Rect = UiRectDetails(current.transform as RectTransform) });
                }
                return rows;
            }).ToArray();
            return new { InstanceId = target.GetInstanceID(), target.activeSelf, target.activeInHierarchy,
                ButtonEnabled = button?.enabled, ButtonInteractable = button?.IsInteractable(),
                FirstClickTarget = FirstUiClickTarget(UiPointerPoint(target)) is GameObject first ? ComponentPath(first.transform) : null,
                RaycastDetails = UiRaycastDetails(UiPointerPoint(target)),
                CurrentRequest = Request, Rect = UiRectDetails(target.transform as RectTransform),
                Groups = UiCanvasGroups(target.transform), CurrentEnterpriseTargets = currentTargets,
                Graphics = target.GetComponentsInChildren<Graphic>(true).Select(graphic => new {
                    graphic.name, graphic.depth, graphic.raycastTarget, Culled = graphic.canvasRenderer.cull,
                    Rect = UiRectDetails(graphic.rectTransform) }).ToArray(),
                Canvases = canvases.Select(canvas => new { canvas.name, canvas.enabled, canvas.isActiveAndEnabled,
                    Raycasters = canvas.GetComponents<UnityEngine.UI.GraphicRaycaster>().Select(raycaster => new {
                        raycaster.enabled, raycaster.isActiveAndEnabled }).ToArray() }).ToArray() };
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
            Require(!networkCommands.Values.Any(value => value.CompetitionResolutionObserved && !value.RejectedByCompetition),
                "收到版本刷新拒绝但缺少准确的正式并发拒绝载荷；不能按正常竞争继续");
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
            bool publicationGap = !string.IsNullOrEmpty(networkBoundGameId) && !NetworkTryReadPublication(out _);
            // 终局脱离是产品的正常生命周期；不把它当作恢复成功，也不清除尚未完成的恢复观察。
            if (NetworkHasResolvedTerminal() && networkTransport?.SimulationCoordinator == null)
            { NetworkPublish(); return false; }
            if (networkInstalledRecovery != null && !publicationGap)
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
                Require(options != null && networkRecoverySource != null && networkBoundGameId == networkRecoverySource.GameId &&
                    networkBoundContentHash == networkRecoverySource.ContentPackHash && options.GameId == networkRecoverySource.GameId &&
                    options.ContentPackHash == networkRecoverySource.ContentPackHash && options.LocalPlayerId == networkRecoverySource.LocalPlayerId &&
                    options.MembershipVersion == networkRecoverySource.MembershipVersion &&
                    (options.SimulationEpoch == networkRecoverySource.SimulationEpoch || options.SimulationEpoch == networkRecoverySource.SimulationEpoch + 1),
                    "正式恢复等待期间身份发生改变，不能继续原对局");
            }
            bool peer = NetworkPeerAwaitingRecovery();
            if (local || peer || publicationGap)
            {
                if (!networkRecoveryPaused)
                {
                    networkRecoveryPaused = true; networkRecoveryPauseBegan = Time.realtimeSinceStartup;
                    Append("联机恢复等待.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                        Role = networkRole, Stage = stage, Phase = "暂停原控件", Local = local, Peer = peer, PublicationGap = publicationGap,
                        StepTimeoutSeconds = NetworkRecoveryStepTimeout, NoGameDurationLimit = true });
                }
                if (Time.realtimeSinceStartup - networkRecoveryPauseBegan >= NetworkRecoveryStepTimeout)
                {
                    string phase = publicationGap ? "等待本代次正式发表版本及本人投影，不读取启动/恢复临时State" :
                        networkInstalledRecovery != null ? "已收到安装事件，等待确认边界和消费者就绪" :
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
        private IEnumerator NetworkUntil(Func<bool> condition, string description, float timeout = 120f, bool settleAfter = true,
            bool resetOnFormalProgress = false, float nonfatalStallThreshold = 0f)
        {
            float began = NetworkActiveTime;
            long applied = -1, validated = -1, sequenced = -1;
            bool stallReported = false;
            while (!condition())
            {
                NetworkCheck();
                if (resetOnFormalProgress && NetworkTryReadPublication(out var progress))
                {
                    long currentSequenced = networkTransport.SimulationCoordinator?.LastSequencedSequence ?? -1;
                    if (progress.LastAppliedSequence > applied || progress.ValidatedThroughSequence > validated || currentSequenced > sequenced)
                    {
                        began = NetworkActiveTime;
                        applied = progress.LastAppliedSequence; validated = progress.ValidatedThroughSequence; sequenced = currentSequenced;
                        Append("原命令等待期间正式进展.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                            Description = description, Applied = applied, Validated = validated, Sequenced = sequenced,
                            NoProgressTimeoutSeconds = timeout, OriginalCommandResubmitted = false,
                            Reason = "仅正式编序、执行或确认序号前进重置停滞计时；轮询、busy、帧和截图不延长等待" });
                    }
                }
                if (!stallReported && nonfatalStallThreshold > 0f && NetworkActiveTime - began >= nonfatalStallThreshold)
                {
                    stallReported = true;
                    var observation = new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                        Stage = stage, StepId = networkActiveStepId, Description = description,
                        NoFormalProgressSeconds = NetworkActiveTime - began, FailureThresholdSeconds = nonfatalStallThreshold,
                        MaximumObservationSeconds = timeout, Applied = applied, Validated = validated, Sequenced = sequenced,
                        OriginalPendingCommands = networkCommands.Values.Where(record => record.Result == null && !record.RetiredByRecovery)
                            .Select(record => record.Command).ToArray(),
                        Passed = false, Blocking = false, OriginalCommandResubmitted = false, FailureRetainedIfReceiptArrives = true };
                    Append("原命令45秒无正式进展失败.jsonl", observation);
                    string first = Path.Combine(output, "原命令首次45秒无正式进展失败.json");
                    if (!File.Exists(first)) Write("原命令首次45秒无正式进展失败.json", observation);
                    checks.Add(observation);
                    nonfatalUiFailures.Add(description + "连续45秒无正式序号推进；补充观察不撤销此失败");
                }
                Require(NetworkActiveTime - began < timeout, description + "超时，未调用服务API或注入状态绕过");
                if (session != null) NetworkPublish();
                yield return new WaitForSecondsRealtime(.2f);
            }
            if (settleAfter) yield return Settle();
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
            SimulationPublishedSnapshot initialPublication = null;
            yield return NetworkUntil(() => networkSubmissionObserver.IsSimulationReady &&
                NetworkTryReadPublication(out initialPublication) &&
                initialPublication.LastAppliedSequence == initialPublication.ValidatedThroughSequence &&
                initialPublication.ReadBoundary(initialPublication.ValidatedThroughSequence) != null,
                "确定性联机初始检查点及本人投影就绪", settleAfter: false);
            Require(initialPublication != null, "正常联机就绪时没有正式发表版本，不能绑定临时局号");
            networkBoundGameId = initialPublication.GameId; networkBoundContentHash = initialPublication.ContentHash;
            networkAdmissionObserver = networkTransport.SimulationCoordinator;
            networkAdmissionObserver.OrderedCommandProduced += NetworkObserveOrdered;
            networkAdmissionObserver.SimulationProposalResultProduced += NetworkObserveFinalAdmission;
            networkBoundPlayerId = initialPublication.LocalPlayerId;
            NetworkRememberConfirmation("本机正式初始检查点就绪", initialPublication);
            Require(networkLastConfirmation != null, "正式初始检查点缺少本机同局确认边界，不能默认序列或借用其他实例");
            if (networkInitialConfirmation == null) networkInitialConfirmation = networkLastConfirmation;
            NetworkPublish();
            yield return NetworkUntil(() => networkRoles.All(role => NetworkRead<NetworkPeerStatus>("状态-" + role + ".json")?.GameId == networkBoundGameId), "四个独立实例进入同一正式GameId");
            Write("联机运行身份.json", new { Role = networkRole, LocalPlayer, State.GameId, State.ContentPackHash,
                Mode = GameLaunchContext.Instance.Mode.ToString(), ExpectedPlayers = 4, NormalMenuFlow = true,
                FixtureState = false, DirectServiceCalls = false, DirectSubmission = false });
        }
        private void NetworkPublish()
        {
            NetworkObserveSaveFailure(networkShortFrozen ? "计划短局取证" : "正常游戏推进");
            if (string.IsNullOrEmpty(networkBoundGameId) || !NetworkTryReadPublication(out var publication)) return;
            var coordinator = networkTransport?.SimulationCoordinator;
            NetworkObserveAdmissionState();
            NetworkRememberConfirmation("本机状态发布时实际读取", publication);
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
            File.WriteAllText(NetworkPath("状态-" + networkRole + ".json"), JsonConvert.SerializeObject(new NetworkPeerStatus {
                Role = networkRole, GameId = publication.GameId, ContentHash = publication.ContentHash, PlayerId = publication.LocalPlayerId,
                Round = publication.Round, Phase = publication.Phase.ToString(), Stage = stage, Revision = publication.StateRevision,
                Applied = publication.LastAppliedSequence, Validated = publication.ValidatedThroughSequence,
                Terminal = networkTerminal, Exported = networkExported, TerminalAuditCompleted = networkTerminalAuditCompleted,
                ShortFrozen = networkShortFrozen, ShortExported = networkShortExported,
                CanSubmit = (Read(mobile, "commandSubmission") as CommandSubmissionController)?.CanSubmitForLocalPlayer == true,
                InteractiveCards = NetworkInteractiveCardCount(),
                RecoveryPending = networkAwaitedConnectionFault != null || networkRecoveryNotice != null,
                ConnectionInterruption = networkAwaitedConnectionFault, RecoveryNotice = networkRecoveryNotice,
                SimulationEpoch = publication.SimulationEpoch, MembershipVersion = publication.MembershipVersion }));
            NetworkObservePaymentRegression(publication);
            NetworkObserveDeclineRegression(publication);
        }
        private bool NetworkPaymentRegressionTurn => NetworkMode && !string.IsNullOrEmpty(networkPaymentRegressionTarget) &&
            State.Round == networkPaymentRegressionRound && State.ActionRound == 2 && LocalPlayer == networkPaymentRegressionPlayer;
        private int NetworkPaymentRegressionPriority(string itemId) => NetworkPaymentRegressionTurn &&
            itemId == networkPaymentRegressionTarget ? -1 : 0;
        private void NetworkObservePaymentRegression(SimulationPublishedSnapshot publication)
        {
            if (string.IsNullOrEmpty(networkPaymentRegressionTarget) || networkPaymentRegressionConfirmed) return;
            var state = State;
            var item = state.ItemZones.Items.FirstOrDefault(value => value.ItemId == networkPaymentRegressionTarget);
            var placed = state.Map.Facilities.FirstOrDefault(value => value.ContentInstanceId == networkPaymentRegressionTarget);
            object Snapshot(string reason, long relatedSequence) => new {
                AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, Role = networkRole,
                publication.GameId, publication.ContentHash, publication.SimulationEpoch, publication.StateRevision,
                publication.LastAppliedSequence, publication.ValidatedThroughSequence,
                state.Round, state.ActionRound, Phase = state.Phase.ToString(), Reason = reason, RelatedSequence = relatedSequence,
                TargetItem = item == null ? null : new { item.ItemId, item.DefinitionId, item.ZoneId, item.SlotId },
                ActualPlacement = placed,
                PublicPlayerResources = state.Players.Select(player => new { player.PlayerId, Resources = player.Resources.Clone() }).ToArray(),
                ReadOnlyObservation = true, ExtraCommandSubmitted = false, ForcedRefresh = false };
            if (!networkPaymentRegressionInitialRecorded)
            {
                networkPaymentRegressionInitialRecorded = true;
                Write("建设付款回归初始正式状态.json", Snapshot("正常菜单原档恢复后首次完整发表状态", publication.LastAppliedSequence));
            }
            foreach (var applied in networkReceivedApplied.Where(value => value.Command?.PlayerId == networkPaymentRegressionPlayer &&
                (value.Command.TargetId == networkPaymentRegressionTarget ||
                    value.Command.Kind == GameCommandKind.PreparePaymentDraft && state.Round == networkPaymentRegressionRound && state.ActionRound == 2)))
            {
                string key = applied.SimulationEpoch + "-" + applied.GlobalCommandSequence;
                if (publication.LastAppliedSequence < applied.GlobalCommandSequence || !networkPaymentRegressionCaptured.Add(key)) continue;
                Write("建设付款回归原命令后正式状态-E" + applied.SimulationEpoch + "-G" + applied.GlobalCommandSequence + ".json",
                    new { Applied = applied, Observation = Snapshot("实际原命令后的本机发表状态；RelatedSequence与实际观察序号分列", applied.GlobalCommandSequence) });
            }
            if (placed?.PlayerId != networkPaymentRegressionPlayer) return;
            if (networkPaymentRegressionPlacementObservedAt < 0)
            {
                networkPaymentRegressionPlacementObservedAt = publication.LastAppliedSequence;
                Write("建设付款回归实际放置原观察.json", Snapshot("本机首次观察原实例已实际放置于目标玩家城市", networkPaymentRegressionPlacementObservedAt));
            }
            if (publication.ValidatedThroughSequence < networkPaymentRegressionPlacementObservedAt) return;
            networkPaymentRegressionConfirmed = true;
            Write("建设付款回归放置后确认事实.json", Snapshot("本机已观察到覆盖实际放置序号的正式确认前缀；最终仍须核四份原件", networkPaymentRegressionPlacementObservedAt));
        }
        private void NetworkObserveDeclineRegression(SimulationPublishedSnapshot publication)
        {
            if (!networkDeclineRegressionEnabled || networkDeclineRegressionEndRecorded) return;
            var state = State;
            if (string.IsNullOrEmpty(networkDeclineRegressionRequest))
            {
                if (state.Round != networkPaymentRegressionRound || state.ActionRound != 2) return;
                var request = state.EffectRuntime.InteractionRequests.FirstOrDefault(value => value.Status == "open" &&
                    value.AnsweringPlayerId == networkPaymentRegressionPlayer && value.InteractionTypeId == "action.decline_effect");
                if (request == null) return;
                networkDeclineRegressionRequest = request.InteractionId;
                var payment = state.EffectRuntime.EffectNodes.FirstOrDefault(value => value.EffectId == request.OwnerEffectId);
                networkDeclineRegressionCondition = payment?.Payment?.RuleConditionOwnerEffectId ?? "";
                var node = payment;
                while (node != null && networkDeclineRegressionNodeIds.Add(node.EffectId))
                    node = state.EffectRuntime.EffectNodes.FirstOrDefault(value => value.EffectId == node.ParentEffectId);
                Write("条件放弃回归前正式状态.json", new { OriginalRequest = request,
                    Observation = NetworkDeclineRegressionSnapshot(publication, "原条件请求开放，原按钮输入前", publication.LastAppliedSequence) });
            }
            var answer = networkReceivedApplied.FirstOrDefault(value => value.Command?.PlayerId == networkPaymentRegressionPlayer &&
                value.Command.Kind == GameCommandKind.AnswerInteraction && value.Command.OptionIds.Contains("decline") &&
                value.Command.Parameters.Any(entry => entry.Key == "interactionId" && entry.Value == networkDeclineRegressionRequest));
            if (answer == null || publication.LastAppliedSequence < answer.GlobalCommandSequence) return;
            networkDeclineRegressionAnswerAt = answer.GlobalCommandSequence;
            if (!networkDeclineRegressionAfterRecorded)
            {
                networkDeclineRegressionAfterRecorded = true;
                Write("条件放弃回归后正式状态.json", new { Applied = answer,
                    Observation = NetworkDeclineRegressionSnapshot(publication, "原decline准确执行后的实际状态", answer.GlobalCommandSequence) });
            }
            var end = networkReceivedApplied.FirstOrDefault(value => value.Command?.PlayerId == networkPaymentRegressionPlayer &&
                value.Command.Kind == GameCommandKind.EndAction && value.GlobalCommandSequence > networkDeclineRegressionAnswerAt);
            if (end == null || publication.LastAppliedSequence < end.GlobalCommandSequence || publication.ValidatedThroughSequence < end.GlobalCommandSequence) return;
            networkDeclineRegressionEndRecorded = true;
            Write("条件放弃回归后继确认事实.json", new { Applied = end,
                Observation = NetworkDeclineRegressionSnapshot(publication, "同一原操作窗口后继EndAction已执行并被正式确认", end.GlobalCommandSequence) });
        }
        private object NetworkDeclineRegressionSnapshot(SimulationPublishedSnapshot publication, string reason, long related)
        {
            var state = State; var actor = state.FindPlayer(networkPaymentRegressionPlayer);
            var condition = state.EffectRuntime.EffectNodes.FirstOrDefault(value => value.EffectId == networkDeclineRegressionCondition);
            return new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, Role = networkRole,
                publication.GameId, publication.ContentHash, publication.SimulationEpoch, publication.StateRevision,
                publication.LastAppliedSequence, publication.ValidatedThroughSequence, state.Round, state.ActionRound,
                Phase = state.Phase.ToString(), Reason = reason, RelatedSequence = related, ConditionEffectId = networkDeclineRegressionCondition,
                RuntimeStatus = state.EffectRuntime.Status.ToString(), state.EffectRuntime.LastFaultCode, state.EffectRuntime.LastFaultMessage,
                Actor = new { actor.PlayerId, Resources = actor.Resources.Clone(), actor.Score },
                ActorFacilities = state.Map.Facilities.Where(value => value.PlayerId == networkPaymentRegressionPlayer).Select(value =>
                    new { value.PlayerId, value.FacilityCardId, value.ContentInstanceId, value.CityBoardSlotIndex }).OrderBy(value => value.ContentInstanceId).ToArray(),
                InfluenceIds = state.Map.Influences.Select(value => value.InfluenceId).OrderBy(value => value).ToArray(),
                Nodes = state.EffectRuntime.EffectNodes.Where(value => networkDeclineRegressionNodeIds.Contains(value.EffectId) ||
                    value.ParentEffectId == networkDeclineRegressionCondition || condition?.ChildEffectIds.Contains(value.EffectId) == true).Select(value =>
                    new { value.EffectId, value.ParentEffectId, value.SourceId, value.EffectTypeId, Status = value.Status.ToString(),
                        PendingOutcome = value.PendingOutcome.ToString(), value.FlowStage, value.FailureReason, value.NormalizedResult,
                        value.ChildEffectIds, value.ContinuationAbilityId, value.AllowDecline }).ToArray(),
                ReadOnlyObservation = true, ExtraCommandSubmitted = false, ForcedRefresh = false };
        }
        private bool NetworkTryBeginStep()
        {
            if (NetworkShortShouldStop()) return false;
            if (NetworkHasResolvedTerminal()) return false;
            NetworkCheck(); NetworkPublish();
            var coordinator = networkTransport?.SimulationCoordinator;
            Require(coordinator != null, "非终局缺少正式联机协调器；不能使用缓存边界申请行动许可");
            if (!NetworkTryReadPublication(out var publication)) return false;
            long sequence = coordinator.LastAppliedSequence;
            if (sequence != networkProgressSequence)
            { networkProgressSequence = sequence; networkLastProgressAt = NetworkActiveTime; }
            if (NetworkActiveTime - networkLastProgressAt > 90f)
            {
                Write("联机停滞原件.json", new { State, LocalPlayer, Sequence = sequence,
                    Boundary = coordinator.ReadBoundary(coordinator.ValidatedThroughSequence),
                    Peers = networkRoles.Select(role => NetworkRead<NetworkPeerStatus>("状态-" + role + ".json")).ToArray(),
                    CompetitionWaitReason = networkCompetitionWaitReason, CompetitionOriginal = networkCompetitionAwaitingRefresh,
                    CompetitionUiTargets = FindObjectsOfType<EnterpriseSelectionDialogView>().SelectMany(dialog =>
                        dialog.GetComponentsInChildren<Button>().Where(button => button.name.StartsWith("EnterprisePickerTarget", StringComparison.Ordinal))
                            .Select(button => NetworkPointerTargetObservation(button.gameObject))).ToArray(),
                    CurrentCardTargets = FindObjectsOfType<PlayerAutomationId>().Where(value =>
                        value.Id.StartsWith("selection.card.", StringComparison.Ordinal)).Take(16)
                        .Select(value => NetworkPointerTargetObservation(value.gameObject)).ToArray(),
                    CurrentUi = ObserveSafely(), Reason = "90秒无任何正式Applied命令；不绕过原控件，不强制刷新" });
                throw new InvalidOperationException("联机90秒没有任何正式命令推进；已保存四端许可、本人实际控件及完整规则状态");
            }
            if (!NetworkCompetitionUiReady(publication)) return false;
            if ((Read(mobile, "commandSubmission") as CommandSubmissionController)?.CanSubmitForLocalPlayer != true) return false;
            var options = coordinator.SessionOptions;
            var permissions = coordinator.ReadWindowPermissions().Where(value => value?.Window != null &&
                value.Window.GameId == networkBoundGameId && value.Window.SimulationEpoch == options.SimulationEpoch &&
                value.Window.MembershipVersion == options.MembershipVersion && value.IsOpen && value.IsPermitted && value.CanSubmitForLocalPlayer).ToArray();
            // 同窗延续和真正换窗均由产品真实许可判定，不从页面/回合推断，也不逐命令要求Host核验追平。
            if (permissions.Length == 0) return false;
            // 盖牌由专门角色页管理，不经过通用路由；只交给实际可点的本人原牌面控件。
            if (State.Phase == GamePhase.CharacterCover && NetworkInteractiveCardCount() == 0) return false;
            NetworkRequireNoPendingOriginalControl("下一步原控件操作");
            Append("原步骤正式窗口许可.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                Role = networkRole, Stage = stage, Session = options, Permissions = permissions,
                LocalAppliedSequence = coordinator.LastAppliedSequence, HostValidatedThrough = coordinator.ValidatedThroughSequence,
                PerControlFourReplicaBarrier = false, CrossProcessActionLease = false });
            return true;
        }
        private void NetworkEndStep() { networkActiveStepId = null; NetworkPublish(); }

        private void NetworkRequireNoPendingOriginalControl(string description)
        {
            if (!NetworkMode || networkTransport == null) return;
            NetworkCheck();
            var pending = networkCommands.Values.FirstOrDefault(value => value.Result == null && !value.RetiredByRecovery);
            Require(pending == null, description + "：上一条原命令仍未取得本人准确结果，不能再次点击或重签同一未完成请求：" + pending?.Command.CommandId);
        }

        private bool NetworkLocalSuccessorReadable(NetworkCommandRecord record)
        {
            if (!NetworkTryReadPublication(out var publication)) return false;
            var coordinator = networkTransport?.SimulationCoordinator;
            var view = session?.View;
            if (coordinator == null || !NetworkSameBoundSession(coordinator.SessionOptions) ||
                record.Result?.AppliedLocally != true || record.Result.CommandResult.Succeeded != true ||
                (Read(mobile, "commandSubmission") as CommandSubmissionController)?.IsSimulationReady != true ||
                view == null || view.GameId != networkBoundGameId || view.ContentPackHash != networkBoundContentHash) return false;
            int revision = publication.StateRevision;
            var request = Request;
            // 此处只核本机已交付的准确结果与当前完整投影/请求。跨窗口的下一次提交仍走正式许可。
            return coordinator.LastAppliedSequence >= record.GlobalCommandSequence && revision >= record.AfterRevision &&
                view.StateRevision == revision && (request == null || request.StateRevision == revision);
        }
        private int NetworkInteractiveCardCount() => FindObjectsOfType<PlayerAutomationId>().Count(value =>
            value.Id.StartsWith("selection.card.", StringComparison.Ordinal) && value.GetComponent<Button>()?.IsInteractable() == true &&
            IsUiTargetVisible(value.gameObject));
        private void NetworkOnApplied(SimulationCommandAppliedDto applied)
        {
            if (applied?.Command == null) return;
            Append("四端全局应用回执.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, Role = networkRole, Applied = applied });
            networkReceivedApplied.Add(applied);
            networkApplied[applied.Command.CommandId] = applied;
            NetworkResolve(applied.Command.CommandId);
            if (applied.Command.PlayerId != networkBoundPlayerId) NetworkObserveEnterpriseDraft("他人原Applied后", applied);
            NetworkPublish();
        }
        private void NetworkObserveEnterpriseDraft(string trigger, SimulationCommandAppliedDto incoming)
        {
            if (string.IsNullOrEmpty(networkBoundGameId) || !NetworkTryReadPublication(out var publication)) return;
            NetworkObserveInitialCharacterDraft(trigger, incoming, publication);
            foreach (var dialog in FindObjectsOfType<EnterpriseSelectionDialogView>())
            {
                var draft = dialog.Draft; var projection = draft.Projection;
                if (projection == null || projection.ReadOnly) continue;
                Append("企业草稿刷新原观察.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                    Trigger = trigger, publication.GameId, publication.LocalPlayerId, publication.LastAppliedSequence,
                    publication.ValidatedThroughSequence, publication.StateRevision,
                    IncomingCommand = incoming?.Command, IncomingSequence = incoming?.GlobalCommandSequence,
                    LocalOriginalCommandCount = networkCommands.Count,
                    Projection = new { projection.GameId, projection.ViewerPlayerId, projection.RequestId, projection.OwnerEffectId,
                        projection.Revision, projection.CandidateSetId, projection.CandidateSetVersion, projection.SubmissionAvailable },
                    SelectedIds = draft.SelectedIds.ToArray(), draft.IsPending, draft.CanConfirm,
                    SelectedStillLegal = draft.SelectedIds.All(id => projection.Targets.Any(target => target.Id == id && draft.IsAvailable(target))),
                    Confirm = ButtonInfo(Read(dialog, "confirm") as Button), CurrentRequest = Request,
                    ReadOnlyObservation = true, ExtraControlOrCommand = false });
            }
        }
        private void NetworkObserveInitialCharacterDraft(string trigger, SimulationCommandAppliedDto incoming, SimulationPublishedSnapshot publication)
        {
            var request = Request;
            if (request?.InteractionTypeId != "setup.basic_character.discard") return;
            var coordinator = Read(mobile, "characterAbilityInteraction");
            var selection = Read(coordinator, "selectedCandidates") as IEnumerable<string>;
            var selected = selection?.OrderBy(id => id, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
            var rendered = Read(coordinator, "renderedRequest") as InteractionRequestProjection;
            var confirm = FindObjectsOfType<Button>().FirstOrDefault(button => button.name == "Confirm Selection" && button.IsActive());
            Append("初始角色多选草稿原观察.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                Trigger = trigger, publication.GameId, publication.LocalPlayerId, publication.LastAppliedSequence,
                publication.ValidatedThroughSequence, publication.StateRevision,
                IncomingCommand = incoming?.Command, IncomingSequence = incoming?.GlobalCommandSequence,
                LocalOriginalCommandCount = networkCommands.Count, SelectionSourcePresent = selection != null,
                SelectedIds = selected, SelectedCount = selected.Length, SelectedStillLegal = selected.All(request.CandidateIds.Contains),
                request.MinSelections, request.MaxSelections, InFlightCommandId = Read(coordinator, "inFlightCommandId"),
                ConfirmedIntentBusy = Read(Read(coordinator, "confirmedIntent"), "IsBusy"),
                Confirm = ButtonInfo(confirm), CurrentRequest = request, RenderedRequest = rendered,
                ReadOnlyObservation = true, ExtraControlOrCommand = false });
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
            if (!NetworkRecoveryNoticeMatches(notice, source) || source.GameId != networkBoundGameId || source.ContentPackHash != networkBoundContentHash)
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
            if (networkCommands.TryGetValue(id, out var concurrent) && NetworkIsNormalCompetitionRejection(concurrent, rejected))
            {
                concurrent.CompetitionRejection = rejected; concurrent.RejectedByCompetition = true;
                concurrent.Result = new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(rejected.ErrorCode, rejected.Reason)), false);
                if (NetworkTryReadPublication(out var visible)) concurrent.AfterRevision = visible.StateRevision;
                Append("正常并发旧请求拒绝.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                    concurrent.StepId, Original = concurrent.Command, concurrent.ObservedGlobalSequence, concurrent.BeforeRevision,
                    concurrent.ConcurrentPlayerIds, Rejection = rejected, OtherPlayerProgressProofPending = true,
                    CountedAsApplied = false, CountedAsSuccess = false });
                Append("命令与准确回执.jsonl", concurrent);
                return;
            }
            if (networkCommands.TryGetValue(id, out var record) && record.Result == null)
            {
                record.Result = new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(rejected.ErrorCode, rejected.Reason)), false);
                if (NetworkTryReadPublication(out var publication)) record.AfterRevision = publication.StateRevision;
                Append("命令与准确回执.jsonl", record);
            }
            networkFault = "正式提案被拒绝：" + rejected.Reason;
        }
        private bool NetworkIsNormalCompetitionRejection(NetworkCommandRecord record, SimulationProposalResultDto rejected)
        {
            // 本批三类请求必须验证原确认；不能用旧驱动的重新选牌/再次确认掩盖失败。
            if (!string.IsNullOrEmpty(record?.TargetProfile)) return false;
            var proposal = rejected?.Proposal;
            var coordinator = networkTransport?.SimulationCoordinator;
            var installed = coordinator?.SessionOptions;
            return record != null && record.StepId == networkActiveStepId && !string.IsNullOrEmpty(record.StepId) &&
                !record.RetiredByRecovery && !record.RejectedByCompetition && !networkApplied.ContainsKey(record.Command.CommandId) &&
                (record.Result == null || record.Result.AppliedLocally == false && record.Result.CommandResult.Succeeded == false) &&
                coordinator != null && !coordinator.IsStopped && !coordinator.RequiresRecovery && installed != null &&
                NetworkSameBoundSession(installed) && installed.SimulationEpoch == record.SimulationEpoch &&
                rejected.ProtocolVersion == OperationCommandProtocol.Version && rejected.Status == OperationAdmissionStatus.Rejected &&
                rejected.ErrorCode == CommandErrorCode.StateRevisionMismatch && rejected.RequiresRefresh && rejected.OrderedCommand == null &&
                rejected.GameId == networkBoundGameId && rejected.SimulationEpoch == record.SimulationEpoch &&
                rejected.MembershipVersion == installed.MembershipVersion && proposal?.Command != null &&
                proposal.ProtocolVersion == OperationCommandProtocol.Version &&
                proposal.GameId == rejected.GameId && proposal.SimulationEpoch == rejected.SimulationEpoch &&
                proposal.MembershipVersion == rejected.MembershipVersion && proposal.Command.PlayerId == networkBoundPlayerId &&
                proposal.OperationWindowId == record.SubmittedWindowId && proposal.WindowGeneration == record.SubmittedWindowGeneration &&
                proposal.ObservedGlobalSequence == record.ObservedGlobalSequence && proposal.ObservedStateRevision == record.BeforeRevision &&
                rejected.HostSequencedThrough > proposal.ObservedGlobalSequence && record.ConcurrentPlayerIds?.Contains(networkBoundPlayerId) == true &&
                JToken.DeepEquals(JToken.FromObject(proposal.Command), JToken.FromObject(GameCommandDto.FromCommand(record.Command)));
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
                // 本机房主可在Submit返回前收到拒绝；另一种订阅顺序会先到生产UI回执。
                // 这里只保留准确刷新回执，Guarded恢复执行前必须再有完整正式并发拒绝载荷。
                if (gameId == networkBoundGameId && commandId == record.Command.CommandId && record.Command.PlayerId == networkBoundPlayerId &&
                    record.StepId == networkActiveStepId && installed?.SimulationEpoch == record.SimulationEpoch &&
                    errorCode == CommandErrorCode.StateRevisionMismatch && requiresRefresh && record.ConcurrentPlayerIds?.Contains(networkBoundPlayerId) == true)
                {
                    record.CompetitionResolutionObserved = true;
                    return;
                }
                networkFault = "准确回执真实拒绝，非同次正式恢复退役：" + JsonConvert.SerializeObject(callback);
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            { networkFault = "验收准确回执观察异常：" + exception; }
        }
        private void NetworkBeginObserveCommand(GameCommand command, int before)
        {
            Require(NetworkTryReadPublication(out var publication), "原UI提交前缺少正式发表版本，不能记录临时提案");
            var record = new NetworkCommandRecord { Stage = stage, Frame = Time.frameCount, Command = command,
                BeforeRevision = before, LocalPlayerAfter = LocalPlayer, StepId = networkActiveStepId,
                SimulationEpoch = publication.SimulationEpoch, ObservedGlobalSequence = publication.LastAppliedSequence,
                ObservedValidatedSequence = publication.ValidatedThroughSequence,
                ReconsideredFromCommandId = networkCompetitionPreviousDecision?.Command.CommandId };
            record.TargetProfile = SubmissionTargetProfile(command);
            Append("原UI命令提交前.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                record.StepId, record.TargetProfile, OriginalUiCommand = JObject.FromObject(command), Ui = CaptureOriginalUiSubmission(command),
                publication.LastAppliedSequence, publication.ValidatedThroughSequence, publication.StateRevision,
                AuthorityFieldsAddedByDriver = false });
            // 只读已安装的独立展示State解析原命令窗口；不访问后台快照的内部活动接口。
            if (OperationWindowService.TryResolveCommandContext(State, command, out var context, out _))
            {
                var permission = publication.ReadWindowPermissions().SingleOrDefault(value => value.Window.OperationWindowId == context.OperationWindowId &&
                    value.Window.WindowGeneration == context.WindowGeneration && value.IsOpen && value.IsPermitted && value.CanSubmitForLocalPlayer);
                if (permission != null)
                {
                    record.SubmittedWindowId = context.OperationWindowId; record.SubmittedWindowGeneration = context.WindowGeneration;
                    record.ConcurrentPlayerIds = permission.AvailablePlayerIds.ToArray();
                }
            }
            commands.Add(record);
            Require(!networkCommands.ContainsKey(command.CommandId), "原UI重复提交相同CommandId，保留原件");
            networkCommands.Add(command.CommandId, record);
            if (networkCompetitionPreviousDecision != null)
            {
                Append("并发拒绝后独立新决策.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                    RejectedStepId = networkCompetitionPreviousDecision.StepId, RejectedOriginal = networkCompetitionPreviousDecision.Command,
                    NewStepId = record.StepId, NewOriginalCommand = command, CurrentRequest = Request,
                    publication.LastAppliedSequence, publication.StateRevision, DriverChoiceCacheReused = false });
                networkCompetitionPreviousDecision = null;
            }
        }
        private void NetworkObserveCommand(GameCommand command, WorkflowSubmissionResult immediate, int before)
        {
            Require(networkCommands.TryGetValue(command.CommandId, out var record), "原控件返回前没有记录同一条提交身份");
            var actual = networkTransport?.SimulationCoordinator?.ReadPendingLocalProposal();
            if (actual?.Command?.CommandId == command.CommandId)
            {
                record.ActualProposal = actual;
                Append("本机实际首提案.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                    record.StepId, record.TargetProfile, OriginalUiCommand = JObject.FromObject(command), ActualOwnedProposal = actual,
                    NegotiatedRequestContractVersion = networkTransport.SimulationCoordinator.RequestContractVersion,
                    AuthorityFieldsAddedByDriver = false });
            }
            NetworkObserveAdmissionState();
            Append("本机原控件提案.jsonl", new { record.Stage, record.Frame, record.StepId, record.SimulationEpoch,
                Command = command, Immediate = immediate, BeforeRevision = before,
                KnownLocalAppliedAtSubmit = record.ObservedGlobalSequence,
                KnownHostValidatedAtSubmit = record.ObservedValidatedSequence });
            if (!immediate.CommandResult.Succeeded && record.Result == null)
            {
                record.Result = immediate; record.AfterRevision = State.EffectRuntime.StateRevision;
                Append("命令与准确回执.jsonl", record);
            }
            else NetworkResolve(command.CommandId);
        }
        private string SubmissionTargetProfile(GameCommand command)
        {
            if (InteractionRequestValidity.TryReadRequestAuthority(State, command, out var authority, out _)) return authority.Profile;
            if (command?.Parameters == null || !command.Parameters.TryGetValue("interactionId", out string id)) return null;
            var request = State.EffectRuntime.InteractionRequests.FirstOrDefault(value => value.GetStableInteractionId() == id);
            if (request?.InteractionTypeId == "setup.enterprise.choose") return InteractionRequestValidity.InitialEnterprise;
            if (request?.InteractionTypeId == "character.cover.card") return InteractionRequestValidity.NormalCover;
            var projection = request == null ? null : InteractionRequestProjector.ProjectForPlayer(request, command.PlayerId);
            return request?.InteractionTypeId == "route_resource.selection" &&
                PaymentField(projection, "purpose")?.IntegerValue == (int)RouteSelectionPurpose.NormalHarvest ? InteractionRequestValidity.NormalHarvest : null;
        }
        private object CaptureOriginalUiSubmission(GameCommand command)
        {
            command.Parameters.TryGetValue("interactionId", out string id);
            var request = State.EffectRuntime.InteractionRequests.FirstOrDefault(value => value.GetStableInteractionId() == id);
            InteractionRequestValidity.TryReadRequestAuthority(State, command, out var authority, out var validation);
            return new { State.GameId, State.Round, State.Phase, State.CurrentPlayerId, LocalPlayer,
                Revision = State.EffectRuntime.StateRevision, Request = request,
                TargetProfile = SubmissionTargetProfile(command), Authority = authority, AuthorityReadValidation = validation,
                Actor = State.FindPlayer(command.PlayerId),
                CoverBusy = Read(Read(mobile, "coverIntent"), "IsBusy"),
                EnterpriseBusy = Read(Read(Read(mobile, "enterpriseInteraction"), "confirmedIntent"), "IsBusy"),
                RouteBusy = Read(Read(Read(mobile, "routeResourceSelection"), "confirmedIntent"), "IsBusy"),
                ReadOnly = true };
        }
        private void NetworkObserveAdmissionState()
        {
            var coordinator = networkTransport?.SimulationCoordinator;
            if (coordinator == null) return;
            var proposal = coordinator.ReadPendingLocalProposal();
            bool pending = Read(coordinator, "backgroundPendingValidationLocal") is bool value && value;
            string key = (proposal?.Command?.CommandId ?? "") + ":" + pending + ":" + coordinator.LastAppliedSequence;
            if (networkAdmissionObservationKey == key) return;
            networkAdmissionObservationKey = key;
            Append("原提案等待状态.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                ActualOwnedProposal = proposal, PendingValidation = pending, coordinator.LastAppliedSequence,
                coordinator.LastSequencedSequence, coordinator.ValidatedThroughSequence,
                Ui = proposal?.Command != null && networkCommands.TryGetValue(proposal.Command.CommandId, out var originalRecord)
                    ? CaptureOriginalUiSubmission(originalRecord.Command) : null,
                ReadOnly = true, Resubmitted = false });
        }
        private void NetworkObserveOrdered(OrderedOperationCommandDto ordered) => Append("Host真实编序原件.jsonl", new {
            AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, Ordered = ordered, ReadOnly = true });
        private void NetworkObserveFinalAdmission(SimulationProposalResultDto result) => Append("Host原Pending最终准入.jsonl", new {
            AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount, Result = result, ReadOnly = true });
        private void NetworkResolve(string commandId)
        {
            if (!networkCommands.TryGetValue(commandId, out var record) || !networkApplied.TryGetValue(commandId, out var applied)) return;
            if (record.RejectedByCompetition) { networkFault = "未获全局号的正式竞争拒绝原命令又收到Applied，不能改写原件"; return; }
            if (record.Result != null) return;
            if (record.RetiredByRecovery) { networkFault = "已准确退役的原命令又收到Applied，需保留原件核对，不能改写旧拒绝"; return; }
            if (!NetworkTryReadPublication(out var publication)) { networkFault = "原Applied在正式同版State/View可读前到达，保留原件，不补准确结果"; return; }
            record.Result = NetworkExactAppliedResult(record.Command, applied, publication.GameId);
            Require(record.Result != null, "准确回执没有匹配原命令身份，不能解除待决或重发原答案");
            record.AfterRevision = publication.StateRevision; record.GlobalCommandSequence = applied.GlobalCommandSequence;
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
        private IEnumerator NetworkAwaitLocalCommands()
        {
            if (!NetworkMode || networkTransport == null) yield break;
            if (networkCommands.Values.Any(record => record.Result == null && !record.RetiredByRecovery))
                yield return NetworkUntil(() => networkCommands.Values.All(record => record.Result != null || record.RetiredByRecovery), "原命令真实联机Applied或准确恢复退役回执", 45f);
            // Guarded在恢复就绪后会先弹出整个旧步骤，不能执行此处之后的旧选择断言。
            if (networkActiveStepId != null && networkRetiredSteps.Contains(networkActiveStepId)) yield break;
            var rejected = networkCommands.Values.FirstOrDefault(record => !record.RejectedByCompetition && record.Result != null &&
                (!record.Result.CommandResult.Succeeded || !record.Result.AppliedLocally));
            Require(rejected == null, "原控件命令真实拒绝：" + rejected?.Result.CommandResult.Validation?.Reason);
            var last = commands.OfType<NetworkCommandRecord>().LastOrDefault(value => value.StepId == networkActiveStepId &&
                !value.RetiredByRecovery && !value.RejectedByCompetition && value.Command.PlayerId == networkBoundPlayerId);
            if (last == null) yield break; // 原本地草稿/浏览控件未提交命令，不虚构Applied。
            yield return NetworkUntil(() => NetworkLocalSuccessorReadable(last), "本人原命令Applied后的完整投影与后继请求可读", 45f);
            Append("本人原控件后继就绪.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                Role = networkRole, last.StepId, OriginalCommandId = last.Command.CommandId, last.GlobalCommandSequence,
                ExactAfterRevision = last.AfterRevision, PublishedRevision = session.View.StateRevision,
                CurrentRequest = Request, PerControlFourReplicaBarrier = false, HostValidationRequiredForSameWindow = false });
        }
        private IEnumerator NetworkTerminalBarrier()
        {
            Require(NetworkHasResolvedTerminal(), "没有真实八回合四人计分，不得导出为终局");
            networkTerminal = true; NetworkPublish();
            Write("联机正式终局规则状态.json", State);
            networkTerminalStateExported = true;
            string observedTerminalWait = null;
            bool OwnTerminalConfirmationReady()
            {
                if (!NetworkTryReadPublication(out var publication)) return false;
                NetworkRememberConfirmation("终局只读等待本机原确认事实", publication);
                var observed = networkLastConfirmation;
                var pending = networkCommands.Values.Where(value => value.Result == null && !value.RetiredByRecovery)
                    .Select(value => value.Command.CommandId).OrderBy(value => value).ToArray();
                string key = publication.LastAppliedSequence + ":" + publication.ValidatedThroughSequence + ":" +
                    publication.StateRevision + ":" + publication.IsStopped + ":" + pending.Length;
                if (key != observedTerminalWait)
                {
                    observedTerminalWait = key;
                    Append("终局原确认等待.jsonl", new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                        publication.GameId, publication.SimulationEpoch, publication.StateRevision, publication.LastAppliedSequence,
                        publication.ValidatedThroughSequence, publication.IsStopped, publication.RequiresRecovery,
                        publication.FinalScoringResolved, PendingOriginalCommands = pending, LastObservedConfirmation = observed,
                        CoordinatorReleased = networkTransport?.SimulationCoordinator == null,
                        NativeCurrentBoundaryAvailable = publication.LastAppliedSequence == publication.ValidatedThroughSequence &&
                            publication.ReadBoundary(publication.ValidatedThroughSequence) != null,
                        BoundaryRecreated = false, OriginalCommandResubmitted = false });
                }
                return observed?.ResolvedTerminal == true && NetworkSameBoundSession(observed.Session) &&
                    observed.Revision == State.EffectRuntime.StateRevision && observed.Applied == observed.Validated &&
                    observed.Boundary != null && networkInitialConfirmation != null &&
                    observed.Validated >= networkInitialConfirmation.Validated && pending.Length == 0;
            }
            yield return NetworkUntil(OwnTerminalConfirmationReady, "真实终局等待本机同局已确认边界及原准确回执",
                networkOriginalCommandObservationSeconds, settleAfter: false, resetOnFormalProgress: true,
                nonfatalStallThreshold: networkOriginalCommandObservationSeconds > 45 ? 45f : 0f);
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
            // 与短局一致，原回执只按JSON比对；CommandResult没有JSON构造器，不还原成运行时领域对象。
            var peers = networkRoles.ToDictionary(role => role,
                role => NetworkRead<JObject>(Path.Combine(role, "联机终局原回执清单.json")));
            bool identitiesMatch = peers.Values.All(peer => peer != null && (string)peer["GameId"] == networkBoundGameId &&
                (string)peer["ContentHash"] == networkBoundContentHash && peer["PlayerId"]?.Type == JTokenType.Integer &&
                (long?)peer["InitialSequence"] == receiptEvidence.InitialSequence &&
                (long?)peer["FinalSequence"] == receiptEvidence.FinalSequence) &&
                peers.Values.Select(peer => (int)peer["PlayerId"]).OrderBy(id => id)
                    .SequenceEqual(networkInitialConfirmation.Session.FrozenPlayerIds.OrderBy(id => id));
            bool boundariesEqual = identitiesMatch && peers.Values.All(peer => peer["Boundary"] is JObject && JToken.DeepEquals(
                peer["Boundary"], JToken.FromObject(receiptEvidence.Boundary)));
            bool receiptsEqual = identitiesMatch && peers.Values.All(peer => peer["Receipts"] is JArray && JToken.DeepEquals(
                peer["Receipts"], JToken.FromObject(receiptEvidence.Receipts)));
            bool receiptsComplete = identitiesMatch && peers.Values.All(peer => (bool?)peer["Complete"] == true);
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
            var normalCompetition = networkCommands.Values.Where(value => value.RejectedByCompetition).ToArray();
            var unexpectedRejected = rejected.Where(id => !networkCommands[id].RejectedByCompetition).ToArray();
            bool continuationsComplete = normalCompetition.All(NetworkCompetitionHasAcceptedSuccessor);
            bool payloadsValid = receipts.All(value => value.ProtocolVersion == OperationCommandProtocol.Version && value.GameId == networkBoundGameId &&
                value.SimulationEpoch >= networkInitialConfirmation.Session.SimulationEpoch && value.SimulationEpoch <= confirmation.Session.SimulationEpoch &&
                value.MembershipVersion == confirmation.Session.MembershipVersion && value.GlobalCommandSequence > initial && value.GlobalCommandSequence <= final &&
                value.Command != null && value.RuleResult?.Succeeded == true);
            bool recoveryPending = networkAwaitedConnectionFault != null || networkRecoveryNotice != null || networkInstalledRecovery != null;
            return new NetworkTerminalReceipts { GameId = networkBoundGameId, ContentHash = networkBoundContentHash, PlayerId = networkBoundPlayerId,
                InitialSequence = initial, FinalSequence = final, Boundary = confirmation.Boundary, Receipts = receipts,
                MissingSequences = missing.ToArray(), DuplicateSequences = duplicates, UnresolvedOriginalCommands = pending,
                RejectedOriginalCommands = rejected, NormalCompetitionRejectedCommands = normalCompetition.Select(value => value.Command.CommandId).ToArray(),
                UnexpectedRejectedCommands = unexpectedRejected, CompetitionContinuationsComplete = continuationsComplete,
                OriginalPayloadsValid = payloadsValid, RecoveryObservationPending = recoveryPending,
                Complete = payloadsValid && missing.Count == 0 && duplicates.Length == 0 && pending.Length == 0 &&
                    unexpectedRejected.Length == 0 && continuationsComplete && !recoveryPending };
        }
        private bool NetworkCompetitionHasAcceptedSuccessor(NetworkCommandRecord rejected)
        {
            var visited = new HashSet<string>();
            while (rejected.RejectedByCompetition)
            {
                if (!rejected.CompetitionResolutionObserved || !rejected.CompetitionOtherPlayerProgressObserved || !visited.Add(rejected.Command.CommandId)) return false;
                var next = networkCommands.Values.Where(value => value.ReconsideredFromCommandId == rejected.Command.CommandId).ToArray();
                if (next.Length != 1 || next[0].Command.CommandId == rejected.Command.CommandId || next[0].StepId == rejected.StepId) return false;
                rejected = next[0];
            }
            return rejected.Result?.AppliedLocally == true && rejected.Result.CommandResult.Succeeded;
        }
        private void NetworkObserveSaveFailure(string phase)
        {
            string failure = MatchSaveController.Instance?.LastFailure;
            if (failure == networkObservedSaveFailure) return;
            networkObservedSaveFailure = failure;
            var coordinator = networkTransport?.SimulationCoordinator;
            var observation = new { AtUtc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                Phase = phase, Stage = stage, GameId = networkBoundGameId ?? GameLaunchContext.Instance?.GameId,
                PlayerId = networkBoundPlayerId, LastFailure = failure,
                AppliedSequence = coordinator?.LastAppliedSequence, ValidatedThroughSequence = coordinator?.ValidatedThroughSequence,
                FirstNonEmpty = !networkFirstSaveFailureCaptured && !string.IsNullOrEmpty(failure),
                ReadOnlyGetter = true, ExtraSaveRequested = false };
            Append("保存错误变化.jsonl", observation);
            if (!networkFirstSaveFailureCaptured && !string.IsNullOrEmpty(failure))
            {
                Write("首次保存错误.json", observation);
                networkFirstSaveFailureCaptured = true;
            }
        }
        private void NetworkAfterReport(Exception failure)
        {
            if (string.IsNullOrEmpty(networkRoot)) { UnityEngine.Application.Quit(2); return; }
            NetworkEndStep();
            if (networkSubmissionObserver != null) networkSubmissionObserver.SubmissionResolutionObserved -= NetworkOnResolutionObserved;
            if (networkAdmissionObserver != null)
            {
                networkAdmissionObserver.OrderedCommandProduced -= NetworkObserveOrdered;
                networkAdmissionObserver.SimulationProposalResultProduced -= NetworkObserveFinalAdmission;
            }
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
            {
                NetworkObserveSaveFailure("报告完成等待退出");
                yield return new WaitForSecondsRealtime(.2f);
            }
            if (code == 0 && networkRole != "host")
            {
                // 成功短局由房主先走正式保存退出；客户端保持连接，收到断开并回到主页后再退出。
                // 同时关闭会把“主动保存退出”变成中途断线，属于另外的生命周期验收。
                began = Time.realtimeSinceStartup;
                while (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "StartScene" &&
                    Time.realtimeSinceStartup - began < 120f)
                    yield return new WaitForSecondsRealtime(.2f);
                if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "StartScene")
                {
                    Write("正常退出等待失败.json", new { AtUtc = DateTime.UtcNow.ToString("o"),
                        Reason = "等待房主保存退出后返回主页超时", WindowPreserved = true, ForcedQuit = false });
                    yield break;
                }
                Write("房主退出后正常返回主页.json", new { AtUtc = DateTime.UtcNow.ToString("o"),
                    Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, ForcedQuit = false });
            }
            // 退出受产品保存门禁约束；一次请求被拦截后保留现场，不循环触发保存失败弹窗。
            Write("正常退出请求.json", new { AtUtc = DateTime.UtcNow.ToString("o"), ExitCode = code, RequestCount = 1,
                ResolvedTerminalObserved = NetworkHasResolvedTerminal(), AutomaticRetry = false });
            UnityEngine.Application.Quit(code);
            float requested = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - requested < 5f)
            {
                NetworkObserveSaveFailure("正常退出请求之后");
                yield return new WaitForSecondsRealtime(.1f);
            }
            Write("正常退出尚未完成.json", new { AtUtc = DateTime.UtcNow.ToString("o"), ExitCode = code,
                RequestCount = 1, AutomaticRetry = false, WindowPreserved = true, ForcedQuit = false });
        }
    }
}
#endif
