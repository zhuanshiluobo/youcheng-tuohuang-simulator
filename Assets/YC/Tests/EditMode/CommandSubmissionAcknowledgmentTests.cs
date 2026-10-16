using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
#if COMMAND_SUBMISSION_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class CommandSubmissionAcknowledgmentTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<GameObject> objects = new List<GameObject>();
        private Scenario current;
        private static Type Production(string name) => Assembly.Load("Assembly-CSharp").GetType(name, true);
        private static Type ControllerType()
        {
#if COMMAND_SUBMISSION_OFFLINE
            return Assembly.Load("YC.CommandSubmission.Checks").GetType("YC.Presentation.CommandSubmissionController", true);
#else
            return Production("YC.Presentation.CommandSubmissionController");
#endif
        }
        private static Type AdapterType()
        {
#if COMMAND_SUBMISSION_OFFLINE
            return Assembly.Load("YC.CommandSubmission.Checks").GetType("YC.Presentation.MobileCityGameplayAdapter", true);
#else
            return Production("YC.Presentation.MobileCityGameplayAdapter");
#endif
        }
        private static Type MirrorType(string name) => Assembly.Load("Mirror").GetType("Mirror." + name, true);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Members).SetValue(target, value);
        private static object Get(object target, string field) => target.GetType().GetField(field, Members).GetValue(target);
        private static object Call(object target, string method, params object[] args)
        {
            try { return target.GetType().GetMethod(method, Members).Invoke(target, args); }
            catch (TargetInvocationException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
                throw;
            }
        }
        private static T Callback<T>(object target, string method) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), target, target.GetType().GetMethod(method, Members));
        private object Component(Type type)
        {
#if COMMAND_SUBMISSION_OFFLINE
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
#else
            var gameObject = new GameObject("准确提交结清检查");
            gameObject.SetActive(false);
            objects.Add(gameObject);
            return gameObject.AddComponent(type);
#endif
        }
        private sealed class RequestHandler : IGameCommandHandler
        {
            public InteractionRequestProjection Request;
            public int Calls;
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.AnswerInteraction;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                if (command.PlayerId != Request.AnsweringPlayerId ||
                    !command.Parameters.TryGetValue("interactionId", out var id) || id != Request.InteractionId ||
                    !command.Parameters.TryGetValue("expectedRevision", out var token) || token != state.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture))
                    return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.StateRevisionMismatch, "保持准确请求与严格版本。"));
                if (command.OptionIds.Count != 1 || !Request.CandidateIds.Contains(command.OptionIds[0]))
                    return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.UnknownCommand, "候选必须属于当前盖牌请求。"));
                Calls++;
                state.FindPlayer(4).Resources.GoldVoucher += 7;
                return CommandResult.SuccessResult(new List<GameEvent>(), "代表性处理器只执行一次。");
            }
        }
        private sealed class Scenario
        {
            public object Transport, Mobile, Controller, ServerConnection;
            public GameSession Authority, Client;
            public AuthoritativeCommandDispatcher Host, Dispatcher;
            public RequestHandler Handler;
            public InteractionRequestProjection OldRequest;
            public GameCommand OldCommand;
            public int Refreshes, CitySettled;
            public object PreviousConnection, PreviousConnectState, PreviousActiveTransport;
            public bool ConnectionCaptured;
            public bool ActiveTransportCaptured;
        }
        private static InteractionRequestProjection Request(string id, int revision) => new InteractionRequestProjection
        {
            InteractionId = id, AnsweringPlayerId = 4, StateRevision = revision,
            InteractionTypeId = "character.cover.card", PromptKey = "character.cover.choose",
            SourceNodeId = id + "-effect", OwnerEffectId = id + "-effect", Visibility = "owner",
            CandidateSetId = id + "-candidates", CandidateSetVersion = 1, AnswerSchema = "candidate_id",
            Status = "open", VisibleToViewer = true, MinSelections = 1, MaxSelections = 1,
            CandidateIds = new List<string> { "card-a", "card-b", "card-c", "card-d", "card-e" }
        };
        private Scenario Create()
        {
            var state = new GameState { GameId = "submission-covered-contract", MapId = StaticMapDefinitions.FourPlayerMapId,
                ContentPackHash = new string('a', 64), Phase = GamePhase.CharacterCover, Round = 6,
                CurrentPlayerId = 4, StartPlayerId = 1, LaunchPlayerOrder = new List<int> { 1, 4 },
                RuleConfiguration = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId,
                    PlayerCount = 2, ContentPackHash = new string('a', 64) },
                Players = { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 4, Resources = new ResourceSet { GoldVoucher = 5 } } } };
            var scenario = current = new Scenario { Authority = new GameSession(state), OldRequest = Request("old-covered", 0) };
            scenario.Handler = new RequestHandler { Request = scenario.OldRequest };
            scenario.Authority.RegisterHandler(scenario.Handler);
            scenario.Host = new AuthoritativeCommandDispatcher(scenario.Authority);
            scenario.Host.RegisterClientPlayer(42, 4);
            scenario.Client = new GameSession(GameStateCloneService.DeepClone(state));
            scenario.Dispatcher = new AuthoritativeCommandDispatcher(scenario.Client, true);
            Assert.That(scenario.Dispatcher.ApplyInitialStateViewSynchronization(scenario.Host.CreateInitialStateViewSynchronization(4)).Succeeded, Is.True);
            scenario.Transport = Component(Production("YC.Infrastructure.Multiplayer.MirrorCommandTransport"));
