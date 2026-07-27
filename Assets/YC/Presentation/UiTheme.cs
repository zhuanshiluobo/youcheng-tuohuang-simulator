using UnityEngine;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public static class UiTheme
    {
        public static readonly Color PanelBackground = new Color(0.08f, 0.07f, 0.055f, 0.94f);
        public static readonly Color PanelBackgroundLighter = new Color(0.14f, 0.1f, 0.06f, 0.96f);
        public static readonly Color SectionTitleBackground = new Color(0.12f, 0.09f, 0.06f, 0.92f);
        public static readonly Color ScrollBackground = new Color(0.06f, 0.05f, 0.04f, 0.5f);
        public static readonly Color ButtonBackground = new Color(0.16f, 0.1f, 0.055f, 0.96f);
        public static readonly Color DisabledButtonBackground = new Color(0.09f, 0.075f, 0.06f, 0.72f);

        public static readonly Color GoldText = new Color(0.86f, 0.75f, 0.55f, 1f);
        public static readonly Color GoldOutline = new Color(0.78f, 0.63f, 0.38f, 0.85f);
        public static readonly Color GoldOutlineThin = new Color(0.78f, 0.63f, 0.38f, 0.65f);
        public static readonly Color GoldSeparator = new Color(0.78f, 0.63f, 0.38f, 0.7f);
        public static readonly Color CyanAccent = new Color(0.12f, 0.88f, 1f, 1f);
        public static readonly Color TacticalMapBg = new Color(0.08f, 0.10f, 0.12f, 1f);

        public static readonly Color DarkShadow = new Color(0.06f, 0.04f, 0.025f, 0.9f);
        public static readonly Color DarkShadowLight = new Color(0.06f, 0.04f, 0.025f, 0.95f);

        public static readonly Color LabelText = new Color(0.7f, 0.65f, 0.55f, 1f);
        public static readonly Color ValueText = new Color(0.95f, 0.9f, 0.82f, 1f);

        public static readonly Color TrackBackground = new Color(0.1f, 0.1f, 0.09f, 0.88f);
        public static readonly Color DangerBand = new Color(0.56f, 0.08f, 0.06f, 0.95f);
        public static readonly Color SafeBand = new Color(0.82f, 0.78f, 0.67f, 0.95f);
        public static readonly Color GameOverOverlay = new Color(0f, 0f, 0f, 0.62f);
        public static readonly Color GameOverDialog = new Color(0.16f, 0.1f, 0.055f, 0.98f);

        public static Color GetPlayerColor(PlayerColor playerColor, float alpha)
        {
            switch (playerColor)
            {
                case PlayerColor.Red:
                    return new Color(0.7019608f, 0f, 0.1137255f, alpha);
                case PlayerColor.Blue:
                    return new Color(0.003921569f, 0.2705882f, 0.6980392f, alpha);
                case PlayerColor.Green:
                    return new Color(0.3764706f, 0.8235294f, 0.003921569f, alpha);
                case PlayerColor.Yellow:
                    return new Color(1f, 0.7450981f, 0f, alpha);
                default:
                    return Color.white;
            }
        }
    }
}
