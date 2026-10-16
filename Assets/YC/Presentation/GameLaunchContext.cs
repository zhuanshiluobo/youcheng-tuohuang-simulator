using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Rules;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using UnityEngine;

namespace YC.Presentation
{
    public sealed class GameLaunchContext : MonoBehaviour
    {
        public static GameLaunchContext Instance { get; private set; }

        public LaunchMode Mode = LaunchMode.Local;
        public int LocalPlayerId = 1;
        public string MapId = YC.Domain.Maps.StaticMapDefinitions.FourPlayerMapId;
        public string RoomId = string.Empty;
        public string GameId = string.Empty;
        public int EventDeckSeed;
        public GameplayRandomState InitialRandomState;
        public bool RandomReady => GameplayRandomRuntime.TryValidate(InitialRandomState, GameId, EventDeckSeed, out _);
        public List<PlayerSeat> Players = new List<PlayerSeat>();
        public List<int> LaunchPlayerOrder = new List<int>();
        public YC.Domain.Rules.GameRuleConfiguration RuleConfiguration;
        [NonSerialized] public MatchSaveData RestoreData;
        [NonSerialized] public ResumeRoomOptions ResumeInfo;
        public void ClearRestore() { RestoreData = null; ResumeInfo = null; }
        public void ConfigureSavedLocal(MatchSaveData data)
        {
            data.Validate();
            if (data.Mode != LaunchMode.Local) throw new InvalidOperationException("请选择本地对局存档。");
            GameId = string.Empty;
            Configure(LaunchMode.Local, data.LocalPlayerId, data.GameId, data.Seats.ConvertAll(s => s.ToSeat()),
                data.Configuration, data.LaunchPlayerOrder, data.GameId, data.EventDeckSeed, data.RandomState);
            RestoreData = data;
        }
        public void ConfigureSavedRoom(LaunchMode mode, int playerId, RoomState room, MatchSaveData data)
        {
            room.Resume.Validate();
            if (!room.HasStarted || mode == LaunchMode.Host && (data == null || data.GameId != room.Resume.GameId) ||
                mode != LaunchMode.Host && mode != LaunchMode.Client) throw new InvalidOperationException("续局启动上下文无效。");
            GameId = string.Empty;
            Configure(mode, playerId, room.RoomId, room.Seats, room.Configuration, room.LaunchPlayerOrder,
                room.GameId, room.EventDeckSeed, room.RandomRuntime);
            RestoreData = data; ResumeInfo = room.Resume.Clone();
        }
        private bool returningToStart;
        public bool IsLeavingGameplay => returningToStart;
        private bool completedSessionDetached;
        private string pendingReturnScene;
        private MirrorNetworkRuntime subscribedRuntime;
        private IOnlineRoomService subscribedRoomService;
        private string pendingOnlineSessionNotice;

        public event Action<string> OnlineSessionNotice;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public static GameLaunchContext Ensure()
        {
            if (Instance != null)
            {
                return Instance;
            }

            var contextObject = new GameObject("GameLaunchContext");
            return contextObject.AddComponent<GameLaunchContext>();
        }

        public void Configure(LaunchMode mode, int localPlayerId, string roomId, IList<PlayerSeat> players, string mapId = null)
        {
            Configure(mode, localPlayerId, roomId, players, new YC.Domain.Rules.GameRuleConfiguration
            {
                MapId = mapId ?? YC.Domain.Maps.StaticMapDefinitions.FourPlayerMapId,
                PlayerCount = players == null || players.Count == 0 ? 1 : players.Count
            });
        }

        public void Configure(LaunchMode mode, int localPlayerId, string roomId, IList<PlayerSeat> players,
            YC.Domain.Rules.GameRuleConfiguration configuration)
            => Configure(mode, localPlayerId, roomId, players, configuration, null);

        public void Configure(LaunchMode mode, int localPlayerId, string roomId, IList<PlayerSeat> players,
            YC.Domain.Rules.GameRuleConfiguration configuration, IReadOnlyList<int> launchPlayerOrder)
        {
            if (mode != LaunchMode.Local) throw new InvalidOperationException("random_initialization_required");
            ConfigureNewLocal(localPlayerId, roomId, players, configuration, null, launchPlayerOrder);
        }

