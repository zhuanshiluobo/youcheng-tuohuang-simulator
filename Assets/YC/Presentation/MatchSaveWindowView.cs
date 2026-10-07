using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using YC.Infrastructure.Multiplayer;
using YC.Infrastructure.Persistence;

namespace YC.Presentation
{
    /// <summary>通用窗口的存档业务层；布局、按钮文案和各模式文案由存档预制体持有。</summary>
    public sealed class MatchSaveWindowView : MonoBehaviour
    {
        [SerializeField] private GenericWindowView window;
        [SerializeField] private Button[] rows;
        [SerializeField] private Text[] rowTexts;
        [SerializeField] private Text[] rowTimes;
        [SerializeField] private Button[] deleteButtons;
        [SerializeField] private GenericWindowView deleteConfirmationPrefab;
        [SerializeField] private string timeFormat = "yyyy-MM-dd HH:mm:ss";
        [SerializeField] private Text message;
        [SerializeField] private Button nextMemberButton;
        [SerializeField] private string loadTitle = "读取存档";
        [SerializeField] private string saveTitle = "手动存档";
        [SerializeField] private string seatsTitle = "续局席位";
        [SerializeField] private string loadHint = "选择存档后点击确认读取。";
        [SerializeField] private string saveHint = "选择一个手动槽位。游戏也会在每次成功行动后自动保存。";
        [SerializeField] private string seatsHint = "为缺席席位选择替补成员。替补将继承该席位的全部状态和私有手牌。";
        [SerializeField] private string overwriteHint = "此手动槽位已有存档。确认后将覆盖原文件。";
        [SerializeField] private string loadingHint = "正在检查存档并准备对局…";
        [SerializeField] private string manualLabel = "手动";
        [SerializeField] private string autoLabel = "自动";
        [SerializeField] private string emptyLabel = "空槽位";
        [SerializeField] private string localLabel = "本地对局";
        [SerializeField] private string onlineLabel = "联机对局";
        [SerializeField] private string threePlayerLabel = "三人地图";
        [SerializeField] private string fourPlayerLabel = "四人地图";
        [SerializeField] private string slotFormat = "{0} {1}　{2}　{3}　回合 {4}";
        [SerializeField] private string emptyFormat = "{0} {1}　{2}";
        [SerializeField] private string selectedFormat = "▶ {0}";
        [SerializeField] private string errorFormat = "操作失败：{0}";
        [SerializeField] private string seatFormat = "席位 {0}　原操作者：{1}\n当前：{2}　{3}";
        [SerializeField] private string presentLabel = "已到场";
        [SerializeField] private string absentLabel = "保留 / 缺席";
        [SerializeField] private string memberFormat = "待分配成员：{0}\n{1}";
        [SerializeField] private string noMemberLabel = "没有等待分配的成员。原玩家可直接加入对应席位。";

        private enum Mode { Load, Save, Seats }
        private static MatchSaveWindowView activeWindow;
        private static int closedFrame = -1;
        private Mode mode;
        private StartMenuController menu;
        private List<MatchSaveSlot> slots;
        private RoomState room;
        private int selected = -1, memberIndex;
        private bool busy;
        private float nextRoomRefresh;
        private string[] labels;
        private string seatError;
        private GenericWindowView deleteConfirmation;
        public static bool IsAnyOpen => activeWindow != null && activeWindow.gameObject.activeInHierarchy;
        public static bool WasClosedThisFrame => closedFrame == Time.frameCount;

