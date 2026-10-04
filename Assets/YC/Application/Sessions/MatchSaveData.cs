using System;
using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Application.Sessions
{
    [Serializable]
    public sealed class MatchSaveSeat
    {
        public int PlayerId;
        public ulong SteamId;
        public string OperatorId;
        public string PlayerName;
        public PlayerColor Color;
        public PlayerSeat ToSeat() => new PlayerSeat
        { PlayerId = PlayerId, SteamId = SteamId, OperatorId = OperatorId, PlayerName = PlayerName, Color = Color };
    }

    /// <summary>仅供本机权威存档使用，禁止放入房间/客户端消息。</summary>
    [Serializable]
    public sealed class MatchSaveData
    {
        public const int CurrentVersion = 1;
        public string Format = "YC.MatchSave";
        public int Version = CurrentVersion;
        public long SavedUtcTicks;
        public LaunchMode Mode;
        public bool LocalTestNetwork;
        public int LocalPlayerId;
        public ulong HostSteamId;
        public string MapId;
        public string GameId;
        public int Round;
        public string ContentHash;
        public List<MatchSaveSeat> Seats = new List<MatchSaveSeat>();
        public HostSessionArchiveDto Archive;

        public void Validate()
        {
            if (Format != "YC.MatchSave" || Version != CurrentVersion)
                throw new InvalidOperationException("不支持此对局存档版本。");
            if (Mode != LaunchMode.Local && Mode != LaunchMode.Host)
                throw new InvalidOperationException("存档必须来自本地对局或房主。");
            if (Archive == null || Archive.Snapshot == null || Archive.Snapshot.State == null ||
                Archive.Snapshot.SchemaVersion != HostSnapshotDto.CurrentSchemaVersion ||
                (Archive.Journal != null && Archive.Journal.SchemaVersion != HostJournalDto.CurrentSchemaVersion))
                throw new InvalidOperationException("存档缺少有效的权威快照。");
            var state = Archive.Snapshot.State;
            if (string.IsNullOrEmpty(ContentHash) || ContentHash != Archive.Snapshot.ContentHash ||
                MapId != state.MapId || GameId != state.GameId || Round != state.Round ||
                SavedUtcTicks <= 0 || SavedUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidOperationException("存档摘要与权威快照不一致。");
            if (Seats == null || Seats.Count == 0 || Seats.Count != state.Players.Count)
                throw new InvalidOperationException("存档席位不完整。");
            var ids = new HashSet<int>();
            var identities = new HashSet<ulong>();
            var operators = new HashSet<string>();
            foreach (var seat in Seats)
            {
                if (seat == null || seat.PlayerId <= 0 || !ids.Add(seat.PlayerId) ||
                    !state.Players.Exists(p => p.PlayerId == seat.PlayerId))
                    throw new InvalidOperationException("存档席位与对局玩家不一致。");
                if (Mode == LaunchMode.Host && LocalTestNetwork &&
                    (string.IsNullOrEmpty(seat.OperatorId) || !operators.Add(seat.OperatorId)))
                    throw new InvalidOperationException("本地联机存档操作者身份缺失或重复。");
                if (Mode == LaunchMode.Host && (seat.SteamId == 0 || !identities.Add(seat.SteamId)))
                    throw new InvalidOperationException("联机存档包含重复或缺失的操作者身份。");
            }
            if (!ids.Contains(LocalPlayerId) || (Mode == LaunchMode.Host &&
                (LocalPlayerId != 1 || HostSteamId == 0 || !Seats.Exists(s => s.PlayerId == 1 && s.SteamId == HostSteamId))))
                throw new InvalidOperationException("存档缺少原房主或本地控制配置。");
        }
    }
}