#if COMMAND_SUBMISSION_OFFLINE
            foreach (string field in new[] { "bindings", "pendingLocalBindings", "clientConnectionTokens", "viewAssembler" })
                Set(scenario.Transport, field, Activator.CreateInstance(scenario.Transport.GetType().GetField(field, Members).FieldType));
#endif
            Set(scenario.Transport, "session", scenario.Client); Set(scenario.Transport, "dispatcher", scenario.Dispatcher);
            Set(scenario.Transport, "mode", LaunchMode.Client); Set(scenario.Transport, "localPlayerId", 4);
            Set(scenario.Transport, "initialized", true); Set(scenario.Transport, "clientConnectionToken", "submission-current-connection");
            scenario.Mobile = Component(Production("YC.Presentation.MobileCityInteractionController"));
            var hud = Component(Production("YC.Presentation.GameplayInteractionHudView"));
            var expansion = Component(Production("YC.Presentation.GameplayExpansionUi"));
            Set(expansion, "pendingSubmissions", Activator.CreateInstance(expansion.GetType().GetField("pendingSubmissions", Members).FieldType));
            Set(hud, "expansionUi", expansion);
            Set(scenario.Mobile, "session", scenario.Client); Set(scenario.Mobile, "localPlayerId", 4);
            Set(scenario.Mobile, "gameplayInteractionHud", hud); Set(scenario.Mobile, "pendingEffectCommandId", string.Empty);
            Set(scenario.Mobile, "coverIntent", new InteractionCommandIntent());
#if !COMMAND_SUBMISSION_OFFLINE
            var frame = Component(Production("YC.Presentation.GameplayHudFrame"));
            var panel = Component(Production("YC.Presentation.ActionPanelView"));
            var buttonType = Assembly.Load("UnityEngine.UI").GetType("UnityEngine.UI.Button", true);
            Set(frame, "endActionButton", Component(buttonType)); Set(panel, "endRoundButton", Component(buttonType));
            Set(hud, "frame", frame); Set(hud, "actionPanelView", panel);
            var activeTransport = MirrorType("Transport").GetField("active", Statics);
            scenario.PreviousActiveTransport = activeTransport.GetValue(null);
            scenario.ActiveTransportCaptured = true;
            activeTransport.SetValue(null, Component(Assembly.Load("Mirror.Transports").GetType("Mirror.TelepathyTransport", true)));
