using System;
using System.Collections.Generic;
using System.IO;
using YC.Application.DevTools;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Infrastructure.Multiplayer;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuController : MonoBehaviour
    {
        private const string DevStartLocalhostArg = "--yc-dev-start-localhost";
        private const string DevLocalMirrorHostPrefix = "--yc-dev-local-mirror-host=";
        private const string DevLocalMirrorJoinPrefix = "--yc-dev-local-mirror-join=";
        private const string DevLocalMirrorRoomFilePrefix = "--yc-dev-local-mirror-room-file=";
        private const string LocalGameSeedSourcePrefix = "LOCAL_GAME_";
        private const int SteamTwoPlayerValidationCount = 2;
        private const string CollectionRoomSceneName = "CollectionRoom";
        private const string CreatorSiteUrl = "https://github.com/zhuanshiluobo";
        private const string OfficialSiteUrl = "https://ak.hypergryph.com/boardgame_nomadcity";
        private const string WikiUrl = "https://prts.wiki/w/%E6%B8%B8%E5%9F%8E%E6%8B%93%E8%8D%92%EF%BC%9A%E9%93%B8%E5%9F%BA%E8%80%85";

        [SerializeField] private string mapSceneName = "SampleScene";
        [SerializeField] private Texture2D coverTexture;
        [SerializeField] private StartMenuView view;

        [NonSerialized] private IOnlineRoomService roomService;
        [NonSerialized] private LobbyJoinRequestFlow lobbyJoinRequestFlow;
        [NonSerialized] private bool roomEventsSubscribed;
        private readonly object networkEventLock = new object();
        private readonly List<Text> roomSeatRows = new List<Text>();
        private GameObject roomPanel;
        private InputField joinRoomInput;
        private Text roomStatusText;
        // 临时通知不参与热重载序列化，避免 null 被还原为空房间并触发弹窗。
        [NonSerialized] private RoomState pendingRoomUpdate;
        [NonSerialized] private RoomState pendingGameStart;
        private string pendingNetworkError;
        private bool pendingRoomDisbanded;
        private bool pendingLobbyJoinRequested;
        private int selectedRoomPlayerCount = 4;
        private int selectedLocalCityCount = 1;
        [System.NonSerialized] private MatchSaveData resumeArchive;
        private bool loadingGame;
        private bool joiningRoom;
        public bool CanOpenMatchSave => !loadingGame && !joiningRoom && roomPanel == null;
        private bool autoStartLocalMirrorGame;
        private bool autoStartLocalMirrorRequestPending;
        private float nextAutoStartLocalMirrorAttemptTime;
        private string devLocalMirrorRoomOutputPath = string.Empty;
        private bool mapSelectionCreatesOnlineRoom;
        private string configuredLocalMapSelectionTitle;

        private void Awake()
        {
            // 每次进入运行模式都从无待处理房间通知开始。
            pendingRoomUpdate = null;
            pendingGameStart = null;
            UnityEngine.Application.runInBackground = true;
            BindRoomService();
            if (TryRunDevCommandLineTask())
            {
                return;
            }

            if (ShouldStartLocalhostFromCommandLine())
            {
                StartGame();
                return;
            }

            roomService.Shutdown();
            if (!BuildMenu())
            {
                return;
            }
            if (TryStartDevLocalMirrorFromCommandLine())
            {
                return;
            }

            MatchSaveController.Instance?.Unbind();
            ProcessPendingLobbyJoinRequest();
        }

        private void OnEnable() => BindRoomService();

        private void OnDisable() => UnbindRoomServiceEvents();

        private void BindRoomService()
        {
            // 脚本重载会丢失托管服务；OnEnable 只恢复服务和订阅，不重建菜单或关闭房间。
            if (roomService == null) roomService = OnlineRoomServiceProvider.GetOrCreate();
            if (lobbyJoinRequestFlow == null) lobbyJoinRequestFlow = new LobbyJoinRequestFlow(roomService);
            if (roomEventsSubscribed) return;
            roomService.RoomUpdated += QueueRoomUpdate;
            roomService.GameStarted += QueueGameStart;
            roomService.RoomDisbanded += QueueRoomDisbanded;
            roomService.ErrorOccurred += QueueNetworkError;
            roomService.LobbyJoinRequested += QueueLobbyJoinRequested;
            roomEventsSubscribed = true;
        }

        private void UnbindRoomServiceEvents()
        {
            if (roomService != null && roomEventsSubscribed)
            {
                roomService.RoomUpdated -= QueueRoomUpdate;
                roomService.GameStarted -= QueueGameStart;
                roomService.RoomDisbanded -= QueueRoomDisbanded;
                roomService.ErrorOccurred -= QueueNetworkError;
                roomService.LobbyJoinRequested -= QueueLobbyJoinRequested;
            }
            roomEventsSubscribed = false;
        }

        private static bool ShouldStartLocalhostFromCommandLine()
        {
            return HasCommandLineArg(Environment.GetCommandLineArgs(), DevStartLocalhostArg);
        }

        private bool TryStartDevLocalMirrorFromCommandLine()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var args = Environment.GetCommandLineArgs();
            var hostPlayerCountText = GetCommandLineValue(args, DevLocalMirrorHostPrefix);
            var joinRoomId = GetCommandLineValue(args, DevLocalMirrorJoinPrefix);
            devLocalMirrorRoomOutputPath = GetCommandLineValue(args, DevLocalMirrorRoomFilePrefix);

            int hostPlayerCount;
            if (int.TryParse(hostPlayerCountText, out hostPlayerCount))
            {
                selectedRoomPlayerCount = Mathf.Clamp(hostPlayerCount, 3, 4);
                autoStartLocalMirrorGame = true;
                Debug.Log(
                    "[LocalMirrorAutomation] Creating " +
                    selectedRoomPlayerCount +
                    "-player room and waiting to auto-start.");
                CreateRoom();
                return true;
            }

            if (!string.IsNullOrEmpty(joinRoomId))
            {
                ShowJoinRoomPanel();
                joinRoomInput.text = joinRoomId;
                Debug.Log("[LocalMirrorAutomation] Joining room " + joinRoomId + ".");
                ConnectToRoom();
                return true;
            }
#endif
            return false;
        }

        private static bool TryRunDevCommandLineTask()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var args = Environment.GetCommandLineArgs();
            if (HasCommandLineArg(args, "--yc-dev-font-health-check") ||
                HasCommandLineArg(args, "--yc-dev-font-health-check-simulate"))
            {
                var mode = HasCommandLineArg(args, "--yc-dev-font-health-check-simulate")
                    ? FontHealthCheckMode.SimulateRecreate
                    : FontHealthCheckMode.CheckOnly;
                try
                {
                    var result = FontHealthCheckRunner.Run(mode);
                    Debug.Log(result.Snapshot);
                    if (!UnityEngine.Application.isEditor && UnityEngine.Application.isBatchMode)
                    {
                        UnityEngine.Application.Quit(0);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    if (!UnityEngine.Application.isEditor && UnityEngine.Application.isBatchMode)
                    {
                        UnityEngine.Application.Quit(1);
                    }
                }

                return true;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--yc-dev-autoplay-localhost", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var playerCountText = GetCommandLineValue(args, "--yc-dev-player-count=");
                var playerCount = 4;
                if (!string.IsNullOrEmpty(playerCountText) &&
                    (!int.TryParse(playerCountText, out playerCount) || (playerCount != 3 && playerCount != 4)))
                {
                    Debug.LogError("自动跑局人数仅支持3或4。");
                    if (!UnityEngine.Application.isEditor) UnityEngine.Application.Quit(1);
                    return true;
                }
                var result = LocalhostAutoplayRunner.RunToRound8Settlement(
                    YC.Infrastructure.Lua.LuaContentCatalog.Register,
                    playerCount);
                if (result.Succeeded)
                {
                    Debug.Log(result.Snapshot);
                }
                else
                {
                    Debug.LogError(result.Snapshot);
                }

                if (!UnityEngine.Application.isEditor && UnityEngine.Application.isBatchMode)
                {
                    UnityEngine.Application.Quit(result.Succeeded ? 0 : 1);
                }

                return true;
            }
#endif
            return false;
        }

        private static bool HasCommandLineArg(string[] args, string expectedValue)
        {
            if (args == null)
            {
                return false;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetCommandLineValue(string[] args, string prefix)
        {
            if (args == null || string.IsNullOrEmpty(prefix))
            {
                return string.Empty;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i].Substring(prefix.Length).Trim();
                }
            }

            return string.Empty;
        }

        private void Update()
        {
            if (roomService == null) return;
            RoomState roomUpdate = null;
            RoomState gameStart = null;
            string networkError = null;
            var roomDisbanded = false;
            var lobbyJoinRequested = false;

            lock (networkEventLock)
            {
                roomUpdate = pendingRoomUpdate;
                gameStart = pendingGameStart;
                networkError = pendingNetworkError;
                roomDisbanded = pendingRoomDisbanded;
                lobbyJoinRequested = pendingLobbyJoinRequested;
                pendingRoomUpdate = null;
                pendingGameStart = null;
                pendingNetworkError = null;
                pendingRoomDisbanded = false;
                pendingLobbyJoinRequested = false;
            }

            if (roomUpdate != null)
            {
                joiningRoom = false;
                ShowRoomPanel(roomUpdate, roomUpdate.LocalPlayerId == roomUpdate.HostPlayerId);
            }

            if (!string.IsNullOrEmpty(networkError))
            {
                var wasJoiningRoom = joiningRoom;
                joiningRoom = false;
                if (wasJoiningRoom && roomStatusText == view.MessagePanel.MessageText)
                {
                    ShowOperationError("联机操作失败", networkError);
                }
                else if (roomStatusText != null)
                {
                    SetRoomStatus(networkError);
                }
                else
                {
                    ShowOperationError("联机操作失败", networkError);
                }
            }

            if (roomDisbanded)
            {
                joiningRoom = false;
                ShowRoomDisbandedPanel();
                return;
            }

            if (gameStart != null)
            {
                StartRoomGame(gameStart);
            }

            if (lobbyJoinRequested || roomService.HasPendingLobbyJoinRequest)
            {
                ProcessPendingLobbyJoinRequest();
            }

            TryAutoStartLocalMirrorGame();
        }

        private void OnDestroy()
        {
            UnbindRoomServiceEvents();
            if (roomService == null) return;

            if (!loadingGame)
            {
                roomService.Shutdown();
            }
        }

        private void OnApplicationQuit()
        {
            OnlineRoomServiceProvider.DisposeActive();
        }

        public async System.Threading.Tasks.Task RestoreMatch(MatchSaveData data)
        {
            if (loadingGame || joiningRoom) throw new InvalidOperationException("已有启动流程正在进行。");
            GameLaunchContext.Ensure().ClearRestore();
            resumeArchive = null;
            if (data.Mode == LaunchMode.Local)
            {
                roomService.Shutdown();
                var context = GameLaunchContext.Ensure();
                context.Configure(LaunchMode.Local, data.LocalPlayerId, data.GameId, data.Seats.ConvertAll(s => s.ToSeat()), data.MapId);
                context.SetRestore(data);
                BeginMapSceneLoad();
                if (!loadingGame) { context.ClearRestore(); throw new InvalidOperationException("无法启动存档地图。"); }
                return;
            }
            if (data.LocalTestNetwork != LocalMirrorTestMode.IsEnabled)
                throw new InvalidOperationException("此存档的联机后端与当前运行模式不一致。");
            joiningRoom = true;
            try
            {
                var resumable = roomService as IResumableOnlineRoomService;
                if (resumable == null) throw new InvalidOperationException("当前后端不支持续局。");
                var room = await resumable.CreateResumeRoomAsync(ResumeRoomOptions.FromSave(data));
                if (this == null) { roomService.Shutdown(); return; }
                resumeArchive = data;
                ShowRoomPanel(room, true);
            }
            catch { resumeArchive = null; roomService.Shutdown(); throw; }
            finally { joiningRoom = false; }
        }
        public void StartGame()
        {
            if (loadingGame)
            {
                return;
            }
            resumeArchive = null;
            GameLaunchContext.Instance?.ClearRestore();

            var seats = new List<PlayerSeat>();
            var colors = new[] { PlayerColor.Blue, PlayerColor.Red, PlayerColor.Green, PlayerColor.Yellow };
            for (var i = 0; i < selectedLocalCityCount; i++)
            {
                seats.Add(new PlayerSeat
                {
                    PlayerId = i + 1,
                    PlayerName = "玩家 " + (i + 1),
                    Color = colors[i],
                    LobbyMemberPresent = true,
                    TransportConnected = true,
                    IdentityVerified = true,
                    GameStateSynchronized = true,
                    IsReady = true
                });
            }
            GameLaunchContext.Ensure().Configure(LaunchMode.Local, 1, CreateLocalGameSeedSource(), seats,
                YC.Domain.Maps.StaticMapDefinitions.ForPlayerCount(selectedRoomPlayerCount).MapId);
            BeginMapSceneLoad();
        }

        private static string CreateLocalGameSeedSource()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var seed = GetCommandLineValue(Environment.GetCommandLineArgs(), "--yc-player-journey-seed=");
            if (!string.IsNullOrEmpty(seed)) return LocalGameSeedSourcePrefix + seed;
