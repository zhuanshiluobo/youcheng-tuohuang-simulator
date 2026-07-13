using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class SteamMultiplayerCoreTests
    {
        [TestCase("76561198000000000", 76561198000000000UL)]
        [TestCase(" 480 ", 480UL)]
        public void TryParseLobbyId_AcceptsPositiveUnsignedIds(string input, ulong expected)
        {
            Assert.IsTrue(SteamLobbyPolicy.TryParseLobbyId(input, out var actual));
            Assert.AreEqual(expected, actual);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0")]
        [TestCase("not-a-lobby")]
        public void TryParseLobbyId_RejectsInvalidIds(string input)
        {
            Assert.IsFalse(SteamLobbyPolicy.TryParseLobbyId(input, out _));
        }

        [TestCase("76561198000000000", 76561198000000000UL)]
        [TestCase("steam://76561198000000001", 76561198000000001UL)]
        [TestCase("steam:76561198000000002", 76561198000000002UL)]
        public void SteamIdentityAddress_ParsesTransportAddresses(string address, ulong expected)
        {
            Assert.IsTrue(SteamIdentityAddress.TryParse(address, out var actual));
            Assert.AreEqual(expected, actual);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("steam://not-a-steamid")]
        [TestCase("steam://0")]
        public void SteamIdentityAddress_RejectsInvalidTransportAddresses(string address)
        {
            Assert.IsFalse(SteamIdentityAddress.TryParse(address, out _));
        }

        [Test]
        public void IsCompatible_RequiresApp480NamespaceProtocolWaitingRoomAndHost()
        {
            var data = CompatibleData();
            Assert.IsTrue(SteamLobbyPolicy.IsCompatible(data, 4, out var reason), reason);

            data["gameKey"] = "another_game_using_480";
            Assert.IsFalse(SteamLobbyPolicy.IsCompatible(data, 4, out _));
            data = CompatibleData();
            data["protocolVersion"] = SteamLobbyPolicy.ProtocolVersion + "-mismatch";
            Assert.IsFalse(SteamLobbyPolicy.IsCompatible(data, 4, out _));
            data = CompatibleData();
            data["roomStatus"] = SteamLobbyPolicy.StartedStatus;
            Assert.IsFalse(SteamLobbyPolicy.IsCompatible(data, 4, out _));
            data = CompatibleData();
            data["hostSteamId"] = "0";
            Assert.IsFalse(SteamLobbyPolicy.IsCompatible(data, 4, out _));
        }

        [Test]
        public void BindingRegistry_BindsSteamIdentityToStableSeatAndRejectsImpersonation()
        {
            var registry = new SteamIdentityBindingRegistry();
            registry.ReplaceLobbySeats(new[]
            {
                new KeyValuePair<int, ulong>(1, 111UL),
                new KeyValuePair<int, ulong>(2, 222UL),
                new KeyValuePair<int, ulong>(3, 333UL)
            });

            Assert.IsTrue(registry.TryBindConnection(7, 222UL, out var playerId));
            Assert.AreEqual(2, playerId);
            Assert.IsTrue(registry.IsCommandOwner(7, 2));
            Assert.IsFalse(registry.IsCommandOwner(7, 1));
            Assert.IsFalse(registry.TryBindConnection(8, 999UL, out _));
            Assert.IsFalse(registry.TryBindConnection(8, 222UL, out _));
        }

        [Test]
        public void BindingRegistry_LobbyMemberEnumerationOrderDoesNotChangeSeats()
        {
            var registry = new SteamIdentityBindingRegistry();
            registry.ReplaceLobbySeats(new[]
            {
                new KeyValuePair<int, ulong>(3, 333UL),
                new KeyValuePair<int, ulong>(1, 111UL),
                new KeyValuePair<int, ulong>(2, 222UL)
            });

            Assert.IsTrue(registry.TryBindConnection(10, 111UL, out var host));
            Assert.IsTrue(registry.TryBindConnection(11, 333UL, out var third));
            Assert.AreEqual(1, host);
            Assert.AreEqual(3, third);
        }

        [Test]
        public void LocalMirrorIdentity_AssignsStableDistinctSimulatedIds()
        {
            Assert.AreEqual(90000000000000001UL, LocalMirrorIdentity.ForPlayer(1));
            Assert.AreEqual(90000000000000004UL, LocalMirrorIdentity.ForPlayer(4));
            Assert.AreNotEqual(LocalMirrorIdentity.ForPlayer(1), LocalMirrorIdentity.ForPlayer(2));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => LocalMirrorIdentity.ForPlayer(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => LocalMirrorIdentity.ForPlayer(5));
        }

        [Test]
        public void SeatAssignment_IsIndependentOfLobbyEnumerationOrderAndPinsHostToOne()
        {
            var forward = SteamSeatAssignment.Assign(300UL, 4, new[] { 300UL, 100UL, 400UL, 200UL }, null);
            var reverse = SteamSeatAssignment.Assign(300UL, 4, new[] { 200UL, 400UL, 100UL, 300UL }, null);

            CollectionAssert.AreEqual(new[] { 300UL, 100UL, 200UL, 400UL }, Values(forward));
            CollectionAssert.AreEqual(Values(forward), Values(reverse));
        }

        [Test]
        public void SeatAssignment_PreservesExistingValidSeatsWhenNewMemberJoins()
        {
            var existing = new Dictionary<int, ulong>
            {
                [1] = 999UL,
                [2] = 400UL,
                [3] = 200UL,
                [4] = 0UL
            };
            var assigned = SteamSeatAssignment.Assign(300UL, 4, new[] { 100UL, 200UL, 300UL, 400UL }, existing);

            CollectionAssert.AreEqual(new[] { 300UL, 400UL, 200UL, 100UL }, Values(assigned));
        }

        [Test]
        public void StartMenu_DoesNotPermanentlyDisposeSharedSteamServiceOnSceneChange()
        {
            var path = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/StartMenuController.cs");
            var source = File.ReadAllText(path);
            var onDestroyStart = source.IndexOf("private void OnDestroy()", System.StringComparison.Ordinal);
            var onQuitStart = source.IndexOf("private void OnApplicationQuit()", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(onDestroyStart, 0);
            Assert.Greater(onQuitStart, onDestroyStart);
            var onDestroyBody = source.Substring(onDestroyStart, onQuitStart - onDestroyStart);
            StringAssert.DoesNotContain("roomService.Dispose()", onDestroyBody);
            StringAssert.Contains("OnlineRoomServiceProvider.DisposeActive()", source.Substring(onQuitStart));
        }

        [Test]
        public void StartMenu_OpenAndLocalStart_DoNotInitializeSteam()
        {
            var path = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/StartMenuController.cs");
            var source = File.ReadAllText(path);
            var awakeStart = source.IndexOf("private void Awake()", System.StringComparison.Ordinal);
            var nextMethod = source.IndexOf("private static bool ShouldStartLocalhostFromCommandLine()", awakeStart, System.StringComparison.Ordinal);
            var localStart = source.IndexOf("public void StartGame()", System.StringComparison.Ordinal);
            var createRoom = source.IndexOf("public async void CreateRoom()", localStart, System.StringComparison.Ordinal);

            Assert.GreaterOrEqual(awakeStart, 0);
            Assert.Greater(nextMethod, awakeStart);
            Assert.Greater(localStart, nextMethod);
            Assert.Greater(createRoom, localStart);
            StringAssert.DoesNotContain("roomService.Initialize()", source.Substring(awakeStart, nextMethod - awakeStart));
            StringAssert.DoesNotContain("SteamBootstrap", source.Substring(localStart, createRoom - localStart));
            StringAssert.DoesNotContain("roomService.", source.Substring(localStart, createRoom - localStart));
        }

        [Test]
        public void SteamBootstrap_AwakeDoesNotInitializeSteamApi()
        {
            var path = Path.Combine(UnityEngine.Application.dataPath, "YC/Infrastructure/Multiplayer/SteamBootstrap.cs");
            var source = File.ReadAllText(path);
            var awakeStart = source.IndexOf("private void Awake()", System.StringComparison.Ordinal);
            var initializeStart = source.IndexOf("public bool Initialize()", awakeStart, System.StringComparison.Ordinal);

            Assert.GreaterOrEqual(awakeStart, 0);
            Assert.Greater(initializeStart, awakeStart);
            StringAssert.DoesNotContain("Initialize();", source.Substring(awakeStart, initializeStart - awakeStart));
        }

        [Test]
        public void RecoverableSteamFailures_DoNotTriggerUnityErrorPause()
        {
            var menuPath = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/StartMenuController.cs");
            var menuSource = File.ReadAllText(menuPath);
            var createStart = menuSource.IndexOf("public async void CreateRoom()", System.StringComparison.Ordinal);
            var buildMenuStart = menuSource.IndexOf("private void BuildMenu()", createStart, System.StringComparison.Ordinal);
            var connectStart = menuSource.IndexOf("private async void ConnectToRoom()", System.StringComparison.Ordinal);
            var showRoomStart = menuSource.IndexOf("private void ShowRoomPanel", connectStart, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(createStart, 0);
            Assert.Greater(buildMenuStart, createStart);
            Assert.GreaterOrEqual(connectStart, 0);
            Assert.Greater(showRoomStart, connectStart);
            StringAssert.DoesNotContain("Debug.LogException", menuSource.Substring(createStart, buildMenuStart - createStart));
            StringAssert.DoesNotContain("Debug.LogException", menuSource.Substring(connectStart, showRoomStart - connectStart));

            var bootstrapPath = Path.Combine(UnityEngine.Application.dataPath, "YC/Infrastructure/Multiplayer/SteamBootstrap.cs");
            var bootstrapSource = File.ReadAllText(bootstrapPath);
            var failStart = bootstrapSource.IndexOf("private void Fail(string message)", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(failStart, 0);
            var failBody = bootstrapSource.Substring(failStart);
            StringAssert.Contains("Debug.LogWarning(message)", failBody);
            StringAssert.DoesNotContain("Debug.LogError(message)", failBody);
        }

        [Test]
        public void SteamRoomProgressCancel_ReturnsToMenuAndLateCallbacksAreIgnored()
        {
            var menuPath = Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/StartMenuController.cs");
            var menuSource = File.ReadAllText(menuPath);
            var progressStart = menuSource.IndexOf("private void ShowRoomProgressPanel", System.StringComparison.Ordinal);
            var nextMenuMethod = menuSource.IndexOf("private void StartRoomGame", progressStart, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(progressStart, 0);
            Assert.Greater(nextMenuMethod, progressStart);
            StringAssert.Contains("HideRoomPanel", menuSource.Substring(progressStart, nextMenuMethod - progressStart));

            var servicePath = Path.Combine(UnityEngine.Application.dataPath, "YC/Infrastructure/Multiplayer/SteamRoomService.cs");
            var serviceSource = File.ReadAllText(servicePath);
            StringAssert.Contains("if (pendingCreate == null)", serviceSource);
            StringAssert.Contains("if (pendingCreate == null && pendingJoin == null)", serviceSource);
        }

        [TestCase("Host")]
        [TestCase("Client")]
        public void ActiveGameLobbyInvite_IsDeferredWithoutTouchingCurrentSession(string role)
        {
            var service = new FakeOnlineRoomService
            {
                CurrentRoom = CreateRoom("100", role == "Host" ? 1 : 2)
            };
            service.RecordInvite("200");
            var flow = new LobbyJoinRequestFlow(service);

            var result = flow.ProcessPendingAsync(true, role).GetAwaiter().GetResult();

            Assert.That(result.Status, Is.EqualTo(LobbyJoinRequestStatus.Deferred));
            Assert.That(service.JoinCalls, Is.Zero);
            Assert.That(service.ShutdownCalls, Is.Zero);
            Assert.That(service.CloseCurrentRoomCalls, Is.Zero);
            Assert.That(service.CurrentRoom.RoomId, Is.EqualTo("100"));
            Assert.That(service.HasPendingLobbyJoinRequest, Is.True);
        }

        [Test]
        public void StartMenuIdleLobbyInvite_JoinsInvitedRoom()
        {
            var service = new FakeOnlineRoomService();
            service.RecordInvite("200");
            var flow = new LobbyJoinRequestFlow(service);

            var result = flow.ProcessPendingAsync(false, "Player 2").GetAwaiter().GetResult();

            Assert.That(result.Status, Is.EqualTo(LobbyJoinRequestStatus.Joined));
            Assert.That(result.Room.RoomId, Is.EqualTo("200"));
            Assert.That(service.JoinCalls, Is.EqualTo(1));
            Assert.That(service.ShutdownCalls, Is.Zero);
        }

        [Test]
        public void WaitingRoomLobbyInvite_ClosesOldRoomExactlyOnceAndConsumesOnce()
        {
            var service = new FakeOnlineRoomService { CurrentRoom = CreateRoom("100", 1) };
            service.RecordInvite("200");
            var flow = new LobbyJoinRequestFlow(service);

            var first = flow.ProcessPendingAsync(false, "Player 1").GetAwaiter().GetResult();
            var second = flow.ProcessPendingAsync(false, "Player 1").GetAwaiter().GetResult();

            Assert.That(first.Status, Is.EqualTo(LobbyJoinRequestStatus.Joined));
            Assert.That(second.Status, Is.EqualTo(LobbyJoinRequestStatus.None));
            Assert.That(service.JoinCalls, Is.EqualTo(1));
            Assert.That(service.CloseCurrentRoomCalls, Is.EqualTo(1));
            Assert.That(service.ShutdownCalls, Is.Zero, "受控流程不得在 JoinRoomAsync 之外重复关房。");
            Assert.That(service.CurrentRoom.RoomId, Is.EqualTo("200"));
        }

        [Test]
        public void FailedInvitedJoin_DoesNotPublishPartialRoomOrRetryConsumedRequest()
        {
            var service = new FakeOnlineRoomService { FailJoin = true };
            service.RecordInvite("200");
            var flow = new LobbyJoinRequestFlow(service);

            var failed = flow.ProcessPendingAsync(false, "Player 2").GetAwaiter().GetResult();
            var retry = flow.ProcessPendingAsync(false, "Player 2").GetAwaiter().GetResult();

            Assert.That(failed.Status, Is.EqualTo(LobbyJoinRequestStatus.Failed));
            Assert.That(retry.Status, Is.EqualTo(LobbyJoinRequestStatus.None));
            Assert.That(service.JoinCalls, Is.EqualTo(1));
            Assert.That(service.CurrentRoom, Is.Null);
            Assert.That(service.ShutdownCalls, Is.Zero);
        }

        [Test]
        public void LobbyJoinRequestInbox_KeepsOnlyLatestLobbyAndConsumesItOnce()
        {
            var inbox = new LobbyJoinRequestInbox();
            inbox.Record("100");
            inbox.Record("200");

            Assert.That(inbox.TryConsume(out var lobbyId), Is.True);
            Assert.That(lobbyId, Is.EqualTo("200"));
            Assert.That(inbox.TryConsume(out _), Is.False);
            Assert.That(inbox.HasPending, Is.False);
        }

        [Test]
        public void SteamInviteCallback_OnlyRecordsRequestAndNeverSwitchesSession()
        {
            var source = ReadSource("YC/Infrastructure/Multiplayer/SteamRoomService.cs");
            var callback = ExtractMethod(source, "private void OnLobbyJoinRequested", "private void RefreshRoom");

            StringAssert.Contains("lobbyJoinRequests.Record", callback);
            StringAssert.DoesNotContain("JoinRoomAsync", callback);
            StringAssert.DoesNotContain("ShutdownNetworkAndLobby", callback);
            StringAssert.DoesNotContain("MirrorNetworkRuntime", callback);
            StringAssert.DoesNotContain("OnlineRoomServiceProvider", callback);
            StringAssert.DoesNotContain("GameLaunchContext", callback);
        }

        [Test]
        public void InviteSubscriptions_AreSymmetricAndGameplayNoticeIsNonDestructive()
        {
            var menuSource = ReadSource("YC/Presentation/StartMenuController.cs");
            StringAssert.Contains("roomService.LobbyJoinRequested += QueueLobbyJoinRequested;", menuSource);
            StringAssert.Contains("roomService.LobbyJoinRequested -= QueueLobbyJoinRequested;", menuSource);
            var process = ExtractMethod(menuSource, "private async void ProcessPendingLobbyJoinRequest", "private void ShowRoomPanel");
            StringAssert.Contains("lobbyJoinRequestFlow.ProcessPendingAsync", process);
            StringAssert.DoesNotContain("roomService.Shutdown", process);
            StringAssert.DoesNotContain("ShutdownOnlineSession", process);

            var contextSource = ReadSource("YC/Presentation/GameLaunchContext.cs");
            StringAssert.Contains("if (mode != LaunchMode.Local)", contextSource);
            StringAssert.Contains("subscribedRoomService.LobbyJoinRequested += OnLobbyJoinRequested;", contextSource);
            StringAssert.Contains("subscribedRoomService.LobbyJoinRequested -= OnLobbyJoinRequested;", contextSource);
            var gameCallback = ExtractMethod(contextSource, "private void OnLobbyJoinRequested", "public bool TryConsumeOnlineSessionNotice");
            StringAssert.Contains("OnlineSessionNotice", gameCallback);
            StringAssert.DoesNotContain("ShutdownOnlineSession", gameCallback);
            StringAssert.DoesNotContain("SceneManager.LoadScene", gameCallback);

            var commandSource = ReadSource("YC/Presentation/CommandSubmissionController.cs");
            StringAssert.Contains("launchContext.OnlineSessionNotice += OnOnlineSessionNotice;", commandSource);
            StringAssert.Contains("launchContext.OnlineSessionNotice -= OnOnlineSessionNotice;", commandSource);
        }

        [Test]
        public void HostAndClientLocalDisconnectsShareTheSameReturnProtection()
        {
            var runtimeSource = ReadSource("YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs");
            var startHost = ExtractMethod(runtimeSource, "public void StartHost()", "public void StartLocalHost()");
            var startClient = ExtractMethod(runtimeSource, "public void StartClient", "public void StartLocalClient");
            StringAssert.Contains("SubscribeMirrorCallbacks();", startHost);
            StringAssert.Contains("SubscribeMirrorCallbacks();", startClient);

            var contextSource = ReadSource("YC/Presentation/GameLaunchContext.cs");
            StringAssert.Contains("if (mode != LaunchMode.Local)", contextSource);
            StringAssert.DoesNotContain("mode == LaunchMode.Client && MirrorNetworkRuntime", contextSource);
        }

        [Test]
        public void LocalMirrorTestMode_UsesProviderTelepathyAndIdentityHandshake()
        {
            var menuSource = ReadSource("YC/Presentation/StartMenuController.cs");
            StringAssert.Contains("OnlineRoomServiceProvider.GetOrCreate()", menuSource);
            StringAssert.Contains("LocalMirrorTestMode.IsEnabled", menuSource);

            var providerSource = ReadSource("YC/Infrastructure/Multiplayer/OnlineRoomServiceProvider.cs");
            StringAssert.Contains("new LocalMirrorRoomService()", providerSource);
            StringAssert.Contains("new SteamRoomService()", providerSource);

            var runtimeSource = ReadSource("YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs");
            StringAssert.Contains("TelepathyTransport", runtimeSource);
            StringAssert.Contains("StartLocalHost", runtimeSource);
            StringAssert.Contains("StartLocalClient", runtimeSource);

            var transportSource = ReadSource("YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs");
            StringAssert.Contains("LocalPlayerIdentityMessage", transportSource);
            StringAssert.Contains("LocalMirrorIdentity.ForPlayer", transportSource);
        }

        [Test]
        public void LocalRoomSignalingPort_DoesNotConflictWithMirrorPort()
        {
            var roomSource = ReadSource("YC/Infrastructure/Multiplayer/Core/NetworkRoomService.cs");
            StringAssert.Contains("private const int DefaultPort = 7780;", roomSource);
            var modeSource = ReadSource("YC/Infrastructure/Multiplayer/LocalMirrorTestMode.cs");
            StringAssert.Contains("public const ushort MirrorPort = 7777;", modeSource);
        }

        [Test]
        public void StartMenu_RoomUpdatesAreQueuedAndRefreshedOnMainThread()
        {
            var menuSource = ReadSource("YC/Presentation/StartMenuController.cs");
            StringAssert.Contains("pendingRoomUpdate = room;", menuSource);
            StringAssert.Contains("ShowRoomPanel(roomUpdate, roomUpdate.LocalPlayerId == roomUpdate.HostPlayerId);", menuSource);
        }

        [TestCase("YC/Presentation/GameSettingsMenuController.cs")]
        [TestCase("YC/Presentation/RoundTrackerController.cs")]
        public void ReturnToStartScene_ShutsDownOnlineSessionBeforeLoadingScene(string relativePath)
        {
            var source = File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, relativePath));
            var methodStart = source.IndexOf("ReturnToStartScene()", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(methodStart, 0, relativePath);

            var shutdown = source.IndexOf("GameLaunchContext.ShutdownOnlineSession();", methodStart, System.StringComparison.Ordinal);
            var loadScene = source.IndexOf("SceneManager.LoadScene", methodStart, System.StringComparison.Ordinal);
            Assert.Greater(shutdown, methodStart, relativePath);
            Assert.Greater(loadScene, shutdown, relativePath);
        }

        private static ulong[] Values(IReadOnlyDictionary<int, ulong> seats)
        {
            return new[] { seats[1], seats[2], seats[3], seats[4] };
        }

        private static string ReadSource(string relativePath) =>
            File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, relativePath));

        private static string ExtractMethod(string source, string methodStart, string nextMethodStart)
        {
            var start = source.IndexOf(methodStart, System.StringComparison.Ordinal);
            var end = source.IndexOf(nextMethodStart, start, System.StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), methodStart);
            Assert.That(end, Is.GreaterThan(start), nextMethodStart);
            return source.Substring(start, end - start);
        }

        private static Dictionary<string, string> CompatibleData()
        {
            return new Dictionary<string, string>
            {
                ["gameKey"] = SteamLobbyPolicy.GameKey,
                ["protocolVersion"] = SteamLobbyPolicy.ProtocolVersion,
                ["roomStatus"] = SteamLobbyPolicy.WaitingStatus,
                ["playerCount"] = "4",
                ["hostSteamId"] = "111"
            };
        }

        private static RoomState CreateRoom(string roomId, int localPlayerId)
        {
            return new RoomState
            {
                RoomId = roomId,
                HostPlayerId = 1,
                LocalPlayerId = localPlayerId,
                PlayerCount = 3
            };
        }

        private sealed class FakeOnlineRoomService : IOnlineRoomService
        {
            private readonly LobbyJoinRequestInbox inbox = new LobbyJoinRequestInbox();

            public bool SupportsFriendInvites => true;
            public bool HasPendingLobbyJoinRequest => inbox.HasPending;
            public RoomState CurrentRoom { get; set; }
            public bool FailJoin { get; set; }
            public int JoinCalls { get; private set; }
            public int ShutdownCalls { get; private set; }
            public int CloseCurrentRoomCalls { get; private set; }

            public event System.Action<RoomState> RoomUpdated;
            public event System.Action<RoomState> GameStarted;
            public event System.Action RoomDisbanded;
            public event System.Action<string> ErrorOccurred;
            public event System.Action<string> LobbyJoinRequested
            {
                add => inbox.LobbyJoinRequested += value;
                remove => inbox.LobbyJoinRequested -= value;
            }

            public void RecordInvite(string lobbyId) => inbox.Record(lobbyId);
            public bool TryConsumePendingLobbyJoinRequest(out string lobbyId) => inbox.TryConsume(out lobbyId);
            public RoomState GetCurrentRoom() => CurrentRoom?.Clone();
            public void Initialize() { }

            public Task<RoomState> JoinRoomAsync(string roomId, string playerName)
            {
                JoinCalls++;
                if (CurrentRoom != null)
                {
                    CloseCurrentRoomCalls++;
                    CurrentRoom = null;
                }

                if (FailJoin)
                    throw new System.InvalidOperationException("模拟加入失败");

                CurrentRoom = CreateRoom(roomId, 2);
                return Task.FromResult(CurrentRoom.Clone());
            }

            public Task<RoomState> CreateRoomAsync(string hostPlayerName, int playerCount) =>
                throw new System.NotSupportedException();

            public Task StartGameAsync() => Task.CompletedTask;
            public void InviteFriends() { }
            public void Shutdown() => ShutdownCalls++;
            public void Dispose() { }
        }
    }
}
