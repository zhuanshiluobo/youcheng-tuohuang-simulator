#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Effects;
using YC.Infrastructure.Multiplayer;
using YC.Infrastructure.Lua;
using YC.Presentation.Validation;

namespace YC.Presentation
{
    // 显式命令行启动的集成驱动。决策只读取本人投影；命令经过实际房间与 Mirror 入口。
    public sealed class NetworkGameValidationDriver : MonoBehaviour
    {
        private string output;
        private bool enterprise, stopped, roomBusy, disconnected, reconnected;
        private float started, nextAction, lastProgressTime, nextDiagnostic;
        private int commands, initialSnapshots, confirmations, snapshotsBeforeDisconnect;
        private int validationTimeout;
        private float nextProgressWarningTime;
        private string lastSignature, interruptedRequest, observedState, frozenHash, frozenConfiguration;
        private string[] interruptedCandidates;
        private string interruptedContract;
        private readonly HashSet<string> captures = new HashSet<string>();
        private readonly HashSet<int> rounds = new HashSet<int>();
        private MirrorCommandTransport subscribed;
        private GameStateView latest;
        private ProductionGameRoutePlan routePlan;
        private ProductionGameRoutePolicy routePolicy;
        private bool observationOnly;
        private int lastSavedRevision = -1;
        private static GameSession recoverySession;
        private static EffectRegistry recoveryRegistry;
        private readonly HashSet<string> recoveredCheckpoints = new HashSet<string>(StringComparer.Ordinal);
        private HostSessionArchiveDto auditArchive;
        private HostSnapshotDto previousBusinessSnapshot;
        private string lastStaleRefreshEvidence;
        private bool finalHostExportStarted;
        private object lastCompletedHostProtocolSummary;
        private string boundGameId, terminationFailure;
        private bool WholeGameHealth => routePlan?.EvaluationMode == "whole-game-health";
        private bool UnboundedWholeGame => WholeGameHealth && !diagnosticSampleRequested;
        private bool ValidationDeadlineExceeded => !UnboundedWholeGame && validationTimeout > 0 && Time.realtimeSinceStartup - started > validationTimeout;
        private bool CommandBudgetExhausted => !UnboundedWholeGame && routePlan != null && routePlan.MaximumCommands > 0 && commands >= routePlan.MaximumCommands;
        private readonly HashSet<string> recordedCompletedEffects = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> recordedCharacterModes = new HashSet<string>(StringComparer.Ordinal);
        private DeterministicSimulationCoordinator simulationCoordinator;
        private long simulationEpoch, subscriptionGeneration, localAppliedSequence, hostValidatedSequence, recoveryTargetEpoch;
        private int simulationMembership, simulationPlayer;
        private bool recoveringSimulation;
        private bool awaitingAcceptedSessionRecovery;
        private MirrorNetworkRuntime recoveryRuntime;
        private GameCommandDto pendingSimulationCommand, recoveryPendingCommand;
        private string activeRecoveryCheckpoint;
        private Action<DeterministicSimulationCoordinator> readySubscription, recoveryCompletedSubscription;
        private Action<SimulationCommandAppliedDto> appliedSubscription;
        private Action<CommandValidationPrefixDto> prefixSubscription;
        private Action<SimulationWindowPermissionDto> permissionSubscription;
        private Action<SimulationProposalResultDto> rejectedSubscription;
        private Action<SimulationProtocolFaultDto> faultSubscription;
        private Action<SimulationRecoveryNoticeDto> recoveryStartedSubscription;
        private bool phaseTimingRequested;
        private bool diagnosticSampleRequested;
        private ExecutionTimingCounters phaseTimings;
        private long nextPhaseTimingSequence;
        private long decisionCalls, decisionTicks;
        private long diagnosticViewCount, diagnosticViewTicks;
        private long diagnosticViewReuseCount, diagnosticViewFallbackCount, diagnosticViewRejectedCount, diagnosticViewLegacyCount;
        private readonly Dictionary<string, DiagnosticIoTotals> diagnosticIo = new Dictionary<string, DiagnosticIoTotals>(StringComparer.Ordinal);
        private sealed class DiagnosticIoTotals
        {
            public long Records, JsonCharacters, SerializeTicks, AppendTicks;
        }

        // 由真实Bootstrapper在全部注册完成后传入同一会话/注册表；驱动不重建规则注册表。
        public static void BindHostRecoverySession(GameSession hostSession, EffectRegistry actualRegistry)
        {
            if (hostSession == null || actualRegistry == null || string.IsNullOrEmpty(actualRegistry.ContentPackHash) ||
                hostSession.State.ContentPackHash != actualRegistry.ContentPackHash) throw new ArgumentException("恢复绑定必须使用同一正式会话与活动注册表。");
            recoverySession = hostSession; recoveryRegistry = actualRegistry;
        }

        internal static HostRecoveryResult RecoverBoundHostSession(GameSession hostSession, HostSessionArchiveDto archive)
        {
            if (!ReferenceEquals(recoverySession, hostSession) || recoveryRegistry == null)
                throw new InvalidOperationException("host_recovery_registry_not_bound");
            if (archive?.Snapshot?.ContentHash != recoveryRegistry.ContentPackHash || hostSession.State.ContentPackHash != recoveryRegistry.ContentPackHash)
                throw new InvalidOperationException("host_recovery_content_hash_mismatch");
            return hostSession.RecoverHost(archive, recoveryRegistry);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRecoveryBinding() { recoverySession = null; recoveryRegistry = null; }
        private static string Argument(string prefix) => Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length) ?? string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartRequestedValidation()
        {
            string path = Argument("--yc-network-validation-output=");
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) return;
            var host = new GameObject("联机整合验证"); DontDestroyOnLoad(host);
            var driver = host.AddComponent<NetworkGameValidationDriver>(); driver.output = path;
            driver.phaseTimingRequested = Argument("--yc-network-validation-phase-timing=") == "true";
            driver.diagnosticSampleRequested = Argument("--yc-network-validation-diagnostic-sample=") == "true";
            string inputMode = Argument("--yc-production-input-mode=");
            driver.observationOnly = inputMode == "external";
            string planPath = Argument("--yc-production-route-plan=");
            if (planPath.Length > 0)
            {
                try
                {
                    if (!Path.IsPathRooted(planPath)) throw new ArgumentException("路线路径必须是绝对路径。");
                    driver.routePlan = JsonConvert.DeserializeObject<ProductionGameRoutePlan>(File.ReadAllText(planPath));
                    driver.routePolicy = new ProductionGameRoutePolicy(driver.routePlan);
                    driver.Write("route-plan.json", driver.routePlan);
                }
                catch (Exception error) { driver.Finish("route_plan_invalid:" + error.Message); return; }
            }
            if (inputMode.Length > 0 && (driver.routePlan == null || inputMode != "internal" && inputMode != "external"))
            { driver.Finish("production_input_contract_invalid"); return; }
            driver.enterprise = Argument("--yc-network-validation-expansion=") == "enterprise";
            driver.started = Time.realtimeSinceStartup;
            try { driver.ConfigureValidationLimits(Argument("--yc-network-validation-timeout=")); }
            catch (Exception error) { driver.Finish("validation_limits_invalid:" + error.Message); }
        }

        private void ConfigureValidationLimits(string argument)
        {
            int requested = 0;
            if (!string.IsNullOrEmpty(argument) && (!int.TryParse(argument, out requested) || requested < 0))
                throw new ArgumentException("验证时限必须是非负整数，0表示不设期限。");
            validationTimeout = UnboundedWholeGame ? 0 : requested;
            Write("validation-limits.json", new { utc = DateTime.UtcNow, Version = "whole-game-unbounded-v1",
                EvaluationMode = routePlan?.EvaluationMode, DiagnosticSampling = diagnosticSampleRequested,
                RequestedTimeoutSeconds = requested, EffectiveTimeoutSeconds = validationTimeout,
                MaximumCommands = UnboundedWholeGame ? 0 : routePlan?.MaximumCommands ?? 0,
                NoProgressHardStopSeconds = UnboundedWholeGame ? 0 : 180,
                NoProgressWarningSeconds = 180, StaleRefreshWaitIsHardStop = !WholeGameHealth });
        }

        private bool ObserveAuthoritativeProgress(GameStateView view)
        {
            string stateKey = view.StateRevision + ":" + view.Round + ":" + view.Phase;
            float now = Time.realtimeSinceStartup;
            if (observedState != stateKey) { observedState = stateKey; lastProgressTime = now; nextProgressWarningTime = 0; }
            else if (now - lastProgressTime > 180f)
            {
                if (!UnboundedWholeGame) { Finish("no_authoritative_progress"); return false; }
                if (now >= nextProgressWarningTime)
                {
                    nextProgressWarningTime = now + 180f;
                    Append("validation-warnings.jsonl", new { utc = DateTime.UtcNow, Kind = "no_authoritative_progress_observation",
                        view.GameId, view.ViewerPlayerId, view.StateRevision, view.Round, view.Phase,
                        WaitingSeconds = now - lastProgressTime, PendingCommandId = pendingSimulationCommand?.CommandId,
                        Requests = view.Interactions.Select(r => r.InteractionId).ToArray(), view.WaitingForPlayerIds,
                        G = simulationCoordinator?.LastAppliedSequence, R = simulationCoordinator?.ValidatedThroughSequence,
                        StaleRefresh = routePolicy?.StaleInteractionRefreshEvidence, AutomaticallyStopped = false });
                }
            }
            return true;
        }