#endif
            return LocalGameSeedSourcePrefix + Guid.NewGuid().ToString("N");
        }

        public async void CreateRoom()
        {
            if (joiningRoom)
            {
                return;
            }
            resumeArchive = null;
            GameLaunchContext.Instance?.ClearRestore();

            joiningRoom = true;
            ShowRoomProgressPanel(
                "创建房间",
                LocalMirrorTestMode.IsEnabled ? "正在创建 Mirror 本地测试房间..." : "正在初始化 Steam Lobby / P2P...");

            try
            {
                var room = await roomService.CreateRoomAsync(
                    LocalMirrorTestMode.IsEnabled ? LocalMirrorTestMode.PlayerName : "Player 1",
                    selectedRoomPlayerCount);
                if (this == null) return;
                joiningRoom = false;
                ShowRoomPanel(room, true);
            }
            catch (Exception ex)
            {
                if (this == null) return;
                joiningRoom = false;
                ShowOperationError("创建房间失败", ex.Message);
            }
        }

        public void CreateStandardRoom()
        {
            selectedRoomPlayerCount = 4;
            CreateRoom();
        }

        public void JoinRoom()
        {
            ShowJoinRoomPanel();
        }

        private bool BuildMenu()
        {
            if (view == null)
            {
                Debug.LogError(
                    "StartMenuController 缺少 StartMenuView 编辑器引用。请使用 " +
                    "Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab 装配开始场景。",
                    this);
                enabled = false;
                return false;
            }

            if (!view.TryValidateConfiguration(out var reason))
            {
                Debug.LogError("StartMenuView 编辑器装配不完整：" + reason, view);
                enabled = false;
                return false;
            }

            configuredLocalMapSelectionTitle = view.MapSelectionPanel.TitleText.text;
            view.CoverImage.texture = coverTexture;
            BindStaticUi();
            view.HideRoomPanels();
            RefreshMatchSaveAvailability();
            return true;
        }

        public void RefreshMatchSaveAvailability()
        {
            if (view != null) view.SetMatchSaveAvailable(MatchSaveController.Ensure().HasSaves);
        }

        private void BindStaticUi()
        {
            if (view.LoadGameButton != null)
                BindButton(view.LoadGameButton, () => MatchSaveWindowView.OpenLoad(this));
            BindButton(view.StartGameButton, ShowLocalMapSelectionPanel);
            YC.PlayerJourney.PlayerAutomationId.Attach(view.StartGameButton.gameObject, "start.local_game");
            // 地图选择已改为人数与地图轮换面板，确认按钮承担原四人地图按钮的自动化入口。
            YC.PlayerJourney.PlayerAutomationId.Attach(view.MapSelectionPanel.StartButton.gameObject, "start.four_player_map");
            BindButton(view.OnlineModeButton, ShowOnlineModePanel);
            BindButton(view.AchievementsButton, ShowAchievementsPanel);
            BindButton(view.QuitGameButton, QuitGame);
            BindButton(view.CreatorSiteButton, () => UnityEngine.Application.OpenURL(CreatorSiteUrl));
            BindButton(view.OfficialSiteButton, () => UnityEngine.Application.OpenURL(OfficialSiteUrl));
            BindButton(view.WikiButton, () => UnityEngine.Application.OpenURL(WikiUrl));

            BindButton(view.OnlineModePanel.CreateRoomButton, ShowOnlineMapSelectionPanel);
            BindButton(view.OnlineModePanel.JoinRoomButton, JoinRoom);
            BindButton(view.OnlineModePanel.BackButton, HideRoomPanel);
            BindButton(view.AchievementsPanel.CollectionRoomButton, EnterCollectionRoom);
            BindButton(view.AchievementsPanel.BackButton, HideRoomPanel);

            BindButton(view.MapSelectionPanel.PreviousCountButton, () => ChangeLocalCityCount(-1));
            BindButton(view.MapSelectionPanel.NextCountButton, () => ChangeLocalCityCount(1));
            BindButton(view.MapSelectionPanel.PreviousMapButton, ToggleSelectedMap);
            BindButton(view.MapSelectionPanel.NextMapButton, ToggleSelectedMap);
            BindButton(view.MapSelectionPanel.StartButton, ConfirmSelectedMap);
            BindButton(view.MapSelectionPanel.BackButton, HideRoomPanel);

            BindButton(view.JoinPanel.PasteButton, PasteRoomCodeFromClipboard);
            BindButton(view.JoinPanel.JoinButton, ConnectToRoom);
            BindButton(view.JoinPanel.BackButton, HideRoomPanel);
            BindButton(view.RoomPanel.InviteButton, InviteSteamFriends);
            BindButton(view.RoomPanel.StartButton, StartOnlineGame);
            BindButton(view.RoomPanel.BackButton, HideRoomPanel);
            if (view.RoomPanel.ResumeSeatsButton != null)
                BindButton(view.RoomPanel.ResumeSeatsButton, MatchSaveWindowView.OpenSeats);
        }

        private static void BindButton(Button button, UnityEngine.Events.UnityAction action)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        private static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            UnityEngine.Application.Quit();
