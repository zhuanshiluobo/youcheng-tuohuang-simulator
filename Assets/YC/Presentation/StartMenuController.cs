using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
        private bool roomRequestPending;
        private int roomNetworkingRequestVersion;
        private GameObject roomPanel;
        private InputField joinRoomInput;
        private Text roomStatusText;
        [NonSerialized] private RoomState pendingRoomUpdate;
        [NonSerialized] private RoomState pendingGameStart;
        private string pendingNetworkError;
        private bool pendingRoomDisbanded;
        private bool pendingLobbyJoinRequested;
        private int selectedRoomPlayerCount = 4;
        [SerializeField] private string roomReadyCaption = "准备";
        [SerializeField] private string roomCancelReadyCaption = "取消准备";
        [SerializeField] private string roomConfigurationSummaryFormat = "{0} · {1} · {2} · {3} 人 · 修订 {4}";
        [SerializeField] private string roomThreePlayerMapCaption = "三人地图";
        [SerializeField] private string roomFourPlayerMapCaption = "四人地图";
        [SerializeField] private string roomStandardRuleCaption = "标准模式";
        [SerializeField] private string roomCoreExpansionCaption = "基础";
        [SerializeField] private string roomEnterpriseExpansionCaption = "企业";
        [SerializeField] private string roomInfectedExpansionCaption = "感染者";
        [SerializeField] private string roomPreparedCaption = "已准备";
        [SerializeField] private string roomUnpreparedCaption = "未准备";
        [SerializeField] private string roomCanStartCaption = "所有成员已准备，可以开始游戏。";
        [SerializeField] private string roomWaitingForHostCaption = "等待房主开始游戏。";
        [Serializable] private sealed class RoomReasonCaption
        {
            public string ReasonCode;
            public string Caption;
            public RoomReasonCaption(string code, string caption) { ReasonCode = code; Caption = caption; }
        }
        [SerializeField] private List<RoomReasonCaption> roomReasonCaptions = new List<RoomReasonCaption>
        {
            new RoomReasonCaption("not_room_host", "只有房主可以执行此操作。"),
            new RoomReasonCaption("configuration_stale", "房间配置已变化，请按最新配置重试。"),
            new RoomReasonCaption("unsupported_player_count", "当前实际人数不满足开局条件。"),
            new RoomReasonCaption("map_requires_four_player", "四名玩家必须使用四人地图。"),
            new RoomReasonCaption("missing_enterprise_dependency", "启用感染者需要同时启用企业。"),
            new RoomReasonCaption("rule_mode_not_implemented", "所选规则模式尚未实现。"),
            new RoomReasonCaption("content_not_ready", "所选内容组合或人数配方尚未完成。"),
            new RoomReasonCaption("content_version_mismatch", "玩家的内容或规则版本与房间不一致。"),
            new RoomReasonCaption("player_not_ready", "等待成员连接、身份验证及当前配置准备。"),
            new RoomReasonCaption("seat_occupied", "目标座位已占用或已关闭。"),
            new RoomReasonCaption("seat_swap_stale", "换座请求已失效，请重新申请。"),
            new RoomReasonCaption("service_unavailable", "该服务暂不可用。"),
            new RoomReasonCaption("room_protocol_mismatch", "房间协议不匹配，请使用相同版本。"),
            new RoomReasonCaption("legacy_entity_state_unsupported", "旧对局状态无法可靠恢复。")
        };
        private string roomHostStartCaption;
        private int selectedLocalCityCount = 1;
        private bool loadingGame;
        [System.NonSerialized] private MatchSaveData resumeArchive;
        public bool CanOpenMatchSave => !loadingGame && !joiningRoom && roomPanel == null;
        public void RefreshMatchSaveAvailability()
        { if (view != null) view.SetMatchSaveAvailable(MatchSaveController.Ensure().HasSaves); }
        public async System.Threading.Tasks.Task RestoreMatch(MatchSaveData data)
        {
            if (!CanOpenMatchSave) throw new InvalidOperationException("已有房间或启动流程正在进行。");
            data.Validate();
            GameLaunchContext.Ensure().ClearRestore(); resumeArchive = null;
            if (data.Mode == LaunchMode.Local)
            {
                roomService.Shutdown(); GameLaunchContext.Ensure().ConfigureSavedLocal(data); BeginMapSceneLoad();
                if (!loadingGame) throw new InvalidOperationException("无法启动存档地图。");
                return;
            }
            if (data.LocalTestNetwork != LocalMirrorTestMode.IsEnabled) throw new InvalidOperationException("此存档的联机后端与当前模式不一致。");
            joiningRoom = true;
            try
            {
                if (!(roomService is IResumableOnlineRoomService resumable)) throw new InvalidOperationException("当前房间服务不支持续局。");
                var room = await resumable.CreateResumeRoomAsync(ResumeRoomOptions.FromSave(data));
                if (this == null) { roomService.Shutdown(); return; }
                resumeArchive = data; ShowRoomPanel(room, true);
            }
            catch { resumeArchive = null; roomService.Shutdown(); throw; }
            finally { joiningRoom = false; }
        }
        private bool joiningRoom;
        private bool autoStartLocalMirrorGame;
        private bool autoStartLocalMirrorRequestPending;
        private float nextAutoStartLocalMirrorAttemptTime;
        private string devLocalMirrorRoomOutputPath = string.Empty;
        private bool mapSelectionCreatesOnlineRoom;
        private string configuredLocalMapSelectionTitle;

        private void Awake()
        {
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

            ProcessPendingLobbyJoinRequest();
        }

        private void OnEnable() => BindRoomService();

        private void OnDisable() => UnbindRoomServiceEvents();

        private void BindRoomService()
        {
            // 热重载只恢复服务和订阅，不重建菜单或关闭现有房间。
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
                selectedRoomPlayerCount = Mathf.Clamp(hostPlayerCount, 2, 4);
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

        public void StartGame()
        {
            if (loadingGame)
            {
                return;
            }

            var seats = new List<PlayerSeat>();
            var colors = new[] { PlayerColor.Green, PlayerColor.Yellow, PlayerColor.Blue, PlayerColor.Red };
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
            var configuration = new GameRuleConfiguration
            {
                MapId = YC.Domain.Maps.StaticMapDefinitions.ForPlayerCount(selectedRoomPlayerCount).MapId,
                PlayerCount = seats.Count
            };
            configuration.ContentPackHash = OnlineRoomServiceProvider.ContentCapabilities.GetContentPackHash(configuration);
            GameLaunchContext.Ensure().ConfigureNewLocal(1, CreateLocalGameSeedSource(), seats, configuration, GetExplicitLocalGameSeed());
            BeginMapSceneLoad();
        }

        private static int? GetExplicitLocalGameSeed()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var seed = GetCommandLineValue(Environment.GetCommandLineArgs(), "--yc-player-journey-seed=");
            if (YC.PlayerJourney.PlayerJourneyRuntime.IsRunning && !string.IsNullOrEmpty(seed))
                return int.TryParse(seed, out var numericSeed) ? numericSeed : YC.Domain.Cards.EventDeckService.CreateSeed(LocalGameSeedSourcePrefix + seed);
#endif
            return null;
        }

        private static string CreateLocalGameSeedSource()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var seed = GetCommandLineValue(Environment.GetCommandLineArgs(), "--yc-player-journey-seed=");
            if (YC.PlayerJourney.PlayerJourneyRuntime.IsRunning && !string.IsNullOrEmpty(seed))
                return LocalGameSeedSourcePrefix + seed;
