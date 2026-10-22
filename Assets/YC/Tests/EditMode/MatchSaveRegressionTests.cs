using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json;
using YC.Domain.Maps;
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
        [TestCase(null)]
        [TestCase("此前的实际写盘错误")]
        public void HostAutomaticSaveWaitsForCheckpointWithoutLosingDirtyStateOrPreviousFailure(string previousFailure)
        {
            // 隔离保存生命周期；正常四实例验收另行验证确认完成后的实际写盘。
            var root = new GameObject("等待房主确认保存点"); root.SetActive(false);
            var type = Type.GetType("YC.Presentation.MatchSaveController, Assembly-CSharp", true);
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var controller = root.AddComponent(type);
            var context = root.AddComponent(contextType);
            void Set(string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, value);
            object Get(string name) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
            try
            {
                contextType.GetField("Mode").SetValue(context, LaunchMode.Host);
                contextType.GetField("GameId").SetValue(context, "waiting-save-" + Guid.NewGuid().ToString("N"));
                Set("session", new GameSession(new GameState())); Set("context", context);
                Set("dirty", 1); Set("lastFailure", previousFailure);
                type.GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, null);
                Assert.That(Get("dirty"), Is.EqualTo(1));
                Assert.That(Get("saved"), Is.EqualTo(0));
                Assert.That(Get("saving"), Is.False);
                Assert.That(type.GetProperty("LastFailure").GetValue(controller), Is.EqualTo(previousFailure));
                var manualSave = (Task<bool>)type.GetMethod("Save").Invoke(controller, new object[] { -1 });
                Assert.That(manualSave.GetAwaiter().GetResult(), Is.False);
                Assert.That(type.GetProperty("LastFailure").GetValue(controller), Is.EqualTo("联机正式确认保存点尚未就绪。"));
            }
            finally
            {
                type.GetMethod("Unbind").Invoke(controller, null);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static MatchSaveData Sample(string gameId = "save-regression")
        {
            var state = new GameState { GameId = gameId, MapId = StaticMapDefinitions.ThreePlayerMapId,
                CurrentPlayerId = 1, Round = 2, ContentPackHash = "test-pack",
                RuleConfiguration = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId,
                    PlayerCount = 1, ContentPackHash = "test-pack" },
                RandomRuntime = GameplayRandomRuntime.Create(gameId), LaunchPlayerOrder = new List<int> { 1 } };
            state.Players.Add(new PlayerState { PlayerId = 1, Color = PlayerColor.Blue, HandCardIds = new List<string> { "private-card" } });
            state.Players[0].Resources.Iron = 17;
            state.Decks.CharacterDeck.AddRange(new[] { "third", "first", "second" });
            return new MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, LocalPlayerId = 1, Mode = LaunchMode.Local,
                GameId = state.GameId, MapId = state.MapId, Round = state.Round, ContentHash = "test-pack",
                ArchiveJson = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state, "test-pack")),
                ContinuationJson = SimulationConfirmedRecoveryInstaller.SerializeContinuation(new GameSessionLogContinuationDto()),
                Seats = new List<MatchSaveSeat> { new MatchSaveSeat { PlayerId = 1, PlayerName = "热座", Color = PlayerColor.Blue } } };
        }
        private static HostSessionArchiveDto ReadArchive(MatchSaveData data) =>
            JsonConvert.DeserializeObject<HostSessionArchiveDto>(data.ArchiveJson,
                (JsonSerializerSettings)typeof(HostRecoveryService).GetField("ArchiveSettings", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
        private static void UpdateArchive(MatchSaveData data, Action<HostSessionArchiveDto> change)
        {
            var archive = ReadArchive(data);
            change(archive);
            data.ArchiveJson = HostRecoveryService.SerializeArchive(archive);
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
            var store = Store(out _); var data = Sample(); var state = ReadArchive(data).Snapshot.State;
            state.PendingChoice = new PendingChoiceState { ChoiceId = "choice", PlayerId = 1, ChoiceType = "test", OptionIds = new List<string> { "a", "b" } };
            var value = NormalizedValue.CreateString("deep-private-leaf");
            for (int i = 0; i < 6; i++) value = NormalizedValue.CreateArray(new[] { value });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "nested", Payload = value });
            UpdateArchive(data, archive => archive.Snapshot.State = state);
            Save(store, data, 0).GetAwaiter().GetResult();
            var restored = ReadArchive(Read(store, 0)).Snapshot.State;
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
                UpdateArchive(data, archive => archive.Snapshot.State.Round = data.Round = i);
                tasks.Add(Save(store, data));
                if (i == 3) tasks.Add(Save(store, data, 1));
            }
            // 调用返回后修改摘要和嵌套字段，不能污染已排队的不可变快照。
            UpdateArchive(data, archive => archive.Snapshot.State.GameId = data.GameId = "later-match");
            UpdateArchive(data, archive => archive.Snapshot.ContentHash = data.ContentHash = "later-pack");
            data.Seats[0].PlayerName = "后续修改";
            UpdateArchive(data, archive => archive.Snapshot.State.Decks.CharacterDeck.Clear());
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
                Assert.That(ReadArchive(saved).Snapshot.State.Decks.CharacterDeck, Is.EqualTo(new[] { "third", "first", "second" }));
            }
            Assert.That(Directory.GetFiles(directory, "auto-*.json").Length, Is.EqualTo(1));
        }
        [Test]
        public void OverwriteKeepsLatestWithoutBackup_CorruptSaveFailsAndFailedWritePreservesOldFile()
        {
            var store = Store(out var directory); var data = Sample();
            Save(store, data, 0).GetAwaiter().GetResult();
            UpdateArchive(data, archive => archive.Snapshot.State.Round = data.Round = 8);
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
            UpdateArchive(data, archive => {
                archive.Snapshot.ContentHash = data.ContentHash = "different-pack";
                archive.Snapshot.State.ContentPackHash = archive.Snapshot.State.RuleConfiguration.ContentPackHash = data.ContentHash;
            });
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
            UpdateArchive(resumed, archive => archive.Snapshot.State.Round = resumed.Round = 4);
            var actionSave = Save(store, resumed);
            UpdateArchive(resumed, archive => archive.Snapshot.State.Round = resumed.Round = 5);
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
            UpdateArchive(resumed, archive => archive.Snapshot.State.Round = resumed.Round = 7);
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
            UpdateArchive(data, archive => archive.Snapshot.State.Round = data.Round = 8);
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
                UpdateArchive(data, archive => archive.Snapshot.State.Round = data.Round = 8);
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
            UpdateArchive(data, archive => archive.Snapshot.State.Round = data.Round = 9);
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
            var data = Sample(); var state = ReadArchive(data).Snapshot.State;
            state.Phase = GamePhase.GameOver;
            state.FinalScoring = new FinalScoringState { IsResolved = true, WinnerPlayerIds = new List<int> { 1 } };
            UpdateArchive(data, archive => archive.Snapshot.State = state);
            var store = Store(out _); Save(store, data, 0).GetAwaiter().GetResult();
            var session = new GameSession(new GameState());
            Assert.That(session.RecoverHost(ReadArchive(Read(store, 0))).Succeeded, Is.True);
            Assert.That(session.State.Phase, Is.EqualTo(GamePhase.GameOver));
            Assert.That(session.State.FinalScoring.IsResolved, Is.True);
            Assert.That(session.State.FindPlayer(1).Resources.Iron, Is.EqualTo(17));
        }
        [TestCase(3)] [TestCase(4)]
        public void ResumeSeats_ReconnectByIdentityAndSubstituteCannotBeReclaimed(int count)
        {
            var options = ResumeOptions(count, "saved");
            for (int i = 1; i <= count; i++) options.Seats.Add(new MatchSaveSeat { PlayerId = i, SteamId = LocalMirrorIdentity.ForPlayer(i), OperatorId = "identity-" + i, PlayerName = "Player " + i, Color = RoomConfigurationService.GetSeatColor(i - 1) });
            using (var service = new NetworkRoomService(false, new Capabilities()))
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
                service.AssignResumeSeat(substitute.OperatorId, 2);
                Assert.That(service.TryResolveIdentityTicket(substitute.IdentityTicket, out var id), Is.True);
                Assert.That(id, Is.EqualTo(2));
                var original = Connection("identity-2");
                Assert.That(service.AssignSeat("原玩家", original), Is.Zero);
                Assert.That(service.GetCurrentRoom().Seats[1].OperatorId, Is.EqualTo("replacement"));
                Assert.Throws<InvalidOperationException>(() => service.AssignResumeSeat(original.OperatorId, 1));
                service.SetTransportReadiness(2, true, true, 2);
                var room = service.GetCurrentRoom();
                foreach (var seat in room.Seats.Where(seat => seat.PlayerId != room.HostPlayerId))
                {
                    var request = new SetRoomReadyRequest { CommandId = Guid.NewGuid().ToString("N"),
                        RoomId = room.RoomId, RequestingPlayerId = seat.PlayerId,
                        ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision, IsUserReady = true };
                    var ready = service.ExecuteAuthenticatedConfiguration(
                        RoomConfigurationMessage.Create(request, new Capabilities(), room.Configuration), seat.PlayerId);
                    Assert.That(ready.Success, Is.True, ready.ReasonCode);
                }
                Assert.That(service.TryStartGame(out var startError), Is.True, startError);
                var roundtrip = NetworkRoomService.DeserializeRoom(NetworkRoomService.SerializeRoom(service.GetCurrentRoom()));
                Assert.That(roundtrip.Resume.GameId, Is.EqualTo("saved"));
                Assert.That(roundtrip.Seats[1].OperatorId, Is.EqualTo("replacement"));
                Assert.That(roundtrip.WaitingMembers.Count, Is.EqualTo(1));
            }
        }
        [TestCase(3)] [TestCase(4)]
        public void ResumeSeats_LoopbackTcpPublishesAssignmentAndFreshTicket(int count)
        {
            var options = ResumeOptions(count, "tcp-saved");
            for (int i = 1; i <= count; i++) options.Seats.Add(new MatchSaveSeat {
                PlayerId = i, SteamId = LocalMirrorIdentity.ForPlayer(i), OperatorId = "tcp-" + i, PlayerName = "Player " + i, Color = RoomConfigurationService.GetSeatColor(i - 1) });
            var clients = new List<NetworkRoomService>();
            using (var host = new NetworkRoomService(new Capabilities()))
            {
                try
                {
                    var room = host.CreateRoom("Host", count, options, "tcp-1");
                    var endpoint = "127.0.0.1:" + room.RoomId.Substring(room.RoomId.LastIndexOf(':') + 1);
                    for (int i = count; i >= 3; i--)
                    {
                        int seat = i;
                        var client = new NetworkRoomService(new Capabilities()); clients.Add(client);
                        client.JoinRoom(endpoint, "Player " + seat, "tcp-" + seat);
                        Assert.That(System.Threading.SpinWait.SpinUntil(() => client.GetCurrentRoom()?.LocalPlayerId == seat, 5000), Is.True);
                    }
                    var replacement = new NetworkRoomService(new Capabilities()); clients.Add(replacement);
                    replacement.JoinRoom(endpoint, "Replacement", "tcp-replacement");
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => replacement.GetCurrentRoom()?.LocalPlayerId == 0 && host.GetCurrentRoom().WaitingMembers.Count == 1, 5000), Is.True);
                    var waitingTicket = replacement.LocalIdentityTicket;
                    Assert.That(host.TryResolveIdentityTicket(waitingTicket, out _), Is.False);
                    host.AssignResumeSeat(host.GetCurrentRoom().WaitingMembers[0].MemberId, 2);
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => replacement.GetCurrentRoom()?.LocalPlayerId == 2 && replacement.GetCurrentRoom().Seats.Count == count && replacement.GetCurrentRoom().Seats[1].OperatorId == "tcp-replacement", 5000), Is.True);
                    Assert.That(replacement.LocalIdentityTicket, Is.Not.EqualTo(waitingTicket));
                    Assert.That(host.TryResolveIdentityTicket(replacement.LocalIdentityTicket, out var assigned), Is.True);
                    Assert.That(assigned, Is.EqualTo(2));
                    var original = new NetworkRoomService(new Capabilities()); clients.Add(original);
                    original.JoinRoom(endpoint, "Original", "tcp-2");
                    Assert.That(System.Threading.SpinWait.SpinUntil(() => original.GetCurrentRoom()?.LocalPlayerId == 0 && original.GetCurrentRoom().Resume != null, 5000), Is.True);
                    Assert.That(host.GetCurrentRoom().Seats[1].OperatorId, Is.EqualTo("tcp-replacement"));
                    Assert.That(host.TryStartGame(out _), Is.False, "TCP membership alone must not bypass transport identity/readiness.");
                    Assert.That(original.GetCurrentRoom().Resume.GameId, Is.EqualTo("tcp-saved"));
                }
                finally { foreach (var client in clients) client.Dispose(); }
            }
        }

        private static ResumeRoomOptions ResumeOptions(int count, string gameId)
        {
            var map = count == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId;
            return new ResumeRoomOptions { GameId = gameId, MapId = map, Round = 6, HostPlayerId = 1,
                ContentHash = "test-pack", Configuration = new GameRuleConfiguration { MapId = map, PlayerCount = count, ContentPackHash = "test-pack" },
                RandomRuntime = GameplayRandomRuntime.Create(gameId), LaunchPlayerOrder = Enumerable.Range(1, count).ToList(),
                SimulationEpoch = 2, MembershipVersion = 1, CommandPrefixDigest = OperationCommandCanonicalizer.EmptyPrefixDigest,
                CheckpointId = "resume-regression", MetadataDigest = "resume-regression" };
        }
        private sealed class Capabilities : IRoomContentCapabilityProvider
        {
            public string GetContentPackHash(GameRuleConfiguration configuration) => "test-pack";
            public bool TryValidate(GameRuleConfiguration configuration, out string reason) { reason = ""; return true; }
        }

        private static NetworkRoomService.ClientConnection Connection(string identity)
            => new NetworkRoomService.ClientConnection(new StringReader(""), new StringWriter()) {
                OperatorId = identity, ContentPackHash = "test-pack", RuleContractVersion = GameRuleConfiguration.CurrentRuleContractVersion };

    }
}