#endif
            scenario.Controller = Activator.CreateInstance(ControllerType(), new object[] { scenario.Client, null, 4, null,
                new Action(() => scenario.Refreshes++), new Action<string>(_ => { }),
                Callback<Action<string>>(scenario.Mobile, "NotifyPendingEffectSettled"),
                new Action<string, bool>((_, accepted) => { if (accepted) scenario.CitySettled++; }),
                Callback<Action<string, string, bool, CommandErrorCode>>(scenario.Mobile, "NotifyCoverCommandResolved") });
            Call(scenario.Controller, "AttachCommandTransport", scenario.Transport);
            scenario.OldCommand = EffectInteractionCommands.Answer(scenario.OldRequest, 4, new[] { "card-a" }, false);
            Assert.That(((InteractionCommandIntent)Get(scenario.Mobile, "coverIntent")).Begin(state.GameId,
                scenario.OldRequest, scenario.OldCommand, true), Is.True);
            Set(scenario.Mobile, "submittedCoverGameId", state.GameId); Set(scenario.Mobile, "submittedCoverCommandId", scenario.OldCommand.CommandId);
            Set(scenario.Mobile, "coverSubmissionInFlight", true);
            return scenario;
        }
        private static void BindRealLocalConnection(Scenario scenario)
        {
            var client = MirrorType("NetworkClient");
            Assert.That(client.GetProperty("isConnected", Statics).GetValue(null), Is.EqualTo(false), "检查不接管已有连接。");
            Assert.That(MirrorType("NetworkServer").GetProperty("active", Statics).GetValue(null), Is.EqualTo(false));
            scenario.PreviousConnection = client.GetProperty("connection", Statics).GetValue(null);
            scenario.PreviousConnectState = client.GetField("connectState", Statics).GetValue(null);
            scenario.ConnectionCaptured = true;
            var connection = Activator.CreateInstance(MirrorType("LocalConnectionToServer"));
            scenario.ServerConnection = Activator.CreateInstance(MirrorType("LocalConnectionToClient"));
            Set(connection, "connectionToClient", scenario.ServerConnection); Set(scenario.ServerConnection, "connectionToServer", connection);
            client.GetProperty("connection", Statics).SetValue(null, connection);
            client.GetField("connectState", Statics).SetValue(null, Enum.Parse(MirrorType("ConnectState"), "Connected"));
        }
        private static int QueueCount(Scenario scenario)
        {
            var queue = Get(scenario.ServerConnection, "queue");
            return (int)queue.GetType().GetProperty("Count").GetValue(queue);
        }
        private static GameCommandDto ReadRealQueuedSubmission(Scenario scenario)
        {
            var queue = Get(scenario.ServerConnection, "queue");
            var writer = queue.GetType().GetMethod("Dequeue").Invoke(queue, null);
            var segment = (ArraySegment<byte>)writer.GetType().GetMethod("ToArraySegment").Invoke(writer, null);
            var reader = Activator.CreateInstance(MirrorType("NetworkReader"), new object[] { segment });
            var extensions = MirrorType("NetworkReaderExtensions");
            ushort id = (ushort)extensions.GetMethod("ReadUShort", Statics).Invoke(null, new[] { reader });
            var messageType = Production("YC.Infrastructure.Multiplayer.SubmitCommandMessage");
            ushort expected = (ushort)MirrorType("NetworkMessages").GetMethod("GetId", Statics).MakeGenericMethod(messageType).Invoke(null, null);
            Assert.That(id, Is.EqualTo(expected), "检查实际Mirror本地连接队列中的Submit消息。");
            string json = (string)extensions.GetMethod("ReadString", Statics).Invoke(null, new[] { reader });
            ((IDisposable)writer).Dispose();
            return JsonConvert.DeserializeObject<GameCommandDto>(json);
        }
        private static void BindOriginalGateway(Scenario scenario)
        {
            var factory = typeof(CommandSubmissionAcknowledgmentTests).GetMethod("ControllerFactory", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(ControllerType()).Invoke(null, new[] { scenario.Controller });
            var adapter = Activator.CreateInstance(AdapterType(), new object[] { new Func<GameState>(() => scenario.Client.State),
                new Func<int>(() => 4), new Func<bool>(() => false), new Action<int>(_ => { }), factory, new Action(() => { }) });
            Set(scenario.Mobile, "commandGateway", new CommandGateway((IGameCommandPort)adapter));
        }
        private static Func<T> ControllerFactory<T>(object controller) => () => (T)controller;
        private static void Covered(Scenario scenario, CommandAuthorityOutcome outcome, string token = "submission-current-connection")
        {
            var message = Activator.CreateInstance(Production("YC.Infrastructure.Multiplayer.CommandOutcomeMessage"));
            Set(message, "Json", JsonConvert.SerializeObject(outcome)); Set(message, "ConnectionToken", token);
            Call(scenario.Transport, "OnCommandOutcome", message);
        }
        private static CommandAuthorityOutcome AcceptAndCover(Scenario scenario, GameCommandDto received)
        {
            Assert.That(scenario.Host.ReceiveClientCommand(42, received).Succeeded, Is.True);
            Assert.That(scenario.Host.TryGetCommandOutcome(42, scenario.Authority.State.GameId, 4, received.CommandId, out var outcome), Is.True);
            Assert.That(scenario.Dispatcher.ApplyInitialStateViewSynchronization(scenario.Host.CreateInitialStateViewSynchronization(4)).Succeeded, Is.True);
            Set(scenario.Transport, "awaitingInitialState", false);
            return outcome;
        }
        private static void AssertWaiting(Scenario scenario, string id)
        {
            Assert.That(Get(scenario.Mobile, "pendingEffectCommandId"), Is.EqualTo(id));
            Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.EqualTo(id));
            Assert.That(scenario.CitySettled, Is.EqualTo(0)); Assert.That(scenario.Refreshes, Is.EqualTo(0));
            Assert.That(((InteractionCommandIntent)Get(scenario.Mobile, "coverIntent")).HasPending, Is.True);
        }
        [Test]
        public void CoveredAcceptance_OriginalMobileCallbackReleasesPendingAndNextCoverActuallyEntersTransport()
        {
            var scenario = Create(); BindRealLocalConnection(scenario); BindOriginalGateway(scenario);
            Call(scenario.Mobile, "SubmitPendingEffectCommand", scenario.OldCommand);
            AssertWaiting(scenario, scenario.OldCommand.CommandId); Assert.That(QueueCount(scenario), Is.EqualTo(1));
            var received = ReadRealQueuedSubmission(scenario);
            Assert.That(received.CommandId, Is.EqualTo(scenario.OldCommand.CommandId));
            var outcome = AcceptAndCover(scenario, received);
            var nextRequest = Request("next-character-cover", scenario.Authority.State.EffectRuntime.StateRevision);
            var next = EffectInteractionCommands.Answer(nextRequest, 4, new[] { "card-b" }, false);
            // 缺订阅时的原始阻断：新意图可创建，但真实Mobile会在旧锁分支返回，不能据此算发送。
            Call(scenario.Mobile, "SubmitPendingEffectCommand", next);
            Assert.That(QueueCount(scenario), Is.EqualTo(0));
            Assert.That(Get(scenario.Mobile, "pendingEffectCommandId"), Is.EqualTo(scenario.OldCommand.CommandId));
            var currentView = scenario.Client.View;
            string before = JsonConvert.SerializeObject(scenario.Client.State);
            Covered(scenario, outcome);
            Assert.That(Get(scenario.Mobile, "pendingEffectCommandId"), Is.EqualTo(string.Empty));
            Assert.That(((InteractionCommandIntent)Get(scenario.Mobile, "coverIntent")).IsBusy, Is.False);
            Assert.That(scenario.CitySettled, Is.EqualTo(1)); Assert.That(scenario.Refreshes, Is.EqualTo(1));
            Assert.That(scenario.Client.View, Is.SameAs(currentView));
            Assert.That(JsonConvert.SerializeObject(scenario.Client.State), Is.EqualTo(before));
            Covered(scenario, outcome);
            Assert.That(scenario.CitySettled, Is.EqualTo(1)); Assert.That(scenario.Refreshes, Is.EqualTo(1));
            scenario.Handler.Request = nextRequest;
            Call(scenario.Mobile, "SubmitPendingEffectCommand", next);
            Assert.That(QueueCount(scenario), Is.EqualTo(1));
            Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.EqualTo(next.CommandId));
            var nextReceived = ReadRealQueuedSubmission(scenario);
            Assert.That(nextReceived.CommandId, Is.EqualTo(next.CommandId));
            Assert.That(nextReceived.Parameters.Single(pair => pair.Key == "expectedRevision").Value,
                Is.EqualTo(nextRequest.StateRevision.ToString(CultureInfo.InvariantCulture)));
            Assert.That(scenario.Host.ReceiveClientCommand(42, nextReceived).Succeeded, Is.True);
            Assert.That(scenario.Handler.Calls, Is.EqualTo(2)); Assert.That(scenario.Authority.State.FindPlayer(4).Resources.GoldVoucher, Is.EqualTo(19));
            var stale = EffectInteractionCommands.Answer(nextRequest, 4, new[] { "card-c" }, false);
            var rejected = scenario.Host.ReceiveClientCommand(42, GameCommandDto.FromCommand(stale));
            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(rejected.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(scenario.Handler.Calls, Is.EqualTo(2));
        }
        [Test]
        public void CoveredAcknowledgment_WrongUnknownAndDuplicateResultsDoNotResolveOtherPending_AndDisposeUnsubscribes()
        {
            var scenario = Create(); BindRealLocalConnection(scenario); BindOriginalGateway(scenario);
            Call(scenario.Mobile, "SubmitPendingEffectCommand", scenario.OldCommand);
            var outcome = AcceptAndCover(scenario, ReadRealQueuedSubmission(scenario));
            foreach (string change in new[] { "unknown", "game", "player", "command", "connection" })
            {
                var wrong = JsonConvert.DeserializeObject<CommandAuthorityOutcome>(JsonConvert.SerializeObject(outcome));
                if (change == "unknown") wrong.Outcome = CommandAuthorityOutcomeKind.Unknown;
                if (change == "game") wrong.GameId = "old-game";
                if (change == "player") wrong.PlayerId = 1;
                if (change == "command") wrong.CommandId = "unsubmitted";
                Covered(scenario, wrong, change == "connection" ? "old-connection" : "submission-current-connection");
                AssertWaiting(scenario, scenario.OldCommand.CommandId);
            }
            var view = scenario.Client.View;
            foreach (string change in new[] { "game", "player", "viewer", "command" })
            {
                var wrong = new ConfirmedGameStateViewDto { Sequence = outcome.AcceptedSequence,
                    Command = GameCommandDto.FromCommand(scenario.OldCommand),
                    View = JsonConvert.DeserializeObject<GameStateView>(JsonConvert.SerializeObject(view)) };
                if (change == "game") wrong.View.GameId = "old-game";
                if (change == "player") wrong.Command.PlayerId = 1;
                if (change == "viewer") wrong.View.ViewerPlayerId = 1;
                if (change == "command") wrong.Command.CommandId = "unsubmitted";
                Call(scenario.Controller, "OnCoveredNetworkCommandAcknowledged", wrong);
                AssertWaiting(scenario, scenario.OldCommand.CommandId);
            }
            Covered(scenario, outcome); Covered(scenario, outcome);
            Assert.That(Get(scenario.Mobile, "pendingEffectCommandId"), Is.EqualTo(string.Empty));
            Assert.That(scenario.CitySettled, Is.EqualTo(1)); Assert.That(scenario.Refreshes, Is.EqualTo(1));
            Assert.That(scenario.Handler.Calls, Is.EqualTo(1)); Assert.That(scenario.Authority.State.FindPlayer(4).Resources.GoldVoucher, Is.EqualTo(12));
            var transportType = scenario.Transport.GetType();
            var eventField = transportType.GetField("CoveredCommandAcknowledged", Members);
            Assert.That(((Delegate)eventField.GetValue(scenario.Transport)).GetInvocationList()
                .Count(handler => handler.Target == scenario.Controller), Is.EqualTo(1));
            var nextRequest = Request("next-pending-cover", scenario.Authority.State.EffectRuntime.StateRevision);
            var next = EffectInteractionCommands.Answer(nextRequest, 4, new[] { "card-b" }, false);
            Call(scenario.Mobile, "SubmitPendingEffectCommand", next);
            Assert.That(QueueCount(scenario), Is.EqualTo(1));
            Covered(scenario, outcome);
            Call(scenario.Controller, "OnCoveredNetworkCommandAcknowledged", new ConfirmedGameStateViewDto
                { Sequence = outcome.AcceptedSequence, Command = GameCommandDto.FromCommand(scenario.OldCommand), View = scenario.Client.View });
            Covered(scenario, new CommandAuthorityOutcome { GameId = scenario.Client.View.GameId, PlayerId = 4,
                CommandId = next.CommandId, Outcome = CommandAuthorityOutcomeKind.Unknown });
            Assert.That(Get(scenario.Mobile, "pendingEffectCommandId"), Is.EqualTo(next.CommandId));
            Assert.That(Get(scenario.Transport, "pendingCommandId"), Is.EqualTo(next.CommandId));
            Assert.That(scenario.CitySettled, Is.EqualTo(1)); Assert.That(scenario.Refreshes, Is.EqualTo(1));
            Call(scenario.Controller, "Dispose");
            var remaining = (Delegate)eventField.GetValue(scenario.Transport);
            Assert.That(remaining == null || remaining.GetInvocationList().All(handler => handler.Target != scenario.Controller), Is.True);
            Assert.That(Get(scenario.Controller, "commandTransport"), Is.Null);
        }
        [TearDown]
        public void Clear()
        {
            if (current?.Controller != null) Call(current.Controller, "Dispose");
            if (current?.ConnectionCaptured == true)
            {
                var client = MirrorType("NetworkClient");
                client.GetProperty("connection", Statics).SetValue(null, current.PreviousConnection);
                client.GetField("connectState", Statics).SetValue(null, current.PreviousConnectState);
                var queue = Get(current.ServerConnection, "queue");
                while ((int)queue.GetType().GetProperty("Count").GetValue(queue) > 0)
                    ((IDisposable)queue.GetType().GetMethod("Dequeue").Invoke(queue, null)).Dispose();
            }
            if (current?.ActiveTransportCaptured == true)
                MirrorType("Transport").GetField("active", Statics).SetValue(null, current.PreviousActiveTransport);
#if !COMMAND_SUBMISSION_OFFLINE
            foreach (var gameObject in objects) if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
#endif
            objects.Clear(); current = null;
        }
    }
}
