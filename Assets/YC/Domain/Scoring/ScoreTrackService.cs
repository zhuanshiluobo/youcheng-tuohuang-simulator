using System;
using YC.Domain.State;

namespace YC.Domain.Scoring
{
    public static class ScoreTrackService
    {
        public const int MinimumTrackScore = -2;
        public const int MaximumTrackScore = 50;

        public static void InitializePlayerMarkers(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (player == null || player.HasScoreTrackMarker)
                {
                    continue;
                }

                if (player.InfluenceSupply <= 0)
                {
                    throw new InvalidOperationException("玩家供应堆中没有可用于计分轨道的影响力。");
                }

                player.InfluenceSupply -= 1;
                player.HasScoreTrackMarker = true;
            }
        }

        public static int ClampToTrack(int score)
        {
            return Math.Max(MinimumTrackScore, Math.Min(MaximumTrackScore, score));
        }
    }
}