        private void Update()
        {
            if (stopped || output == null) return;
            if (Time.realtimeSinceStartup >= nextDiagnostic)
            {
                nextDiagnostic = Time.realtimeSinceStartup + 5f;
                var runtime = MirrorNetworkRuntime.Instance;
                Write("runtime-diagnostics.json", new {
                    utc = DateTime.UtcNow, scene = SceneManager.GetActiveScene().name,
                    room = OnlineRoomServiceProvider.GetActive()?.GetCurrentRoom(),
                    clientActive = NetworkClient.active, clientConnected = NetworkClient.isConnected,
                    serverActive = NetworkServer.active, transportConnected = runtime?.ActiveTransport?.ClientConnected(),
                    transportEnabled = runtime?.ActiveTransport?.enabled,
                    networkAddress = runtime?.Manager?.networkAddress,
                    revision = latest?.StateRevision, round = latest?.Round, phase = latest?.Phase,
                    initialSnapshots, confirmations, commands, disconnected, reconnected,
                    visibleText = FindObjectsOfType<UnityEngine.UI.Text>().Where(t => t.isActiveAndEnabled).Select(t => t.text).ToArray()
                });
            }
            if (ValidationDeadlineExceeded) { Finish("validation_timeout"); return; }
            try { routePolicy?.CheckStaleInteractionRefreshTimeout(); }
            catch (Exception error)
            { RecordStaleRefresh("等待终止：" + error.Message); Finish("route_refresh_failed:" + error.Message); return; }
            if (subscribed == null && MirrorCommandTransport.Instance != null)
            {
                AttachSimulationTransport(MirrorCommandTransport.Instance);
            }
            if (simulationCoordinator == null && subscribed?.SimulationCoordinator != null)
                OnSimulationReady(subscribed.SimulationCoordinator);
            BindRecoveryRuntime(MirrorNetworkRuntime.Instance);
            if (awaitingAcceptedSessionRecovery && subscribed != null && !subscribed.IsAwaitingLocalSimulationConnectionRecovery &&
                !string.IsNullOrEmpty(subscribed.LocalSimulationConnectionRecoveryFailureCode))
                OnNetworkDisconnected();
            if (stopped) return;
            if (awaitingAcceptedSessionRecovery || recoveringSimulation) return;
            if (Time.realtimeSinceStartup < nextAction) return;
            nextAction = Time.realtimeSinceStartup + 0.15f;
            if (SceneManager.GetActiveScene().name == "StartScene") { if (!observationOnly) PrepareRoom(); return; }
            // 保留上方订阅；成员只在真实初始同步成功回调后消费当前投影。
            if (IsAwaitingClientInitialState) return;
            var controller = FindObjectOfType<MobileCityInteractionController>();
            if (controller == null || GameLaunchContext.Instance == null) return;
            int player = GameLaunchContext.Instance.LocalPlayerId;
            var currentView = ReadValidationView(controller, player);
            if (currentView == null || string.IsNullOrEmpty(currentView.GameId) || currentView.Players.Count < 2) return;
            if (!TryAcceptRunView(currentView)) return;
            latest = currentView;
            if (latest.ViewerRole != GameStateViewerRole.Player || latest.ViewerPlayerId != player)
            { Finish("not_owner_player_projection"); return; }
            if (!ObserveAuthoritativeProgress(latest)) return;
            string configuration = JsonConvert.SerializeObject(latest.RuleConfiguration);
            if (frozenHash == null) { frozenHash = latest.ContentPackHash; frozenConfiguration = configuration; }
            else if (frozenHash != latest.ContentPackHash || frozenConfiguration != configuration) { Finish("frozen_configuration_changed"); return; }
            if (routePlan != null && (routePlan.ContentPackHash != latest.ContentPackHash || latest.MapId != routePlan.MapId ||
                latest.Players.Count != routePlan.PlayerCount || !latest.RuleConfiguration.EnabledExpansionIds.OrderBy(id => id).SequenceEqual(routePlan.EnabledExpansionIds.OrderBy(id => id))))
            { Finish("route_configuration_mismatch"); return; }
            if (lastSavedRevision != latest.StateRevision)
            {
                bool firstOrRecoveredProjection = lastSavedRevision < 0;
                lastSavedRevision = latest.StateRevision;
                Append("projections.jsonl", new { utc = DateTime.UtcNow,
                    view = WholeGameDiagnosticView(latest, WholeGameHealth && SimulationCurrent && !firstOrRecoveredProjection) });
                if (GameLaunchContext.Instance.Mode == LaunchMode.Host && routePlan != null)
                {
                    try
                    {
                        if (!WholeGameHealth || auditArchive == null)
                        {
                        string archivePath = Path.Combine(output, "host-checkpoint.json");
                        var archive = CaptureHostArchiveStage("checkpoint", "CreateHostArchive", null, archivePath, controller.ValidationHostArchive);
                        if (previousBusinessSnapshot == null) previousBusinessSnapshot = archive?.Snapshot;
                        if (auditArchive == null) auditArchive = archive;
                        else CaptureHostArchiveStage("checkpoint", "AppendJournal", auditArchive, archivePath,
                            () => { controller.ValidationAppendHostJournal(auditArchive, boundGameId); return true; });
                        if (!WholeGameHealth)
                        {
                            string archiveJson = CaptureHostArchiveStage("checkpoint", "SerializeArchive", archive, archivePath,
                                () => HostRecoveryService.SerializeArchive(archive));
                            CaptureHostArchiveStage("checkpoint", "WriteAllText", archive, archivePath,
                                () => { File.WriteAllText(archivePath, archiveJson); return true; });
                        }
                        }
                    }
                    catch (Exception error) { Finish("host_journal_capture_failed:" + error.Message); return; }
                    if (!File.Exists(Path.Combine(output, "active-catalog.json")))
                    {
                        try
                        {
                            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentRuntime.Pack, latest.RuleConfiguration);
                            if (catalog.ContentPackHash != latest.ContentPackHash) throw new InvalidOperationException("活动目录与本局哈希不一致。");
                            Write("active-catalog.json", new { catalog.ContentPackHash, catalog.EnabledExpansionIds, catalog.RuleModeId,
                                nativeDefinitions = catalog.ContentDefinitions,
                                definitions = catalog.Definitions.Select(d => new { d.DefinitionId, d.RuntimeId, d.ContentType, d.Version, d.Abilities, d.DataTemplate, d.Data }) });
                        }
                        catch (Exception error) { Finish("active_catalog_capture_failed:" + error.Message); return; }
                    }
                }
            }
            if (latest.IsPausedFault) { Finish("effect_fault:" + latest.FaultCode); return; }
            if (latest.Players.Any(p => p.PlayerId != player && (p.HandCardIds.Count != 0 || p.CoveredCharacterCardId.Length != 0)))
            { Finish("opponent_private_cards_visible"); return; }
            if (latest.ItemZones.Zones.Any(z => !z.IdentitiesVisible &&
                (latest.ItemZones.Items.Any(i => i.ZoneId == z.Zone.ZoneId && !(z.Zone.TemplateId == "core.zone.box_secret_retired" &&
                    i.PhysicalKind == YC.Domain.Items.ItemPhysicalKind.Card && i.ControllerSubject.Kind == YC.Domain.Items.ItemSubjectKind.Player &&
                    i.ControllerSubject.PlayerId == player)) || latest.ItemZones.Orders.Any(o => o.ZoneId == z.Zone.ZoneId))))
            { Finish("hidden_zone_identities_visible"); return; }
            if (latest.Round > 0) rounds.Add(latest.Round);
            if (latest.Phase == GamePhase.FinalScoring && latest.FinalScoring != null && latest.FinalScoring.IsResolved)
            {
                if (!SimulationCurrent || recoveringSimulation || simulationCoordinator.HasPendingLocalApplication ||
                    simulationCoordinator.ValidatedThroughSequence < simulationCoordinator.LastAppliedSequence) return;
                if (finalHostExportStarted) return;
                Capture("八回合终局");
                Write("final-view.json", latest);
                RefreshFinalProtocolSummary(controller);
                if (GameLaunchContext.Instance.Mode == LaunchMode.Host)
                {
                    finalHostExportStarted = true;
                    // 保持帧循环，让可靠通道先送达真实终局；成员按原结果路径自然退出后再写大档案。
                    // 正式整局等待只观察进展，不因经过时间结束，也不结束任何远端进程。
                    StartCoroutine(HostArchiveCaptureDiagnostics.AfterRemotePlayersExit(
                        () => NetworkServer.connections.Values.Any(c => !(c is LocalConnectionToClient)),
                        () => stopped, () => WriteFinalHostArchive(controller)));
                    return;
                }
                Finish(null); return;
            }
            // 外部模式只观察真实投影与确认事件；禁止房间操作、提交命令、自动断连。
            if (observationOnly) return;
            if (TryRecoverHostCheckpoint(controller, player)) return;
            if (!SimulationCurrent || recoveringSimulation || simulationCoordinator.IsStopped || simulationCoordinator.HasPendingLocalApplication ||
                pendingSimulationCommand != null || !controller.ValidationCanSubmit ||
                !simulationCoordinator.ReadWindowPermissions().Any(p => p.CanSubmitForLocalPlayer) ||
                GameLaunchContext.Instance.Mode == LaunchMode.Client && (!NetworkClient.isConnected || subscribed.IsSynchronizing)) return;
            var request = latest.Interactions.FirstOrDefault(r => r.Status == "open" && r.AnsweringPlayerId == player &&
                !r.Kind.StartsWith(InteractionRequest.MainlineCompletionInteractionPrefix, StringComparison.Ordinal) && r.Kind != "collection.task");
            string signature = latest.StateRevision + ":" + latest.Phase + ":" + latest.CurrentPlayerId + ":" + request?.InteractionId;
            if (signature == lastSignature && routePolicy?.IsWaitingForStaleInteractionRefresh != true) return;
            if (request != null)
            {
                if (Capture("交互-" + request.Kind)) { Write("view-" + Safe(request.Kind) + ".json", latest); nextAction += 0.7f; return; }
                if (simulationCoordinator == null && !disconnected && Argument("--yc-network-validation-reconnect=") == "true" &&
                    GameLaunchContext.Instance.Mode == LaunchMode.Client && initialSnapshots > 0)
                {
                    disconnected = true; interruptedRequest = request.InteractionId;
                    interruptedCandidates = request.CandidateIds.ToArray();
                    interruptedContract = RequestContract(latest, request.InteractionId);
                    snapshotsBeforeDisconnect = initialSnapshots;
                    Write("before-reconnect-view.json", latest);
                    NetworkClient.Disconnect(); nextAction += 2f; return;
                }
                if (disconnected && !reconnected) return;
            }
            GameCommand command = null;
            if (routePolicy != null)
            {
                try
                {
                    command = DecideValidationRoute(latest, player, out string waiting);
                    RecordStaleRefresh(command == null ? "等待新投影" : "重建提交", command?.CommandId);
                    Write("route-progress.json", new { utc = DateTime.UtcNow, latest.StateRevision, completedSteps = routePolicy.CompletedSteps, skippedSteps = routePolicy.SkippedSteps, waiting });
                }
                catch (Exception error) { RecordStaleRefresh("决策终止：" + error.Message); Finish("route_decision_failed:" + error.Message); return; }
            }
            else if (request != null) command = Answer(request, player, latest);
            else if (routePolicy == null && latest.Phase == GamePhase.ResourceCollection)
            {
                var own = latest.Players.Single(p => p.PlayerId == player);
                if (!own.HasCollectedResourcesThisRound)
                {
                    command = new GameCommand { Kind = GameCommandKind.CollectResource, PlayerId = player };
                    if (!string.IsNullOrEmpty(own.CityLocationId)) command.OptionIds.Add(own.CityLocationId);
                }
            }
            else if (routePolicy == null && latest.CurrentPlayerId == player && (latest.Phase == GamePhase.Entrance || latest.Phase == GamePhase.ActionRound1 || latest.Phase == GamePhase.ActionRound2 || latest.Phase == GamePhase.Cleanup))
            {
                // 他人的秘密请求只暴露等待玩家，不能因看不到候选而抢先结束主链。
                if (latest.WaitingForPlayerIds.Any(id => id != player)) return;
                var own = latest.Players.Single(p => p.PlayerId == player);
                command = (latest.Phase == GamePhase.ActionRound1 || latest.Phase == GamePhase.ActionRound2) && !own.ActedMainActionThisTurn
                    ? MainAction(GameStateViewProjector.ToClientState(latest), player)
                    : new GameCommand { PlayerId = player, Kind = GameCommandKind.EndAction };
            }
            if (command == null) return;
            if (CommandBudgetExhausted) { Finish("route_command_budget_exhausted"); return; }
            lastSignature = signature;
            if (routePolicy == null) command.CommandId = Guid.NewGuid().ToString("N");
            pendingSimulationCommand = GameCommandDto.FromCommand(command);
            File.AppendAllText(Path.Combine(output, "commands.jsonl"), JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, latest.Round, latest.Phase, latest.StateRevision, command }) + "\n");
            commands++;
            if (routePolicy != null) Append("route-command-trace.jsonl", new { utc = DateTime.UtcNow, stepId = routePolicy.PendingStepId, latest.StateRevision, command,
                view = WholeGameDiagnosticView(latest, WholeGameHealth && SimulationCurrent) });
            controller.SubmitValidationCommand(command);
            // 同步拒绝没有提案事件时，保未获回执事实，禁止把空队列当成功。
            if (pendingSimulationCommand != null && simulationCoordinator.ReadPendingLocalProposal()?.Command?.CommandId != command.CommandId)
                Finish("validation_submission_not_admitted:" + command.CommandId);
        }

        private bool TryRecoverHostCheckpoint(MobileCityInteractionController controller, int player)
        {
            if (simulationCoordinator != null) return TryRecoverConfirmedCheckpoint(player);
            if (routePlan == null || GameLaunchContext.Instance.Mode != LaunchMode.Host || routePolicy.HasPendingCommand) return false;
            var checkpoint = routePlan.RecoveryCheckpoints.FirstOrDefault(c => !recoveredCheckpoints.Contains(c.CheckpointId) && c.PlayerId == player && c.Matches(latest));
            if (checkpoint == null) return false;
            string directory = Path.Combine(output, "host-recovery", checkpoint.CheckpointId);
            try
            {
                if (Directory.Exists(directory)) throw new InvalidOperationException("host_recovery_evidence_already_exists");
                Directory.CreateDirectory(directory);
                var before = controller.ValidationHostArchive();
                RequireRunGameId(before?.Snapshot?.State?.GameId);
                if (before == null || before.Snapshot.State.GameId != latest.GameId || before.Snapshot.ContentHash != frozenHash)
                    throw new InvalidOperationException("host_recovery_checkpoint_mismatch");
                var open = before.Snapshot.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.AnsweringPlayerId == player &&
                    r.InteractionTypeId == checkpoint.RequestKind && (checkpoint.PromptKey.Length == 0 || r.PromptKey == checkpoint.PromptKey));
                var payments = before.Snapshot.State.EffectRuntime.EffectNodes.Where(n => n.PlayerId == player && n.Payment != null && !n.Payment.IsCommitted &&
                    SettlementEntryService.IsWithin(before.Snapshot.State.EffectRuntime, open.OwnerEffectId, n.EffectId)).Select(n => n.Payment.PaymentId).OrderBy(id => id).ToArray();
                if (checkpoint.RequirePayment && payments.Length == 0)
                    throw new InvalidOperationException("host_recovery_payment_checkpoint_missing");
                string beforeJson = HostRecoveryService.SerializeArchive(before);
                File.WriteAllText(Path.Combine(directory, "before-host-recovery.json"), beforeJson);
                // 保留正式CreateHostArchive返回的原对象；落盘字节核验后直接交正式恢复入口，不改字段/重造根树。
                if (File.ReadAllText(Path.Combine(directory, "before-host-recovery.json")) != beforeJson) throw new IOException("恢复输入落盘核验失败。");
                var result = controller.ValidationRecoverHost(before);
                if (HostRecoveryService.SerializeArchive(before) != beforeJson) throw new InvalidOperationException("host_recovery_input_archive_changed");
                var restored = controller.ValidationHostArchive();
                RequireRunGameId(restored?.Snapshot?.State?.GameId);
                if (restored != null) File.WriteAllText(Path.Combine(directory, "restored-host-archive.json"), HostRecoveryService.SerializeArchive(restored));
                var evidence = new { utc = DateTime.UtcNow, checkpoint.CheckpointId, RuntimeMethod = "GameSession.RecoverHost",
                    InputSource = "GameSession.CreateHostArchive", RecoveryKind = "same_process_checkpoint", PersistedBeforeRecover = true,
                    GameId = latest.GameId, ContentPackHash = frozenHash, PlayerId = player, InteractionId = open.GetStableInteractionId(),
                    WindowId = latest.ActiveSettlementWindowId, PaymentIds = payments, result.Succeeded, result.FaultCode, result.Diagnostic,
                    BeforeArchiveSha256 = FileSha256(Path.Combine(directory, "before-host-recovery.json")),
                    RestoredArchiveSha256 = FileSha256(Path.Combine(directory, "restored-host-archive.json")),
                    processId = System.Diagnostics.Process.GetCurrentProcess().Id };
                File.WriteAllText(Path.Combine(directory, "host-recovery-result.json"), JsonConvert.SerializeObject(evidence, Formatting.Indented));
                if (recoveredCheckpoints.Count == 0)
                {
                    foreach (string name in new[] { "before-host-recovery.json", "restored-host-archive.json", "host-recovery-result.json" })
                        File.Copy(Path.Combine(directory, name), Path.Combine(output, name), false);
                }
                Append("host-recovery-events.jsonl", evidence);
                if (!result.Succeeded) throw new InvalidOperationException("host_recovery_failed:" + result.FaultCode);
                recoveredCheckpoints.Add(checkpoint.CheckpointId);
                var restoredView = controller.ValidationView(player);
                if (!TryAcceptRunView(restoredView)) return true;
                latest = restoredView;
                lastSignature = null; nextAction = Time.realtimeSinceStartup + 0.7f;
                return true;
            }
            catch (Exception error)
            {
                if (Directory.Exists(directory)) File.WriteAllText(Path.Combine(directory, "driver-failure.json"), JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, checkpoint.CheckpointId, failure = error.Message }, Formatting.Indented));
                Finish("host_recovery_driver_failed:" + error.Message); return true;
            }
        }

        private async void PrepareRoom()
        {
            if (stopped || roomBusy || observationOnly) return;
            var service = OnlineRoomServiceProvider.GetActive(); var room = service?.GetCurrentRoom();
            if (room == null || room.Configuration == null || room.HasStarted || room.LocalPlayerId <= 0 || room.HostPlayerId <= 0) return;
            RoomConfigurationRequest request = null;
            var expectedExpansions = routePlan?.EnabledExpansionIds ?? (enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" });
            string expectedMap = routePlan?.MapId ?? room.Configuration.MapId;
            bool configurationMatches = room.Configuration.MapId == expectedMap && room.Configuration.RuleModeId == "standard" &&
                room.Configuration.EnabledExpansionIds.OrderBy(id => id).SequenceEqual(expectedExpansions.OrderBy(id => id));
            if (room.LocalPlayerId == room.HostPlayerId && !configurationMatches)
                request = new ChangeRoomConfigurationRequest { MapId = expectedMap, RuleModeId = "standard", EnabledExpansionIds = expectedExpansions.ToList() };
            else if (room.LocalPlayerId != room.HostPlayerId && configurationMatches)
            {
                var seat = room.SeatSlots.Single(s => s.Occupant?.PlayerId == room.LocalPlayerId);
                if (!seat.Occupant.IsReady || !NetworkClient.isConnected) return;
                if (!seat.IsUserReady || seat.ReadyConfigurationRevision != room.Configuration.ConfigurationRevision)
                    request = new SetRoomReadyRequest { IsUserReady = true };
            }
            if (request == null) return;
            request.CommandId = Guid.NewGuid().ToString("N"); request.RoomId = room.RoomId;
            request.RequestingPlayerId = room.LocalPlayerId; request.ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision;
            Write("room-request.json", new { request, room });
            roomBusy = true;
            try
            {
                var reply = request is ChangeRoomConfigurationRequest configuration ? await service.ChangeConfigurationAsync(configuration) : await service.SetReadyAsync((SetRoomReadyRequest)request);
                if (stopped) return;
                Write("room-reply.json", reply);
                if (!reply.Success && reply.ReasonCode != "configuration_stale" && reply.ReasonCode != "service_unavailable") Finish("room_request:" + reply.ReasonCode);
                if (reply.Success) { Write("room.json", reply.Snapshot); Capture("实际房间配置"); }
            }
            catch (Exception error) { Finish("room_exception:" + error.Message); }
            finally { roomBusy = false; }
        }

        private static GameCommand MainAction(GameState state, int player)
        {
            var influence = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)));
            foreach (var item in state.Map.Influences.Where(i => i.PlayerId == player))
            {
                string id = InfluenceIdentity.GetStableId(state, item);
                foreach (string target in influence.GetLegalMovementSlotIds(state, id))
                    if (influence.CanMoveAtomically(state, player, new[] { new InfluenceMoveRequest(id, target) }).IsValid)
                        return new GameCommand { Kind = GameCommandKind.DispatchInfluence, PlayerId = player, SourceId = id, TargetId = target };
            }
            return new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = player };
        }

        private static GameCommand Answer(InteractionView request, int player, GameStateView view)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = player };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.InteractionId;
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
            var total = request.CandidateIds.FirstOrDefault(c => c.StartsWith("total:", StringComparison.Ordinal));
            if (request.AnswerSchema == "bounded_amount")
            {
                var minimum = request.PromptParameters?.Properties.Find(value => value.Name == "minAmount")?.Value;
                if (minimum?.Kind != NormalizedValueKind.Integer) throw new InvalidOperationException("数量请求缺少最小值。");
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] =
                    minimum.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (request.Kind == "character.cleanup.settlement" && request.AllowDecline)
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "false";
            else if (request.AnswerSchema == "resource_allocation" && total != null)
                command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "Originium=" + total.Substring(6);
            else if (request.CandidateIds.Count > 0)
            {
                var skip = request.CandidateIds.FirstOrDefault(c => c == "choice.skip" || c == "decline");
                if (skip != null && request.MinSelections <= 1) command.OptionIds.Add(skip);
                else command.OptionIds.AddRange(request.CandidateIds.OrderByDescending(id => request.Kind.Contains("enterprise") &&
                    view.ItemZones.Items.Any(item => item.ItemId == id && (item.DefinitionId == "enterprise.rhine_lab" || item.DefinitionId == "enterprise.rhodes_island")))
                    .Take(Math.Max(1, request.MinSelections)));
            }
            else command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = "True";
            return command;
        }

        private bool SimulationCurrent => !stopped && simulationCoordinator != null &&
            ReferenceEquals(subscribed?.SimulationCoordinator, simulationCoordinator) &&
            GameLaunchContext.Instance != null && !GameLaunchContext.Instance.IsLeavingGameplay &&
            GameLaunchContext.Instance.LocalPlayerId == simulationPlayer && GameLaunchContext.Instance.GameId == boundGameId;

        private bool MatchesSimulation(string gameId, long epoch, int membership) => SimulationCurrent &&
            boundGameId == gameId && simulationEpoch == epoch && simulationMembership == membership;

        private void AttachSimulationTransport(MirrorCommandTransport transport)
        {
            if (stopped || ReferenceEquals(transport, null)) return;
            DetachTransport(); subscribed = transport;
            long generation = ++subscriptionGeneration;
            bool Current() => !stopped && subscriptionGeneration == generation && ReferenceEquals(subscribed, transport);
            readySubscription = value => { if (Current()) OnSimulationReady(value); };
            appliedSubscription = value => { if (Current()) OnSimulationApplied(value); };
            prefixSubscription = value => { if (Current()) OnSimulationPrefix(value); };
            permissionSubscription = value => { if (Current()) OnSimulationPermission(value); };
            rejectedSubscription = value => { if (Current()) OnSimulationRejected(value); };
            faultSubscription = value => { if (Current()) OnSimulationFault(value); };
            recoveryStartedSubscription = value => { if (Current()) OnSimulationRecoveryStarted(value); };
            recoveryCompletedSubscription = value => { if (Current()) OnSimulationRecoveryCompleted(value); };
            transport.DeterministicSimulationReady += readySubscription;
            transport.SimulationCommandApplied += appliedSubscription;
            transport.CommandValidationPrefixAdvanced += prefixSubscription;
            transport.OperationWindowPermissionChanged += permissionSubscription;
            transport.SimulationProposalRejected += rejectedSubscription;
            transport.SimulationProtocolFaulted += faultSubscription;
            transport.SimulationRecoveryStarted += recoveryStartedSubscription;
            transport.SimulationRecoveryCompleted += recoveryCompletedSubscription;
            // Ready 可能早于驱动订阅；仅接续同一真实已安装实例，不伪造初始事件。
            if (transport.SimulationCoordinator != null) OnSimulationReady(transport.SimulationCoordinator);
        }

        private void OnSimulationReady(DeterministicSimulationCoordinator coordinator)
        {
            if (stopped || coordinator == null || simulationCoordinator != null ||
                !ReferenceEquals(subscribed?.SimulationCoordinator, coordinator)) return;
            var controller = FindObjectOfType<MobileCityInteractionController>();
            var options = coordinator.SessionOptions;
            if (controller?.ValidationReadyFor(options) != true) return;
            var view = ReadValidationView(controller, options.LocalPlayerId);
            if (view?.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != options.LocalPlayerId ||
                view.GameId != options.GameId || !TryAcceptRunView(view)) return;
            BindSimulationCoordinator(coordinator);
            EnablePhaseTiming(controller);
            latest = view; initialSnapshots++;
            Write("initial-view-" + initialSnapshots + ".json", view);
            Append("initial-state-synchronizations.jsonl", new { utc = DateTime.UtcNow, initialSnapshot = initialSnapshots,
                Source = "DeterministicSimulationReady_or_current_installed_coordinator", GameId = boundGameId,
                SimulationEpoch = simulationEpoch, MembershipVersion = simulationMembership, ViewerPlayerId = simulationPlayer,
                view.ViewerRole, view.StateRevision, LocalAppliedSequence = localAppliedSequence, HostValidatedSequence = hostValidatedSequence });
            Append("simulation-ready.jsonl", new { utc = DateTime.UtcNow, options, localAppliedSequence, hostValidatedSequence });
        }

        private void BindSimulationCoordinator(DeterministicSimulationCoordinator coordinator)
        {
            var options = coordinator.SessionOptions;
            BindOrValidateRunGame(options.GameId);
            simulationCoordinator = coordinator; simulationEpoch = options.SimulationEpoch;
            simulationMembership = options.MembershipVersion; simulationPlayer = options.LocalPlayerId;
            localAppliedSequence = coordinator.LastAppliedSequence; hostValidatedSequence = coordinator.ValidatedThroughSequence;
            lastSignature = null;
        }

        private void OnSimulationApplied(SimulationCommandAppliedDto applied)
        {
            if (applied?.Command == null || applied.RuleResult == null || applied.ProtocolVersion != OperationCommandProtocol.Version ||
                !MatchesSimulation(applied.GameId, applied.SimulationEpoch, applied.MembershipVersion) || recoveringSimulation ||
                applied.GlobalCommandSequence <= localAppliedSequence ||
                (applied.RuleResult.Succeeded ? applied.GlobalCommandSequence > simulationCoordinator.LastAppliedSequence :
                    applied.GlobalCommandSequence != simulationCoordinator.LastAppliedSequence + 1)) return;
            var controller = FindObjectOfType<MobileCityInteractionController>();
            var view = ReadValidationView(controller, simulationPlayer);
            if (!TryAcceptRunView(view)) return;
            Append("simulation-applied.jsonl", new { utc = DateTime.UtcNow, applied,
                view = WholeGameDiagnosticView(view, WholeGameHealth && applied.RuleResult.Succeeded),
                LocalAppliedSequence = simulationCoordinator.LastAppliedSequence, HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence });
            if (!applied.RuleResult.Succeeded) { Finish("effect_fault:" + applied.RuleResult.Validation?.ErrorCode); return; }
            localAppliedSequence = applied.GlobalCommandSequence;
            bool ownPending = ExactPending(applied.Command);
            try
            {
                if (ownPending)
                {
                    if (routePolicy != null && !routePolicy.ApplyLocalResult(applied.GameId, applied.Command, true))
                        throw new InvalidOperationException("route_local_receipt_identity_mismatch");
                    pendingSimulationCommand = null;
                }
                if (GameLaunchContext.Instance.Mode == LaunchMode.Host && routePlan != null)
                {
                    if (WholeGameHealth) RecordCompletedCoverage(recoverySession?.State);
                    else
                    {
                        var after = controller.ValidationHostArchive()?.Snapshot;
                        RequireRunSnapshotPair(previousBusinessSnapshot, after);
                        Append("authoritative-transactions.jsonl", new { utc = DateTime.UtcNow, GameId = boundGameId,
                            CommandId = applied.Command.CommandId, Command = applied.Command, Before = previousBusinessSnapshot, After = after,
                            BeforeSource = "previous_observed_GameSession.CreateHostArchive", AfterSource = "local_applied_GameSession.CreateHostArchive" });
                        previousBusinessSnapshot = after;
                    }
                }
            }
            catch (Exception error) { Finish("route_confirmation_failed:" + error.Message); return; }
            latest = view; lastSignature = null; lastProgressTime = Time.realtimeSinceStartup;
            WritePhaseTimingSnapshot("applied", false);
        }

        private bool ExactPending(GameCommandDto command) => command != null && pendingSimulationCommand != null &&
            command.PlayerId == simulationPlayer && JsonConvert.SerializeObject(command) == JsonConvert.SerializeObject(pendingSimulationCommand);

        private void OnSimulationPrefix(CommandValidationPrefixDto prefix)
        {
            if (prefix == null || prefix.ProtocolVersion != OperationCommandProtocol.Version ||
                !MatchesSimulation(prefix.GameId, prefix.SimulationEpoch, prefix.MembershipVersion) || recoveringSimulation ||
                prefix.ValidatedThroughSequence <= hostValidatedSequence || prefix.ValidatedThroughSequence > simulationCoordinator.ValidatedThroughSequence) return;
            hostValidatedSequence = prefix.ValidatedThroughSequence;
            confirmations++;
            Append("simulation-validation-prefixes.jsonl", new { utc = DateTime.UtcNow, prefix,
                LocalAppliedSequence = simulationCoordinator.LastAppliedSequence, HostValidatedSequence = hostValidatedSequence });
        }

        private void OnSimulationPermission(SimulationWindowPermissionDto permission)
        {
            var window = permission?.Window;
            if (window == null || !MatchesSimulation(window.GameId, window.SimulationEpoch, window.MembershipVersion)) return;
            Append("simulation-permissions.jsonl", new { utc = DateTime.UtcNow, permission,
                LocalAppliedSequence = simulationCoordinator.LastAppliedSequence, HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence });
            // 许可不是提交成功；同一投影也可能因 Permit 获得合法操作资格。
            lastSignature = null;
        }

        private void OnSimulationRejected(SimulationProposalResultDto result)
        {
            if (result == null || result.ProtocolVersion != OperationCommandProtocol.Version ||
                !MatchesSimulation(result.GameId, result.SimulationEpoch, result.MembershipVersion) || recoveringSimulation ||
                !ExactPending(result.Proposal?.Command)) return;
            var command = result.Proposal.Command;
            Append("simulation-rejections.jsonl", new { utc = DateTime.UtcNow, result,
                LocalAppliedSequence = simulationCoordinator.LastAppliedSequence, HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence });
            bool expected = routePolicy?.Reject(command.CommandId, result.ErrorCode.ToString()) == true;
            bool retired = expected || routePolicy?.RetireUnappliedSubmission(result.GameId, command, result.ErrorCode.ToString()) == true;
            pendingSimulationCommand = null; lastSignature = null;
            if (!expected && result.ErrorCode != CommandErrorCode.StateRevisionMismatch && result.Status != OperationAdmissionStatus.Backpressure)
                Finish("command_rejected:" + result.ErrorCode + ":" + result.Reason);
            else if (routePolicy != null && !retired) Finish("route_rejection_identity_mismatch");
        }

        private void OnSimulationFault(SimulationProtocolFaultDto fault)
        {
            if (fault == null || !MatchesRecoveryFault(fault)) return;
            Append("simulation-faults.jsonl", new { utc = DateTime.UtcNow, fault,
                LocalAppliedSequence = simulationCoordinator.LastAppliedSequence, HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence });
            BindRecoveryRuntime(MirrorNetworkRuntime.Instance);
            if (subscribed.CanAwaitLocalSimulationConnectionRecovery(fault))
            {
                awaitingAcceptedSessionRecovery = true;
                lastSignature = null;
                Append("simulation-recovery-events.jsonl", new { utc = DateTime.UtcNow, Phase = "accepted_disconnect_wait",
                    fault.GameId, fault.SimulationEpoch, fault.MembershipVersion,
                    subscribed.IsAwaitingLocalSimulationConnectionRecovery,
                    PendingCommand = pendingSimulationCommand, LocalAppliedSequence = simulationCoordinator.LastAppliedSequence,
                    HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence, Completed = false });
                return;
            }
            Finish("simulation_fault:" + fault.FaultCode + ":" + fault.Diagnostic);
        }

        private bool MatchesRecoveryFault(SimulationProtocolFaultDto fault)
        {
            var context = GameLaunchContext.Instance;
            var current = subscribed?.SimulationCoordinator?.SessionOptions;
            return !stopped && context != null && !context.IsLeavingGameplay && context.GameId == boundGameId &&
                context.LocalPlayerId == simulationPlayer && current != null && current.GameId == boundGameId &&
                current.LocalPlayerId == simulationPlayer && current.MembershipVersion == simulationMembership &&
                (current.SimulationEpoch == simulationEpoch || recoveringSimulation && current.SimulationEpoch == recoveryTargetEpoch) &&
                fault.GameId == boundGameId && fault.MembershipVersion == simulationMembership &&
                (fault.SimulationEpoch == simulationEpoch || recoveringSimulation && fault.SimulationEpoch == recoveryTargetEpoch);
        }

        private void BindRecoveryRuntime(MirrorNetworkRuntime runtime)
        {
            if (ReferenceEquals(recoveryRuntime, runtime)) return;
            if (recoveryRuntime != null) recoveryRuntime.ClientDisconnected -= OnNetworkDisconnected;
            recoveryRuntime = runtime;
            if (recoveryRuntime != null) recoveryRuntime.ClientDisconnected += OnNetworkDisconnected;
        }

        private void OnNetworkDisconnected()
        {
            if (stopped || !awaitingAcceptedSessionRecovery || subscribed == null) return;
            if (subscribed.IsAwaitingLocalSimulationConnectionRecovery) return;
            string failure = subscribed.LocalSimulationConnectionRecoveryFailureCode;
            if (string.IsNullOrEmpty(failure)) return;
            Append("simulation-recovery-events.jsonl", new { utc = DateTime.UtcNow, Phase = "accepted_reconnect_failed", FailureCode = failure,
                GameId = boundGameId, SimulationEpoch = simulationEpoch, MembershipVersion = simulationMembership,
                PendingCommand = pendingSimulationCommand, Completed = false });
            Finish("simulation_fault:" + failure + ":原身份连接恢复已终结。");
        }

        private bool TryRecoverConfirmedCheckpoint(int player)
        {
            if (recoveringSimulation) return true;
            if (routePlan == null || GameLaunchContext.Instance.Mode != LaunchMode.Host || pendingSimulationCommand != null ||
                routePolicy.HasPendingCommand || !SimulationCurrent || simulationCoordinator.HasPendingLocalApplication) return false;
            var checkpoint = routePlan.RecoveryCheckpoints.FirstOrDefault(c => c.PlayerId == player &&
                !recoveredCheckpoints.Contains(c.CheckpointId) && c.Matches(latest));
            bool representative = routePlan.RepresentativeRecovery && !recoveredCheckpoints.Contains("representative-confirmed-prefix") &&
                latest.Round >= 1 && simulationCoordinator.LastAppliedSequence > 0 &&
                latest.Interactions.Any(r => r.Status == "open" && r.AnsweringPlayerId == player);
            if (checkpoint == null && !representative) return false;
            activeRecoveryCheckpoint = checkpoint?.CheckpointId ?? "representative-confirmed-prefix";
            Append("simulation-recovery-events.jsonl", new { utc = DateTime.UtcNow, Phase = "driver_request",
                CheckpointId = activeRecoveryCheckpoint, GameId = boundGameId, SimulationEpoch = simulationEpoch, MembershipVersion = simulationMembership,
                RuntimeMethod = "MirrorCommandTransport.RequestConfirmedRecovery", LocalAppliedSequence = simulationCoordinator.LastAppliedSequence,
                HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence });
            subscribed.RequestConfirmedRecovery();
            if (!recoveringSimulation) Finish("confirmed_recovery_request_not_started");
            return true;
        }

        private void OnSimulationRecoveryStarted(SimulationRecoveryNoticeDto notice)
        {
            if (notice == null || !MatchesSimulation(notice.GameId, notice.SourceSimulationEpoch, notice.MembershipVersion) ||
                notice.SimulationEpoch != simulationEpoch + 1) return;
            if (recoveringSimulation && recoveryTargetEpoch != notice.SimulationEpoch) return;
            recoveringSimulation = true; recoveryTargetEpoch = notice.SimulationEpoch;
            recoveryPendingCommand = pendingSimulationCommand;
            Append("simulation-recovery-events.jsonl", new { utc = DateTime.UtcNow, Phase = "started", notice,
                OriginalPendingCommand = recoveryPendingCommand, LocalAppliedSequence = simulationCoordinator.LastAppliedSequence,
                HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence });
        }

        private void OnSimulationRecoveryCompleted(DeterministicSimulationCoordinator coordinator)
        {
            if (stopped || !recoveringSimulation || coordinator == null || !ReferenceEquals(subscribed?.SimulationCoordinator, coordinator)) return;
            var options = coordinator.SessionOptions;
            if (options.GameId != boundGameId || options.SimulationEpoch != recoveryTargetEpoch ||
                options.MembershipVersion != simulationMembership || options.LocalPlayerId != simulationPlayer) return;
            if (coordinator.IsStopped || coordinator.RequiresRecovery || coordinator.IsRecoveringConfirmedPrefix)
            {
                Append("simulation-recovery-events.jsonl", new { utc = DateTime.UtcNow, Phase = "completed_but_not_resumable",
                    options, coordinator.IsStopped, coordinator.RequiresRecovery, coordinator.IsRecoveringConfirmedPrefix });
                Finish("simulation_fault:confirmed_recovery_not_resumable:正式恢复未允许继续。");
                return;
            }
            Append("simulation-recovery-events.jsonl", new { utc = DateTime.UtcNow, Phase = "completed", options,
                SourceFault = subscribed.ReadRecoverySourceFault(), SourceTail = subscribed.ReadRecoverySourceTail(),
                SourceProposal = subscribed.ReadRecoverySourceLocalProposal(), activeRecoveryCheckpoint,
                OriginalPendingCommand = recoveryPendingCommand, LocalAppliedSequence = coordinator.LastAppliedSequence,
                HostValidatedSequence = coordinator.ValidatedThroughSequence });
            if (recoveryPendingCommand != null)
            {
                if (routePolicy != null && !routePolicy.RetireUnappliedSubmission(boundGameId, recoveryPendingCommand, "confirmed_recovery_retired"))
                { Finish("recovery_pending_identity_mismatch"); return; }
                pendingSimulationCommand = recoveryPendingCommand = null;
            }
            BindSimulationCoordinator(coordinator); recoveringSimulation = false; awaitingAcceptedSessionRecovery = false;
            // 终局等待远端退出期间换代，先记录仍可执行的正式确认态；不依赖已退出的UI绑定。
            if (options.IsHost && finalHostExportStarted) RefreshFinalProtocolSummary(null);
            // 恢复换State根仍是同一GameSession及同一计数器；此通知可能先于共享绑定刷新。
            // 计时不再次要求UI ready，也不为诊断改变实际恢复完成顺序。
            if (phaseTimingRequested && phaseTimings != null)
                nextPhaseTimingSequence = (coordinator.LastAppliedSequence / 40 + 1) * 40;
            previousBusinessSnapshot = null; auditArchive = null; lastSavedRevision = -1;
            if (activeRecoveryCheckpoint != null) recoveredCheckpoints.Add(activeRecoveryCheckpoint);
            activeRecoveryCheckpoint = null; nextAction = Time.realtimeSinceStartup + 0.15f;
        }

        private void OnDisable()
        {
            DetachTransport();
            if (phaseTimings != null) phaseTimings.Enabled = false;
        }

        private void DetachTransport()
        {
            BindRecoveryRuntime(null);
            var transport = subscribed;
            subscribed = null;
            subscriptionGeneration++;
            if (ReferenceEquals(transport, null)) return;
            transport.DeterministicSimulationReady -= readySubscription;
            transport.SimulationCommandApplied -= appliedSubscription;
            transport.CommandValidationPrefixAdvanced -= prefixSubscription;
            transport.OperationWindowPermissionChanged -= permissionSubscription;
            transport.SimulationProposalRejected -= rejectedSubscription;
            transport.SimulationProtocolFaulted -= faultSubscription;
            transport.SimulationRecoveryStarted -= recoveryStartedSubscription;
            transport.SimulationRecoveryCompleted -= recoveryCompletedSubscription;
            transport.InitialStateViewApplied -= OnInitial;
            transport.ConfirmedStateViewApplied -= OnConfirmed;
            transport.CoveredCommandAcknowledged -= OnCoveredCommandAcknowledged;
            transport.CommandConfirmationObserved -= OnCommandConfirmationObserved;
            transport.CommandRejected -= OnRejected;
        }

        private bool StopValidation(string failure)
        {
            if (stopped) return false;
            stopped = true;
            terminationFailure = failure;
            DetachTransport();
            return true;
        }

        private void BindOrValidateRunGame(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) throw new InvalidOperationException("validation_game_identity_missing");
            if (boundGameId == null) boundGameId = gameId;
            else RequireRunGameId(gameId);
        }

        private void RequireRunGameId(string gameId)
        {
            if (string.IsNullOrEmpty(boundGameId) || string.IsNullOrEmpty(gameId))
                throw new InvalidOperationException("validation_game_identity_missing");
            if (!string.Equals(boundGameId, gameId, StringComparison.Ordinal))
                throw new InvalidOperationException("validation_game_changed");
        }

        private bool TryAcceptRunView(GameStateView view)
        {
            if (stopped || view == null || string.IsNullOrEmpty(view.GameId)) return false;
            try { BindOrValidateRunGame(view.GameId); return true; }
            catch (InvalidOperationException error)
            {
                Append("game-identity-rejections.jsonl", new { utc = DateTime.UtcNow,
                    BoundGameId = boundGameId, ReceivedGameId = view.GameId, view.StateRevision });
                Finish(error.Message);
                return false;
            }
        }

        private void RequireRunSnapshotPair(HostSnapshotDto before, HostSnapshotDto after)
        {
            if (before != null) RequireRunGameId(before.State?.GameId);
            RequireRunGameId(after?.State?.GameId);
        }

        internal static void RequireArchiveGameIdentity(string expectedGameId, string archiveGameId, string committedGameId)
        {
            if (string.IsNullOrEmpty(expectedGameId) || string.IsNullOrEmpty(archiveGameId) || string.IsNullOrEmpty(committedGameId))
                throw new InvalidOperationException("validation_game_identity_missing");
            if (!string.Equals(expectedGameId, archiveGameId, StringComparison.Ordinal) ||
                !string.Equals(expectedGameId, committedGameId, StringComparison.Ordinal))
                throw new InvalidOperationException("validation_game_changed");
        }

        private void OnCommandConfirmationObserved(LocalCommandConfirmationDiagnostic diagnostic)
        {
            if (stopped || diagnostic == null || string.IsNullOrEmpty(output)) return;
            // 只观察原接收事实；错局/错ID也原样保留，不能据此确认命令或绑定新局。
            Append("command-confirmation-diagnostics.jsonl", new { utc = DateTime.UtcNow,
                LocalPlayerId = GameLaunchContext.Instance?.LocalPlayerId,
                RunId = Directory.GetParent(output)?.Name, diagnostic });
        }

        private void OnInitial(InitialGameStateViewDto state)
        {
            if (!TryAcceptRunView(state?.View)) return;
            initialSnapshots++;
            Write("initial-view-" + initialSnapshots + ".json", state.View);
            Append("initial-state-synchronizations.jsonl", new { utc = DateTime.UtcNow, initialSnapshot = initialSnapshots,
                state.ProtocolVersion, state.NextConfirmedSequence, state.View.GameId, state.View.StateRevision,
                state.View.Round, state.View.Phase, state.View.ViewerRole, state.View.ViewerPlayerId });
            if (disconnected && !reconnected && initialSnapshots > snapshotsBeforeDisconnect && NetworkClient.isConnected)
            {
                Write("after-reconnect-view.json", state.View);
                var restored = state.View.Interactions.FirstOrDefault(r => r.InteractionId == interruptedRequest && r.Status == "open");
                if (restored == null || !restored.CandidateIds.SequenceEqual(interruptedCandidates) || interruptedContract != RequestContract(state.View, interruptedRequest)) Finish("reconnect_lost_pending_request");
                else { reconnected = true; lastSignature = null; }
            }
        }
        private bool IsAwaitingClientInitialState =>
            simulationCoordinator == null;

        private void OnConfirmed(ConfirmedGameStateViewDto state)
        {
            if (IsAwaitingClientInitialState || !TryAcceptRunView(state?.View)) return;
            // Host 广播回调提供权威投影；只记录重新投影后的本人可见视图。
            var view = state.View;
            if (view?.ViewerRole == GameStateViewerRole.Host)
            {
                var controller = FindObjectOfType<MobileCityInteractionController>();
                view = controller?.ValidationView(GameLaunchContext.Instance.LocalPlayerId);
                if (view != null && !TryAcceptRunView(view)) return;
                confirmations++;
                if (routePlan != null && controller != null)
                {
                    try
                    {
                        if (WholeGameHealth)
                        {
                            RecordCompletedCoverage(recoverySession?.State);
                        }
                        else
                        {
                        var after = CaptureHostArchiveStage("confirmed", "CreateHostArchive", null, null, controller.ValidationHostArchive)?.Snapshot;
                        if (after == null) throw new InvalidOperationException("缺少真实Host提交后快照。");
                        RequireRunSnapshotPair(previousBusinessSnapshot, after);
                        // 保存实际连续观察，不从Journal.StateAfter反造逐条提交前态。
                        Append("authoritative-transactions.jsonl", new { utc = DateTime.UtcNow,
                            GameId = after.State.GameId, ContentPackHash = after.ContentHash,
                            CommandId = state.Command?.CommandId, Command = state.Command,
                            Before = previousBusinessSnapshot, After = after,
                            BeforeSource = "previous_observed_GameSession.CreateHostArchive",
                            AfterSource = "confirmed_GameSession.CreateHostArchive" });
                        previousBusinessSnapshot = after;
                        if (auditArchive == null) auditArchive = CaptureHostArchiveStage("confirmed", "CreateHostArchive", null, null, controller.ValidationHostArchive);
                        else CaptureHostArchiveStage("confirmed", "AppendJournal", auditArchive, null,
                            () => { controller.ValidationAppendHostJournal(auditArchive, boundGameId); return true; });
                        }
                    }
                    catch (Exception error) { Finish("host_journal_capture_failed:" + error.Message); return; }
                }
            }
            else confirmations++;
            Append("confirmed.jsonl", new { utc = DateTime.UtcNow, state.Sequence, command = state.Command, view });
            try { routePolicy?.Confirm(state.Command?.CommandId); RecordStaleRefresh("真实确认", state.Command?.CommandId); }
            catch (Exception error) { Finish("route_confirmation_failed:" + error.Message); return; }
            lastSignature = null;
        }
        private void OnCoveredCommandAcknowledged(ConfirmedGameStateViewDto state)
        {
            if (stopped || IsAwaitingClientInitialState || state?.View == null || string.IsNullOrEmpty(state.Command?.CommandId)) return;
            if (!TryAcceptRunView(state.View)) return;
            var controller = FindObjectOfType<MobileCityInteractionController>();
            var currentView = GameLaunchContext.Instance == null ? latest :
                controller?.ValidationView(GameLaunchContext.Instance.LocalPlayerId) ?? latest;
            if (currentView != null && !TryAcceptRunView(currentView)) return;
            Append("covered-command-acknowledgments.jsonl", new { utc = DateTime.UtcNow,
                state.ProtocolVersion, state.Sequence, state.Command.CommandId, state.View.GameId,
                ConfirmedStateRevision = state.View.StateRevision, CurrentGameId = currentView?.GameId,
                CurrentStateRevision = currentView?.StateRevision, DriverObservedStateRevision = latest?.StateRevision,
                state.View.ViewerRole, state.View.ViewerPlayerId,
                PolicyHadPendingCommand = routePolicy?.HasPendingCommand, PolicyPendingStepId = routePolicy?.PendingStepId });
            // 运输层已核验真实pending确认被当前同步覆盖；仅消费命令确认，不重放旧投影。
            try { routePolicy?.Confirm(state.Command.CommandId); RecordStaleRefresh("已覆盖的真实确认", state.Command.CommandId); }
            catch (Exception error) { Finish("route_confirmation_failed:" + error.Message); return; }
            lastSignature = null;
        }
        private static string RequestContract(GameStateView view, string interactionId)
        {
            var request = view.Interactions.FirstOrDefault(r => r.InteractionId == interactionId);
            return JsonConvert.SerializeObject(new {
                request?.InteractionId, request?.OwnerEffectId, request?.Kind, request?.AnsweringPlayerId,
                request?.CandidateSetId, request?.CandidateSetVersion, request?.MinSelections, request?.MaxSelections, request?.AllowDecline, request?.AnswerSchema,
                options = view.CharacterSettlementOptions.Where(o => o.InteractionId == interactionId).OrderBy(o => o.RegistrationSequence)
                    .Select(o => new { o.OptionId, o.WindowId, o.LocalId, o.EntryKind, o.TargetEffectId, o.TargetUseId, o.RegistrationSequence, o.SettlementState, o.CanDeclineWholeEffect })
            });
        }
        private void OnRejected(RejectedGameCommandDto rejected)
        {
            if (stopped || IsAwaitingClientInitialState || rejected == null) return;
            var controller = FindObjectOfType<MobileCityInteractionController>();
            var rejectedView = controller?.ValidationView(GameLaunchContext.Instance.LocalPlayerId);
            if (!TryAcceptRunView(rejectedView)) return;
            if (!WholeGameHealth && routePlan != null && controller != null && GameLaunchContext.Instance.Mode == LaunchMode.Host)
            {
                try
                {
                    var after = controller.ValidationHostArchive()?.Snapshot;
                    RequireRunSnapshotPair(previousBusinessSnapshot, after);
                    Append("authoritative-rejections.jsonl", new { utc = DateTime.UtcNow, CommandId = rejected.Command?.CommandId,
                        Before = previousBusinessSnapshot, After = after, errorCodeName = rejected.ErrorCode.ToString(),
                        InputSource = "GameSession.CreateHostArchive" });
                    previousBusinessSnapshot = after;
                }
                catch (Exception error) { Finish("host_rejection_capture_failed:" + error.Message); return; }
            }
            Append("rejected.jsonl", new { utc = DateTime.UtcNow, rejected, errorCodeName = rejected.ErrorCode.ToString(), observedRevision = rejectedView?.StateRevision, view = rejectedView });
            // 外部观察继续记录；是否为计划内拒绝由真实原生轨迹与审计合同核对。
            if (observationOnly) return;
            if (!observationOnly && routePolicy?.Reject(rejected.Command?.CommandId, rejected.ErrorCode.ToString()) == true)
            { lastSignature = null; return; }
            try
            {
                if (routePolicy?.IsRetiredStaleInteractionRejection(rejected, rejectedView) == true)
                { RecordStaleRefresh("已处理旧命令的迟到拒绝", rejected.Command.CommandId, true); return; }
                if (routePolicy?.TryBeginStaleInteractionRefresh(rejected, rejectedView, out _) == true)
                { RecordStaleRefresh("准确过期拒绝，释放旧pending", rejected.Command.CommandId); lastSignature = null; return; }
            }
            catch (Exception error)
            { RecordStaleRefresh("拒绝刷新终止：" + error.Message, rejected.Command?.CommandId, true); Finish("route_refresh_failed:" + error.Message); return; }
            Finish("command_rejected:" + JsonConvert.SerializeObject(rejected));
        }
        private T CaptureHostArchiveStage<T>(string context, string stage, HostSessionArchiveDto archive, string archivePath, Func<T> operation)
        {
            return HostArchiveCaptureDiagnostics.Execute(() =>
            {
                if (stopped) throw new InvalidOperationException("validation_already_stopped");
                RequireRunGameId(boundGameId);
                if (latest != null) RequireRunGameId(latest.GameId);
                if (archive != null) RequireRunGameId(archive.Snapshot?.State?.GameId);
                var value = operation();
                if (value is HostSessionArchiveDto capturedArchive) RequireRunGameId(capturedArchive.Snapshot?.State?.GameId);
                if (value is HostSnapshotDto capturedSnapshot) RequireRunGameId(capturedSnapshot.State?.GameId);
                return value;
            }, error =>
            {
                if (stopped) return;
                var evidence = new
                {
                    utc = DateTime.UtcNow, context, stage, archivePath,
                    PartialPath = context == "final" && archivePath != null ? archivePath + ".partial" : null,
                    GameId = latest?.GameId, PlayerId = latest?.ViewerPlayerId,
                    StateRevision = latest?.StateRevision, Round = latest?.Round, Phase = latest?.Phase,
                    SnapshotRevision = archive?.Snapshot?.StateRevision,
                    SnapshotLastCommitSequence = archive?.Snapshot?.LastCommitSequence,
                    JournalEntries = archive?.Journal?.Entries?.Count,
                    LastJournalCommitSequence = archive?.Journal?.Entries?.LastOrDefault()?.CommitSequence,
                    ExceptionType = error.GetType().FullName, ExceptionText = error.ToString(),
                    InnerExceptionText = error.InnerException?.ToString(),
                    FailureViewPath = Path.Combine(output, "failure-view.json"),
                    LastCheckpointPath = Path.Combine(output, "host-checkpoint.json")
                };
                try { Write("host-archive-capture-failure.json", evidence); }
                catch (Exception evidenceError) { Debug.LogError("[NetworkGameValidation] 归档异常证据写入失败：" + evidenceError); }
                Debug.LogError("[NetworkGameValidation] Host归档失败 " + context + "/" + stage + "：" + error);
            });
        }
        private void RecordStaleRefresh(string stage, string commandId = null, bool force = false)
        {
            var evidence = routePolicy?.StaleInteractionRefreshEvidence;
            if (evidence == null) return;
            string serialized = JsonConvert.SerializeObject(evidence);
            if (!force && serialized == lastStaleRefreshEvidence) return;
            lastStaleRefreshEvidence = serialized;
            Append("stale-interaction-refresh.jsonl", new { utc = DateTime.UtcNow, stage, commandId, evidence });
        }
        private bool Capture(string name)
        {
            if (!captures.Add(name)) return false;
            ScreenCapture.CaptureScreenshot(Path.Combine(output, Safe(name) + ".png")); return true;
        }
        private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        private static string FileSha256(string path)
        {
            using (var digest = System.Security.Cryptography.SHA256.Create())
            using (var input = File.OpenRead(path)) return BitConverter.ToString(digest.ComputeHash(input)).Replace("-", "");
        }
        // 仅缩减新模拟整局的重复诊断载荷。正式决策、提交与隐私投影仍消费原GameStateView。
        // Players/ItemZones完整保留供原审计逐条核验；完成效果增量另由observed-coverage记录。
        internal static object WholeGameDiagnosticView(GameStateView view, bool compact)
        {
            if (!compact || view == null || view.IsPausedFault || view.Phase == GamePhase.FinalScoring) return view;
            return new { view.SchemaVersion, view.ViewerRole, view.ViewerPlayerId, view.GameId, view.ContentPackHash,
                view.RuleConfiguration, view.Phase, view.Round, view.MaxRounds, view.CurrentPlayerId, view.MapId,
                view.StateRevision, view.RuntimeStatus, view.ActiveMainNodeId, view.Players, view.ItemZones,
                view.FinalScoring, view.FaultCode, view.IsPausedFault };
        }
        private GameStateView ReadValidationView(MobileCityInteractionController controller, int player)
        {
            if (!WholeGameHealth) return controller?.ValidationView(player);
            long reuse = controller?.ValidationViewReuseCount ?? 0, fallback = controller?.ValidationViewFallbackCount ?? 0,
                rejected = controller?.ValidationViewRejectedCount ?? 0, legacy = controller?.ValidationViewLegacyCount ?? 0;
            long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return controller?.ValidationView(player); }
            finally
            {
                diagnosticViewCount++; diagnosticViewTicks += System.Diagnostics.Stopwatch.GetTimestamp() - timestamp;
                diagnosticViewReuseCount += (controller?.ValidationViewReuseCount ?? reuse) - reuse;
                diagnosticViewFallbackCount += (controller?.ValidationViewFallbackCount ?? fallback) - fallback;
                diagnosticViewRejectedCount += (controller?.ValidationViewRejectedCount ?? rejected) - rejected;
                diagnosticViewLegacyCount += (controller?.ValidationViewLegacyCount ?? legacy) - legacy;
            }
        }
        private void EnablePhaseTiming(MobileCityInteractionController controller)
        {
            if (!phaseTimingRequested || simulationCoordinator == null) return;
            var options = simulationCoordinator.SessionOptions;
            var counters = controller?.ValidationExecutionTimings(options);
            if (counters == null) throw new InvalidOperationException("validation_phase_timing_binding_unavailable");
            if (phaseTimings != null && !ReferenceEquals(phaseTimings, counters))
                throw new InvalidOperationException("validation_phase_timing_session_changed");
            phaseTimings = counters;
            phaseTimings.Enabled = true;
            nextPhaseTimingSequence = (simulationCoordinator.LastAppliedSequence / 40 + 1) * 40;
            var binding = new { utc = DateTime.UtcNow, options.GameId, options.LocalPlayerId,
                options.SimulationEpoch, options.MembershipVersion, EventDeckSeed = controller.ValidationEventDeckSeed(options),
                ExplicitOptIn = true, DiagnosticSampling = diagnosticSampleRequested, SameSeedPathComparison = false };
            if (!File.Exists(Path.Combine(output, "phase-timing-enabled.json"))) Write("phase-timing-enabled.json", binding);
            Append("phase-timing-bindings.jsonl", binding);
        }
        private GameCommand DecideValidationRoute(GameStateView view, int player, out string waiting)
        {
            if (!phaseTimingRequested) return routePolicy.Decide(view, player, out waiting);
            long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return routePolicy.Decide(view, player, out waiting); }
            finally { decisionCalls++; decisionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - timestamp; }
        }
        private void WritePhaseTimingSnapshot(string reason, bool force)
        {
            if (!phaseTimingRequested || phaseTimings == null || simulationCoordinator == null ||
                !force && simulationCoordinator.LastAppliedSequence < nextPhaseTimingSequence) return;
            var options = simulationCoordinator.SessionOptions;
            if (options.GameId != boundGameId || options.LocalPlayerId != simulationPlayer ||
                options.SimulationEpoch != simulationEpoch || options.MembershipVersion != simulationMembership) return;
            var snapshot = phaseTimings.Snapshot();
            File.AppendAllText(Path.Combine(output, "phase-timings.jsonl"), JsonConvert.SerializeObject(new {
                utc = DateTime.UtcNow, Reason = reason, options.GameId, ActualLocalPlayerId = options.LocalPlayerId,
                options.SimulationEpoch, options.MembershipVersion, G = simulationCoordinator.LastAppliedSequence,
                R = simulationCoordinator.ValidatedThroughSequence, ElapsedSeconds = Time.realtimeSinceStartup - started,
                StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency, Counters = snapshot, Cumulative = true,
                DecisionCalls = decisionCalls, DecisionTicks = decisionTicks,
                DecisionSeconds = decisionTicks / (double)System.Diagnostics.Stopwatch.Frequency,
                SnapshotPoint = reason == "applied" ? "applied_callback_before_current_pump_return" : "driver_exit" }) + "\n");
            nextPhaseTimingSequence = (simulationCoordinator.LastAppliedSequence / 40 + 1) * 40;
        }
        private void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonConvert.SerializeObject(value, Formatting.Indented));
        private void Append(string name, object value)
        {
            if (stopped) return;
            if (!WholeGameHealth || !SimulationCurrent)
            { File.AppendAllText(Path.Combine(output, name), JsonConvert.SerializeObject(value) + "\n"); return; }
            if (!diagnosticIo.TryGetValue(name, out var totals)) diagnosticIo.Add(name, totals = new DiagnosticIoTotals());
            long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            string json = JsonConvert.SerializeObject(value);
            totals.SerializeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - timestamp;
            timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            File.AppendAllText(Path.Combine(output, name), json + "\n");
            totals.AppendTicks += System.Diagnostics.Stopwatch.GetTimestamp() - timestamp;
            totals.Records++; totals.JsonCharacters += json.Length + 1;
        }
        private void WriteFinalHostArchive(MobileCityInteractionController controller)
        {
            if (stopped) return;
            try
            {
                if (WholeGameHealth) RecordCompletedCoverage(recoverySession?.State);
                string archivePath = Path.Combine(output, "final-host-archive.json");
                if (auditArchive == null) auditArchive = CaptureHostArchiveStage("final", "CreateHostArchive", null, archivePath, controller.ValidationHostArchive);
                else CaptureHostArchiveStage("final", "AppendJournal", auditArchive, archivePath,
                    () => { controller.ValidationAppendHostJournal(auditArchive, boundGameId); return true; });
                try
                {
                    HostArchiveCaptureDiagnostics.WriteArchive(auditArchive, archivePath,
                        (stage, operation) => CaptureHostArchiveStage("final", stage, auditArchive, archivePath,
                            () => { operation(); return true; }));
                }
                catch (Exception error)
                {
                    if (error.Data.Contains("PartialWriterCloseException"))
                    {
                        try { Write("host-archive-partial-close-failure.json", new { utc = DateTime.UtcNow,
                            archivePath, PartialPath = archivePath + ".partial", ExceptionText = error.Data["PartialWriterCloseException"] }); }
                        catch (Exception evidenceError) { Debug.LogError("[NetworkGameValidation] 半成品关闭异常证据写入失败：" + evidenceError); }
                    }
                    throw;
                }
            }
            catch (Exception error) { Finish("host_journal_capture_failed:" + error.Message); return; }
            RefreshFinalProtocolSummary(controller);
            // 已满足终局检查；证据写入耗时不能将真实成功翻成时间预算失败。
            Finish(null);
        }

        private void RefreshFinalProtocolSummary(MobileCityInteractionController controller)
        {
            // 远端退出等待期间可发生同局恢复；归档收尾从当前正式绑定读取终局代次。
            var options = simulationCoordinator.SessionOptions;
            if (!options.IsHost)
            {
                Write("protocol-summary.json", controller.ValidationProtocolSummary(simulationCoordinator));
                return;
            }
            // 已退出的房间/UI提交绑定不再代表最终Host会话；使用Bootstrapper绑定的同一实际会话。
            if (recoverySession == null || recoverySession.State.GameId != boundGameId || options.GameId != boundGameId ||
                recoverySession.State.ContentPackHash != options.ContentPackHash || options.LocalPlayerId != simulationPlayer ||
                options.SimulationEpoch != simulationEpoch || options.MembershipVersion != simulationMembership)
                throw new InvalidOperationException("final_protocol_host_session_binding_changed");
            var prefix = simulationCoordinator.ReadLocalValidationPrefix();
            var summary = new { options.GameId, options.SimulationEpoch, options.MembershipVersion, options.LocalPlayerId,
                LocalAppliedSequence = simulationCoordinator.LastAppliedSequence, HostValidatedSequence = simulationCoordinator.ValidatedThroughSequence,
                prefix.PrefixDigest, RuleDigestVersion = RuleStateDigestService.DigestVersion,
                RuleStateDigest = RuleStateDigestService.Compute(recoverySession.State), simulationCoordinator.HasPendingLocalApplication,
                simulationCoordinator.IsRecoveringConfirmedPrefix, simulationCoordinator.IsStopped, simulationCoordinator.RequiresRecovery };
            bool confirmed = !summary.HasPendingLocalApplication && !summary.IsRecoveringConfirmedPrefix && !summary.RequiresRecovery &&
                simulationCoordinator.LastFault == null && summary.LocalAppliedSequence == summary.HostValidatedSequence &&
                !string.IsNullOrEmpty(summary.PrefixDigest) && !string.IsNullOrEmpty(summary.RuleStateDigest) &&
                recoverySession.State.Phase == GamePhase.FinalScoring && recoverySession.State.FinalScoring?.IsResolved == true &&
                recoverySession.State.EffectRuntime.Status != EffectRuntimeStatus.PausedFault;
            if (!summary.IsStopped)
            {
                if (confirmed) lastCompletedHostProtocolSummary = summary;
                Write("protocol-summary.json", summary);
                return;
            }
            // 正常关闭的Stop不再代表关停前的终局确认态；真实停止事实仍单独保存。
            Write("protocol-shutdown-summary.json", summary);
            if (confirmed && lastCompletedHostProtocolSummary != null)
            {
                var captured = Newtonsoft.Json.Linq.JObject.FromObject(lastCompletedHostProtocolSummary);
                var current = Newtonsoft.Json.Linq.JObject.FromObject(summary);
                var identity = new[] { "GameId", "SimulationEpoch", "MembershipVersion", "LocalPlayerId", "LocalAppliedSequence",
                    "HostValidatedSequence", "PrefixDigest", "RuleDigestVersion", "RuleStateDigest" };
                if (identity.All(key => Newtonsoft.Json.Linq.JToken.DeepEquals(captured[key], current[key])))
                {
                    Write("protocol-summary.json", lastCompletedHostProtocolSummary);
                    return;
                }
            }
            // 缺少实际关停前确认原件、换代/状态分歧或真实故障时，仍输出当前异常事实供验收拒绝。
            Write("protocol-summary.json", summary);
        }

        private void Finish(string failure)
        {
            if (stopped) return;
            if (failure == null && (awaitingAcceptedSessionRecovery || recoveringSimulation)) return;
            if (failure == null && (rounds.Count != 8 || Argument("--yc-network-validation-reconnect=") == "true" && !reconnected)) failure = "incomplete_evidence";
            if (failure == null && !observationOnly && routePlan != null && routePlan.Steps.Any(s => s.PlayerId == latest.ViewerPlayerId && !routePolicy.CompletedSteps.Contains(s.StepId) && !routePolicy.SkippedSteps.ContainsKey(s.StepId))) failure = "route_steps_incomplete";
            if (failure == null && !observationOnly && routePlan != null && routePlan.RecoveryCheckpoints.Any(c => !c.OptionalIfUnavailable && c.PlayerId == latest.ViewerPlayerId && !recoveredCheckpoints.Contains(c.CheckpointId))) failure = "host_recovery_checkpoints_incomplete";
            if (failure == null && routePlan?.RepresentativeRecovery == true && simulationPlayer == 1 && recoveredCheckpoints.Count == 0)
                failure = "confirmed_recovery_not_run";
            if (!StopValidation(failure)) return;
            WritePhaseTimingSnapshot("exit", true);
            if (WholeGameHealth) Write("diagnostic-timings.json", new { utc = DateTime.UtcNow, GameId = boundGameId,
                LocalPlayerId = simulationPlayer, ElapsedSeconds = Time.realtimeSinceStartup - started,
                ViewCalls = diagnosticViewCount, ViewSeconds = diagnosticViewTicks / (double)System.Diagnostics.Stopwatch.Frequency,
                DecisionCalls = decisionCalls, DecisionTicks = decisionTicks,
                DecisionSeconds = decisionTicks / (double)System.Diagnostics.Stopwatch.Frequency,
                ViewReuseCount = diagnosticViewReuseCount, ViewFallbackCount = diagnosticViewFallbackCount,
                ViewRejectedCount = diagnosticViewRejectedCount, ViewLegacyCount = diagnosticViewLegacyCount,
                Files = diagnosticIo.Select(pair => new { Name = pair.Key, pair.Value.Records, pair.Value.JsonCharacters,
                    SerializeSeconds = pair.Value.SerializeTicks / (double)System.Diagnostics.Stopwatch.Frequency,
                    AppendSeconds = pair.Value.AppendTicks / (double)System.Diagnostics.Stopwatch.Frequency }),
                Scope = "只累计Driver本人投影读取及Append；不代表UI、规则执行或协议摘要的全部耗时" });
            Write("result.json", new { success = failure == null, failure, enterprise, inputMode = observationOnly ? "external" : "internal", routeName = routePlan?.RouteName,
                ProtocolMode = "deterministic-simulation", GameId = boundGameId, SimulationEpoch = simulationEpoch,
                MembershipVersion = simulationMembership, LocalPlayerId = simulationPlayer,
                LocalAppliedSequence = localAppliedSequence, HostValidatedSequence = hostValidatedSequence,
                HasUnresolvedLocalCommand = pendingSimulationCommand != null, RecoveryInProgress = recoveringSimulation,
                DiagnosticSampling = diagnosticSampleRequested,
                EvaluationKind = diagnosticSampleRequested ? "phase-timing-diagnostic" : "whole-game-health",
                GameplayRunPassed = WholeGameHealth ? (bool?)(!diagnosticSampleRequested && failure == null) : null,
                RunOutcome = WholeGameHealth ? ClassifyRunOutcome(failure) : null,
                EffectChainHealthy = WholeGameHealth ? (bool?)(failure == null && !diagnosticSampleRequested ? true : failure?.StartsWith("effect_fault:", StringComparison.Ordinal) == true ? false : (bool?)null) : null,
                completedSteps = routePolicy?.CompletedSteps, skippedSteps = routePolicy?.SkippedSteps, recoveredCheckpoints = recoveredCheckpoints.OrderBy(id => id), commands, initialSnapshots, snapshotsBeforeDisconnect, confirmations, disconnected, reconnected, interruptedRequest, rounds = rounds.OrderBy(r => r), latest?.ContentPackHash, latest?.RuleConfiguration, elapsed = Time.realtimeSinceStartup - started, processId = System.Diagnostics.Process.GetCurrentProcess().Id });
            if (failure != null) { Write("failure-view.json", latest); Capture("失败现场"); Debug.LogWarning("[NetworkGameValidation] " + failure); }
            if (!observationOnly) StartCoroutine(QuitAfterCapture(failure == null ? 0 : 1));
        }
        private void RecordCompletedCoverage(GameState state)
        {
            if (state == null || GameLaunchContext.Instance.Mode != LaunchMode.Host) throw new InvalidOperationException("缺少同一正式Host会话的执行事实。");
            RequireRunGameId(state.GameId);
            var completed = state.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Completed && recordedCompletedEffects.Add(n.EffectId))
                .Select(n => new { n.EffectId, n.ParentEffectId, n.EffectTypeId, n.SourceId, n.PlayerId,
                    n.ContinuationContentId, n.ContinuationAbilityId, n.NormalizedArguments, n.NormalizedResult, n.ContinuationData }).ToArray();
            var modes = state.CharacterUses.SelectMany(use => use.CompletedModes.Where(mode => recordedCharacterModes.Add(use.UseId + ":" + mode))
                .Select(mode => new { use.UseId, use.CardInstanceId, use.ExecutingPlayerId,
                    DefinitionId = state.ItemZones.Items.FirstOrDefault(item => item.ItemId == use.CardInstanceId)?.DefinitionId, Mode = mode })).ToArray();
            if (completed.Length > 0 || modes.Length > 0) Append("observed-coverage.jsonl", new { utc = DateTime.UtcNow, state.GameId,
                state.ContentPackHash, state.Round, state.Phase, state.EffectRuntime.StateRevision, CompletedEffects = completed, CharacterModes = modes });
        }
        private static string ClassifyRunOutcome(string failure)
        {
            if (failure == null) return "completed";
            if (failure.StartsWith("effect_fault:", StringComparison.Ordinal) || failure == "opponent_private_cards_visible" || failure == "hidden_zone_identities_visible" ||
                failure.StartsWith("simulation_fault:", StringComparison.Ordinal) ||
                failure == "frozen_configuration_changed" || failure == "not_owner_player_projection") return "game_failure";
            if (failure == "validation_timeout" || failure == "incomplete_evidence" || failure == "route_command_budget_exhausted" || failure == "no_authoritative_progress") return "incomplete";
            return "driver_failure";
        }
        private IEnumerator QuitAfterCapture(int code)
        {
            yield return new WaitForSecondsRealtime(2f);
            // 终局 Host 留到成员收到投影并退出，避免截断仍在可靠通道中的最后一次同步。
            if (code == 0 && GameLaunchContext.Instance?.Mode == LaunchMode.Host)
            {
                float deadline = Time.realtimeSinceStartup + 30f;
                while (NetworkServer.connections.Values.Any(c => !(c is LocalConnectionToClient)) && Time.realtimeSinceStartup < deadline)
                    yield return new WaitForSecondsRealtime(0.2f);
            }
            UnityEngine.Application.Quit(code);
        }
    }

    internal static class HostArchiveCaptureDiagnostics
    {
        internal static IEnumerator AfterRemotePlayersExit(Func<bool> hasRemotePlayers, Func<bool> isStopped, Action writeArchive)
        {
            while (!isStopped() && hasRemotePlayers()) yield return null;
            if (!isStopped()) writeArchive();
        }

        internal static void WriteArchive(HostSessionArchiveDto archive, string path, Action<string, Action> executeStage,
            Func<string, TextWriter> writerFactory = null)
        {
            string partialPath = path + ".partial";
            TextWriter writer = null;
            Exception operationError = null;
            try
            {
                executeStage("CreateWriter", () => writer = writerFactory != null ? writerFactory(partialPath) :
                    new StreamWriter(new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                        new System.Text.UTF8Encoding(false), 65536));
                executeStage("SerializeArchive", () => HostRecoveryService.SerializeArchive(archive, writer));
                executeStage("FlushWriter", () => writer.Flush());
                var closingWriter = writer; writer = null;
                executeStage("CloseWriter", () => closingWriter.Dispose());
                // 仅全部写入、flush和关闭成功才发布完成路径；失败的partial原件保留。
                executeStage("PublishCompleteArchive", () => File.Move(partialPath, path));
            }
            catch (Exception error) { operationError = error; throw; }
            finally
            {
                if (writer != null)
                {
                    try { writer.Dispose(); }
                    catch (Exception closeError)
                    {
                        if (operationError == null) throw;
                        operationError.Data["PartialWriterCloseException"] = closeError.ToString();
                    }
                }
            }
        }
        // 只观察原异常并原样重抛；不替换正式档案或改变收证成功条件。
        internal static T Execute<T>(Func<T> operation, Action<Exception> onFailure)
        {
            try { return operation(); }
            catch (Exception error) { onFailure(error); throw; }
        }
    }

    public sealed partial class MobileCityInteractionController
    {
        internal long ValidationViewReuseCount, ValidationViewFallbackCount, ValidationViewRejectedCount, ValidationViewLegacyCount;
        internal GameStateView ValidationView(int player)
        {
            if (commandSubmission?.UsesDeterministicSimulation != true)
            { ValidationViewLegacyCount++; return session?.ProjectFor(GameStateViewer.Player(player)); }
            var coordinator = MirrorCommandTransport.Instance?.SimulationCoordinator;
            var options = coordinator?.SessionOptions;
            var context = GameLaunchContext.Instance;
            if (!isActiveAndEnabled || !ValidationReadyFor(options) || player != localPlayerId ||
                context == null || context.IsLeavingGameplay || context.GameId != options.GameId ||
                !GameSessionBootstrapper.IsNetworkLaunch(context) ||
                (context.Mode == LaunchMode.Host) != options.IsHost ||
                session.State.ContentPackHash != options.ContentPackHash || coordinator.IsRecoveringConfirmedPrefix ||
                coordinator.IsStopped || coordinator.RequiresRecovery)
            { ValidationViewRejectedCount++; return null; }
            // 始终消费当前会话的现有View；没有按revision缓存旧对象，恢复/换根由共享绑定维护。
            var view = session.View;
            if (view != null && (view.ViewerRole != GameStateViewerRole.Player || view.ViewerPlayerId != player ||
                view.GameId != options.GameId || view.ContentPackHash != options.ContentPackHash))
            { ValidationViewRejectedCount++; return null; }
            if (view != null && view.StateRevision == (session.State.EffectRuntime?.StateRevision ?? 0))
            { ValidationViewReuseCount++; return view; }
            // 合法本人在共享刷新前仍须收到准确Applied；此临时投影不写回session.View。
            ValidationViewFallbackCount++;
            return session.ProjectFor(GameStateViewer.Player(player));
        }
        internal bool ValidationCanSubmit => CanSubmitGameplayCommand && string.IsNullOrEmpty(pendingEffectCommandId) &&
            commandSubmission?.CanSubmitForLocalPlayer == true;
        internal bool ValidationReadyFor(DeterministicSimulationSessionOptions options) => options != null && session != null &&
            session.State.GameId == options.GameId && localPlayerId == options.LocalPlayerId &&
            GameLaunchContext.Instance?.LocalPlayerId == options.LocalPlayerId &&
            commandSubmission?.UsesDeterministicSimulation == true && commandSubmission.IsSimulationReady;
        internal ExecutionTimingCounters ValidationExecutionTimings(DeterministicSimulationSessionOptions options) =>
            ValidationReadyFor(options) && session.State.ContentPackHash == options.ContentPackHash ? session.ExecutionTimings : null;
        internal int? ValidationEventDeckSeed(DeterministicSimulationSessionOptions options) =>
            ValidationReadyFor(options) ? session.State.EventDeckSeed : (int?)null;
        internal object ValidationProtocolSummary(DeterministicSimulationCoordinator coordinator)
        {
            var options = coordinator.SessionOptions;
            if (!ValidationReadyFor(options)) throw new InvalidOperationException("validation_protocol_binding_changed");
            var prefix = coordinator.ReadLocalValidationPrefix();
            return new { options.GameId, options.SimulationEpoch, options.MembershipVersion, options.LocalPlayerId,
                LocalAppliedSequence = coordinator.LastAppliedSequence, HostValidatedSequence = coordinator.ValidatedThroughSequence,
                prefix.PrefixDigest, RuleDigestVersion = RuleStateDigestService.DigestVersion,
                RuleStateDigest = RuleStateDigestService.Compute(session.State),
                coordinator.HasPendingLocalApplication, coordinator.IsRecoveringConfirmedPrefix,
                coordinator.IsStopped, coordinator.RequiresRecovery };
        }
        internal HostSessionArchiveDto ValidationHostArchive() => GameLaunchContext.Instance?.Mode == LaunchMode.Host ? session?.CreateHostArchive() : null;
        internal void ValidationAppendHostJournal(HostSessionArchiveDto archive, string expectedGameId)
        {
            if (GameLaunchContext.Instance?.Mode != LaunchMode.Host || session == null) throw new InvalidOperationException("只有实际Host会话能记录权威提交。");
            NetworkGameValidationDriver.RequireArchiveGameIdentity(expectedGameId, archive?.Snapshot?.State?.GameId, session.State.GameId);
            HostRecoveryService.AppendJournal(archive, session.State);
        }
        internal HostRecoveryResult ValidationRecoverHost(HostSessionArchiveDto archive)
        {
            if (GameLaunchContext.Instance?.Mode != LaunchMode.Host || session == null) throw new InvalidOperationException("只有实际Host会话能执行恢复。");
            return NetworkGameValidationDriver.RecoverBoundHostSession(session, archive);
        }
        internal void SubmitValidationCommand(GameCommand command) => SubmitPendingEffectCommand(command);
    }
}
#endif
