using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    // 旧视图快照已退役；沿用真实 Mirror Weaver 验证新检查点的分块容量和往返完整性。
    public sealed class MirrorSnapshotTransportTests
    {
        [Test]
        public void CheckpointChunks_RoundTripThroughMirrorWithinTransportCapacity()
        {
            var transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            var messageType = transportType.Assembly.GetType("YC.Infrastructure.Multiplayer.SimulationCheckpointPartMessage", true);
            var writerType = Type.GetType("Mirror.NetworkWriter, Mirror", true);
            var readerType = Type.GetType("Mirror.NetworkReader, Mirror", true);
            var messagesType = Type.GetType("Mirror.NetworkMessages, Mirror", true);
            var activeTransport = Type.GetType("Mirror.Transport, Mirror", true).GetField("active");
            var previous = activeTransport.GetValue(null);
            var root = new GameObject("检查点传输容量验证");
            root.SetActive(false);
            try
            {
                activeTransport.SetValue(null, root.AddComponent(Type.GetType("Mirror.TelepathyTransport, Mirror.Transports", true)));
                var limit = (int)messagesType.GetMethod("MaxMessageSize").Invoke(null, new object[] { 0 });
                var pack = messagesType.GetMethod("Pack").MakeGenericMethod(messageType);
                var read = readerType.GetMethod("Read").MakeGenericMethod(messageType);
                var idSize = (int)messagesType.GetField("IdSize").GetRawConstantValue();
                var identity = SimulationCheckpointTransferTests.InitialIdentity;
                var archive = SimulationCheckpointTransferTests.InitialArchiveJson;
                var transfer = SimulationCheckpointTransfer.Encode(identity, archive);
                var parts = transfer.Split().ToArray();
                Assert.That(parts.Length, Is.GreaterThan(1));
                var assembler = new SimulationCheckpointTransfer.Assembler(identity);
                string restored = null;
                foreach (var part in parts)
                {
                    var message = Activator.CreateInstance(messageType);
                    messageType.GetField("EnvelopeJson").SetValue(message, JsonConvert.SerializeObject(part.Envelope));
                    messageType.GetField("Offset").SetValue(message, part.Offset);
                    messageType.GetField("Payload").SetValue(message, part.Payload);
                    messageType.GetField("ConnectionToken").SetValue(message, "checkpoint-wire-peer");
                    var writer = Activator.CreateInstance(writerType);
                    pack.Invoke(null, new[] { message, writer });
                    var bytes = (byte[])writerType.GetMethod("ToArray").Invoke(writer, null);
                    Assert.That(bytes.Length, Is.LessThanOrEqualTo(limit));
                    var reader = Activator.CreateInstance(readerType, new object[] { new ArraySegment<byte>(bytes) });
                    readerType.GetField("Position").SetValue(reader, idSize);
                    var received = read.Invoke(reader, null);
                    Assert.That(readerType.GetProperty("Remaining").GetValue(reader), Is.EqualTo(0));
                    Assert.That(messageType.GetField("ConnectionToken").GetValue(received), Is.EqualTo("checkpoint-wire-peer"));
                    var envelope = JsonConvert.DeserializeObject<SimulationCheckpointEnvelope>((string)messageType.GetField("EnvelopeJson").GetValue(received));
                    Assert.That(assembler.TryAppend(envelope, (int)messageType.GetField("Offset").GetValue(received),
                        (byte[])messageType.GetField("Payload").GetValue(received), out restored, out var fault), Is.True, fault);
                }
                Assert.That(restored, Is.EqualTo(archive));
            }
            finally
            {
                activeTransport.SetValue(null, previous);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [TestCase("PrivateViewPartMessage", "ToPart")]
        [TestCase("PrivateViewAckMessage", "ToAck")]
        [TestCase("PrivateViewInstalledMessage", "ToReceipt")]
        [TestCase("PrivatePayloadPartMessage", "ToPart")]
        [TestCase("PrivatePayloadAckMessage", "ToAck")]
        public void PrivateMessages_RoundTripThroughMirrorAndFitReliableTransports(string name, string convertBack)
        {
            var transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            var messageType = transportType.Assembly.GetType("YC.Infrastructure.Multiplayer." + name, true);
            var writerType = Type.GetType("Mirror.NetworkWriter, Mirror", true);
            var readerType = Type.GetType("Mirror.NetworkReader, Mirror", true);
            var messagesType = Type.GetType("Mirror.NetworkMessages, Mirror", true);
            var activeTransport = Type.GetType("Mirror.Transport, Mirror", true).GetField("active");
            var previous = activeTransport.GetValue(null);
            var root = new GameObject("私密消息传输容量夹具"); root.SetActive(false);
            try
            {
                object source;
                var payload = Enumerable.Range(0, GameStateViewTransfer.ChunkBytes).Select(index => (byte)(index % 251)).ToArray();
                switch (name)
                {
                    case "PrivateViewPartMessage": source = new PrivatePlayerViewPart { GameId = "fixture-game", ContentPackHash = "fixture-content",
                        ConnectionToken = "fixture-token", Digest = new string('a', 64), SessionEpoch = 2, TransferId = 7,
                        PlayerId = 3, TotalBytes = payload.Length, Offset = 0, IsInitial = true, Payload = payload }; break;
                    case "PrivateViewAckMessage": source = new PrivatePlayerViewAcknowledgment { GameId = "fixture-game", ConnectionToken = "fixture-token",
                        SessionEpoch = 2, TransferId = 7, PlayerId = 3, ReceivedBytes = payload.Length }; break;
                    case "PrivateViewInstalledMessage": source = new PrivateViewInstalledReceipt { GameId = "fixture-game", ContentPackHash = "fixture-content",
                        ConnectionToken = "fixture-token", SessionEpoch = 2, MembershipVersion = 4, PlayerId = 3, TransferId = 7,
                        CommittedSequence = 12, StateRevision = 15 }; break;
                    case "PrivatePayloadPartMessage": source = new PrivatePayloadPart { GameId = "fixture-game", ContentPackHash = "fixture-content",
                        ConnectionToken = "fixture-token", Purpose = "command", Digest = new string('b', 64), SessionEpoch = 2, TransferId = 7,
                        MembershipVersion = 4, PlayerId = 3, TotalBytes = payload.Length, Offset = 0, Payload = payload }; break;
                    default: source = new PrivatePayloadAcknowledgment { GameId = "fixture-game", ContentPackHash = "fixture-content",
                        ConnectionToken = "fixture-token", Purpose = "result", SessionEpoch = 2, TransferId = 7,
                        MembershipVersion = 4, PlayerId = 3, ReceivedBytes = payload.Length }; break;
                }
                var message = messageType.GetMethod("From", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { source });
                var writer = Activator.CreateInstance(writerType);
                messagesType.GetMethod("Pack").MakeGenericMethod(messageType).Invoke(null, new[] { message, writer });
                var bytes = (byte[])writerType.GetMethod("ToArray").Invoke(writer, null);
                foreach (string transport in new[] { "Mirror.TelepathyTransport, Mirror.Transports", "Mirror.FizzySteam.FizzySteamworks, FizzySteamworks" })
                {
                    var type = Type.GetType(transport, false) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(transport.Split(',')[0])).First(t => t != null);
                    activeTransport.SetValue(null, root.AddComponent(type));
                    var limit = (int)messagesType.GetMethod("MaxMessageSize").Invoke(null, new object[] { 0 });
                    Assert.That(bytes.Length, Is.LessThanOrEqualTo(limit), transport);
                }
                var reader = Activator.CreateInstance(readerType, new object[] { new ArraySegment<byte>(bytes) });
                readerType.GetField("Position").SetValue(reader, (int)messagesType.GetField("IdSize").GetRawConstantValue());
                var decoded = readerType.GetMethod("Read").MakeGenericMethod(messageType).Invoke(reader, null);
                Assert.That(readerType.GetProperty("Remaining").GetValue(reader), Is.EqualTo(0));
                var restored = messageType.GetMethod(convertBack).Invoke(decoded, null);
                Assert.That(JsonConvert.SerializeObject(restored), Is.EqualTo(JsonConvert.SerializeObject(source)));
            }
            finally
            {
                activeTransport.SetValue(null, previous);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [TestCase("PrivateRoomStatusMessage")]
        [TestCase("PrivateTerminalAckMessage")]
        [TestCase("PrivateReconnectMessage")]
        [TestCase("PrivateRegionPreviewRequestMessage")]
        [TestCase("PrivateRegionPreviewResponseMessage")]
        public void PrivateControlMessages_RoundTripThroughMirror(string name)
        {
            var transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            var messageType = transportType.Assembly.GetType("YC.Infrastructure.Multiplayer." + name, true);
            var writerType = Type.GetType("Mirror.NetworkWriter, Mirror", true);
            var readerType = Type.GetType("Mirror.NetworkReader, Mirror", true);
            var messagesType = Type.GetType("Mirror.NetworkMessages, Mirror", true);
            var message = Activator.CreateInstance(messageType);
            foreach (var field in messageType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.FieldType == typeof(string)) field.SetValue(message, field.Name + "-fixture");
                else if (field.FieldType == typeof(long)) field.SetValue(message, 17L);
                else if (field.FieldType == typeof(int)) field.SetValue(message, 2);
                else if (field.FieldType == typeof(bool)) field.SetValue(message, true);
                else Assert.Fail("未覆盖的控制消息字段：" + field.Name);
            }
            var writer = Activator.CreateInstance(writerType);
            messagesType.GetMethod("Pack").MakeGenericMethod(messageType).Invoke(null, new[] { message, writer });
            var bytes = (byte[])writerType.GetMethod("ToArray").Invoke(writer, null);
            var reader = Activator.CreateInstance(readerType, new object[] { new ArraySegment<byte>(bytes) });
            readerType.GetField("Position").SetValue(reader, (int)messagesType.GetField("IdSize").GetRawConstantValue());
            var restored = readerType.GetMethod("Read").MakeGenericMethod(messageType).Invoke(reader, null);
            Assert.That(readerType.GetProperty("Remaining").GetValue(reader), Is.EqualTo(0));
            Assert.That(JsonConvert.SerializeObject(restored), Is.EqualTo(JsonConvert.SerializeObject(message)));
        }

        [Test]
        public void RegionPreviewPart_RoundTripsWithinTelepathyPacketCapacity()
        {
            var transportType = Type.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport, Assembly-CSharp", true);
            transportType.Assembly.GetType("Mirror.GeneratedNetworkCode", true)
                .GetMethod("InitReadWriters", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            var messageType = transportType.Assembly.GetType("YC.Infrastructure.Multiplayer.PrivateRegionPreviewResponseMessage", true);
            var writerType = Type.GetType("Mirror.NetworkWriter, Mirror", true);
            var readerType = Type.GetType("Mirror.NetworkReader, Mirror", true);
            var messagesType = Type.GetType("Mirror.NetworkMessages, Mirror", true);
            var activeTransport = Type.GetType("Mirror.Transport, Mirror", true).GetField("active");
            var previous = activeTransport.GetValue(null);
            var root = new GameObject("区控预览分段容量夹具"); root.SetActive(false);
            try
            {
                activeTransport.SetValue(null, root.AddComponent(Type.GetType("Mirror.TelepathyTransport, Mirror.Transports", true)));
                int limit = (int)messagesType.GetMethod("MaxMessageSize").Invoke(null, new object[] { 0 });
                var payload = new PrivateRegionPreviewPayload(System.Text.Encoding.UTF8.GetBytes(new string('区', 20000)));
                var message = Activator.CreateInstance(messageType);
                foreach (var pair in new[] { ("GameId", new string('g', 32)), ("ContentHash", new string('h', 64)),
                    ("Token", new string('t', 32)), ("RequestId", new string('r', 32)),
                    ("Payload", payload.ReadPart(0)), ("Failure", ""), ("Digest", payload.Digest) })
                    messageType.GetField(pair.Item1).SetValue(message, pair.Item2);
                foreach (var pair in new[] { ("Membership", 1), ("PlayerId", 1), ("Revision", 2), ("Part", 0), ("PartCount", payload.PartCount) })
                    messageType.GetField(pair.Item1).SetValue(message, pair.Item2);
                messageType.GetField("Epoch").SetValue(message, 1L);
                var writer = Activator.CreateInstance(writerType);
                messagesType.GetMethod("Pack").MakeGenericMethod(messageType).Invoke(null, new[] { message, writer });
                var bytes = (byte[])writerType.GetMethod("ToArray").Invoke(writer, null);
                Assert.That(bytes.Length, Is.LessThanOrEqualTo(limit));
                var reader = Activator.CreateInstance(readerType, new object[] { new ArraySegment<byte>(bytes) });
                readerType.GetField("Position").SetValue(reader, (int)messagesType.GetField("IdSize").GetRawConstantValue());
                var restored = readerType.GetMethod("Read").MakeGenericMethod(messageType).Invoke(reader, null);
                Assert.That(JsonConvert.SerializeObject(restored), Is.EqualTo(JsonConvert.SerializeObject(message)));
            }
            finally { activeTransport.SetValue(null, previous); UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
