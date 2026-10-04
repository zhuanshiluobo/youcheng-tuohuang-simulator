using UnityEngine;

namespace YC.Presentation
{
    /// <summary>采样 PRTS 带框材料图标稀有度圆环的 sRGB 主色，用于收藏室背景。</summary>
    public static class CollectionRarityPalette
    {
        public static bool TryGetSpotlightColor(string rarity, out Color color)
        {
            switch (rarity)
            {
                case "常见": color = new Color32(160, 160, 160, 255); return true; // T1 #A0A0A0 源岩
                case "普通": color = new Color32(220, 229, 55, 255); return true;  // T2 #DCE537 固源岩
                case "精良": color = new Color32(0, 179, 253, 255); return true;   // T3 #00B3FD 固源岩组
                case "罕见": color = new Color32(214, 197, 214, 255); return true; // T4 #D6C5D6 提纯源岩、糖聚块、酮阵列
                case "稀有": color = new Color32(255, 200, 2, 255); return true;   // T5 #FFC802 聚合剂、双极纳米片
                default: color = Color.white; return false;
            }
        }
    }
}
