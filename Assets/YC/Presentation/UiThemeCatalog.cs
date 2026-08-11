using System;
using UnityEngine;
using YC.Domain.Rules;

namespace YC.Presentation
{
    [CreateAssetMenu(fileName = "UiThemeCatalog", menuName = "YC/UI/Theme Catalog")]
    public sealed class UiThemeCatalog : ScriptableObject
    {
        [Header("Panels and controls")]
        [SerializeField] private Color panelBackground;
        [SerializeField] private Color panelBackgroundLighter;
        [SerializeField] private Color sectionTitleBackground;
        [SerializeField] private Color scrollBackground;
        [SerializeField] private Color buttonBackground;
        [SerializeField] private Color disabledButtonBackground;

        [Header("Accents and text")]
        [SerializeField] private Color goldText;
        [SerializeField] private Color goldOutline;
        [SerializeField] private Color goldOutlineThin;
        [SerializeField] private Color goldSeparator;
        [SerializeField] private Color cyanAccent;
        [SerializeField] private Color tacticalMapBackground;
        [SerializeField] private Color darkShadow;
        [SerializeField] private Color darkShadowLight;
        [SerializeField] private Color labelText;
        [SerializeField] private Color valueText;

        [Header("Round tracker")]
        [SerializeField] private Color trackBackground;
        [SerializeField] private Color dangerBand;
        [SerializeField] private Color safeBand;
        [SerializeField] private Color gameOverOverlay;
        [SerializeField] private Color gameOverDialog;

        [Header("Players")]
        [SerializeField] private Color redPlayer;
        [SerializeField] private Color bluePlayer;
        [SerializeField] private Color greenPlayer;
        [SerializeField] private Color yellowPlayer;

        [Header("Shared high-value layout")]
        [SerializeField] private Vector2 canvasReferenceResolution;
        [SerializeField, Range(0f, 1f)] private float canvasMatchWidthOrHeight;
        [SerializeField] private Vector2 dialogActionButtonSize;
        [SerializeField] private Vector2 collapsibleMapPromptSize;
        [SerializeField] private Vector2 viewerCloseButtonSize;
        [SerializeField] private Vector2 viewerCloseButtonOffset;

        public Color PanelBackground => panelBackground;
        public Color PanelBackgroundLighter => panelBackgroundLighter;
        public Color SectionTitleBackground => sectionTitleBackground;
        public Color ScrollBackground => scrollBackground;
        public Color ButtonBackground => buttonBackground;
        public Color DisabledButtonBackground => disabledButtonBackground;
        public Color GoldText => goldText;
        public Color GoldOutline => goldOutline;
        public Color GoldOutlineThin => goldOutlineThin;
        public Color GoldSeparator => goldSeparator;
        public Color CyanAccent => cyanAccent;
        public Color TacticalMapBackground => tacticalMapBackground;
        public Color DarkShadow => darkShadow;
        public Color DarkShadowLight => darkShadowLight;
        public Color LabelText => labelText;
        public Color ValueText => valueText;
        public Color TrackBackground => trackBackground;
        public Color DangerBand => dangerBand;
        public Color SafeBand => safeBand;
        public Color GameOverOverlay => gameOverOverlay;
        public Color GameOverDialog => gameOverDialog;
        public Vector2 CanvasReferenceResolution => canvasReferenceResolution;
        public float CanvasMatchWidthOrHeight => canvasMatchWidthOrHeight;
        public Vector2 DialogActionButtonSize => dialogActionButtonSize;
        public Vector2 CollapsibleMapPromptSize => collapsibleMapPromptSize;
        public Vector2 ViewerCloseButtonSize => viewerCloseButtonSize;
        public Vector2 ViewerCloseButtonOffset => viewerCloseButtonOffset;

#if UNITY_EDITOR
        public void ConfigureCanonicalValuesForEditor()
        {
            panelBackground = new Color(0.08f, 0.07f, 0.055f, 0.94f);
            panelBackgroundLighter = new Color(0.14f, 0.1f, 0.06f, 0.96f);
            sectionTitleBackground = new Color(0.12f, 0.09f, 0.06f, 0.92f);
            scrollBackground = new Color(0.06f, 0.05f, 0.04f, 0.5f);
            buttonBackground = new Color(0.16f, 0.1f, 0.055f, 0.96f);
            disabledButtonBackground = new Color(0.09f, 0.075f, 0.06f, 0.72f);
            goldText = new Color(0.86f, 0.75f, 0.55f, 1f);
            goldOutline = new Color(0.78f, 0.63f, 0.38f, 0.85f);
            goldOutlineThin = new Color(0.78f, 0.63f, 0.38f, 0.65f);
            goldSeparator = new Color(0.78f, 0.63f, 0.38f, 0.7f);
            cyanAccent = new Color(0.12f, 0.88f, 1f, 1f);
            tacticalMapBackground = new Color(0.08f, 0.10f, 0.12f, 1f);
            darkShadow = new Color(0.06f, 0.04f, 0.025f, 0.9f);
            darkShadowLight = new Color(0.06f, 0.04f, 0.025f, 0.95f);
            labelText = new Color(0.7f, 0.65f, 0.55f, 1f);
            valueText = new Color(0.95f, 0.9f, 0.82f, 1f);
            trackBackground = new Color(0.1f, 0.1f, 0.09f, 0.88f);
            dangerBand = new Color(0.56f, 0.08f, 0.06f, 0.95f);
            safeBand = new Color(0.82f, 0.78f, 0.67f, 0.95f);
            gameOverOverlay = new Color(0f, 0f, 0f, 0.62f);
            gameOverDialog = new Color(0.16f, 0.1f, 0.055f, 0.98f);
            redPlayer = new Color(0.7019608f, 0f, 0.1137255f, 1f);
            bluePlayer = new Color(0.003921569f, 0.2705882f, 0.6980392f, 1f);
            greenPlayer = new Color(0.3764706f, 0.8235294f, 0.003921569f, 1f);
            yellowPlayer = new Color(1f, 0.7450981f, 0f, 1f);
            canvasReferenceResolution = new Vector2(1920f, 1080f);
            canvasMatchWidthOrHeight = 0.5f;
            dialogActionButtonSize = new Vector2(220f, 48f);
            collapsibleMapPromptSize = new Vector2(650f, 260f);
            viewerCloseButtonSize = new Vector2(42f, 42f);
            viewerCloseButtonOffset = new Vector2(-18f, -12f);
        }
#endif

