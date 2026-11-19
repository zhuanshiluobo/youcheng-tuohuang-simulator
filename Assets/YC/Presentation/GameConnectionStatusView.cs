using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Infrastructure.Multiplayer;

namespace YC.Presentation
{
    /// <summary>局内设置页只读显示连接事实，并将本机操作交给原身份恢复流程。</summary>
    public sealed class GameConnectionStatusView : MonoBehaviour
    {
        [SerializeField] private Text playerStatuses;
        [SerializeField] private Text reconnectHint;
        [SerializeField] private Button reconnectButton;
        [SerializeField] private string rowFormat;
        [SerializeField] private string connectedText;
        [SerializeField] private string disconnectedText;
        [SerializeField] private string reconnectingText;
        [SerializeField] private string verifyingText;
        [SerializeField] private string synchronizingText;
        [SerializeField] private string leftRoomText;
        [SerializeField] private string unknownText;
        [SerializeField] private string localGameText;
        [SerializeField] private string hostHint;
        [SerializeField] private string hostConnectedHint;
        [SerializeField] private string hostUnknownHint;
        [SerializeField] private string clientHint;
        [SerializeField] private string connectedHint;
        [SerializeField] private string waitingHint;
        [SerializeField] private string reconnectingHint;
        [SerializeField] private string synchronizingHint;
        [SerializeField] private string unavailableHint;
        private float nextRefresh;

        private void Awake()
        {
            if (reconnectButton != null) reconnectButton.onClick.AddListener(RequestReconnect);
        }

        private void OnEnable() { nextRefresh = 0; Refresh(); }
        private void OnDestroy()
        {
            if (reconnectButton != null) reconnectButton.onClick.RemoveListener(RequestReconnect);
        }
        private void Update()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .5f;
            Refresh();
        }

        public void Refresh()
        {
            if (playerStatuses == null || reconnectHint == null || reconnectButton == null) return;
            var launch = GameLaunchContext.Instance;
            if (launch == null || launch.Mode == LaunchMode.Local)
            {
                playerStatuses.text = localGameText; reconnectHint.text = string.Empty; reconnectButton.interactable = false;
                return;
            }
            var runtime = MirrorNetworkRuntime.Instance;
            var transport = MirrorCommandTransport.Instance;
            bool connected = launch.Mode == LaunchMode.Host ? runtime != null && runtime.IsHost : runtime != null && runtime.IsClientTransportConnected;
            bool reconnecting = launch.Mode == LaunchMode.Client && runtime != null && runtime.IsReconnectingClient;
            bool recovering = transport != null && (transport.IsRecoveringConfirmedPrefix ||
                transport.UsesPrivateTransport && connected && !transport.IsPrivateRoomReady);
            bool waiting = transport != null && transport.IsCoordinatedReconnectPending;
            var room = OnlineRoomServiceProvider.GetActive()?.GetCurrentRoom();
            var rows = ConnectionStatusProjection.Read(launch.Mode, launch.LocalPlayerId, launch.GameId, launch.Players,
                room, connected, reconnecting, recovering);
            var lines = new List<string>(rows.Count);
            bool hostWaiting = false, hostUnknown = false;
            foreach (var row in rows)
            {
                string name = (row.PlayerName ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
                lines.Add((rowFormat ?? string.Empty).Replace("{1}", StatusText(row.Status)).Replace("{0}", name));
                if (row.PlayerId != launch.LocalPlayerId && row.Status == PlayerConnectionStatus.Unknown) hostUnknown = true;
                else if (row.PlayerId != launch.LocalPlayerId && row.Status != PlayerConnectionStatus.Connected) hostWaiting = true;
            }
            playerStatuses.text = lines.Count == 0 ? unknownText : string.Join("\n", lines);
            bool canReconnect = launch.Mode == LaunchMode.Client && transport != null && transport.CanRequestLocalConnectionRecovery;
            reconnectButton.interactable = canReconnect;
            reconnectHint.text = launch.Mode == LaunchMode.Host ? (hostWaiting ? hostHint : hostUnknown ? hostUnknownHint : hostConnectedHint) : reconnecting ? reconnectingHint : waiting ? waitingHint :
                recovering ? synchronizingHint : canReconnect ? clientHint : connected ? connectedHint : unavailableHint;
        }

        private string StatusText(PlayerConnectionStatus status)
        {
            switch (status)
            {
                case PlayerConnectionStatus.Connected: return connectedText;
                case PlayerConnectionStatus.Disconnected: return disconnectedText;
                case PlayerConnectionStatus.Reconnecting: return reconnectingText;
                case PlayerConnectionStatus.Verifying: return verifyingText;
                case PlayerConnectionStatus.Synchronizing: return synchronizingText;
                case PlayerConnectionStatus.LeftRoom: return leftRoomText;
                default: return unknownText;
            }
        }

        private void RequestReconnect()
        {
            MirrorCommandTransport.Instance?.TryRequestLocalConnectionRecovery();
            Refresh();
        }
    }
}
