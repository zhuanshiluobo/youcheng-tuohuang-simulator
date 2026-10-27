using System;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    // 新链路使用连接身份与检查点重传，不再调用旧分派器的视图快照窗口。
    public sealed class MirrorSnapshotWindowTests
    {
        [Test]
        public void RebindingCheckpointConnectionDropsOldBufferedPartsAndRejectsLatePackets()
        {
            var identity = SimulationCheckpointTransferTests.InitialIdentity;
            var transfer = SimulationCheckpointTransfer.Encode(identity, SimulationCheckpointTransferTests.InitialArchiveJson);
            var parts = transfer.Split().ToArray();
            Assert.That(parts.Length, Is.GreaterThan(1));
            var client = SimulationCheckpointTransferTests.CreateDeferredClient(out var registry);
            var type = Type.GetType("YC.Infrastructure.Multiplayer.SimulationCheckpointChannel, Assembly-CSharp", true);
            var installer = new InitialSimulationCheckpointInstaller(client, registry, GameStateViewer.Player(2));
            var channel = Activator.CreateInstance(type, identity, installer, null, new[] { 1, 2, 3 });
            object Call(string method, params object[] args) => type.GetMethod(method).Invoke(channel, args);
            int Buffered() => (int)type.GetProperty("BufferedBytes").GetValue(channel);
            string Receive(string token, SimulationCheckpointPart part)
            {
                var result = Call("ReceivePart", token, part.Envelope, part.Offset, part.Payload);
                return result.GetType().GetProperty("Status").GetValue(result).ToString();
            }
            Call("BindClientConnection", "old-connection");
            Assert.That(Receive("old-connection", parts[0]), Is.EqualTo("Buffered"));
            Assert.That(Buffered(), Is.GreaterThan(0));
            Call("BindClientConnection", "new-connection");
            Assert.That(Buffered(), Is.Zero);
            Assert.That(Receive("old-connection", parts[1]), Is.EqualTo("Rejected"));
            Assert.That(Buffered(), Is.Zero);
            string status = null;
            foreach (var part in parts) status = Receive("new-connection", part);
            Assert.That(status, Is.EqualTo("Installed"));
            var view = client.View;
            foreach (var part in parts) status = Receive("new-connection", part);
            Assert.That(status, Is.EqualTo("AlreadyInstalled"));
            Assert.That(client.View, Is.SameAs(view));
            Assert.That(Receive("old-connection", parts.Last()), Is.EqualTo("Rejected"));
            Assert.That(client.View, Is.SameAs(view));
            Call("Stop");
        }
    }
}