        public void ConfigureNewLocal(int localPlayerId, string roomId, IList<PlayerSeat> players,
            GameRuleConfiguration configuration, int? explicitSeed = null, IReadOnlyList<int> explicitOrder = null)
        {
            if (configuration == null || !configuration.TryValidate(out _)) throw new InvalidOperationException("room_protocol_mismatch");
            var ids = players == null || players.Count == 0 ? new List<int> { localPlayerId } : players.Select(p => p.PlayerId).ToList();
            if (ids.Count != configuration.PlayerCount || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
                throw new InvalidOperationException("unsupported_player_count");
            var gameId = Guid.NewGuid().ToString("N");
            int seed = explicitSeed ?? GameplayRandomSeed.CreateMatchSeed();
            var state = new YC.Domain.State.GameState { GameId = gameId, EventDeckSeed = seed, RandomRuntime = GameplayRandomRuntime.Create(gameId) };
            if (explicitOrder == null) GameplayRandomRuntime.Shuffle(ids, state, GameplayRandomRuntime.LaunchOrder);
            var seatCopies = players == null ? null : players.Select(p => p.Clone()).ToList();
            Configure(LaunchMode.Local, localPlayerId, roomId, seatCopies, configuration, explicitOrder ?? ids, gameId, seed, state.RandomRuntime);
        }

        public void Configure(LaunchMode mode, int localPlayerId, string roomId, IList<PlayerSeat> players,
            GameRuleConfiguration configuration, IReadOnlyList<int> launchPlayerOrder, string gameId, int seed, GameplayRandomState initialRandomState)
        {
            if (!GameplayRandomRuntime.TryValidate(initialRandomState, gameId, seed, out var randomReason))
                throw new InvalidOperationException(randomReason);
            if (GameId == gameId)
            {
                if (!RandomReady || EventDeckSeed != seed || InitialRandomState.AlgorithmVersion != initialRandomState.AlgorithmVersion ||
                    Mode != mode || LocalPlayerId != localPlayerId || RoomId != (roomId ?? string.Empty) ||
                    Newtonsoft.Json.JsonConvert.SerializeObject(RuleConfiguration) != Newtonsoft.Json.JsonConvert.SerializeObject(configuration) ||
                    launchPlayerOrder == null || !LaunchPlayerOrder.SequenceEqual(launchPlayerOrder) ||
                    !YC.Domain.State.GameStateCloneService.AreEquivalent(
                        new YC.Domain.State.GameState { RandomRuntime = InitialRandomState }, new YC.Domain.State.GameState { RandomRuntime = initialRandomState }))
                    throw new InvalidOperationException("random_initialization_conflict");
                return;
            }
            if (configuration == null) throw new InvalidOperationException("room_protocol_mismatch");
            if (!configuration.TryValidate(out var reason)) throw new InvalidOperationException(reason);
            if (configuration.PlayerCount != (players == null || players.Count == 0 ? 1 : players.Count))
                throw new InvalidOperationException("unsupported_player_count");
            if (launchPlayerOrder != null && !YC.Domain.Rules.TurnOrderService.IsValidPlayerOrder(
                launchPlayerOrder, players == null ? new List<int> { localPlayerId } : new List<PlayerSeat>(players).ConvertAll(player => player.PlayerId)))
                throw new InvalidOperationException("launch_player_order_invalid");
            if (launchPlayerOrder == null) throw new InvalidOperationException("launch_player_order_invalid");
            GameId = gameId;
            ClearRestore();
            EventDeckSeed = seed;
            InitialRandomState = initialRandomState.Clone();
            LaunchPlayerOrder = new List<int>(launchPlayerOrder);
            RuleConfiguration = configuration.Clone();
            UnsubscribeDisconnect();
            UnsubscribeRoomService();
            returningToStart = false;
            completedSessionDetached = false;
            pendingReturnScene = null;
            pendingOnlineSessionNotice = null;
            MapId = RuleConfiguration.MapId;
            Mode = mode;
            LocalPlayerId = localPlayerId;
            RoomId = roomId ?? string.Empty;
            Players.Clear();

            if (players != null)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var seat = players[i];
                    Players.Add(seat.Clone());
                }
            }

            if (mode != LaunchMode.Local)
            {
                subscribedRuntime = MirrorNetworkRuntime.Instance;
                if (subscribedRuntime != null)
                    subscribedRuntime.ClientDisconnected += OnNetworkDisconnected;

                subscribedRoomService = OnlineRoomServiceProvider.GetActive();
                if (subscribedRoomService != null)
                    subscribedRoomService.LobbyJoinRequested += OnLobbyJoinRequested;
            }
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            UnsubscribeDisconnect();
            UnsubscribeRoomService();
            Instance = null;
        }

