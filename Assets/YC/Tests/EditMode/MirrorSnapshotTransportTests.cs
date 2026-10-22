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
    }
}
