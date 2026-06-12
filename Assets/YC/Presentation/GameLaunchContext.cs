using System.Collections.Generic;
using YC.Application.Sessions;
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
            Mode = mode;
            LocalPlayerId = localPlayerId;
            RoomId = roomId ?? string.Empty;
            Players.Clear();

            if (players == null)
            {
                return;
            }

            for (var i = 0; i < players.Count; i++)
            {
                var seat = players[i];
                Players.Add(new PlayerSeat
                {
                    PlayerId = seat.PlayerId,
                    NetcodeClientId = seat.NetcodeClientId,
                    PlayerName = seat.PlayerName,
                    Color = seat.Color,
                    IsReady = seat.IsReady
                });
            }
        }
    }
}