        private void Update()
        {
            if (returningToStart || completedSessionDetached || Mode == LaunchMode.Local) return;
            var transport = MirrorCommandTransport.Instance;
            if (transport != null && transport.CanDetachCompletedSession)
                DetachCompletedSession();
        }

        private void DetachCompletedSession()
        {
            if (completedSessionDetached) return;
            completedSessionDetached = true;
            // 保留本机玩家身份和结算状态，仅切断联机；不切场景，也不重建单人对局。
            UnsubscribeDisconnect();
            UnsubscribeRoomService();
            pendingOnlineSessionNotice = null;
            MirrorCommandTransport.Instance?.Shutdown();
            OnlineRoomServiceProvider.ShutdownActive();
            if (!string.IsNullOrEmpty(pendingReturnScene)) ReturnToStartScene(pendingReturnScene);
        }

        public static void ReturnToStartScene(string sceneName)
        {
            var transport = MirrorCommandTransport.Instance;
            if (Instance != null && !Instance.completedSessionDetached &&
                transport != null && transport.HasCompletedSettlement &&
                !transport.CanDetachCompletedSession)
            {
                // 房主提前关闭时，等最终结果送达其余玩家再退出。
                Instance.pendingReturnScene = sceneName;
                return;
            }
            MatchSaveController.Ensure().RequestLeave(() =>
            {
                ShutdownOnlineSession();
                SceneTransitionContext.TryBeginBlackTransition(sceneName);
            });
        }

        private void OnNetworkDisconnected()
        {
            if (returningToStart || completedSessionDetached) return;
            if (subscribedRuntime != null && subscribedRuntime.IsReconnectingLocalClient) return;
            if (MirrorCommandTransport.Instance != null &&
                MirrorCommandTransport.Instance.HasCompletedSettlement)
            {
                // 最终结果已在本机，即使断线先于 Update 到达也不能自动返回主页。
                DetachCompletedSession();
                return;
            }
            returningToStart = true;
            GameplayHudFrame.Active?.ClearRequest();
            foreach (var viewer in FindObjectsOfType<CardViewer>()) viewer.Dismiss();
            ShutdownOnlineSession();
            SceneTransitionContext.TryBeginBlackTransition("StartScene");
        }

        private void OnLobbyJoinRequested(string lobbyId)
        {
            if (returningToStart || Mode == LaunchMode.Local) return;
            const string notice = "当前对局正在进行，已保存新的 Steam 房间邀请；返回开始页后将自动加入。";
            pendingOnlineSessionNotice = notice;
            OnlineSessionNotice?.Invoke(notice);
            Debug.LogWarning(notice);
        }

        public bool TryConsumeOnlineSessionNotice(out string notice)
        {
            notice = pendingOnlineSessionNotice;
            pendingOnlineSessionNotice = null;
            return !string.IsNullOrEmpty(notice);
        }

        public static void ShutdownOnlineSession()
        {
            MatchSaveController.Instance?.Unbind();
            Instance?.ClearRestore();
            if (Instance != null)
            {
                Instance.UnsubscribeDisconnect();
                Instance.UnsubscribeRoomService();
                Instance.returningToStart = true;
                Instance.pendingOnlineSessionNotice = null;
                Instance.Mode = LaunchMode.Local;
                Instance.LocalPlayerId = 1;
                Instance.RoomId = string.Empty;
                Instance.GameId = string.Empty;
                Instance.EventDeckSeed = 0;
                Instance.InitialRandomState = null;
                Instance.Players.Clear();
                Instance.LaunchPlayerOrder.Clear();
                Instance.RuleConfiguration = null;
            }

            MirrorCommandTransport.Instance?.Shutdown();
            OnlineRoomServiceProvider.ShutdownActive();
        }

        private void UnsubscribeDisconnect()
        {
            if (subscribedRuntime != null)
                subscribedRuntime.ClientDisconnected -= OnNetworkDisconnected;
            subscribedRuntime = null;
        }

        private void UnsubscribeRoomService()
        {
            if (subscribedRoomService != null)
                subscribedRoomService.LobbyJoinRequested -= OnLobbyJoinRequested;
            subscribedRoomService = null;
        }
    }
}
