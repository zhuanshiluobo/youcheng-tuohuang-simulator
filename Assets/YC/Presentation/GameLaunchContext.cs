using System;
using System.Collections.Generic;
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
        public string RoomId = string.Empty;
        public List<PlayerSeat> Players = new List<PlayerSeat>();
        private bool returningToStart;
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

        public void Configure(LaunchMode mode, int localPlayerId, string roomId, IList<PlayerSeat> players)
        {
            UnsubscribeDisconnect();
            UnsubscribeRoomService();
            returningToStart = false;
            completedSessionDetached = false;
            pendingReturnScene = null;
            pendingOnlineSessionNotice = null;
            Mode = mode;
            LocalPlayerId = localPlayerId;
            RoomId = roomId ?? string.Empty;
            Players.Clear();

            if (players != null)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var seat = players[i];
                    Players.Add(new PlayerSeat
                    {
                        PlayerId = seat.PlayerId,
                        SteamId = seat.SteamId,
                        NetworkClientId = seat.NetworkClientId,
                        PlayerName = seat.PlayerName,
                        Color = seat.Color,
                        LobbyMemberPresent = seat.LobbyMemberPresent,
                        TransportConnected = seat.TransportConnected,
                        IdentityVerified = seat.IdentityVerified,
                        GameStateSynchronized = seat.GameStateSynchronized,
                        IsReady = seat.IsReady
                    });
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
            ShutdownOnlineSession();
            SceneTransitionContext.TryBeginBlackTransition(sceneName);
        }

        private void OnNetworkDisconnected()
        {
            if (returningToStart || completedSessionDetached) return;
            if (MirrorCommandTransport.Instance != null &&
                MirrorCommandTransport.Instance.HasCompletedSettlement)
            {
                // 最终结果已在本机，即使断线先于 Update 到达也不能自动返回主页。
                DetachCompletedSession();
                return;
            }
            returningToStart = true;
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
            if (Instance != null)
            {
                Instance.UnsubscribeDisconnect();
                Instance.UnsubscribeRoomService();
                Instance.returningToStart = true;
                Instance.pendingOnlineSessionNotice = null;
                Instance.Mode = LaunchMode.Local;
                Instance.LocalPlayerId = 1;
                Instance.RoomId = string.Empty;
                Instance.Players.Clear();
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
