using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class MatchSaveRegressionTests
    {
        private static MatchSaveData Sample(string gameId = "save-regression")
        {
            var state = new GameState { GameId = gameId, MapId = "four-player", CurrentPlayerId = 1, Round = 2 };
            state.Players.Add(new PlayerState { PlayerId = 1, Color = PlayerColor.Blue, HandCardIds = new List<string> { "private-card" } });
            state.Players[0].Resources.Iron = 17;
            state.Decks.CharacterDeck.AddRange(new[] { "third", "first", "second" });
            return new MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, LocalPlayerId = 1, Mode = LaunchMode.Local,
                GameId = state.GameId, MapId = state.MapId, Round = state.Round, ContentHash = "test-pack",
                Archive = HostRecoveryService.CreateArchive(state, "test-pack"),
                Seats = new List<MatchSaveSeat> { new MatchSaveSeat { PlayerId = 1, PlayerName = "热座", Color = PlayerColor.Blue } } };
        }
        // Persistence lives in Assembly-CSharp; follow existing tests' reflection boundary instead of changing assembly layout.
        private static Type StoreType => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("YC.Infrastructure.Persistence.MatchSaveStore")).First(t => t != null);
        private static object Store(out string directory)
        {
            directory = Path.Combine(Path.GetTempPath(), "YC-MatchSaveTests", Guid.NewGuid().ToString("N"));
            return Activator.CreateInstance(StoreType, directory);
        }
        private static Task Save(object store, MatchSaveData data, int slot = -1)
            => (Task)StoreType.GetMethod("SaveAsync").Invoke(store, new object[] { data, slot });
        private static MatchSaveData Read(object store, int slot)
            => (MatchSaveData)StoreType.GetMethod("Read").Invoke(store, new object[] { slot });
        private static void SetRestoreSource(object store, int slot, string gameId)
            => StoreType.GetMethod("SetRestoreSource").Invoke(store, new object[] { slot, gameId });
        private static void BeginMatch(object store, string gameId, bool isNewMatch)
            => StoreType.GetMethod("BeginMatch").Invoke(store, new object[] { gameId, isNewMatch });

        [Test]
        public void JsonRoundTrip_PreservesDeepEffectParametersPendingChoiceAndDeckOrder()
        {
            var store = Store(out _); var data = Sample(); var state = data.Archive.Snapshot.State;
            state.PendingChoice = new PendingChoiceState { ChoiceId = "choice", PlayerId = 1, ChoiceType = "test", OptionIds = new List<string> { "a", "b" } };
            var value = NormalizedValue.CreateString("deep-private-leaf");
            for (int i = 0; i < 6; i++) value = NormalizedValue.CreateArray(new[] { value });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "nested", Payload = value });
            Save(store, data, 0).GetAwaiter().GetResult();
            var restored = Read(store, 0).Archive.Snapshot.State;
            Assert.That(restored.PendingChoice.OptionIds, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(restored.Decks.CharacterDeck, Is.EqualTo(new[] { "third", "first", "second" }));
            Assert.That(restored.FindPlayer(1).Resources.Iron, Is.EqualTo(17));
            Assert.That(restored.FindPlayer(1).HandCardIds, Is.EqualTo(new[] { "private-card" }));
            var leaf = restored.EffectRuntime.RuleEvents[0].Payload;
            for (int i = 0; i < 6; i++) leaf = leaf.Items[0];
            Assert.That(leaf.StringValue, Is.EqualTo("deep-private-leaf"));
        }
        [Test]
        public void QueuedSaves_KeepOneAutomaticSavePerMatchAndManualIsNotDropped()
        {
            var store = Store(out var directory); var data = Sample();
            var tasks = new List<Task>();
            for (int i = 0; i < 7; i++)
            {
                data.Round = data.Archive.Snapshot.State.Round = i;
                tasks.Add(Save(store, data));
                if (i == 3) tasks.Add(Save(store, data, 1));
            }
            // 调用返回后修改摘要和嵌套字段，不能污染已排队的不可变快照。
            data.GameId = data.Archive.Snapshot.State.GameId = "later-match";
            data.ContentHash = data.Archive.Snapshot.ContentHash = "later-pack";
            data.Seats[0].PlayerName = "后续修改";
            data.Archive.Snapshot.State.Decks.CharacterDeck.Clear();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            var automatic = Read(store, 3);
            var manual = Read(store, 1);
            Assert.That(automatic.Round, Is.EqualTo(6));
            Assert.That(manual.Round, Is.EqualTo(3));
            foreach (var saved in new[] { automatic, manual })
            {
                Assert.That(saved.GameId, Is.EqualTo("save-regression"));
                Assert.That(saved.ContentHash, Is.EqualTo("test-pack"));
                Assert.That(saved.Seats[0].PlayerName, Is.EqualTo("热座"));
                Assert.That(saved.Archive.Snapshot.State.Decks.CharacterDeck, Is.EqualTo(new[] { "third", "first", "second" }));
            }
            Assert.That(Directory.GetFiles(directory, "auto-*.json").Length, Is.EqualTo(1));
        }
        [Test]
        public void OverwriteKeepsLatestWithoutBackup_CorruptSaveFailsAndFailedWritePreservesOldFile()
        {
            var store = Store(out var directory); var data = Sample();
            Save(store, data, 0).GetAwaiter().GetResult();
            data.Round = data.Archive.Snapshot.State.Round = 8;
            Save(store, data, 0).GetAwaiter().GetResult();
            var file = Path.Combine(directory, "manual-1.json"); var before = File.ReadAllText(file);
            Assert.That(Read(store, 0).Round, Is.EqualTo(8));
            Assert.That(File.Exists(file + ".bak"), Is.False);
            using (var locked = new FileStream(file + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => Save(store, data, 0).GetAwaiter().GetResult());
            Assert.That(File.ReadAllText(file), Is.EqualTo(before));
            File.WriteAllText(file, "broken");
            Assert.Throws<TargetInvocationException>(() => Read(store, 0));
        }
        [Test]
        public void AutomaticSave_DoesNotOverwriteIncompatibleOrCorruptSlots()
        {
            var store = Store(out var directory);
            for (int i = 0; i < 3; i++) Save(store, Sample("protected-" + i)).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(directory, "auto-2.json"), "broken");
            var old = Enumerable.Range(1, 3).Select(i => File.ReadAllText(Path.Combine(directory, "auto-" + i + ".json"))).ToArray();
            var data = Sample("protected-0");
            data.ContentHash = data.Archive.Snapshot.ContentHash = "different-pack";
            Assert.Throws<IOException>(() => Save(store, data).GetAwaiter().GetResult());
            Assert.That(Enumerable.Range(1, 3).Select(i => File.ReadAllText(Path.Combine(directory, "auto-" + i + ".json"))).ToArray(), Is.EqualTo(old));
        }
        [Test]
        public void AutomaticSaves_UpdateOnlyResumedMatchAndFullSlotsDoNotEvictOtherMatches()
        {
            var store = Store(out var directory);
            Save(store, Sample("first-match")).GetAwaiter().GetResult();
            Save(store, Sample("second-match")).GetAwaiter().GetResult();
            Save(store, Sample("third-match")).GetAwaiter().GetResult();
            var firstFile = Path.Combine(directory, "auto-1.json");
            var thirdFile = Path.Combine(directory, "auto-3.json");
            var firstBefore = File.ReadAllText(firstFile);
            var thirdBefore = File.ReadAllText(thirdFile);
            var resumed = Read(store, 4);
            SetRestoreSource(store, 4, resumed.GameId);
            resumed.Round = resumed.Archive.Snapshot.State.Round = 4;
            var actionSave = Save(store, resumed);
            resumed.Round = resumed.Archive.Snapshot.State.Round = 5;
            var leaveSave = Save(store, resumed);
            Task.WhenAll(actionSave, leaveSave).GetAwaiter().GetResult();
            Assert.That(Read(store, 4).GameId, Is.EqualTo("second-match"));
            Assert.That(Read(store, 4).Round, Is.EqualTo(5));
            Assert.That(File.ReadAllText(firstFile), Is.EqualTo(firstBefore));
            Assert.That(File.ReadAllText(thirdFile), Is.EqualTo(thirdBefore));
            var secondBefore = File.ReadAllText(Path.Combine(directory, "auto-2.json"));
            Assert.Throws<IOException>(() => Save(store, Sample("fourth-match")).GetAwaiter().GetResult());
            Assert.That(File.ReadAllText(firstFile), Is.EqualTo(firstBefore));
            Assert.That(File.ReadAllText(Path.Combine(directory, "auto-2.json")), Is.EqualTo(secondBefore));
            Assert.That(File.ReadAllText(thirdFile), Is.EqualTo(thirdBefore));
        }
        [Test]
        public void NewMatch_FullAutomaticSlotsReplaceOldestSaveWhileResumeCannotEvictOtherMatches()
        {
            var store = Store(out var directory);
            var latest = Sample("latest-match");
            var oldest = Sample("oldest-match"); oldest.SavedUtcTicks = DateTime.UtcNow.AddDays(-2).Ticks;
            var middle = Sample("middle-match"); middle.SavedUtcTicks = DateTime.UtcNow.AddDays(-1).Ticks;
            Save(store, latest).GetAwaiter().GetResult();
            Save(store, oldest).GetAwaiter().GetResult();
            Save(store, middle).GetAwaiter().GetResult();
            var firstFile = Path.Combine(directory, "auto-1.json");
            var secondFile = Path.Combine(directory, "auto-2.json");
            var thirdFile = Path.Combine(directory, "auto-3.json");
            var firstBefore = File.ReadAllText(firstFile);
            var thirdBefore = File.ReadAllText(thirdFile);
            var newMatch = Sample("new-match");
            BeginMatch(store, newMatch.GameId, true);
            Save(store, newMatch).GetAwaiter().GetResult();
            Assert.That(Read(store, 4).GameId, Is.EqualTo(newMatch.GameId));
            Assert.That(File.ReadAllText(firstFile), Is.EqualTo(firstBefore));
            Assert.That(File.ReadAllText(thirdFile), Is.EqualTo(thirdBefore));
            var secondBefore = File.ReadAllText(secondFile);
            BeginMatch(store, oldest.GameId, false);
            Assert.Throws<IOException>(() => Save(store, oldest).GetAwaiter().GetResult());
            Assert.That(File.ReadAllText(firstFile), Is.EqualTo(firstBefore));
            Assert.That(File.ReadAllText(secondFile), Is.EqualTo(secondBefore));
            Assert.That(File.ReadAllText(thirdFile), Is.EqualTo(thirdBefore));
            var capacityError = Assert.Throws<TargetInvocationException>(() =>
                StoreType.GetMethod("EnsureResumeCapacity").Invoke(store, new object[] { oldest.GameId }));
            Assert.That(capacityError.InnerException, Is.TypeOf<IOException>());
            ((Task)StoreType.GetMethod("DeleteAsync").Invoke(store, new object[] { 5 })).GetAwaiter().GetResult();
            Assert.DoesNotThrow(() => StoreType.GetMethod("EnsureResumeCapacity").Invoke(store, new object[] { oldest.GameId }));
        }
        [Test]
        public void RestoreSource_UpdatesLoadedLegacyAutomaticSlotAndRemovesOnlySameMatchDuplicates()
        {
            var store = Store(out var directory); var data = Sample();
            Save(store, data).GetAwaiter().GetResult();
            var duplicateFile = Path.Combine(directory, "auto-1.json");
            var loadedFile = Path.Combine(directory, "auto-2.json");
            File.Copy(duplicateFile, loadedFile);
            Save(store, Sample("other-match")).GetAwaiter().GetResult();
            var otherFile = Path.Combine(directory, "auto-3.json");
            var otherBefore = File.ReadAllText(otherFile);
            var resumed = Read(store, 4);
            SetRestoreSource(store, 4, resumed.GameId);
            resumed.Round = resumed.Archive.Snapshot.State.Round = 7;
            Save(store, resumed).GetAwaiter().GetResult();
            Assert.That(Read(store, 4).Round, Is.EqualTo(7));
            Assert.That(File.Exists(duplicateFile), Is.False);
            Assert.That(File.ReadAllText(otherFile), Is.EqualTo(otherBefore));
            Assert.That(Directory.GetFiles(directory, "auto-*.json").Length, Is.EqualTo(2));
        }
        [Test]
        public void ManualSaves_StayInOwnSlotAndConsolidateLegacyDuplicatesAfterSaving()
        {
            var store = Store(out var directory); var data = Sample();
            Save(store, data, 1).GetAwaiter().GetResult();
            Save(store, Sample("other-match"), 2).GetAwaiter().GetResult();
            var ownFile = Path.Combine(directory, "manual-2.json");
            var otherFile = Path.Combine(directory, "manual-3.json");
            var ownBefore = File.ReadAllText(ownFile);
            var otherBefore = File.ReadAllText(otherFile);
            Assert.Throws<IOException>(() => Save(store, data, 0).GetAwaiter().GetResult());
            Assert.Throws<IOException>(() => Save(store, data, 2).GetAwaiter().GetResult());
            Assert.That(File.ReadAllText(ownFile), Is.EqualTo(ownBefore));
            Assert.That(File.ReadAllText(otherFile), Is.EqualTo(otherBefore));
            var duplicateFile = Path.Combine(directory, "manual-1.json");
            File.Copy(ownFile, duplicateFile);
            data.Round = data.Archive.Snapshot.State.Round = 8;
            Save(store, data, 1).GetAwaiter().GetResult();
            Assert.That(Read(store, 1).Round, Is.EqualTo(8));
            Assert.That(File.Exists(duplicateFile), Is.False);
            Assert.That(File.ReadAllText(otherFile), Is.EqualTo(otherBefore));
        }
        [Test]
        public void DuplicateCleanupFailure_DoesNotFailAutomaticOrManualSaveAndFlush()
        {
            foreach (int manualSlot in new[] { -1, 1 })
            {
                var store = Store(out var directory); var data = Sample();
                Save(store, data, manualSlot).GetAwaiter().GetResult();
                bool automatic = manualSlot < 0;
                string prefix = automatic ? "auto-" : "manual-";
                string lockedDuplicate = Path.Combine(directory, prefix + "1.json");
                string targetFile = Path.Combine(directory, prefix + "2.json");
                string otherDuplicate = Path.Combine(directory, prefix + "3.json");
                File.Copy(automatic ? lockedDuplicate : targetFile, automatic ? targetFile : lockedDuplicate);
                File.Copy(targetFile, otherDuplicate);
                string duplicateBefore = File.ReadAllText(lockedDuplicate);
                int targetSlot = automatic ? 4 : manualSlot;
                if (automatic) SetRestoreSource(store, targetSlot, data.GameId);
                data.Round = data.Archive.Snapshot.State.Round = 8;
                data.SavedUtcTicks = DateTime.UtcNow.Ticks;
                // 旧副本仍可供扫描读取，但占用句柄不允许删除。
                using (var locked = new FileStream(lockedDuplicate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Assert.DoesNotThrow(() => Save(store, data, manualSlot).GetAwaiter().GetResult());
                    Assert.DoesNotThrow(() => ((Task)StoreType.GetMethod("FlushAsync").Invoke(store, null)).GetAwaiter().GetResult());
                    Assert.That(Read(store, targetSlot).Round, Is.EqualTo(8));
                    Assert.That(File.ReadAllText(lockedDuplicate), Is.EqualTo(duplicateBefore));
                    Assert.That(File.Exists(otherDuplicate), Is.False);
                }
                // 下一次保存可以重试清理，不影响已经落盘的新档。
                Save(store, data, manualSlot).GetAwaiter().GetResult();
                Assert.That(File.Exists(lockedDuplicate), Is.False);
                Assert.That(Read(store, targetSlot).Round, Is.EqualTo(8));
            }
        }
        [Test]
        public void FailedAutomaticWrite_PreservesLoadedLegacySaveAndDuplicate()
        {
            var store = Store(out var directory); var data = Sample();
            Save(store, data).GetAwaiter().GetResult();
            var duplicateFile = Path.Combine(directory, "auto-1.json");
            var loadedFile = Path.Combine(directory, "auto-2.json");
            File.Copy(duplicateFile, loadedFile);
            var duplicateBefore = File.ReadAllText(duplicateFile);
            var loadedBefore = File.ReadAllText(loadedFile);
            SetRestoreSource(store, 4, data.GameId);
            data.Round = data.Archive.Snapshot.State.Round = 9;
            using (var locked = new FileStream(loadedFile + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => Save(store, data).GetAwaiter().GetResult());
            Assert.That(File.ReadAllText(duplicateFile), Is.EqualTo(duplicateBefore));
            Assert.That(File.ReadAllText(loadedFile), Is.EqualTo(loadedBefore));
            Assert.That(Directory.GetFiles(directory, "auto-*.json").Length, Is.EqualTo(2));
        }
        [Test]
        public void FailedRecovery_DoesNotModifyCurrentSession()
        {
            var state = new GameState { GameId = "live", Round = 9 };
            var session = new GameSession(state);
            var archive = HostRecoveryService.CreateArchive(new GameState { GameId = "wrong", Round = 2 });
            archive.Snapshot.StateRevision = 77;
            var result = session.RecoverHost(archive);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(session.State, Is.SameAs(state));
            Assert.That(state.GameId, Is.EqualTo("live")); Assert.That(state.Round, Is.EqualTo(9));
        }
        [Test]
        public void CommitNotification_RejectDoesNotNotifyAndObserverFailureDoesNotRejectCommit()
        {
            var session = new GameSession(new GameState());
            var handler = new Handler(); session.RegisterHandler(handler);
            int commits = 0; session.StateCommitted += () => throw new IOException("disk failed");
            session.StateCommitted += () => commits++;
            Assert.That(session.Submit(new GameCommand()).Succeeded, Is.False);
            Assert.That(commits, Is.Zero);
            handler.Accept = true;
            Assert.That(session.Submit(new GameCommand()).Succeeded, Is.True);
            Assert.That(commits, Is.EqualTo(1)); Assert.That(session.State.Round, Is.EqualTo(1));
        }
        private sealed class Handler : IGameCommandHandler
        {
            public bool Accept;
            public bool CanHandle(GameCommand command) => true;
            public CommandResult Handle(GameState state, GameCommand command)
            {
                state.Round++;
                return Accept ? CommandResult.SuccessResult(null, "committed") : CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.WrongPhase, "rejected"));
            }
        }
        [Test]
        public void GameOverArchive_RestoresResolvedResultsWithoutSettlement()
        {
            var data = Sample(); var state = data.Archive.Snapshot.State;
            state.Phase = GamePhase.GameOver;
            state.FinalScoring = new FinalScoringState { IsResolved = true, WinnerPlayerIds = new List<int> { 1 } };
            var store = Store(out _); Save(store, data, 0).GetAwaiter().GetResult();
            var session = new GameSession(new GameState());
            Assert.That(session.RecoverHost(Read(store, 0).Archive).Succeeded, Is.True);
            Assert.That(session.State.Phase, Is.EqualTo(GamePhase.GameOver));
            Assert.That(session.State.FinalScoring.IsResolved, Is.True);
            Assert.That(session.State.FindPlayer(1).Resources.Iron, Is.EqualTo(17));
        }
        [TestCase(3)] [TestCase(4)]
        public void ResumeSeats_ReconnectByIdentityAndSubstituteCannotBeReclaimed(int count)
        {
            var options = new ResumeRoomOptions { MapId = "map", GameId = "saved", Round = 6 };
            for (int i = 1; i <= count; i++) options.Seats.Add(new MatchSaveSeat { PlayerId = i, SteamId = (ulong)i, OperatorId = "identity-" + i, PlayerName = "Player " + i });
            using (var service = new NetworkRoomService(false))
            {
                service.CreateRoom("Host", count, options, "identity-1");
                service.SetTransportReadiness(1, true, true, 0);
                for (int i = count; i >= 3; i--)
                {
                    var c = Connection("identity-" + i);
                    Assert.That(service.AssignSeat("Player " + i, c), Is.EqualTo(i));
                    service.SetTransportReadiness(i, true, true, (ulong)i);
                }
                var substitute = Connection("replacement");
                Assert.That(service.AssignSeat("替补", substitute), Is.Zero);
                Assert.That(service.TryResolveIdentityTicket(substitute.IdentityTicket, out _), Is.False);
                Assert.That(service.TryStartGame(out _), Is.False);
                service.AssignResumeSeat(substitute.MemberId, 2);
                Assert.That(service.TryResolveIdentityTicket(substitute.IdentityTicket, out var id), Is.True);
                Assert.That(id, Is.EqualTo(2));
                var original = Connection("identity-2");
                Assert.That(service.AssignSeat("原玩家", original), Is.Zero);
                Assert.That(service.GetCurrentRoom().Seats[1].OperatorId, Is.EqualTo("replacement"));
                Assert.Throws<InvalidOperationException>(() => service.AssignResumeSeat(original.MemberId, 1));
                service.SetTransportReadiness(2, true, true, 2);
                Assert.That(service.TryStartGame(out _), Is.True);
                var roundtrip = NetworkRoomService.DeserializeRoom(NetworkRoomService.SerializeRoom(service.GetCurrentRoom()));
                Assert.That(roundtrip.Resume.GameId, Is.EqualTo("saved"));
                Assert.That(roundtrip.Seats[1].OperatorId, Is.EqualTo("replacement"));
                Assert.That(roundtrip.WaitingMembers.Count, Is.EqualTo(1));
            }
        }
        [TestCase(3)] [TestCase(4)]
        public void ResumeSeats_LoopbackTcpPublishesAssignmentAndFreshTicket(int count)
        {
            var options = new ResumeRoomOptions { MapId = "map", GameId = "tcp-saved", Round = 6 };
            for (int i = 1; i <= count; i++) options.Seats.Add(new MatchSaveSeat {
                PlayerId = i, SteamId = (ulong)i, OperatorId = "tcp-" + i, PlayerName = "Player " + i });
            var clients = new List<NetworkRoomService>();
            using (var host = new NetworkRoomService())
            {
                try
                {
                    var room = host.CreateRoom("Host", count, options, "tcp-1");
                    var endpoint = "127.0.0.1:" + room.RoomId.Substring(room.RoomId.LastIndexOf(':') + 1);
                    for (int i = count; i >= 3; i--)
                    {
                        int seat = i;
                        var client = new NetworkRoomService(); clients.Add(client);
                        client.JoinRoom(endpoint, "Player " + seat, "tcp-" + seat);
                        Assert.That(System.Threading.SpinWait.SpinUntil(() => client.GetCurrentRoom()?.LocalPlayerId == seat, 5000), Is.True);
                    }
                    var replacement = new NetworkRoomService(); clients.Add(replacement);
                    replacement.JoinRoom(endpoint, "Replacement", "tcp-replacement");
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => replacement.GetCurrentRoom()?.LocalPlayerId == 0 && host.GetCurrentRoom().WaitingMembers.Count == 1, 5000), Is.True);
                    var waitingTicket = replacement.LocalIdentityTicket;
                    Assert.That(host.TryResolveIdentityTicket(waitingTicket, out _), Is.False);
                    host.AssignResumeSeat(host.GetCurrentRoom().WaitingMembers[0].MemberId, 2);
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => replacement.GetCurrentRoom()?.LocalPlayerId == 2 && replacement.GetCurrentRoom().Seats.Count == count && replacement.GetCurrentRoom().Seats[1].OperatorId == "tcp-replacement", 5000), Is.True);
                    Assert.That(replacement.LocalIdentityTicket, Is.Not.EqualTo(waitingTicket));
                    Assert.That(host.TryResolveIdentityTicket(replacement.LocalIdentityTicket, out var assigned), Is.True);
                    Assert.That(assigned, Is.EqualTo(2));
                    var original = new NetworkRoomService(); clients.Add(original);
                    original.JoinRoom(endpoint, "Original", "tcp-2");
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => original.GetCurrentRoom()?.LocalPlayerId == 0 && original.GetCurrentRoom().Resume != null, 5000), Is.True);
                    Assert.That(host.GetCurrentRoom().Seats[1].OperatorId, Is.EqualTo("tcp-replacement"));
                    Assert.That(host.TryStartGame(out _), Is.False, "TCP membership alone must not bypass transport identity/readiness.");
                    Assert.That(original.GetCurrentRoom().Resume.GameId, Is.EqualTo("tcp-saved"));
                }
                finally { foreach (var client in clients) client.Dispose(); }
            }
        }

        private static NetworkRoomService.ClientConnection Connection(string identity)
            => new NetworkRoomService.ClientConnection(new StringReader(""), new StringWriter()) { OperatorId = identity };

    }
}
