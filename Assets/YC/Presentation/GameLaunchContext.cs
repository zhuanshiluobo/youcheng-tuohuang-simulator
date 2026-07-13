using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        private void OnNetworkDisconnected()
        {
            if (returningToStart) return;
            returningToStart = true;
            ShutdownOnlineSession();
            SceneManager.LoadScene("StartScene");
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
