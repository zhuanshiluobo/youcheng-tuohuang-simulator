using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YC.Application.Sessions;
namespace YC.Infrastructure.Multiplayer
{
    // 仅公开房间摘要和席位；绝不能包含 HostSessionArchiveDto。
    [Serializable] public sealed class RoomMember
    {
        public string MemberId;
        public string PlayerName;
    }
    [Serializable] public sealed class ResumeRoomOptions
    {
        public string MapId;
        public string GameId;
        public int Round;
        public List<MatchSaveSeat> Seats = new List<MatchSaveSeat>();
        public ResumeRoomOptions Clone()
        {
            var result = new ResumeRoomOptions { MapId = MapId, GameId = GameId, Round = Round };
            foreach (var s in Seats) result.Seats.Add(new MatchSaveSeat {
                PlayerId = s.PlayerId, SteamId = s.SteamId, OperatorId = s.OperatorId, PlayerName = s.PlayerName, Color = s.Color });
            return result;
        }
        public static ResumeRoomOptions FromSave(MatchSaveData save)
        {
            save.Validate();
            return new ResumeRoomOptions { MapId = save.MapId, GameId = save.GameId, Round = save.Round, Seats = save.Seats }.Clone();
        }
        public void Validate()
        {
            if (Seats == null || Seats.Count < 2 || Seats.Count > 4 || string.IsNullOrEmpty(MapId))
                throw new InvalidOperationException("续局房间席位无效。");
            var ids = new HashSet<ulong>();
            for (int i = 1; i <= Seats.Count; i++)
            {
                var seat = Seats.Find(s => s != null && s.PlayerId == i);
                if (seat == null || seat.SteamId == 0 || !ids.Add(seat.SteamId))
                    throw new InvalidOperationException("续局房间身份重复或缺失。");
            }
        }
    }
    public interface IResumableOnlineRoomService : IOnlineRoomService
    {
        Task<RoomState> CreateResumeRoomAsync(ResumeRoomOptions options);
        void AssignResumeSeat(string memberId, int playerId);
    }
}
