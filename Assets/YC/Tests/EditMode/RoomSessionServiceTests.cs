using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class RoomSessionServiceTests
    {
        private sealed class Content : IRoomContentCapabilityProvider
        {
            public string GetContentPackHash(GameRuleConfiguration c) => "room-session-test-content:" + c.PlayerCount + ":" + c.MapId + ":" + string.Join(",", c.EnabledExpansionIds);
            public bool TryValidate(GameRuleConfiguration c, out string reason) { reason = string.Empty; return true; }
        }
        private static T Request<T>(RoomState room) where T : RoomConfigurationRequest, new() => new T
        {
            CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId, RequestingPlayerId = room.LocalPlayerId,
            ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision
        };

        [Test] public void CreateLocalRoomDoesNotConstructOrInitializeNetwork()
        {
            var constructed = 0;
            using (var service = new RoomSessionService(new Content(), () => { constructed++; throw new Exception("不应创建网络"); }))
            {
                service.Initialize();
                var room = service.CreateRoomAsync("本机玩家", 4).GetAwaiter().GetResult();
                Assert.That(constructed, Is.Zero);
                Assert.That(service.IsOnline, Is.False);
                Assert.That(service.SupportsFriendInvites, Is.False);
                Assert.That(room.PlayerCount, Is.EqualTo(1));
                Assert.That(room.Configuration.MapId, Is.EqualTo("map-four-players"));
                Assert.That(room.Seats[0].SteamId, Is.Zero);
                Assert.That(RoomStateSerializer.TryValidate(room, out _), Is.True);
                room.Configuration.MapId = "map-three-players";
                Assert.That(service.GetCurrentRoom().Configuration.MapId, Is.EqualTo("map-four-players"));
            }
        }

        [Test] public void CloseDisconnectsGuestsAndPreservesHostSeatConfigurationAndClosedSlots()
        {
            var content = new Content(); var networks = new List<TestNetwork>();
            using (var service = new RoomSessionService(content, () => { var n = new TestNetwork(content); networks.Add(n); return n; }))
            {
                service.CreateRoomAsync("房主", 4).GetAwaiter().GetResult();
                var configuration = Request<ChangeRoomConfigurationRequest>(service.GetCurrentRoom());
                configuration.MapId = "map-three-players"; configuration.RuleModeId = "standard";
                configuration.EnabledExpansionIds = new List<string> { "core", "enterprise" };
                Assert.That((service.ChangeConfigurationAsync(configuration).GetAwaiter().GetResult()).Success, Is.True);
                var swap = Request<RequestRoomSeatSwapRequest>(service.GetCurrentRoom()); swap.TargetSeatIndex = 2;
                Assert.That((service.RequestSeatSwapAsync(swap).GetAwaiter().GetResult()).Success, Is.True);
                var close = Request<SetRoomSeatStatusRequest>(service.GetCurrentRoom()); close.SeatIndex = 3; close.Status = "closed";
                Assert.That((service.SetSeatStatusAsync(close).GetAwaiter().GetResult()).Success, Is.True);
                var before = service.GetCurrentRoom();
                var online = service.SetOnlineAsync(true).GetAwaiter().GetResult();
                Assert.That(service.IsOnline, Is.True);
                Assert.That(online.SeatSlots[2].Occupant.PlayerId, Is.EqualTo(1));
                Assert.That(online.Configuration.MapId, Is.EqualTo(before.Configuration.MapId));
                Assert.That(online.Configuration.EnabledExpansionIds, Is.EquivalentTo(before.Configuration.EnabledExpansionIds));
                Assert.That(online.SeatSlots[3].Status, Is.EqualTo("closed"));
                networks[0].AddGuest();
                var disbanded = false; service.RoomDisbanded += () => disbanded = true;
                var offline = service.SetOnlineAsync(false).GetAwaiter().GetResult();
                Assert.That(networks[0].Disposed, Is.True, "关闭必须停止实际网络服务");
                Assert.That(disbanded, Is.False, "关闭远程房间不能把保留的本地房间一起关闭");
                Assert.That(service.IsOnline, Is.False);
                Assert.That(offline.PlayerCount, Is.EqualTo(1));
                Assert.That(offline.SeatSlots[2].Occupant.PlayerId, Is.EqualTo(1));
                Assert.That(offline.SeatSlots[2].Occupant.SteamId, Is.Zero, "本地房间不沿用已失效的网络身份");
                Assert.That(offline.SeatSlots[3].Status, Is.EqualTo("closed"));
                Assert.That(offline.Configuration.MapId, Is.EqualTo(before.Configuration.MapId));
                Assert.That(offline.Configuration.EnabledExpansionIds, Is.EquivalentTo(before.Configuration.EnabledExpansionIds));
                Assert.That(offline.PendingSeatSwaps, Is.Empty);
                Assert.That(offline.SeatSlots.TrueForAll(s => !s.IsUserReady), Is.True);
                Assert.That(RoomStateSerializer.TryValidate(offline, out _), Is.True);
                var reopened = service.SetOnlineAsync(true).GetAwaiter().GetResult();
                Assert.That(networks.Count, Is.EqualTo(2));
                Assert.That(reopened.RoomId, Is.Not.EqualTo(online.RoomId));
                Assert.That(reopened.PlayerCount, Is.EqualTo(1));
            }
        }

        [UnityTest] public IEnumerator DelayedOrFailedOpeningKeepsLocalRoomAndRejectsConcurrentChanges()
        {
            var network = new TestNetwork(new Content()) { DelayCreate = true };
            using (var service = new RoomSessionService(new Content(), () => network))
            {
                var before = service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                var opening = service.SetOnlineAsync(true);
                Assert.That(service.IsOnline, Is.False);
                Assert.That(service.IsChangingNetworking, Is.True);
                Assert.That(service.GetCurrentRoom().RoomId, Is.EqualTo(before.RoomId));
                var close = Request<SetRoomSeatStatusRequest>(before); close.SeatIndex = 3; close.Status = "closed";
                Assert.That((service.SetSeatStatusAsync(close).GetAwaiter().GetResult()).ReasonCode, Is.EqualTo("room_network_busy"));
                network.Completion.SetException(new InvalidOperationException("Steam不可用"));
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!opening.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                Assert.That(opening.IsCompleted, Is.True, "异步失败应返回本地房间");
                Assert.Throws<InvalidOperationException>(() => opening.GetAwaiter().GetResult());
                Assert.That(service.IsOnline, Is.False);
                Assert.That(service.IsChangingNetworking, Is.False);
                Assert.That(network.Disposed, Is.True);
                Assert.That(service.GetCurrentRoom().RoomId, Is.EqualTo(before.RoomId));
                Assert.That(service.GetCurrentRoom().Configuration.ConfigurationRevision, Is.EqualTo(before.Configuration.ConfigurationRevision));
            }
        }

        [UnityTest] public IEnumerator LeavingWhileOpeningDoesNotResurrectRoomAfterLateCompletion()
        {
            var network = new TestNetwork(new Content()) { DelayCreate = true };
            using (var service = new RoomSessionService(new Content(), () => network))
            {
                service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                var opening = service.SetOnlineAsync(true);
                var late = network.State.Clone();
                service.Shutdown();
                network.Completion.SetResult(late);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!opening.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                Assert.That(opening.IsCompleted, Is.True, "过期异步操作应及时取消");
                Assert.Throws<OperationCanceledException>(() => opening.GetAwaiter().GetResult());
                Assert.That(service.GetCurrentRoom(), Is.Null);
                Assert.That(service.IsOnline, Is.False);
                Assert.That(network.Disposed, Is.True);
            }
        }

        [Test] public void JoinedMemberCannotCloseHostNetwork()
        {
            var network = new TestNetwork(new Content());
            using (var service = new RoomSessionService(new Content(), () => network))
            {
                var room = service.JoinRoomAsync("000123", "成员").GetAwaiter().GetResult();
                Assert.That(room.RoomId, Is.EqualTo("000123"));
                Assert.That(service.IsOnline, Is.True);
                Assert.Throws<InvalidOperationException>(() => service.SetOnlineAsync(false).GetAwaiter().GetResult());
                Assert.That(network.Disposed, Is.False);
                Assert.That(service.GetCurrentRoom().PlayerCount, Is.EqualTo(2));
            }
        }

        private static RoomConfigurationResult Add(RoomSessionService service, int index)
        {
            var request = Request<AddLocalRoomPlayerRequest>(service.GetCurrentRoom());
            request.SeatIndex = index; request.PlayerName = "本地玩家 " + (index + 1);
            return service.AddLocalPlayerAsync(request).GetAwaiter().GetResult();
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void HotseatStartsWithRealLocalSeatsWithoutNetworkOrGuestReady(int count)
        {
            using (var service = new RoomSessionService(new Content(), () => throw new Exception("热座不能创建网络")))
            {
                var initial = service.CreateRoomAsync("房主", 3).Result;
                Assert.That(service.StartConfiguredGameAsync(Request<StartConfiguredGameRequest>(initial)).Result.ReasonCode, Is.EqualTo("unsupported_player_count"));
                for (var i = 1; i < count; i++) Assert.That(Add(service, i).Success, Is.True);
                var room = service.GetCurrentRoom();
                Assert.That(room.PlayerCount, Is.EqualTo(count));
                Assert.That(room.Configuration.MapId, Is.EqualTo(count == 4 ? "map-four-players" : "map-three-players"));
                foreach (var slot in room.SeatSlots)
                {
                    if (slot.Occupant == null) continue;
                    Assert.That(slot.Occupant.SteamId, Is.Zero);
                    Assert.That(slot.Occupant.TransportConnected || slot.Occupant.IdentityVerified || slot.IsUserReady, Is.False);
                    Assert.That(slot.Occupant.ContentPackHash, Is.EqualTo(room.Configuration.ContentPackHash));
                }
                Assert.That(new RoomConfigurationService(new Content()).TryValidateLaunch(room, out var onlineReason), Is.False);
                Assert.That(onlineReason, Is.EqualTo("player_not_ready"), "同一快照不能绕过联机检查");
                var starts = 0; RoomState launched = null;
                service.GameStarted += snapshot => { starts++; launched = snapshot; };
                var start = Request<StartConfiguredGameRequest>(room);
                Assert.That(service.StartConfiguredGameAsync(start).Result.Success, Is.True);
                Assert.That(service.StartConfiguredGameAsync(start).Result.Success, Is.True);
                Assert.That(starts, Is.EqualTo(1), "重复命令不能重复加载场景");
                Assert.That(launched.HasStarted, Is.True);
                Assert.That(launched.Configuration.ConfigurationRevision, Is.EqualTo(room.Configuration.ConfigurationRevision));
                launched.Configuration.MapId = "invalid";
                Assert.That(service.GetCurrentRoom().Configuration.MapId, Is.EqualTo(room.Configuration.MapId));
                Assert.That(Add(service, 1).ReasonCode, Is.EqualTo("configuration_stale"));
            }
        }

        [Test] public void HotseatCommandsEnforceSeatHostRevisionAndIdempotency()
        {
            using (var service = new RoomSessionService(new Content(), () => throw new Exception()))
            {
                var room = service.CreateRoomAsync("房主", 3).Result;
                var close = Request<SetRoomSeatStatusRequest>(room); close.SeatIndex = 1; close.Status = "closed";
                Assert.That(service.SetSeatStatusAsync(close).Result.Success, Is.True);
                Assert.That(Add(service, 1).ReasonCode, Is.EqualTo("seat_occupied"));
                Assert.That(Add(service, 0).ReasonCode, Is.EqualTo("seat_occupied"));
                Assert.That(Add(service, 4).ReasonCode, Is.EqualTo("room_protocol_mismatch"));
                var add = Request<AddLocalRoomPlayerRequest>(service.GetCurrentRoom()); add.SeatIndex = 3; add.PlayerName = "本地玩家";
                var first = service.AddLocalPlayerAsync(add).Result;
                Assert.That(first.Success, Is.True);
                Assert.That(service.AddLocalPlayerAsync(add).Result.Snapshot.Configuration.ConfigurationRevision, Is.EqualTo(first.Snapshot.Configuration.ConfigurationRevision));
                Assert.That(service.GetCurrentRoom().SeatSlots[3].Occupant.Color, Is.EqualTo(PlayerColor.Red));
                var stale = Request<AddLocalRoomPlayerRequest>(room); stale.SeatIndex = 2; stale.PlayerName = "过期玩家";
                Assert.That(service.AddLocalPlayerAsync(stale).Result.ReasonCode, Is.EqualTo("configuration_stale"));
                var nonHost = Request<RemoveLocalRoomPlayerRequest>(service.GetCurrentRoom()); nonHost.RequestingPlayerId = first.Snapshot.SeatSlots[3].Occupant.PlayerId; nonHost.SeatIndex = 3;
                Assert.That(service.RemoveLocalPlayerAsync(nonHost).Result.ReasonCode, Is.EqualTo("not_room_host"));
                var removeHost = Request<RemoveLocalRoomPlayerRequest>(service.GetCurrentRoom()); removeHost.SeatIndex = 0;
                Assert.That(service.RemoveLocalPlayerAsync(removeHost).Result.ReasonCode, Is.EqualTo("seat_occupied"));
                Assert.That(service.GetCurrentRoom().PlayerCount, Is.EqualTo(2));
            }
        }

        [Test] public void HotseatSwapAndRemovalUpdateConfigurationAndKeepHostIdentity()
        {
            using (var service = new RoomSessionService(new Content(), () => throw new Exception()))
            {
                service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult();
                for (var i = 1; i < 4; i++) Assert.That(Add(service, i).Success, Is.True);
                var before = service.GetCurrentRoom();
                var swap = Request<RequestRoomSeatSwapRequest>(before); swap.TargetSeatIndex = 2;
                var swapped = service.RequestSeatSwapAsync(swap).Result.Snapshot;
                Assert.That(swapped.PendingSeatSwaps, Is.Empty);
                Assert.That(swapped.SeatSlots[2].Occupant.PlayerId, Is.EqualTo(before.HostPlayerId));
                Assert.That(swapped.SeatSlots[2].Occupant.Color, Is.EqualTo(PlayerColor.Blue));
                var remove = Request<RemoveLocalRoomPlayerRequest>(swapped); remove.SeatIndex = 0;
                var removed = service.RemoveLocalPlayerAsync(remove).Result.Snapshot;
                Assert.That(removed.PlayerCount, Is.EqualTo(3));
                Assert.That(removed.SeatSlots[0].Status, Is.EqualTo("open"));
                Assert.That(removed.Configuration.MapId, Is.EqualTo("map-four-players"));
                Assert.That(removed.Configuration.ContentPackHash, Is.Not.EqualTo(before.Configuration.ContentPackHash));
                var change = Request<ChangeRoomConfigurationRequest>(removed);
                change.MapId = "map-three-players"; change.RuleModeId = "standard";
                change.EnabledExpansionIds = new List<string> { "core", "enterprise" };
                var configured = service.ChangeConfigurationAsync(change).Result;
                Assert.That(configured.Success, Is.True);
                Assert.That(configured.Snapshot.Seats.TrueForAll(p => p.ContentPackHash == configured.Snapshot.Configuration.ContentPackHash), Is.True);
                Assert.That(service.StartConfiguredGameAsync(Request<StartConfiguredGameRequest>(configured.Snapshot)).Result.Success, Is.True);
            }
        }

        [Test] public void NetworkingCannotSilentlyDiscardHotseatAndOnlineCannotAddLocalPlayers()
        {
            var networks = 0;
            using (var service = new RoomSessionService(new Content(), () => { networks++; return new TestNetwork(new Content()); }))
            {
                service.CreateRoomAsync("房主", 4).GetAwaiter().GetResult();
                Assert.That(Add(service, 1).Success, Is.True);
                var error = Assert.Throws<InvalidOperationException>(() => service.SetOnlineAsync(true).GetAwaiter().GetResult());
                Assert.That(error.Message, Is.EqualTo("hotseat_requires_offline"));
                Assert.That(networks, Is.Zero);
                Assert.That(service.GetCurrentRoom().PlayerCount, Is.EqualTo(2));
                var remove = Request<RemoveLocalRoomPlayerRequest>(service.GetCurrentRoom()); remove.SeatIndex = 1;
                Assert.That(service.RemoveLocalPlayerAsync(remove).Result.Success, Is.True);
                Assert.That(service.StartConfiguredGameAsync(Request<StartConfiguredGameRequest>(service.GetCurrentRoom())).Result.ReasonCode, Is.EqualTo("unsupported_player_count"));
                service.SetOnlineAsync(true).GetAwaiter().GetResult();
                Assert.That(service.SupportsLocalPlayers, Is.False);
                Assert.That(Add(service, 1).ReasonCode, Is.EqualTo("hotseat_requires_offline"));
                service.SetOnlineAsync(false).GetAwaiter().GetResult();
                Assert.That(service.SupportsLocalPlayers, Is.True);
                Assert.That(Add(service, 1).Success, Is.True);
            }
        }

        [Test] public void LocalLaunchStillRejectsInvalidContentAndConfiguration()
        {
            using (var service = new RoomSessionService(new Content(), () => throw new Exception()))
            {
                service.CreateRoomAsync("房主", 3).GetAwaiter().GetResult(); Add(service, 1);
                var room = service.GetCurrentRoom();
                Assert.That(new RoomConfigurationService(localPlayers: true).TryValidateLaunch(room, out var missing), Is.False);
                Assert.That(missing, Is.EqualTo("content_not_ready"));
                var authority = new RoomConfigurationService(new Content(), localPlayers: true);
                room.Configuration.ContentPackHash = "stale";
                Assert.That(authority.TryValidateLaunch(room, out var stale), Is.False);
                Assert.That(stale, Is.EqualTo("content_version_mismatch"));
                room = service.GetCurrentRoom(); room.Seats[1].ContentPackHash = "stale";
                Assert.That(authority.TryValidateLaunch(room, out var playerStale), Is.False);
                Assert.That(playerStale, Is.EqualTo("content_version_mismatch"));
                room = service.GetCurrentRoom(); room.Configuration.RuleModeId = "unsupported";
                Assert.That(authority.TryValidateLaunch(room, out _), Is.False);
            }
        }

        private sealed class TestNetwork : IOnlineRoomService, IPreconfiguredRoomHost
        {
            private readonly IRoomContentCapabilityProvider content;
            private readonly RoomConfigurationService authority;
            public RoomState State;
            public bool Disposed, DelayCreate;
            public readonly TaskCompletionSource<RoomState> Completion = new TaskCompletionSource<RoomState>();
            public TestNetwork(IRoomContentCapabilityProvider content) { this.content = content; authority = new RoomConfigurationService(content); }
            public bool SupportsFriendInvites => true;
            public bool HasPendingLobbyJoinRequest => false;
            public event Action<RoomState> RoomUpdated;
            public event Action RoomDisbanded;
            public event Action<RoomState> GameStarted { add { } remove { } }
            public event Action<string> ErrorOccurred { add { } remove { } }
            public event Action<string> LobbyJoinRequested { add { } remove { } }
            public void Initialize() { }
            public Task<RoomState> CreateRoomFromSnapshotAsync(RoomState source)
            {
                State = RoomHostSnapshot.Create(source, Guid.NewGuid().ToString("N"), new PlayerSeat
                { PlayerId = 1, PlayerName = "网络房主", LobbyMemberPresent = true, SteamId = LocalMirrorIdentity.ForPlayer(1) }, content);
                return DelayCreate ? Completion.Task : Task.FromResult(State.Clone());
            }
            public Task<RoomState> CreateRoomAsync(string name, int count) => throw new NotSupportedException();
            public Task<RoomState> JoinRoomAsync(string id, string name)
            {
                State = new RoomState { RoomId = id, HostPlayerId = 1, LocalPlayerId = 2 };
                authority.TryOccupySeat(State, new PlayerSeat { PlayerId = 1, PlayerName = "房主", LobbyMemberPresent = true }, out _);
                AddGuest(); return Task.FromResult(State.Clone());
            }
            public void AddGuest()
            { authority.TryOccupySeat(State, new PlayerSeat { PlayerId = 2, PlayerName = "成员", LobbyMemberPresent = true }, out _); RoomUpdated?.Invoke(State.Clone()); }
            public RoomState GetCurrentRoom() => State?.Clone();
            public void Shutdown() { State = null; RoomDisbanded?.Invoke(); }
            public void Dispose() { Disposed = true; Shutdown(); }
            public void InviteFriends() { }
            public bool TryConsumePendingLobbyJoinRequest(out string id) { id = null; return false; }
            public Task StartGameAsync() => throw new NotSupportedException();
            public Task<RoomConfigurationResult> ChangeConfigurationAsync(ChangeRoomConfigurationRequest r) => Task.FromResult(authority.ChangeConfiguration(State, r));
            public Task<RoomConfigurationResult> SetReadyAsync(SetRoomReadyRequest r) => Task.FromResult(authority.SetReady(State, r));
            public Task<RoomConfigurationResult> SetSeatStatusAsync(SetRoomSeatStatusRequest r) => Task.FromResult(authority.SetSeatStatus(State, r));
            public Task<RoomConfigurationResult> RequestSeatSwapAsync(RequestRoomSeatSwapRequest r) => Task.FromResult(authority.RequestSeatSwap(State, r));
            public Task<RoomConfigurationResult> ConfirmSeatSwapAsync(ConfirmRoomSeatSwapRequest r) => Task.FromResult(authority.ConfirmSeatSwap(State, r));
            public Task<RoomConfigurationResult> StartConfiguredGameAsync(StartConfiguredGameRequest r) => throw new NotSupportedException();
        }
    }
}
