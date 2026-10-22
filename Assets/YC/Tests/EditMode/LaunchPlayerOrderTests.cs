using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.CityStyles;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class LaunchPlayerOrderTests
    {
        private sealed class Content : IRoomContentCapabilityProvider
        {
            public string GetContentPackHash(GameRuleConfiguration configuration) => "launch-order-content";
            public bool TryValidate(GameRuleConfiguration configuration, out string reason)
            { reason = string.Empty; return true; }
        }

        private static StartConfiguredGameRequest Start(RoomState room, string id = "start") => new StartConfiguredGameRequest
        { RoomId = room.RoomId, CommandId = id, RequestingPlayerId = room.HostPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision };

        private static GameState State(params int[] order)
        {
            var configuration = new GameRuleConfiguration { PlayerCount = order.Length, ContentPackHash = "launch-order-content",
                MapId = order.Length == 4 ? "map-four-players" : "map-three-players" };
            return new GameState
            {
                GameId = "launch-order-game", RuleConfiguration = configuration, MapId = configuration.MapId,
                ContentPackHash = configuration.ContentPackHash, LaunchPlayerOrder = order.ToList(), UseSeatTurnOrder = true,
                StartPlayerId = order[0], CurrentPlayerId = order[0],
                Players = order.OrderBy(id => id).Select((id, i) => new PlayerState { PlayerId = id, Name = "玩家 " + id, Color = (PlayerColor)i }).ToList()
            };
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void FreezeRunsOnceAfterGatesAndPurePreflightPreservingIdentity(int count)
        {
            var calls = 0;
            var service = new RoomConfigurationService(new Content(), true, limit => { calls++; return limit > 2 ? 1 : 0; });
            var room = new RoomState { RoomId = "order-room", HostPlayerId = 1, LocalPlayerId = 1 };
            for (var i = 0; i < count; i++) Assert.That(service.TryOccupySeat(room,
                new PlayerSeat { PlayerId = 1 + i * 3, PlayerName = "玩家 " + i, LobbyMemberPresent = true }, out _), Is.True);
            var before = JsonConvert.SerializeObject(room.SeatSlots);
            var revision = room.Configuration.ConfigurationRevision;
            var denied = Start(room, "non-host"); denied.RequestingPlayerId = 4;
            Assert.That(service.TryFreezeForLaunch(room, denied, out _, out _), Is.False);
            var stale = Start(room, "stale"); stale.ExpectedConfigurationRevision--;
            Assert.That(service.TryFreezeForLaunch(room, stale, out _, out _), Is.False);
            Assert.That(calls, Is.Zero);
            var start = Start(room);
            var message = RoomConfigurationMessage.Create(start, new Content(), room.Configuration);
            Assert.That(message.ValidateStart(room, 1, service).Success, Is.True);
            Assert.That(message.ValidateStart(room, 1, service).Success, Is.True);
            Assert.That(calls, Is.Zero);
            Assert.That(room.HasStarted, Is.False);
            Assert.That(room.LaunchPlayerOrder, Is.Empty);
            Assert.That(LobbyStartCommitPolicy.TryCommit(_ => false, _ => true, out _), Is.False);
            Assert.That(calls, Is.Zero);
            Assert.That(message.Execute(room, 1, service).Success, Is.True);
            var frozen = room.LaunchPlayerOrder.ToArray();
            Assert.That(TurnOrderService.IsValidPlayerOrder(frozen, room.Seats.Select(player => player.PlayerId)), Is.True);
            if (count == 4) Assert.That(frozen, Is.EqualTo(new[] { 7, 1, 10, 4 }));
            Assert.That(message.Execute(room, 1, service).Success, Is.True);
            Assert.That(service.TryFreezeForLaunch(room, Start(room, "another-start"), out _, out _), Is.False);
            Assert.That(calls, Is.EqualTo(count - 1));
            Assert.That(room.LaunchPlayerOrder, Is.EqualTo(frozen));
            Assert.That(JsonConvert.SerializeObject(room.SeatSlots), Is.EqualTo(before));
            Assert.That(room.Configuration.ConfigurationRevision, Is.EqualTo(revision));
            Assert.That(room.HostPlayerId, Is.EqualTo(1));
            Assert.That(RoomStateSerializer.TryDeserialize(RoomStateSerializer.Serialize(room), out var restoredRoom, out _), Is.True);
            Assert.That(restoredRoom.LaunchPlayerOrder, Is.EqualTo(frozen));
            var roomJson = JObject.Parse(RoomStateSerializer.Serialize(room)); roomJson.Remove("LaunchPlayerOrder");
            Assert.That(RoomStateSerializer.TryDeserialize(roomJson.ToString(), out _, out _), Is.False);
            roomJson["LaunchPlayerOrder"] = new JArray(Enumerable.Repeat(1, count));
            Assert.That(RoomStateSerializer.TryDeserialize(roomJson.ToString(), out _, out _), Is.False);
            var copy = room.Clone(); copy.LaunchPlayerOrder.Reverse();
            Assert.That(room.LaunchPlayerOrder, Is.EqualTo(frozen));
            Assert.That(new RoomConfigurationService(new Content()).TryValidateLaunch(new RoomState
                { RoomId = room.RoomId, HostPlayerId = 1, LocalPlayerId = 1, SeatSlots = room.Clone().SeatSlots, Configuration = room.Configuration.Clone() }, out _), Is.False);
        }

        [Test]
        public void FrozenOrderPassesFactoryProjectionAndRoundChainWithoutReorderingPlayers()
        {
            var original = State(7, 1, 10, 4);
            var seats = original.Players.Select(player => new PlayerSeat { PlayerId = player.PlayerId, PlayerName = player.Name, Color = player.Color }).ToList();
            var frozen = original.LaunchPlayerOrder.ToArray();
            var recipe = new GameSetupRecipe();
            var definitions = new GameContentDefinitions();
            var client = GameLaunchStateFactory.CreateInitialState(LaunchMode.Client, 4, seats, original.RuleConfiguration, 123, recipe, definitions, frozen);
            var repeated = GameLaunchStateFactory.CreateInitialState(LaunchMode.Client, 4, seats, original.RuleConfiguration, 123, recipe, definitions, frozen);
            Assert.That(client.StartPlayerId, Is.EqualTo(7));
            Assert.That(client.CurrentPlayerId, Is.EqualTo(7));
            Assert.That(client.Players.Select(player => player.PlayerId), Is.EqualTo(seats.Select(player => player.PlayerId)));
            Assert.That(repeated.LaunchPlayerOrder, Is.EqualTo(frozen));
            var service = new TurnOrderService();
            Assert.That(service.GetTurnOrder(client), Is.EqualTo(frozen));
            var chain = new RoundMainChainBuilder().Build(client.GameId, "round", 1, frozen, 7, true);
            foreach (var type in new[] { RoundMainlineNodeTypeIds.PlayerEntrance, RoundMainlineNodeTypeIds.PlayerCleanupWindow })
                Assert.That(chain.Nodes.Where(node => node.NodeTypeId == type).Select(node => node.PlayerId), Is.EqualTo(frozen));
            foreach (var actionRound in new[] { 1, 2 })
                Assert.That(chain.Nodes.Where(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && node.ActionRound == actionRound).Select(node => node.PlayerId), Is.EqualTo(frozen));
            Assert.That(service.GetTurnOrder(client, 1), Is.EqualTo(new[] { 1, 10, 4, 7 }));
            var view = GameStateViewProjector.ProjectForPlayer(client, 4);
            var mirror = GameStateViewProjector.ToClientState(view);
            Assert.That(service.GetLaunchPlayerOrder(view), Is.EqualTo(frozen));
            Assert.That(service.GetTurnOrder(mirror), Is.EqualTo(frozen));
            Assert.That(service.GetLaunchPlayerOrder(view), Is.Not.SameAs(view.LaunchPlayerOrder));
            Assert.Throws<InvalidOperationException>(() => GameLaunchStateFactory.CreateInitialState(LaunchMode.Client, 4, seats,
                original.RuleConfiguration, 123, recipe, definitions, new[] { 1, 1, 7, 10 }));
        }

        [Test]
        public void ProjectionAndRecoveryPreserveOrderAndRejectDriftWhileLegacyArchiveInheritsKnownRing()
        {
            var state = State(3, 1, 4, 2);
            var chain = new RoundMainChainBuilder().Build(state.GameId, "recovery-round", 1, state.LaunchPlayerOrder, 3, true);
            state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = 1;
            state.EffectRuntime.CurrentRoundStartPlayerId = 3;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int>(chain.PlayerOrderSnapshot);
            state.EffectRuntime.MainNodes = chain.Nodes;
            state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.ActiveMainNodeId = chain.FirstNodeId;
            var view = GameStateViewProjector.ProjectForPlayer(state, 2);
            Assert.That(view.HasValidPlayerOrderAndSlots(), Is.True);
            Assert.That(view.LaunchPlayerOrder, Is.EqualTo(state.LaunchPlayerOrder));
            var archive = HostRecoveryService.CreateArchive(state);
            var committed = GameStateCloneService.DeepClone(state); committed.Round = 2;
            RuleCommit.Apply(committed, "round-advance", new RuleJournalEntry { Kind = RuleJournalEntryKind.DomainState, EntityId = "round", ContentHash = state.ContentPackHash });
            HostRecoveryService.AppendJournal(archive, committed);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive));
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.LaunchPlayerOrder, Is.EqualTo(state.LaunchPlayerOrder));
            Assert.That(new TurnOrderService().GetTurnOrder(recovered.State, 1), Is.EqualTo(new[] { 1, 4, 2, 3 }));
            var brokenMainline = GameStateCloneService.DeepClone(archive);
            brokenMainline.Snapshot.State.EffectRuntime.PlayerOrderSnapshot.Reverse();
            Assert.That(HostRecoveryService.Recover(brokenMainline).Succeeded, Is.False);
            var missing = JObject.Parse(HostRecoveryService.SerializeArchive(archive)); ((JObject)missing["Snapshot"]["State"]).Remove("LaunchPlayerOrder");
            Assert.That(HostRecoveryService.RecoverJson(missing.ToString()).Succeeded, Is.False);
            archive.Journal.Entries[0].StateAfter.LaunchPlayerOrder.Reverse();
            Assert.That(HostRecoveryService.Recover(archive).Succeeded, Is.False);
            var old = HostRecoveryService.CreateArchive(State(1, 2, 3));
            var legacyJson = JObject.Parse(HostRecoveryService.SerializeArchive(old));
            legacyJson["Snapshot"]["SchemaVersion"] = 2; legacyJson["Journal"]["SchemaVersion"] = 2;
            ((JObject)legacyJson["Snapshot"]["State"]).Remove("LaunchPlayerOrder");
            var inherited = HostRecoveryService.RecoverJson(legacyJson.ToString());
            Assert.That(inherited.Succeeded, Is.True, inherited.Diagnostic);
            Assert.That(inherited.State.LaunchPlayerOrder, Is.EqualTo(new[] { 1, 2, 3 }));
            legacyJson["Snapshot"]["State"]["UseSeatTurnOrder"] = false;
            var legacyPlayers = (JArray)legacyJson["Snapshot"]["State"]["Players"];
            legacyJson["Snapshot"]["State"]["Players"] = new JArray(legacyPlayers[2].DeepClone(), legacyPlayers[0].DeepClone(), legacyPlayers[1].DeepClone());
            inherited = HostRecoveryService.RecoverJson(legacyJson.ToString());
            Assert.That(inherited.Succeeded, Is.True, inherited.Diagnostic);
            Assert.That(inherited.State.LaunchPlayerOrder, Is.EqualTo(new[] { 3, 1, 2 }));
        }

        [Test]
        public void PublicCityStyleSlotsAreDistinctOrderedClonedAndValidatedWithoutPrivateState()
        {
            var state = State(2, 1);
            state.FindPlayer(1).DeclaredCityStyles.Add(new CityStyleDeclarationState { CityStyleId = "one", UsedCityBoardSlotIndexes = new List<int> { 11, 3, 0 } });
            state.FindPlayer(1).DeclaredCityStyles.Add(new CityStyleDeclarationState { CityStyleId = "two", UsedCityBoardSlotIndexes = new List<int> { 3, 7 } });
            state.FindPlayer(1).HandCardIds.Add("private-card");
            foreach (var viewer in new[] { GameStateViewer.Host, GameStateViewer.Player(2), GameStateViewer.Spectator })
            {
                var view = GameStateViewProjector.Project(state, viewer);
                Assert.That(view.Players.Find(player => player.PlayerId == 1).CityStyleUsedSlotIndexes, Is.EqualTo(new[] { 0, 3, 7, 11 }));
                Assert.That(view.Players.Find(player => player.PlayerId == 2).CityStyleUsedSlotIndexes, Is.Empty);
                var received = JsonConvert.DeserializeObject<GameStateView>(JsonConvert.SerializeObject(view));
                Assert.That(received.HasValidPlayerOrderAndSlots(), Is.True);
                var copy = GameStateCloneService.DeepClone(received);
                copy.Players[0].CityStyleUsedSlotIndexes.Add(9);
                Assert.That(received.Players[0].CityStyleUsedSlotIndexes, Is.EqualTo(new[] { 0, 3, 7, 11 }));
                if (!viewer.IsHost) Assert.That(JsonConvert.SerializeObject(view), Does.Not.Contain("private-card"));
                foreach (var invalid in new[] { -1, 12 })
                {
                    var raw = JObject.FromObject(view); raw["Players"][0]["CityStyleUsedSlotIndexes"] = new JArray(invalid);
                    Assert.That(raw.ToObject<GameStateView>().HasValidPlayerOrderAndSlots(), Is.False);
                }
            }
            Assert.That(state.FindPlayer(1).DeclaredCityStyles[0].UsedCityBoardSlotIndexes, Is.EqualTo(new[] { 11, 3, 0 }));
        }
    }
}
