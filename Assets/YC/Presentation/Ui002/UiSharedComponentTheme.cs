using UnityEngine;

namespace YC.Presentation
{
    [CreateAssetMenu(fileName = "UiSharedComponentTheme", menuName = "YC/UI/Shared Component Theme")]
    public sealed class UiSharedComponentTheme : ScriptableObject
    {
        [Header("Separate fill and frame sprites")]
        public Sprite outerFrame;
        public Sprite moduleBackground;
        public Sprite moduleFrame;
        public Sprite paperBackground;
        public Sprite headerBackground;
        public Sprite drawerFrame;
        public Sprite cardBackground;
        public Sprite cardFrame;
        public Sprite primaryButton;
        public Sprite secondaryButton;
        public Sprite disabledButton;
        public Sprite selectedOverlay;
        public Sprite scrollbarTrack;
        public Sprite scrollbarThumb;

        [Header("Logical sizes and content spacing")]
        public Vector2 frameMinimum = new Vector2(320f, 200f);
        public Vector2 framePreferred = new Vector2(720f, 480f);
        public Vector4 frameContentInset = new Vector4(24f, 24f, 24f, 24f);
        public Vector2 buttonMinimum = new Vector2(120f, 44f);
        public Vector2 buttonPreferred = new Vector2(180f, 55f);
        public Vector4 buttonContentInset = new Vector4(20f, 10f, 20f, 10f);
        public Vector2 cardSlotPreferred = new Vector2(160f, 228f);
        public Vector2 scrollbarSize = new Vector2(14f, 14f);
        public float compactWidth = 900f;
        public float wideWidth = 1600f;
        public float layoutHysteresis = 24f;

        [Header("Derived states; no dedicated Frontier sprites")]
        public Color hoverTint = new Color(0.3f, 0.85f, 0.9f, 0.2f);
        public Color pressedTint = new Color(0.05f, 0.1f, 0.12f, 0.35f);
        public Color focusedTint = new Color(0.2f, 0.55f, 0.75f, 0.3f);
        public Color selectedTint = new Color(0.05f, 0.5f, 0.75f, 0.48f);
        public Color pendingTint = new Color(0.1f, 0.7f, 0.9f, 0.34f);
    }
}
