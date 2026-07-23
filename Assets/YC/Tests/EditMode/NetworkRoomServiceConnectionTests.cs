using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class NetworkRoomServiceConnectionTests
    {
        [Test]
        public void NormalClientClose_ThreePlayerRoom_ReleasesSeatAndRaisesOneUpdate()
        {
            using (var service = CreateHost(3))
            {
                var reader = new ControlledDisconnectReader("JOIN|Client 2");
                var connection = new NetworkRoomService.ClientConnection(reader, new RecordingWriter());
                var clientLoop = Task.Run(() => service.HostClientLoop(connection));
                Assert.IsTrue(reader.WaitingForDisconnect.Wait(2000), "客户端读取循环没有进入等待断线状态。");
                service.SetTransportReadiness(connection.PlayerId, true, true, (ulong)connection.PlayerId);
                var updateCount = 0;
                service.RoomUpdated += _ => updateCount++;

                reader.AllowDisconnect.Set();
                Assert.IsTrue(clientLoop.Wait(3000), "客户端读取循环未在断线后退出。");
                service.RemoveClientConnection(connection);

                Assert.AreEqual(0, service.HostClientCount);
                AssertSeat(service, 2, "Player 2", false);
                Assert.AreEqual(1, updateCount);
                Assert.IsTrue(connection.IsDisposed);
            }
        }

        [Test]
        public void BroadcastIOException_ReleasesSeatAndBroadcastsLatestSnapshot()
        {
            AssertBroadcastFailure(new IOException("write failed"), 3);
        }

        [Test]
        public void BroadcastObjectDisposedException_FourPlayerRoom_MatchesIOExceptionBehavior()
        {
            AssertBroadcastFailure(new ObjectDisposedException("writer"), 4);
        }

        [Test]
        public void ReadLoopAndBroadcastDiscoverDisconnectTogether_CleanupIsIdempotent()
        {
            using (var service = CreateHost(3))
            {
                var reader = new ControlledDisconnectReader("JOIN|Client 2");
                var writer = new BlockingThrowWriter(new IOException("simulated concurrent failure"));
                var connection = new NetworkRoomService.ClientConnection(reader, writer);
                var clientLoop = Task.Run(() => service.HostClientLoop(connection));
                Assert.IsTrue(reader.WaitingForDisconnect.Wait(2000), "客户端读取循环没有进入等待断线状态。");
                service.SetTransportReadiness(connection.PlayerId, true, true, (ulong)connection.PlayerId);
                var otherConnection = AddClient(service, "Client 3");
                var updateCount = 0;
                service.RoomUpdated += room =>
                {
                    updateCount++;
                    Assert.NotNull(service.GetCurrentRoom());
                };

                writer.BlockWrites = true;
                var broadcastTask = Task.Run(() => service.Broadcast("ROOM|stale"));
                Assert.IsTrue(writer.WriteEntered.Wait(2000), "广播没有进入模拟写入点。");
                reader.AllowDisconnect.Set();
                writer.AllowFailure.Set();

                Assert.IsTrue(Task.WaitAll(new[] { broadcastTask, clientLoop }, 3000), "并发清理未在超时内结束。");
                Assert.AreEqual(1, service.HostClientCount);
                AssertSeat(service, 2, "Player 2", false);
                AssertSeat(service, 3, "Client 3", true);
                Assert.AreEqual(1, updateCount);
                Assert.IsFalse(otherConnection.IsDisposed);
            }
        }

        [Test]
        public void DisconnectThenStartGame_IsRejectedWithoutSettingHasStarted()
        {
            using (var service = CreateHost(3))
            {
                AddClient(service, "Client 2");
                var disconnected = AddClient(service, "Client 3");
                service.RemoveClientConnection(disconnected);
                string error = null;
                service.ErrorOccurred += message => error = message;

                service.StartGame();

                Assert.IsNotEmpty(error);
                StringAssert.Contains("等待", error);
                Assert.IsFalse(service.GetCurrentRoom().HasStarted);
            }
        }

        [Test]
        public void FullyReadyRoom_TryStartGame_BroadcastsStartedRoomAndRaisesEvent()
        {
            using (var service = CreateHost(4))
            {
                AddClient(service, "Client 2");
                AddClient(service, "Client 3");
                AddClient(service, "Client 4");
                RoomState startedRoom = null;
                service.GameStarted += room => startedRoom = room;

                var started = service.TryStartGame(out var error);

                Assert.IsTrue(started);
                Assert.IsTrue(string.IsNullOrEmpty(error));
                Assert.NotNull(startedRoom);
                Assert.IsTrue(startedRoom.HasStarted);
                Assert.IsTrue(service.GetCurrentRoom().HasStarted);
            }
        }

        [Test]
        public void RejoinReusesSeat_AndLateOldCleanupDoesNotClearNewOwner()
        {
            using (var service = CreateHost(4))
            {
                var oldConnection = AddClient(service, "Old Client");
                var updateCount = 0;
                service.RoomUpdated += _ => updateCount++;
                service.RemoveClientConnection(oldConnection);

                var newConnection = AddClient(service, "New Client");
                Assert.AreEqual(2, newConnection.PlayerId);
                var updatesBeforeLateCleanup = updateCount;
                service.RemoveClientConnection(oldConnection);

                AssertSeat(service, 2, "New Client", true);
                Assert.AreEqual(1, service.HostClientCount);
                Assert.AreEqual(updatesBeforeLateCleanup, updateCount);
                Assert.IsFalse(newConnection.IsDisposed);
            }
        }

        [Test]
        public void LocalMirrorIdentityTicket_IsHostIssuedAndRevokedWithSignalingConnection()
        {
            using (var service = CreateHost(3))
            {
                var first = new NetworkRoomService.ClientConnection(
                    new StringReader(string.Empty),
                    new RecordingWriter());
                Assert.AreEqual(2, service.AssignSeat("Client 2", first));
                var firstTicket = first.IdentityTicket;

                Assert.IsNotEmpty(firstTicket);
                Assert.IsTrue(service.TryResolveIdentityTicket(firstTicket, out var playerId));
                Assert.AreEqual(2, playerId);
                Assert.IsFalse(service.TryResolveIdentityTicket("2", out _));
                Assert.IsFalse(service.TryResolveIdentityTicket(System.Guid.NewGuid().ToString("N"), out _));

                service.RemoveClientConnection(first);
                Assert.IsFalse(service.TryResolveIdentityTicket(firstTicket, out _));

                var reconnected = new NetworkRoomService.ClientConnection(
                    new StringReader(string.Empty),
                    new RecordingWriter());
                Assert.AreEqual(2, service.AssignSeat("Client 2 Reconnected", reconnected));
                Assert.AreNotEqual(firstTicket, reconnected.IdentityTicket);
                Assert.IsTrue(service.TryResolveIdentityTicket(reconnected.IdentityTicket, out playerId));
                Assert.AreEqual(2, playerId);
            }
        }

        [Test]
        public void Shutdown_ClosesConnectionsWithoutDisconnectEventsOrThreads()
        {
            var service = CreateHost(4);
            var connections = new[]
            {
                AddClient(service, "Client 2"),
                AddClient(service, "Client 3"),
                AddClient(service, "Client 4")
            };
            var roomUpdates = 0;
            var roomDisbanded = 0;
            var errors = 0;
            service.RoomUpdated += _ => roomUpdates++;
            service.RoomDisbanded += () => roomDisbanded++;
            service.ErrorOccurred += _ => errors++;

            Assert.DoesNotThrow(service.Shutdown);
            Assert.DoesNotThrow(service.Shutdown);

            Assert.AreEqual(0, service.HostClientCount);
            Assert.IsNull(service.GetCurrentRoom());
            Assert.IsFalse(service.HasActiveBackgroundThreads);
            Assert.AreEqual(0, roomUpdates);
            Assert.AreEqual(0, roomDisbanded);
            Assert.AreEqual(0, errors);
            Assert.IsTrue(connections.All(connection => connection.IsDisposed));
            service.Dispose();
        }

        [Test]
        public void StartedGameDisconnect_KeepsSeatButReleasesSignalingConnection()
        {
            using (var service = CreateHost(3))
            {
                var connection = AddClient(service, "Client 2");
                AddClient(service, "Client 3");
                service.StartGame();
                var updateCount = 0;
                service.RoomUpdated += _ => updateCount++;

                service.RemoveClientConnection(connection);

                AssertSeat(service, 2, "Client 2", true);
                Assert.AreEqual(1, service.HostClientCount);
                Assert.AreEqual(0, updateCount);
                Assert.IsTrue(connection.IsDisposed);
            }
        }

        private static void AssertBroadcastFailure(Exception exception, int playerCount)
        {
            using (var service = CreateHost(playerCount))
            {
                var failingWriter = new SwitchableThrowWriter();
                var failingConnection = AddClient(service, "Failing Client", failingWriter);
                var remainingWriter = new RecordingWriter();
                var remainingConnection = AddClient(service, "Remaining Client", remainingWriter);
                var updateCount = 0;
                service.RoomUpdated += _ => updateCount++;

                failingWriter.Failure = exception;
                service.Broadcast("ROOM|stale");

                Assert.AreEqual(1, service.HostClientCount);
                Assert.IsTrue(failingConnection.IsDisposed);
                Assert.IsFalse(remainingConnection.IsDisposed);
                AssertSeat(service, 2, "Player 2", false);
                AssertSeat(service, 3, "Remaining Client", true);
                Assert.AreEqual(1, updateCount);

                var latestMessage = remainingWriter.Messages.Last(message => message.StartsWith("ROOM|", StringComparison.Ordinal));
                var latestRoom = NetworkRoomService.DeserializeRoom(latestMessage.Substring("ROOM|".Length));
                var releasedSeat = latestRoom.Seats.Single(seat => seat.PlayerId == 2);
                var remainingSeat = latestRoom.Seats.Single(seat => seat.PlayerId == 3);
                Assert.IsFalse(releasedSeat.IsReady);
                Assert.AreEqual("Player 2", releasedSeat.PlayerName);
                Assert.IsTrue(remainingSeat.IsReady);
            }
        }

        private static NetworkRoomService CreateHost(int playerCount)
        {
            var service = new NetworkRoomService(false);
            service.CreateRoom("Host", playerCount);
            service.SetTransportReadiness(1, true, true, 0);
            return service;
        }

        private static NetworkRoomService.ClientConnection AddClient(
            NetworkRoomService service,
            string playerName,
            TextWriter writer = null)
        {
            var connection = new NetworkRoomService.ClientConnection(
                new StringReader(string.Empty),
                writer ?? new RecordingWriter());
            var playerId = service.AssignSeat(playerName, connection);
            Assert.Greater(playerId, 0);
            service.SetTransportReadiness(playerId, true, true, (ulong)playerId);
            return connection;
        }

        private static void AssertSeat(NetworkRoomService service, int playerId, string playerName, bool isReady)
        {
            var seat = service.GetCurrentRoom().Seats.Single(candidate => candidate.PlayerId == playerId);
            Assert.AreEqual(playerName, seat.PlayerName);
            Assert.AreEqual(isReady, seat.IsReady);
        }

        private sealed class RecordingWriter : TextWriter
        {
            private readonly List<string> messages = new List<string>();
            public override Encoding Encoding => Encoding.UTF8;
            public IReadOnlyList<string> Messages
            {
                get
                {
                    lock (messages) return messages.ToArray();
                }
            }

            public override void WriteLine(string value)
            {
                lock (messages) messages.Add(value);
            }
        }

        private sealed class SwitchableThrowWriter : TextWriter
        {
            public Exception Failure { get; set; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void WriteLine(string value)
            {
                if (Failure != null) throw Failure;
            }
        }

        private sealed class BlockingThrowWriter : TextWriter
        {
            private readonly Exception exception;
            public readonly ManualResetEventSlim WriteEntered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim AllowFailure = new ManualResetEventSlim(false);
            public bool BlockWrites { get; set; }

            public BlockingThrowWriter(Exception exception) => this.exception = exception;
            public override Encoding Encoding => Encoding.UTF8;

            public override void WriteLine(string value)
            {
                if (!BlockWrites) return;
                WriteEntered.Set();
                if (!AllowFailure.Wait(2000)) throw new TimeoutException("测试未释放模拟写入。");
                throw exception;
            }
        }

        private sealed class ControlledDisconnectReader : TextReader
        {
            private readonly string joinMessage;
            private int readCount;
            public readonly ManualResetEventSlim WaitingForDisconnect = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim AllowDisconnect = new ManualResetEventSlim(false);

            public ControlledDisconnectReader(string joinMessage) => this.joinMessage = joinMessage;

            public override string ReadLine()
            {
                if (Interlocked.Increment(ref readCount) == 1) return joinMessage;
                WaitingForDisconnect.Set();
                if (!AllowDisconnect.Wait(3000)) throw new TimeoutException("测试未释放模拟断线。");
                return null;
            }

            protected override void Dispose(bool disposing)
            {
                AllowDisconnect.Set();
                base.Dispose(disposing);
            }
        }
    }
}