        internal bool HasSameValues(UiThemeCatalog other)
        {
            return other != null && JsonUtility.ToJson(this) == JsonUtility.ToJson(other);
        }

        public Color GetPlayerColor(PlayerColor playerColor, float alpha)
        {
            Color color;
            switch (playerColor)
            {
                case PlayerColor.Red:
                    color = redPlayer;
                    break;
                case PlayerColor.Blue:
                    color = bluePlayer;
                    break;
                case PlayerColor.Green:
                    color = greenPlayer;
                    break;
                case PlayerColor.Yellow:
                    color = yellowPlayer;
                    break;
                default:
                    return Color.white;
            }

            color.a = alpha;
            return color;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            var colors = new[]
            {
                panelBackground, panelBackgroundLighter, sectionTitleBackground,
                scrollBackground, buttonBackground, disabledButtonBackground,
                goldText, goldOutline, goldOutlineThin, goldSeparator, cyanAccent,
                tacticalMapBackground, darkShadow, darkShadowLight, labelText, valueText,
                trackBackground, dangerBand, safeBand, gameOverOverlay, gameOverDialog,
                redPlayer, bluePlayer, greenPlayer, yellowPlayer
            };
            for (var index = 0; index < colors.Length; index++)
            {
                if (!IsNormalizedFinite(colors[index]))
                {
                    reason = "UI 主题包含无效或超出 0..1 范围的颜色。";
                    return false;
                }
            }

            if (!IsPositiveFinite(canvasReferenceResolution) ||
                !IsPositiveFinite(dialogActionButtonSize) ||
                !IsPositiveFinite(collapsibleMapPromptSize) ||
                !IsPositiveFinite(viewerCloseButtonSize))
            {
                reason = "UI 主题中的共享尺寸必须是有限正数。";
                return false;
            }

            if (!IsFinite(canvasMatchWidthOrHeight) ||
                canvasMatchWidthOrHeight < 0f || canvasMatchWidthOrHeight > 1f)
            {
                reason = "Canvas 宽高匹配值必须位于 0..1。";
                return false;
            }

            if (!IsFinite(viewerCloseButtonOffset.x) || !IsFinite(viewerCloseButtonOffset.y))
            {
                reason = "查看器关闭按钮偏移必须是有限数值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsNormalizedFinite(Color color)
        {
            return IsNormalized(color.r) && IsNormalized(color.g) &&
                   IsNormalized(color.b) && IsNormalized(color.a);
        }

        private static bool IsNormalized(float value)
        {
            return IsFinite(value) && value >= 0f && value <= 1f;
        }

        private static bool IsPositiveFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && value.x > 0f && value.y > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
