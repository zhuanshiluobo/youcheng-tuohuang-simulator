using System;
using UnityEngine;
using YC.Domain.Maps;

namespace YC.Presentation.Maps
{
    public static class FourPlayerScoreTrackDisplayDefinition
    {
        public static Vector2 GetNormalizedPosition(int score)
        {
            var layout = MapDisplayLayoutCatalog.Load(StaticMapDefinitions.FourPlayerMapId);
            string reason = null;
            if (layout == null || !layout.TryValidateScoreTrack(out reason))
            {
                throw new InvalidOperationException(
                    "缺少有效四人地图计分轨迹资产：" +
                    (layout == null ? "MapDisplayLayout 引用为空。" : reason));
            }

            return layout.GetScoreTrackNormalizedPosition(score);
        }
    }
}
