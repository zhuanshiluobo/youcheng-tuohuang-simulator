using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
#if NETWORK_VALIDATION_LIFECYCLE_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    // 离线检查托管边界；独立原生用例通过临时对象触发生命周期，不替代网络现场。
    public sealed class NetworkGameValidationLifecycleTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private object originalContext;
        private static Type Target(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name)).First(type => type != null);
        private static object ManagedComponent(Type type)
        {
#if NETWORK_VALIDATION_LIFECYCLE_OFFLINE
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
#else
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
#endif
        }
        private static object Driver() => ManagedComponent(Target("YC.Presentation.NetworkGameValidationDriver"));
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Members).SetValue(target, value);
        private static object Get(object target, string name) => target.GetType().GetField(name, Members).GetValue(target);
        private static object Call(object target, string name, params object[] arguments)
        {
            try { return target.GetType().GetMethods(Members).Single(m => m.Name == name &&
                m.GetParameters().Length == arguments.Length).Invoke(target, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void Fails(Action action, string reason)
        {
            Exception failure = null;
            try { action(); }
            catch (Exception error) { failure = error; }
            Assert.That(failure?.GetType() == typeof(InvalidOperationException), Is.True);
            Assert.That(failure.Message, Is.EqualTo(reason));
        }
        private static string EvidenceDirectory()
        {
            string root = Environment.GetEnvironmentVariable("NMC_VALIDATION_LIFECYCLE_EVIDENCE");
#if NETWORK_VALIDATION_LIFECYCLE_OFFLINE
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("离线检查必须指定证据目录。");
#else
            if (string.IsNullOrEmpty(root)) root = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath,
                "../Logs/ExpansionValidation/ProductionGameRoutes/Preparation/ArchiveLifecycleClosure/生命周期检查原件"));
#endif
            string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
        private static HostSnapshotDto Snapshot(string gameId, int revision) => new HostSnapshotDto
        { StateRevision = revision, State = new GameState { GameId = gameId } };
        private static object Context(int player)
        {
            var type = Target("YC.Presentation.GameLaunchContext");
            object context = ManagedComponent(type);
            Set(context, "LocalPlayerId", player);
            Set(context, "Mode", Enum.Parse(type.GetField("Mode", Members).FieldType, "Host"));
            type.GetField("<Instance>k__BackingField", Statics).SetValue(null, context);
            return context;
        }
        private static object Diagnostic(string branch, string gameId, string receivedGameId, int? sequence,
            int? nextSequence, bool? synchronized, bool? matched, CommandResult result)
        {
            var type = Target("YC.Infrastructure.Multiplayer.LocalCommandConfirmationDiagnostic");
            return type.GetConstructors(Members).Single().Invoke(new object[]
            { branch, gameId, receivedGameId, "真实命令", "真实等待", sequence, nextSequence, synchronized, matched, result });
        }

        [SetUp]
        public void PreserveContext()
        {
            var field = Target("YC.Presentation.GameLaunchContext").GetField("<Instance>k__BackingField", Statics);
            originalContext = field.GetValue(null);
            field.SetValue(null, null);
        }
        [TearDown]
        public void RestoreContext() => Target("YC.Presentation.GameLaunchContext")
            .GetField("<Instance>k__BackingField", Statics).SetValue(null, originalContext);

        [Test]
        public void StoppedCallbacksPreserveFilesCountersAndFirstFailure()
        {
            object driver = Driver();
            string output = EvidenceDirectory();
            Set(driver, "output", output); Set(driver, "confirmations", 9); Set(driver, "initialSnapshots", 2);
            Set(driver, "lastSignature", "原签名");
            var latest = new GameStateView { GameId = "旧局", StateRevision = 108 };
            Set(driver, "latest", latest);
            File.WriteAllText(Path.Combine(output, "result.json"), "首次失败原件");
            File.WriteAllText(Path.Combine(output, "authoritative-transactions.jsonl"), "旧局原件\n");
            Assert.That(Call(driver, "StopValidation", "frozen_configuration_changed"), Is.True);
            var view = new GameStateView { GameId = "新局", StateRevision = 14 };
            Call(driver, "OnInitial", new InitialGameStateViewDto { View = view });
            Call(driver, "OnConfirmed", new ConfirmedGameStateViewDto { View = view });
            Call(driver, "OnCoveredCommandAcknowledged", new ConfirmedGameStateViewDto { View = view });
            Call(driver, "OnRejected", new RejectedGameCommandDto());
            Call(driver, "OnSimulationReady", new object[] { null });
            Call(driver, "OnSimulationApplied", new SimulationCommandAppliedDto());
            Call(driver, "OnSimulationPrefix", new CommandValidationPrefixDto());
            Call(driver, "OnSimulationPermission", new SimulationWindowPermissionDto());
            Call(driver, "OnSimulationRejected", new SimulationProposalResultDto());
            Call(driver, "OnSimulationFault", new SimulationProtocolFaultDto());
            Call(driver, "OnSimulationRecoveryStarted", new SimulationRecoveryNoticeDto());
            Call(driver, "OnSimulationRecoveryCompleted", new object[] { null });
            Call(driver, "OnCommandConfirmationObserved", Diagnostic("shutdown_pending_cleared", "旧局", null, null, null, null, null, null));
#if !NETWORK_VALIDATION_LIFECYCLE_OFFLINE
            Call(driver, "WriteFinalHostArchive", new object[] { null });
#endif
            Call(driver, "Append", "authoritative-transactions.jsonl", new { GameId = "新局" });
#if !NETWORK_VALIDATION_LIFECYCLE_OFFLINE
            Call(driver, "Finish", "host_journal_capture_failed:launch_player_order_changed");
#else
            Assert.That(Call(driver, "StopValidation", "host_journal_capture_failed:launch_player_order_changed"), Is.False);
#endif
            Assert.That(Get(driver, "confirmations"), Is.EqualTo(9));
            Assert.That(Get(driver, "initialSnapshots"), Is.EqualTo(2));
            Assert.That(Get(driver, "lastSignature"), Is.EqualTo("原签名"));
            Assert.That(ReferenceEquals(Get(driver, "latest"), latest), Is.True);
            Assert.That(Get(driver, "terminationFailure"), Is.EqualTo("frozen_configuration_changed"));
            Assert.That(File.ReadAllText(Path.Combine(output, "result.json")), Is.EqualTo("首次失败原件"));
            Assert.That(File.ReadAllText(Path.Combine(output, "authoritative-transactions.jsonl")), Is.EqualTo("旧局原件\n"));
            Assert.That(Directory.GetFiles(output).Length, Is.EqualTo(2));
        }

        [Test]
        public void FirstValidGameIdentityCannotBeRebound()
        {
            object driver = Driver();
            Fails(() => Call(driver, "BindOrValidateRunGame", ""), "validation_game_identity_missing");
            Assert.That(Get(driver, "boundGameId"), Is.Null);
            Call(driver, "BindOrValidateRunGame", "真实首局");
            Call(driver, "BindOrValidateRunGame", "真实首局");
            Fails(() => Call(driver, "BindOrValidateRunGame", "另一局"), "validation_game_changed");
            Fails(() => Call(driver, "BindOrValidateRunGame", "真实首局 "), "validation_game_changed");
            Assert.That(Get(driver, "boundGameId"), Is.EqualTo("真实首局"));
        }

        [Test]
        public void FirstBoundConfirmationCanCaptureBeforeUpdateHasAView()
        {
            object driver = Driver(); Call(driver, "BindOrValidateRunGame", "首局");
            var archive = new HostSessionArchiveDto { Snapshot = Snapshot("首局", 14) };
            int calls = 0;
            var capture = driver.GetType().GetMethod("CaptureHostArchiveStage", Members).MakeGenericMethod(typeof(HostSessionArchiveDto));
            var actual = capture.Invoke(driver, new object[] { "confirmed", "CreateHostArchive", null, null,
                new Func<HostSessionArchiveDto>(() => { calls++; return archive; }) });
            Assert.That(ReferenceEquals(actual, archive), Is.True);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(Get(driver, "latest"), Is.Null);
            Assert.That(Get(driver, "boundGameId"), Is.EqualTo("首局"));
        }

        [Test]
        public void SnapshotPairsRejectMixedGamesAndMissingAfter()
        {
            object driver = Driver(); Call(driver, "BindOrValidateRunGame", "旧局");
            var before = Snapshot("旧局", 108); var after = Snapshot("旧局", 109);
            string original = JsonConvert.SerializeObject(before);
            Call(driver, "RequireRunSnapshotPair", before, after);
            Call(driver, "RequireRunSnapshotPair", null, after);
            Fails(() => Call(driver, "RequireRunSnapshotPair", before, Snapshot("新局", 14)), "validation_game_changed");
            Fails(() => Call(driver, "RequireRunSnapshotPair", Snapshot("新局", 14), after), "validation_game_changed");
            Fails(() => Call(driver, "RequireRunSnapshotPair", before, null), "validation_game_identity_missing");
            Assert.That(JsonConvert.SerializeObject(before), Is.EqualTo(original));
            Assert.That(Get(driver, "boundGameId"), Is.EqualTo("旧局"));
        }

        [Test]
        public void JournalAppendGuardRejectsForeignActualSessionBeforeMutation()
        {
            Context(1);
            var session = new GameSession(new GameState { GameId = "新局" });
            var archive = new HostSessionArchiveDto { Snapshot = Snapshot("旧局", 108) };
            string original = JsonConvert.SerializeObject(archive);
            var guard = Target("YC.Presentation.NetworkGameValidationDriver").GetMethod("RequireArchiveGameIdentity", Statics);
#if NETWORK_VALIDATION_LIFECYCLE_OFFLINE
            // 控制器静态Profiler需Unity；离线直接执行实际追加入口调用的同一身份守卫。
            Fails(() =>
            {
                try { guard.Invoke(null, new object[] { "旧局", archive.Snapshot.State.GameId, session.State.GameId }); }
                catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            }, "validation_game_changed");
#else
            var controller = ManagedComponent(Target("YC.Presentation.MobileCityInteractionController"));
            Set(controller, "session", session);
            Fails(() => Call(controller, "ValidationAppendHostJournal", archive, "旧局"), "validation_game_changed");
#endif
            Assert.That(JsonConvert.SerializeObject(archive), Is.EqualTo(original));
            Assert.That(session.State.GameId, Is.EqualTo("新局"));
            guard.Invoke(null, new object[] { "同局", "同局", "同局" });
        }

        [Test]
        public void StopAndDisableDetachAllSimulationEventsIdempotently()
        {
            object driver = Driver(); var transport = ManagedComponent(Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
            var bindings = new Dictionary<string, string>
            {
                { "DeterministicSimulationReady", "OnSimulationReady" }, { "SimulationCommandApplied", "OnSimulationApplied" },
                { "CommandValidationPrefixAdvanced", "OnSimulationPrefix" }, { "OperationWindowPermissionChanged", "OnSimulationPermission" },
                { "SimulationProposalRejected", "OnSimulationRejected" }, { "SimulationProtocolFaulted", "OnSimulationFault" },
                { "SimulationRecoveryStarted", "OnSimulationRecoveryStarted" }, { "SimulationRecoveryCompleted", "OnSimulationRecoveryCompleted" }
            };
            foreach (var binding in bindings)
            {
                var eventInfo = transport.GetType().GetEvent(binding.Key);
                var handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, driver, driver.GetType().GetMethod(binding.Value, Members));
                eventInfo.AddEventHandler(transport, handler);
                string subscription = new Dictionary<string, string> { { "OnSimulationReady", "readySubscription" },
                    { "OnSimulationApplied", "appliedSubscription" }, { "OnSimulationPrefix", "prefixSubscription" },
                    { "OnSimulationPermission", "permissionSubscription" }, { "OnSimulationRejected", "rejectedSubscription" },
                    { "OnSimulationFault", "faultSubscription" }, { "OnSimulationRecoveryStarted", "recoveryStartedSubscription" },
                    { "OnSimulationRecoveryCompleted", "recoveryCompletedSubscription" } }[binding.Value];
                Set(driver, subscription, handler);
            }
            Set(driver, "subscribed", transport);
            Assert.That(Call(driver, "StopValidation", "首次原因"), Is.True);
            Assert.That(Call(driver, "StopValidation", "第二原因"), Is.False);
            Call(driver, "OnDisable"); Call(driver, "OnDisable");
            Assert.That(Get(driver, "subscribed"), Is.Null);
            Assert.That(Get(driver, "terminationFailure"), Is.EqualTo("首次原因"));
            foreach (string eventName in bindings.Keys) Assert.That(Get(transport, eventName), Is.Null, eventName);
        }

        [Test]
        public void ConfirmationDiagnosticsPreserveAllScalarsWithoutBusinessEffects()
        {
            object driver = Driver(); string output = Path.Combine(EvidenceDirectory(), "真实运行", "player-2");
            Directory.CreateDirectory(output); Set(driver, "output", output); Set(driver, "boundGameId", "旧局");
            Set(driver, "confirmations", 9); Set(driver, "commands", 3); Set(driver, "initialSnapshots", 2);
            Set(driver, "lastSignature", "等待签名"); var latest = new GameStateView { GameId = "旧局", StateRevision = 108 };
            Set(driver, "latest", latest);
            object policy = ManagedComponent(Target("YC.Presentation.Validation.ProductionGameRoutePolicy"));
            Set(policy, "pendingCommandId", "真实等待"); Set(policy, "pendingStepId", "真实步骤"); Set(driver, "routePolicy", policy);
            var diagnostics = new[]
            {
                Diagnostic("accepted_received", "旧局", "另一局", 298, 299, null, true, null),
                Diagnostic("accepted_apply_result", "旧局", "旧局", 298, 299, true, true,
                    CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.UnknownCommand, "受测失败"))),
                Diagnostic("initial_apply_result", "旧局", "旧局", null, 300, false, null, CommandResult.SuccessResult(null, "")),
                Diagnostic("connection_pending_cleared", "旧局", null, null, null, null, null, null)
            };
            Call(driver, "OnCommandConfirmationObserved", diagnostics[0]);
            Context(2);
            foreach (object diagnostic in diagnostics.Skip(1)) Call(driver, "OnCommandConfirmationObserved", diagnostic);
            var rows = File.ReadAllLines(Path.Combine(output, "command-confirmation-diagnostics.jsonl")).Select(JObject.Parse).ToArray();
            Assert.That(rows.Length, Is.EqualTo(4));
            for (int index = 0; index < rows.Length; index++)
            {
                Assert.That(rows[index]["diagnostic"].Children<JProperty>().Count(), Is.EqualTo(12));
                Assert.That(JToken.DeepEquals(rows[index]["diagnostic"], JObject.FromObject(diagnostics[index])), Is.True);
                Assert.That((string)rows[index]["RunId"], Is.EqualTo("真实运行"));
                Assert.That(rows[index]["utc"].Type, Is.EqualTo(JTokenType.Date));
                Assert.That(rows[index]["LocalPlayerId"].Type, Is.EqualTo(index == 0 ? JTokenType.Null : JTokenType.Integer));
            }
            Assert.That((int)rows[1]["LocalPlayerId"], Is.EqualTo(2));
            Assert.That(rows[0]["diagnostic"]["ApplySucceeded"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That((bool)rows[1]["diagnostic"]["ApplySucceeded"], Is.False);
            Assert.That((string)rows[1]["diagnostic"]["ErrorCode"], Is.EqualTo("UnknownCommand"));
            Assert.That((bool)rows[2]["diagnostic"]["ApplySucceeded"], Is.True);
            Assert.That(Get(driver, "boundGameId"), Is.EqualTo("旧局"));
            Assert.That(Get(driver, "stopped"), Is.False);
            Assert.That(Get(driver, "confirmations"), Is.EqualTo(9)); Assert.That(Get(driver, "commands"), Is.EqualTo(3));
            Assert.That(Get(driver, "initialSnapshots"), Is.EqualTo(2)); Assert.That(Get(driver, "lastSignature"), Is.EqualTo("等待签名"));
            Assert.That(ReferenceEquals(Get(driver, "latest"), latest), Is.True);
            Assert.That(Get(policy, "pendingCommandId"), Is.EqualTo("真实等待"));
            Assert.That(Get(policy, "pendingStepId"), Is.EqualTo("真实步骤"));
        }

#if !NETWORK_VALIDATION_LIFECYCLE_OFFLINE
        // 复用既有壳装配与真实正式会话夹具，不执行它们的测试，不改场景/Prefab。
        private sealed class SimulationDriverFixture : IDisposable
        {
            private readonly CommandSubmissionAcknowledgmentTests shell = new CommandSubmissionAcknowledgmentTests();
            private readonly object scenario, context, submission;
            private readonly UnityEngine.GameObject contextObject, driverObject;
            private readonly object previousTransport;
            public readonly object Driver, Mobile, Transport;
            public readonly object Group;
            public readonly GameSession Session;
            public readonly DeterministicSimulationCoordinator Coordinator, Host;
            public readonly YC.Domain.Effects.EffectRegistry Registry;
            public readonly string Output = EvidenceDirectory();
            public SimulationDriverFixture(bool productionResults = false)
            {
                scenario = Call(shell, "Create"); Mobile = Get(scenario, "Mobile"); Transport = Get(scenario, "Transport");
                previousTransport = Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport").GetField("<Instance>k__BackingField", Statics).GetValue(null);
                Call(Get(scenario, "Controller"), "Dispose");
                var group = typeof(ConfirmedSimulationRecoveryTests).GetMethod("CreateGroup", Statics).Invoke(null, new object[] { false });
                Group = group;
                Session = ((GameSession[])Get(group, "Sessions"))[1];
                Registry = ((YC.Domain.Effects.EffectRegistry[])Get(group, "Registries"))[1];
                var coordinators = (DeterministicSimulationCoordinator[])Get(group, "Coordinators");
                Coordinator = coordinators[1]; Host = coordinators[0];
                contextObject = new UnityEngine.GameObject("正式驱动启动接缝"); context = contextObject.AddComponent(Target("YC.Presentation.GameLaunchContext"));
                Set(context, "Mode", LaunchMode.Client); Set(context, "LocalPlayerId", 1); Set(context, "GameId", Session.State.GameId);
                Set(context, "Players", Session.State.Players.Select(p => new PlayerSeat { PlayerId = p.PlayerId }).ToList());
                // EditMode不自动执行普通MonoBehaviour的Awake；保持与既有原生夹具同一静态绑定。
                context.GetType().GetField("<Instance>k__BackingField", Statics).SetValue(null, context);
                Set(Mobile, "session", Session); Set(Mobile, "localPlayerId", 1);
                Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport").GetField("<Instance>k__BackingField", Statics).SetValue(null, Transport);
                typeof(CommandSubmissionAcknowledgmentTests).GetMethod("BindRealLocalConnection", Statics).Invoke(null, new[] { scenario });
                var notify = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), Mobile, Mobile.GetType().GetMethod("NotifyPendingEffectSettled", Members));
                if (productionResults)
                {
                    Set(Mobile, "sessionEffectRegistry", Registry);
                    // 消费BuildCommandSubmission实际注册的结果委托；仅隔离本例无关的完整画面刷新。
                    Call(Mobile, "BuildCommandSubmission", context);
                    submission = Get(Mobile, "commandSubmission");
                    Set(submission, "refreshFromState", new Action(() => { }));
                }
                else submission = Activator.CreateInstance(Target("YC.Presentation.CommandSubmissionController"), new object[] {
                    Session, context, 1, null, new Action(() => { }), new Action<string>(_ => { }), notify,
                    new Action<string, bool>((_, __) => { }), new Action<string, string, bool, CommandErrorCode>((_, __, ___, ____) => { }) });
                Set(scenario, "Controller", submission); Set(Mobile, "commandSubmission", submission);
                if (!productionResults) Call(submission, "Initialize", Registry);
                Call(Transport, "AttachSimulation", Coordinator, true);
                var factory = typeof(CommandSubmissionAcknowledgmentTests).GetMethod("ControllerFactory", Statics)
                    .MakeGenericMethod(submission.GetType()).Invoke(null, new[] { submission });
                var adapter = Activator.CreateInstance(Target("YC.Presentation.MobileCityGameplayAdapter"), new object[] {
                    new Func<GameState>(() => Session.State), new Func<int>(() => 1), new Func<bool>(() => false), new Action<int>(_ => { }), factory, new Action(() => { }) });
                Set(Mobile, "gameplayAdapter", adapter);
                Set(Mobile, "commandGateway", new YC.Presentation.Workflows.CommandGateway((YC.Presentation.Workflows.IGameCommandPort)adapter));
                // EditMode普通Mobile壳不自动Awake；仅复用已装配的真实提交入口。
                ((UnityEngine.MonoBehaviour)Mobile).gameObject.SetActive(true);
                driverObject = new UnityEngine.GameObject("正式驱动原生检查"); Driver = driverObject.AddComponent(Target("YC.Presentation.NetworkGameValidationDriver"));
                Set(Driver, "output", Output); Set(Driver, "observationOnly", true);
                Call(Driver, "AttachSimulationTransport", Transport);
            }
            public GameCommand Submit(string id)
            {
                var command = DeterministicSimulationCoordinatorTests.CreateCurrentAnswer(Session, 1, id);
                Set(Driver, "pendingSimulationCommand", GameCommandDto.FromCommand(command));
                Call(Mobile, "SubmitValidationCommand", command);
                return command;
            }
            public GameCommand SubmitFromOwnerView()
            {
                var view = (GameStateView)Call(Mobile, "ValidationView", 1);
                var plan = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(new {
                    RouteName="恢复后显式新决策", view.MapId, ContentPackHash=view.ContentPackHash, PlayerCount=view.Players.Count,
                    EnabledExpansionIds=view.RuleConfiguration.EnabledExpansionIds, AllowAdaptiveMainActions=true, EvaluationMode="whole-game-health"
                }), Target("YC.Presentation.Validation.ProductionGameRoutePlan"));
                var policy=Activator.CreateInstance(Target("YC.Presentation.Validation.ProductionGameRoutePolicy"),plan,null);
                var next=(GameCommand)Call(policy,"Decide",view,1,"");
                Assert.That(next,Is.Not.Null);Assert.That(next.PlayerId,Is.EqualTo(1));
                Assert.That(view.ViewerRole,Is.EqualTo(GameStateViewerRole.Player));Assert.That(view.ViewerPlayerId,Is.EqualTo(1));
                Assert.That(next.Parameters["expectedRevision"],Is.EqualTo(view.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Set(Driver,"routePolicy",policy);Set(Driver,"pendingSimulationCommand",GameCommandDto.FromCommand(next));
                Call(Mobile,"SubmitValidationCommand",next);
                return next;
            }
            public int QueuedFrameCount => (int)Get(Get(scenario,"ServerConnection"),"queue").GetType().GetProperty("Count").GetValue(Get(Get(scenario,"ServerConnection"),"queue"));
            public void BindActualRefresh(Action refresh) => Set(submission,"refreshFromState",refresh);
            public void Dispose()
            {
                Call(Driver, "DetachTransport"); UnityEngine.Object.DestroyImmediate(driverObject);
                Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport").GetField("<Instance>k__BackingField", Statics).SetValue(null, previousTransport);
                shell.Clear(); UnityEngine.Object.DestroyImmediate(contextObject);
            }
        }

        [Test]
        public void NativeDeterministicReadyAndLocalReceiptUseFormalSubmission()
        {
            using (var fixture = new SimulationDriverFixture())
            {
                Assert.That(Get(fixture.Driver, "boundGameId"), Is.EqualTo(fixture.Session.State.GameId));
                Assert.That(Get(fixture.Driver, "simulationCoordinator"), Is.SameAs(fixture.Coordinator));
                Assert.That(Get(fixture.Driver, "initialSnapshots"), Is.EqualTo(1), "晚订阅从同一已安装Coordinator接续。");
                Call(fixture.Driver, "OnSimulationReady", fixture.Coordinator);
                Assert.That(Get(fixture.Driver, "initialSnapshots"), Is.EqualTo(1));
                Assert.That(Get(fixture.Transport, "InitialStateViewApplied"), Is.Null);
                var command = fixture.Submit("native-local-receipt");
                Assert.That(fixture.Coordinator.HasPendingLocalApplication, Is.True);
                Assert.That(Get(fixture.Mobile, "pendingEffectCommandId"), Is.EqualTo(command.CommandId));
                var accepted = fixture.Host.AdmitProposal(1, fixture.Coordinator.ReadPendingLocalProposal());
                Assert.That(accepted.Status, Is.EqualTo(OperationAdmissionStatus.Accepted));
                fixture.Coordinator.ReceiveProposalResult(accepted);
                fixture.Coordinator.ReceiveOrdered(accepted.OrderedCommand);
                fixture.Host.Pump(1); fixture.Coordinator.Pump(1);
                Assert.That(Get(fixture.Driver, "pendingSimulationCommand"), Is.Null);
                Assert.That(Get(fixture.Mobile, "pendingEffectCommandId"), Is.Null.Or.Empty);
                Assert.That(fixture.Coordinator.LastAppliedSequence, Is.EqualTo(1));
                Assert.That(fixture.Coordinator.ValidatedThroughSequence, Is.EqualTo(0), "本地解锁不等待Host前缀通知。");
                Assert.That(File.Exists(Path.Combine(fixture.Output, "confirmed.jsonl")), Is.False);
                fixture.Coordinator.ReceiveValidationPrefix(fixture.Host.ReadLocalValidationPrefix());
                Assert.That(Get(fixture.Driver, "hostValidatedSequence"), Is.EqualTo(1));
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output, "simulation-applied.jsonl")).Length, Is.EqualTo(1));
                File.WriteAllText(Path.Combine(fixture.Output, "正式入口观察.json"), JsonConvert.SerializeObject(new {
                    Entry="SubmitValidationCommand → SubmitPendingEffectCommand → CommandGateway → CommandSubmissionController → MirrorCommandTransport → Coordinator",
                    LocalApplied=1,HostPrefixBeforeDelivery=0,UnknownPendingSucceeded=false,UiClickUsed=false}, Formatting.Indented));
            }
        }

        [Test]
        public void NativeWholeGameLimitsRemainDisabledAndWaitingPreservesPending()
        {
            using(var fixture=new SimulationDriverFixture())
            {
                var view=fixture.Session.View;
                var plan=JsonConvert.DeserializeObject(JsonConvert.SerializeObject(new {
                    RouteName="正式整局撤限接缝",view.MapId,ContentPackHash=view.ContentPackHash,
                    PlayerCount=view.Players.Count,EnabledExpansionIds=view.RuleConfiguration.EnabledExpansionIds,
                    AllowAdaptiveMainActions=true,EvaluationMode="whole-game-health",MaximumCommands=1200
                }),Target("YC.Presentation.Validation.ProductionGameRoutePlan"));
                var policy=Activator.CreateInstance(Target("YC.Presentation.Validation.ProductionGameRoutePolicy"),plan,null);
                Assert.That((int)Get(plan,"MaximumCommands"),Is.EqualTo(0),"旧正式路线1200不能恢复命令硬停。");
                Set(fixture.Driver,"routePlan",plan);Set(fixture.Driver,"routePolicy",policy);
                Assert.That((int)Get(fixture.Driver,"validationTimeout"),Is.EqualTo(0),"Player默认不设时限。");
                Call(fixture.Driver,"ConfigureValidationLimits","1800");
                var limits=JObject.Parse(File.ReadAllText(Path.Combine(fixture.Output,"validation-limits.json")));
                Assert.That((int)limits["EffectiveTimeoutSeconds"],Is.EqualTo(0));
                Assert.That((int)limits["MaximumCommands"],Is.EqualTo(0));
                Assert.That((int)limits["NoProgressHardStopSeconds"],Is.EqualTo(0));
                Assert.That((bool)limits["StaleRefreshWaitIsHardStop"],Is.False);
                var pending=fixture.Submit("撤限保留真实本人在途提交");
                float now=UnityEngine.Time.realtimeSinceStartup;
                Set(fixture.Driver,"validationTimeout",1);Set(fixture.Driver,"started",now-1801f);
                Set(fixture.Driver,"commands",5001);Set(fixture.Driver,"nextAction",now+100f);
                Assert.That(fixture.Driver.GetType().GetProperty("ValidationDeadlineExceeded",Members).GetValue(fixture.Driver),Is.False);
                Assert.That(fixture.Driver.GetType().GetProperty("CommandBudgetExhausted",Members).GetValue(fixture.Driver),Is.False);
                Call(fixture.Driver,"Update");
                Set(fixture.Driver,"observedState",view.StateRevision+":"+view.Round+":"+view.Phase);
                Set(fixture.Driver,"lastProgressTime",now-600f);
                Assert.That(Call(fixture.Driver,"ObserveAuthoritativeProgress",view),Is.True);
                Assert.That(Call(fixture.Driver,"ObserveAuthoritativeProgress",view),Is.True);
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"validation-warnings.jsonl")).Length,Is.EqualTo(1));
                Assert.That(Get(fixture.Driver,"stopped"),Is.False);
                Assert.That(File.Exists(Path.Combine(fixture.Output,"result.json")),Is.False,"纯等待不能写失败或假终局。");
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.EqualTo(pending.CommandId));
                Assert.That(((GameCommandDto)Get(fixture.Driver,"pendingSimulationCommand")).CommandId,Is.EqualTo(pending.CommandId));
                Assert.That(fixture.Coordinator.ReadPendingLocalProposal().Command.CommandId,Is.EqualTo(pending.CommandId));
                File.WriteAllText(Path.Combine(fixture.Output,"正式撤限与纯等待观察.json"),JsonConvert.SerializeObject(new {
                    Limits=limits,PendingCommandId=pending.CommandId,StillPending=true,WarningCount=1,
                    Stopped=false,Boundary="实际Driver Update/观察和真实Gateway在途；时间与命令数为阈值夹具，不替代终局"},Formatting.Indented));
            }
            using(var fixture=new SimulationDriverFixture())
            {
                Set(fixture.Driver,"diagnosticSampleRequested",true);
                Call(fixture.Driver,"ConfigureValidationLimits","600");
                Set(fixture.Driver,"started",UnityEngine.Time.realtimeSinceStartup-601f);
                Assert.That(fixture.Driver.GetType().GetProperty("ValidationDeadlineExceeded",Members).GetValue(fixture.Driver),Is.True,
                    "显式有限诊断保留正数时限，不作为本次正式局前置。");
            }
        }

        [Test]
        public void NativeSameRevisionPermissionRefreshKeepsBusyReceiptAndNextRoute()
        {
            using(var fixture=new SimulationDriverFixture(productionResults:true))
            {
                var options=fixture.Coordinator.SessionOptions;
                var timing=fixture.Session.ExecutionTimings;timing.Enabled=true;
                Assert.That(Call(fixture.Mobile,"ValidationReadyFor",options),Is.True);
                Assert.That(Call(fixture.Mobile,"RefreshSimulationPermissionOnly"),Is.False,"首个本人上下文需要原完整同步。");
                int fast=0,synchronize=0;
                fixture.BindActualRefresh(()=>{
                    bool permissionOnly=(bool)Call(fixture.Mobile,"RefreshSimulationPermissionOnly");
                    if(permissionOnly)fast++;else synchronize++;
                });
                var initialView=fixture.Session.View;int revision=initialView.StateRevision;
                var permission=fixture.Coordinator.ReadWindowPermissions().First(p=>p.CanSubmitForLocalPlayer);
                ((Delegate)Get(fixture.Transport,"OperationWindowPermissionChanged")).DynamicInvoke(permission);
                Assert.That(fast,Is.GreaterThan(0));Assert.That(fixture.Session.View,Is.SameAs(initialView));
                Assert.That(fixture.Mobile.GetType().GetProperty("ValidationCanSubmit",Members).GetValue(fixture.Mobile),Is.True,"同revision真实许可通知后立即可提交。");
                var old=fixture.SubmitFromOwnerView();var proposal=fixture.Coordinator.ReadPendingLocalProposal();
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.EqualTo(old.CommandId));
                Assert.That(fixture.Mobile.GetType().GetProperty("ValidationCanSubmit",Members).GetValue(fixture.Mobile),Is.False);
                int prior=synchronize;
                var rejected=new SimulationProposalResultDto {GameId=proposal.GameId,SimulationEpoch=proposal.SimulationEpoch,
                    MembershipVersion=proposal.MembershipVersion,Proposal=proposal,Status=OperationAdmissionStatus.Rejected,
                    ErrorCode=CommandErrorCode.StateRevisionMismatch,RequiresRefresh=true,
                    ExpectedClientSubmissionSequence=proposal.ClientSubmissionSequence,Reason="准确同版本拒绝接缝"};
                fixture.Coordinator.ReceiveProposalResult(rejected);
                Assert.That(fixture.Session.State.EffectRuntime.StateRevision,Is.EqualTo(revision));
                Assert.That(fixture.Session.View,Is.SameAs(initialView));
                Assert.That(synchronize,Is.GreaterThan(prior),"准确结果即使同revision也不能被纯许可快路吞掉。");
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                Assert.That(fixture.Mobile.GetType().GetProperty("ValidationCanSubmit",Members).GetValue(fixture.Mobile),Is.True,"准确拒绝解busy后可走后继本人路由。");
                var next=fixture.SubmitFromOwnerView();Assert.That(next.CommandId,Is.Not.EqualTo(old.CommandId));
                Assert.That(next.Parameters["expectedRevision"],Is.EqualTo(revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(fixture.Coordinator.ReadPendingLocalProposal().Command.CommandId,Is.EqualTo(next.CommandId));
                var admitted=fixture.Host.AdmitProposal(1,fixture.Coordinator.ReadPendingLocalProposal());
                Assert.That(admitted.Status,Is.EqualTo(OperationAdmissionStatus.Accepted));
                fixture.Coordinator.ReceiveProposalResult(admitted);fixture.Coordinator.ReceiveOrdered(admitted.OrderedCommand);
                int applications=0;fixture.Coordinator.SimulationCommandApplied+=_=>applications++;
                fixture.Host.Pump(1);fixture.Coordinator.Pump(1);
                Assert.That(applications,Is.EqualTo(1));Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                Assert.That(fixture.Coordinator.IsStopped,Is.False,fixture.Coordinator.LastFault?.Diagnostic);
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"simulation-applied.jsonl")).Length,Is.EqualTo(1));
                File.WriteAllText(Path.Combine(fixture.Output,"同版本许可拒绝与后继路由.json"),JsonConvert.SerializeObject(new {
                    GameId=fixture.Session.State.GameId,ActualLocalPlayerId=1,Revision=revision,SameViewKept=true,
                    FastPermissionCallbacks=fast,RequiredSynchronizationCallbacks=synchronize,OldCommandId=old.CommandId,
                    NewCommandId=next.CommandId,Applications=applications,BusyCleared=true,UiClickUsed=false,
                    Boundary="实际提交/Gateway/Coordinator回执与Mobile纯许可同步判定；不替代完整画面/全部交互控件验证"},Formatting.Indented));
            }
        }

        [Test]
        public void NativePhaseTimingOptInPreservesFormalReceiptAndExitSnapshot()
        {
            using(var fixture=new SimulationDriverFixture())
            {
                Assert.That(fixture.Session.ExecutionTimings.Enabled,Is.False);
                var command=fixture.Submit("phase-default-off");
                var admitted=fixture.Host.AdmitProposal(1,fixture.Coordinator.ReadPendingLocalProposal());
                Assert.That(admitted.Status,Is.EqualTo(OperationAdmissionStatus.Accepted));
                fixture.Coordinator.ReceiveProposalResult(admitted);fixture.Coordinator.ReceiveOrdered(admitted.OrderedCommand);
                fixture.Host.Pump(1);fixture.Coordinator.Pump(1);
                Assert.That(fixture.Session.ExecutionTimings.Snapshot().Where(p=>p.Key.EndsWith(".calls")||p.Key.EndsWith(".ticks")).All(p=>p.Value==0),Is.True);
                Call(fixture.Driver,"WritePhaseTimingSnapshot","exit",true);
                Assert.That(File.Exists(Path.Combine(fixture.Output,"phase-timings.jsonl")),Is.False,"未显式启用没有相位输出。");
                Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"simulation-applied.jsonl")).Length,Is.EqualTo(1));
                ExercisePhaseDecisionRead(fixture);
                Assert.That((long)Get(fixture.Driver,"decisionCalls"),Is.EqualTo(0),"关闭时不读决策计时钟或累计。");
            }
            using(var fixture=new SimulationDriverFixture())
            {
                Set(fixture.Driver,"phaseTimingRequested",true);Call(fixture.Driver,"EnablePhaseTiming",fixture.Mobile);
                var enabled=JObject.Parse(File.ReadAllText(Path.Combine(fixture.Output,"phase-timing-enabled.json")));
                Assert.That((bool)enabled["DiagnosticSampling"],Is.False,"正式局可显式计时，不因计时开关排除整局验收。");
                Assert.That(fixture.Session.ExecutionTimings.Enabled,Is.True);
                Assert.That(Get(fixture.Driver,"phaseTimings"),Is.SameAs(fixture.Session.ExecutionTimings));
                int applications=0;fixture.Coordinator.SimulationCommandApplied+=_=>applications++;
                var command=fixture.Submit("phase-explicit-on");
                var admitted=fixture.Host.AdmitProposal(1,fixture.Coordinator.ReadPendingLocalProposal());
                Assert.That(admitted.Status,Is.EqualTo(OperationAdmissionStatus.Accepted));
                fixture.Coordinator.ReceiveProposalResult(admitted);fixture.Coordinator.ReceiveOrdered(admitted.OrderedCommand);
                fixture.Host.Pump(1);fixture.Coordinator.Pump(1);
                var snapshot=fixture.Session.ExecutionTimings.Snapshot();
                Assert.That(snapshot["submit.total.calls"],Is.GreaterThan(0));Assert.That(snapshot["pump.apply.calls"],Is.GreaterThan(0));
                Assert.That(snapshot["pump.total.calls"],Is.GreaterThan(0));Assert.That(snapshot["boundary.digest.calls"],Is.GreaterThan(0));
                Assert.That(snapshot["clock.frequency"],Is.EqualTo(System.Diagnostics.Stopwatch.Frequency));
                snapshot["submit.total.calls"]=-100;
                Assert.That(fixture.Session.ExecutionTimings.Snapshot()["submit.total.calls"],Is.GreaterThan(0),"Snapshot是独立小副本。");
                Assert.That(applications,Is.EqualTo(1));Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"simulation-applied.jsonl")).Length,Is.EqualTo(1));
                Assert.That(File.Exists(Path.Combine(fixture.Output,"phase-timings.jsonl")),Is.False,"未满40G不逐条采样。");
                ExercisePhaseDecisionRead(fixture);
                Assert.That((long)Get(fixture.Driver,"decisionCalls"),Is.EqualTo(1));
                Assert.That((long)Get(fixture.Driver,"decisionTicks"),Is.GreaterThanOrEqualTo(0));
                int revision=fixture.Session.State.EffectRuntime.StateRevision;int frames=fixture.QueuedFrameCount;
                Call(fixture.Driver,"WritePhaseTimingSnapshot","exit",true);
                var rows=File.ReadAllLines(Path.Combine(fixture.Output,"phase-timings.jsonl"));Assert.That(rows.Length,Is.EqualTo(1));
                var row=JObject.Parse(rows[0]);Assert.That((string)row["GameId"],Is.EqualTo(fixture.Session.State.GameId));
                Assert.That((int)row["ActualLocalPlayerId"],Is.EqualTo(1));Assert.That((long)row["G"],Is.EqualTo(1));
                Assert.That((long)row["SimulationEpoch"],Is.EqualTo(1));
                Assert.That((long)row["StopwatchFrequency"],Is.EqualTo(System.Diagnostics.Stopwatch.Frequency));
                Assert.That((long)row["Counters"]["submit.total.calls"],Is.GreaterThan(0));
                Assert.That((long)row["DecisionCalls"],Is.EqualTo(1));
                Assert.That(fixture.Session.State.EffectRuntime.StateRevision,Is.EqualTo(revision));Assert.That(fixture.QueuedFrameCount,Is.EqualTo(frames));
                File.WriteAllText(Path.Combine(fixture.Output,"相位计时正式接缝.json"),JsonConvert.SerializeObject(new {
                    GameId=fixture.Session.State.GameId,Viewer=1,CommandId=command.CommandId,Applications=applications,Sequence=1,
                    DefaultOffHasSamples=false,ExplicitOnHasSamples=true,UnchangedRevision=revision,NoExtraProposal=true,
                    Boundary="真实Gateway/Replica/Pump及当前计数器；退出输出直接调用，不代表40G现场采样或完整局"},Formatting.Indented));
            }
            using(var fixture=new SimulationDriverFixture(productionResults:true))
            {
                Set(fixture.Driver,"diagnosticSampleRequested",true);
                Set(fixture.Driver,"phaseTimingRequested",true);Call(fixture.Driver,"EnablePhaseTiming",fixture.Mobile);
                var enabled=JObject.Parse(File.ReadAllText(Path.Combine(fixture.Output,"phase-timing-enabled.json")));
                Assert.That((bool)enabled["DiagnosticSampling"],Is.True,"仅显式有限采样标记不得纳入整局验收。");
                var counters=fixture.Session.ExecutionTimings;
                fixture.Submit("phase-enabled-before-recovery");Call(fixture.Transport,"RequestConfirmedRecovery");
                fixture.Host.BeginConfirmedRecovery();
                Assert.That(fixture.Host.TryCreateConfirmedRecoveryCheckpoint(0,2,out var checkpoint,out var created),Is.True,created?.Reason);
                var notice=new SimulationRecoveryNoticeDto{GameId=checkpoint.Baseline.GameId,SimulationEpoch=2,SourceSimulationEpoch=1,MembershipVersion=1,
                    CheckpointId=checkpoint.Baseline.CheckpointId,ConfirmedGlobalSequence=0,MetadataDigest=checkpoint.Baseline.MetadataDigest,Phase=SimulationRecoveryPhase.Prepare,ResumeAllowed=true};
                var installer=new SimulationConfirmedRecoveryInstaller(fixture.Session,fixture.Registry,GameStateViewer.Player(1),fixture.Coordinator.SessionOptions);
                Assert.That(installer.TryPrepare(notice,SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint),out var prepared,out var preparedResult),Is.True,preparedResult?.Reason);
                Set(fixture.Transport,"recoveryInstaller",installer);Set(fixture.Transport,"preparedRecovery",prepared);
                Set(fixture.Transport,"recoveryNotice",notice);Set(fixture.Transport,"recoveryCommitIssued",true);Call(fixture.Transport,"InstallLocalRecovery");
                prepared.Coordinator.CompleteConfirmedRecovery(true);Set(fixture.Transport,"recoveryFinished",true);
                Assert.That(Call(fixture.Mobile,"ValidationExecutionTimings",prepared.Coordinator.SessionOptions),Is.Null,
                    "真实共享目标绑定尚未就绪；诊断不得以此改变恢复回调。");
                ((Delegate)Get(fixture.Transport,"SimulationRecoveryCompleted")).DynamicInvoke(prepared.Coordinator);
                Assert.That(Get(fixture.Driver,"phaseTimings"),Is.SameAs(counters));Assert.That(fixture.Session.ExecutionTimings,Is.SameAs(counters));
                Assert.That(counters.Enabled,Is.True);Assert.That(prepared.Coordinator.IsStopped,Is.False);
                Assert.That(Get(fixture.Driver,"simulationEpoch"),Is.EqualTo(2));Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                Assert.That(File.Exists(Path.Combine(fixture.Output,"simulation-faults.jsonl")),Is.False);
                Call(fixture.Driver,"WritePhaseTimingSnapshot","exit",true);
                var exit=JObject.Parse(File.ReadAllLines(Path.Combine(fixture.Output,"phase-timings.jsonl")).Last());
                Assert.That((long)exit["SimulationEpoch"],Is.EqualTo(2));Assert.That((long)exit["G"],Is.EqualTo(0));
                File.WriteAllText(Path.Combine(fixture.Output,"恢复计时同会话接缝.json"),JsonConvert.SerializeObject(new {
                    GameId=fixture.Session.State.GameId,TargetEpoch=2,SameCounterReference=true,SharedBindingTemporarilyUnavailable=true,
                    RecoveryCallbackUnchanged=true,PendingRetired=true,UiClickUsed=false},Formatting.Indented));
            }
            var throwing=new GameSession(new GameState {GameId="phase-original-exception"});
            throwing.RegisterHandler(new PhaseTimingThrowingHandler());
            var failedOff=throwing.Submit(new GameCommand {CommandId="phase-throw-off",PlayerId=1,Kind=GameCommandKind.AnswerInteraction});
            throwing.ExecutionTimings.Enabled=true;
            var failedOn=throwing.Submit(new GameCommand {CommandId="phase-throw-on",PlayerId=1,Kind=GameCommandKind.AnswerInteraction});
            Assert.That(failedOff.Succeeded,Is.False);Assert.That(failedOn.Succeeded,Is.False);
            Assert.That(JsonConvert.SerializeObject(failedOn),Is.EqualTo(JsonConvert.SerializeObject(failedOff)),"计时不能改变原handler异常转失败及回滚结果。");
            Assert.That(throwing.ExecutionTimings.Snapshot()["submit.handler.calls"],Is.EqualTo(1));
        }

        [Test]
        public void NativeAcceptedDisconnectWaitsForConfirmedRecoveryAndNextRoute()
        {
            using(var fixture=new SimulationDriverFixture(productionResults:true))
            {
                WithLocalRecoveryRuntime(fixture, (runtime, transport) =>
                {
                var pending=fixture.SubmitFromOwnerView();
                var options=fixture.Coordinator.SessionOptions;
                Set(fixture.Driver,"finalHostExportStarted",true);
                File.WriteAllText(Path.Combine(fixture.Output,"protocol-summary.json"),JsonConvert.SerializeObject(Call(fixture.Mobile,"ValidationProtocolSummary",fixture.Coordinator)));
                Call(transport,"OnCheckpointClientDisconnected");
                Assert.That(Call(transport,"CanAwaitLocalSimulationConnectionRecovery",fixture.Coordinator.LastFault),Is.True);
                Assert.That(Get(fixture.Driver,"stopped"),Is.False);
                Assert.That(Get(fixture.Driver,"awaitingAcceptedSessionRecovery"),Is.True);
                Assert.That(((GameCommandDto)Get(fixture.Driver,"pendingSimulationCommand")).CommandId,Is.EqualTo(pending.CommandId));
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"simulation-faults.jsonl")).Length,Is.EqualTo(1));
                Call(fixture.Driver,"Finish",new object[]{null});
                Assert.That(File.Exists(Path.Combine(fixture.Output,"result.json")),Is.False,"待恢复不可写完成或安排退出。");
                Call(fixture.Driver,"OnNetworkDisconnected");
                Assert.That(Get(fixture.Driver,"awaitingAcceptedSessionRecovery"),Is.True,"物理Connected不能当作确认恢复完成。");
                fixture.Host.BeginConfirmedRecovery();
                Assert.That(fixture.Host.TryCreateConfirmedRecoveryCheckpoint(0,2,out var checkpoint,out var created),Is.True,created?.Reason);
                var notice=new SimulationRecoveryNoticeDto {GameId=options.GameId,SourceSimulationEpoch=1,SimulationEpoch=2,
                    MembershipVersion=1,CheckpointId=checkpoint.Baseline.CheckpointId,ConfirmedGlobalSequence=0,
                    MetadataDigest=checkpoint.Baseline.MetadataDigest,Phase=SimulationRecoveryPhase.Prepare,ResumeAllowed=true};
                // 输入为本地连接边界夹具；恢复状态、分片、Installer和完成通知均走实际运输处理。
                Set(transport,"checkpointHostConnection",Target("Mirror.NetworkClient").GetProperty("connection",Statics).GetValue(null));
                ReceiveValidationRecoveryControl(transport,notice);
                Assert.That(Get(fixture.Driver,"recoveringSimulation"),Is.True);
                var envelope=new SimulationCheckpointEnvelope {ProtocolVersion=SimulationCheckpointEnvelope.RecoveryProtocolVersion,
                    Purpose=SimulationCheckpointEnvelope.ConfirmedSimulationRecovery,GameId=options.GameId,ContentPackHash=options.ContentPackHash,
                    SourceSimulationEpoch=1,SimulationEpoch=2,MembershipVersion=1,CheckpointId=notice.CheckpointId,
                    GlobalCommandSequence=0,ArchiveDigestVersion=SimulationCheckpointEnvelope.RecoveryDigestVersion};
                var transfer=SimulationCheckpointTransfer.Encode(envelope,SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint));
                foreach(var part in transfer.Split())
                    Call(transport,"ReceiveConfirmedRecoveryPart",ValidationCheckpointPart(transport,transfer.Envelope,part),transfer.Envelope);
                notice.Phase=SimulationRecoveryPhase.Commit;ReceiveValidationRecoveryControl(transport,notice);
                Assert.That(RecoveryProperty(transport,"IsAwaitingLocalSimulationConnectionRecovery"),Is.True,"安装完成仍须规定成员完整恢复。");
                Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Not.Null);
                notice.Phase=SimulationRecoveryPhase.Installed;ReceiveValidationRecoveryControl(transport,notice);
                var target=(DeterministicSimulationCoordinator)RecoveryProperty(transport,"SimulationCoordinator");
                Assert.That(RecoveryProperty(transport,"IsAwaitingLocalSimulationConnectionRecovery"),Is.False);
                Assert.That(Get(fixture.Driver,"stopped"),Is.False);Assert.That(Get(fixture.Driver,"awaitingAcceptedSessionRecovery"),Is.False);
                Assert.That(Get(fixture.Driver,"recoveringSimulation"),Is.False);Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                var sessions=(GameSession[])Get(fixture.Group,"Sessions");
                var registries=(YC.Domain.Effects.EffectRegistry[])Get(fixture.Group,"Registries");
                var coordinators=(DeterministicSimulationCoordinator[])Get(fixture.Group,"Coordinators");coordinators[1]=target;
                var peerNotice=JsonConvert.DeserializeObject<SimulationRecoveryNoticeDto>(JsonConvert.SerializeObject(notice));peerNotice.Phase=SimulationRecoveryPhase.Prepare;
                foreach(int index in new[]{0,2})
                {
                    int player=index==0?3:2;coordinators[index].BeginConfirmedRecovery();
                    var installer=new SimulationConfirmedRecoveryInstaller(sessions[index],registries[index],GameStateViewer.Player(player),coordinators[index].SessionOptions);
                    Assert.That(installer.TryPrepare(peerNotice,SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint),out var peer,out var result),Is.True,result?.Reason);
                    Assert.That(installer.TryCommit(peer,out result),Is.True,result?.Reason);
                    coordinators[index]=peer.Coordinator;peer.Coordinator.CompleteConfirmedRecovery(true);
                }
                typeof(ConfirmedSimulationRecoveryTests).GetMethod("Seal",Statics).Invoke(null,new[]{fixture.Group,(object)0L});
                Assert.That(target.ReadWindowPermissions().Any(p=>p.CanSubmitForLocalPlayer),Is.True);
                Call(fixture.Driver,"RefreshFinalProtocolSummary",fixture.Mobile);
                var finalProtocol=JObject.Parse(File.ReadAllText(Path.Combine(fixture.Output,"protocol-summary.json")));
                Assert.That((long)finalProtocol["SimulationEpoch"],Is.EqualTo(2),"延迟终局归档的旧摘要须在实际共享绑定与许可就绪后刷新。");
                Assert.That((string)finalProtocol["GameId"],Is.EqualTo(options.GameId));
                var next=fixture.SubmitFromOwnerView();
                Assert.That(next,Is.Not.Null);Assert.That(next.CommandId,Is.Not.EqualTo(pending.CommandId));
                Assert.That(next.Parameters["expectedRevision"],Is.EqualTo(fixture.Session.View.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Assert.That(target.ReadPendingLocalProposal().Command.CommandId,Is.EqualTo(next.CommandId));
                Assert.That(target.ReadPendingLocalProposal().SimulationEpoch,Is.EqualTo(2));
                File.WriteAllText(Path.Combine(fixture.Output,"已接受断线与确认恢复后继路由.json"),JsonConvert.SerializeObject(new {
                    GameId=options.GameId,SourceEpoch=1,TargetEpoch=2,OriginalCommandId=pending.CommandId,NextCommandId=next.CommandId,
                    OriginalFaultPreserved=true,NoPrematureResult=true,PendingRetiredAfterResume=true,
                    Boundary="实际Local接纳、断线记录、运输恢复控制与分片、Installer、完成通知及真实许可后新提交；连接输入为夹具，非TCP重连"},Formatting.Indented));
                });
            }
            using(var fixture=new SimulationDriverFixture())
            {
                WithLocalRecoveryRuntime(fixture,(runtime,transport)=>
                {
                    Call(transport,"OnCheckpointClientDisconnected");
                    Assert.That(Get(fixture.Driver,"stopped"),Is.False);
                    Call(transport,"OnLocalClientReconnectFailed","simulation_local_reconnect_timeout");
                    Assert.That(RecoveryProperty(transport,"IsAwaitingLocalSimulationConnectionRecovery"),Is.False);
                    Assert.That(RecoveryProperty(transport,"LocalSimulationConnectionRecoveryFailureCode"),Is.EqualTo("simulation_local_reconnect_timeout"));
                    Assert.That(Get(fixture.Driver,"stopped"),Is.True);
                    Assert.That(File.ReadAllText(Path.Combine(fixture.Output,"result.json")),Does.Contain("simulation_local_reconnect_timeout"));
                    Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"simulation-faults.jsonl")).Length,Is.EqualTo(2));
                });
            }
            using(var fixture=new SimulationDriverFixture())
            {
                var o=fixture.Coordinator.SessionOptions;
                fixture.Coordinator.ReceiveRemoteFault(new SimulationProtocolFaultDto {GameId=o.GameId,SimulationEpoch=o.SimulationEpoch,
                    MembershipVersion=o.MembershipVersion,FaultCode="simulation_connection_lost",RequiresRecovery=true});
                Assert.That(Get(fixture.Driver,"stopped"),Is.True,"未被生产接受的恢复不能仅凭RequiresRecovery吞故障。");
                Assert.That(File.Exists(Path.Combine(fixture.Output,"result.json")),Is.True);
            }
        }

        private static void WithLocalRecoveryRuntime(SimulationDriverFixture fixture,Action<object,object> check)
        {
            var runtimeType=Target("YC.Infrastructure.Multiplayer.MirrorNetworkRuntime");
            var previous=runtimeType.GetProperty("Instance",Statics).GetValue(null);
            var root=new UnityEngine.GameObject("正式恢复状态消费者夹具");root.SetActive(false);
            var runtime=root.AddComponent(runtimeType);
            var transport=fixture.Transport;
            try
            {
                runtimeType.GetField("<Instance>k__BackingField",Statics).SetValue(null,runtime);
                runtimeType.GetProperty("IsLocalTestMode").GetSetMethod(true).Invoke(runtime,new object[]{true});
                Call(runtime,"SetLocalClientIdentityTicket","driver-original-session-ticket");
                Call(transport,"ConfigureConfirmedRecovery",new SimulationConfirmedRecoveryInstaller(fixture.Session,fixture.Registry,GameStateViewer.Player(1),fixture.Coordinator.SessionOptions));
                check(runtime,transport);
            }
            finally
            {
                Call(fixture.Driver,"BindRecoveryRuntime",new object[]{null});
                runtimeType.GetField("<Instance>k__BackingField",Statics).SetValue(null,previous);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void ReceiveValidationRecoveryControl(object transport,SimulationRecoveryNoticeDto notice)
        {
            var parts=(System.Collections.IEnumerable)Call(transport,"ProtocolParts","confirmed-recovery",notice,(string)Get(transport,"clientConnectionToken"));
            foreach(var part in parts)Call(transport,"ReceiveRecoveryHostControl",part);
        }

        private static object RecoveryProperty(object target,string name)=>target.GetType().GetProperty(name,Members).GetValue(target);
        private static object ValidationCheckpointPart(object transport,SimulationCheckpointEnvelope envelope,SimulationCheckpointPart part)
        {
            var message=Activator.CreateInstance(Target("YC.Infrastructure.Multiplayer.SimulationCheckpointPartMessage"));
            Set(message,"EnvelopeJson",JsonConvert.SerializeObject(envelope));Set(message,"Offset",part.Offset);
            Set(message,"Payload",part.Payload);Set(message,"ConnectionToken",Get(transport,"clientConnectionToken"));
            return message;
        }

        [Test]
        public void NativeSettlementOptionAdvancesReadyCleanupSuccessor()
        {
            string fixture = Environment.GetEnvironmentVariable("NMC_CLEANUP_SUCCESSOR_FIXTURE");
            if (string.IsNullOrEmpty(fixture)) throw new InvalidOperationException("必须指定固定正式归档夹具。");
            string output = EvidenceDirectory();
            var input = JObject.Parse(File.ReadAllText(Path.Combine(fixture, "正式输入.json")));
            string beforeJson = File.ReadAllText(Path.Combine(fixture, "G81完整归档.json"));
            var beforeRuntime = JObject.Parse(beforeJson)["Snapshot"]["State"]["EffectRuntime"];
            var originalAfterRuntime = JObject.Parse(File.ReadAllText(Path.Combine(fixture, "原G82完整归档.json")))
                ["Snapshot"]["State"]["EffectRuntime"];
            var entry = beforeRuntime["EffectNodes"].Single(node => node["SettlementWindow"].Type != JTokenType.Null &&
                (string)node["SettlementWindow"]["Context"]["Id"] == (string)beforeRuntime["ActiveSettlementWindowId"])
                ["SettlementWindow"]["Entries"].Single();
            string targetId = (string)entry["TargetEffectId"];
            var originalCompleted = originalAfterRuntime["EffectNodes"].Single(node => (string)node["EffectId"] == targetId);
            var driverType = Target("YC.Presentation.NetworkGameValidationDriver");
            var hostSessionField = driverType.GetField("recoverySession", Statics);
            var hostRegistryField = driverType.GetField("recoveryRegistry", Statics);
            object priorSession = hostSessionField.GetValue(null), priorRegistry = hostRegistryField.GetValue(null);
            var contextObject = new UnityEngine.GameObject("正式收尾后继原生检查");
            try
            {
                object context = contextObject.AddComponent(Target("YC.Presentation.GameLaunchContext"));
                Set(context, "Mode", LaunchMode.Host); Set(context, "LocalPlayerId", 1);
                Set(context, "GameId", (string)input["GameId"]); Set(context, "RoomId", "原局收尾归档检查");
                Set(context, "EventDeckSeed", (int)input["Seed"]);
                Set(context, "InitialRandomState", input["Random"].ToObject<GameplayRandomState>());
                Set(context, "LaunchPlayerOrder", input["Order"].ToObject<List<int>>());
                var configuration = input["Configuration"].ToObject<GameRuleConfiguration>();
                Set(context, "RuleConfiguration", configuration); Set(context, "MapId", configuration.MapId);
                Set(context, "Players", input["Players"].Select(player => new PlayerSeat {
                    PlayerId = (int)player["PlayerId"], PlayerName = (string)player["Name"],
                    Color = (PlayerColor)(int)player["Color"] }).ToList());
                context.GetType().GetField("<Instance>k__BackingField", Statics).SetValue(null, context);
                object pack = Target("YC.Presentation.ExternalContentRuntime").GetProperty("Pack", Statics).GetValue(null);
                foreach (var catalog in new[] {
                    new[] { "YC.Domain.Cards.CharacterCardDatabase", "Initialize", "CreateCharacters" },
                    new[] { "YC.Domain.Cards.EventCardDatabase", "InitializeExternal", "CreateEvents" },
                    new[] { "YC.Domain.Facilities.FacilityCardDatabase", "InitializeExternal", "CreateFacilities" } })
                {
                    var database = Target(catalog[0]);
                    if (!(bool)database.GetProperty("IsInitialized", Statics).GetValue(null))
                        database.GetMethod(catalog[1], Statics).Invoke(null, new[] { Call(pack, catalog[2]) });
                }
                object built = Target("YC.Presentation.GameSessionBootstrapper").GetMethod("BuildForGameplay", Statics)
                    .Invoke(null, new[] { context, (object)false, false });
                var session = (GameSession)Get(built, "Session");
                var registry = (YC.Domain.Effects.EffectRegistry)Get(built, "EffectRegistry");
                Assert.That(session.State.ContentPackHash, Is.EqualTo(configuration.ContentPackHash));
                var recovered = HostRecoveryService.RecoverJson(beforeJson, registry);
                Assert.That(recovered.State, Is.Not.Null, "使用完整正式归档及实际Registry恢复，不能从精简投影补造状态。");
                session.ReplaceState(recovered.State);
                Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(794));
                var commandDto = input["Commands"].Last()["Applied"]["Command"].ToObject<GameCommandDto>();
                var accepted = session.Submit(commandDto.ToCommand());
                Assert.That(accepted.Succeeded, Is.True, accepted.Validation.Reason);
                var active = session.State.EffectRuntime.MainNodes.Single(node => node.NodeId == session.State.EffectRuntime.ActiveMainNodeId);
                Assert.That(active.PlayerId, Is.EqualTo(4));
                Assert.That(active.NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.PlayerCleanupWindow));
                Assert.That(string.IsNullOrEmpty(active.ExecutionEffectId), Is.False);
                Assert.That(session.State.EffectRuntime.EffectNodes.Any(node => node.EffectId == active.ExecutionEffectId), Is.True);
                var window = OperationWindowService.ReadOpenWindows(session.State).Single(w => w.SourceMainNodeId == active.NodeId &&
                    w.WindowKind == RoundMainlineNodeTypeIds.PlayerCleanupWindow);
                Assert.That(window.AvailablePlayerIds.Contains(4), Is.True);
                string digest = RuleStateDigestService.Compute(session.State);
                var duplicate = session.Submit(commandDto.ToCommand());
                Assert.That(duplicate.Succeeded, Is.False);
                Assert.That(RuleStateDigestService.Compute(session.State), Is.EqualTo(digest));
                var stale = commandDto.ToCommand(); stale.CommandId = "旧版本收尾拒绝检查";
                var staleResult = session.Submit(stale);
                Assert.That(staleResult.Succeeded, Is.False);
                Assert.That(RuleStateDigestService.Compute(session.State), Is.EqualTo(digest));
                var next = new GameCommand { PlayerId = 4, Kind = GameCommandKind.EndAction, CommandId = "真实后继窗口检查" };
                Assert.That(OperationWindowService.TryResolveCommandContext(session.State, next, out var resolved, out var validation),
                    Is.True, validation.Reason);
                Assert.That(resolved.OperationWindowId, Is.EqualTo(window.OperationWindowId));
                var completed = session.State.EffectRuntime.EffectNodes.Single(node => node.EffectId == targetId);
                Assert.That(completed.Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(completed.LastCommitSequence, Is.EqualTo((long)originalCompleted["LastCommitSequence"]));
                Assert.That(JToken.DeepEquals(JToken.FromObject(completed.PositionChanges), originalCompleted["PositionChanges"]), Is.True);
                File.WriteAllText(Path.Combine(output, "真实收尾后继检查.json"), JsonConvert.SerializeObject(new {
                    Passed = true, GameId = session.State.GameId, CommandId = commandDto.CommandId,
                    ReplayedCommands = 1, BeforeGlobalSequence = 81, Revision = session.State.EffectRuntime.StateRevision,
                    ActiveMain = active, Window = window, CompletedTargetId = targetId,
                    DuplicateRejected = !duplicate.Succeeded, StaleRejected = !staleResult.Succeeded,
                    RejectedCommandsPreserveDigest = true, CompletedSettlementNotConsumedAgain = true,
                    ActualBootstrapperRegistryHandler = true, FullArchiveRecovery = true,
                    RealSocketVerified = false, SteamEndToEndVerified = false, UiClickUsed = false
                }, Formatting.Indented));
                File.WriteAllText(Path.Combine(output, "G82原生完整归档.json"), HostRecoveryService.SerializeArchive(session.CreateHostArchive()));
            }
            finally
            {
                hostSessionField.SetValue(null, priorSession); hostRegistryField.SetValue(null, priorRegistry);
                UnityEngine.Object.DestroyImmediate(contextObject);
            }
        }

        [Test]
        public void NativeFinalHostProtocolSummaryRemainsCurrentAfterSubmissionBindingEnds()
        {
            var type=Target("YC.Presentation.NetworkGameValidationDriver");
            var previousSession=type.GetField("recoverySession",Statics).GetValue(null);
            var previousRegistry=type.GetField("recoveryRegistry",Statics).GetValue(null);
            var group=typeof(ConfirmedSimulationRecoveryTests).GetMethod("CreateGroup",Statics).Invoke(null,new object[]{false});
            var session=((GameSession[])Get(group,"Sessions"))[0];
            var registry=((YC.Domain.Effects.EffectRegistry[])Get(group,"Registries"))[0];
            var coordinator=((DeterministicSimulationCoordinator[])Get(group,"Coordinators"))[0];
            var driver=Driver();var output=EvidenceDirectory();
            try
            {
                type.GetMethod("BindHostRecoverySession",Statics).Invoke(null,new object[]{session,registry});
                Set(driver,"output",output);Set(driver,"boundGameId",session.State.GameId);
                Set(driver,"simulationPlayer",coordinator.SessionOptions.LocalPlayerId);Set(driver,"simulationMembership",1);
                Set(driver,"simulationEpoch",1L);Set(driver,"simulationCoordinator",coordinator);
                Call(driver,"RefreshFinalProtocolSummary",new object[]{null});
                coordinator.BeginConfirmedRecovery();
                Assert.That(coordinator.TryCreateConfirmedRecoveryCheckpoint(0,2,out var checkpoint,out var created),Is.True,created?.Reason);
                var notice=new SimulationRecoveryNoticeDto {GameId=session.State.GameId,SourceSimulationEpoch=1,SimulationEpoch=2,MembershipVersion=1,
                    CheckpointId=checkpoint.Baseline.CheckpointId,ConfirmedGlobalSequence=0,MetadataDigest=checkpoint.Baseline.MetadataDigest,
                    Phase=SimulationRecoveryPhase.Prepare,ResumeAllowed=true};
                var installer=new SimulationConfirmedRecoveryInstaller(session,registry,GameStateViewer.Player(coordinator.SessionOptions.LocalPlayerId),coordinator.SessionOptions);
                Assert.That(installer.TryPrepare(notice,SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint),out var prepared,out var result),Is.True,result?.Reason);
                Assert.That(installer.TryCommit(prepared,out result),Is.True,result?.Reason);prepared.Coordinator.CompleteConfirmedRecovery(true);
                Set(driver,"simulationCoordinator",prepared.Coordinator);Set(driver,"simulationEpoch",2L);
                // 实际同局Host安装后，提交UI/房间绑定可已退出；诊断只读正式Host会话和当前Coordinator。
                Call(driver,"RefreshFinalProtocolSummary",new object[]{null});
                var summary=JObject.Parse(File.ReadAllText(Path.Combine(output,"protocol-summary.json")));
                Assert.That((long)summary["SimulationEpoch"],Is.EqualTo(2));
                Assert.That((string)summary["GameId"],Is.EqualTo(session.State.GameId));
                Assert.That((string)summary["RuleStateDigest"],Is.EqualTo(RuleStateDigestService.Compute(session.State)));
                Assert.That((string)summary["PrefixDigest"],Is.EqualTo(prepared.Coordinator.ReadLocalValidationPrefix().PrefixDigest));
                Set(driver,"boundGameId","wrong-final-host-game");
                var mismatch=Assert.Throws<InvalidOperationException>(()=>Call(driver,"RefreshFinalProtocolSummary",new object[]{null}));
                Assert.That(mismatch.Message,Is.EqualTo("final_protocol_host_session_binding_changed"));
            }
            finally
            {
                type.GetField("recoverySession",Statics).SetValue(null,previousSession);
                type.GetField("recoveryRegistry",Statics).SetValue(null,previousRegistry);
            }
        }

        // 从已经自然完成八回合的原归档读取完整终态；协议接缝新建代次，不冒充重跑原局549条命令。
        private sealed class TerminalProtocolFixture : IDisposable
        {
            private readonly Type type = Target("YC.Presentation.NetworkGameValidationDriver");
            private readonly object previousSession, previousRegistry;
            private readonly UnityEngine.GameObject driverObject;
            internal readonly object Driver, Transport;
            internal readonly GameSession Session;
            internal readonly YC.Domain.Effects.EffectRegistry Registry;
            internal DeterministicSimulationCoordinator Coordinator;
            internal readonly string Output = EvidenceDirectory();
            internal TerminalProtocolFixture()
            {
                previousSession = type.GetField("recoverySession", Statics).GetValue(null);
                previousRegistry = type.GetField("recoveryRegistry", Statics).GetValue(null);
                string path = Environment.GetEnvironmentVariable("NMC_TERMINAL_PROTOCOL_SNAPSHOT");
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("必须指定真实终局完整快照。");
                var settings = (JsonSerializerSettings)typeof(HostRecoveryService).GetField("ArchiveSettings", Statics).GetValue(null);
                var snapshot = JsonConvert.DeserializeObject<HostSnapshotDto>(File.ReadAllText(path), settings);
                Assert.That(snapshot.State.Phase, Is.EqualTo(GamePhase.FinalScoring));
                Assert.That(snapshot.State.FinalScoring.IsResolved, Is.True);
                Assert.That(snapshot.State.Round, Is.EqualTo(8));
                var contextType = Target("YC.Presentation.GameLaunchContext");
                var contextField = contextType.GetField("<Instance>k__BackingField", Statics);
                object previousContext = contextField.GetValue(null);
                var contextObject = new UnityEngine.GameObject("真实终态延迟安装上下文");
                try
                {
                    object context = contextObject.AddComponent(contextType);
                    contextField.SetValue(null, context);
                    Set(context, "Mode", LaunchMode.Client); Set(context, "LocalPlayerId", 1);
                    Set(context, "GameId", snapshot.State.GameId); Set(context, "RoomId", "真实终局接缝");
                    Set(context, "EventDeckSeed", snapshot.State.EventDeckSeed);
                    Set(context, "InitialRandomState", snapshot.State.RandomRuntime.Clone());
                    Set(context, "LaunchPlayerOrder", snapshot.State.LaunchPlayerOrder.ToList());
                    Set(context, "RuleConfiguration", snapshot.State.RuleConfiguration);
                    Set(context, "MapId", snapshot.State.MapId);
                    Set(context, "Players", snapshot.State.Players.Select(player => new PlayerSeat {
                        PlayerId = player.PlayerId, PlayerName = player.Name, Color = player.Color }).ToList());
                    var built = Target("YC.Presentation.GameSessionBootstrapper").GetMethod("Build", Statics, null,
                        new[] { contextType, typeof(bool), typeof(bool), typeof(bool) }, null)
                        .Invoke(null, new[] { context, (object)false, false, true });
                    Session = (GameSession)Get(built, "Session"); Registry = (YC.Domain.Effects.EffectRegistry)Get(built, "EffectRegistry");
                    Session.ReplaceState(snapshot.State);
                }
                finally { UnityEngine.Object.DestroyImmediate(contextObject); contextField.SetValue(null, previousContext); }
                var options = DeterministicSimulationSessionOptions.CreateInitial(true, 1, Session,
                    Session.State.Players.Select(player => player.PlayerId).ToArray());
                Coordinator = new DeterministicSimulationCoordinator(Session, options); Coordinator.Begin();
                Assert.That(Coordinator.IsStopped, Is.False, Coordinator.LastFault?.Diagnostic);
                driverObject = new UnityEngine.GameObject("终局确认态与正常关停原生接缝");
                Driver = driverObject.AddComponent(type);
                Transport = ManagedComponent(Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
                Set(Transport, "simulationCoordinator", Coordinator);
                Set(Driver, "subscribed", Transport); Set(Driver, "output", Output);
                type.GetMethod("BindHostRecoverySession", Statics).Invoke(null, new object[] { Session, Registry });
                Call(Driver, "BindSimulationCoordinator", Coordinator);
            }
            internal void Refresh() => Call(Driver, "RefreshFinalProtocolSummary", new object[] { null });
            internal JObject Read(string name = "protocol-summary.json") => JObject.Parse(File.ReadAllText(Path.Combine(Output, name)));
            public void Dispose()
            {
                Set(Driver, "subscribed", null); UnityEngine.Object.DestroyImmediate(driverObject);
                type.GetField("recoverySession", Statics).SetValue(null, previousSession);
                type.GetField("recoveryRegistry", Statics).SetValue(null, previousRegistry);
            }
        }

        [Test]
        public void NativeTerminalProtocolCaptureSurvivesNormalStopAndActualRecoveryCallback()
        {
            using (var fixture = new TerminalProtocolFixture())
            {
                string digest = RuleStateDigestService.Compute(fixture.Session.State);
                fixture.Refresh();
                Assert.That(Get(fixture.Driver, "lastCompletedHostProtocolSummary"), Is.Not.Null);
                fixture.Coordinator.BeginConfirmedRecovery();
                Assert.That(fixture.Coordinator.TryCreateConfirmedRecoveryCheckpoint(0, 2, out var checkpoint, out var created), Is.True, created?.Reason);
                var installer = new SimulationConfirmedRecoveryInstaller(fixture.Session, fixture.Registry,
                    GameStateViewer.Player(1), fixture.Coordinator.SessionOptions);
                Assert.That(installer.TryPrepare(ConfirmedSimulationRecoveryTests.Notice(checkpoint, true),
                    SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint), out var prepared, out var result), Is.True, result?.Reason);
                Assert.That(installer.TryCommit(prepared, out result), Is.True, result?.Reason);
                prepared.Coordinator.CompleteConfirmedRecovery(true);
                fixture.Coordinator = prepared.Coordinator;
                Set(fixture.Transport, "simulationCoordinator", fixture.Coordinator);
                Set(fixture.Driver, "recoveringSimulation", true); Set(fixture.Driver, "recoveryTargetEpoch", 2L);
                Set(fixture.Driver, "finalHostExportStarted", true);
                Call(fixture.Driver, "OnSimulationRecoveryCompleted", fixture.Coordinator);
                var confirmed = fixture.Read();
                Assert.That((long)confirmed["SimulationEpoch"], Is.EqualTo(2));
                Assert.That((bool)confirmed["IsStopped"], Is.False);
                Assert.That((bool)Get(fixture.Driver, "recoveringSimulation"), Is.False);
                Assert.That((string)confirmed["RuleStateDigest"], Is.EqualTo(digest));
                File.WriteAllText(Path.Combine(fixture.Output, "关停前真实确认摘要.json"), confirmed.ToString());
                fixture.Coordinator.Stop(); fixture.Refresh();
                Assert.That(JToken.DeepEquals(confirmed, fixture.Read()), Is.True);
                var shutdown = fixture.Read("protocol-shutdown-summary.json");
                Assert.That((bool)shutdown["IsStopped"], Is.True);
                shutdown["IsStopped"] = false;
                Assert.That(JToken.DeepEquals(shutdown, confirmed), Is.True, "独立保留真实关停事实，其余同局同代次同序号同摘要。");
                Assert.That(RuleStateDigestService.Compute(fixture.Session.State), Is.EqualTo(digest));
            }
        }

        [Test]
        public void NativeTerminalProtocolRefusesAbsentStaleUnconfirmedOrFaultedCapture()
        {
            using (var fixture = new TerminalProtocolFixture())
            {
                fixture.Refresh(); var original = fixture.Read(); fixture.Coordinator.Stop();
                Set(fixture.Driver, "lastCompletedHostProtocolSummary", null); fixture.Refresh();
                Assert.That((bool)fixture.Read()["IsStopped"], Is.True, "缺关停前原件必须保留停止态。");
                foreach (string key in new[] { "GameId", "SimulationEpoch", "MembershipVersion", "LocalPlayerId",
                    "LocalAppliedSequence", "HostValidatedSequence", "PrefixDigest", "RuleDigestVersion", "RuleStateDigest" })
                {
                    var stale = (JObject)original.DeepClone();
                    stale[key] = stale[key].Type == JTokenType.Integer ? new JValue(-1) : new JValue("不匹配");
                    Set(fixture.Driver, "lastCompletedHostProtocolSummary", stale); fixture.Refresh();
                    Assert.That((bool)fixture.Read()["IsStopped"], Is.True, key + "差额不得借旧摘要通过。");
                    File.WriteAllText(Path.Combine(fixture.Output, "拒绝-" + key + ".json"), fixture.Read().ToString());
                }
                Set(fixture.Driver, "lastCompletedHostProtocolSummary", original);
                fixture.Coordinator.BeginConfirmedRecovery(); fixture.Refresh();
                Assert.That((bool)fixture.Read()["IsRecoveringConfirmedPrefix"], Is.True);
                Assert.That((bool)fixture.Read()["IsStopped"], Is.True);
            }
            using (var fixture = new TerminalProtocolFixture())
            {
                fixture.Refresh();
                fixture.Coordinator.ReceiveRemoteFault(new SimulationProtocolFaultDto { GameId = fixture.Session.State.GameId,
                    SimulationEpoch = 1, MembershipVersion = 1, FaultCode = "native_terminal_real_fault", Diagnostic = "真实故障接缝" });
                Assert.That(fixture.Coordinator.LastFault, Is.Not.Null);
                fixture.Refresh();
                Assert.That((bool)fixture.Read()["RequiresRecovery"], Is.True);
                Assert.That((bool)fixture.Read()["IsStopped"], Is.True);
            }
        }

        private sealed class PhaseTimingThrowingHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command)=>true;
            public CommandResult Handle(GameState state,GameCommand command)=>throw new InvalidOperationException("phase-original-handler-error");
        }
        private static void ExercisePhaseDecisionRead(SimulationDriverFixture fixture)
        {
            var view=(GameStateView)Call(fixture.Mobile,"ValidationView",1);
            var plan=JsonConvert.DeserializeObject(JsonConvert.SerializeObject(new {
                RouteName="决策计时直接差额",view.MapId,ContentPackHash=view.ContentPackHash,PlayerCount=view.Players.Count,
                EnabledExpansionIds=view.RuleConfiguration.EnabledExpansionIds,AllowAdaptiveMainActions=true,EvaluationMode="whole-game-health"
            }),Target("YC.Presentation.Validation.ProductionGameRoutePlan"));
            var policy=Activator.CreateInstance(Target("YC.Presentation.Validation.ProductionGameRoutePolicy"),plan,null);
            Set(fixture.Driver,"routePolicy",policy);
            Call(fixture.Driver,"DecideValidationRoute",view,1,"");
        }

        [Test]
        public void NativeValidationProjectionReusePreservesBindingRecoveryAndLocalReceipt()
        {
            using(var fixture=new SimulationDriverFixture())
            {
                var shared=fixture.Session.View;
                long fallback=(long)Get(fixture.Mobile,"ValidationViewFallbackCount");
                for(int i=0;i<12;i++) Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.SameAs(shared));
                Assert.That((long)Get(fixture.Mobile,"ValidationViewFallbackCount"),Is.EqualTo(fallback),"有效重复读取不再生成投影。");
                Assert.That(Call(fixture.Mobile,"ValidationView",2),Is.Null,"不能借回退读取其他玩家。");
                string hash=shared.ContentPackHash,game=shared.GameId;var role=shared.ViewerRole;
                shared.ContentPackHash="错误内容";Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null);shared.ContentPackHash=hash;
                shared.GameId="错误局";Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null);shared.GameId=game;
                shared.ViewerRole=GameStateViewerRole.Host;Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null);shared.ViewerRole=role;
                int revision=shared.StateRevision;shared.StateRevision--;
                var temporary=(GameStateView)Call(fixture.Mobile,"ValidationView",1);
                Assert.That(temporary,Is.Not.SameAs(shared));Assert.That(temporary.StateRevision,Is.EqualTo(revision));
                Assert.That(fixture.Session.View,Is.SameAs(shared),"验证回退不能写回共享View。");shared.StateRevision=revision;
                Assert.That((long)Get(fixture.Mobile,"ValidationViewFallbackCount"),Is.EqualTo(fallback+1));
                // 正式同版本状态替换会清View；读取不能按revision找回旧对象。
                fixture.Session.ReplaceState(GameStateCloneService.DeepClone(fixture.Session.State));
                Assert.That(fixture.Session.View,Is.Null);
                var replaced=(GameStateView)Call(fixture.Mobile,"ValidationView",1);
                Assert.That(replaced,Is.Not.SameAs(shared));Assert.That(replaced.StateRevision,Is.EqualTo(revision));
                Assert.That(fixture.Session.View,Is.Null);
                ((Delegate)Get(fixture.Transport,"OperationWindowPermissionChanged")).DynamicInvoke(fixture.Coordinator.ReadWindowPermissions().First());
                var current=fixture.Session.View;Assert.That(current,Is.Not.SameAs(shared));
                Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.SameAs(current));
                int applications=0,permissionReads=0;GameStateView atPermission=null;
                fixture.BindActualRefresh(()=>{
                    var owner=(GameStateView)Call(fixture.Mobile,"ValidationView",1);
                    Assert.That(owner,Is.SameAs(fixture.Session.View));
                    Assert.That(owner.StateRevision,Is.EqualTo(fixture.Session.State.EffectRuntime.StateRevision));
                    if(fixture.Coordinator.LastAppliedSequence==1&&applications==0){atPermission=owner;permissionReads++;}
                });
                fixture.Coordinator.SimulationCommandApplied+=_=>applications++;
                var command=fixture.SubmitFromOwnerView();
                var admitted=fixture.Host.AdmitProposal(1,fixture.Coordinator.ReadPendingLocalProposal());
                Assert.That(admitted.Status,Is.EqualTo(OperationAdmissionStatus.Accepted));
                fixture.Coordinator.ReceiveProposalResult(admitted);fixture.Coordinator.ReceiveOrdered(admitted.OrderedCommand);
                fixture.Host.Pump(1);fixture.Coordinator.Pump(1);
                Assert.That(applications,Is.EqualTo(1));Assert.That(permissionReads,Is.GreaterThan(0));
                Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.SameAs(atPermission));
                Assert.That(atPermission,Is.Not.SameAs(current));
                Assert.That(atPermission.Interactions.Where(r=>r.Status=="open"&&r.AnsweringPlayerId==1).All(r=>r.StateRevision==atPermission.StateRevision),Is.True);
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"simulation-applied.jsonl")).Length,Is.EqualTo(1));
                var context=Get(fixture,"context");Set(context,"LocalPlayerId",2);
                Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null);Set(context,"LocalPlayerId",1);
                Set(context,"GameId","其他绑定局");Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null);Set(context,"GameId",game);
                ((UnityEngine.MonoBehaviour)fixture.Mobile).enabled=false;
                Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null,"停用不能回退生成本人投影。");
                ((UnityEngine.MonoBehaviour)fixture.Mobile).enabled=true;
                Call(Get(fixture,"submission"),"Dispose");Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null,"已退订绑定不能回退。");
                // 仅核旧本地/热座读取入口未变；不重复执行已通过的热座整局。
                Set(context,"Mode",LaunchMode.Local);Set(fixture.Mobile,"commandSubmission",null);
                var local=(GameStateView)Call(fixture.Mobile,"ValidationView",2);
                Assert.That(local.ViewerPlayerId,Is.EqualTo(2));Assert.That(local,Is.Not.SameAs(fixture.Session.View));
                Assert.That(fixture.Session.View,Is.SameAs(atPermission));
                File.WriteAllText(Path.Combine(fixture.Output,"验证投影复用正式提交.json"),JsonConvert.SerializeObject(new {
                    GameId=game,BeforeRevision=revision,AfterRevision=atPermission.StateRevision,Applications=applications,PermissionReads=permissionReads,
                    Reuse=Get(fixture.Mobile,"ValidationViewReuseCount"),Fallback=Get(fixture.Mobile,"ValidationViewFallbackCount"),
                    Rejected=Get(fixture.Mobile,"ValidationViewRejectedCount"),Legacy=Get(fixture.Mobile,"ValidationViewLegacyCount"),CommandId=command.CommandId,
                    Boundary="实际SDK许可/提交/Gateway/Pump与验证消费者；未运行完整画面或热座整局"},Formatting.Indented));
            }
            using(var fixture=new SimulationDriverFixture(productionResults:true))
            {
                var oldView=fixture.Session.View;var oldState=fixture.Session.State;
                fixture.Submit("native-projection-before-recovery");Call(fixture.Transport,"RequestConfirmedRecovery");
                Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null,"恢复等待不复用源代次View。");
                fixture.Host.BeginConfirmedRecovery();
                Assert.That(fixture.Host.TryCreateConfirmedRecoveryCheckpoint(0,2,out var checkpoint,out var created),Is.True,created?.Reason);
                var notice=new SimulationRecoveryNoticeDto{GameId=checkpoint.Baseline.GameId,SimulationEpoch=2,SourceSimulationEpoch=1,MembershipVersion=1,
                    CheckpointId=checkpoint.Baseline.CheckpointId,ConfirmedGlobalSequence=0,MetadataDigest=checkpoint.Baseline.MetadataDigest,Phase=SimulationRecoveryPhase.Prepare,ResumeAllowed=true};
                var installer=new SimulationConfirmedRecoveryInstaller(fixture.Session,fixture.Registry,GameStateViewer.Player(1),fixture.Coordinator.SessionOptions);
                Assert.That(installer.TryPrepare(notice,SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint),out var prepared,out var preparedResult),Is.True,preparedResult?.Reason);
                Set(fixture.Transport,"recoveryInstaller",installer);Set(fixture.Transport,"preparedRecovery",prepared);
                Set(fixture.Transport,"recoveryNotice",notice);Set(fixture.Transport,"recoveryCommitIssued",true);Call(fixture.Transport,"InstallLocalRecovery");
                prepared.Coordinator.CompleteConfirmedRecovery(true);Set(fixture.Transport,"recoveryFinished",true);
                Assert.That(Call(fixture.Mobile,"ValidationView",1),Is.Null,"目标Coordinator安装后共享绑定尚未完成，不能借同revision复用旧View。");
                ((Delegate)Get(fixture.Transport,"SimulationRecoveryCompleted")).DynamicInvoke(prepared.Coordinator);
                var recovered=(GameStateView)Call(fixture.Mobile,"ValidationView",1);
                Assert.That(fixture.Session.State,Is.Not.SameAs(oldState));Assert.That(recovered,Is.SameAs(fixture.Session.View));
                Assert.That(recovered,Is.Not.SameAs(oldView));Assert.That(recovered.StateRevision,Is.EqualTo(oldView.StateRevision));
                Assert.That(Get(fixture.Driver,"simulationEpoch"),Is.EqualTo(2));
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                File.WriteAllText(Path.Combine(fixture.Output,"同版本恢复投影复用.json"),JsonConvert.SerializeObject(new {
                    GameId=recovered.GameId,SourceEpoch=1,TargetEpoch=2,Revision=recovered.StateRevision,StateRootReplaced=true,OldViewReused=false,
                    CurrentSharedViewReused=true,RecoveredPendingRetired=true,UiClickUsed=false},Formatting.Indented));
            }
        }

        [Test]
        public void NativePermissionRefreshProjectsOwnerBeforeActualSettlementHudAndClearsEmptyScope()
        {
            using(var fixture=new SimulationDriverFixture())
            {
                const string hudPath="Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
                var source=UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(hudPath);
                Assert.That(source,Is.Not.Null);
                var hudObject=UnityEngine.Object.Instantiate(source);
                try
                {
                    var hud=hudObject.GetComponent(Target("YC.Presentation.GameplayInteractionHudView"));
                    var frame=hud.GetType().GetProperty("Frame").GetValue(hud);
                    var expansion=hud.GetType().GetProperty("ExpansionUi").GetValue(hud);
                    var strip=Get(expansion,"settlementStrip");
                    var catalog=YC.Infrastructure.Lua.ActiveContentCatalogBuilder.Build(
                        YC.Infrastructure.Lua.ExternalContentPack.Current,fixture.Session.State.RuleConfiguration);
                    Call(hud,"ConfigureGameplayPresentation",catalog.TextCatalog,new Dictionary<string,string>());
                    GameStateView overrideView=null;bool empty=false;int sends=0,refreshes=0,applied=0;
                    GameStateView OwnerView()=>empty?null:overrideView??(GameStateView)Call(fixture.Mobile,"VisibleSaleState");
                    Call(expansion,"Configure",new Func<GameStateView>(OwnerView),new Func<int>(()=>1),null,
                        new Dictionary<string,YC.Domain.Cards.CharacterModeContract>(),new Action<GameCommand>(_=>sends++));
                    var order=new List<string>();GameStateView projectedAtPermission=null;
                    fixture.BindActualRefresh(()=>
                    {
                        refreshes++;
                        var owner=OwnerView();
                        Assert.That(owner,Is.Not.Null,"许可回调UI刷新前必须已生成同一已提交状态的本人投影。");
                        Assert.That(owner.ViewerRole,Is.EqualTo(GameStateViewerRole.Player));Assert.That(owner.ViewerPlayerId,Is.EqualTo(1));
                        Assert.That(owner.GameId,Is.EqualTo(fixture.Session.State.GameId));
                        Assert.That(owner.StateRevision,Is.EqualTo(fixture.Session.State.EffectRuntime.StateRevision));
                        if(fixture.Coordinator.LastAppliedSequence==1 && applied==0){projectedAtPermission=owner;order.Add("permission_ui");}
                        Call(expansion,"InvalidateSettlementAvailability");
                    });
                    var before=fixture.Session.View;
                    fixture.Coordinator.SimulationCommandApplied+=_=>{applied++;order.Add("applied");};
                    var command=fixture.Submit("native-permission-before-applied");
                    var admitted=fixture.Host.AdmitProposal(1,fixture.Coordinator.ReadPendingLocalProposal());
                    Assert.That(admitted.Status,Is.EqualTo(OperationAdmissionStatus.Accepted));
                    fixture.Coordinator.ReceiveProposalResult(admitted);fixture.Coordinator.ReceiveOrdered(admitted.OrderedCommand);
                    fixture.Host.Pump(1);fixture.Coordinator.Pump(1);
                    Assert.That(fixture.Coordinator.IsStopped,Is.False,fixture.Coordinator.LastFault?.Diagnostic);
                    Assert.That(applied,Is.EqualTo(1));Assert.That(projectedAtPermission,Is.Not.Null);
                    Assert.That(order.IndexOf("permission_ui"),Is.LessThan(order.IndexOf("applied")));
                    Assert.That(fixture.Session.View,Is.SameAs(projectedAtPermission),"Applied复用刚刚生效的本人投影。");
                    Assert.That(fixture.Session.View,Is.Not.SameAs(before));
                    Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                    var permission=fixture.Coordinator.ReadWindowPermissions().First();
                    var same=fixture.Session.View;int prior=refreshes;
                    ((Delegate)Get(fixture.Transport,"OperationWindowPermissionChanged")).DynamicInvoke(permission);
                    Assert.That(refreshes,Is.GreaterThan(prior),"同版本许可仍刷新控件。");Assert.That(fixture.Session.View,Is.SameAs(same));
                    var owned=(GameStateView)Call(fixture.Mobile,"VisibleSaleState");
                    Assert.That(owned.Interactions.Where(r=>r.Status=="open"&&r.AnsweringPlayerId==1).All(r=>r.StateRevision==owned.StateRevision),Is.True);
                    // UI空态探针只复制本人现有描述，验证真实芯片/绑定租约；不代表游戏内容执行。
                    var label=owned.Interactions.Where(r=>r.AnsweringPlayerId==1).SelectMany(r=>r.Presentation?.Options??new List<YC.Domain.Presentation.GameplayOptionPresentation>())
                        .Select(o=>o.Label).FirstOrDefault(r=>r!=null);
                    if(label==null) label=before.Interactions.Where(r=>r.AnsweringPlayerId==1).Select(r=>r.Presentation?.Prompt).First(r=>r!=null);
                    overrideView=JsonConvert.DeserializeObject<GameStateView>(JsonConvert.SerializeObject(owned));
                    overrideView.ActiveSettlementWindowId="native-ui-empty-probe";
                    overrideView.CharacterSettlementOptions.Add(new YC.Domain.Cards.CharacterSettlementOption {OptionId="native-ui-old",WindowId=overrideView.ActiveSettlementWindowId,
                        EntryKind="optional_insertion",AnsweringPlayerId=1,VisibleToViewer=true,IsEnabled=true,StateRevision=overrideView.StateRevision,
                        Presentation=new YC.Domain.Presentation.SettlementPresentationDefinition{Label=label,Description=label}});
                    Call(expansion,"InvalidateSettlementAvailability");
                    Assert.That(strip.GetType().GetProperty("VisibleOptionCount").GetValue(strip),Is.EqualTo(1));
                    var binding=Get(strip,"binding");var entry=((System.Collections.IEnumerable)binding.GetType().GetProperty("Entries").GetValue(binding)).Cast<object>().Single();
                    long oldScope=(long)strip.GetType().GetProperty("ScopeToken").GetValue(strip);long identity=(long)entry.GetType().GetProperty("Identity").GetValue(entry);
                    empty=true;Call(expansion,"InvalidateSettlementAvailability");
                    Assert.That(strip.GetType().GetProperty("VisibleOptionCount").GetValue(strip),Is.EqualTo(0));
                    Assert.That(((UnityEngine.Component)Get(expansion,"settlementHost")).gameObject.activeSelf,Is.False);
                    Call(strip,"Use",oldScope,identity,owned.StateRevision,"native-ui-old");Assert.That(sends,Is.EqualTo(0));
                    empty=false;Call(expansion,"InvalidateSettlementAvailability");
                    Assert.That(strip.GetType().GetProperty("VisibleOptionCount").GetValue(strip),Is.EqualTo(1),"有效本人投影可在同一宿主重绑。");
                    Assert.That((long)strip.GetType().GetProperty("ScopeToken").GetValue(strip),Is.Not.EqualTo(oldScope));
                    Call(strip,"Use",oldScope,identity,owned.StateRevision,"native-ui-old");Assert.That(sends,Is.EqualTo(0));
                    File.WriteAllText(Path.Combine(fixture.Output,"许可投影与实际HUD接缝.json"),JsonConvert.SerializeObject(new {
                        HudSource=hudPath,GameId=owned.GameId,Viewer=owned.ViewerPlayerId,BeforeRevision=before.StateRevision,AfterRevision=owned.StateRevision,
                        EventOrder=order,Applications=applied,ActualHudRefreshes=refreshes,SameProjectionReused=true,EmptyClearsOldButtons=true,
                        RebindWorks=true,OldLeaseSends=sends,UiClickUsed=false,Boundary="真实许可和正式提交/Pump、生产结算栏刷新；空态UI描述探针不代表业务内容执行"},Formatting.Indented));
                }
                finally{UnityEngine.Object.DestroyImmediate(hudObject);}
            }
        }

        [Test]
        public void NativeDeterministicRecoveryRetiresPendingAndOldEpochCannotResolveIt()
        {
            using (var fixture = new SimulationDriverFixture(productionResults: true))
            {
                var command = fixture.Submit("native-unapplied-before-recovery");
                var proposal = fixture.Coordinator.ReadPendingLocalProposal();
                var sourceApplied=(Delegate)Get(fixture.Transport,"SimulationCommandApplied");
                var sourceRejected=(Delegate)Get(fixture.Transport,"SimulationProposalRejected");
                Call(fixture.Driver, "OnSimulationRejected", new SimulationProposalResultDto { GameId="异局", SimulationEpoch=1,MembershipVersion=1,Proposal=proposal });
                Assert.That(Get(fixture.Driver, "pendingSimulationCommand"), Is.Not.Null);
                Call(fixture.Transport, "RequestConfirmedRecovery");
                Assert.That(Get(fixture.Driver, "recoveringSimulation"), Is.True);
                fixture.Host.BeginConfirmedRecovery();
                Assert.That(fixture.Host.TryCreateConfirmedRecoveryCheckpoint(0,2,out var checkpoint,out var created), Is.True, created?.Reason);
                var notice=new SimulationRecoveryNoticeDto {GameId=checkpoint.Baseline.GameId,SimulationEpoch=2,SourceSimulationEpoch=1,MembershipVersion=1,
                    CheckpointId=checkpoint.Baseline.CheckpointId,ConfirmedGlobalSequence=0,MetadataDigest=checkpoint.Baseline.MetadataDigest,Phase=SimulationRecoveryPhase.Prepare,ResumeAllowed=true};
                var installer=new SimulationConfirmedRecoveryInstaller(fixture.Session,fixture.Registry,GameStateViewer.Player(1),fixture.Coordinator.SessionOptions);
                Assert.That(installer.TryPrepare(notice,SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint),out var prepared,out var preparedResult),Is.True,preparedResult?.Reason);
                Set(fixture.Transport,"recoveryInstaller",installer);Set(fixture.Transport,"preparedRecovery",prepared);
                Set(fixture.Transport,"recoveryNotice",notice);Set(fixture.Transport,"recoveryCommitIssued",true);
                Call(fixture.Transport,"InstallLocalRecovery");
                var target=prepared.Coordinator; target.CompleteConfirmedRecovery(true);
                Set(fixture.Transport,"recoveryFinished",true);
                // 专用安装后的真实运输事件消费者接缝；全员运输汇合由协议原件覆盖，本例不冒称联网恢复。
                ((Delegate)Get(fixture.Transport,"SimulationRecoveryCompleted")).DynamicInvoke(target);
                Assert.That(Get(fixture.Driver,"simulationEpoch"),Is.EqualTo(2));
                Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                Assert.That(Get(fixture.Driver,"localAppliedSequence"),Is.EqualTo(0));
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty,
                    "未执行提交退役必须沿准确拒绝解除Mobile等待，不能冒充成功或留下后继提交锁。");
                var sessions=(GameSession[])Get(fixture.Group,"Sessions");
                var registries=(YC.Domain.Effects.EffectRegistry[])Get(fixture.Group,"Registries");
                var coordinators=(DeterministicSimulationCoordinator[])Get(fixture.Group,"Coordinators");
                coordinators[1]=target;
                foreach(int index in new[]{0,2})
                {
                    int player=index==0?3:2;coordinators[index].BeginConfirmedRecovery();
                    var peerInstaller=new SimulationConfirmedRecoveryInstaller(sessions[index],registries[index],GameStateViewer.Player(player),coordinators[index].SessionOptions);
                    Assert.That(peerInstaller.TryPrepare(notice,SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint),out var peer,out var peerResult),Is.True,peerResult?.Reason);
                    Assert.That(peerInstaller.TryCommit(peer,out peerResult),Is.True,peerResult?.Reason);
                    coordinators[index]=peer.Coordinator;peer.Coordinator.CompleteConfirmedRecovery(true);
                }
                // 使用三份真实恢复副本的同前缀边界与许可；不手填允许操作的标志。
                typeof(ConfirmedSimulationRecoveryTests).GetMethod("Seal",Statics).Invoke(null,new[]{fixture.Group,(object)0L});
                Assert.That(target.ReadWindowPermissions().Any(p=>p.CanSubmitForLocalPlayer),Is.True);
                var next=fixture.SubmitFromOwnerView();
                Assert.That(next.CommandId,Is.Not.EqualTo(command.CommandId));
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.EqualTo(next.CommandId));
                Assert.That(target.ReadPendingLocalProposal().Command.CommandId,Is.EqualTo(next.CommandId));
                Assert.That(target.ReadPendingLocalProposal().SimulationEpoch,Is.EqualTo(2));
                int frames=fixture.QueuedFrameCount;
                var currentApplied=(Delegate)Get(fixture.Transport,"SimulationCommandApplied");
                var currentRejected=(Delegate)Get(fixture.Transport,"SimulationProposalRejected");
                var lateApplied=new SimulationCommandAppliedDto {GameId=fixture.Session.State.GameId,SimulationEpoch=1,MembershipVersion=1,
                    Command=GameCommandDto.FromCommand(next),GlobalCommandSequence=1,RuleResult=CommandResult.SuccessResult(null,"")};
                sourceApplied.DynamicInvoke(lateApplied);
                sourceRejected.DynamicInvoke(new SimulationProposalResultDto {GameId=fixture.Session.State.GameId,SimulationEpoch=1,MembershipVersion=1,
                    Proposal=proposal,Status=OperationAdmissionStatus.Rejected,ErrorCode=CommandErrorCode.StateRevisionMismatch,RequiresRefresh=true});
                lateApplied.GameId="异局";lateApplied.SimulationEpoch=2;currentApplied.DynamicInvoke(lateApplied);
                var oldIdentityProposal=new OperationCommandProposalDto {GameId=fixture.Session.State.GameId,SimulationEpoch=2,MembershipVersion=1,
                    Command=GameCommandDto.FromCommand(command),OperationWindowId=target.ReadPendingLocalProposal().OperationWindowId};
                currentRejected.DynamicInvoke(new SimulationProposalResultDto {GameId=fixture.Session.State.GameId,SimulationEpoch=2,MembershipVersion=1,
                    Proposal=oldIdentityProposal,Status=OperationAdmissionStatus.Rejected,ErrorCode=CommandErrorCode.StateRevisionMismatch,RequiresRefresh=true});
                ((Delegate)Get(fixture.Transport,"SimulationRecoveryCompleted")).DynamicInvoke(target);
                Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.EqualTo(next.CommandId));
                Assert.That(target.ReadPendingLocalProposal().Command.CommandId,Is.EqualTo(next.CommandId));
                Assert.That(((GameCommandDto)Get(fixture.Driver,"pendingSimulationCommand")).CommandId,Is.EqualTo(next.CommandId));
                Assert.That(fixture.QueuedFrameCount,Is.EqualTo(frames),"迟到/错局/重复不能再次发送任何命令。");
                Assert.That(File.Exists(Path.Combine(fixture.Output,"simulation-applied.jsonl")),Is.False);
                var accepted=coordinators[0].AdmitProposal(1,target.ReadPendingLocalProposal());
                Assert.That(accepted.Status,Is.EqualTo(OperationAdmissionStatus.Accepted));
                Assert.That(accepted.OrderedCommand.SimulationEpoch,Is.EqualTo(2));
                target.ReceiveProposalResult(accepted);target.ReceiveOrdered(accepted.OrderedCommand);
                SimulationCommandAppliedDto actualApplied=null;int applications=0;
                target.SimulationCommandApplied+=value=>{actualApplied=value;applications++;};
                coordinators[0].Pump(1);target.Pump(1);
                Assert.That(applications,Is.EqualTo(1));Assert.That(Get(fixture.Mobile,"pendingEffectCommandId"),Is.Null.Or.Empty);
                Assert.That(Get(fixture.Driver,"pendingSimulationCommand"),Is.Null);
                int afterRevision=fixture.Session.State.EffectRuntime.StateRevision;frames=fixture.QueuedFrameCount;
                currentApplied.DynamicInvoke(actualApplied);currentApplied.DynamicInvoke(actualApplied);
                Assert.That(applications,Is.EqualTo(1));Assert.That(fixture.Session.State.EffectRuntime.StateRevision,Is.EqualTo(afterRevision));
                Assert.That(fixture.QueuedFrameCount,Is.EqualTo(frames));
                Assert.That(File.ReadAllLines(Path.Combine(fixture.Output,"simulation-applied.jsonl")).Length,Is.EqualTo(1));
                File.WriteAllText(Path.Combine(fixture.Output,"恢复后真实提交接缝.json"),JsonConvert.SerializeObject(new {
                    OldCommandId=command.CommandId,NewCommandId=next.CommandId,GameId=fixture.Session.State.GameId,SourceEpoch=1,TargetEpoch=2,
                    Source="BuildCommandSubmission实际结果委托 → 准确恢复拒绝 → 当前本人View新决策 → SubmitPendingEffectCommand/Gateway → 本地真实执行",
                    ExplicitNewSubmission=true,NewPendingProtected=true,Applications=applications,UiClickUsed=false},Formatting.Indented));
            }
        }

        [Test]
        public void UnityDisableDetachesTransportEventsAndIgnoresLateCallbacks()
        {
            var names=new[]{"DeterministicSimulationReady","SimulationCommandApplied","CommandValidationPrefixAdvanced","OperationWindowPermissionChanged",
                "SimulationProposalRejected","SimulationProtocolFaulted","SimulationRecoveryStarted","SimulationRecoveryCompleted"};
            foreach(var trigger in new[]{"禁用","销毁"}) {
                var transportObject=new UnityEngine.GameObject("运输生命周期检查");var transport=transportObject.AddComponent(Target("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
                var driverObject=new UnityEngine.GameObject("驱动生命周期检查");var driver=driverObject.AddComponent(Target("YC.Presentation.NetworkGameValidationDriver"));
                ((UnityEngine.MonoBehaviour)driver).runInEditMode=true;
                try {
                    Call(driver,"AttachSimulationTransport",transport);
                    var callbacks=names.ToDictionary(name=>name,name=>(Delegate)Get(transport,name));
                    if(trigger=="禁用")driverObject.SetActive(false);else UnityEngine.Object.DestroyImmediate(driverObject);
                    Assert.That(Get(driver,"subscribed"),Is.Null);
                    foreach(var name in names){Assert.That(Get(transport,name),Is.Null,name);callbacks[name].DynamicInvoke(new object[]{null});}
                    Assert.That(Get(driver,"initialSnapshots"),Is.EqualTo(0));Assert.That(Get(driver,"boundGameId"),Is.Null);
                } finally {if(driverObject!=null)UnityEngine.Object.DestroyImmediate(driverObject);UnityEngine.Object.DestroyImmediate(transportObject);}
            }
        }
#endif

    }
}
