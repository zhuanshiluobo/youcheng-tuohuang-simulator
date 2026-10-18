using System;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.Effects;
using YC.Domain.State;
using System.Collections.Generic;
using System.Linq;
using YC.Infrastructure.Multiplayer;
using UnityEngine;
using YC.Presentation.Workflows;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace YC.Presentation
{
    internal sealed class CommandSubmissionController : IDisposable
    {
        private static readonly Unity.Profiling.ProfilerMarker RuleSubmitMarker = new Unity.Profiling.ProfilerMarker("YC.RuleSubmit");
        private static readonly Dictionary<MirrorCommandTransport, CommandSubmissionController> SimulationTransportOwners =
            new Dictionary<MirrorCommandTransport, CommandSubmissionController>();
        private readonly GameSession session;
        private readonly GameLaunchContext launchContext;
        private readonly int localPlayerId;
        private readonly UnityEngine.Object logContext;
        private readonly Action refreshFromState;
        private readonly Action<string> setPrompt;
        private readonly Action<string> commandSettled;
        private readonly Action<string, bool> cityStyleCommandResolved;
        private readonly Action<string, string, bool, CommandErrorCode> interactionCommandResolved;
        // 只在原准确回调成功交付后观察；不参与受理、规则执行、恢复或UI重试。
        internal event Action<string, string, bool, CommandErrorCode, bool> SubmissionResolutionObserved;
        private readonly SubmittedInteractionCommands submittedCommands;
        private INetworkCommandTransport commandTransport;
        private bool sessionNoticeSubscribed;
        private MirrorCommandTransport simulationTransport;
        private DeterministicSimulationCoordinator simulationCoordinator;
        private readonly Dictionary<string, bool> simulationPermissions = new Dictionary<string, bool>(StringComparer.Ordinal);
        private bool usesDeterministicSimulation, simulationStopped, disposed;
        private LaunchMode simulationLaunchMode;
        private string simulationGameId;
        private long simulationEpoch, lastAppliedResultSequence, lastValidatedSequence;
        private int simulationMembershipVersion;
        private EffectRegistry simulationRegistry;
        private GameState simulationProjectionState;
        private GameStateView simulationProjectionView;
        private long simulationSubscriptionGeneration, recoveryTargetEpoch;
        private Action<DeterministicSimulationCoordinator> simulationReadySubscription, recoveryCompletedSubscription;
        private Action<SimulationCommandAppliedDto> simulationAppliedSubscription;
        private Action<CommandValidationPrefixDto> simulationPrefixSubscription;
        private Action<SimulationWindowPermissionDto> simulationPermissionSubscription;
        private Action<SimulationProtocolFaultDto> simulationFaultSubscription;
        private Action<SimulationProposalResultDto> simulationRejectedSubscription;
        private Action<SimulationRecoveryNoticeDto> recoveryStartedSubscription;
        private readonly HashSet<string> deliveredSimulationCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyValuePair<string, int>> unresolvedSimulationSubmissions =
            new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal);

        private readonly struct ExecutionSample : IDisposable
        {
            private readonly ExecutionTimingCounters counters;
            private readonly string phase;
            private readonly long started;

            public ExecutionSample(ExecutionTimingCounters counters, string phase)
            {
                this.counters = counters.Enabled ? counters : null;
                this.phase = this.counters == null ? null : phase;
                started = this.counters == null ? 0 : Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (counters != null && counters.Enabled)
                    counters.AddSample(phase, Stopwatch.GetTimestamp() - started);
            }
        }

        public bool UsesDeterministicSimulation => usesDeterministicSimulation;
        public bool IsSimulationReady => !disposed && (!UsesDeterministicSimulation ||
            SimulationBindingIsCurrent && simulationCoordinator != null && !simulationCoordinator.IsStopped && !simulationCoordinator.RequiresRecovery);
        public bool CanSubmitForLocalPlayer => !disposed && (launchContext == null || !launchContext.IsLeavingGameplay) &&
            (UsesDeterministicSimulation
                ? IsSimulationReady && !simulationCoordinator.HasPendingLocalApplication && simulationPermissions.Values.Any(value => value)
                : !GameSessionBootstrapper.IsNetworkLaunch(launchContext) || commandTransport != null);
        public bool RequiresExplicitRefreshForCurrentResult { get; private set; }
        private bool OwnsSimulationTransport => !ReferenceEquals(simulationTransport, null) &&
            SimulationTransportOwners.TryGetValue(simulationTransport, out var owner) && ReferenceEquals(owner, this);
        private bool SimulationBindingIsCurrent => !disposed && !simulationStopped && UsesDeterministicSimulation &&
            OwnsSimulationTransport && ReferenceEquals(simulationTransport.SimulationCoordinator, simulationCoordinator) &&
            session.State.GameId == simulationGameId && launchContext != null && !launchContext.IsLeavingGameplay &&
            launchContext.GameId == simulationGameId && launchContext.LocalPlayerId == localPlayerId &&
            launchContext.Mode == simulationLaunchMode && GameSessionBootstrapper.IsNetworkLaunch(launchContext);

        public CommandSubmissionController(
            GameSession session,
            GameLaunchContext launchContext,
            int localPlayerId,
            UnityEngine.Object logContext,
            Action refreshFromState,
            Action<string> setPrompt,
            Action<string> commandSettled, Action<string, bool> cityStyleCommandResolved = null,
            Action<string, string, bool, CommandErrorCode> interactionCommandResolved = null)
        {
            this.session = session;
            this.launchContext = launchContext;
            this.localPlayerId = localPlayerId;
            this.logContext = logContext;
            this.refreshFromState = refreshFromState;
            this.setPrompt = setPrompt;
            this.commandSettled = commandSettled;
            this.cityStyleCommandResolved = cityStyleCommandResolved;
            this.interactionCommandResolved = interactionCommandResolved;
            submittedCommands = new SubmittedInteractionCommands(localPlayerId,
                launchContext == null || launchContext.Mode == LaunchMode.Local);
        }

        public void Initialize()
        {
            if (disposed || UsesDeterministicSimulation) throw new InvalidOperationException("submission_transport_mode_already_selected");
            if (GameSessionBootstrapper.IsNetworkLaunch(launchContext))
                throw new InvalidOperationException("联网必须提供效果注册表并初始化确定性模拟。");
        }

        public void Initialize(EffectRegistry effectRegistry)
        {
            if (effectRegistry == null) throw new ArgumentNullException(nameof(effectRegistry));
            if (!GameSessionBootstrapper.IsNetworkLaunch(launchContext)) { Initialize(); return; }
            if (disposed || commandTransport != null || UsesDeterministicSimulation)
                throw new InvalidOperationException("submission_transport_mode_already_selected");
            usesDeterministicSimulation = true;
            try
            {
                var options = DeterministicSimulationSessionOptions.CreateInitial(launchContext.Mode == LaunchMode.Host,
                    localPlayerId, session, launchContext.Players.Select(seat => seat.PlayerId));
                if (launchContext.ResumeInfo != null) options = launchContext.ResumeInfo.Options(launchContext.Mode == LaunchMode.Host, localPlayerId);
                simulationGameId = options.GameId; simulationEpoch = options.SimulationEpoch;
                simulationMembershipVersion = options.MembershipVersion;
                simulationLaunchMode = launchContext.Mode;
                launchContext.OnlineSessionNotice += OnOnlineSessionNotice; sessionNoticeSubscribed = true;
                if (launchContext.TryConsumeOnlineSessionNotice(out var notice)) setPrompt(notice);
                var transport = MirrorCommandTransport.Ensure();
                simulationTransport = transport; commandTransport = transport;
                SimulationTransportOwners[transport] = this;
                simulationRegistry = effectRegistry;
                BindSimulationSubscriptions();
                ISimulationCheckpointInstaller installer = launchContext.Mode == LaunchMode.Client
                    ? new InitialSimulationCheckpointInstaller(session, effectRegistry, GameStateViewer.Player(localPlayerId)) : null;
                Func<string> archive = launchContext.Mode == LaunchMode.Host
                    ? (Func<string>)(() => HostRecoveryService.SerializeArchive(session.CreateHostArchive(session.State.ContentPackHash))) : null;
                SimulationCheckpointEnvelope savedIdentity = null;
                Func<DeterministicSimulationCoordinator> savedCoordinator = null;
                if (launchContext.ResumeInfo != null)
                {
                    var resume = launchContext.ResumeInfo;
                    savedIdentity = new SimulationCheckpointEnvelope { ProtocolVersion = SimulationCheckpointEnvelope.SavedProtocolVersion,
                        Purpose = SimulationCheckpointEnvelope.SavedSimulation, GameId = resume.GameId, ContentPackHash = resume.ContentHash,
                        SimulationEpoch = resume.SimulationEpoch, SourceSimulationEpoch = resume.SimulationEpoch - 1,
                        MembershipVersion = resume.MembershipVersion, GlobalCommandSequence = resume.ConfirmedGlobalSequence,
                        CheckpointId = resume.CheckpointId };
                    if (launchContext.Mode == LaunchMode.Host)
                    {
                        var data = launchContext.RestoreData ?? throw new InvalidOperationException("续局规则存档已丢失。");
                        var baseline = SimulationConfirmedRecoveryInstaller.ReadBaseline(data.SimulationCheckpointJson);
                        archive = () => data.SimulationCheckpointJson;
                        savedCoordinator = () => DeterministicSimulationCoordinator.FromConfirmedRecovery(session, options, baseline, session.State);
                    }
                    else
                    {
                        var savedInstaller = new SavedSimulationCheckpointInstaller(session, effectRegistry, GameStateViewer.Player(localPlayerId),
                            options, resume.CheckpointId, resume.MetadataDigest);
                        installer = savedInstaller; savedCoordinator = () => savedInstaller.Coordinator;
                    }
                }
                transport.InitializeDeterministicSimulation(session, launchContext.Mode, localPlayerId, launchContext.Players,
                    options, installer, archive, savedIdentity, savedCoordinator);
            }
            catch (Exception ex)
            {
                Dispose(); Debug.LogException(ex, logContext);
                setPrompt("联网模拟初始化失败，请返回房间重试。");
            }
        }

        private void BindSimulationSubscriptions()
        {
            UnbindSimulationSubscriptions();
            long generation = ++simulationSubscriptionGeneration;
            var transport = simulationTransport;
            bool Current() => !disposed && generation == simulationSubscriptionGeneration && OwnsSimulationTransport && ReferenceEquals(transport, simulationTransport);
            simulationReadySubscription = value => { if (Current()) OnDeterministicSimulationReady(value); };
            simulationAppliedSubscription = value => { if (Current()) OnSimulationCommandApplied(value); };
            simulationPrefixSubscription = value => { if (Current()) OnSimulationValidationAdvanced(value); };
            simulationPermissionSubscription = value => { if (Current()) OnSimulationPermissionChanged(value); };
            simulationFaultSubscription = value => { if (Current()) OnSimulationFaulted(value); };
            simulationRejectedSubscription = value => { if (Current()) OnSimulationProposalRejected(value); };
            recoveryStartedSubscription = value => { if (Current()) OnSimulationRecoveryStarted(value); };
            recoveryCompletedSubscription = value => { if (Current()) OnSimulationRecoveryCompleted(value); };
            transport.DeterministicSimulationReady += simulationReadySubscription;
            transport.SimulationCommandApplied += simulationAppliedSubscription;
            transport.CommandValidationPrefixAdvanced += simulationPrefixSubscription;
            transport.OperationWindowPermissionChanged += simulationPermissionSubscription;
            transport.SimulationProtocolFaulted += simulationFaultSubscription;
            transport.SimulationProposalRejected += simulationRejectedSubscription;
            transport.SimulationRecoveryStarted += recoveryStartedSubscription;
            transport.SimulationRecoveryCompleted += recoveryCompletedSubscription;
        }

        private void UnbindSimulationSubscriptions()
        {
            if (ReferenceEquals(simulationTransport, null)) return;
            simulationTransport.DeterministicSimulationReady -= simulationReadySubscription;
            simulationTransport.SimulationCommandApplied -= simulationAppliedSubscription;
            simulationTransport.CommandValidationPrefixAdvanced -= simulationPrefixSubscription;
            simulationTransport.OperationWindowPermissionChanged -= simulationPermissionSubscription;
            simulationTransport.SimulationProtocolFaulted -= simulationFaultSubscription;
            simulationTransport.SimulationProposalRejected -= simulationRejectedSubscription;
            simulationTransport.SimulationRecoveryStarted -= recoveryStartedSubscription;
            simulationTransport.SimulationRecoveryCompleted -= recoveryCompletedSubscription;
        }

        private void OnSimulationRecoveryStarted(SimulationRecoveryNoticeDto notice)
        {
            if (notice == null || notice.ProtocolVersion != OperationCommandProtocol.Version || notice.GameId != simulationGameId ||
                notice.SourceSimulationEpoch != simulationEpoch || notice.SimulationEpoch != simulationEpoch + 1 ||
                notice.MembershipVersion != simulationMembershipVersion || recoveryTargetEpoch != 0) return;
            recoveryTargetEpoch = notice.SimulationEpoch;
            simulationStopped = true; simulationPermissions.Clear();
            BindSimulationSubscriptions();
            refreshFromState();
        }

        private void OnSimulationRecoveryCompleted(DeterministicSimulationCoordinator coordinator)
        {
            if (coordinator == null || recoveryTargetEpoch == 0 || !ReferenceEquals(simulationTransport.SimulationCoordinator, coordinator) ||
                coordinator.SessionOptions.GameId != simulationGameId || coordinator.SessionOptions.SimulationEpoch != recoveryTargetEpoch ||
                coordinator.SessionOptions.MembershipVersion != simulationMembershipVersion || coordinator.SessionOptions.LocalPlayerId != localPlayerId ||
                launchContext == null || launchContext.IsLeavingGameplay || launchContext.GameId != simulationGameId ||
                launchContext.LocalPlayerId != localPlayerId || launchContext.Mode != simulationLaunchMode || session.State.GameId != simulationGameId) return;
            simulationCoordinator = coordinator; simulationEpoch = recoveryTargetEpoch; recoveryTargetEpoch = 0;
            lastAppliedResultSequence = coordinator.LastAppliedSequence; lastValidatedSequence = coordinator.ValidatedThroughSequence;
            simulationStopped = coordinator.IsStopped || coordinator.RequiresRecovery;
            simulationPermissions.Clear();
            foreach (var permission in coordinator.ReadWindowPermissions()) StoreSimulationPermission(permission);
            BindSimulationSubscriptions();
            EnsureSimulationLocalProjection();
            // 恢复退役的是尚未交付的原提交等待；UI草稿由原工作流继续持有。
            // 只发准确刷新拒绝，不能冒充成功、结算通知或自动重发玩家答案。
            foreach (var submission in unresolvedSimulationSubmissions.ToArray())
                ResolveSubmitted(submission.Key, submission.Value.Value, submission.Value.Key,
                    false, CommandErrorCode.StateRevisionMismatch, true);
            refreshFromState();
        }

        private bool MatchesSimulation(string gameId, long epoch, int membership) =>
            SimulationBindingIsCurrent && gameId == simulationGameId && epoch == simulationEpoch && membership == simulationMembershipVersion;

        private void OnDeterministicSimulationReady(DeterministicSimulationCoordinator coordinator)
        {
            if (disposed || simulationStopped || !UsesDeterministicSimulation || coordinator == null || !OwnsSimulationTransport ||
                !ReferenceEquals(simulationTransport.SimulationCoordinator, coordinator) ||
                simulationCoordinator != null && !ReferenceEquals(simulationCoordinator, coordinator)) return;
            var options = coordinator.SessionOptions;
            if (session.State.GameId != simulationGameId || launchContext.IsLeavingGameplay || launchContext.GameId != simulationGameId ||
                launchContext.Mode != simulationLaunchMode || launchContext.LocalPlayerId != localPlayerId ||
                options.GameId != simulationGameId || options.LocalPlayerId != localPlayerId || options.SimulationEpoch != simulationEpoch ||
                options.MembershipVersion != simulationMembershipVersion) return;
            simulationCoordinator = coordinator;
            simulationTransport.ConfigureConfirmedRecovery(new SimulationConfirmedRecoveryInstaller(session, simulationRegistry,
                GameStateViewer.Player(localPlayerId), options));
            lastAppliedResultSequence = coordinator.LastAppliedSequence; lastValidatedSequence = coordinator.ValidatedThroughSequence;
            simulationPermissions.Clear();
            foreach (var permission in coordinator.ReadWindowPermissions()) StoreSimulationPermission(permission);
            EnsureSimulationLocalProjection(); refreshFromState();
        }

        private bool StoreSimulationPermission(SimulationWindowPermissionDto permission)
        {
            var window = permission?.Window;
            if (window == null || window.ProtocolVersion != OperationCommandProtocol.Version ||
                !MatchesSimulation(window.GameId, window.SimulationEpoch, window.MembershipVersion) || string.IsNullOrEmpty(window.OperationWindowId)) return false;
            simulationPermissions[window.OperationWindowId] = permission.IsOpen && permission.IsPermitted && permission.CanSubmitForLocalPlayer;
            return true;
        }

        private void OnSimulationPermissionChanged(SimulationWindowPermissionDto permission)
        {
            if (!SimulationBindingIsCurrent) return;
            if (!StoreSimulationPermission(permission)) return;
            using var permissionSample = new ExecutionSample(session.ExecutionTimings, "consumer.permission");
            EnsureSimulationLocalProjection();
            refreshFromState();
        }

        private void EnsureSimulationLocalProjection()
        {
            var state = session.State;
            var view = session.View;
            if (ReferenceEquals(simulationProjectionState, state) && ReferenceEquals(simulationProjectionView, view) &&
                view != null && view.GameId == state.GameId && view.ContentPackHash == state.ContentPackHash &&
                view.ViewerRole == GameStateViewerRole.Player && view.ViewerPlayerId == localPlayerId &&
                view.StateRevision == (state.EffectRuntime?.StateRevision ?? 0))
            {
                session.ExecutionTimings.AddSample("projection.reuse", 0);
                return;
            }
            // 许可可能先于 Applied 发布；同一状态的后续通知继续使用这个本人投影。
            using var projectionSample = new ExecutionSample(session.ExecutionTimings, "projection.build");
            simulationProjectionView = session.RefreshLocalProjection(GameStateViewer.Player(localPlayerId));
            simulationProjectionState = state;
        }

        private void OnSimulationCommandApplied(SimulationCommandAppliedDto applied)
        {
            if (!SimulationBindingIsCurrent || simulationCoordinator == null || applied?.Command == null || applied.RuleResult == null ||
                applied.ProtocolVersion != OperationCommandProtocol.Version || !MatchesSimulation(applied.GameId, applied.SimulationEpoch, applied.MembershipVersion) ||
                deliveredSimulationCommandIds.Contains(applied.Command.CommandId) || applied.GlobalCommandSequence <= lastAppliedResultSequence ||
                (applied.RuleResult.Succeeded ? applied.GlobalCommandSequence > simulationCoordinator.LastAppliedSequence :
                    applied.GlobalCommandSequence != simulationCoordinator.LastAppliedSequence + 1)) return;
            using var appliedSample = new ExecutionSample(session.ExecutionTimings, "consumer.applied");
            lastAppliedResultSequence = applied.GlobalCommandSequence;
            deliveredSimulationCommandIds.Add(applied.Command.CommandId);
            EnsureSimulationLocalProjection();
            bool resolved = ResolveSubmitted(applied.Command.CommandId, applied.Command.PlayerId, applied.GameId,
                applied.RuleResult.Succeeded, applied.RuleResult.Validation?.ErrorCode ?? CommandErrorCode.UnknownCommand);
            if (resolved)
            {
                cityStyleCommandResolved?.Invoke(applied.Command.CommandId, applied.RuleResult.Succeeded);
                commandSettled?.Invoke(applied.Command.CommandId);
            }
            refreshFromState();
            if (!applied.RuleResult.Succeeded) setPrompt(applied.RuleResult.Validation?.Reason ?? applied.RuleResult.LogMessage);
        }

        private void OnSimulationProposalRejected(SimulationProposalResultDto result)
        {
            if (result == null || result.ProtocolVersion != OperationCommandProtocol.Version ||
                !MatchesSimulation(result.GameId, result.SimulationEpoch, result.MembershipVersion) || result.Proposal?.Command == null ||
                result.Proposal.GameId != result.GameId || result.Proposal.SimulationEpoch != result.SimulationEpoch ||
                result.Proposal.MembershipVersion != result.MembershipVersion ||
                result.Status == OperationAdmissionStatus.Accepted || result.Status == OperationAdmissionStatus.Duplicate) return;
            var command = result.Proposal.Command;
            EnsureSimulationLocalProjection();
            if (!ResolveSubmitted(command.CommandId, command.PlayerId, result.GameId, false, result.ErrorCode, result.RequiresRefresh)) return;
            cityStyleCommandResolved?.Invoke(command.CommandId, false); commandSettled?.Invoke(command.CommandId);
            refreshFromState(); setPrompt(result.Reason);
        }

        private void OnSimulationValidationAdvanced(CommandValidationPrefixDto prefix)
        {
            if (prefix == null || prefix.ProtocolVersion != OperationCommandProtocol.Version ||
                !MatchesSimulation(prefix.GameId, prefix.SimulationEpoch, prefix.MembershipVersion) || simulationCoordinator == null ||
                prefix.ValidatedThroughSequence <= lastValidatedSequence || prefix.ValidatedThroughSequence > simulationCoordinator.ValidatedThroughSequence) return;
            using var validationSample = new ExecutionSample(session.ExecutionTimings, "consumer.validation");
            lastValidatedSequence = prefix.ValidatedThroughSequence;
            refreshFromState();
        }

        private void OnSimulationFaulted(SimulationProtocolFaultDto fault)
        {
            if (fault == null || fault.ProtocolVersion != OperationCommandProtocol.Version ||
                !MatchesSimulation(fault.GameId, fault.SimulationEpoch, fault.MembershipVersion)) return;
            simulationStopped = true; simulationPermissions.Clear();
            refreshFromState(); setPrompt(fault.Diagnostic);
        }

        public CommandResult Submit(GameCommand command, out bool appliedLocally)
        {
            if (disposed || UsesDeterministicSimulation && !SimulationBindingIsCurrent)
            {
                appliedLocally = false;
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.UnknownCommand,
                    "当前模拟会话已停止或切换，请返回房间重试。"));
            }
            submittedCommands.Record(session.State.GameId, command);
            if (UsesDeterministicSimulation && command != null && command.PlayerId == localPlayerId && !string.IsNullOrEmpty(command.CommandId))
                unresolvedSimulationSubmissions[command.CommandId] = new KeyValuePair<string, int>(session.State.GameId, command.PlayerId);
            if (disposed || launchContext != null && launchContext.IsLeavingGameplay)
            {
                appliedLocally = false;
                ResolveSubmitted(command?.CommandId, command?.PlayerId ?? -1, null, false, CommandErrorCode.UnknownCommand);
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.UnknownCommand,
                    "当前对局连接已结束，请返回开始页。"));
            }
            if (commandTransport != null)
            {
                var transported = commandTransport.SubmitOrSend(command, out appliedLocally);
                if (transported != null && (appliedLocally || !transported.Succeeded))
                    ResolveSubmitted(command?.CommandId, command?.PlayerId ?? -1, null, transported.Succeeded,
                        transported.Validation?.ErrorCode ?? CommandErrorCode.UnknownCommand,
                        UsesDeterministicSimulation && transported.Validation?.ErrorCode == CommandErrorCode.StateRevisionMismatch);
                return transported;
            }

            if (GameSessionBootstrapper.IsNetworkLaunch(launchContext))
            {
                appliedLocally = false;
                ResolveSubmitted(command?.CommandId, command?.PlayerId ?? -1, null, false, CommandErrorCode.UnknownCommand);
                return CommandResult.Invalid(ValidationResult.Failure(
                    CommandErrorCode.UnknownCommand,
                    "\u8054\u7f51\u540c\u6b65\u672a\u521d\u59cb\u5316"));
            }

            CommandResult result;
            using (RuleSubmitMarker.Auto()) result = session.Submit(command);
            appliedLocally = result.Succeeded;
            ResolveSubmitted(command?.CommandId, command?.PlayerId ?? -1, null, result.Succeeded,
                result.Validation?.ErrorCode ?? CommandErrorCode.UnknownCommand);
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; simulationStopped = true; simulationPermissions.Clear();
            if (sessionNoticeSubscribed && launchContext != null)
            {
                launchContext.OnlineSessionNotice -= OnOnlineSessionNotice;
                sessionNoticeSubscribed = false;
            }

            if (commandTransport == null)
            {
                return;
            }

            if (!ReferenceEquals(simulationTransport, null))
            {
                bool ownsTransport = OwnsSimulationTransport;
                simulationSubscriptionGeneration++;
                UnbindSimulationSubscriptions();
                if (ownsTransport)
                {
                    SimulationTransportOwners.Remove(simulationTransport);
                    commandTransport.Shutdown();
                }
                commandTransport = null; simulationTransport = null; simulationCoordinator = null;
                return;
            }

            commandTransport = null;
        }

        private void OnOnlineSessionNotice(string message)
        {
            if (disposed || launchContext == null || launchContext.IsLeavingGameplay ||
                UsesDeterministicSimulation && (!OwnsSimulationTransport || launchContext.GameId != simulationGameId || session.State.GameId != simulationGameId ||
                    launchContext.Mode != simulationLaunchMode || launchContext.LocalPlayerId != localPlayerId)) return;
            launchContext?.TryConsumeOnlineSessionNotice(out _);
            setPrompt(message);
        }

        private bool ResolveSubmitted(string commandId, int answeringPlayerId, string receivedGameId,
            bool accepted, CommandErrorCode errorCode, bool requiresRefresh = false)
        {
            if (!submittedCommands.TryResolve(commandId, answeringPlayerId, receivedGameId, out var submittedGameId)) return false;
            unresolvedSimulationSubmissions.Remove(commandId);
            bool previous = RequiresExplicitRefreshForCurrentResult;
            RequiresExplicitRefreshForCurrentResult = requiresRefresh;
            try
            {
                interactionCommandResolved?.Invoke(submittedGameId, commandId, accepted, errorCode);
                SubmissionResolutionObserved?.Invoke(submittedGameId, commandId, accepted, errorCode, requiresRefresh);
            }
            finally { RequiresExplicitRefreshForCurrentResult = previous; }
            return true;
        }
    }
}
