using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class MirrorSnapshotWindowTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int ConnectionId = 731;
        private readonly List<object> outgoing = new List<object>();
        private readonly HashSet<string> submittedRanges = new HashSet<string>();
        private readonly List<GameObject> testObjects = new List<GameObject>();
        private Type transportType;
        private Type chunkType;
        private Type acknowledgmentType;
        private Component sender;
        private Component receiver;
        private object connection;
        private FieldInfo activeTransport;
        private object previousTransport;
        private EventInfo outgoingEvent;
        private Delegate capture;
        private GameSession hostSession;
        private GameSession clientSession;
        private AuthoritativeCommandDispatcher host;
        private int applications;
        private int peakUnconfirmedBytes;
        private int retryChunks;

        [Test]
        public void SnapshotWindow_RetriesLostChunksAndAcknowledgmentsWithoutApplyingTwice()
        {
            try
            {
                Initialize();
                Enqueue(true);
                var totalBytes = CurrentWindow().TotalBytes;
                Assert.Greater(totalBytes, 512 * 1024);
                Assert.IsEmpty(outgoing, "入队不能立即发送完整快照。");
                var now = Time.unscaledTime;
                var firstWindow = Pump(now);
                Assert.Greater(firstWindow.Count, 1);
                var firstChunkBytes = Get<byte[]>(firstWindow[0], "Payload").Length;
                // Steam 拒绝一块后不会重试；模拟丢掉第二块及收到缺口后的块。
                for (var i = 0; i < firstWindow.Count; i++)
                    if (i != 1) Deliver(firstWindow[i], true);
                Assert.AreEqual(firstChunkBytes, ReceivedBuffer().ReceivedBytes);
                Assert.AreEqual(firstChunkBytes, CurrentWindow().AcknowledgedBytes);
                foreach (var chunk in Pump(now + 0.2f)) Deliver(chunk, true);
                Assert.AreEqual(firstChunkBytes, ReceivedBuffer().ReceivedBytes);

                var retry = Pump(now + 2f);
                Assert.AreEqual(firstChunkBytes, Get<int>(retry[0], "Offset"));
                // 整个窗口的确认丢失后，连续接收进度仍保留，发送端仍保留原窗口。
                foreach (var chunk in retry) Deliver(chunk, false);
                var contiguousBytes = ReceivedBuffer().ReceivedBytes;
                Assert.Greater(contiguousBytes, firstChunkBytes);
                Assert.AreEqual(firstChunkBytes, CurrentWindow().AcknowledgedBytes);
                foreach (var chunk in Pump(now + 5f)) Deliver(chunk, true);
                Assert.AreEqual(contiguousBytes, ReceivedBuffer().ReceivedBytes);
                Assert.AreEqual(contiguousBytes, CurrentWindow().AcknowledgedBytes);

                // 最后一块完整应用，但末次确认也丢失。
                now += 15f;
                for (var rounds = 0; applications == 0 && rounds < 100; rounds++, now += 10f)
                {
                    var chunks = Pump(now);
                    Assert.IsNotEmpty(chunks);
                    foreach (var chunk in chunks)
                    {
                        var endsSnapshot = Get<int>(chunk, "Offset") + Get<byte[]>(chunk, "Payload").Length == totalBytes;
                        Deliver(chunk, !endsSnapshot);
                    }
                }
                Assert.AreEqual(1, applications);
                Assert.AreEqual(1, PendingCount());
                Assert.Less(CurrentWindow().AcknowledgedBytes, totalBytes);
                Assert.AreEqual(hostSession.State.Logs[0].Message, clientSession.State.Logs[0].Message);

                var finalRetry = Pump(now + 10f);
                Assert.IsNotEmpty(finalRetry);
                foreach (var chunk in finalRetry) Deliver(chunk, true);
                Assert.AreEqual(1, applications, "已完成传输重试只能补确认，不能重复应用。");
                Assert.AreEqual(0, PendingCount());
                Assert.IsEmpty(Pump(now + 20f));
                Assert.Greater(retryChunks, 0);
                Debug.Log($"[快照窗口验证] 快照={totalBytes}B；未确认峰值={peakUnconfirmedBytes}B；" +
                    $"重试块={retryChunks}；初始应用={applications}；覆盖中块、窗口确认和末次确认丢失。");
            }
            finally { Cleanup(); }
        }

        [Test]
        public void InitialRequest_NewGenerationCancelsOldFifoAndIgnoresOldChunksAndAcknowledgments()
        {
            try
            {
                Initialize();
                var bindings = Get<SteamIdentityBindingRegistry>(sender, "bindings");
                bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 901UL) });
                Assert.IsTrue(bindings.TryBindConnection(ConnectionId, 901UL, out _));
                RequestInitial(1);
                var oldTransferId = CurrentTransferId();
                var now = Time.unscaledTime;
                var oldChunks = Pump(now);
                Deliver(oldChunks[0], true);
                var oldProgress = CurrentWindow().AcknowledgedBytes;
                Enqueue(false);
                Enqueue(false);
                Assert.AreEqual(3, PendingCount());
                RequestInitial(1);
                Assert.AreEqual(3, PendingCount(), "同一请求代次不能反复重建全量快照。");
                Assert.AreEqual(oldTransferId, CurrentTransferId());
                Assert.AreEqual(oldProgress, CurrentWindow().AcknowledgedBytes);

                hostSession.State.Players[0].Score = 7;
                hostSession.State.Logs.Add(new GameLogEntry { Sequence = 2, PlayerId = 1, Message = "新补同步日志" });
                RequestInitial(2);
                var newTransferId = CurrentTransferId();
                Assert.Greater(newTransferId, oldTransferId);
                Assert.AreEqual(1, PendingCount(), "新的完整基线必须取消旧快照和排队确认。");
                Assert.AreEqual(0, CurrentWindow().AcknowledgedBytes);
                RequestInitial(1);
                RequestInitial(2);
                Assert.AreEqual(newTransferId, CurrentTransferId());
                Assert.AreEqual(1, PendingCount());

                var newChunks = Pump(now + 0.2f);
                Deliver(newChunks[0], true);
                var newProgress = CurrentWindow().AcknowledgedBytes;
                Deliver(oldChunks[oldChunks.Count - 1], true);
                Acknowledge(oldTransferId, Get<int>(oldChunks[0], "TotalBytes"));
                Assert.AreEqual(newProgress, ReceivedBuffer().ReceivedBytes);
                Assert.AreEqual(newProgress, CurrentWindow().AcknowledgedBytes);
                for (var i = 1; i < newChunks.Count; i++) Deliver(newChunks[i], true);
                for (var rounds = 0; PendingCount() > 0 && rounds < 100; rounds++)
                {
                    foreach (var chunk in Pump(now + 10f + rounds * 10f))
                    {
                        Assert.AreEqual(newTransferId, Get<int>(chunk, "TransferId"));
                        Deliver(chunk, true);
                    }
                }
                Assert.AreEqual(0, PendingCount());
                Assert.AreEqual(1, applications);
                Assert.AreEqual(7, clientSession.View.Players[0].Score);
                Assert.AreEqual(2, clientSession.State.Logs.Count);
                Assert.AreEqual(hostSession.State.Logs[0].Message, clientSession.State.Logs[0].Message);
                Assert.AreEqual("新补同步日志", clientSession.State.Logs[1].Message);
                Debug.Log($"[快照代次验证] 新快照={Get<int>(receiver, "completedTransferBytes")}B；" +
                    $"未确认峰值={peakUnconfirmedBytes}B；初始应用={applications}；" +
                    "重复请求不重建，新请求取消旧 FIFO，延迟旧块和旧确认均忽略。");
            }
            finally { Cleanup(); }
        }

        private void Initialize()
        {
            outgoing.Clear();
            submittedRanges.Clear();
            applications = peakUnconfirmedBytes = retryChunks = 0;
            transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            Assert.IsFalse((bool)Type.GetType("Mirror.NetworkServer, Mirror", true).GetProperty("active").GetValue(null));
            Assert.IsFalse((bool)Type.GetType("Mirror.NetworkClient, Mirror", true).GetProperty("isConnected").GetValue(null));
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            chunkType = transportType.Assembly.GetType("YC.Infrastructure.Multiplayer.StateSnapshotChunkMessage", true);
            acknowledgmentType = transportType.Assembly.GetType("YC.Infrastructure.Multiplayer.StateSnapshotAcknowledgmentMessage", true);
            sender = CreateInactive("窗口发送测试").AddComponent(transportType);
            receiver = CreateInactive("窗口接收测试").AddComponent(transportType);
            activeTransport = Type.GetType("Mirror.Transport, Mirror", true).GetField("active");
            previousTransport = activeTransport.GetValue(null);
            var telepathy = CreateInactive("窗口容量测试").AddComponent(Type.GetType("Mirror.TelepathyTransport, Mirror.Transports", true));
            activeTransport.SetValue(null, telepathy);
            connection = Activator.CreateInstance(Type.GetType("Mirror.NetworkConnectionToClient, Mirror", true),
                new object[] { ConnectionId, "localhost" });

            hostSession = new GameSession(new GameState
            {
                GameId = "窗口大快照测试局", MapId = "窗口测试地图",
                Players = { new PlayerState { PlayerId = 1, Name = "玩家一" } },
                Logs = { new GameLogEntry { Sequence = 1, PlayerId = 1, Message = new string('数', 256 * 1024) } }
            });
            host = new AuthoritativeCommandDispatcher(hostSession);
            clientSession = new GameSession(new GameState
            {
                MapId = hostSession.State.MapId, Players = { new PlayerState { PlayerId = 1 } }
            });
            Set(sender, "dispatcher", host);
            Set(receiver, "session", clientSession);
            Set(receiver, "dispatcher", new AuthoritativeCommandDispatcher(clientSession, true));
            Set(receiver, "localPlayerId", 1);
            Set(receiver, "awaitingInitialState", true);
            transportType.GetEvent("InitialStateViewApplied").AddEventHandler(receiver,
                new Action<InitialGameStateViewDto>(_ => applications++));

            outgoingEvent = Type.GetType("Mirror.NetworkDiagnostics, Mirror", true).GetEvent("OutMessageEvent");
            var infoType = outgoingEvent.EventHandlerType.GetGenericArguments()[0];
            var info = Expression.Parameter(infoType);
            Action<object> collect = message => { if (chunkType.IsInstanceOfType(message)) outgoing.Add(message); };
            capture = Expression.Lambda(outgoingEvent.EventHandlerType,
                Expression.Invoke(Expression.Constant(collect), Expression.Convert(Expression.Field(info, "message"), typeof(object))),
                info).Compile();
            outgoingEvent.AddEventHandler(null, capture);
        }

        private void Enqueue(bool initial)
        {
            var view = host.CreateInitialStateViewSynchronization(1);
            var payload = Activator.CreateInstance(transportType.GetNestedType("SnapshotPayload", BindingFlags.NonPublic), true);
            Set(payload, "Sequence", view.NextConfirmedSequence);
            Set(payload, "View", view.View);
            transportType.GetMethod("SendSnapshot", InstanceFields).Invoke(sender,
                new object[] { connection, 1, payload, initial, JsonUtility.ToJson(view) });
        }

        private void RequestInitial(int requestId)
        {
            var request = Activator.CreateInstance(transportType.Assembly.GetType("YC.Infrastructure.Multiplayer.InitialStateRequestMessage", true));
            Set(request, "RequestId", requestId);
            transportType.GetMethod("OnInitialStateRequest", InstanceFields).Invoke(sender, new[] { connection, request });
        }

        private List<object> Pump(float now)
        {
            outgoing.Clear();
            transportType.GetMethod("PumpSnapshotSends", InstanceFields).Invoke(sender, new object[] { now });
            var chunks = new List<object>(outgoing);
            var bytes = 0;
            foreach (var chunk in chunks)
            {
                var end = Get<int>(chunk, "Offset") + Get<byte[]>(chunk, "Payload").Length;
                var unconfirmed = end - CurrentWindow().AcknowledgedBytes;
                peakUnconfirmedBytes = Math.Max(peakUnconfirmedBytes, unconfirmed);
                Assert.LessOrEqual(unconfirmed, SnapshotSendWindow.MaxInFlightBytes);
                bytes += Get<byte[]>(chunk, "Payload").Length;
                if (!submittedRanges.Add(Get<int>(chunk, "TransferId") + ":" + Get<int>(chunk, "Offset"))) retryChunks++;
            }
            Assert.LessOrEqual(bytes, SnapshotSendWindow.MaxInFlightBytes);
            // 不连接底层传输；清掉真实 Send 写入的 batch，模拟本轮交给 Steam 后已释放。
            connection.GetType().GetMethod("Cleanup").Invoke(connection, null);
            return chunks;
        }

        private void Deliver(object chunk, bool sendAcknowledgment)
        {
            // 经真实 Weaver 序列化和读取，再交给生产接收入口。
            var writerType = Type.GetType("Mirror.NetworkWriter, Mirror", true);
            var readerType = Type.GetType("Mirror.NetworkReader, Mirror", true);
            var messagesType = Type.GetType("Mirror.NetworkMessages, Mirror", true);
            var writer = Activator.CreateInstance(writerType);
            messagesType.GetMethod("Pack").MakeGenericMethod(chunkType).Invoke(null, new[] { chunk, writer });
            var bytes = (byte[])writerType.GetMethod("ToArray").Invoke(writer, null);
            Assert.LessOrEqual(bytes.Length, (int)messagesType.GetMethod("MaxMessageSize").Invoke(null, new object[] { 0 }));
            var reader = Activator.CreateInstance(readerType, new object[] { new ArraySegment<byte>(bytes) });
            Set(reader, "Position", (int)messagesType.GetField("IdSize").GetRawConstantValue());
            var received = readerType.GetMethod("Read").MakeGenericMethod(chunkType).Invoke(reader, null);
            Assert.AreEqual(0, readerType.GetProperty("Remaining").GetValue(reader));
            transportType.GetMethod("OnSnapshotChunk", InstanceFields).Invoke(receiver, new[] { received });
            if (!sendAcknowledgment) return;
            var transferId = Get<int>(chunk, "TransferId");
            var nextOffset = Get<int>(receiver, "completedTransferId") == transferId
                ? Get<int>(receiver, "completedTransferBytes") : ReceivedBuffer().ReceivedBytes;
            Acknowledge(transferId, nextOffset);
        }

        private void Acknowledge(int transferId, int nextOffset)
        {
            var acknowledgment = Activator.CreateInstance(acknowledgmentType);
            Set(acknowledgment, "TransferId", transferId);
            Set(acknowledgment, "NextOffset", nextOffset);
            transportType.GetMethod("OnSnapshotAcknowledgment", InstanceFields).Invoke(sender, new[] { connection, acknowledgment });
        }

        private object Pending()
        {
            var queues = Get<IDictionary>(sender, "snapshotSendQueues");
            return Get<object>(queues[ConnectionId], "Pending");
        }

        private int PendingCount() => (int)Pending().GetType().GetProperty("Count").GetValue(Pending());
        private object CurrentSnapshot() => Pending().GetType().GetMethod("Peek").Invoke(Pending(), null);
        private int CurrentTransferId() => Get<int>(CurrentSnapshot(), "TransferId");
        private SnapshotSendWindow CurrentWindow() => Get<SnapshotSendWindow>(CurrentSnapshot(), "Window");
        private SnapshotChunkBuffer ReceivedBuffer() => Get<SnapshotChunkBuffer>(receiver, "snapshotBuffer");
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, InstanceFields).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, InstanceFields).SetValue(target, value);

        private GameObject CreateInactive(string name)
        {
            var result = new GameObject(name);
            result.SetActive(false);
            testObjects.Add(result);
            return result;
        }

        private void Cleanup()
        {
            if (capture != null) outgoingEvent.RemoveEventHandler(null, capture);
            if (connection != null) connection.GetType().GetMethod("Cleanup").Invoke(connection, null);
            if (activeTransport != null) activeTransport.SetValue(null, previousTransport);
            for (var i = testObjects.Count - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(testObjects[i]);
            testObjects.Clear();
            capture = null;
            connection = null;
            activeTransport = null;
        }
    }
}
