using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
#if !COVERED_ACKNOWLEDGMENT_OFFLINE
using UnityEngine.TestTools;
#endif
#if COVERED_ACKNOWLEDGMENT_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class CoveredCommandAcknowledgmentTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<GameObject> objects = new List<GameObject>();
        private static GameRuleConfiguration Configuration() => new GameRuleConfiguration
        { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 2, ContentPackHash = new string('a', 64) };
        private static GameStateView View(int revision) => new GameStateView
        {
            GameId = "covered-confirmation-contract", StateRevision = revision,
            RuleConfiguration = Configuration(), MapId = Configuration().MapId,
            ContentPackHash = Configuration().ContentPackHash, ViewerRole = GameStateViewerRole.Player,
            ViewerPlayerId = 2, LaunchPlayerOrder = new List<int> { 1, 2 },
            Phase = GamePhase.ActionRound1, Round = 1, CurrentPlayerId = 2,
            Players = new List<PlayerStateView> { new PlayerStateView { PlayerId = 1 }, new PlayerStateView { PlayerId = 2 } }
        };
        private static Type TransportType() => typeof(CoveredCommandAcknowledgmentTests).Assembly
            .GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport") ?? AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport")).First(type => type != null);
        private static void Set(object value, string field, object data) => value.GetType().GetField(field, Members).SetValue(value, data);
        private static object Get(object value, string field) => value.GetType().GetField(field, Members).GetValue(value);
        private sealed class Scenario
        {
            public object Transport;
            public GameSession Session;
            public AuthoritativeCommandDispatcher Dispatcher;
            public int Covered, Applied;
            public ConfirmedGameStateViewDto Notification;
            public bool HasCoveredEvent;
            public int Initial;
            public readonly List<Dictionary<string, object>> Diagnostics = new List<Dictionary<string, object>>();
            public void Observe<T>(T diagnostic)
            {
                var record = diagnostic.GetType().GetProperties().ToDictionary(property => property.Name,
                    property => property.GetValue(diagnostic));
                record.Add("AtEventState", Summary(this));
                Diagnostics.Add(record);
            }
            public void RejectObserver<T>(T diagnostic) => throw new InvalidOperationException("本地诊断记录器测试异常");
        }
        private Scenario Create(string pending = "pending-action", bool synchronized = true)
        {
            var session = new GameSession(new GameState
            { RuleConfiguration = Configuration(), MapId = Configuration().MapId, ContentPackHash = Configuration().ContentPackHash,
                Players = { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } });
            var dispatcher = new AuthoritativeCommandDispatcher(session, true);
            var initial = new InitialGameStateViewDto { NextConfirmedSequence = 6, View = View(12) };
            Assert.That(dispatcher.ApplyInitialStateViewSynchronization(initial).Succeeded, Is.True);
            if (!synchronized) Set(dispatcher, "initialStateSynchronized", false);
            object transport;
#if COVERED_ACKNOWLEDGMENT_OFFLINE
            // 仅分配真实类型的托管部分，避免离线CLR调用Unity组件构造；不替换受测方法。
            transport = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(TransportType());
            foreach (string field in new[] { "bindings", "pendingLocalBindings", "clientConnectionTokens", "viewAssembler" })
                Set(transport, field, Activator.CreateInstance(TransportType().GetField(field, Members).FieldType));
#else
            var gameObject = new GameObject("已覆盖确认专属检查");
            objects.Add(gameObject);
            transport = gameObject.AddComponent(TransportType());
#endif
            Set(transport, "session", session); Set(transport, "dispatcher", dispatcher);
            Set(transport, "localPlayerId", 2); Set(transport, "mode", LaunchMode.Client);
            Set(transport, "clientConnectionToken", "covered-test-connection");
            SetPending(transport, pending, session.View.GameId);
            Set(transport, "awaitingInitialState", !synchronized);
            var scenario = new Scenario { Transport = transport, Session = session, Dispatcher = dispatcher };
            TransportType().GetEvent("ConfirmedStateViewApplied").AddEventHandler(transport,
                new Action<ConfirmedGameStateViewDto>(dto => scenario.Applied++));
            var coveredEvent = TransportType().GetEvent("CoveredCommandAcknowledged");
            scenario.HasCoveredEvent = coveredEvent != null;
            if (coveredEvent != null) coveredEvent.AddEventHandler(transport,
                new Action<ConfirmedGameStateViewDto>(dto => { scenario.Covered++; scenario.Notification = dto; }));
            return scenario;
        }
        private static void SetPending(object transport, string commandId, string gameId)
        {
            Set(transport, "pendingCommandId", commandId);
            Set(transport, "pendingGameId", string.IsNullOrEmpty(commandId) ? null : gameId);
            Set(transport, "pendingCommand", string.IsNullOrEmpty(commandId) ? null :
                new GameCommandDto { CommandId = commandId, PlayerId = 2 });
        }
        private static ConfirmedGameStateViewDto Confirmation(string commandId = "pending-action", int sequence = 4) =>
            new ConfirmedGameStateViewDto
            { Sequence = sequence, View = View(sequence >= 6 ? 13 : 11),
                Command = commandId == null ? null : new GameCommandDto { CommandId = commandId, PlayerId = 2 } };
        private static void Deliver(Scenario scenario, ConfirmedGameStateViewDto dto)
        {
            string json = JsonConvert.SerializeObject(dto);
            Assert.That(GameStateViewProtocol.TryReadConfirmed(json, scenario.Session.State.RuleConfiguration,
                scenario.Session.State.LaunchPlayerOrder, out _), Is.True, "夹具必须先通过原正式协议校验。");
            var message = Activator.CreateInstance(TransportType().Assembly.GetType("YC.Infrastructure.Multiplayer.AcceptedCommandMessage"));
            Set(message, "Json", json);
            Set(message, "ConnectionToken", Get(scenario.Transport, "clientConnectionToken"));
            try { TransportType().GetMethod("OnAccepted", Members).Invoke(scenario.Transport, new[] { message }); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [TearDown] public void Clear()
        {
            foreach (var gameObject in objects) if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
            objects.Clear();
        }

        [Test]
        public void CoveredPendingCommandNotifiesOnceWithoutReapplyingOldView()
        {
            var scenario = Create();
            var currentView = scenario.Session.View;
            string before = JsonConvert.SerializeObject(scenario.Session.State);
            Deliver(scenario, Confirmation());
            Console.WriteLine("覆盖确认入口：pending=" + (Get(scenario.Transport, "pendingCommandId") ?? "空") +
                "，正常通知=" + scenario.Applied + "，覆盖通知=" + scenario.Covered +
                "，当前revision=" + scenario.Session.View.StateRevision + "，next=" + scenario.Dispatcher.NextExpectedConfirmedSequence);
            Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.Null);
            Assert.That(scenario.Applied, Is.EqualTo(0));
            Assert.That(scenario.Covered, Is.EqualTo(1), "覆盖状态后的准确受理结果应只通知一次。");
            Assert.That(scenario.HasCoveredEvent, Is.True);
            Assert.That(scenario.Notification.Command.CommandId, Is.EqualTo("pending-action"));
            Assert.That(scenario.Notification.Sequence, Is.EqualTo(4));
            Assert.That(scenario.Notification.View.StateRevision, Is.EqualTo(12));
            Assert.That(scenario.Notification.View, Is.SameAs(currentView));
            Assert.That(scenario.Session.View, Is.SameAs(currentView));
            Assert.That(scenario.Session.View.StateRevision, Is.EqualTo(12));
            Assert.That(scenario.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(6));
            Assert.That(Get(scenario.Transport, "awaitingInitialState"), Is.False);
            Assert.That(JsonConvert.SerializeObject(scenario.Session.State), Is.EqualTo(before));
            Deliver(scenario, Confirmation());
            Assert.That(scenario.Covered, Is.EqualTo(1));
            Assert.That(scenario.Applied, Is.EqualTo(0));
            Assert.That(scenario.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(6));
        }

        [Test]
        public void UnknownEmptyMissingAndUnsynchronizedCoveredConfirmationsDoNotNotify()
        {
            foreach (var pair in new[] { new[] { "pending-action", "other-action" },
                new[] { "pending-action", "Pending-action" }, new[] { "pending-action", "" },
                new[] { "pending-action", (string)null }, new[] { (string)null, "pending-action" }, new[] { "", "pending-action" } })
            {
                var scenario = Create(pair[0]);
                Set(scenario.Transport, "awaitingInitialState", true);
                var view = scenario.Session.View;
                Deliver(scenario, Confirmation(pair[1]));
                Assert.That(scenario.Covered, Is.EqualTo(0));
                Assert.That(scenario.Applied, Is.EqualTo(0));
                Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.EqualTo(pair[0]));
                Assert.That(Get(scenario.Transport, "awaitingInitialState"), Is.True);
                Assert.That(scenario.Session.View, Is.SameAs(view));
                Assert.That(scenario.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(6));
            }
            var unsynchronized = Create(synchronized: false);
            Deliver(unsynchronized, Confirmation());
            Assert.That(unsynchronized.Covered, Is.EqualTo(0));
            Assert.That(unsynchronized.Applied, Is.EqualTo(0));
            Assert.That(Get(unsynchronized.Transport, "pendingCommandId"), Is.EqualTo("pending-action"),
                "未完成同步时，准确受理结果也不能提前清除pending。");
            Assert.That(Get(unsynchronized.Transport, "awaitingInitialState"), Is.True);
            Assert.That(unsynchronized.Dispatcher.IsInitialStateSynchronized, Is.False);
        }

        [Test]
        public void OrdinaryConfirmationUsesOnlyTheOriginalAppliedEvent()
        {
            var scenario = Create();
            Deliver(scenario, Confirmation(sequence: 6));
            Assert.That(scenario.Covered, Is.EqualTo(0));
            Assert.That(scenario.Applied, Is.EqualTo(1));
            Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.Null);
            Assert.That(scenario.Session.View.StateRevision, Is.EqualTo(13));
            Assert.That(scenario.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(7));
            Deliver(scenario, Confirmation(sequence: 6));
            Assert.That(scenario.Covered, Is.EqualTo(0));
            Assert.That(scenario.Applied, Is.EqualTo(1));
            Assert.That(scenario.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(7));
        }

        [Test]
        public void ProtocolStillRejectsWrongVersionConfigurationAndPlayerOrder()
        {
            foreach (var change in new Action<ConfirmedGameStateViewDto>[]
            { dto => dto.ProtocolVersion = -1, dto => dto.View.RuleConfiguration.ContentPackHash = "wrong-hash",
                dto => dto.View.LaunchPlayerOrder.Reverse() })
            {
                var scenario = Create();
                var dto = Confirmation(); change(dto);
                Assert.That(GameStateViewProtocol.TryReadConfirmed(JsonConvert.SerializeObject(dto),
                    scenario.Session.State.RuleConfiguration, scenario.Session.State.LaunchPlayerOrder, out _), Is.False);
                Assert.That(scenario.Covered, Is.EqualTo(0));
                Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.EqualTo("pending-action"));
            }
            // 此方法只验证未改的正式解析器；不冒称离线CLR执行了Unity日志/断连分支。
        }

        private static object Summary(Scenario scenario) => new
        {
            GameId = scenario.Session.View?.GameId, Revision = scenario.Session.View?.StateRevision,
            Pending = Get(scenario.Transport, "pendingCommandId"),
            AwaitingInitial = Get(scenario.Transport, "awaitingInitialState"),
            Next = scenario.Dispatcher.NextExpectedConfirmedSequence,
            Synchronized = scenario.Dispatcher.IsInitialStateSynchronized,
            NormalNotifications = scenario.Applied, CoveredNotifications = scenario.Covered,
            InitialNotifications = scenario.Initial
        };
        private static Delegate Observer(Scenario scenario, string method)
        {
            var eventType = TransportType().GetEvent("CommandConfirmationObserved").EventHandlerType;
            return Delegate.CreateDelegate(eventType, scenario, typeof(Scenario).GetMethod(method)
                .MakeGenericMethod(eventType.GetGenericArguments()[0]));
        }
        private static void Observe(Scenario scenario)
        {
            TransportType().GetEvent("CommandConfirmationObserved").AddEventHandler(scenario.Transport,
                Observer(scenario, "Observe"));
            TransportType().GetEvent("InitialStateViewApplied").AddEventHandler(scenario.Transport,
                new Action<InitialGameStateViewDto>(dto => scenario.Initial++));
        }
        private static void Invoke(Scenario scenario, string method, params object[] args)
        {
            try { TransportType().GetMethod(method, Members).Invoke(scenario.Transport, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static bool ClientConnected() => (bool)Assembly.Load(TransportType().Assembly.GetReferencedAssemblies()
            .First(reference => reference.Name == "Mirror")).GetType("Mirror.NetworkClient")
            .GetProperty("isConnected", BindingFlags.Public | BindingFlags.Static).GetValue(null);
        private static void DeliverInitial(Scenario scenario, int revision = 14, int next = 8, string gameId = null)
        {
            var view = View(revision);
            if (gameId != null) view.GameId = gameId;
            var dto = new InitialGameStateViewDto { NextConfirmedSequence = next, View = view };
            string json = JsonConvert.SerializeObject(dto);
            Assert.That(GameStateViewProtocol.TryReadInitial(json, scenario.Session.State.RuleConfiguration,
                scenario.Session.State.LaunchPlayerOrder, out _), Is.True);
            var message = Activator.CreateInstance(TransportType().Assembly.GetType("YC.Infrastructure.Multiplayer.InitialStateMessage"));
            Set(message, "Json", json); Set(message, "ProtocolVersion", SteamLobbyPolicy.ProtocolVersion);
            Set(message, "ConnectionToken", Get(scenario.Transport, "clientConnectionToken"));
            Invoke(scenario, "OnInitialState", message);
        }

        [Test]
        public void SequenceGapAcceptedThenInitialSynchronizationRecordsNotificationGap()
        {
            // 不建连接或桩。直接递送合法DTO；真实RequestInitialState按未连接条件返回。
            // 离线CLR若仍不能解析该方法中的Unity native调用，保留失败，转原生检查。
            foreach (bool synchronized in new[] { true, false })
            {
                var scenario = Create(synchronized: synchronized);
                Observe(scenario);
                bool connected = ClientConnected();
                Assert.That(connected, Is.False, "此原生定向检查不建立网络连接。");
                var beforeView = scenario.Session.View;
                string beforeState = JsonConvert.SerializeObject(scenario.Session.State);
                Deliver(scenario, Confirmation(sequence: synchronized ? 7 : 6));
                var afterAccepted = Summary(scenario);
                Assert.That(scenario.Diagnostics.Select(record => record["Branch"]).ToArray(),
                    Is.EqualTo(new[] { "accepted_received", "accepted_apply_result" }));
                var entry = scenario.Diagnostics[0]; var apply = scenario.Diagnostics[1];
                Assert.That(entry["GameId"], Is.EqualTo("covered-confirmation-contract"));
                Assert.That(entry["ReceivedGameId"], Is.EqualTo(entry["GameId"]));
                Assert.That(entry["PendingCommandId"], Is.EqualTo("pending-action"));
                Assert.That(entry["ExactPendingMatched"], Is.EqualTo(true));
                Assert.That(entry["IsInitialStateSynchronized"], Is.EqualTo(synchronized));
                Assert.That(entry["ApplyAttempted"], Is.EqualTo(false));
                Assert.That(entry["ApplySucceeded"], Is.Null);
                Assert.That(entry["ErrorCode"], Is.Null);
                Assert.That(apply["PendingCommandId"], Is.EqualTo("pending-action"));
                Assert.That(apply["ExactPendingMatched"], Is.EqualTo(true));
                Assert.That(apply["ApplyAttempted"], Is.EqualTo(true));
                Assert.That(apply["ApplySucceeded"], Is.EqualTo(false));
                Assert.That(apply["ErrorCode"], Is.EqualTo("UnknownCommand"));
                Assert.That(apply["NextExpectedSequence"], Is.EqualTo(6));
                Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.EqualTo("pending-action"));
                Assert.That(Get(scenario.Transport, "awaitingInitialState"), Is.True);
                Assert.That(scenario.Session.View, Is.SameAs(beforeView));
                Assert.That(JsonConvert.SerializeObject(scenario.Session.State), Is.EqualTo(beforeState));
                Assert.That(scenario.Applied, Is.EqualTo(0)); Assert.That(scenario.Covered, Is.EqualTo(0));
                // 必须走运输OnInitialState，不能用dispatcher直接补同步冒充完整接收链。
                DeliverInitial(scenario);
                Assert.That(scenario.Diagnostics.Count, Is.EqualTo(4));
                var initial = scenario.Diagnostics[2];
                Assert.That(initial["Branch"], Is.EqualTo("initial_apply_result"));
                Assert.That(initial["ApplyAttempted"], Is.EqualTo(true));
                Assert.That(initial["ApplySucceeded"], Is.EqualTo(true));
                Assert.That(initial["ErrorCode"], Is.EqualTo("None"));
                Assert.That(initial["PendingCommandId"], Is.EqualTo("pending-action"));
                Assert.That(initial["CommandId"], Is.Null); Assert.That(initial["Sequence"], Is.Null);
                Assert.That(initial["ExactPendingMatched"], Is.Null);
                Assert.That(initial["NextExpectedSequence"], Is.EqualTo(8));
                Assert.That(initial["IsInitialStateSynchronized"], Is.EqualTo(true));
                Assert.That(scenario.Initial, Is.EqualTo(1));
                Assert.That(scenario.Session.View.StateRevision, Is.EqualTo(14));
                Assert.That(scenario.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(8));
                Assert.That(Get(scenario.Transport, "awaitingInitialState"), Is.False);
                Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.Null);
                Assert.That(scenario.Applied, Is.EqualTo(0)); Assert.That(scenario.Covered, Is.EqualTo(1));
                var recovered = scenario.Diagnostics[3];
                Assert.That(recovered["Branch"], Is.EqualTo("command_result_accepted"));
                Assert.That(recovered["CommandId"], Is.EqualTo("pending-action"));
                Assert.That(recovered["PendingCommandId"], Is.EqualTo("pending-action"));
                Assert.That(recovered["Sequence"], Is.EqualTo(synchronized ? 7 : 6));
                Assert.That(scenario.Notification.View, Is.SameAs(scenario.Session.View));
                Deliver(scenario, Confirmation(sequence: synchronized ? 7 : 6));
                Assert.That(scenario.Covered, Is.EqualTo(1));
                Assert.That(scenario.Applied, Is.EqualTo(0));
                Assert.That(scenario.Session.View.StateRevision, Is.EqualTo(14));
                Assert.That(scenario.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(8));
                Console.WriteLine("确认诊断证据：" + JsonConvert.SerializeObject(new
                { Case = synchronized ? "序列缺口完整接收链" : "未同步完整接收链", OnAcceptedCompleted = true,
                    OnInitialCompleted = true, ClientConnected = connected, ActualNetworkRequestSent = false,
                    AfterAccepted = afterAccepted, AfterInitial = Summary(scenario), Observations = scenario.Diagnostics }));
            }
        }

        [Test]
        public void ConfirmationDiagnosticsPreserveSuccessfulAndCoveredAcknowledgments()
        {
            var diagnosticType = TransportType().GetEvent("CommandConfirmationObserved").EventHandlerType.GetGenericArguments()[0];
            string[] fields = { "Branch", "GameId", "ReceivedGameId", "CommandId", "PendingCommandId", "Sequence",
                "NextExpectedSequence", "IsInitialStateSynchronized", "ExactPendingMatched", "ApplyAttempted", "ApplySucceeded", "ErrorCode" };
            Assert.That(diagnosticType.GetProperties().Select(property => property.Name).OrderBy(name => name).ToArray(),
                Is.EqualTo(fields.OrderBy(name => name).ToArray()));
            Assert.That(diagnosticType.GetProperties().All(property => property.SetMethod == null), Is.True);
            Assert.That(diagnosticType.GetInterfaces().Any(type => type.FullName == "Mirror.NetworkMessage"), Is.False);
            var evidence = new List<object>();
            var normal = Create(); Observe(normal);
            Deliver(normal, Confirmation(sequence: 6)); Deliver(normal, Confirmation(sequence: 6));
            Assert.That(normal.Applied, Is.EqualTo(1)); Assert.That(normal.Covered, Is.EqualTo(0));
            Assert.That(normal.Diagnostics.Select(record => record["Branch"]).ToArray(),
                Is.EqualTo(new[] { "accepted_received", "accepted_apply_result", "accepted_received" }));
            Assert.That(normal.Diagnostics[1]["ApplySucceeded"], Is.EqualTo(true));
            Assert.That(normal.Diagnostics[1]["NextExpectedSequence"], Is.EqualTo(7));
            Assert.That(normal.Diagnostics[2]["ExactPendingMatched"], Is.EqualTo(false));
            Assert.That(normal.Session.View.StateRevision, Is.EqualTo(13));
            evidence.Add(new { Case = "正常成功及重复", Observations = normal.Diagnostics, Final = Summary(normal) });
            var covered = Create(); Observe(covered);
            var coveredView = covered.Session.View;
            Deliver(covered, Confirmation()); Deliver(covered, Confirmation());
            Assert.That(covered.Applied, Is.EqualTo(0)); Assert.That(covered.Covered, Is.EqualTo(1));
            Assert.That(covered.Diagnostics.Select(record => record["Branch"]).ToArray(),
                Is.EqualTo(new[] { "accepted_received", "command_result_accepted", "accepted_received" }));
            Assert.That(covered.Diagnostics[0]["ExactPendingMatched"], Is.EqualTo(true));
            Assert.That(covered.Diagnostics[2]["ExactPendingMatched"], Is.EqualTo(false));
            Assert.That(covered.Session.View, Is.SameAs(coveredView));
            Assert.That(covered.Dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(6));
            evidence.Add(new { Case = "已覆盖成功及重复", Observations = covered.Diagnostics, Final = Summary(covered) });
            var wrong = Create(); Observe(wrong);
            var foreign = Confirmation("other-action"); foreign.View.GameId = "different-game";
            Deliver(wrong, foreign);
            Assert.That(wrong.Diagnostics.Count, Is.EqualTo(0), "错局消息应在消费前被隔离。");
            Assert.That(Get(wrong.Transport, "pendingCommandId"), Is.EqualTo("pending-action"));
#if !COVERED_ACKNOWLEDGMENT_OFFLINE
            LogAssert.Expect(LogType.Error, "权威开局地图或人数与当前房间不一致，已拒绝同步。");
#endif
            DeliverInitial(wrong, gameId: "different-game");
            Assert.That(wrong.Diagnostics.Count, Is.EqualTo(0));
            Assert.That(wrong.Initial, Is.EqualTo(0));
            Assert.That(wrong.Session.View.GameId, Is.EqualTo("covered-confirmation-contract"));
            Assert.That(Get(wrong.Transport, "pendingCommandId"), Is.EqualTo("pending-action"));
            Invoke(wrong, "OnLocalClientConnected");
            Assert.That(wrong.Diagnostics[0]["Branch"], Is.EqualTo("connection_pending_retained"));
            Assert.That(wrong.Diagnostics[0]["PendingCommandId"], Is.EqualTo("pending-action"));
            Assert.That(wrong.Diagnostics[0]["ApplyAttempted"], Is.EqualTo(false));
            Assert.That(wrong.Diagnostics[0]["ApplySucceeded"], Is.Null);
            Assert.That(Get(wrong.Transport, "pendingCommandId"), Is.EqualTo("pending-action"));
            Assert.That(Get(wrong.Transport, "awaitingInitialState"), Is.True);
            Assert.That(wrong.Applied, Is.EqualTo(0)); Assert.That(wrong.Covered, Is.EqualTo(0));
            SetPending(wrong.Transport, "unknown-before-shutdown", wrong.Session.View.GameId);
            Invoke(wrong, "Shutdown");
            Assert.That(wrong.Diagnostics[1]["Branch"], Is.EqualTo("shutdown_pending_cleared"));
            Assert.That(wrong.Diagnostics[1]["PendingCommandId"], Is.EqualTo("unknown-before-shutdown"));
            Assert.That(wrong.Diagnostics[1]["GameId"], Is.EqualTo("covered-confirmation-contract"));
            Assert.That(Get(wrong.Transport, "session"), Is.Null);
            Assert.That(Get(wrong.Transport, "pendingCommandId"), Is.Null);
            Assert.That(Get(wrong.Transport, "awaitingInitialState"), Is.False);
            evidence.Add(new { Case = "错局隔离、重连保留及真正关闭清等待", Observations = wrong.Diagnostics });
#if !COVERED_ACKNOWLEDGMENT_OFFLINE
            // 原生环境验证某个记录器异常既留日志，也不阻断后续观察者及业务处理。
            var isolated = Create();
            TransportType().GetEvent("CommandConfirmationObserved").AddEventHandler(isolated.Transport,
                Observer(isolated, "RejectObserver"));
            Observe(isolated);
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("本地诊断记录器测试异常"));
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("本地诊断记录器测试异常"));
            Deliver(isolated, Confirmation(sequence: 6));
            Assert.That(isolated.Diagnostics.Count, Is.EqualTo(2));
            Assert.That(isolated.Applied, Is.EqualTo(1)); Assert.That(isolated.Covered, Is.EqualTo(0));
            Assert.That(Get(isolated.Transport, "pendingCommandId"), Is.Null);
            evidence.Add(new { Case = "诊断记录器异常隔离", Observations = isolated.Diagnostics, Final = Summary(isolated) });
#endif
            // 不订阅诊断时仍使用原业务路径，不需要复制任何状态。
            var unobserved = Create(); Deliver(unobserved, Confirmation(sequence: 6));
            Assert.That(unobserved.Applied, Is.EqualTo(1)); Assert.That(unobserved.Covered, Is.EqualTo(0));
            Console.WriteLine("确认诊断证据：" + JsonConvert.SerializeObject(evidence));
        }
    }
}
