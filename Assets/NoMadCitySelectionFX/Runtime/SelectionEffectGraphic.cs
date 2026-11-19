using UnityEngine;
using UnityEngine.UI;

namespace NoMadCity.SelectionFX
{
    /// Decorative child only. Selection authority remains with the game's rule layer.
    [AddComponentMenu("UI/NoMadCity/Selection Effect")]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SelectionEffectGraphic : MaskableGraphic
    {
        public enum SelectionState { Available, Selected, Unavailable }
        [SerializeField] private SelectionState state;
        [SerializeField, Min(0.5f)] private float lineWidth = 3f;
        [SerializeField, Min(0)] private float cornerCut = 7f;
        [SerializeField, Min(1)] private float glowRadius = 10f;
        [SerializeField] private bool reducedMotion;
        [Tooltip("Optional dedicated selection badge only. Never bind original board/content UI here.")]
        [SerializeField] private GameObject selectedBadge;

        private bool hovered, focused;
        private float selectedAt = -100f;
        private Material ownedMaterial;
        public SelectionState State => state;
        public bool ReducedMotion => reducedMotion;
        public float LastUploadedAnimationTime { get; private set; }
        public bool HasEffectMaterial => ownedMaterial != null;

        // Explicit opt-in for existing serialized prefabs: changing field defaults is not enough.
        [ContextMenu("Apply Strong Visible Preset")]
        public void ApplyStrongVisiblePreset()
        {
            lineWidth = 3f; cornerCut = 7f; glowRadius = 10f;
            color = new Color(1f, .69f, .15f, 1f);
            SetVerticesDirty();
        }
        [ContextMenu("Debug/Force Selected")]
        public void DebugForceSelected() { SetState(SelectionState.Selected); ReplaySelection(); }
        [ContextMenu("Debug/Force Available")]
        public void DebugForceAvailable() { SetState(SelectionState.Available); }
        public void ReplaySelection()
        { selectedAt = Time.unscaledTime; SetVerticesDirty(); }

        protected override void OnEnable()
        {
            base.OnEnable();
            raycastTarget = false;
            var shader = Resources.Load<Shader>("NoMadCitySelection");
            if (shader != null)
            {
                ownedMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                material = ownedMaterial;
            }
            else Debug.LogError("NoMadCitySelection.shader missing from Resources.", this);
            if (state == SelectionState.Selected) selectedAt = Time.unscaledTime;
            EnableChannels(); UpdateBadges(); SetVerticesDirty();
        }
        protected override void OnDisable()
        {
            base.OnDisable(); material = null;
            if (ownedMaterial != null)
            {
                if (Application.isPlaying) Destroy(ownedMaterial); else DestroyImmediate(ownedMaterial);
                ownedMaterial = null;
            }
        }
        protected override void OnCanvasHierarchyChanged()
        { base.OnCanvasHierarchyChanged(); EnableChannels(); }
        private void EnableChannels()
        {
            if (canvas != null) canvas.additionalShaderChannels |=
                AdditionalCanvasShaderChannels.TexCoord1 |
                AdditionalCanvasShaderChannels.TexCoord2 |
                AdditionalCanvasShaderChannels.TexCoord3;
        }
        public void SetState(SelectionState value)
        {
            if (state == value) return;
            state = value;
            if (state == SelectionState.Selected) selectedAt = Time.unscaledTime;
            UpdateBadges(); SetVerticesDirty();
        }
        public void SetHovered(bool value) { if (hovered != value) { hovered = value; SetVerticesDirty(); } }
        public void SetFocused(bool value) { if (focused != value) { focused = value; SetVerticesDirty(); } }
        public void SetReducedMotion(bool value) { reducedMotion = value; SetVerticesDirty(); }
        private void UpdateBadges()
        {
            if (selectedBadge != null) selectedBadge.SetActive(state == SelectionState.Selected);
            // Unavailable reasons/badges belong to the original game UI, not this overlay.
        }
        private void Update()
        {
            if (!reducedMotion && state == SelectionState.Selected) SetVerticesDirty();
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            // v3: this is a SELECTED-ONLY overlay. Clear existing geometry on deselection,
            // including hover/focus/disabled outlines, without touching the original board.
            if (state != SelectionState.Selected)
            {
                LastUploadedAnimationTime = 0f;
                return;
            }
            Rect r = rectTransform.rect;
            if (r.width <= 0 || r.height <= 0) return;
            float pad = Mathf.Max(6f, glowRadius * 2f);
            float w = r.width * .5f, h = r.height * .5f;
            float flash = reducedMotion ? 0 : Mathf.Clamp01(1 - (Time.unscaledTime - selectedAt) / .35f);
            var flags = new Vector4(state == SelectionState.Selected ? 1 : 0,
                hovered && state != SelectionState.Unavailable ? 1 : 0,
                state == SelectionState.Unavailable ? 1 : 0, focused ? 1 : 0);
            LastUploadedAnimationTime = reducedMotion ? 0 : Mathf.Repeat(Mathf.Max(0f, Time.unscaledTime - selectedAt), 48f);
            var anim = new Vector4(LastUploadedAnimationTime, flash,
                Mathf.Max(.5f, lineWidth), Mathf.Clamp(cornerCut, 0, Mathf.Min(w,h)));
            for (int n = 0; n < 4; n++)
            {
                float x = (n == 0 || n == 1) ? -w-pad : w+pad;
                float y = (n == 0 || n == 3) ? -h-pad : h+pad;
                UIVertex v = UIVertex.simpleVert;
                v.position = new Vector3(r.center.x+x, r.center.y+y, 0); v.color = color;
                v.uv0 = new Vector4(x,y,w,h); v.uv1 = flags; v.uv2 = anim;
                v.uv3 = new Vector4(glowRadius, reducedMotion ? 0 : 1, 0, 0); vh.AddVert(v);
            }
            vh.AddTriangle(0,1,2); vh.AddTriangle(2,3,0);
        }
#if UNITY_EDITOR
        protected override void OnValidate()
        { base.OnValidate(); raycastTarget = false; EnableChannels(); UpdateBadges(); SetVerticesDirty(); }
#endif
    }
}
