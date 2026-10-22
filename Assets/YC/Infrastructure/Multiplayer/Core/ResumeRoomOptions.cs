using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YC.Application.Sessions;
using YC.Domain.Rules;

namespace YC.Infrastructure.Multiplayer
{
    [Serializable] public sealed class RoomMember { public string MemberId; public string PlayerName; }

    /// <summary>仅广播冻结的开局事实和确认点标识。规则快照、手牌和操作窗口不进入房间消息。</summary>
    [Serializable]
    public sealed class ResumeRoomOptions
    {
        public string MapId, GameId, ContentHash;
        public int Round, HostPlayerId, EventDeckSeed;
        public GameRuleConfiguration Configuration;
        public GameplayRandomState RandomRuntime;
        public List<int> LaunchPlayerOrder = new List<int>();
        public List<MatchSaveSeat> Seats = new List<MatchSaveSeat>();
        public long SimulationEpoch, ConfirmedGlobalSequence;
        public int MembershipVersion;
        public string CommandPrefixDigest, CheckpointId, MetadataDigest;
        public ResumeRoomOptions Clone() => Newtonsoft.Json.JsonConvert.DeserializeObject<ResumeRoomOptions>(Newtonsoft.Json.JsonConvert.SerializeObject(this));
        public static ResumeRoomOptions FromSave(MatchSaveData save)
        {
            save.Validate();
            if (save.Mode != LaunchMode.Host) throw new InvalidOperationException("只有联机存档可以创建续局房间。");
            var baseline = SimulationConfirmedRecoveryInstaller.ReadBaseline(save.SimulationCheckpointJson);
            return new ResumeRoomOptions { MapId = save.MapId, GameId = save.GameId, ContentHash = save.ContentHash,
                Round = save.Round, HostPlayerId = save.LocalPlayerId, Configuration = save.Configuration,
                RandomRuntime = save.RandomState, EventDeckSeed = save.EventDeckSeed, LaunchPlayerOrder = save.LaunchPlayerOrder,
                Seats = save.Seats, SimulationEpoch = baseline.SimulationEpoch, MembershipVersion = baseline.MembershipVersion,
                ConfirmedGlobalSequence = baseline.ConfirmedGlobalSequence, CommandPrefixDigest = baseline.CommandPrefixDigest,
                CheckpointId = baseline.CheckpointId, MetadataDigest = baseline.MetadataDigest }.Clone();
        }
        public DeterministicSimulationSessionOptions Options(bool host, int playerId) => new DeterministicSimulationSessionOptions {
            IsHost = host, LocalPlayerId = playerId, GameId = GameId, ContentPackHash = ContentHash,
            SimulationEpoch = SimulationEpoch, MembershipVersion = MembershipVersion,
            CheckpointGlobalSequence = ConfirmedGlobalSequence, CheckpointPrefixDigest = CommandPrefixDigest,
            FrozenPlayerIds = Seats.Select(s => s.PlayerId).OrderBy(id => id).ToList(),
            RequiredReplicaIds = Seats.Select(s => s.PlayerId).OrderBy(id => id).Select(DeterministicSimulationSessionOptions.ReplicaIdForPlayer).ToList() };
        public void Validate()
        {
            if (Seats == null || Seats.Count < 2 || Seats.Count > 4 || Seats.Any(s => s == null || s.PlayerId <= 0 || s.SteamId == 0) ||
                Seats.Select(s => s.PlayerId).Distinct().Count() != Seats.Count || Seats.Select(s => s.SteamId).Distinct().Count() != Seats.Count ||
                Seats.Select(s => s.Color).Distinct().Count() != Seats.Count || !Seats.Any(s => s.PlayerId == HostPlayerId) ||
                Configuration == null || !Configuration.TryValidate(out _) || Configuration.PlayerCount != Seats.Count ||
                Configuration.MapId != MapId || Configuration.ContentPackHash != ContentHash || string.IsNullOrEmpty(ContentHash) ||
                !TurnOrderService.IsValidPlayerOrder(LaunchPlayerOrder, Seats.Select(s => s.PlayerId)) ||
                !GameplayRandomRuntime.TryValidate(RandomRuntime, GameId, EventDeckSeed, out _) ||
                SimulationEpoch <= 1 || MembershipVersion <= 0 || ConfirmedGlobalSequence < 0 ||
                string.IsNullOrEmpty(CommandPrefixDigest) || string.IsNullOrEmpty(CheckpointId) || string.IsNullOrEmpty(MetadataDigest))
                throw new InvalidOperationException("续局房间摘要不完整或与存档配置不一致。");
        }

        public void ApplyTo(RoomState room, PlayerSeat host, IRoomContentCapabilityProvider content)
        {
            Validate();
            if (host == null || host.PlayerId != HostPlayerId || host.SteamId != Seats.Single(s => s.PlayerId == HostPlayerId).SteamId)
                throw new InvalidOperationException("只有原房主可以创建此续局房间。");
            if (content == null || !content.TryValidate(Configuration, out _) || content.GetContentPackHash(Configuration) != ContentHash)
                throw new InvalidOperationException("存档的内容版本或扩展配置与本机不一致。");
            room.Resume = Clone(); room.WaitingMembers.Clear(); room.PendingSeatSwaps.Clear();
            room.Configuration = Configuration.Clone(); room.HostPlayerId = room.LocalPlayerId = HostPlayerId;
            foreach (var slot in room.SeatSlots) { slot.Occupant = null; slot.Status = "closed"; slot.IsUserReady = false; slot.ReadyConfigurationRevision = -1; }
            foreach (var saved in Seats)
            {
                var slot = room.SeatSlots.Single(s => RoomConfigurationService.GetSeatColor(s.SeatIndex) == saved.Color);
                slot.Status = "occupied"; slot.Occupant = saved.PlayerId == HostPlayerId ? host.Clone() : saved.ToSeat();
                slot.Occupant.Color = saved.Color; slot.Occupant.ContentPackHash = ContentHash;
                slot.Occupant.RuleContractVersion = Configuration.RuleContractVersion;
                if (saved.PlayerId != HostPlayerId) slot.Occupant.LobbyMemberPresent = false;
            }
            room.StateRevision++;
        }
        public static void SetAbsent(RoomState room, int playerId)
        {
            var slot = RoomConfigurationService.FindSlot(room, playerId);
            if (slot == null) return;
            slot.Occupant.LobbyMemberPresent = slot.Occupant.TransportConnected = slot.Occupant.IdentityVerified = slot.Occupant.IsReady = false;
            slot.IsUserReady = false; slot.ReadyConfigurationRevision = -1; room.StateRevision++;
        }
    }

    public interface IResumableOnlineRoomService : IOnlineRoomService
    {
        Task<RoomState> CreateResumeRoomAsync(ResumeRoomOptions options);
        void AssignResumeSeat(string memberId, int playerId);
    }
}
