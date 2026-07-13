using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class WaitingRoomReadinessTests
    {
        [Test]
        public void FullLobbyWithoutRemoteMirrorConnections_RejectsStart()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Connect(authority, 0, 101UL, 1);

            Assert.IsFalse(authority.TryValidateStart(room, _ => true, out var reason));
            StringAssert.Contains("Mirror", reason);
            Assert.IsFalse(room.HasStarted);
        }

        [Test]
        public void PartiallyConnectedLobby_StillRejectsStart()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Connect(authority, 0, 101UL, 1);
            Connect(authority, 7, 102UL, 2);

            Assert.IsFalse(authority.TryValidateStart(room, _ => true, out _));
            Assert.IsFalse(room.Seats.Single(seat => seat.PlayerId == 3).IsReady);
        }

        [Test]
        public void AllUniqueVerifiedConnections_AllowStart()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            var active = new HashSet<int> { 0, 7, 8 };
            Connect(authority, 0, 101UL, 1);
            Connect(authority, 7, 102UL, 2);
            Connect(authority, 8, 103UL, 3);

            Assert.IsTrue(authority.TryValidateStart(room, active.Contains, out var reason), reason);
            Assert.IsTrue(room.Seats.All(seat => seat.TransportConnected && seat.IdentityVerified && seat.IsReady));
        }

        [Test]
        public void NonLobbySteamIdentity_DoesNotReadyAnySeat()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Assert.IsTrue(authority.ObserveTransportConnection(9));

            Assert.IsFalse(authority.TryVerifyIdentity(9, 999UL, -1, out _));
            authority.ApplyTo(room);

            Assert.IsFalse(room.Seats.Any(seat => seat.TransportConnected || seat.IdentityVerified || seat.IsReady));
        }

        [Test]
        public void SecondConnectionForSameSteamIdentity_IsRejected()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Connect(authority, 7, 102UL, 2);
            Assert.IsTrue(authority.ObserveTransportConnection(8));

            Assert.IsFalse(authority.TryVerifyIdentity(8, 102UL, -1, out _));
            authority.ApplyTo(room);

            var seat = room.Seats.Single(candidate => candidate.PlayerId == 2);
            Assert.AreEqual(7UL, seat.NetworkClientId);
            Assert.IsTrue(seat.IsReady);
        }

        [Test]
        public void WaitingRoomDisconnect_ClearsReadinessAndRejectsStartAgain()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Connect(authority, 0, 101UL, 1);
            Connect(authority, 7, 102UL, 2);
            Connect(authority, 8, 103UL, 3);
            Assert.IsTrue(authority.TryValidateStart(room, _ => true, out _));

            Assert.IsTrue(authority.Disconnect(7, out var playerId));
            Assert.AreEqual(2, playerId);
            Assert.IsFalse(authority.TryValidateStart(room, _ => true, out _));

            var seat = room.Seats.Single(candidate => candidate.PlayerId == 2);
            Assert.IsFalse(seat.TransportConnected);
            Assert.IsFalse(seat.IdentityVerified);
            Assert.IsFalse(seat.IsReady);
        }

        [Test]
        public void DisconnectedPlayer_ReconnectsToOriginalStableSeat()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Connect(authority, 7, 102UL, 2);
            Assert.IsTrue(authority.Disconnect(7, out _));

            Connect(authority, 19, 102UL, 2);
            authority.ApplyTo(room);

            var seat = room.Seats.Single(candidate => candidate.PlayerId == 2);
            Assert.AreEqual(19UL, seat.NetworkClientId);
            Assert.IsTrue(seat.IsReady);
        }

        [Test]
        public void HostLocalConnection_IsConnectionZeroAndNotAnExtraRemoteSeat()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Connect(authority, 0, 101UL, 1);
            Connect(authority, 4, 102UL, 2);
            Connect(authority, 5, 103UL, 3);
            authority.ApplyTo(room);

            Assert.AreEqual(3, room.Seats.Count(seat => seat.IsReady));
            Assert.AreEqual(0UL, room.Seats.Single(seat => seat.PlayerId == 1).NetworkClientId);
            CollectionAssert.AreEquivalent(new ulong[] { 0, 4, 5 }, room.Seats.Select(seat => seat.NetworkClientId));
        }

        [Test]
        public void LocalMirrorMode_UsesTheSameAllConnectionsReadyGate()
        {
            var room = CreateRoom(3, LocalMirrorIdentity.ForPlayer);
            var authority = Begin(room);
            Connect(authority, 0, LocalMirrorIdentity.ForPlayer(1), 1);
            Connect(authority, 3, LocalMirrorIdentity.ForPlayer(2), 2);
            Assert.IsFalse(authority.TryValidateStart(room, _ => true, out _));

            Connect(authority, 4, LocalMirrorIdentity.ForPlayer(3), 3);
            Assert.IsTrue(authority.TryValidateStart(room, _ => true, out var reason), reason);
        }

        [Test]
        public void Shutdown_IgnoresLateConnectionEvents()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            authority.Shutdown();

            Assert.IsFalse(authority.ObserveTransportConnection(7));
            Assert.IsFalse(authority.TryVerifyIdentity(7, 102UL, 2, out _));
            authority.ApplyTo(room);

            Assert.IsFalse(room.Seats.Any(seat => seat.TransportConnected || seat.IdentityVerified || seat.IsReady));
        }

        [Test]
        public void GameSceneIdentityBinding_MustExactlyMatchWaitingRoomMapping()
        {
            var room = CreateRoom(3);
            var authority = Begin(room);
            Connect(authority, 0, 101UL, 1);
            Connect(authority, 7, 102UL, 2);
            Connect(authority, 8, 103UL, 3);
            authority.ApplyTo(room);

            Assert.IsTrue(RoomReadinessPolicy.TryResolveExpectedBinding(room.Seats, 7, 102UL, out var playerId));
            Assert.AreEqual(2, playerId);
            Assert.IsFalse(RoomReadinessPolicy.TryResolveExpectedBinding(room.Seats, 8, 102UL, out _));
            Assert.IsFalse(RoomReadinessPolicy.TryResolveExpectedBinding(room.Seats, 7, 103UL, out _));
        }

        [Test]
        public void FailedStartCommit_LeavesWaitingStatusJoinableAndNotStarted()
        {
            var room = CreateRoom(3);
            var joinable = true;
            var roomStatus = SteamLobbyPolicy.WaitingStatus;
            var joinableWrites = new List<bool>();

            var committed = LobbyStartCommitPolicy.TryCommit(
                value =>
                {
                    joinableWrites.Add(value);
                    joinable = value;
                    return true;
                },
                _ => false,
                out var reason);

            Assert.IsFalse(committed);
            Assert.IsNotEmpty(reason);
            Assert.IsTrue(joinable);
            Assert.AreEqual(SteamLobbyPolicy.WaitingStatus, roomStatus);
            Assert.IsFalse(room.HasStarted);
            CollectionAssert.AreEqual(new[] { false, true }, joinableWrites);
        }

        private static WaitingRoomConnectionAuthority Begin(RoomState room)
        {
            var authority = new WaitingRoomConnectionAuthority();
            authority.Begin(room.Seats);
            return authority;
        }

        private static void Connect(
            WaitingRoomConnectionAuthority authority,
            int connectionId,
            ulong identity,
            int playerId)
        {
            Assert.IsTrue(authority.ObserveTransportConnection(connectionId));
            Assert.IsTrue(authority.TryVerifyIdentity(connectionId, identity, playerId, out var actualPlayerId));
            Assert.AreEqual(playerId, actualPlayerId);
        }

        private static RoomState CreateRoom(int playerCount, System.Func<int, ulong> identityFactory = null)
        {
            identityFactory ??= playerId => (ulong)(100 + playerId);
            var room = new RoomState
            {
                RoomId = "test-room",
                HostPlayerId = 1,
                LocalPlayerId = 1,
                PlayerCount = playerCount,
                HasStarted = false
            };
            for (var playerId = 1; playerId <= playerCount; playerId++)
            {
                room.Seats.Add(new PlayerSeat
                {
                    PlayerId = playerId,
                    SteamId = identityFactory(playerId),
                    PlayerName = "Player " + playerId,
                    LobbyMemberPresent = true
                });
            }
            return room;
        }
    }
}
