using System;
using UnityEngine;

namespace YC.Presentation
{
    [CreateAssetMenu(
        fileName = "CharacterHandLayoutProfile",
        menuName = "YC/Presentation/Character Hand Layout Profile")]
    public sealed class CharacterHandLayoutProfile : ScriptableObject
    {
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private Vector2 cardSize;
        [SerializeField] private float fanSpacing;
        [SerializeField] private float fanMaxAngle;
        [SerializeField] private float passiveVisibleFraction;
        [SerializeField] private float passiveAlpha;
        [SerializeField] private float hoverScale;
        [SerializeField] private float hoverAlpha;
        [SerializeField] private float hoverBottom;
        [SerializeField] private float expandedSpacing;
        [SerializeField] private float expandedMaxAngle;
        [SerializeField] private float expandedBottom;
        [SerializeField] private float fanCenterOffsetX;
        [SerializeField] private Vector2 overlayCardSize;
        [SerializeField] private Vector2 overlaySpacing;
        [SerializeField] private float discardAlpha;
        [SerializeField] private Vector2 discardButtonSize;
        [SerializeField] private Vector2 discardButtonPosition;
        [SerializeField] private Vector2 modalPanelSize;
        [SerializeField] private Vector2 modalPanelPosition;

        public string SourceManifestSha256 => sourceManifestSha256;
        public Vector2 CardSize => cardSize;
        public float FanSpacing => fanSpacing;
        public float FanMaxAngle => fanMaxAngle;
        public float PassiveVisibleFraction => passiveVisibleFraction;
        public float PassiveAlpha => passiveAlpha;
        public float HoverScale => hoverScale;
        public float HoverAlpha => hoverAlpha;
        public float HoverBottom => hoverBottom;
        public float ExpandedSpacing => expandedSpacing;
        public float ExpandedMaxAngle => expandedMaxAngle;
        public float ExpandedBottom => expandedBottom;
        public float FanCenterOffsetX => fanCenterOffsetX;
        public Vector2 OverlayCardSize => overlayCardSize;
        public Vector2 OverlaySpacing => overlaySpacing;
        public float DiscardAlpha => discardAlpha;
        public Vector2 DiscardButtonSize => discardButtonSize;
        public Vector2 DiscardButtonPosition => discardButtonPosition;
        public Vector2 ModalPanelSize => modalPanelSize;
        public Vector2 ModalPanelPosition => modalPanelPosition;

        public bool TryValidateConfiguration(out string reason)
        {
            if (!SecondaryLayoutProfileValidation.IsSha256(sourceManifestSha256) ||
                !IsPositive(cardSize) || !IsPositive(overlayCardSize) ||
                !IsNonNegative(overlaySpacing) || !IsPositive(discardButtonSize) ||
                !IsPositive(modalPanelSize) ||
                !SecondaryLayoutProfileValidation.IsFinite(discardButtonPosition) ||
                !SecondaryLayoutProfileValidation.IsFinite(modalPanelPosition) ||
                fanSpacing <= 0f || expandedSpacing <= 0f ||
                fanMaxAngle < 0f || expandedMaxAngle < 0f ||
                passiveVisibleFraction <= 0f || passiveVisibleFraction > 1f ||
                passiveAlpha <= 0f || passiveAlpha > 1f ||
                hoverScale < 1f || hoverAlpha <= passiveAlpha || hoverAlpha > 1f ||
                hoverBottom < 0f || expandedBottom < 0f ||
                float.IsNaN(fanCenterOffsetX) || float.IsInfinity(fanCenterOffsetX) ||
                discardAlpha <= 0f || discardAlpha > 1f)
            {
                reason = "CharacterHandLayoutProfile 缺少有效 manifest 或包含越界布局值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string sourceSha256,
            Vector2 configuredCardSize,
            float configuredFanSpacing,
            float configuredFanMaxAngle,
            float configuredPassiveVisibleFraction,
            float configuredPassiveAlpha,
            float configuredHoverScale,
            float configuredHoverAlpha,
            float configuredHoverBottom,
            float configuredExpandedSpacing,
            float configuredExpandedMaxAngle,
            float configuredExpandedBottom,
            float configuredFanCenterOffsetX,
            Vector2 configuredOverlayCardSize,
            Vector2 configuredOverlaySpacing,
            float configuredDiscardAlpha,
            Vector2 configuredDiscardButtonSize,
            Vector2 configuredDiscardButtonPosition,
            Vector2 configuredModalPanelSize,
            Vector2 configuredModalPanelPosition)
        {
            sourceManifestSha256 = sourceSha256 ?? string.Empty;
            cardSize = configuredCardSize;
            fanSpacing = configuredFanSpacing;
            fanMaxAngle = configuredFanMaxAngle;
            passiveVisibleFraction = configuredPassiveVisibleFraction;
            passiveAlpha = configuredPassiveAlpha;
            hoverScale = configuredHoverScale;
            hoverAlpha = configuredHoverAlpha;
            hoverBottom = configuredHoverBottom;
            expandedSpacing = configuredExpandedSpacing;
            expandedMaxAngle = configuredExpandedMaxAngle;
            expandedBottom = configuredExpandedBottom;
            fanCenterOffsetX = configuredFanCenterOffsetX;
            overlayCardSize = configuredOverlayCardSize;
            overlaySpacing = configuredOverlaySpacing;
            discardAlpha = configuredDiscardAlpha;
            discardButtonSize = configuredDiscardButtonSize;
            discardButtonPosition = configuredDiscardButtonPosition;
            modalPanelSize = configuredModalPanelSize;
            modalPanelPosition = configuredModalPanelPosition;
        }
#endif

        private static bool IsPositive(Vector2 value)
        {
            return SecondaryLayoutProfileValidation.IsFinite(value) && value.x > 0f && value.y > 0f;
        }

        private static bool IsNonNegative(Vector2 value)
        {
            return SecondaryLayoutProfileValidation.IsFinite(value) && value.x >= 0f && value.y >= 0f;
        }
    }
}
