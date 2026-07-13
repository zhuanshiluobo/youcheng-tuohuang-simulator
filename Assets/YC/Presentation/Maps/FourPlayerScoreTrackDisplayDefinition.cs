using UnityEngine;
using YC.Domain.Scoring;

namespace YC.Presentation.Maps
{
    public static class FourPlayerScoreTrackDisplayDefinition
    {
        public static Vector2 GetNormalizedPosition(int score)
        {
            var clampedScore = ScoreTrackService.ClampToTrack(score);
            if (clampedScore <= 23)
            {
                const float horizontalStartX = 0.1013f;
                const float horizontalEndX = 0.98015f;
                var t = (clampedScore - ScoreTrackService.MinimumTrackScore) / 25f;
                return new Vector2(Mathf.Lerp(horizontalStartX, horizontalEndX, t), 0.98065f);
            }

            const float verticalBottomY = 0.9437f;
            const float verticalTopY = 0.02535f;
            var verticalT = (clampedScore - 24) / 26f;
            return new Vector2(0.98015f, Mathf.Lerp(verticalBottomY, verticalTopY, verticalT));
        }
    }
}
