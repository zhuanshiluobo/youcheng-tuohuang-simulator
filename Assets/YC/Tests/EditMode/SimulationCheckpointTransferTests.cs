using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
#if YC_CHECKPOINT_OFFLINE
using Assert = YC.CheckpointTransportOffline.OfflineAssert;
#endif

namespace YC.Tests.EditMode
{
    public sealed class SimulationCheckpointTransferTests
    {
        private static Fixture fixture;
        private static Fixture Shared => fixture ?? (fixture = new Fixture("checkpoint-transfer-match"));
        public static string InitialArchiveJson => Shared.ArchiveJson;
        public static SimulationCheckpointEnvelope InitialIdentity => Shared.Identity;
        public static GameSession InitialHostSession => Shared.Host.Session;
        public static SimulationCheckpointEnvelope TerminalMeasuredEnvelope { get; private set; }
        public static GameSession CreateDeferredClient(out EffectRegistry registry)
        { var built = Shared.Build(LaunchMode.Client, true); registry = built.Registry; return built.Session; }

        [Test]
        public void HostChannelInstallsRealCompleteArchiveOnceAndConfirmsOnlyBoundPeer()
        {
            var f = Shared; var client = f.Build(LaunchMode.Client, true); int buildCalls = 0;
            var host = new Channel(f.Identity, null, () => { buildCalls++; return f.ArchiveJson; });
            Assert.That(host.Create(99, "peer-token", f.Identity, out _, out _), Is.False);
            Assert.That(buildCalls, Is.EqualTo(0));
            Assert.That(host.Create(2, "peer-token", f.Identity, out var transfer, out _), Is.True);
            Assert.That(host.Create(2, "peer-token", f.Identity, out var repeat, out _), Is.True);
            Assert.That(transfer, Is.SameAs(repeat)); Assert.That(buildCalls, Is.EqualTo(1));
            var receiver = new Channel(f.Identity, new InitialSimulationCheckpointInstaller(client.Session, client.Registry, GameStateViewer.Player(2)), null);
            receiver.Bind("peer-token"); var before = GameStateCloneService.DeepClone(client.Session.State);
            var parts = transfer.Split().ToList(); Assert.That(parts.Count > 1, Is.True);
            for (int index = 0; index < parts.Count - 1; index++)
                Assert.That(receiver.Receive("peer-token", parts[index]).Status, Is.EqualTo("Buffered"));
            Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, before), Is.True); Assert.That(client.Session.View, Is.Null);
            var installed = receiver.Receive("peer-token", parts.Last());
            Assert.That(installed.Status, Is.EqualTo("Installed")); Assert.That(installed.InstallResult.Succeeded, Is.True);
            Assert.That(installed.InstallResult.ArchiveDigest, Is.EqualTo(transfer.Envelope.ArchiveDigest));
            Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, f.Host.Session.State), Is.True);
            Assert.That(client.Session.State.FindPlayer(1).HandCardIds.Count > 0, Is.True);
            Assert.That(client.Session.View.Players.Single(p => p.PlayerId == 1).HandCardIds, Is.Empty);
            Assert.That(client.Session.View.ViewerPlayerId, Is.EqualTo(2));
            var view = client.Session.View; client.Session.State.EffectRuntime.StateRevision++;
            foreach (var part in transfer.Split()) installed = receiver.Receive("peer-token", part);
            Assert.That(installed.Status, Is.EqualTo("AlreadyInstalled"));
            Assert.That(client.Session.View, Is.SameAs(view));
            Assert.That(client.Session.State.EffectRuntime.StateRevision, Is.EqualTo(f.Host.Session.State.EffectRuntime.StateRevision + 1));
            Assert.That(host.Confirm(2, "wrong-token", transfer.Envelope, out _), Is.False);
            Assert.That(host.Confirm(2, "peer-token", transfer.Envelope, out var duplicate), Is.True); Assert.That(duplicate, Is.False);
            Assert.That(host.Confirm(2, "peer-token", transfer.Envelope, out duplicate), Is.True); Assert.That(duplicate, Is.True);
            Assert.That(host.IsPeerInstalled(2), Is.True);
            var changed = transfer.Split().First(); changed.Envelope.ArchiveDigest = new string('0', 64);
            Assert.That(receiver.Receive("peer-token", changed).FaultCode, Is.EqualTo("checkpoint_installed_conflict"));
        }

        [Test]
        public void ForeignMixedCorruptAndTruncatedTransfersNeverPartiallyInstall()
        {
            var f = Shared; var transfer = SimulationCheckpointTransfer.Encode(f.Identity, f.ArchiveJson);
            foreach (string mutation in new[] { "game", "epoch", "purpose", "mixed", "corrupt", "truncated", "host-token" })
            {
                var client = f.Build(LaunchMode.Client, true); var before = GameStateCloneService.DeepClone(client.Session.State);
                var receiver = new Channel(f.Identity, new InitialSimulationCheckpointInstaller(client.Session, client.Registry, GameStateViewer.Player(2)), null);
                receiver.Bind("peer-token"); var parts = transfer.Split().ToList();
                if (mutation == "truncated")
                {
                    var bytes = transfer.CopyEncodedBytes(); Array.Resize(ref bytes, bytes.Length - 4);
                    var envelope = transfer.Envelope; envelope.EncodedByteCount = bytes.Length; envelope.EncodedDigest = Digest(bytes);
                    Assert.That(Append(receiver, envelope, bytes).Status, Is.EqualTo("Rejected"));
                }
                else if (mutation == "mixed")
                {
                    Assert.That(receiver.Receive("peer-token", parts[0]).Status, Is.EqualTo("Buffered"));
                    parts[1].Envelope.CheckpointId = "different-checkpoint";
                    Assert.That(receiver.Receive("peer-token", parts[1]).Status, Is.EqualTo("Rejected"));
                }
                else if (mutation == "corrupt")
                {
                    parts[0].Payload[12] ^= 1; Received result = null;
                    foreach (var part in parts) result = receiver.Receive("peer-token", part);
                    Assert.That(result.FaultCode, Is.EqualTo("checkpoint_encoded_digest_mismatch"));
                }
                else
                {
                    if (mutation == "game") parts[0].Envelope.GameId = "other-game";
                    if (mutation == "epoch") parts[0].Envelope.SimulationEpoch++;
                    if (mutation == "purpose") parts[0].Envelope.Purpose = "RuntimeRecovery";
                    Assert.That(receiver.Receive(mutation == "host-token" ? "unbound-host" : "peer-token", parts[0]).Status, Is.EqualTo("Rejected"));
                }
                Assert.That(client.Session.View, Is.Null);
                Assert.That(GameStateCloneService.AreEquivalent(client.Session.State, before), Is.True);
            }
        }

        [Test]
        public void EncodedDeclaredAndIncrementalDecodedLimitsRemainIndependentFromLegacyProjection()
        {
            var f = Shared; var transfer = SimulationCheckpointTransfer.Encode(f.Identity, f.ArchiveJson);
            var envelope = transfer.Envelope; var first = transfer.Split().First(); var assembler = new SimulationCheckpointTransfer.Assembler(f.Identity);
            envelope.EncodedByteCount = SimulationCheckpointTransfer.MaximumEncodedBytes + 1;
            Assert.That(assembler.TryAppend(envelope, 0, first.Payload, out _, out _), Is.False); Assert.That(assembler.BufferedBytes, Is.EqualTo(0));
            envelope = transfer.Envelope; envelope.DecodedByteCount = SimulationCheckpointTransfer.MaximumDecodedBytes + 1;
            Assert.That(assembler.TryAppend(envelope, 0, first.Payload, out _, out _), Is.False);
            envelope = transfer.Envelope; envelope.DecodedByteCount--;
            Assert.That(Append(assembler, envelope, transfer.CopyEncodedBytes(), out _), Is.False);
            envelope = transfer.Envelope; envelope.ArchiveDigest = new string('0', 64);
            Assert.That(Append(assembler, envelope, transfer.CopyEncodedBytes(), out var digestFault), Is.False);
            Assert.That(digestFault, Is.EqualTo("checkpoint_archive_digest_mismatch"));
            var expansion = GzipZeros(SimulationCheckpointTransfer.MaximumDecodedBytes + 32768);
            envelope = transfer.Envelope; envelope.EncodedByteCount = expansion.Length; envelope.EncodedDigest = Digest(expansion);
            envelope.DecodedByteCount = SimulationCheckpointTransfer.MaximumDecodedBytes;
            Assert.That(Append(assembler, envelope, expansion, out var limitFault), Is.False);
            Assert.That(limitFault, Is.EqualTo("checkpoint_decoded_limit")); Assert.That(assembler.BufferedBytes, Is.EqualTo(0));
            var entropy = new byte[SimulationCheckpointTransfer.MaximumEncodedBytes + 262144]; new System.Random(17).NextBytes(entropy);
            Rejects(() => SimulationCheckpointTransfer.Encode(f.Identity, Convert.ToBase64String(entropy)));
            Rejects(() => SimulationCheckpointTransfer.Encode(f.Identity, "\ud800"));
            var unicodeIdentity = f.Identity; unicodeIdentity.GameId += "\ud83d\ude80";
            Assert.That(unicodeIdentity.HasValidIdentity(), Is.True);
            unicodeIdentity.GameId = "\ud800"; Assert.That(unicodeIdentity.HasValidIdentity(), Is.False);
            Assert.That(GameStateViewTransfer.MaximumBytes, Is.EqualTo(8 * 1024 * 1024));
            Assert.That(GameStateViewTransfer.ChunkBytes, Is.EqualTo(8 * 1024));
        }

        [Test]
        public void StopAndNewGameIsolateBuffersAndInstallerFailuresRemainOriginal()
        {
            var f = Shared; var transfer = SimulationCheckpointTransfer.Encode(f.Identity, f.ArchiveJson);
            var client = f.Build(LaunchMode.Client, true);
            var receiver = new Channel(f.Identity, new InitialSimulationCheckpointInstaller(client.Session, client.Registry, GameStateViewer.Player(2)), null);
            receiver.Bind("peer-token"); receiver.Receive("peer-token", transfer.Split().First());
            Assert.That(receiver.BufferedBytes > 0, Is.True); receiver.Stop(); Assert.That(receiver.BufferedBytes, Is.EqualTo(0));
            Assert.That(receiver.Receive("peer-token", transfer.Split().First()).Status, Is.EqualTo("Stopped")); Assert.That(client.Session.View, Is.Null);
            var other = new Fixture("new-checkpoint-game"); var next = other.Build(LaunchMode.Client, true);
            var nextReceiver = new Channel(other.Identity, new InitialSimulationCheckpointInstaller(next.Session, next.Registry, GameStateViewer.Player(2)), null);
            nextReceiver.Bind("new-token"); Assert.That(nextReceiver.Receive("new-token", transfer.Split().First()).Status, Is.EqualTo("Rejected"));
            Assert.That(nextReceiver.BufferedBytes, Is.EqualTo(0));
            var nextTransfer = SimulationCheckpointTransfer.Encode(other.Identity, other.ArchiveJson); Received success = null;
            foreach (var part in nextTransfer.Split()) success = nextReceiver.Receive("new-token", part);
            Assert.That(success.Status, Is.EqualTo("Installed")); Assert.That(next.Session.State.GameId, Is.EqualTo(other.GameId));
            var failedClient = f.Build(LaunchMode.Client, true);
            var failing = new Channel(f.Identity, new InitialSimulationCheckpointInstaller(failedClient.Session, failedClient.Registry, GameStateViewer.Player(2)), null);
            failing.Bind("peer-token"); var bad = SimulationCheckpointTransfer.Encode(f.Identity, "{broken"); Received failed = null;
            foreach (var part in bad.Split()) failed = failing.Receive("peer-token", part);
            Assert.That(failed.Status, Is.EqualTo("Rejected")); Assert.That(failed.InstallResult.Succeeded, Is.False);
            Assert.That(failed.FaultCode, Is.EqualTo(failed.InstallResult.FaultCode)); Assert.That(failing.IsStopped, Is.True);
            Assert.That(failedClient.Session.View, Is.Null);
        }

        // 显式输入的正式终档只验证编码/往返；不调用Installer、不作为Unity自动测试资源。
        public static void VerifyTerminalArchiveRoundTrip(string archiveJson)
        {
            var identity = Shared.Identity; identity.CheckpointId = "terminal-size-measurement";
            var transfer = SimulationCheckpointTransfer.Encode(identity, archiveJson);
            TerminalMeasuredEnvelope = transfer.Envelope;
            var assembler = new SimulationCheckpointTransfer.Assembler(identity); string decoded = null;
            bool accepted = true;
            foreach (var part in transfer.Split())
                if (!assembler.TryAppend(part.Envelope, part.Offset, part.Payload, out decoded, out _)) { accepted = false; break; }
            Assert.That(accepted, Is.True);
            Assert.That(transfer.Envelope.DecodedByteCount, Is.EqualTo(39755601));
            Assert.That(string.Equals(archiveJson, decoded, StringComparison.Ordinal), Is.True);
        }

        private static Received Append(Channel channel, SimulationCheckpointEnvelope envelope, byte[] bytes)
        {
            Received result = null;
            for (int offset = 0; offset < bytes.Length; offset += SimulationCheckpointTransfer.ChunkBytes)
            {
                int count = Math.Min(SimulationCheckpointTransfer.ChunkBytes, bytes.Length - offset); var payload = new byte[count];
                Buffer.BlockCopy(bytes, offset, payload, 0, count); result = channel.Receive("peer-token", envelope, offset, payload);
            }
            return result;
        }
        private static bool Append(SimulationCheckpointTransfer.Assembler assembler, SimulationCheckpointEnvelope envelope, byte[] bytes, out string fault)
        {
            fault = null;
            for (int offset = 0; offset < bytes.Length; offset += SimulationCheckpointTransfer.ChunkBytes)
            {
                int count = Math.Min(SimulationCheckpointTransfer.ChunkBytes, bytes.Length - offset); var payload = new byte[count]; Buffer.BlockCopy(bytes, offset, payload, 0, count);
                if (!assembler.TryAppend(envelope, offset, payload, out _, out fault)) return false;
            }
            return true;
        }
        private static byte[] GzipZeros(int count)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
                { var block = new byte[32768]; for (int written = 0; written < count; written += block.Length) gzip.Write(block, 0, Math.Min(block.Length, count - written)); }
                return output.ToArray();
            }
        }
        private static string Digest(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static void Rejects(Action action)
        { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Assert.That(rejected, Is.True); }

        private sealed class Received
        {
            internal string Status, FaultCode; internal InitialSimulationCheckpointInstallResult InstallResult;
            internal Received(object value)
            { var type = value.GetType(); Status = type.GetProperty("Status").GetValue(value).ToString(); FaultCode = (string)type.GetProperty("FaultCode").GetValue(value); InstallResult = (InitialSimulationCheckpointInstallResult)type.GetProperty("InstallResult").GetValue(value); }
        }
        // Assembly-CSharp只能由Editor测试反射访问；对生产Channel调用真实API。
        private sealed class Channel
        {
            private readonly object value; private readonly Type type;
            internal Channel(SimulationCheckpointEnvelope identity, InitialSimulationCheckpointInstaller installer, Func<string> factory)
            { type = Type.GetType("YC.Infrastructure.Multiplayer.SimulationCheckpointChannel, Assembly-CSharp", true); value = Activator.CreateInstance(type, identity, installer, factory, new[] { 1, 2, 3 }); }
            internal void Bind(string token) => type.GetMethod("BindClientConnection").Invoke(value, new object[] { token });
            internal void Stop() => type.GetMethod("Stop").Invoke(value, null);
            internal bool IsStopped => (bool)type.GetProperty("IsStopped").GetValue(value);
            internal int BufferedBytes => (int)type.GetProperty("BufferedBytes").GetValue(value);
            internal bool IsPeerInstalled(int player) => (bool)type.GetMethod("IsPeerInstalled").Invoke(value, new object[] { player });
            internal Received Receive(string token, SimulationCheckpointPart part) => Receive(token, part.Envelope, part.Offset, part.Payload);
            internal Received Receive(string token, SimulationCheckpointEnvelope envelope, int offset, byte[] payload) =>
                new Received(type.GetMethod("ReceivePart").Invoke(value, new object[] { token, envelope, offset, payload }));
            internal bool Create(int player, string token, SimulationCheckpointEnvelope request, out SimulationCheckpointEncodedTransfer transfer, out string fault)
            { object[] args = { player, token, request, null, null }; bool result = (bool)type.GetMethod("TryCreateHostTransfer").Invoke(value, args); transfer = (SimulationCheckpointEncodedTransfer)args[3]; fault = (string)args[4]; return result; }
            internal bool Confirm(int player, string token, SimulationCheckpointEnvelope envelope, out bool duplicate)
            { object[] args = { player, token, envelope, false, null }; bool result = (bool)type.GetMethod("TryConfirmInstalled").Invoke(value, args); duplicate = (bool)args[3]; return result; }
        }
        private sealed class Built { internal GameSession Session; internal EffectRegistry Registry; }
        private sealed class Fixture
        {
            internal readonly string GameId; internal readonly Built Host; internal readonly string ArchiveJson;
            private readonly GameRuleConfiguration configuration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3 };
            private readonly List<PlayerSeat> players = new List<PlayerSeat> { new PlayerSeat { PlayerId = 1, PlayerName = "玩家1" }, new PlayerSeat { PlayerId = 2, PlayerName = "玩家2" }, new PlayerSeat { PlayerId = 3, PlayerName = "玩家3" } };
            private readonly List<int> order = new List<int> { 1, 2, 3 }; private readonly GameplayRandomState random;
            internal SimulationCheckpointEnvelope Identity => new SimulationCheckpointEnvelope { GameId = GameId, SimulationEpoch = 1, MembershipVersion = 1,
                CheckpointId = "initial-checkpoint", ContentPackHash = Host.Session.State.ContentPackHash };
            internal Fixture(string gameId)
            {
                GameId = gameId; var state = new GameState { GameId = GameId, EventDeckSeed = 42, RandomRuntime = GameplayRandomRuntime.Create(GameId) };
                GameplayRandomRuntime.Shuffle(order, state, GameplayRandomRuntime.LaunchOrder); random = state.RandomRuntime;
                Host = Build(LaunchMode.Host, false); configuration.ContentPackHash = Host.Session.State.ContentPackHash;
                ArchiveJson = HostRecoveryService.SerializeArchive(Host.Session.CreateHostArchive());
            }
            internal Built Build(LaunchMode mode, bool defer)
            {
                var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
                var bootstrapperType = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var instance = contextType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public); var previous = instance.GetValue(null); var setter = instance.GetSetMethod(true);
                GameObject gameObject = null;
                try
                {
                    setter.Invoke(null, new object[] { null }); gameObject = new GameObject("SimulationCheckpointTransferCheck"); var context = gameObject.AddComponent(contextType);
                    contextType.GetMethods().Single(m => m.Name == "Configure" && m.GetParameters().Length == 9).Invoke(context,
                        new object[] { mode, 2, "checkpoint-transfer-room", players, configuration, order, GameId, 42, random.Clone() });
                    var build = defer ? bootstrapperType.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic, null,
                        new[] { contextType, typeof(bool), typeof(bool), typeof(bool) }, null) : bootstrapperType.GetMethod("Build", BindingFlags.Static | BindingFlags.Public);
                    var value = build.Invoke(null, defer ? new object[] { context, false, false, true } : new object[] { context, false, false });
                    return new Built { Session = (GameSession)value.GetType().GetField("Session").GetValue(value), Registry = (EffectRegistry)value.GetType().GetField("EffectRegistry").GetValue(value) };
                }
                finally { if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject); setter.Invoke(null, new[] { previous }); }
            }
        }
    }
}
