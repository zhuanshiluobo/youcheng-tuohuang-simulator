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

namespace YC.Tests.EditMode
{
    public sealed class NetworkStateSynchronizationTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject transportObject;
        private static readonly GameRuleConfiguration Configuration = new GameRuleConfiguration {
            MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 2, ContentPackHash = new string('a', 64)
        };
        private static GameStateView View(int revision, string gameId = "synchronization-game") => new GameStateView {
            GameId = gameId, StateRevision = revision, MapId = Configuration.MapId,
            ContentPackHash = Configuration.ContentPackHash, RuleConfiguration = Configuration.Clone(),
            ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 2,
            Phase = GamePhase.ActionRound1, Round = 1, CurrentPlayerId = 2,
            Players = new List<PlayerStateView> { new PlayerStateView { PlayerId = 1 }, new PlayerStateView { PlayerId = 2 } }
        };
        private static InitialGameStateViewDto Initial(int revision, int sequence, string gameId = "synchronization-game") =>
            new InitialGameStateViewDto { NextConfirmedSequence = sequence, View = View(revision, gameId) };
        private static GameSession Session() => new GameSession(new GameState {
            RuleConfiguration = Configuration.Clone(), MapId = Configuration.MapId, ContentPackHash = Configuration.ContentPackHash
        });
        private static Type NetworkType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("YC.Infrastructure.Multiplayer." + name)).First(t => t != null);
        private static void Set(object value, string field, object data) => value.GetType().GetField(field, Members).SetValue(value, data);
        private static object Get(object value, string field) => value.GetType().GetField(field, Members).GetValue(value);
        private object Transport(GameSession session, AuthoritativeCommandDispatcher dispatcher)
        {
            transportObject = new GameObject("网络同步回归");
            var transport = transportObject.AddComponent(NetworkType("MirrorCommandTransport"));
            Set(transport, "session", session); Set(transport, "dispatcher", dispatcher);
            Set(transport, "mode", LaunchMode.Client); Set(transport, "localPlayerId", 2);
            return transport;
        }
        private static void Deliver(object transport, string method, string messageType, object dto)
        {
            var message = Activator.CreateInstance(NetworkType(messageType));
            Set(message, "Json", JsonConvert.SerializeObject(dto));
            if (messageType == "InitialStateMessage") Set(message, "ProtocolVersion", SteamLobbyPolicy.ProtocolVersion);
            transport.GetType().GetMethod(method, Members).Invoke(transport, new[] { message });
        }
        [TearDown] public void Clear() { if (transportObject != null) UnityEngine.Object.DestroyImmediate(transportObject); }

        [TestCase(9, 5, "synchronization-game")]
        [TestCase(11, 4, "synchronization-game")]
        [TestCase(11, 5, "other-game")]
        public void InitialSnapshotCannotRollBackRevisionSequenceOrGame(int revision, int sequence, string game)
        {
            var session = Session(); var dispatcher = new AuthoritativeCommandDispatcher(session, true);
            Assert.That(dispatcher.ApplyInitialStateViewSynchronization(Initial(10, 4)).Succeeded, Is.True);
            Assert.That(dispatcher.ApplyConfirmedStateViewSynchronization(new ConfirmedGameStateViewDto { Sequence = 4, View = View(11) }).Succeeded, Is.True);
            Assert.That(dispatcher.ApplyInitialStateViewSynchronization(Initial(revision, sequence, game)).Succeeded, Is.False);
            Assert.That(session.View.StateRevision, Is.EqualTo(11));
            Assert.That(dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(5));
            Assert.That(dispatcher.ApplyConfirmedStateViewSynchronization(new ConfirmedGameStateViewDto { Sequence = 5, View = View(12) }).Succeeded, Is.True);
        }

        [Test]
        public void QueuedInitialSnapshotCannotAcknowledgePendingCommand()
        {
            var session = Session(); var dispatcher = new AuthoritativeCommandDispatcher(session, true);
            dispatcher.ApplyInitialStateViewSynchronization(Initial(10, 4));
            var transport = Transport(session, dispatcher);
            Set(transport, "pendingCommandId", "pending-action");
            Deliver(transport, "OnInitialState", "InitialStateMessage", Initial(10, 4));
            Assert.That(Get(transport, "pendingCommandId"), Is.EqualTo("pending-action"));
            Assert.That(Get(transport, "awaitingInitialState"), Is.True);
            Deliver(transport, "OnAccepted", "AcceptedCommandMessage", new ConfirmedGameStateViewDto {
                Sequence = 4, View = View(11), Command = new GameCommandDto { CommandId = "pending-action", PlayerId = 2 }
            });
            Assert.That(Get(transport, "pendingCommandId"), Is.Null);
            Assert.That(Get(transport, "awaitingInitialState"), Is.False);
            Assert.That(session.View.StateRevision, Is.EqualTo(11));
        }

        [TestCase(false)][TestCase(true)]
        public void ConfirmationAlreadyCoveredBySnapshotDoesNotStartAnotherSynchronization(bool pendingCommand)
        {
            var session = Session(); var dispatcher = new AuthoritativeCommandDispatcher(session, true);
            dispatcher.ApplyInitialStateViewSynchronization(Initial(12, 6));
            var transport = Transport(session, dispatcher);
            if (pendingCommand)
            {
                Set(transport, "pendingCommandId", "covered-action");
                Set(transport, "awaitingInitialState", true);
            }
            Deliver(transport, "OnAccepted", "AcceptedCommandMessage", new ConfirmedGameStateViewDto {
                Sequence = 4, View = View(11), Command = new GameCommandDto { CommandId = "covered-action", PlayerId = 2 }
            });
            Assert.That(Get(transport, "pendingCommandId"), Is.Null);
            Assert.That(Get(transport, "awaitingInitialState"), Is.False);
            Assert.That(session.View.StateRevision, Is.EqualTo(12));
            Assert.That(dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(6));
        }

        [Test]
        public void UnrelatedCoveredConfirmationCannotCancelRequestedResynchronization()
        {
            var session = Session(); var dispatcher = new AuthoritativeCommandDispatcher(session, true);
            dispatcher.ApplyInitialStateViewSynchronization(Initial(12, 6));
            var transport = Transport(session, dispatcher);
            Set(transport, "awaitingInitialState", true);
            Deliver(transport, "OnAccepted", "AcceptedCommandMessage", new ConfirmedGameStateViewDto { Sequence = 4, View = View(11) });
            Assert.That(Get(transport, "awaitingInitialState"), Is.True);
            Assert.That(session.View.StateRevision, Is.EqualTo(12));
            Assert.That(dispatcher.NextExpectedConfirmedSequence, Is.EqualTo(6));
        }
    }
}