        public static void OpenLoad(StartMenuController menu)
        {
            if (menu == null || !menu.CanOpenMatchSave) return;
            Open(Mode.Load, menu);
        }
        public static void OpenSave()
        {
            if (MatchSaveController.Instance == null || !MatchSaveController.Instance.CanSave) return;
            Open(Mode.Save, null);
        }
        public static void OpenSeats()
        {
            var room = OnlineRoomServiceProvider.GetActive()?.GetCurrentRoom();
            if (room?.Resume == null || room.HasStarted || room.LocalPlayerId != room.HostPlayerId) return;
            Open(Mode.Seats, null);
        }
        private static void Open(Mode mode, StartMenuController menu)
        {
            if (IsAnyOpen) return;
            var prefab = Resources.Load<MatchSaveWindowView>("MatchSaveWindow");
            if (prefab == null) { Debug.LogError("缺少存档窗口预制体 MatchSaveWindow。"); return; }
            var instance = Instantiate(prefab);
            instance.mode = mode;
            instance.menu = menu;
            instance.labels = new string[instance.rows.Length];
            activeWindow = instance;
            if (mode == Mode.Seats)
            {
                instance.RefreshSeats();
                if (instance.room?.Resume == null || instance.room.HasStarted)
                {
                    activeWindow = null;
                    Destroy(instance.gameObject);
                    return;
                }
            }
            else
            {
                foreach (var row in instance.rows) row.gameObject.SetActive(false);
                _ = instance.RefreshSlots();
            }
            GameplayHudFrame.Active?.SuspendEffectForInformation();
            instance.window.Show();
        }
        private void Awake()
        {
            window.Confirmed.AddListener(Confirm);
            for (int i = 0; i < rows.Length; i++)
            {
                int index = i;
                rows[i].onClick.AddListener(() => Select(index));
                deleteButtons[i].onClick.AddListener(() => RequestDelete(index));
            }
            nextMemberButton.onClick.AddListener(() => { seatError = null; memberIndex++; RefreshSeats(); });
        }
        private void OnDisable()
        {
            if (activeWindow != this) return;
            activeWindow = null;
            closedFrame = Time.frameCount;
            if (UnityEngine.Application.isPlaying) Destroy(gameObject);
        }
        private void OnDestroy()
        {
            if (deleteConfirmation != null) Destroy(deleteConfirmation.gameObject);
        }
        private void Update()
        {
            if (mode != Mode.Seats || busy || Time.unscaledTime < nextRoomRefresh) return;
            nextRoomRefresh = Time.unscaledTime + 0.5f;
            RefreshSeats();
        }
        private async Task RefreshSlots()
        {
            SetBusy(true);
            window.SetContent(mode == Mode.Save ? saveTitle : loadTitle,
                mode == Mode.Save ? saveHint : loadHint, string.Empty);
            nextMemberButton.gameObject.SetActive(false);
            try
            {
                slots = await MatchSaveController.Ensure().ListSlotsAsync();
                if (this == null || !isActiveAndEnabled) return;
                // 展示列表只读取并校验存档文件，不为每个槽位初始化内容库和恢复整局。
                // 玩家确认读取所选存档时，Read 仍执行完整恢复预检。
                for (int i = 0; i < rows.Length; i++)
                {
                    var slot = slots[i];
                    rows[i].gameObject.SetActive(mode == Mode.Load || i < MatchSaveStore.ManualCount);
                    var kind = i < MatchSaveStore.ManualCount ? manualLabel : autoLabel;
                    var data = slot.Data;
                    rowTimes[i].gameObject.SetActive(data != null);
                    rowTimes[i].text = data == null ? string.Empty
                        : new DateTime(data.SavedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString(timeFormat);
                    deleteButtons[i].gameObject.SetActive(mode == Mode.Load && slot.Exists);
                    labels[i] = data == null
                        ? string.Format(emptyFormat, kind, i % MatchSaveStore.ManualCount + 1, slot.Error ?? emptyLabel)
                        : string.Format(slotFormat, kind, i % MatchSaveStore.ManualCount + 1,
                            data.Mode == YC.Application.Sessions.LaunchMode.Local ? localLabel : onlineLabel,
                            data.MapId == YC.Domain.Maps.StaticMapDefinitions.ThreePlayerMapId ? threePlayerLabel : fourPlayerLabel,
                            data.Round)
                            + (slot.Error == null ? string.Empty : "\n" + slot.Error);
                }
                UpdateSelection();
            }
            catch (Exception ex)
            {
                if (this != null) message.text = string.Format(errorFormat, ex.Message);
            }
            finally
            {
                if (this != null) SetBusy(false);
            }
        }
        private void RefreshSeats()
        {
            room = OnlineRoomServiceProvider.GetActive()?.GetCurrentRoom();
            if (room?.Resume == null || room.HasStarted) { window.Hide(); return; }
            var hint = room.WaitingMembers.Count == 0 ? noMemberLabel : string.Format(memberFormat,
                room.WaitingMembers[memberIndex % room.WaitingMembers.Count].PlayerName, seatsHint);
            window.SetContent(seatsTitle, seatError == null ? hint : string.Format(errorFormat, seatError), string.Empty);
            nextMemberButton.gameObject.SetActive(true);
            nextMemberButton.interactable = room.WaitingMembers.Count > 1;
            for (int i = 0; i < rows.Length; i++)
            {
                rowTimes[i].gameObject.SetActive(false);
                deleteButtons[i].gameObject.SetActive(false);
                rows[i].gameObject.SetActive(i < room.Seats.Count);
                if (i >= room.Seats.Count) continue;
                var seat = room.Seats[i];
                var original = room.Resume.Seats.Find(s => s.PlayerId == seat.PlayerId);
                labels[i] = string.Format(seatFormat, seat.PlayerId, original?.PlayerName, seat.PlayerName,
                    seat.LobbyMemberPresent ? presentLabel : absentLabel);
            }
            UpdateSelection();
        }
        private void Select(int index)
        {
            if (busy) return;
            selected = index;
            if (mode != Mode.Seats)
                message.text = mode == Mode.Save && slots[index].Exists ? overwriteHint
                    : slots[index].Error ?? (mode == Mode.Save ? saveHint : loadHint);
            else { seatError = null; RefreshSeats(); }
            UpdateSelection();
        }
        private void UpdateSelection()
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rowTexts[i].text = i == selected ? string.Format(selectedFormat, labels[i]) : labels[i];
                rows[i].interactable = !busy;
                deleteButtons[i].interactable = !busy;
            }
            bool canConfirm = selected >= 0;
            if (mode == Mode.Load && canConfirm) canConfirm = slots[selected].Data != null && slots[selected].Error == null;
            if (mode == Mode.Seats && canConfirm) canConfirm = room != null && selected < room.Seats.Count &&
                room.LocalPlayerId == room.HostPlayerId && room.Seats[selected].PlayerId != room.HostPlayerId &&
                !room.Seats[selected].LobbyMemberPresent && room.WaitingMembers.Count > 0;
            window.ConfirmButton.interactable = !busy && canConfirm;
        }
        private void SetBusy(bool value)
        {
            busy = value;
            window.SetInteractionEnabled(!value);
            nextMemberButton.interactable = !value && room != null && room.WaitingMembers.Count > 1;
            UpdateSelection();
        }
        private void RequestDelete(int index)
        {
            if (busy || mode != Mode.Load || !slots[index].Exists) return;
            SetBusy(true);
            deleteConfirmation = Instantiate(deleteConfirmationPrefab);
            deleteConfirmation.Cancelled.AddListener(CancelDelete);
            deleteConfirmation.Confirmed.AddListener(() => DeleteSlot(index));
            deleteConfirmation.Show();
        }
        private void CancelDelete()
        {
            Destroy(deleteConfirmation.gameObject);
            deleteConfirmation = null;
            SetBusy(false);
        }
        private async void DeleteSlot(int index)
        {
            deleteConfirmation.SetInteractionEnabled(false);
            deleteConfirmation.ConfirmButton.interactable = false;
            try
            {
                await MatchSaveController.Ensure().Delete(index);
                if (this == null) return;
                selected = -1;
                await RefreshSlots();
                if (this == null) return;
                message.text = string.Empty;
                menu?.RefreshMatchSaveAvailability();
            }
            catch (Exception ex)
            {
                if (this == null) return;
                await RefreshSlots();
                if (this == null) return;
                message.text = string.Format(errorFormat, ex.Message);
                menu?.RefreshMatchSaveAvailability();
            }
            finally
            {
                if (this != null)
                {
                    if (deleteConfirmation != null) Destroy(deleteConfirmation.gameObject);
                    deleteConfirmation = null;
                    SetBusy(false);
                }
            }
        }
        private async void Confirm()
        {
            if (busy || selected < 0) return;
            if (mode == Mode.Load) { LoadSelected(); return; }
            if (mode == Mode.Seats)
            {
                seatError = null;
                try
                {
                    var service = OnlineRoomServiceProvider.GetActive() as IResumableOnlineRoomService;
                    var member = room.WaitingMembers[memberIndex % room.WaitingMembers.Count];
                    service.AssignResumeSeat(member.MemberId, room.Seats[selected].PlayerId);
                    selected = -1;
                    RefreshSeats();
                }
                catch (Exception ex) { seatError = ex.Message; message.text = string.Format(errorFormat, seatError); }
                return;
            }
            SetBusy(true);
            try
            {
                var saves = MatchSaveController.Ensure();
                if (await saves.Save(selected)) window.Hide();
                else message.text = string.Format(errorFormat, saves.LastFailure);
            }
            finally { if (this != null) SetBusy(false); }
        }
        private async void LoadSelected()
        {
            if (busy || selected < 0 || menu == null) return;
            SetBusy(true);
            message.text = loadingHint;
            try
            {
                var data = MatchSaveController.Ensure().Read(selected);
                await menu.RestoreMatch(data);
                window.Hide();
            }
            catch (Exception ex)
            {
                GameLaunchContext.Instance?.ClearRestore();
                message.text = string.Format(errorFormat, ex.Message);
            }
            finally { if (this != null) SetBusy(false); }
        }
    }
}
