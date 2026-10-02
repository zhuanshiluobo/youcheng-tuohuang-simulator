using System;
using UnityEngine;

namespace YC.Presentation
{
    public enum InfluenceMarker2DColor
    {
        [InspectorName("空色")] Empty,
        [InspectorName("绿色")] Green,
        [InspectorName("黄色")] Yellow,
        [InspectorName("绿黄")] GreenYellow,
        [InspectorName("蓝色")] Blue,
        [InspectorName("绿蓝")] GreenBlue,
        [InspectorName("黄蓝")] YellowBlue,
        [InspectorName("绿黄蓝")] GreenYellowBlue,
        [InspectorName("红色")] Red,
        [InspectorName("绿红")] GreenRed,
        [InspectorName("黄红")] YellowRed,
        [InspectorName("绿黄红")] GreenYellowRed,
        [InspectorName("蓝红")] BlueRed,
        [InspectorName("绿蓝红")] GreenBlueRed,
        [InspectorName("黄蓝红")] YellowBlueRed,
        [InspectorName("绿黄蓝红")] AllPlayers,
        [InspectorName("A 区")] RegionA,
        [InspectorName("B 区")] RegionB,
        [InspectorName("C 区")] RegionC,
        [InspectorName("D 区")] RegionD,
        [InspectorName("E 区")] RegionE,
        [InspectorName("F 区")] RegionF,
        [InspectorName("G 区")] RegionG,
        [InspectorName("R 区")] RegionR
    }

    public enum InfluenceMarker2DState
    {
        [InspectorName("普通")] Normal,
        [InspectorName("悬停")] Hover,
        [InspectorName("选中")] Selected,
        [InspectorName("聚焦")] Focus
    }

    /// <summary>统一保存合作色、区域色、数字底板和状态叠层，贴图不烘焙数字。</summary>
    [CreateAssetMenu(menuName = "YC/界面/二维影响力方块贴图目录")]
    public sealed class InfluenceMarker2DCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public InfluenceMarker2DColor color;
            public string displayName;
            public Sprite plain;
            public Sprite numbered;
            public Sprite flat;
            public Color numberInk = Color.black;
        }

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();
        [SerializeField] private Sprite hoverOverlay, selectedOverlay, focusOverlay;
        public Entry[] Entries => entries;
        public Entry Find(InfluenceMarker2DColor color) => Array.Find(entries, entry => entry != null && entry.color == color);
        public Sprite Overlay(InfluenceMarker2DState state) => state == InfluenceMarker2DState.Hover ? hoverOverlay :
            state == InfluenceMarker2DState.Selected ? selectedOverlay : state == InfluenceMarker2DState.Focus ? focusOverlay : null;

        public bool TryValidateConfiguration(out string reason)
        {
            foreach (InfluenceMarker2DColor color in Enum.GetValues(typeof(InfluenceMarker2DColor)))
            {
                var entry = Find(color);
                if (entry == null || entry.plain == null || entry.numbered == null ||
                    ((int)color >= (int)InfluenceMarker2DColor.RegionA && entry.flat == null))
                {
                    reason = "二维影响力方块贴图不完整：" + color;
                    return false;
                }
            }
            reason = hoverOverlay != null && selectedOverlay != null && focusOverlay != null
                ? string.Empty : "二维影响力方块状态叠层不完整。";
            return reason.Length == 0;
        }
    }
}
