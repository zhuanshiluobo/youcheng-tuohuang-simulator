using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class RoomConfigurationTests
    {
        private readonly BasicCapabilities content = new BasicCapabilities();
        private RoomConfigurationService service;
        private Action restoreRuntimeCatalogs;
        [SetUp] public void SetUp() { service = new RoomConfigurationService(content); }

        private sealed class BasicCapabilities : IRoomContentCapabilityProvider
        {
            public string GetContentPackHash(GameRuleConfiguration configuration) =>
                configuration.EnabledExpansionIds.Count == 1 && configuration.EnabledExpansionIds[0] == "core" ? "tested-basic-content" : string.Empty;
            public bool TryValidate(GameRuleConfiguration configuration, out string reasonCode)
            { reasonCode = GetContentPackHash(configuration).Length == 0 ? "content_not_ready" : string.Empty; return reasonCode.Length == 0; }
        }
        private RoomState Room(int count = 3)
        {
            var room = new RoomState { RoomId = "001208", HostPlayerId = 1, LocalPlayerId = 1 };
            room.Configuration.ContentPackHash = content.GetContentPackHash(room.Configuration);
            for (var id = 1; id <= count; id++)
                Assert.That(service.TryOccupySeat(room, Player(id), out _), Is.True);
            return room;
        }
        private PlayerSeat Player(int id) => new PlayerSeat
        {
            PlayerId = id, SteamId = (ulong)(100 + id), NetworkClientId = (ulong)(id - 1),
            PlayerName = "玩家" + id, LobbyMemberPresent = true, TransportConnected = true,
            IdentityVerified = true, IsReady = true, ContentPackHash = "tested-basic-content",
            RuleContractVersion = GameRuleConfiguration.CurrentRuleContractVersion
        };
        private T Request<T>(RoomState room, int playerId = 1) where T : RoomConfigurationRequest, new() => new T
        { RoomId = room.RoomId, CommandId = Guid.NewGuid().ToString("N"), RequestingPlayerId = playerId,
            ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision };
        private ChangeRoomConfigurationRequest Change(RoomState room, string map = "map-four-players", params string[] expansions)
        {
            var request = Request<ChangeRoomConfigurationRequest>(room);
            request.MapId = map; request.RuleModeId = "standard";
            request.EnabledExpansionIds = new List<string>(expansions.Length == 0 ? new[] { "core" } : expansions);
            return request;
        }
        private void ReadyGuests(RoomState room)
        {
            foreach (var player in room.Seats)
                if (player.PlayerId != room.HostPlayerId)
                    Assert.That(service.SetReady(room, new SetRoomReadyRequest
                    { RoomId = room.RoomId, CommandId = Guid.NewGuid().ToString("N"), RequestingPlayerId = player.PlayerId,
                        ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision, IsUserReady = true }).Success, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void JoiningFourthThenLeavingKeepsFourPlayerMap(bool chooseFourBeforeJoin)
        {
            var room = Room();
            if (chooseFourBeforeJoin) Assert.That(service.ChangeConfiguration(room, Change(room)).Success, Is.True);
            ReadyGuests(room);
            Assert.That(service.TryOccupySeat(room, Player(4), out _), Is.True);
            Assert.That(room.Configuration.MapId, Is.EqualTo("map-four-players"));
            Assert.That(room.PlayerCount, Is.EqualTo(4));
            Assert.That(room.SeatSlots.TrueForAll(s => !s.IsUserReady && s.ReadyConfigurationRevision == -1), Is.True);
            Assert.That(service.VacateSeat(room, 4), Is.True);
            Assert.That(room.PlayerCount, Is.EqualTo(3));
            Assert.That(room.Configuration.PlayerCount, Is.EqualTo(3));
            Assert.That(room.Configuration.MapId, Is.EqualTo("map-four-players"));
        }

        [Test]
        public void EmptyClosedAndInvitedSlotsAreExcludedFromActualCount()
        {
            var room = Room(2);
            var request = Request<SetRoomSeatStatusRequest>(room); request.SeatIndex = 3; request.Status = "closed";
            Assert.That(service.SetSeatStatus(room, request).Success, Is.True);
            Assert.That(room.SeatSlots.Count, Is.EqualTo(4));
            Assert.That(room.PlayerCount, Is.EqualTo(2));
            Assert.That(room.Configuration.PlayerCount, Is.EqualTo(2));
            var invited = Player(9); invited.LobbyMemberPresent = false;
            Assert.That(service.TryOccupySeat(room, invited, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("service_unavailable"));
            Assert.That(room.PlayerCount, Is.EqualTo(2));
            var projection = room.Seats; projection.Add(Player(9)); projection.Clear();
            Assert.That(room.PlayerCount, Is.EqualTo(2), "旧列表增删不能写入权威座位");
        }

        [Test]
        public void FourOccupantsCannotSelectThreePlayerMap()
        {
            var room = Room(4); var revision = room.Configuration.ConfigurationRevision;
            Assert.That(service.ChangeConfiguration(room, Change(room, "map-three-players")).ReasonCode, Is.EqualTo("map_requires_four_player"));
            Assert.That(room.Configuration.ConfigurationRevision, Is.EqualTo(revision));
        }

        [Test]
        public void ConfigurationChangeInvalidatesUserPreparationWithoutDisconnectingPlayers()
        {
            var room = Room(); ReadyGuests(room);
            var staleStart = Request<StartConfiguredGameRequest>(room);
            var staleReady = Request<SetRoomReadyRequest>(room, 2); staleReady.IsUserReady = true;
            var previous = room.Seats[1].Clone();
            Assert.That(service.ChangeConfiguration(room, Change(room)).Success, Is.True);
            var slot = RoomConfigurationService.FindSlot(room, 2);
            Assert.That(slot.IsUserReady, Is.False); Assert.That(slot.ReadyConfigurationRevision, Is.EqualTo(-1));
            Assert.That(slot.Occupant.IsReady, Is.EqualTo(previous.IsReady));
            Assert.That(slot.Occupant.TransportConnected && slot.Occupant.IdentityVerified, Is.True);
            Assert.That(slot.Occupant.NetworkClientId, Is.EqualTo(previous.NetworkClientId));
            Assert.That(service.SetReady(room, staleReady).ReasonCode, Is.EqualTo("configuration_stale"));
            Assert.That(service.TryFreezeForLaunch(room, staleStart, out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("configuration_stale"));
        }

        [Test]
        public void DuplicateCommandAndSameValueDoNotClearPreparationOrIncrementRevision()
        {
            var room = Room(); var request = Change(room);
            Assert.That(service.ChangeConfiguration(room, request).Success, Is.True);
            ReadyGuests(room); var revision = room.Configuration.ConfigurationRevision;
            Assert.That(service.ChangeConfiguration(room, request).Success, Is.True);
            Assert.That(service.ChangeConfiguration(room, Change(room)).Success, Is.True);
            Assert.That(room.Configuration.ConfigurationRevision, Is.EqualTo(revision));
            Assert.That(RoomConfigurationService.FindSlot(room, 2).IsUserReady, Is.True);
        }

        [Test]
        public void NonHostCannotChangeConfigurationOrStart()
        {
            var room = Room(); var request = Change(room); request.RequestingPlayerId = 2;
            Assert.That(service.ChangeConfiguration(room, request).ReasonCode, Is.EqualTo("not_room_host"));
            Assert.That(service.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room, 2), out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("not_room_host"));
        }

        [Test]
        public void DependencyAndEntrepreneurModeAreRejectedAtomically()
        {
            var room = Room(); ReadyGuests(room); var before = RoomStateSerializer.Serialize(room);
            Assert.That(service.ChangeConfiguration(room, Change(room, "map-four-players", "core", "infected")).ReasonCode,
                Is.EqualTo("missing_enterprise_dependency"));
            var entrepreneur = Change(room); entrepreneur.RuleModeId = "entrepreneur";
            Assert.That(service.ChangeConfiguration(room, entrepreneur).ReasonCode, Is.EqualTo("rule_mode_not_implemented"));
            Assert.That(RoomStateSerializer.Serialize(room), Is.EqualTo(before));
        }

        [Test]
        public void UnimplementedExpansionCannotFreezeOrCreateFakeInitialState()
        {
            var room = Room(); Assert.That(service.ChangeConfiguration(room, Change(room, "map-four-players", "core", "enterprise")).Success, Is.True);
            Assert.That(service.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("content_not_ready")); Assert.That(room.HasStarted, Is.False);
            Assert.That(() => GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, room.Seats, room.Configuration, 42),
                Throws.InvalidOperationException.With.Message.EqualTo("content_not_ready"));
        }

        [Test]
        public void HostSwapRequiresTargetConsentAndPreservesIdentityWhileChangingColors()
        {
            var room = Room(); ReadyGuests(room);
            var swap = Request<RequestRoomSeatSwapRequest>(room); swap.TargetSeatIndex = 2;
            Assert.That(service.RequestSeatSwap(room, swap).Success, Is.True);
            Assert.That(room.SeatSlots[0].Occupant.PlayerId, Is.EqualTo(1));
            var wrong = Request<ConfirmRoomSeatSwapRequest>(room, 2); wrong.SwapRequestId = swap.CommandId; wrong.Accepted = true;
            Assert.That(service.ConfirmSeatSwap(room, wrong).ReasonCode, Is.EqualTo("seat_swap_stale"));
            var accepted = Request<ConfirmRoomSeatSwapRequest>(room, 3); accepted.SwapRequestId = swap.CommandId; accepted.Accepted = true;
            Assert.That(service.ConfirmSeatSwap(room, accepted).Success, Is.True);
            Assert.That(room.HostPlayerId, Is.EqualTo(1));
            Assert.That(room.SeatSlots[2].Occupant.PlayerId, Is.EqualTo(1));
            Assert.That(room.SeatSlots[2].Occupant.Color, Is.EqualTo(PlayerColor.Blue));
            Assert.That(room.SeatSlots[0].Occupant.PlayerId, Is.EqualTo(3));
            Assert.That(room.SeatSlots[0].Occupant.Color, Is.EqualTo(PlayerColor.Green));
            Assert.That(room.Seats.ConvertAll(p => p.PlayerId), Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(room.SeatSlots.TrueForAll(s => !s.IsUserReady), Is.True);
        }

        [Test]
        public void SwapToOpenSeatIsAtomicAndOccupiedSeatCannotBeClosed()
        {
            var room = Room(); var move = Request<RequestRoomSeatSwapRequest>(room); move.TargetSeatIndex = 3;
            Assert.That(service.RequestSeatSwap(room, move).Success, Is.True);
            Assert.That(room.SeatSlots[0].Occupant, Is.Null);
            Assert.That(room.SeatSlots[3].Occupant.PlayerId, Is.EqualTo(1));
            var close = Request<SetRoomSeatStatusRequest>(room); close.SeatIndex = 3; close.Status = "closed";
            Assert.That(service.SetSeatStatus(room, close).ReasonCode, Is.EqualTo("seat_occupied"));
            Assert.That(room.PlayerCount, Is.EqualTo(3));
        }

        [Test]
        public void PendingSwapIsInvalidatedByMembershipChange()
        {
            var room = Room(); var swap = Request<RequestRoomSeatSwapRequest>(room); swap.TargetSeatIndex = 2;
            service.RequestSeatSwap(room, swap); service.VacateSeat(room, 2);
            var answer = Request<ConfirmRoomSeatSwapRequest>(room, 3); answer.SwapRequestId = swap.CommandId; answer.Accepted = true;
            Assert.That(service.ConfirmSeatSwap(room, answer).ReasonCode, Is.EqualTo("seat_swap_stale"));
            Assert.That(room.HostPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void CancellationPreservesTransportReadinessAndVersionMismatchBlocksLaunch()
        {
            var room = Room(); ReadyGuests(room);
            Assert.That(service.SetReady(room, Request<SetRoomReadyRequest>(room, 2)).Success, Is.True);
            Assert.That(RoomConfigurationService.FindSlot(room, 2).Occupant.IsReady, Is.True);
            Assert.That(service.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("player_not_ready"));
            ReadyGuests(room); room.Seats[1].ContentPackHash = "other-build";
            Assert.That(service.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("content_version_mismatch"));
        }

        [Test]
        public void RoomJsonRoundTripRetainsEmptySlotsPreparationAndLeadingZeros()
        {
            var room = Room(); ReadyGuests(room);
            Assert.That(RoomStateSerializer.TryDeserialize(RoomStateSerializer.Serialize(room), out var restored, out var reason), Is.True, reason);
            Assert.That(restored.RoomId, Is.EqualTo("001208"));
            Assert.That(restored.SeatSlots.Count, Is.EqualTo(4)); Assert.That(restored.SeatSlots[3].Occupant, Is.Null);
            Assert.That(RoomState.TryLocalizeAuthoritativeSnapshot(restored, room.RoomId, 102, out var localized), Is.True);
            Assert.That(localized.LocalPlayerId, Is.EqualTo(2)); Assert.That(localized.SeatSlots[1].IsUserReady, Is.True);
            localized.Configuration.EnabledExpansionIds.Add("enterprise"); localized.Seats[0].PlayerName = "已改变";
            Assert.That(room.Configuration.EnabledExpansionIds, Is.EqualTo(new[] { "core" }));
            Assert.That(room.Seats[0].PlayerName, Is.EqualTo("玩家1"));
        }

        [TestCase("ProtocolVersion")] [TestCase("Configuration")] [TestCase("SeatSlots")]
        public void MissingRoomFieldsAreNotReplacedByInitializers(string field)
        {
            var json = JObject.Parse(RoomStateSerializer.Serialize(Room())); json.Remove(field);
            Assert.That(RoomStateSerializer.TryDeserialize(json.ToString(), out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("room_protocol_mismatch"));
        }

        [Test]
        public void LegacyPipeAndOldProtocolAreRejected()
        {
            Assert.That(RoomStateSerializer.TryDeserialize("001208|1|3|0|1,host,0,1", out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("room_protocol_mismatch"));
            var json = JObject.Parse(RoomStateSerializer.Serialize(Room())); json["ProtocolVersion"] = 1;
            Assert.That(RoomStateSerializer.TryDeserialize(json.ToString(), out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("room_protocol_mismatch"));
        }

        [Test]
        public void ForgedConnectionIdentityAndStaleReadyAreRejectedWithoutMutation()
        {
            var room = Room(); var ready = Request<SetRoomReadyRequest>(room, 2); ready.IsUserReady = true;
            var message = RoomConfigurationMessage.Create(ready, content, room.Configuration);
            var before = RoomStateSerializer.Serialize(room);
            Assert.That(message.Execute(room, 3, service).ReasonCode, Is.EqualTo("room_protocol_mismatch"));
            Assert.That(RoomStateSerializer.Serialize(room), Is.EqualTo(before));
            service.ChangeConfiguration(room, Change(room));
            Assert.That(message.Execute(room, 2, service).ReasonCode, Is.EqualTo("configuration_stale"));
        }

        [Test]
        public void ConcurrentConfigurationRequestsAcceptOnlyOneExpectedRevision()
        {
            var room = Room();
            var first = Change(room);
            var second = Change(room, "map-four-players", "core", "enterprise");
            var results = new RoomConfigurationResult[2];
            Parallel.Invoke(() => results[0] = service.ChangeConfiguration(room, first),
                () => results[1] = service.ChangeConfiguration(room, second));
            Assert.That(Array.FindAll(results, result => result.Success).Length, Is.EqualTo(1));
            Assert.That(Array.Find(results, result => !result.Success).ReasonCode, Is.EqualTo("configuration_stale"));
            Assert.That(room.Configuration.ConfigurationRevision, Is.EqualTo(first.ExpectedConfigurationRevision + 1));
        }

        [Test]
        public void ReadyCommandCannotBeReusedAsStartCommand()
        {
            var room = Room(); var ready = Request<SetRoomReadyRequest>(room); ready.IsUserReady = true;
            Assert.That(service.SetReady(room, ready).Success, Is.True);
            var start = Request<StartConfiguredGameRequest>(room); start.CommandId = ready.CommandId;
            Assert.That(service.TryFreezeForLaunch(room, start, out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("room_protocol_mismatch"));
            Assert.That(room.HasStarted, Is.False);
        }

        [TestCase("MapId")] [TestCase("RuleContractVersion")] [TestCase("EnabledExpansionIds")]
        public void MissingConfigurationFieldsAreRejectedInsteadOfDefaulting(string field)
        {
            var json = JObject.Parse(RoomStateSerializer.Serialize(Room())); ((JObject)json["Configuration"]).Remove(field);
            Assert.That(RoomStateSerializer.TryDeserialize(json.ToString(), out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("room_protocol_mismatch"));
        }

        [Test]
        public void CommandEnvelopeRejectsMissingVersionsAndDefaultedOperationFields()
        {
            var room = Room(); var ready = Request<SetRoomReadyRequest>(room, 2); ready.IsUserReady = true;
            var envelope = JObject.FromObject(RoomConfigurationMessage.Create(ready, content, room.Configuration));
            Assert.That(RoomConfigurationMessage.TryDeserialize(envelope.ToString(), out _), Is.True);
            envelope.Remove("ProtocolVersion");
            Assert.That(RoomConfigurationMessage.TryDeserialize(envelope.ToString(), out _), Is.False);
            envelope["ProtocolVersion"] = "2";
            Assert.That(RoomConfigurationMessage.TryDeserialize(envelope.ToString(), out _), Is.False);
            envelope["ProtocolVersion"] = RoomState.CurrentProtocolVersion;
            var payload = JObject.Parse((string)envelope["RequestJson"]); payload.Remove("IsUserReady");
            envelope["RequestJson"] = payload.ToString();
            Assert.That(RoomConfigurationMessage.TryDeserialize(envelope.ToString(), out _), Is.False);
            var reply = JObject.FromObject(new RoomConfigurationReply { CommandId = ready.CommandId, Result = RoomConfigurationService.Result(room, string.Empty) });
            Assert.That(RoomConfigurationReply.TryDeserialize(reply.ToString(), out _), Is.True);
            ((JObject)reply["Result"]).Remove("Success");
            Assert.That(RoomConfigurationReply.TryDeserialize(reply.ToString(), out _), Is.False);
        }

        [TestCase("null")] [TestCase("outside-slot")] [TestCase("missing-fields")]
        public void MalformedPendingSwapSnapshotsAreRejectedBeforeClone(string shape)
        {
            var room = Room(); var request = Request<RequestRoomSeatSwapRequest>(room); request.TargetSeatIndex = 1;
            Assert.That(service.RequestSeatSwap(room, request).Success, Is.True);
            var json = JObject.Parse(RoomStateSerializer.Serialize(room));
            Assert.That(RoomStateSerializer.TryDeserialize(json.ToString(), out _, out _), Is.True);
            var swaps = (JArray)json["PendingSeatSwaps"];
            if (shape == "null") swaps[0] = JValue.CreateNull();
            else if (shape == "outside-slot") swaps[0]["SourceSeatIndex"] = 99;
            else ((JObject)swaps[0]).Remove("SourceSeatIndex");
            Assert.That(RoomStateSerializer.TryDeserialize(json.ToString(), out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("room_protocol_mismatch"));
        }

        [TestCase("map-three-players", 2)] [TestCase("map-three-players", 3)]
        [TestCase("map-four-players", 2)] [TestCase("map-four-players", 3)] [TestCase("map-four-players", 4)]
        public void FrozenLaunchFactoryPreservesExplicitMapAndActualPlayers(string mapId, int count)
        {
            var room = Room(count); Assert.That(service.ChangeConfiguration(room, Change(room, mapId)).Success, Is.True);
            ReadyGuests(room);
            Assert.That(service.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out var frozen, out var reason), Is.True, reason);
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, room.Seats, frozen, 71);
            Assert.That(state.MapId, Is.EqualTo(mapId)); Assert.That(state.RuleConfiguration.PlayerCount, Is.EqualTo(count));
            Assert.That(state.Players.Count, Is.EqualTo(count));
            Assert.That(RedZoneAccessRule.GetOpenRound(StaticMapDefinitions.Resolve(mapId), state.RuleConfiguration.PlayerCount),
                Is.EqualTo(count == 2 ? 6 : count == 3 ? 5 : 4));
            Assert.That(state.Players.TrueForAll(p => p.HandCardIds.Count == 5), Is.True);
            frozen.MapId = "mutated"; room.Configuration.RuleModeId = "mutated";
            Assert.That(state.RuleConfiguration.MapId, Is.EqualTo(mapId)); Assert.That(state.RuleConfiguration.RuleModeId, Is.EqualTo("standard"));
            var clone = GameStateCloneService.DeepClone(state);
            clone.RuleConfiguration.EnabledExpansionIds.Add("enterprise");
            Assert.That(state.RuleConfiguration.EnabledExpansionIds.Count, Is.EqualTo(1));
        }

        [Test]
        public void HostCanFreezeWithoutManualReadyButCannotChangeAfterStart()
        {
            var room = Room(); ReadyGuests(room);
            Assert.That(room.SeatSlots[0].IsUserReady, Is.False);
            var start = Request<StartConfiguredGameRequest>(room);
            Assert.That(service.TryFreezeForLaunch(room, start, out _, out _), Is.True);
            Assert.That(service.TryFreezeForLaunch(room, start, out _, out _), Is.True, "重复请求返回既有冻结结果");
            Assert.That(service.ChangeConfiguration(room, Change(room)).ReasonCode, Is.EqualTo("configuration_stale"));
            Assert.That(service.TryOccupySeat(room, Player(4), out _), Is.False);
        }

        [Test]
        public void TcpRoomCommandsReachHostAndReturnAuthoritativeSnapshots()
        {
            using (var host = new NetworkRoomService(content))
            using (var member = new NetworkRoomService(content))
            {
                var created = host.CreateRoom("房主", 3);
                member.JoinRoom("127.0.0.1:" + created.RoomId.Split(':')[1], "成员");
                Assert.That(SpinWait.SpinUntil(() => member.GetCurrentRoom()?.LocalPlayerId > 0 && host.GetCurrentRoom().PlayerCount == 2, 4000), Is.True);
                var guestId = member.GetCurrentRoom().LocalPlayerId;
                host.SetTransportReadiness(1, true, true, 0); host.SetTransportReadiness(guestId, true, true, 1);
                Assert.That(SpinWait.SpinUntil(() => member.GetCurrentRoom().Seats.TrueForAll(p => p.IsReady), 4000), Is.True);
                var snapshot = member.GetCurrentRoom();
                var ready = new SetRoomReadyRequest { RoomId = snapshot.RoomId, CommandId = "member-ready", RequestingPlayerId = guestId,
                    ExpectedConfigurationRevision = snapshot.Configuration.ConfigurationRevision, IsUserReady = true };
                var result = member.SendConfigurationAsync(ready).GetAwaiter().GetResult();
                Assert.That(result.Success, Is.True, result.ReasonCode);
                Assert.That(RoomConfigurationService.FindSlot(host.GetCurrentRoom(), guestId).IsUserReady, Is.True);
                var hostSnapshot = host.GetCurrentRoom();
                var changed = Change(hostSnapshot);
                Assert.That(host.SendConfigurationAsync(changed).GetAwaiter().GetResult().Success, Is.True);
                Assert.That(member.SendConfigurationAsync(ready).GetAwaiter().GetResult().Success, Is.True, "相同 CommandId 返回原受理结果");
                ready.CommandId = "stale-ready";
                Assert.That(member.SendConfigurationAsync(ready).GetAwaiter().GetResult().ReasonCode, Is.EqualTo("configuration_stale"));
                Assert.That(RoomConfigurationService.FindSlot(host.GetCurrentRoom(), guestId).IsUserReady, Is.False);
            }
        }

        [Test]
        public void FrozenLaunchArchiveRejectsConfigurationDriftBeforeEntityRecipeIsInstalled()
        {
            var room = Room(); ReadyGuests(room);
            Assert.That(service.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out var frozen, out _), Is.True);
            var session = new GameSession(GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, room.Seats, frozen, 42));
            var archive = session.CreateHostArchive();
            Assert.That(HostRecoveryService.Recover(archive).Succeeded, Is.True);
            archive.Snapshot.State.RuleConfiguration.PlayerCount = 4;
            Assert.That(HostRecoveryService.Recover(archive).Succeeded, Is.False);
            archive.Snapshot.State.RuleConfiguration.PlayerCount = 3;
            archive.Snapshot.State.RuleConfiguration.MapId = "map-four-players";
            Assert.That(HostRecoveryService.Recover(archive).Succeeded, Is.False);
        }

        [UnityTest]
        public IEnumerator SelectedMapsReachBothGameplayScenesWithActualThreePlayerConfiguration()
        {
            IsolateRuntimeCatalogs();
            yield return new EnterPlayMode();
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var providerType = Type.GetType("YC.Infrastructure.Multiplayer.BasicRoomContentCapabilityProvider, Assembly-CSharp", true);
            var capabilities = (IRoomContentCapabilityProvider)Activator.CreateInstance(providerType);
            var configure = contextType.GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration) });
            foreach (var sceneName in new[] { "ThreePlayerScene", "SampleScene" })
            {
                var mapId = sceneName == "ThreePlayerScene" ? "map-three-players" : "map-four-players";
                var authority = new RoomConfigurationService(capabilities);
                var room = new RoomState { RoomId = "001208", HostPlayerId = 1, LocalPlayerId = 1 };
                room.Configuration.MapId = mapId;
                room.Configuration.ContentPackHash = capabilities.GetContentPackHash(room.Configuration);
                for (var id = 1; id <= 3; id++)
                {
                    var player = Player(id); player.ContentPackHash = room.Configuration.ContentPackHash;
                    Assert.That(authority.TryOccupySeat(room, player, out _), Is.True);
                }
                foreach (var player in room.Seats)
                    player.ContentPackHash = room.Configuration.ContentPackHash;
                foreach (var player in room.Seats)
                    if (player.PlayerId != room.HostPlayerId)
                    {
                        var ready = Request<SetRoomReadyRequest>(room, player.PlayerId); ready.IsUserReady = true;
                        Assert.That(authority.SetReady(room, ready).Success, Is.True);
                    }
                Assert.That(authority.TryFreezeForLaunch(room, Request<StartConfiguredGameRequest>(room), out var frozen, out var reason), Is.True, reason);
                var context = contextType.GetMethod("Ensure").Invoke(null, null);
                configure.Invoke(context, new object[] { LaunchMode.Local, 1, room.RoomId, room.Seats, frozen });
                yield return SceneManager.LoadSceneAsync(sceneName);
                yield return null;
                var controllerType = Type.GetType("YC.Presentation.MobileCityInteractionController, Assembly-CSharp", true);
                var controller = UnityEngine.Object.FindObjectOfType(controllerType);
                Assert.That(controller, Is.Not.Null);
                var session = (GameSession)controllerType.GetField("session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                Assert.That(session, Is.Not.Null, "实际场景控制器必须完成正式组合根装配");
                Assert.That(session.State.MapId, Is.EqualTo(mapId));
                Assert.That(session.State.RuleConfiguration.PlayerCount, Is.EqualTo(3));
                Assert.That(session.State.RuleConfiguration.ContentPackHash, Is.EqualTo(frozen.ContentPackHash));
                Assert.That(session.State.Players.Count, Is.EqualTo(3));
                Assert.That(RedZoneAccessRule.GetOpenRound(StaticMapDefinitions.Resolve(mapId), session.State.Players.Count), Is.EqualTo(5));
                var archived = session.CreateHostArchive();
                Assert.That(archived.Snapshot.State.RuleConfiguration.MapId, Is.EqualTo(mapId));
                Assert.That(archived.Snapshot.State.RuleConfiguration.PlayerCount, Is.EqualTo(3));
                EffectRegistry runtimeRegistry = null;
                var handlers = (IEnumerable)typeof(GameSession).GetField("commandHandlers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
                foreach (var handler in handlers)
                    if (handler is YC.Application.Interactions.AnswerInteractionCommandHandler)
                        runtimeRegistry = (EffectRegistry)handler.GetType().GetField("registry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler);
                Assert.That(runtimeRegistry, Is.Not.Null, "恢复使用当前正式组合根登记的同一 registry");
                var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archived), runtimeRegistry);
                Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
                Assert.That(restored.State.RuleConfiguration.MapId, Is.EqualTo(mapId));
                Assert.That(restored.State.RuleConfiguration.ContentPackHash, Is.EqualTo(frozen.ContentPackHash));
                restored.State.RuleConfiguration.EnabledExpansionIds.Add("enterprise");
                Assert.That(session.State.RuleConfiguration.EnabledExpansionIds.Count, Is.EqualTo(1));
                Debug.Log("房间冻结配置进入实际场景：" + sceneName + "，地图=" + mapId + "，实际人数=3，revision=" + frozen.ConfigurationRevision);
            }
            yield return new ExitPlayMode();
        }

        [UnityTearDown]
        public IEnumerator LeavePlayModeAfterSceneVerification()
        {
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
            restoreRuntimeCatalogs?.Invoke(); restoreRuntimeCatalogs = null;
        }

        private void IsolateRuntimeCatalogs()
        {
            // 项目关闭 Domain Reload；测试目录只在内存中隔离，实际场景自行加载生产目录。
            var snapshots = new List<KeyValuePair<FieldInfo, object>>();
            var types = new[] { typeof(FacilityCardDatabase), typeof(CharacterCardDatabase), typeof(EventCardDatabase), typeof(CityStyleDatabase), typeof(SpecialActionDatabase) };
            foreach (var type in types)
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                    if (!field.IsInitOnly && !field.IsLiteral) snapshots.Add(new KeyValuePair<FieldInfo, object>(field, field.GetValue(null)));
            var facilityDefinitions = (Dictionary<string, FacilityCardDefinition>)typeof(FacilityCardDatabase).GetField("Definitions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var previousFacilities = new Dictionary<string, FacilityCardDefinition>(facilityDefinitions);
            restoreRuntimeCatalogs = () =>
            {
                facilityDefinitions.Clear(); foreach (var pair in previousFacilities) facilityDefinitions.Add(pair.Key, pair.Value);
                foreach (var snapshot in snapshots) snapshot.Key.SetValue(null, snapshot.Value);
            };
            facilityDefinitions.Clear();
            typeof(FacilityCardDatabase).GetField("injectedDefinitions", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
            foreach (var type in types)
                if (type != typeof(FacilityCardDatabase)) type.GetMethod("ResetForTests", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }
    }
}