#endif
        }


        private void ShowLocalMapSelectionPanel()
        {
            ShowMapSelectionPanel(false);
        }

        private void ShowOnlineModePanel()
        {
            view.HideRoomPanels();
            var panel = view.OnlineModePanel;
            panel.gameObject.SetActive(true);
            roomPanel = panel.gameObject;
            joinRoomInput = null;
            roomStatusText = null;
        }

        private void ShowAchievementsPanel()
        {
            view.HideRoomPanels();
            var panel = view.AchievementsPanel;
            panel.gameObject.SetActive(true);
            roomPanel = panel.gameObject;
            joinRoomInput = null;
            roomStatusText = null;
        }

        private void EnterCollectionRoom()
        {
            BeginSceneLoad(CollectionRoomSceneName, "收藏室加载失败");
        }

        private void ShowOnlineMapSelectionPanel()
        {
            ShowMapSelectionPanel(true);
        }

        private void ShowMapSelectionPanel(bool createsOnlineRoom)
        {
            view.HideRoomPanels();
            var panel = view.MapSelectionPanel;
            panel.gameObject.SetActive(true);
            panel.TitleText.text = createsOnlineRoom ? "联机模式" : configuredLocalMapSelectionTitle;
            mapSelectionCreatesOnlineRoom = createsOnlineRoom;
            RefreshMapSelection();
            roomPanel = panel.gameObject;
            joinRoomInput = null;
            roomStatusText = null;
        }

        private void ChangeLocalCityCount(int delta)
        {
            if (mapSelectionCreatesOnlineRoom || loadingGame) return;
            selectedLocalCityCount = Mathf.Clamp(selectedLocalCityCount + delta, 1, selectedRoomPlayerCount);
            RefreshMapSelection();
        }
        private void ToggleSelectedMap()
        {
            if (loadingGame) return;
            selectedRoomPlayerCount = selectedRoomPlayerCount == 3 ? 4 : 3;
            selectedLocalCityCount = Mathf.Min(selectedLocalCityCount, selectedRoomPlayerCount);
            RefreshMapSelection();
        }
        private void RefreshMapSelection()
        {
            var panel = view.MapSelectionPanel;
            panel.CountText.text = (mapSelectionCreatesOnlineRoom ? selectedRoomPlayerCount : selectedLocalCityCount).ToString();
            panel.MapText.text = selectedRoomPlayerCount == 3 ? "三人地图" : "四人地图";
            panel.PreviousCountButton.interactable = !mapSelectionCreatesOnlineRoom && selectedLocalCityCount > 1;
            panel.NextCountButton.interactable = !mapSelectionCreatesOnlineRoom && selectedLocalCityCount < selectedRoomPlayerCount;
        }
        private void ConfirmSelectedMap()
        {
            if (loadingGame || SceneTransitionContext.IsTransitionInProgress) return;
            if (mapSelectionCreatesOnlineRoom) CreateRoom();
            else StartGame();
        }

        private void ShowJoinRoomPanel()
        {
            view.HideRoomPanels();
            var panel = view.JoinPanel;
            panel.gameObject.SetActive(true);
            roomPanel = panel.gameObject;
            joinRoomInput = panel.RoomCodeInput;
            roomStatusText = panel.StatusText;
            roomStatusText.text = string.Empty;
            panel.DescriptionText.text = LocalMirrorTestMode.IsEnabled
                ? "输入房主显示的本地地址（IP:端口）"
                : "输入房主显示的 Lobby 房间码";

            var placeholder = joinRoomInput.placeholder as Text;
            if (placeholder != null)
            {
                placeholder.text = LocalMirrorTestMode.IsEnabled
                    ? "例如 127.0.0.1:7780"
                    : "请输入 Steam Lobby ID";
            }
        }

        private async void ConnectToRoom()
        {
            if (joiningRoom)
            {
                return;
            }

            if (joinRoomInput == null || string.IsNullOrEmpty(joinRoomInput.text))
            {
                SetRoomStatus("请输入房间号。");
                return;
            }

            resumeArchive = null;
            GameLaunchContext.Instance?.ClearRestore();
            var roomCode = joinRoomInput.text.Trim();
            joiningRoom = true;
            SetRoomStatus(LocalMirrorTestMode.IsEnabled
                ? "正在连接 Mirror 本地测试房间..."
                : "正在通过 Steam Lobby / P2P 加入房间...");

            try
            {
                var room = await roomService.JoinRoomAsync(
                    roomCode,
                    LocalMirrorTestMode.IsEnabled ? LocalMirrorTestMode.PlayerName : "Player 2");
                if (this == null) return;
                joiningRoom = false;
                ShowRoomPanel(room, false);
            }
            catch (Exception ex)
            {
                if (this == null) return;
                joiningRoom = false;
                SetRoomStatus("加入房间失败：" + ex.Message);
            }
        }

        private async void ProcessPendingLobbyJoinRequest()
        {
            if (roomService == null ||
                lobbyJoinRequestFlow == null ||
                !roomService.HasPendingLobbyJoinRequest)
            {
                return;
            }

            var defer = loadingGame || joiningRoom;
            if (defer)
            {
                await lobbyJoinRequestFlow.ProcessPendingAsync(true, "Player 2");
                return;
            }

            joiningRoom = true;
            ShowRoomProgressPanel("加入受邀房间", "正在通过 Steam Lobby / P2P 加入受邀房间...");
            var result = await lobbyJoinRequestFlow.ProcessPendingAsync(false, "Player 2");
            if (this == null) return;
            joiningRoom = false;

            switch (result.Status)
            {
                case LobbyJoinRequestStatus.Joined:
                    resumeArchive = null;
                    GameLaunchContext.Instance?.ClearRestore();
                    goto case LobbyJoinRequestStatus.AlreadyInRoom;
                case LobbyJoinRequestStatus.AlreadyInRoom:
                    ShowRoomPanel(
                        result.Room,
                        result.Room != null && result.Room.LocalPlayerId == result.Room.HostPlayerId);
                    break;
                case LobbyJoinRequestStatus.Failed:
                    ShowOperationError("加入受邀房间失败", result.Message);
                    break;
                case LobbyJoinRequestStatus.Canceled:
                case LobbyJoinRequestStatus.Deferred:
                case LobbyJoinRequestStatus.None:
                    view.HideRoomPanels();
                    roomPanel = null;
                    roomStatusText = null;
                    break;
            }

            if (roomService.HasPendingLobbyJoinRequest)
                QueueLobbyJoinRequested(string.Empty);
        }

        private void ShowRoomPanel(RoomState room, bool hostControls)
        {
            if (room == null)
            {
                ShowOperationError("房间状态不可用", "没有收到有效的房间信息，请重试。");
                return;
            }

            view.HideRoomPanels();
            var panel = view.RoomPanel;
            panel.gameObject.SetActive(true);
            roomPanel = panel.gameObject;
            roomStatusText = panel.StatusText;
            ClearRoomSeatRows();

            if (hostControls && !string.IsNullOrEmpty(devLocalMirrorRoomOutputPath))
            {
                try
                {
                    var fullPath = Path.GetFullPath(devLocalMirrorRoomOutputPath);
                    var directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(fullPath, room.RoomId);
                    Debug.Log("[LocalMirrorAutomation] Room id written to " + fullPath + ".");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[LocalMirrorAutomation] Could not write room id: " + ex.Message);
                }
            }

            panel.RoomCodeText.text = "房间号 " + room.RoomId;
            BindButton(panel.CopyButton, () => CopyRoomCodeToClipboard(room.RoomId));

            var isTwoPlayerSteamValidation = room.PlayerCount == SteamTwoPlayerValidationCount &&
                                             roomService.SupportsFriendInvites;
            panel.ValidationText.gameObject.SetActive(isTwoPlayerSteamValidation);
            panel.ValidationText.text = isTwoPlayerSteamValidation
                ? "Steam 双人联机验证：复用四人地图，仅验证 Lobby、P2P、身份和同步链路。"
                : string.Empty;

            for (var i = 0; i < room.Seats.Count; i++)
            {
                var seat = room.Seats[i];
                var status = GetSeatReadinessLabel(seat);
                var marker = seat.PlayerId == room.LocalPlayerId ? "（你）" : string.Empty;
                var line = string.Format("{0}. {1}{2}  {3}", seat.PlayerId, seat.PlayerName, marker, status);
                var row = Instantiate(panel.SeatTemplate, panel.SeatListRoot, false);
                row.name = "Seat " + seat.PlayerId;
                row.text = line;
                row.color = UiTheme.GetPlayerColor(seat.Color, seat.LobbyMemberPresent ? 1f : 0.55f);
                row.gameObject.SetActive(true);
                roomSeatRows.Add(row);
            }

            var canStart = RoomReadinessPolicy.TryValidateStart(room, out var readinessReason);
            roomStatusText.text = hostControls
                ? (canStart
                    ? (isTwoPlayerSteamValidation
                        ? "两名玩家均已验证，可以开始 Steam 联机人工验证。"
                        : "所有玩家的网络连接和身份均已验证，可以开始游戏。")
                    : readinessReason)
                : "等待房主确认所有玩家的网络连接和身份。";

            panel.InviteButton.gameObject.SetActive(roomService.SupportsFriendInvites);
            panel.StartButton.gameObject.SetActive(hostControls);
            panel.StartButton.interactable = canStart;
            if (panel.ResumeSeatsButton != null)
                panel.ResumeSeatsButton.gameObject.SetActive(hostControls && room.Resume != null && !room.HasStarted);

            if (autoStartLocalMirrorGame && hostControls && canStart)
            {
                TryAutoStartLocalMirrorGame();
            }
        }

        private void ClearRoomSeatRows()
        {
            for (var i = 0; i < roomSeatRows.Count; i++)
            {
                if (roomSeatRows[i] != null)
                {
                    Destroy(roomSeatRows[i].gameObject);
                }
            }

            roomSeatRows.Clear();
        }

        private void TryAutoStartLocalMirrorGame()
        {
            if (!autoStartLocalMirrorGame ||
                autoStartLocalMirrorRequestPending ||
                loadingGame ||
                Time.unscaledTime < nextAutoStartLocalMirrorAttemptTime)
            {
                return;
            }

            var room = roomService == null ? null : roomService.GetCurrentRoom();
            if (room == null ||
                room.LocalPlayerId != room.HostPlayerId ||
                !RoomReadinessPolicy.TryValidateStart(room, out _))
            {
                return;
            }

            autoStartLocalMirrorRequestPending = true;
            Debug.Log("[LocalMirrorAutomation] All seats are ready; requesting game start.");
            StartAutomatedOnlineGame();
        }

        private async void StartAutomatedOnlineGame()
        {
            try
            {
                await roomService.StartGameAsync();
                autoStartLocalMirrorGame = false;
            }
            catch (Exception ex)
            {
                autoStartLocalMirrorRequestPending = false;
                nextAutoStartLocalMirrorAttemptTime = Time.unscaledTime + 1f;
                Debug.LogWarning(
                    "[LocalMirrorAutomation] Start request will retry: " +
                    (string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message));
            }
        }

        private async void StartOnlineGame()
        {
            try
            {
                await roomService.StartGameAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("开始联机对局失败：" + ex.Message);
                SetRoomStatus("开始游戏失败：" + ex.Message);
            }
        }

        private void InviteSteamFriends()
        {
            try
            {
                roomService.InviteFriends();
            }
            catch (Exception ex)
            {
                SetRoomStatus("无法打开 Steam 好友邀请：" + ex.Message);
            }
        }

        private void ShowRoomProgressPanel(string title, string message)
        {
            view.HideRoomPanels();
            var panel = view.MessagePanel;
            panel.gameObject.SetActive(true);
            roomPanel = panel.gameObject;
            joinRoomInput = null;
            roomStatusText = panel.MessageText;
            panel.TitleText.text = title;
            panel.MessageText.text = message;
            panel.ActionButton.gameObject.SetActive(false);
        }

        private void StartRoomGame(RoomState room)
        {
            if (loadingGame)
            {
                return;
            }

            if (room == null)
            {
                SetRoomStatus("房间状态不可用，请重新加入房间。");
                return;
            }

            var localPlayerId = room.LocalPlayerId;
            localPlayerId = GameLaunchStateFactory.ResolveHostLocalPlayerId(
                localPlayerId,
                room.HostPlayerId,
                room.LocalPlayerId == room.HostPlayerId,
                room.Seats);

            if (localPlayerId <= 0 || !RoomContainsPlayer(room, localPlayerId))
            {
                loadingGame = false;
                SetRoomStatus("无法确认本机玩家座位，请重新加入房间。");
                return;
            }

            var mode = localPlayerId == room.HostPlayerId ? LaunchMode.Host : LaunchMode.Client;
            if (room.Resume != null && mode == LaunchMode.Host && resumeArchive == null)
            {
                Debug.LogWarning("续局存档已失效，已中止启动。", this); return;
            }
            var context = GameLaunchContext.Ensure();
            context.Configure(mode, localPlayerId, room.RoomId, room.Seats, room.Resume?.MapId);
            if (mode == LaunchMode.Host && resumeArchive != null) context.SetRestore(resumeArchive);
            resumeArchive = null;
            BeginMapSceneLoad();
        }

        private void BeginMapSceneLoad()
        {
            var context = GameLaunchContext.Instance;
            var sceneName = context != null && context.MapId == YC.Domain.Maps.StaticMapDefinitions.ThreePlayerMapId ? "ThreePlayerScene" : mapSceneName;
            BeginSceneLoad(sceneName, "地图加载失败");
        }

        private void BeginSceneLoad(string sceneName, string errorTitle)
        {
            if (loadingGame || SceneTransitionContext.IsTransitionInProgress)
            {
                return;
            }

            loadingGame = true;
            try
            {
                if (!SceneTransitionContext.TryBeginTransition(sceneName))
                {
                    loadingGame = false;
                }
            }
            catch (Exception ex)
            {
                HandleSceneLoadFailure(errorTitle, ex.Message);
            }
        }

        private void HandleSceneLoadFailure(string title, string message)
        {
            loadingGame = false;
            SceneTransitionContext.Clear();
            ShowOperationError(title, message);
        }

        private static bool RoomContainsPlayer(RoomState room, int playerId)
        {
            if (room == null || room.Seats == null)
            {
                return false;
            }

            for (var i = 0; i < room.Seats.Count; i++)
            {
                if (room.Seats[i].PlayerId == playerId)
                {
                    return true;
                }
            }

            return false;
        }

        private void HideRoomPanel()
        {
            resumeArchive = null;
            GameLaunchContext.Instance?.ClearRestore();
            view.HideRoomPanels();
            roomPanel = null;
            ClearRoomSeatRows();
            joinRoomInput = null;
            roomStatusText = null;
            joiningRoom = false;
            roomService.Shutdown();
        }

        private void ShowOperationError(string title, string message)
        {
            view.HideRoomPanels();
            var panel = view.MessagePanel;
            panel.gameObject.SetActive(true);
            roomPanel = panel.gameObject;
            joinRoomInput = null;
            roomStatusText = panel.MessageText;
            panel.TitleText.text = title;
            panel.MessageText.text = string.IsNullOrEmpty(message) ? "发生未知错误，请重试。" : message;
            panel.ActionButton.gameObject.SetActive(true);
            panel.ActionButtonText.text = "确认";
            BindButton(panel.ActionButton, HideRoomPanel);
        }

        private void ShowRoomDisbandedPanel()
        {
            view.HideRoomPanels();
            var panel = view.MessagePanel;
            panel.gameObject.SetActive(true);
            roomPanel = panel.gameObject;
            roomStatusText = null;
            joinRoomInput = null;
            panel.TitleText.text = "房间已解散";
            panel.MessageText.text = "点击确认后返回主页";
            panel.ActionButton.gameObject.SetActive(true);
            panel.ActionButtonText.text = "确认";
            BindButton(panel.ActionButton, ConfirmRoomDisbanded);
        }

        private void ConfirmRoomDisbanded()
        {
            resumeArchive = null;
            GameLaunchContext.Instance?.ClearRestore();
            view.HideRoomPanels();
            roomPanel = null;
            ClearRoomSeatRows();
            roomStatusText = null;
            joinRoomInput = null;
            joiningRoom = false;
            roomService.Shutdown();
        }

        private static string GetSeatReadinessLabel(PlayerSeat seat)
        {
            if (seat == null || !seat.LobbyMemberPresent) return "等待加入 Lobby";
            if (!seat.TransportConnected) return "已在 Lobby，等待 Mirror 连接";
            if (!seat.IdentityVerified) return "Mirror 已连接，身份校验中";
            if (seat.GameStateSynchronized) return "对局状态已同步";
            return seat.IsReady ? "网络身份已验证" : "等待房主确认";
        }

        private void SetRoomStatus(string message)
        {
            if (roomStatusText != null)
            {
                roomStatusText.text = message;
            }
        }

        private void CopyRoomCodeToClipboard(string roomCode)
        {
            if (string.IsNullOrEmpty(roomCode))
            {
                SetRoomStatus("房间号不可用，无法复制。");
                return;
            }

            GUIUtility.systemCopyBuffer = roomCode;
            SetRoomStatus("房间号已复制。");
        }

        private void PasteRoomCodeFromClipboard()
        {
            if (joinRoomInput == null)
            {
                return;
            }

            var roomCode = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(roomCode))
            {
                SetRoomStatus("剪贴板没有可粘贴的房间号。");
                return;
            }

            joinRoomInput.text = roomCode.Trim();
            joinRoomInput.ActivateInputField();
            SetRoomStatus("已粘贴房间号。");
        }

        private void QueueRoomUpdate(RoomState room)
        {
            lock (networkEventLock)
            {
                pendingRoomUpdate = room;
            }
        }

        private void QueueGameStart(RoomState room)
        {
            lock (networkEventLock)
            {
                pendingGameStart = room;
            }
        }

        private void QueueNetworkError(string message)
        {
            lock (networkEventLock)
            {
                pendingNetworkError = message;
            }
        }

        private void QueueRoomDisbanded()
        {
            lock (networkEventLock)
            {
                pendingRoomDisbanded = true;
            }
        }

        private void QueueLobbyJoinRequested(string lobbyId)
        {
            lock (networkEventLock)
            {
                pendingLobbyJoinRequested = true;
            }
        }

    }
}
