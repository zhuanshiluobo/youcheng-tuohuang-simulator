using System;
using UnityEngine;
using YC.Domain.Rules;

namespace YC.Presentation
{
    public static class UiTheme
    {
        private static UiThemeCatalog catalog;
        private static UiThemeCatalog snapshot;

        public static bool IsInitialized => snapshot != null;
        public static UiThemeCatalog Catalog => RequireCatalog();
        public static Color PanelBackground => RequireCatalog().PanelBackground;
        public static Color PanelBackgroundLighter => RequireCatalog().PanelBackgroundLighter;
        public static Color SectionTitleBackground => RequireCatalog().SectionTitleBackground;
        public static Color ScrollBackground => RequireCatalog().ScrollBackground;
        public static Color ButtonBackground => RequireCatalog().ButtonBackground;
        public static Color DisabledButtonBackground => RequireCatalog().DisabledButtonBackground;
        public static Color GoldText => RequireCatalog().GoldText;
        public static Color GoldOutline => RequireCatalog().GoldOutline;
        public static Color GoldOutlineThin => RequireCatalog().GoldOutlineThin;
        public static Color GoldSeparator => RequireCatalog().GoldSeparator;
        public static Color CyanAccent => RequireCatalog().CyanAccent;
        public static Color TacticalMapBg => RequireCatalog().TacticalMapBackground;
        public static Color DarkShadow => RequireCatalog().DarkShadow;
        public static Color DarkShadowLight => RequireCatalog().DarkShadowLight;
        public static Color LabelText => RequireCatalog().LabelText;
        public static Color ValueText => RequireCatalog().ValueText;
        public static Color TrackBackground => RequireCatalog().TrackBackground;
        public static Color DangerBand => RequireCatalog().DangerBand;
        public static Color SafeBand => RequireCatalog().SafeBand;
        public static Color GameOverOverlay => RequireCatalog().GameOverOverlay;
        public static Color GameOverDialog => RequireCatalog().GameOverDialog;
        public static Vector2 CanvasReferenceResolution => RequireCatalog().CanvasReferenceResolution;
        public static float CanvasMatchWidthOrHeight => RequireCatalog().CanvasMatchWidthOrHeight;
        public static Vector2 DialogActionButtonSize => RequireCatalog().DialogActionButtonSize;
        public static Vector2 CollapsibleMapPromptSize => RequireCatalog().CollapsibleMapPromptSize;
        public static Vector2 ViewerCloseButtonSize => RequireCatalog().ViewerCloseButtonSize;
        public static Vector2 ViewerCloseButtonOffset => RequireCatalog().ViewerCloseButtonOffset;

        public static void Initialize(UiThemeCatalog themeCatalog)
        {
            if (themeCatalog == null)
            {
                throw new ArgumentNullException(nameof(themeCatalog));
            }

            if (!themeCatalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("UiThemeCatalog 无效：" + reason);
            }

            if (snapshot != null && !snapshot.HasSameValues(themeCatalog))
            {
                throw new InvalidOperationException("UiTheme 已由不同数据的 UiThemeCatalog 初始化。");
            }

            if (snapshot == null)
            {
                snapshot = ScriptableObject.CreateInstance<UiThemeCatalog>();
                snapshot.hideFlags = HideFlags.HideAndDontSave;
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(themeCatalog), snapshot);
                catalog = themeCatalog;
            }
        }

        public static Color GetPlayerColor(PlayerColor playerColor, float alpha)
        {
            return RequireCatalog().GetPlayerColor(playerColor, alpha);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForRuntimeStart()
        {
            catalog = null;
            snapshot = null;
        }

#if UNITY_EDITOR
        public static void ResetForTests()
        {
            catalog = null;
            snapshot = null;
        }
#endif

        private static UiThemeCatalog RequireCatalog()
        {
            if (snapshot == null)
            {
                throw new InvalidOperationException(
                    "UiTheme 未初始化。场景必须通过 UiThemeBootstrap 注入 UiThemeCatalog。");
            }

            return snapshot;
        }
    }
}
