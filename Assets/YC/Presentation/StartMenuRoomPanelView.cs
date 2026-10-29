using System;
using System.Collections.Generic;
using YC.Infrastructure.Multiplayer;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed class StartMenuRoomPanelView : MonoBehaviour
    {
        private static readonly ProfilerMarker RoomRenderMarker = new ProfilerMarker("YC.RoomRender");
        [SerializeField] private Text roomCodeText;
        [SerializeField] private Button copyButton;
        [SerializeField] private Text validationText;
        [SerializeField] private RoomBodyResponsiveLayout responsiveLayout;
        [SerializeField] private RectTransform seatListRoot;
        [SerializeField] private Text seatTemplate;
        [SerializeField] private Text statusText;
        [SerializeField] private Button inviteButton;
        [SerializeField] private Text networkingCaption;
        [SerializeField] private string enableNetworkingCaption = "打开联机", disableNetworkingCaption = "关闭联机";
        [SerializeField] private string offlineRoomCaption = "本地房间", localHostCaption = "本机房主";
        [SerializeField] private Button startButton;
        [SerializeField] private Button backButton;

        [Serializable] public sealed class SeatBinding
        {
            public Text NameText, StateText;
            public SteamAvatarView Avatar;
            public GameObject HostBadge, InviteState;
            public Button SwapButton, InviteButton, CloseButton, OpenButton, RemoveButton, HotSeatButton, BotButton;
        }
        // 名称与规则 ID 是序列化内容配置，静态栏目标题仍属于 Prefab。
        [Serializable] public sealed class ContentOption
        {
            public string Id, DisplayName;
            public Button Button;
            public Image Selection;
            public Image Frame;
            public Sprite DefaultFrame, SelectedFrame, DisabledFrame;
            public GameObject DisabledOverlay;
        }
        [Serializable] public sealed class ReasonCaption
        {
            public string Code, Caption;
        }
        [SerializeField] private SeatBinding[] seats;
        [SerializeField] private ContentOption[] maps, ruleModes, expansions;
        [SerializeField] private ReasonCaption[] reasons;
        [SerializeField] private Text primaryCaption, permissionsText, mapHintText, swapPromptText;
        [SerializeField] private Button passwordButton, helpButton, helpCloseButton, swapAcceptButton, swapDeclineButton;
        [SerializeField] private GameObject helpPanel, swapPanel;
        [SerializeField] private Button[] swapTargets;
        [SerializeField] private GameObject[] swapHostBadges, swapEmptyBadges;
        [SerializeField] private Image primaryIcon;
        [SerializeField] private Sprite hostStartIcon, memberReadyIcon, memberCancelIcon;
        [SerializeField] private Button swapCloseButton;
        [SerializeField] private Button resumeSeatsButton;
        [SerializeField] private string resumePermissionCaption = "续局沿用存档配置与席位。房主可指定替补，全部成员到场并准备后开始。";
        [SerializeField] private string resumeWaitingCaption = "等待房主分配续局席位。";
        [SerializeField] private string resumeAbsentCaption = "保留席位，等待原玩家或替补到场";
        [SerializeField] private string roomCodeFormat = "房间号 {0}";
        [SerializeField] private string summaryFormat = "{0}\n{1}\n{2}\n{3} 位玩家";
        [SerializeField] private string expansionNameSeparator = "·";
        [SerializeField] private string seatNameFormat = "{0}号座位 · {1}{2}";
        [SerializeField] private string selfCaption = "（你）", emptyCaption = "开放空座", closedCaption = "已关闭";
        [SerializeField] private string readyCaption = "已准备", unreadyCaption = "未准备", hostCaption = "房主";
        [SerializeField] private string startCaption = "开始游戏", prepareCaption = "准备", cancelPrepareCaption = "取消准备";
        [SerializeField] private string hostPermissionCaption = "房主可修改配置；配置或座位变化后，成员需重新准备。";
        [SerializeField] private string memberPermissionCaption = "房间设置只读；准备即确认当前配置。";
        [SerializeField] private string fourPlayerHint = "满四人仅可使用四人地图；减员后保留当前地图。";
        [SerializeField] private string mapSelectionHint = "少于四人可选两种地图";
        [SerializeField] private string canStartCaption = "成员已确认当前配置，可以开始。";
        [SerializeField] private string waitingHostCaption = "等待房主开始游戏。";
        [SerializeField] private string swapPromptFormat = "{0} 请求与你交换座位。";
        [SerializeField] private string swapPendingCaption = "换座请求已发送，等待对方确认。";
        [SerializeField] private string invitingCaption = "邀请入口已打开，成员尚未入座";
        [SerializeField] private string connectedCaption = "连接与身份已验证", connectingCaption = "等待连接与身份验证";
        [SerializeField] private string localPlayerNameFormat = "本地玩家 {0}";
        [SerializeField] private string localPlayerCaption = "本机热座", localReadyCaption = "由房主统一开始";
        [SerializeField] private string localPermissionCaption = "本机玩家由房主统一开始；切换联机前请移除热座玩家。";
        [SerializeField] private string localCanStartCaption = "本地玩家已入座，可以开始热座游戏。";
        [SerializeField] private string hotseatRequiresOfflineCaption = "热座仅支持本地房间；打开联机前请移除热座玩家。";
        private RoomState renderedRoom;
        private int viewerId;
        private bool requestPending, supportsInvites, bound;
        private bool online = true, canToggleNetworking;
        private bool supportsLocalPlayers;
        private readonly HashSet<int> invitedSeats = new HashSet<int>();

        public event Action<string> MapSelectionRequested, RuleModeSelectionRequested;
        public event Action<string, bool> ExpansionSelectionRequested;
        public event Action<bool> ReadyRequested;
        public event Action StartRequested;
        public event Action<bool> NetworkingRequested;
        public event Action<int> InvitationRequested;
        public event Action<int, string> LocalPlayerAdditionRequested;
        public event Action<int> LocalPlayerRemovalRequested;
        public event Action<int, string> SeatStatusRequested;
        public event Action<int> SeatSwapRequested;
        public event Action<string, bool> SeatSwapConfirmationRequested;

        public void SetServiceAvailability(bool friendInvites) { supportsInvites = friendInvites; }
        public void SetLocalPlayerAvailability(bool available) { supportsLocalPlayers = available; }
        public void SetNetworkingState(bool isOnline, bool canToggle)
        { online = isOnline; canToggleNetworking = canToggle; }
        public void SetRequestPending(bool pending)
        {
            requestPending = pending;
            if (renderedRoom != null) Render(renderedRoom, viewerId);
        }

        private void BindRequests()
        {
            if (bound) return;
            bound = true;
            foreach (var option in maps)
                option.Button.onClick.AddListener(() => { if (CanConfigure && option.Button.interactable) MapSelectionRequested?.Invoke(option.Id); });
            foreach (var option in ruleModes)
                option.Button.onClick.AddListener(() => { if (CanConfigure) RuleModeSelectionRequested?.Invoke(option.Id); });
            foreach (var option in expansions)
                option.Button.onClick.AddListener(() =>
                {
                    if (CanConfigure && option.Id != "core")
                        ExpansionSelectionRequested?.Invoke(option.Id, !renderedRoom.Configuration.EnabledExpansionIds.Contains(option.Id));
                });
            startButton.onClick.AddListener(() =>
            {
                if (requestPending || renderedRoom == null || !startButton.interactable) return;
                if (viewerId == renderedRoom.HostPlayerId) StartRequested?.Invoke();
                else ReadyRequested?.Invoke(RoomConfigurationService.FindSlot(renderedRoom, viewerId)?.IsUserReady != true);
            });
            copyButton.onClick.AddListener(() =>
            {
                if (renderedRoom == null || !online) return;
                GUIUtility.systemCopyBuffer = renderedRoom.RoomId;
            });
            inviteButton.onClick.AddListener(() => { if (CanConfigure && canToggleNetworking) NetworkingRequested?.Invoke(!online); });
            if (resumeSeatsButton != null) resumeSeatsButton.onClick.AddListener(MatchSaveWindowView.OpenSeats);
            helpButton.onClick.AddListener(() => helpPanel.SetActive(true));
            helpCloseButton.onClick.AddListener(() => helpPanel.SetActive(false));
            swapCloseButton.onClick.AddListener(() => swapPanel.SetActive(false));
            for (var i = 0; i < seats.Length; i++)
            {
                var index = i;
                seats[i].SwapButton.onClick.AddListener(ShowSwapSelection);
                seats[i].CloseButton.onClick.AddListener(() => { if (CanConfigure) SeatStatusRequested?.Invoke(index, "closed"); });
                seats[i].OpenButton.onClick.AddListener(() => { if (CanConfigure) SeatStatusRequested?.Invoke(index, "open"); });
                seats[i].HotSeatButton.onClick.AddListener(() =>
                {
                    if (CanConfigure && CanUseLocalPlayers && seats[index].HotSeatButton.interactable)
                        LocalPlayerAdditionRequested?.Invoke(index, string.Format(localPlayerNameFormat, index + 1));
                });
                seats[i].RemoveButton.onClick.AddListener(() =>
                {
                    if (CanConfigure && CanUseLocalPlayers && seats[index].RemoveButton.interactable)
                        LocalPlayerRemovalRequested?.Invoke(index);
                });
                seats[i].InviteButton.onClick.AddListener(() =>
                {
                    if (!CanConfigure || !supportsInvites) return;
                    InvitationRequested?.Invoke(index);
                });
                swapTargets[i].onClick.AddListener(() =>
                {
                    if (requestPending || renderedRoom.SeatSlots[index].Status == "closed") return;
                    SeatSwapRequested?.Invoke(index);
                });
            }
            swapAcceptButton.onClick.AddListener(() => ConfirmSwap(true));
            swapDeclineButton.onClick.AddListener(() => ConfirmSwap(false));
        }

        private bool CanConfigure => renderedRoom != null && renderedRoom.Resume == null && viewerId == renderedRoom.HostPlayerId && !requestPending && !renderedRoom.HasStarted;
        private bool CanUseLocalPlayers => supportsLocalPlayers && !online;

        public void Render(RoomState room, int viewerPlayerId)
        {
            using var scope = RoomRenderMarker.Auto();
            if (room == null) return;
            if (renderedRoom?.RoomId != room.RoomId) invitedSeats.Clear();
            renderedRoom = room.Clone(); // 隔离展示副本；View 从不写权威状态。
            viewerId = viewerPlayerId;
            BindRequests();
            var host = viewerId == room.HostPlayerId;
            roomCodeText.text = online ? string.Format(roomCodeFormat, room.RoomId) : offlineRoomCaption;
            copyButton.interactable = online;
            networkingCaption.text = online ? disableNetworkingCaption : enableNetworkingCaption;
            validationText.text = string.Format(summaryFormat, FindName(maps, room.Configuration.MapId),
                FindName(ruleModes, room.Configuration.RuleModeId), ExpansionNames(room.Configuration.EnabledExpansionIds),
                room.PlayerCount);
            permissionsText.text = room.Resume != null ? resumePermissionCaption : host ? CanUseLocalPlayers ? localPermissionCaption : hostPermissionCaption : memberPermissionCaption;
            mapHintText.text = room.PlayerCount == 4 ? fourPlayerHint : mapSelectionHint;
            foreach (var option in maps)
            {
                var forcedDisabled = room.PlayerCount == 4 && option.Id == "map-three-players";
                option.Button.interactable = CanConfigure && !forcedDisabled;
                option.Selection.gameObject.SetActive(room.Configuration.MapId == option.Id);
                option.DisabledOverlay.SetActive(forcedDisabled);
                option.Frame.sprite = forcedDisabled ? option.DisabledFrame : room.Configuration.MapId == option.Id ? option.SelectedFrame : option.DefaultFrame;
                var states = option.Button.spriteState;
                states.disabledSprite = option.Frame.sprite;
                option.Button.spriteState = states;
            }
            foreach (var option in ruleModes)
            {
                // 保留未实现模式的请求入口，Host 拒绝后显示真实原因；绝不改成 standard。
                option.Button.interactable = CanConfigure;
                option.Selection.gameObject.SetActive(room.Configuration.RuleModeId == option.Id);
            }
            foreach (var option in expansions)
            {
                option.Button.interactable = CanConfigure && option.Id != "core";
                option.Selection.gameObject.SetActive(room.Configuration.EnabledExpansionIds.Contains(option.Id));
            }
            passwordButton.gameObject.SetActive(host);
            passwordButton.interactable = false;
            inviteButton.gameObject.SetActive(host && canToggleNetworking && room.Resume == null);
            if (resumeSeatsButton != null)
            {
                resumeSeatsButton.gameObject.SetActive(host && room.Resume != null);
                resumeSeatsButton.interactable = !requestPending && !room.HasStarted;
            }
            inviteButton.interactable = CanConfigure && canToggleNetworking && (online || room.PlayerCount == 1);
            for (var i = 0; i < seats.Length; i++)
            {
                var slot = room.SeatSlots[i];
                var occupant = slot.Occupant;
                seats[i].Avatar?.Bind(online ? occupant?.SteamId ?? 0 : 0);
                var self = occupant != null && occupant.PlayerId == viewerId;
                var label = occupant == null ? (slot.Status == "closed" ? closedCaption : emptyCaption) : occupant.PlayerName;
                seats[i].NameText.text = string.Format(seatNameFormat, i + 1, label, self ? selfCaption : string.Empty);
                seats[i].HostBadge.SetActive(occupant != null && occupant.PlayerId == room.HostPlayerId);
                seats[i].InviteState.SetActive(occupant == null && slot.Status == "open" && invitedSeats.Contains(i));
                seats[i].StateText.text = occupant == null ? (invitedSeats.Contains(i) && slot.Status == "open" ? invitingCaption : label) :
                    (!online ? occupant.PlayerId == room.HostPlayerId ? localHostCaption : localPlayerCaption : occupant.TransportConnected && occupant.IdentityVerified ? connectedCaption : connectingCaption) + "\n" +
                    (occupant.PlayerId == room.HostPlayerId ? hostCaption : CanUseLocalPlayers ? localReadyCaption : slot.IsUserReady && slot.ReadyConfigurationRevision == room.Configuration.ConfigurationRevision ? readyCaption : unreadyCaption);
                if (room.Resume != null && occupant != null && !occupant.LobbyMemberPresent) seats[i].StateText.text = resumeAbsentCaption;
                ShowButton(seats[i].SwapButton, self && room.Resume == null, !requestPending);
                ShowButton(seats[i].RemoveButton, host && occupant != null && !self, CanUseLocalPlayers && CanConfigure);
                ShowButton(seats[i].InviteButton, host && occupant == null && slot.Status == "open", supportsInvites && !requestPending);
                ShowButton(seats[i].HotSeatButton, host && occupant == null && slot.Status == "open", CanUseLocalPlayers && CanConfigure);
                ShowButton(seats[i].BotButton, host && occupant == null && slot.Status == "open", false);
                ShowButton(seats[i].CloseButton, host && occupant == null && slot.Status == "open", CanConfigure);
                ShowButton(seats[i].OpenButton, host && slot.Status == "closed", CanConfigure);
                swapTargets[i].interactable = room.Resume == null && !requestPending && slot.Status != "closed" && !self;
                swapTargets[i].GetComponentInChildren<Text>().text = seats[i].NameText.text;
                swapHostBadges[i].SetActive(occupant != null && occupant.PlayerId == room.HostPlayerId);
                swapEmptyBadges[i].SetActive(slot.Status == "open" && occupant == null);
            }
            var canStart = new RoomConfigurationService(OnlineRoomServiceProvider.ContentCapabilities, localPlayers: CanUseLocalPlayers).TryValidateLaunch(room, out var reasonCode);
            CanStartCurrentRoom = canStart;
            var local = RoomConfigurationService.FindSlot(room, viewerId);
            primaryCaption.text = host ? startCaption : local?.IsUserReady == true ? cancelPrepareCaption : prepareCaption;
            primaryIcon.sprite = host ? hostStartIcon : local?.IsUserReady == true ? memberCancelIcon : memberReadyIcon;
            startButton.interactable = !requestPending && !room.HasStarted && (host ? canStart : CanPrepare(room, local));
            statusText.text = canStart ? (host ? CanUseLocalPlayers ? localCanStartCaption : canStartCaption : waitingHostCaption) : ResolveReason(reasonCode);
            if (room.Resume != null && local == null) statusText.text = resumeWaitingCaption;
            var incoming = room.PendingSeatSwaps.Find(s => s.TargetPlayerId == viewerId);
            swapAcceptButton.gameObject.SetActive(incoming != null);
            swapDeclineButton.gameObject.SetActive(incoming != null);
            swapAcceptButton.interactable = swapDeclineButton.interactable = !requestPending;
            if (incoming != null)
            {
                swapPanel.SetActive(true);
                swapPromptText.text = string.Format(swapPromptFormat, RoomConfigurationService.FindSlot(room, incoming.RequestingPlayerId)?.Occupant?.PlayerName);
            }
            else if (room.PendingSeatSwaps.Exists(s => s.RequestingPlayerId == viewerId)) swapPromptText.text = swapPendingCaption;
            responsiveLayout?.RefreshContentLayout();
        }

        private static bool CanPrepare(RoomState room, RoomSeatSlotState local) => local?.Occupant != null &&
            (local.IsUserReady || local.Occupant.IsReady && local.Occupant.TransportConnected && local.Occupant.IdentityVerified &&
             // 新成员和配置变化后的席位只保存上次确认值；准备时才提交本机规则及内容声明给Host复验。
             room.Configuration.RuleContractVersion == YC.Domain.Rules.GameRuleConfiguration.CurrentRuleContractVersion &&
             !string.IsNullOrEmpty(room.Configuration.ContentPackHash) &&
             OnlineRoomServiceProvider.ContentCapabilities.GetContentPackHash(room.Configuration) == room.Configuration.ContentPackHash);
        private static void ShowButton(Button button, bool visible, bool enabled)
        { button.gameObject.SetActive(visible); button.interactable = visible && enabled; }
        private static string FindName(ContentOption[] options, string id) => Array.Find(options, o => o.Id == id)?.DisplayName ?? id;
        private string ExpansionNames(List<string> ids)
        {
            var labels = new List<string>();
            foreach (var id in ids) labels.Add(FindName(expansions, id));
            return string.Join(expansionNameSeparator, labels);
        }
        public string ResolveReason(string code) => code == "resume_configuration_locked" ? resumePermissionCaption : code == "hotseat_requires_offline" ? hotseatRequiresOfflineCaption : Array.Find(reasons, r => r.Code == code)?.Caption ?? code;
        public void ShowInvitationEntryOpened(int seatIndex)
        {
            if (seatIndex < 0 || seatIndex >= seats.Length || renderedRoom.SeatSlots[seatIndex].Status != "open") return;
            invitedSeats.Add(seatIndex);
            Render(renderedRoom, viewerId);
        }
        public void ShowCommandResult(RoomConfigurationResult result)
        {
            if (result.Snapshot != null) Render(result.Snapshot, viewerId);
            if (!result.Success) statusText.text = ResolveReason(result.ReasonCode);
        }
        private void ShowSwapSelection() { swapPanel.SetActive(true); }
        private void ConfirmSwap(bool accepted)
        {
            if (requestPending) return;
            var request = renderedRoom.PendingSeatSwaps.Find(s => s.TargetPlayerId == viewerId);
            if (request != null) SeatSwapConfirmationRequested?.Invoke(request.SwapRequestId, accepted);
        }
        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            if (helpPanel.activeSelf) helpPanel.SetActive(false);
            else swapPanel.SetActive(false);
        }

        public Text RoomCodeText => roomCodeText;
        public Button CopyButton => copyButton;
        public Text ValidationText => validationText;
        public RectTransform SeatListRoot => seatListRoot;
        public Text SeatTemplate => seatTemplate;
        public Text StatusText => statusText;
        public Button InviteButton => inviteButton;
        public Button NetworkingButton => inviteButton;
        public Button StartButton => startButton;
        public Button BackButton => backButton;
        public RoomState DisplayedRoom => renderedRoom?.Clone();
        // 仅供同次 Render 后的自动开局判断；真正开始仍由房间服务重新验证。
        public bool CanStartCurrentRoom { get; private set; }

        public bool TryValidateConfiguration(out string reason)
        {
            if (roomCodeText == null || copyButton == null || validationText == null ||
                seatListRoot == null || seatTemplate == null || statusText == null ||
                inviteButton == null || networkingCaption == null || startButton == null || backButton == null ||
                seats == null || seats.Length != 4 || maps == null || maps.Length != 2 || ruleModes == null || expansions == null ||
                primaryCaption == null || permissionsText == null || mapHintText == null || swapPromptText == null ||
                passwordButton == null || helpButton == null || helpCloseButton == null || helpPanel == null || swapPanel == null ||
                swapAcceptButton == null || swapDeclineButton == null || swapCloseButton == null || swapTargets == null || swapTargets.Length != 4 || reasons == null)
            {
                reason = "等待房间面板引用不完整。";
                return false;
            }

            foreach (var seat in seats)
                if (seat == null || seat.NameText == null || seat.StateText == null || seat.HostBadge == null || seat.InviteState == null ||
                    seat.Avatar == null || !seat.Avatar.IsConfigured ||
                    seat.SwapButton == null || seat.InviteButton == null || seat.CloseButton == null || seat.OpenButton == null ||
                    seat.RemoveButton == null || seat.HotSeatButton == null || seat.BotButton == null)
                { reason = "座位引用不完整。"; return false; }
            foreach (var options in new[] { maps, ruleModes, expansions })
                foreach (var option in options)
                    if (option == null || string.IsNullOrEmpty(option.Id) || option.Button == null || option.Selection == null)
                    { reason = "房间内容选项引用不完整。"; return false; }
            foreach (var option in maps)
                if (option.DisabledOverlay == null || option.Frame == null || option.DefaultFrame == null || option.SelectedFrame == null || option.DisabledFrame == null)
                { reason = "地图状态引用不完整。"; return false; }
            foreach (var target in swapTargets)
                if (target == null) { reason = "换座目标引用不完整。"; return false; }
            if (primaryIcon == null || hostStartIcon == null || memberReadyIcon == null || memberCancelIcon == null ||
                swapHostBadges == null || swapHostBadges.Length != 4 || swapEmptyBadges == null || swapEmptyBadges.Length != 4)
            { reason = "房间操作图标引用不完整。"; return false; }
            foreach (var badge in swapHostBadges)
                if (badge == null) { reason = "换座房主徽标引用不完整。"; return false; }
            foreach (var badge in swapEmptyBadges)
                if (badge == null) { reason = "换座空座徽标引用不完整。"; return false; }

            reason = string.Empty;
            return true;
        }
    }
}
