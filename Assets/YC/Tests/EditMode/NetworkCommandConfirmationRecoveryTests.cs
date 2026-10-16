using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
#if COMMAND_CONFIRMATION_OFFLINE
using Assert = YC.OfflineTests.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class NetworkCommandConfirmationRecoveryTests
    {
        internal const string OriginalRejection = "命令输入版本已变化，保留原意图等待刷新。";
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<GameObject> objects = new List<GameObject>();

        internal sealed class RecordingHandler : IGameCommandHandler
        {
            public int Calls;
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.EndAction;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                Calls++;
                if (command.Parameters["expectedRevision"] != state.EffectRuntime.StateRevision.ToString(CultureInfo.InvariantCulture))
                    return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.StateRevisionMismatch, OriginalRejection));
                state.FindPlayer(3).Resources.GoldVoucher += 7;
                return CommandResult.SuccessResult(new List<GameEvent>(), "命令已执行一次。");
            }
        }
        internal sealed class Authority
        {
            public GameSession Session;
            public AuthoritativeCommandDispatcher Dispatcher;
            public RecordingHandler Handler;
            public string GameId => Session.State.GameId;
            public GameCommandDto Command(string id, int revision) => GameCommandDto.FromCommand(new GameCommand {
                CommandId = id, Kind = GameCommandKind.EndAction, PlayerId = 3,
                Parameters = { { "expectedRevision", revision.ToString(CultureInfo.InvariantCulture) } } });
        }
        internal static Authority CreateAuthority()
        {
            var state = new GameState { GameId = "confirmation-recovery-game", MapId = StaticMapDefinitions.ThreePlayerMapId,
                ContentPackHash = new string('a', 64), StartPlayerId = 1, CurrentPlayerId = 3,
                UseSeatTurnOrder = true, Phase = GamePhase.ActionRound1, Round = 1,
                LaunchPlayerOrder = new List<int> { 1, 3 },
                RuleConfiguration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId,
                    PlayerCount = 2, ContentPackHash = new string('a', 64) },
                Players = { new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 3, Color = PlayerColor.Green, Resources = new ResourceSet { GoldVoucher = 5 } } } };
            var authority = new Authority { Session = new GameSession(state), Handler = new RecordingHandler() };
            authority.Session.RegisterHandler(authority.Handler);
            authority.Dispatcher = new AuthoritativeCommandDispatcher(authority.Session);
            authority.Dispatcher.RegisterClientPlayer(42, 3);
            return authority;
        }
        private sealed class Client
        {
            public object Transport;
            public GameSession Session;
            public AuthoritativeCommandDispatcher Dispatcher;
            public int Covered, Rejected, Applied;
            public ConfirmedGameStateViewDto Acknowledgment;
            public RejectedGameCommandDto Rejection;
        }
        private static Type TransportType() => typeof(NetworkCommandConfirmationRecoveryTests).Assembly
            .GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport") ?? AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport")).First(type => type != null);
        private Client CreateClient(Authority authority, GameCommandDto pending)
        {
            var client = new Client { Session = new GameSession(GameStateCloneService.DeepClone(authority.Session.State)) };
            client.Dispatcher = new AuthoritativeCommandDispatcher(client.Session, true);
            Assert.That(client.Dispatcher.ApplyInitialStateViewSynchronization(authority.Dispatcher.CreateInitialStateViewSynchronization(3)).Succeeded, Is.True);
#if COMMAND_CONFIRMATION_OFFLINE
            // 分配实际运输类型的托管字段，不替换任何受测生产方法，不调用Unity组件构造。
            client.Transport = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(TransportType());
            Set(client.Transport, "bindings", new SteamIdentityBindingRegistry());
            Set(client.Transport, "pendingLocalBindings", new Dictionary<int, float>());
            Set(client.Transport, "clientConnectionTokens", new Dictionary<int, string>());
            Set(client.Transport, "viewAssembler", new SnapshotChunkBuffer());
            Set(client.Transport, "receivedViewHistory", new StateViewHistoryTracker());
#else
            var gameObject = new GameObject("命令确认恢复检查");
            objects.Add(gameObject);
            client.Transport = gameObject.AddComponent(TransportType());
#endif
            Set(client.Transport, "session", client.Session); Set(client.Transport, "dispatcher", client.Dispatcher);
            Set(client.Transport, "mode", LaunchMode.Client); Set(client.Transport, "localPlayerId", 3);
            Set(client.Transport, "pendingCommand", pending); Set(client.Transport, "pendingCommandId", pending.CommandId);
            Set(client.Transport, "pendingGameId", authority.GameId); Set(client.Transport, "clientConnectionToken", "old-connection");
            TransportType().GetEvent("CoveredCommandAcknowledged").AddEventHandler(client.Transport,
                new Action<ConfirmedGameStateViewDto>(dto => { client.Covered++; client.Acknowledgment = dto; }));
            TransportType().GetEvent("CommandRejected").AddEventHandler(client.Transport,
                new Action<RejectedGameCommandDto>(dto => { client.Rejected++; client.Rejection = dto; }));
            TransportType().GetEvent("ConfirmedStateViewApplied").AddEventHandler(client.Transport,
                new Action<ConfirmedGameStateViewDto>(_ => client.Applied++));
            return client;
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Members).SetValue(target, value);
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Members).GetValue(target);
        private static void Invoke(object target, string method, params object[] values)
        {
            try { target.GetType().GetMethod(method, Members).Invoke(target, values); }
            catch (TargetInvocationException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
                throw;
            }
        }
        private static object Message(string name, string json, string token, string gameId = null)
        {
            var message = Activator.CreateInstance(TransportType().Assembly.GetType("YC.Infrastructure.Multiplayer." + name, true));
            Set(message, "Json", json); Set(message, "ConnectionToken", token);
            if (gameId != null) Set(message, "GameId", gameId);
            return message;
        }
        private static bool Synchronizing(Client client) => (bool)TransportType().GetProperty("IsSynchronizing").GetValue(client.Transport);
        private static void Deliver(Client client, CommandAuthorityOutcome outcome, string token = null) => Invoke(client.Transport,
            "OnCommandOutcome", Message("CommandOutcomeMessage", JsonConvert.SerializeObject(outcome),
                token ?? Get<string>(client.Transport, "clientConnectionToken")));
        private static void Synchronize(Client client, Authority authority)
        {
            Assert.That(client.Dispatcher.ApplyInitialStateViewSynchronization(authority.Dispatcher.CreateInitialStateViewSynchronization(3)).Succeeded, Is.True);
            Set(client.Transport, "awaitingInitialState", false);
            Invoke(client.Transport, "TryCompleteRecoveredOutcome");
        }

        [Test]
        public void ReconnectedAcceptance_WaitsForCoveringSnapshotThenNotifiesOnlyOnce()
        {
            var authority = CreateAuthority();
            var command = authority.Command("accepted-after-disconnect", 0);
            var client = CreateClient(authority, command);
            Assert.That(authority.Dispatcher.ReceiveClientCommand(42, command).Succeeded, Is.True);
            authority.Dispatcher.UnregisterClientPlayer(42);
            authority.Dispatcher.RegisterClientPlayer(77, 3);
            Assert.That(authority.Dispatcher.TryGetCommandOutcome(77, authority.GameId, 3, command.CommandId, out var outcome), Is.True);
            Invoke(client.Transport, "OnLocalClientConnected");
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.EqualTo(command.CommandId));
            Assert.That(Synchronizing(client), Is.True);
            Assert.That(Get<string>(client.Transport, "clientConnectionToken"), Is.Not.EqualTo("old-connection"));
            Deliver(client, outcome, "old-connection");
            Assert.That(client.Covered, Is.Zero);
            Deliver(client, outcome);
            Assert.That(client.Covered, Is.Zero, "新连接取得覆盖状态前不能宣告确认。");
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.EqualTo(command.CommandId));
            var before = JsonConvert.SerializeObject(client.Session.View);
            Synchronize(client, authority);
            var current = client.Session.View;
            Assert.That(client.Covered, Is.EqualTo(1));
            Assert.That(client.Acknowledgment.Command.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(client.Acknowledgment.Sequence, Is.EqualTo(outcome.AcceptedSequence));
            Assert.That(client.Acknowledgment.View, Is.SameAs(current));
            Assert.That(client.Applied, Is.Zero);
            Assert.That(Synchronizing(client), Is.False);
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.Null);
            var covered = JsonConvert.SerializeObject(current);
            Deliver(client, outcome);
            Assert.That(client.Covered, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(client.Session.View), Is.EqualTo(covered));
            Assert.That(covered, Is.Not.EqualTo(before), "只有实际初始同步更新投影，查询确认不重放旧view。");
            Assert.That(authority.Handler.Calls, Is.EqualTo(1));
            Assert.That(authority.Session.State.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(authority.Session.State.Logs, Has.Count.EqualTo(1));

            // 原正常确认出口仍只应用一次；旧连接、跨局和错人消息不能触发确认回调。
            var next = authority.Command("next-normal-command", authority.Session.State.EffectRuntime.StateRevision);
            Set(client.Transport, "pendingCommand", next); Set(client.Transport, "pendingCommandId", next.CommandId);
            Set(client.Transport, "pendingGameId", authority.GameId);
            ConfirmedGameCommandDto accepted = null;
            authority.Dispatcher.CommandAccepted += dto => accepted = dto;
            Assert.That(authority.Dispatcher.ReceiveClientCommand(77, next).Succeeded, Is.True);
            var normal = authority.Dispatcher.CreateConfirmedStateViewSynchronization(accepted, GameStateViewer.Player(3));
            var wrongGame = JsonConvert.DeserializeObject<ConfirmedGameStateViewDto>(JsonConvert.SerializeObject(normal));
            wrongGame.View.GameId = "old-game";
            var wrongPlayer = JsonConvert.DeserializeObject<ConfirmedGameStateViewDto>(JsonConvert.SerializeObject(normal));
            wrongPlayer.Command.PlayerId = 1;
            foreach (var ignored in new[] { wrongGame, wrongPlayer })
                Invoke(client.Transport, "OnAccepted", Message("AcceptedCommandMessage", JsonConvert.SerializeObject(ignored),
                    Get<string>(client.Transport, "clientConnectionToken")));
            Invoke(client.Transport, "OnAccepted", Message("AcceptedCommandMessage", JsonConvert.SerializeObject(normal), "old-connection"));
            Assert.That(client.Applied, Is.Zero);
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.EqualTo(next.CommandId));
            Assert.That(client.Session.View, Is.SameAs(current));
            var message = Message("AcceptedCommandMessage", JsonConvert.SerializeObject(normal),
                Get<string>(client.Transport, "clientConnectionToken"));
            Invoke(client.Transport, "OnAccepted", message);
            Invoke(client.Transport, "OnAccepted", message);
            Assert.That(client.Applied, Is.EqualTo(1));
            Assert.That(client.Covered, Is.EqualTo(1));
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.Null);
            Assert.That(client.Session.View.StateRevision, Is.EqualTo(normal.View.StateRevision));
            Assert.That(authority.Handler.Calls, Is.EqualTo(2));
            Assert.That(authority.Session.State.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(19));
        }

        [Test]
        public void UnknownOldAndWrongOwnerResults_DoNotClearPending_TrueRejectionKeepsOriginalError()
        {
            var authority = CreateAuthority();
            var command = authority.Command("rejected-after-disconnect", -1);
            var client = CreateClient(authority, command);
            Assert.That(authority.Dispatcher.ReceiveClientCommand(42, command).Succeeded, Is.False);
            Assert.That(authority.Dispatcher.TryGetCommandOutcome(42, authority.GameId, 3, command.CommandId, out var rejection), Is.True);
            Invoke(client.Transport, "OnLocalClientConnected");
            Synchronize(client, authority);
            var before = JsonConvert.SerializeObject(client.Session.View);
            Deliver(client, new CommandAuthorityOutcome { GameId = authority.GameId, PlayerId = 3,
                CommandId = command.CommandId, Outcome = CommandAuthorityOutcomeKind.Unknown });
            foreach (var changed in new Action<CommandAuthorityOutcome>[] {
                value => value.GameId = "old-game", value => value.PlayerId = 1, value => value.CommandId = "old-command" })
            {
                var wrong = JsonConvert.DeserializeObject<CommandAuthorityOutcome>(JsonConvert.SerializeObject(rejection));
                changed(wrong); Deliver(client, wrong);
            }
            Deliver(client, rejection, "old-connection");
            Assert.That(Synchronizing(client), Is.True);
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.EqualTo(command.CommandId));
            Assert.That(client.Rejected, Is.Zero);
            Assert.That(client.Covered, Is.Zero);
            Deliver(client, rejection);
            Assert.That(client.Rejected, Is.EqualTo(1));
            Assert.That(client.Rejection.ErrorCode, Is.EqualTo(CommandErrorCode.StateRevisionMismatch));
            Assert.That(client.Rejection.Reason, Is.EqualTo(OriginalRejection));
            Assert.That(client.Rejection.Command.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(Synchronizing(client), Is.False);
            var later = authority.Command("later-pending", 0);
            Set(client.Transport, "pendingCommand", later); Set(client.Transport, "pendingCommandId", later.CommandId);
            Set(client.Transport, "pendingGameId", authority.GameId);
            Deliver(client, rejection);
            Invoke(client.Transport, "OnRejected", Message("RejectedCommandMessage", JsonConvert.SerializeObject(client.Rejection),
                Get<string>(client.Transport, "clientConnectionToken"), authority.GameId));
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.EqualTo(later.CommandId));
            Assert.That(client.Rejected, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(client.Session.View), Is.EqualTo(before));
            Assert.That(authority.Handler.Calls, Is.EqualTo(1));
            Assert.That(authority.Session.State.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(authority.Session.State.Logs, Is.Empty);
            Invoke(client.Transport, "Shutdown");
            Deliver(client, rejection);
            Assert.That(Get<string>(client.Transport, "pendingCommandId"), Is.Null);
            Assert.That(client.Rejected, Is.EqualTo(1));
        }

        [TearDown]
        public void Clear()
        {
#if !COMMAND_CONFIRMATION_OFFLINE
            foreach (var gameObject in objects) if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
#endif
            objects.Clear();
        }
    }
}
