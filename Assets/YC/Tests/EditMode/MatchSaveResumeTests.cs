using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    // 隔离存读档协议验收，不替代可见独立程序的正常流程验收。
    public sealed class MatchSaveResumeTests
    {
        [Test]
        public void ImportedSaveWindowAndEntryButtonsHaveValidPrefabReferences()
        {
            foreach (var path in new[] { "Assets/YC/Presentation/Resources/MatchSaveWindow.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/GenericWindow.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/MatchSaveDeleteConfirmation.prefab" })
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(path);
                Assert.That(prefab, Is.Not.Null, path);
                foreach (var transform in prefab.GetComponentsInChildren<UnityEngine.Transform>(true))
                    Assert.That(transform.GetComponents<UnityEngine.Component>().All(c => c != null), Is.True, path + ":" + transform.name);
            }
            CheckButton("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab", "StartMenuRoomPanelView", "resumeSeatsButton");
            CheckButton("Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameSettingsMenu.prefab", "GameSettingsMenuView", "saveGameButton");
        }
        private static void CheckButton(string path, string name, string field)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(path);
            var type = Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
            var component = prefab.GetComponentInChildren(type, true);
            Assert.That(type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(component), Is.Not.Null, path + ":" + field);
        }
        public static MatchSaveData FromSession(GameSession session, LaunchMode mode, int playerId)
        {
            var point = session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture());
            return FromArchive(point.Archive, point.Continuation, mode, playerId);
        }
        public static MatchSaveData FromCheckpoint(SimulationRecoveryCheckpointDto checkpoint, int playerId)
        {
            var data = FromArchive(checkpoint.Archive, checkpoint.Continuation, LaunchMode.Host, playerId);
            data.SimulationCheckpointJson = SimulationConfirmedRecoveryInstaller.SerializeCheckpoint(checkpoint);
            data.Validate(); return data;
        }
        private static MatchSaveData FromArchive(HostSessionArchiveDto archive, GameSessionLogContinuationDto continuation, LaunchMode mode, int local)
        {
            var state = archive.Snapshot.State;
            return new MatchSaveData { SavedUtcTicks = DateTime.UtcNow.Ticks, Mode = mode, LocalPlayerId = local,
                HostSteamId = (ulong)(100 + local), MapId = state.MapId, GameId = state.GameId, Round = state.Round,
                ContentHash = state.ContentPackHash, ArchiveJson = HostRecoveryService.SerializeArchive(archive),
                ContinuationJson = SimulationConfirmedRecoveryInstaller.SerializeContinuation(continuation),
                Seats = state.Players.Select(p => new MatchSaveSeat { PlayerId = p.PlayerId, Color = p.Color,
                    PlayerName = p.Name, SteamId = (ulong)(100 + p.PlayerId), OperatorId = "operator-" + p.PlayerId }).ToList() };
        }
        public static GameSession Restore(MatchSaveData data)
        {
            var type = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var result = type.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { null, data });
            return (GameSession)result.GetType().GetField("Session").GetValue(result);
        }
        private static MatchSaveData Save(int localPlayer = 3)
        {
            var sessions = DeterministicSimulationCoordinatorTests.CreateFormalSessions(out _, true);
            var host = new DeterministicSimulationCoordinator(sessions[0],
                DeterministicSimulationSessionOptions.CreateInitial(true, 3, sessions[0], new[] { 1, 2, 3 }));
            host.Begin();
            Assert.That(host.TryCreateSaveCheckpoint(out var checkpoint, out var result), Is.True, result.Reason);
            return FromCheckpoint(checkpoint, localPlayer);
        }
        [Test]
        public void LocalResumeReturnsOriginalSeatAndAssignsSubstituteOnlyToAbsentSeat()
        {
            var save = Save(1);
            foreach (var seat in save.Seats) seat.SteamId = LocalMirrorIdentity.ForPlayer(seat.PlayerId);
            save.HostSteamId = LocalMirrorIdentity.ForPlayer(1); save.LocalTestNetwork = true;
            using var service = new NetworkRoomService(false, new Capabilities(save.ContentHash));
            service.CreateRoom("房主", 3, ResumeRoomOptions.FromSave(save), "operator-1");
            var original = new NetworkRoomService.ClientConnection(new StringReader(""), new StringWriter())
            { OperatorId = "operator-2", PlayerName = "原玩家" };
            Assert.That(service.AssignSeat(original.PlayerName, original), Is.EqualTo(2));
            var substitute = new NetworkRoomService.ClientConnection(new StringReader(""), new StringWriter())
            { OperatorId = "substitute", PlayerName = "替补" };
            Assert.That(service.AssignSeat(substitute.PlayerName, substitute), Is.Zero);
            Assert.Throws<InvalidOperationException>(() => service.AssignResumeSeat("substitute", 2));
            var duplicate = new NetworkRoomService.ClientConnection(new StringReader(""), new StringWriter())
            { OperatorId = "substitute" };
            Assert.That(service.AssignSeat("重复", duplicate), Is.EqualTo(-1));
            service.RemoveClientConnection(duplicate);
            Assert.That(service.GetCurrentRoom().WaitingMembers.Count, Is.EqualTo(1));
            service.RemoveClientConnection(original);
            Assert.That(service.GetCurrentRoom().Seats.Single(s => s.PlayerId == 2).LobbyMemberPresent, Is.False);
            service.AssignResumeSeat("substitute", 2);
            Assert.That(substitute.PlayerId, Is.EqualTo(2));
            Assert.That(service.GetCurrentRoom().Seats.Single(s => s.PlayerId == 2).OperatorId, Is.EqualTo("substitute"));
            Assert.That(service.GetCurrentRoom().WaitingMembers, Is.Empty);
            Assert.That(service.TryResolveIdentityTicket(substitute.IdentityTicket, out int playerId), Is.True);
            Assert.That(playerId, Is.EqualTo(2));
        }
        [Test]
        public void ResumeRoomLocksConfigurationAndPreservesStartFactsAndPrivateBoundary()
        {
            var save = Save(); var resume = ResumeRoomOptions.FromSave(save);
            var room = new RoomState { RoomId = "saved-room" };
            var caps = new Capabilities(save.ContentHash);
            resume.ApplyTo(room, new PlayerSeat { PlayerId = 3, SteamId = 103, PlayerName = "房主", LobbyMemberPresent = true,
                TransportConnected = true, IdentityVerified = true, IsReady = true }, caps);
            var policy = new RoomConfigurationService(caps);
            Assert.That(policy.TryValidateLaunch(room, out _), Is.False);
            var change = policy.ChangeConfiguration(room, new ChangeRoomConfigurationRequest { CommandId = "change", RoomId = room.RoomId,
                RequestingPlayerId = 3, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision, MapId = room.Configuration.MapId });
            Assert.That(change.Success, Is.False); Assert.That(change.ReasonCode, Is.EqualTo("resume_configuration_locked"));
            foreach (var slot in room.SeatSlots.Where(s => s.Occupant != null))
            { slot.Occupant.LobbyMemberPresent = slot.Occupant.TransportConnected = slot.Occupant.IdentityVerified = slot.Occupant.IsReady = true;
                slot.Occupant.NetworkClientId = slot.Occupant.PlayerId == room.HostPlayerId ? 0 : (ulong)slot.Occupant.PlayerId;
                slot.IsUserReady = true; slot.ReadyConfigurationRevision = room.Configuration.ConfigurationRevision; }
            Assert.That(policy.TryFreezeForLaunch(room, new StartConfiguredGameRequest { CommandId = "resume-start", RoomId = room.RoomId, RequestingPlayerId = 3, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision }, out _, out var reason), Is.True, reason);
            Assert.That(room.GameId, Is.EqualTo(save.GameId));
            Assert.That(room.LaunchPlayerOrder, Is.EqualTo(save.LaunchPlayerOrder));
            Assert.That(JsonConvert.SerializeObject(room.RandomRuntime), Is.EqualTo(JsonConvert.SerializeObject(save.RandomState)));
            var json = RoomStateSerializer.Serialize(room);
            Assert.That(json, Does.Not.Contain("ArchiveJson")); Assert.That(json, Does.Not.Contain("ContinuationJson"));
            Assert.That(json, Does.Not.Contain("SimulationCheckpointJson"));
            Assert.That(RoomStateSerializer.TryDeserialize(json, out _, out _), Is.True);
            var broken = JObject.Parse(json); broken.Remove("Resume");
            Assert.That(RoomStateSerializer.TryDeserialize(broken.ToString(), out _, out _), Is.False);
        }
        [Test]
        public void OldOrMixedSaveIsRejectedBeforeRestoringState()
        {
            var save = Save(); save.Version = 1; Assert.Throws<InvalidOperationException>(save.Validate);
            save.Version = 2; save.Seats[0].Color = save.Seats[1].Color; Assert.Throws<InvalidOperationException>(save.Validate);
            save = Save(); var json = JObject.Parse(save.ContinuationJson); json["Message"] = "混合保存点";
            save.ContinuationJson = json.ToString(); Assert.Throws<InvalidOperationException>(save.Validate);
        }
        [Test]
        public void StoreRoundTripsFullCheckpointAndDetectsCorruption()
        {
            var save = Save(); var path = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "GitMergeValidation", "SaveResume-20261016", "store-tests", Guid.NewGuid().ToString("N"));
            var type = Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
            var store = Activator.CreateInstance(type, path);
            ((Task)type.GetMethod("SaveAsync").Invoke(store, new object[] { save, 0 })).GetAwaiter().GetResult();
            var actual = (MatchSaveData)type.GetMethod("Read").Invoke(store, new object[] { 0 });
            Assert.That(actual.SimulationCheckpointJson, Is.EqualTo(save.SimulationCheckpointJson));
            Assert.That(RuleStateDigestService.Compute(Restore(actual).State), Is.EqualTo(RuleStateDigestService.Compute(Restore(save).State)));
            var file = Directory.GetFiles(path, "*.json").Single();
            var text = File.ReadAllText(file); File.WriteAllText(file, text.Replace(save.GameId, "invalid-match"));
            Assert.Throws<TargetInvocationException>(() => type.GetMethod("Read").Invoke(store, new object[] { 0 }));
            // 产品 Delete API 仅清理本夹具明确创建的槽位。
            ((Task)type.GetMethod("DeleteAsync").Invoke(store, new object[] { 0 })).GetAwaiter().GetResult();
        }
        private sealed class Capabilities : IRoomContentCapabilityProvider
        {
            private readonly string hash;
            public Capabilities(string value) { hash = value; }
            public string GetContentPackHash(GameRuleConfiguration configuration) => hash;
            public bool TryValidate(GameRuleConfiguration configuration, out string reason) { reason = ""; return true; }
        }
    }
}
