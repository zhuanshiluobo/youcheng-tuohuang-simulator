using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class MirrorSnapshotTransportTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void SnapshotChunks_ApplyLargeInitialAndResyncSnapshotsThenKeepIncrementalHistory()
        {
            var transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            var mirrorServerType = Type.GetType("Mirror.NetworkServer, Mirror", true);
            Assert.IsFalse((bool)mirrorServerType.GetProperty("active").GetValue(null),
                "此测试需要未启动的网络服务端。");
            // EditMode 不进入 PlayMode；初始化 Weaver 实际生成的序列化委托。
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);

            var hostSession = CreateHostSession();
            var host = new AuthoritativeCommandDispatcher(hostSession);
            var clientSession = new GameSession(new GameState
            {
                MapId = hostSession.State.MapId,
                Players = { new PlayerState { PlayerId = 1 } }
            });
            var client = new AuthoritativeCommandDispatcher(clientSession, true);
            var senderHistory = new StateViewHistoryTracker();
            ConfirmedGameCommandDto accepted = null;
            host.CommandAccepted += value => accepted = value;

            var testObject = new GameObject("快照传输测试");
            testObject.SetActive(false);
            try
            {
                // 仅使用实际接收入口，不注册全局网络处理器或启动 socket。
                var transport = testObject.AddComponent(transportType);
                SetField(transport, "session", clientSession);
                SetField(transport, "dispatcher", client);
                SetField(transport, "localPlayerId", 1);
                SetField(transport, "awaitingInitialState", true);
                var payloadType = transportType.GetNestedType("SnapshotPayload", BindingFlags.NonPublic);

                // 用默认 16 KiB 容量的真实传输组件验证发送端自动缩小分块，只入队而不启动网络。
                var activeTransportField = Type.GetType("Mirror.Transport, Mirror", true).GetField("active");
                var previousTransport = activeTransportField.GetValue(null);
                var telepathyObject = new GameObject("快照容量测试");
                telepathyObject.SetActive(false);
                object connection = null;
                try
                {
                    var telepathy = telepathyObject.AddComponent(Type.GetType("Mirror.TelepathyTransport, Mirror.Transports", true));
                    activeTransportField.SetValue(null, telepathy);
                    var connectionType = Type.GetType("Mirror.NetworkConnectionToClient, Mirror", true);
                    connection = Activator.CreateInstance(connectionType, new object[] { 123, "localhost" });
                    var full = host.CreateInitialStateViewSynchronization(1);
                    var fullJson = JsonUtility.ToJson(full);
                    Assert.Greater(Encoding.UTF8.GetByteCount(fullJson), 65534);
                    transportType.GetMethod("SendSnapshot", InstanceFields).Invoke(transport, new object[]
                    {
                        connection, 1, CreatePayload(payloadType, full.NextConfirmedSequence, null, full.View, null),
                        true, fullJson
                    });
                    var maxMessageBytes = (int)Type.GetType("Mirror.NetworkMessages, Mirror", true)
                        .GetMethod("MaxMessageSize").Invoke(null, new object[] { 0 });
                    var largestMessageBytes = (int)transportType.GetProperty("LastSnapshotLargestMessageBytes").GetValue(transport);
                    var chunkCount = (int)transportType.GetProperty("LastSnapshotChunkCount").GetValue(transport);
                    Assert.Greater(chunkCount, 1);
                    Assert.Greater(largestMessageBytes, 0);
                    Assert.LessOrEqual(largestMessageBytes, maxMessageBytes);
                    Debug.Log($"完整视图 JSON={Encoding.UTF8.GetByteCount(fullJson)}B；" +
                        $"真实 Mirror 单块最大={largestMessageBytes}B；消息上限={maxMessageBytes}B；分块={chunkCount}。");
                }
                finally
                {
                    if (connection != null) connection.GetType().GetMethod("Cleanup").Invoke(connection, null);
                    activeTransportField.SetValue(null, previousTransport);
                    UnityEngine.Object.DestroyImmediate(telepathyObject);
                }

                var initial = host.CreateInitialStateViewSynchronization(1);
                var initialPayload = CreatePayload(payloadType, initial.NextConfirmedSequence, null, initial.View,
                    senderHistory.CreateDelta(initial.View, true));
                var initialJson = JsonUtility.ToJson(initialPayload);
                Assert.Greater(Encoding.UTF8.GetByteCount(initialJson), 65534);
                Assert.Greater(DeliverChunks(transport, initialJson, true, 1), 1);
                Assert.IsTrue(client.IsInitialStateSynchronized);
                AssertClientMatchesHost(hostSession, clientSession);

                Assert.IsTrue(host.SubmitHostCommand(Command("确认 1")).Succeeded);
                var confirmed = host.CreateConfirmedStateViewSynchronization(accepted, GameStateViewer.Player(1));
                var incremental = senderHistory.CreateDelta(confirmed.View, false);
                Assert.IsFalse(incremental.Reset);
                Assert.AreEqual(1, confirmed.View.Events.Count);
                Assert.AreEqual(1, confirmed.View.Logs.Count);
                var incrementalJson = JsonUtility.ToJson(CreatePayload(payloadType, confirmed.Sequence, confirmed.Command,
                    confirmed.View, incremental));
                Assert.Less(Encoding.UTF8.GetByteCount(incrementalJson), Encoding.UTF8.GetByteCount(initialJson) / 4);
                Debug.Log($"初始传输 JSON={Encoding.UTF8.GetByteCount(initialJson)}B；" +
                    $"后续增量 JSON={Encoding.UTF8.GetByteCount(incrementalJson)}B。");
                DeliverChunks(transport, incrementalJson, false, 2);
                AssertClientMatchesHost(hostSession, clientSession);

                // 客户端错过一次确认后，使用同一生产接收入口建立完整历史基线。
                Assert.IsTrue(host.SubmitHostCommand(Command("未收到的确认")).Succeeded);
                transportType.GetMethod("OnLocalClientDisconnected", InstanceFields).Invoke(transport, null);
                var resync = host.CreateInitialStateViewSynchronization(1);
                var resyncJson = JsonUtility.ToJson(CreatePayload(payloadType, resync.NextConfirmedSequence, null,
                    resync.View, senderHistory.CreateDelta(resync.View, true)));
                Assert.Greater(Encoding.UTF8.GetByteCount(resyncJson), 65534);
                Assert.Greater(DeliverChunks(transport, resyncJson, true, 3), 1);
                AssertClientMatchesHost(hostSession, clientSession);

                Assert.IsTrue(host.SubmitHostCommand(Command("补同步后的确认")).Succeeded);
                confirmed = host.CreateConfirmedStateViewSynchronization(accepted, GameStateViewer.Player(1));
                incremental = senderHistory.CreateDelta(confirmed.View, false);
                Assert.IsFalse(incremental.Reset);
                Assert.AreEqual(1, confirmed.View.Events.Count);
                Assert.AreEqual(1, confirmed.View.Logs.Count);
                DeliverChunks(transport, JsonUtility.ToJson(CreatePayload(payloadType, confirmed.Sequence,
                    confirmed.Command, confirmed.View, incremental)), false, 4);
                AssertClientMatchesHost(hostSession, clientSession);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(testObject);
            }
        }

        private static int DeliverChunks(Component transport, string json, bool isInitial, int transferId)
        {
            var messageType = transport.GetType().Assembly.GetType("YC.Infrastructure.Multiplayer.StateSnapshotChunkMessage", true);
            var writerType = Type.GetType("Mirror.NetworkWriter, Mirror", true);
            var readerType = Type.GetType("Mirror.NetworkReader, Mirror", true);
            var messagesType = Type.GetType("Mirror.NetworkMessages, Mirror", true);
            var pack = messagesType.GetMethod("Pack").MakeGenericMethod(messageType);
            var read = readerType.GetMethod("Read").MakeGenericMethod(messageType);
            var receive = transport.GetType().GetMethod("OnSnapshotChunk", InstanceFields);
            var chunkSize = (int)transport.GetType().GetField("PreferredChunkBytes",
                BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            var idSize = (int)messagesType.GetField("IdSize").GetRawConstantValue();
            var bytes = Encoding.UTF8.GetBytes(json);
            var count = 0;
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                var payload = new byte[Math.Min(chunkSize, bytes.Length - offset)];
                Buffer.BlockCopy(bytes, offset, payload, 0, payload.Length);
                var message = Activator.CreateInstance(messageType);
                SetField(message, "TransferId", transferId);
                SetField(message, "IsInitial", isInitial);
                SetField(message, "ProtocolVersion", SteamLobbyPolicy.ProtocolVersion);
                SetField(message, "TotalBytes", bytes.Length);
                SetField(message, "Offset", offset);
                SetField(message, "Payload", payload);
                var writer = Activator.CreateInstance(writerType);
                pack.Invoke(null, new[] { message, writer });
                var wireBytes = (byte[])writerType.GetMethod("ToArray").Invoke(writer, null);
                // 记录真实 Weaver 输出，包含消息 ID、字段及数组长度前缀。
                Assert.Greater(wireBytes.Length, payload.Length);
                Assert.Less(wireBytes.Length, 65534);
                var reader = Activator.CreateInstance(readerType, new object[] { new ArraySegment<byte>(wireBytes) });
                SetField(reader, "Position", idSize);
                var receivedMessage = read.Invoke(reader, null);
                Assert.AreEqual(0, readerType.GetProperty("Remaining").GetValue(reader));
                receive.Invoke(transport, new[] { receivedMessage });
                count++;
            }
            return count;
        }

        private static object CreatePayload(Type type, int sequence, GameCommandDto command,
            GameStateView view, StateViewHistoryDelta history)
        {
            var payload = Activator.CreateInstance(type, true);
            SetField(payload, "Sequence", sequence);
            SetField(payload, "Command", command);
            SetField(payload, "View", view);
            SetField(payload, "History", history);
            return payload;
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, InstanceFields).SetValue(target, value);
        }

        private static GameCommandDto Command(string id)
        {
            return new GameCommandDto { CommandId = id, Kind = GameCommandKind.EndAction, PlayerId = 1 };
        }

        private static GameSession CreateHostSession()
        {
            var state = new GameState
            {
                GameId = "大快照测试局",
                MapId = "快照测试地图",
                Players = { new PlayerState { PlayerId = 1, Name = "玩家一" } }
            };
            var message = new StringBuilder();
            for (var i = 0; i < 100; i++) message.Append("游城🚀");
            for (var i = 1; i <= 100; i++)
            {
                state.Logs.Add(new GameLogEntry { Sequence = i, PlayerId = 1, Message = message + "日志 " + i });
                state.EffectRuntime.RuleEvents.Add(new RuleEvent
                {
                    EventId = "历史事件 " + i,
                    EventType = "历史记录",
                    Visibility = "public"
                });
            }
            var session = new GameSession(state);
            session.RegisterHandler(new HistoryCommandHandler());
            return session;
        }

        private static void AssertClientMatchesHost(GameSession host, GameSession client)
        {
            var expected = GameStateViewProjector.ProjectForPlayer(host.State, 1);
            Assert.AreEqual(expected.Players[0].Score, client.View.Players[0].Score);
            CollectionAssert.AreEqual(expected.Logs.ConvertAll(item => item.Sequence),
                client.View.Logs.ConvertAll(item => item.Sequence));
            CollectionAssert.AreEqual(expected.Logs.ConvertAll(item => item.Message),
                client.State.Logs.ConvertAll(item => item.Message));
            CollectionAssert.AreEqual(expected.Events.ConvertAll(item => item.EventId),
                client.View.Events.ConvertAll(item => item.EventId));
            CollectionAssert.AreEqual(expected.Events.ConvertAll(item => item.Sequence),
                client.View.Events.ConvertAll(item => item.Sequence));
            Assert.IsNull(client.State.EffectRuntime);
        }

        private sealed class HistoryCommandHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command) => command.Kind == GameCommandKind.EndAction;

            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.FindPlayer(command.PlayerId).Score++;
                return CommandResult.SuccessResult(new List<GameEvent> { GameEvent.Log("新增事件🚀") }, "新增日志🚀");
            }
        }
    }
}
