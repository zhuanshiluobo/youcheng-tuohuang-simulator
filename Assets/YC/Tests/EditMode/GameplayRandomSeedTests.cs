using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    public sealed class GameplayRandomSeedTests
    {
        private static GameState State(int seed = -1234567) => new GameState
        { GameId = "match-a", EventDeckSeed = seed, RandomRuntime = GameplayRandomRuntime.Create("match-a") };
        [Test]
        public void VersionedIntegerSequenceMatchesIndependentGoldenVector()
        {
            var state = State();
            Assert.That(Enumerable.Range(0, 10).Select(_ => GameplayRandomRuntime.Next(state, GameplayRandomRuntime.Dice, 6)).ToArray(),
                Is.EqualTo(new[] { 5, 1, 0, 2, 4, 2, 0, 0, 0, 3 }));
            var before = JsonConvert.SerializeObject(state.RandomRuntime);
            Reject(() => GameplayRandomRuntime.Next(new GameState(), GameplayRandomRuntime.Dice, 6));
            state.RandomRuntime.AlgorithmVersion = "unsupported";
            Assert.That(GameplayRandomRuntime.TryValidate(state.RandomRuntime, state.GameId, state.EventDeckSeed, out _), Is.False);
            state.RandomRuntime.AlgorithmVersion = GameplayRandomRuntime.AlgorithmVersion;
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(before));
        }
        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Assert.That(rejected, Is.True);
        }
        private sealed class Capabilities : IRoomContentCapabilityProvider
        {
            public string GetContentPackHash(GameRuleConfiguration configuration) => "random-test-content";
            public bool TryValidate(GameRuleConfiguration configuration, out string reason)
            { reason = ""; return true; }
        }
        private static RoomState Room(RoomConfigurationService service)
        {
            var room = new RoomState { RoomId = "room-a", HostPlayerId = 1, LocalPlayerId = 1 };
            for (int id = 1; id <= 3; id++)
                Assert.That(service.TryOccupySeat(room, new PlayerSeat
                { PlayerId = id, PlayerName = "玩家" + id, LobbyMemberPresent = true }, out _), Is.True);
            return room;
        }
        private static StartConfiguredGameRequest Request(RoomState room, string commandId = "start") => new StartConfiguredGameRequest
        { RoomId = room.RoomId, RequestingPlayerId = 1, CommandId = commandId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision };
        private static GameState Launch(RoomState room, LaunchMode mode = LaunchMode.Local)
        {
            var configuration = room.Configuration.Clone();
            configuration.ContentPackHash = string.Empty;
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(
                Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core")), configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            return GameLaunchStateFactory.CreateInitialState(mode, 1, room.Seats, configuration, room.EventDeckSeed,
                catalog.SetupRecipe, catalog.ContentDefinitions, room.LaunchPlayerOrder, room.GameId, room.RandomRuntime);
        }

        [Test]
        public void HostFreezesSeedBeforeOrderAndDuplicateStartDoesNotConsumeAgain()
        {
            int entropyCalls = 0;
            var service = new RoomConfigurationService(new Capabilities(), true, seedSource: () => { entropyCalls++; return 0; });
            var room = Room(service); var request = Request(room);
            Assert.That(service.TryFreezeForLaunch(room, request, out _, out _), Is.True);
            Assert.That(entropyCalls, Is.EqualTo(1));
            Assert.That(room.EventDeckSeed, Is.EqualTo(0));
            Assert.That(room.RandomRuntime.Streams.Single().StreamId, Is.EqualTo(GameplayRandomRuntime.LaunchOrder));
            Assert.That(room.RandomRuntime.Streams.Single().RequestsConsumed, Is.EqualTo(2UL));
            string frozen = RoomStateSerializer.Serialize(room);
            Assert.That(service.TryFreezeForLaunch(room, request, out _, out _), Is.True);
            Assert.That(service.TryFreezeForLaunch(room, Request(room, "another-start"), out _, out _), Is.False);
            Assert.That(entropyCalls, Is.EqualTo(1));
            Assert.That(RoomStateSerializer.Serialize(room), Is.EqualTo(frozen));
            Assert.That(RoomStateSerializer.TryDeserialize(frozen, out var received, out _), Is.True);
            Assert.That(received.GameId, Is.EqualTo(room.GameId));
            received.RandomRuntime.Streams[0].State++;
            Assert.That(room.RandomRuntime.Streams[0].State, Is.Not.EqualTo(received.RandomRuntime.Streams[0].State));
            foreach (string field in new[] { "GameId", "EventDeckSeed", "RandomReady", "RandomAlgorithmVersion", "RandomRuntime" })
            {
                var missing = JObject.Parse(frozen); missing.Remove(field);
                Assert.That(RoomStateSerializer.TryDeserialize(missing.ToString(), out _, out _), Is.False, field);
            }
        }

        [Test]
        public void RejectedRoomLaunchDoesNotGenerateSeed()
        {
            int calls = 0;
            var service = new RoomConfigurationService(new Capabilities(), true, seedSource: () => { calls++; return 42; });
            var room = Room(service); var request = Request(room); request.RequestingPlayerId = 2;
            Assert.That(service.TryFreezeForLaunch(room, request, out _, out _), Is.False);
            Assert.That(calls, Is.EqualTo(0));
            Assert.That(room.RandomReady, Is.False);
        }

        [Test]
        public void SameFrozenFactProducesIdenticalSetupAndClientWaitsForInitialView()
        {
            var service = new RoomConfigurationService(new Capabilities(), true, seedSource: () => 42);
            var room = Room(service); Assert.That(service.TryFreezeForLaunch(room, Request(room), out _, out _), Is.True);
            string initial = JsonConvert.SerializeObject(room.RandomRuntime);
            var states = Enumerable.Range(0, 4).Select(_ => Launch(room)).ToArray();
            foreach (var state in states.Skip(1)) Assert.That(GameStateCloneService.AreEquivalent(states[0], state), Is.True);
            Assert.That(JsonConvert.SerializeObject(room.RandomRuntime), Is.EqualTo(initial));
            var client = Launch(room, LaunchMode.Client);
            Assert.That(client.GameId, Is.EqualTo(room.GameId));
            Assert.That(client.EventDeckSeed, Is.EqualTo(room.EventDeckSeed));
            Assert.That(JsonConvert.SerializeObject(client.RandomRuntime), Is.EqualTo(initial));
            Assert.That(client.ItemZones.Items.Count, Is.EqualTo(0));
            Reject(() => GameLaunchStateFactory.CreateInitialState(LaunchMode.Client, 1, room.Seats, room.Configuration, 42));
            var session = new GameSession(client);
            var view = GameStateViewProjector.Project(states[0], GameStateViewer.Player(1));
            session.ReplaceView(view); session.ReplaceView(view);
            Assert.That(session.State.EventDeckSeed, Is.EqualTo(42));
            Assert.That(JsonConvert.SerializeObject(session.State.RandomRuntime), Is.EqualTo(initial));
            var wrong = GameStateCloneService.DeepClone(view); wrong.GameId = "another-match";
            Reject(() => session.ReplaceView(wrong));
        }

        [Test]
        public void FullReturnAndRepeatedShuffleKeepAdvancingSameStream()
        {
            var state = State(); var control = State();
            var original = Enumerable.Range(0, 12).Select(i => "card-" + i).ToList();
            for (int cycle = 0; cycle < 3; cycle++)
            {
                var returned = new List<string>(original); var expected = new List<string>(original);
                GameplayRandomRuntime.Shuffle(returned, state, GameplayRandomRuntime.DeckShuffle);
                GameplayRandomRuntime.Shuffle(expected, control, GameplayRandomRuntime.DeckShuffle);
                Assert.That(returned, Is.EqualTo(expected));
                Assert.That(returned.OrderBy(id => id), Is.EqualTo(original.OrderBy(id => id)));
                Assert.That(state.RandomRuntime.Streams.Single().RequestsConsumed, Is.EqualTo((ulong)(11 * (cycle + 1))));
            }
            Assert.That(state.EventDeckSeed, Is.EqualTo(-1234567));
            for (int i = 0; i < 100; i++) Assert.That(GameplayRandomRuntime.Next(state, GameplayRandomRuntime.Dice, 6), Is.InRange(0, 5));
            Assert.That(state.RandomRuntime.Streams.Count, Is.EqualTo(2));
        }

        [Test]
        public void DiceAndDeckExecutorsUseStateAndExistingDiceReceiptDoesNotConsumeAgain()
        {
            var state = State(); state.Players.Add(new PlayerState { PlayerId = 1 });
            var registry = new EffectRegistry(); DiceRollEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry);
            var kernel = new EffectTreeExecutor(state, registry);
            var dice = new EffectSpec(LuaDomainEffectTypeIds.RollDice, Object("apiVersion", NormalizedValue.CreateInteger(1),
                "player", NormalizedValue.CreateString("1"), "count", NormalizedValue.CreateInteger(1), "sides", NormalizedValue.CreateInteger(6))) { PlayerId = 1 };
            string id = kernel.CreateRoot(dice);
            Assert.That(kernel.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(kernel.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.RandomRuntime.Streams.Single().RequestsConsumed, Is.EqualTo(1UL));
            Assert.That(kernel.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(state.RandomRuntime.Streams.Single().RequestsConsumed, Is.EqualTo(1UL));
            var resumed = State(); resumed.Players.Add(new PlayerState { PlayerId = 1 });
            resumed.RandomRuntime = state.RandomRuntime.Clone();
            var resumedKernel = new EffectTreeExecutor(resumed, registry);
            string nextId = resumedKernel.CreateRoot(dice);
            Set(resumedKernel.GetNode(nextId), "diceResult", NormalizedValue.CreateInteger(4));
            Assert.That(resumedKernel.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(resumed.RandomRuntime.Streams.Single().RequestsConsumed, Is.EqualTo(1UL));
            Assert.That(Number(resumedKernel.GetNode(nextId).NormalizedResult, "diceResult"), Is.EqualTo(4));
            var shuffle = State(); shuffle.EntitySchemaVersion = 0; shuffle.Players.Add(new PlayerState { PlayerId = 1 });
            shuffle.Decks.FacilityDeck.AddRange(new[] { "a", "b", "c", "d" });
            var shuffleKernel = new EffectTreeExecutor(shuffle, registry);
            var spec = new EffectSpec(LuaDomainEffectTypeIds.ShuffleFacilityDeck, Object("player", NormalizedValue.CreateString("1"))) { PlayerId = 1 };
            shuffleKernel.CreateRoot(spec);
            Assert.That(shuffleKernel.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(shuffle.RandomRuntime.Streams.Single().RequestsConsumed, Is.EqualTo(3UL));
            shuffleKernel.CreateRoot(spec);
            Assert.That(shuffleKernel.RunUntilQuiescent().Faulted, Is.False);
            Assert.That(shuffle.RandomRuntime.Streams.Single().RequestsConsumed, Is.EqualTo(6UL));
        }

        private sealed class FailedRandomHandler : IGameCommandHandler
        {
            public bool Throw;
            public bool CanHandle(GameCommand command) => true;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                GameplayRandomRuntime.Next(state, GameplayRandomRuntime.Dice, 6);
                if (Throw) throw new InvalidOperationException("expected-test-failure");
                return CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, "测试拒绝"));
            }
        }
        [Test]
        public void FailedRuleTransactionsDoNotConsumeCommittedCursor()
        {
            var state = State(); var session = new GameSession(state); var handler = new FailedRandomHandler(); session.RegisterHandler(handler);
            string before = JsonConvert.SerializeObject(state.RandomRuntime);
            foreach (bool throws in new[] { false, true })
            {
                handler.Throw = throws;
                Assert.That(session.Submit(new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 }).Succeeded, Is.False);
                Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(before));
            }
            Assert.That(GameplayRandomRuntime.Next(state, GameplayRandomRuntime.Dice, 6),
                Is.EqualTo(GameplayRandomRuntime.Next(State(), GameplayRandomRuntime.Dice, 6)));
        }

        [Test]
        public void SavedRandomStateResumesAndUnsupportedAlgorithmIsRejected()
        {
            var service = new RoomConfigurationService(new Capabilities(), true, seedSource: () => 42);
            var room = Room(service); Assert.That(service.TryFreezeForLaunch(room, Request(room), out _, out _), Is.True);
            var state = Launch(room);
            for (int i = 0; i < 17; i++) GameplayRandomRuntime.Next(state, GameplayRandomRuntime.Dice, 6);
            var archive = HostRecoveryService.CreateArchive(state);
            string json = HostRecoveryService.SerializeArchive(archive);
            var result = HostRecoveryService.RecoverJson(json);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            for (int i = 0; i < 20; i++) Assert.That(GameplayRandomRuntime.Next(result.State, GameplayRandomRuntime.Dice, 6),
                Is.EqualTo(GameplayRandomRuntime.Next(state, GameplayRandomRuntime.Dice, 6)));
            var invalid = JObject.Parse(json); invalid["Snapshot"]["State"]["RandomRuntime"]["AlgorithmVersion"] = "unsupported";
            Assert.That(HostRecoveryService.RecoverJson(invalid.ToString()).Succeeded, Is.False);
            invalid = JObject.Parse(json); ((JObject)invalid["Snapshot"]["State"]["RandomRuntime"]).Remove("AlgorithmVersion");
            Assert.That(HostRecoveryService.RecoverJson(invalid.ToString()).Succeeded, Is.False);
            invalid = JObject.Parse(json); ((JObject)invalid["Snapshot"]["State"]).Remove("RandomRuntime");
            Assert.That(HostRecoveryService.RecoverJson(invalid.ToString()).Succeeded, Is.False);
            var legacy = JObject.Parse(json); ((JObject)legacy["Snapshot"]).Remove("RandomStateRequired");
            ((JObject)legacy["Snapshot"]["State"]).Remove("RandomRuntime");
            var old = HostRecoveryService.RecoverJson(legacy.ToString());
            Assert.That(old.Succeeded, Is.True, old.Diagnostic);
            Assert.That(old.State.RandomRuntime, Is.Null);
            Assert.That(old.State.EventDeckSeed, Is.EqualTo(42));
            Reject(() => GameplayRandomRuntime.Next(old.State, GameplayRandomRuntime.Dice, 6));
        }
    }
}