#endif
            return LocalGameSeedSourcePrefix + Guid.NewGuid().ToString("N");
        }

        public async void CreateRoom()
        {
            if (joiningRoom)
            {
                return;
            }

            joiningRoom = true;
            ShowRoomProgressPanel(
                "创建房间",
                LocalMirrorTestMode.IsEnabled ? "正在创建 Mirror 本地测试房间..." : "正在创建本地房间...");

            try
            {
                var room = await roomService.CreateRoomAsync(
                    LocalMirrorTestMode.IsEnabled ? LocalMirrorTestMode.PlayerName : "房主",
                    selectedRoomPlayerCount);
                var configured = await roomService.ChangeConfigurationAsync(new ChangeRoomConfigurationRequest
                {
                    CommandId = Guid.NewGuid().ToString("N"), RoomId = room.RoomId,
                    RequestingPlayerId = room.LocalPlayerId, ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision,
                    MapId = selectedRoomPlayerCount == 4 ? YC.Domain.Maps.StaticMapDefinitions.FourPlayerMapId : YC.Domain.Maps.StaticMapDefinitions.ThreePlayerMapId,
                    RuleModeId = "standard", EnabledExpansionIds = new List<string> { "core" }
                });
                if (!configured.Success) throw new InvalidOperationException(configured.ReasonCode);
                room = configured.Snapshot;
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
            // 存读档另项适配，当前构建不开放尚未接线的入口。
            RefreshMatchSaveAvailability();
            return true;
        }

        private void BindStaticUi()
        {
            if (view.LoadGameButton != null) BindButton(view.LoadGameButton, () => MatchSaveWindowView.OpenLoad(this));
            BindButton(view.StartGameButton, CreateStandardRoom);
            YC.PlayerJourney.PlayerAutomationId.Attach(view.StartGameButton.gameObject, "start.local_game");
            // 地图选择已改为人数与地图轮换面板，确认按钮承担原四人地图按钮的自动化入口。
            YC.PlayerJourney.PlayerAutomationId.Attach(view.MapSelectionPanel.StartButton.gameObject, "start.four_player_map");
            BindButton(view.OnlineModeButton, JoinRoom);
            BindButton(view.AchievementsButton, ShowAchievementsPanel);
            BindButton(view.QuitGameButton, QuitGame);
            BindButton(view.CreatorSiteButton, () => UnityEngine.Application.OpenURL(CreatorSiteUrl));
            BindButton(view.OfficialSiteButton, () => UnityEngine.Application.OpenURL(OfficialSiteUrl));
            BindButton(view.WikiButton, () => UnityEngine.Application.OpenURL(WikiUrl));

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
            view.RoomPanel.SetServiceAvailability(roomService.SupportsFriendInvites);
            view.RoomPanel.MapSelectionRequested += id => ChangeRoomConfiguration(id, null, null, false);
            view.RoomPanel.RuleModeSelectionRequested += id => ChangeRoomConfiguration(null, id, null, false);
            view.RoomPanel.ExpansionSelectionRequested += (id, enabled) => ChangeRoomConfiguration(null, null, id, enabled);
            view.RoomPanel.ReadyRequested += ready => SendRoomRequest(new SetRoomReadyRequest { IsUserReady = ready }, roomService.SetReadyAsync);
            view.RoomPanel.StartRequested += () => SendRoomRequest(new StartConfiguredGameRequest(), roomService.StartConfiguredGameAsync);
            view.RoomPanel.SeatStatusRequested += (index, status) => SendRoomRequest(new SetRoomSeatStatusRequest { SeatIndex = index, Status = status }, roomService.SetSeatStatusAsync);
            view.RoomPanel.SeatSwapRequested += index => SendRoomRequest(new RequestRoomSeatSwapRequest { TargetSeatIndex = index }, roomService.RequestSeatSwapAsync);
            view.RoomPanel.SeatSwapConfirmationRequested += (id, accepted) => SendRoomRequest(new ConfirmRoomSeatSwapRequest { SwapRequestId = id, Accepted = accepted }, roomService.ConfirmSeatSwapAsync);
            view.RoomPanel.InvitationRequested += InviteSteamFriends;
            view.RoomPanel.NetworkingRequested += ToggleRoomNetworking;
            view.RoomPanel.LocalPlayerAdditionRequested += (index, name) =>
            {
                if (roomService is ILocalRoomPlayerControl local)
                    SendRoomRequest(new AddLocalRoomPlayerRequest { SeatIndex = index, PlayerName = name }, local.AddLocalPlayerAsync);
            };
            view.RoomPanel.LocalPlayerRemovalRequested += index =>
            {
                if (roomService is ILocalRoomPlayerControl local)
                    SendRoomRequest(new RemoveLocalRoomPlayerRequest { SeatIndex = index }, local.RemoveLocalPlayerAsync);
            };
            BindButton(view.RoomPanel.BackButton, HideRoomPanel);
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
            panel.TitleText.text = createsOnlineRoom ? panel.OnlineModeTitle : configuredLocalMapSelectionTitle;
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
            panel.ShowConnectionType(LocalMirrorTestMode.IsEnabled);
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning(ex.ToString());
#endif
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

            var panel = view.RoomPanel;
            if (roomPanel != panel.gameObject || !panel.gameObject.activeSelf)
            {
                view.HideRoomPanels();
                panel.gameObject.SetActive(true);
            }
            roomPanel = panel.gameObject;
            roomStatusText = panel.StatusText;

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

            panel.SetServiceAvailability(roomService.SupportsFriendInvites);
            var networking = roomService as IRoomNetworkingControl;
            panel.SetNetworkingState(networking == null || networking.IsOnline, networking != null);
            var supportsLocalPlayers = roomService is ILocalRoomPlayerControl local && local.SupportsLocalPlayers;
            panel.SetLocalPlayerAvailability(supportsLocalPlayers);
            panel.Render(room, room.LocalPlayerId);

            if (autoStartLocalMirrorGame && hostControls && panel.CanStartCurrentRoom)
            {
                TryAutoStartLocalMirrorGame();
            }
        }

        private void ChangeRoomConfiguration(string mapId, string ruleModeId, string expansionId, bool enabled)
        {
            var room = view.RoomPanel.DisplayedRoom;
            if (room == null || room.LocalPlayerId != room.HostPlayerId) return;
            var selected = new List<string>(room.Configuration.EnabledExpansionIds);
            if (expansionId != null)
            {
                if (enabled && !selected.Contains(expansionId)) selected.Add(expansionId);
                else if (!enabled) selected.Remove(expansionId);
            }
            SendRoomRequest(new ChangeRoomConfigurationRequest
            {
                MapId = mapId ?? room.Configuration.MapId,
                RuleModeId = ruleModeId ?? room.Configuration.RuleModeId,
                EnabledExpansionIds = selected
            }, roomService.ChangeConfigurationAsync);
        }

        private async void SendRoomRequest<T>(T request, Func<T, Task<RoomConfigurationResult>> send) where T : RoomConfigurationRequest
        {
            var room = view.RoomPanel.DisplayedRoom;
            if (roomRequestPending || room == null) return;
            var hostOnly = request is ChangeRoomConfigurationRequest || request is SetRoomSeatStatusRequest || request is StartConfiguredGameRequest ||
                request is AddLocalRoomPlayerRequest || request is RemoveLocalRoomPlayerRequest;
            if (hostOnly && room.LocalPlayerId != room.HostPlayerId) return;
            request.CommandId = Guid.NewGuid().ToString("N");
            request.RoomId = room.RoomId;
            request.RequestingPlayerId = room.LocalPlayerId;
            request.ExpectedConfigurationRevision = room.Configuration.ConfigurationRevision;
            roomRequestPending = true;
            view.RoomPanel.SetRequestPending(true);
            RoomConfigurationResult result;
            try { result = await send(request); }
            catch (Exception ex) { result = new RoomConfigurationResult { Success = false, ReasonCode = ex.Message }; }
            if (this == null) return;
            roomRequestPending = false;
            view.RoomPanel.SetRequestPending(false);
            if (roomPanel == view.RoomPanel.gameObject && view.RoomPanel.DisplayedRoom?.RoomId == request.RoomId)
                view.RoomPanel.ShowCommandResult(result);
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
                room.PlayerCount != selectedRoomPlayerCount ||
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

        private void InviteSteamFriends(int seatIndex)
        {
            try
            {
                roomService.InviteFriends();
                view.RoomPanel.ShowInvitationEntryOpened(seatIndex);
            }
            catch (Exception ex)
            {
                SetRoomStatus("无法打开 Steam 好友邀请：" + ex.Message);
            }
        }

        private async void ToggleRoomNetworking(bool enabled)
        {
            if (roomRequestPending || !(roomService is IRoomNetworkingControl networking)) return;
            var current = view.RoomPanel.DisplayedRoom;
            if (current == null || current.LocalPlayerId != current.HostPlayerId) return;
            var operation = ++roomNetworkingRequestVersion;
            roomRequestPending = true;
            view.RoomPanel.SetRequestPending(true);
            string error = null;
            try { await networking.SetOnlineAsync(enabled); }
            catch (Exception ex) { error = ex.Message; }
            if (this == null || operation != roomNetworkingRequestVersion) return;
            roomRequestPending = false;
            view.RoomPanel.SetRequestPending(false);
            if (roomPanel != view.RoomPanel.gameObject) return;
            var room = roomService.GetCurrentRoom();
            if (room != null) ShowRoomPanel(room, room.LocalPlayerId == room.HostPlayerId);
            if (!string.IsNullOrEmpty(error)) SetRoomStatus(view.RoomPanel.ResolveReason(error));
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

            var mode = roomService is IRoomNetworkingControl networking && !networking.IsOnline
                ? LaunchMode.Local : localPlayerId == room.HostPlayerId ? LaunchMode.Host : LaunchMode.Client;
            if (!room.HasStarted || !RoomStateSerializer.TryValidate(room, out _))
            {
                SetRoomStatus(view.RoomPanel.ResolveReason("room_protocol_mismatch"));
                return;
            }
            if (room.Resume != null)
                GameLaunchContext.Ensure().ConfigureSavedRoom(mode, localPlayerId, room, mode == LaunchMode.Host ? resumeArchive : null);
            else GameLaunchContext.Ensure().Configure(mode, localPlayerId, room.RoomId, room.Seats, room.Configuration.Clone(), room.LaunchPlayerOrder,
                room.GameId, room.EventDeckSeed, room.RandomRuntime);
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
            roomNetworkingRequestVersion++;
            roomRequestPending = false;
            view.RoomPanel.SetRequestPending(false);
            lock (networkEventLock) { pendingRoomUpdate = null; pendingGameStart = null; }
            view.HideRoomPanels();
            roomPanel = null;
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
            panel.MessageText.text = string.IsNullOrEmpty(message) ? panel.UnknownErrorMessage : ResolveRoomReasonCaption(message);
            panel.ActionButton.gameObject.SetActive(true);
            panel.ActionButtonText.text = panel.OperationErrorActionLabel;
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
            panel.TitleText.text = panel.RoomDisbandedTitle;
            panel.MessageText.text = panel.RoomDisbandedMessage;
            panel.ActionButton.gameObject.SetActive(true);
            panel.ActionButtonText.text = panel.RoomDisbandedActionLabel;
            BindButton(panel.ActionButton, ConfirmRoomDisbanded);
        }

        private void ConfirmRoomDisbanded()
        {
            view.HideRoomPanels();
            roomPanel = null;
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
                roomStatusText.text = ResolveRoomReasonCaption(message);
            }
        }

        private string ResolveRoomReasonCaption(string code)
        {
            var caption = roomReasonCaptions.Find(item => item != null && item.ReasonCode == code);
            return caption == null ? code : caption.Caption;
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
