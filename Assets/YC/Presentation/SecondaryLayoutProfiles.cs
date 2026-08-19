using System;
using UnityEngine;

namespace YC.Presentation
{
    [Serializable]
    public struct SecondaryAnchorLayout
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;

        public void ApplyTo(RectTransform target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.anchorMin = AnchorMin;
            target.anchorMax = AnchorMax;
            target.pivot = Pivot;
        }

        internal bool TryValidate(out string reason)
        {
            if (!SecondaryLayoutProfileValidation.IsNormalized(AnchorMin) ||
                !SecondaryLayoutProfileValidation.IsNormalized(AnchorMax) ||
                !SecondaryLayoutProfileValidation.IsNormalized(Pivot) ||
                AnchorMin.x > AnchorMax.x || AnchorMin.y > AnchorMax.y)
            {
                reason = "次级布局锚点包含越界、逆序或非有限数值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public struct SecondaryRectLayout
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;
        public Vector2 SizeDelta;
        public Vector2 AnchoredPosition;

        public void ApplyTo(RectTransform target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.anchorMin = AnchorMin;
            target.anchorMax = AnchorMax;
            target.pivot = Pivot;
            target.sizeDelta = SizeDelta;
            target.anchoredPosition = AnchoredPosition;
        }

        internal bool TryValidate(bool requirePositiveSize, out string reason)
        {
            var anchors = new SecondaryAnchorLayout
            {
                AnchorMin = AnchorMin,
                AnchorMax = AnchorMax,
                Pivot = Pivot
            };
            if (!anchors.TryValidate(out reason) ||
                !SecondaryLayoutProfileValidation.IsFinite(SizeDelta) ||
                !SecondaryLayoutProfileValidation.IsFinite(AnchoredPosition))
            {
                reason = "次级 RectTransform 布局包含越界、逆序或非有限数值。";
                return false;
            }

            if (requirePositiveSize && (SizeDelta.x <= 0f || SizeDelta.y <= 0f))
            {
                reason = "次级 RectTransform 固定尺寸必须为正值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public struct CardDragGhostLayout
    {
        public SecondaryAnchorLayout RootLayout;
        public Vector2 FallbackOffsetMin;
        public Vector2 FallbackOffsetMax;
        public Vector2 FallbackOutlineDistance;

        internal bool TryValidate(out string reason)
        {
            if (!RootLayout.TryValidate(out reason) ||
                !SecondaryLayoutProfileValidation.IsFinite(FallbackOffsetMin) ||
                !SecondaryLayoutProfileValidation.IsFinite(FallbackOffsetMax) ||
                !SecondaryLayoutProfileValidation.IsFinite(FallbackOutlineDistance))
            {
                reason = "卡牌拖拽虚影布局包含无效数值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    public abstract class ActionPanelLayoutProfileBase : ScriptableObject
    {
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private Vector2 cardImageOffsetMin;
        [SerializeField] private Vector2 cardImageOffsetMax;
        [SerializeField] private SecondaryRectLayout characterContainerLayout;

        public string SourceManifestSha256 => sourceManifestSha256;
        public Vector2 CardImageOffsetMin => cardImageOffsetMin;
        public Vector2 CardImageOffsetMax => cardImageOffsetMax;
        public SecondaryRectLayout CharacterContainerLayout => characterContainerLayout;

        public bool TryValidateConfiguration(out string reason)
        {
            if (!SecondaryLayoutProfileValidation.IsSha256(sourceManifestSha256) ||
                !SecondaryLayoutProfileValidation.IsFinite(cardImageOffsetMin) ||
                !SecondaryLayoutProfileValidation.IsFinite(cardImageOffsetMax) ||
                !characterContainerLayout.TryValidate(true, out reason))
            {
                reason = "ActionPanelLayoutProfile 缺少有效 manifest 或布局数据。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string sourceSha256,
            Vector2 imageOffsetMin,
            Vector2 imageOffsetMax,
            SecondaryRectLayout containerLayout)
        {
            sourceManifestSha256 = sourceSha256 ?? string.Empty;
            cardImageOffsetMin = imageOffsetMin;
            cardImageOffsetMax = imageOffsetMax;
            characterContainerLayout = containerLayout;
        }
#endif
    }

    public abstract class CardInteractionLayoutProfileBase : ScriptableObject
    {
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private Vector2 pendingBuildGhostAnchorMin;
        [SerializeField] private Vector2 pendingBuildGhostAnchorMax;
        [SerializeField] private CardDragGhostLayout dragGhostLayout;
        [SerializeField] private SecondaryAnchorLayout externalCardAnchorLayout;
        [SerializeField] private Vector2 facilitySelectableOutlineDistance;
        [SerializeField] private Vector2 normalOutlineDistance;
        [SerializeField] private Vector2 legalCityBoardSlotOutlineDistance;
        [SerializeField] private Vector2 cityStyleBoardActiveOutlineDistance;
        [SerializeField] private Vector2 cityStyleBoardInactiveOutlineDistance;
        [SerializeField] private Vector2 cityStyleSelectedSlotOutlineDistance;
        [SerializeField] private Vector2 cityStyleSelectableSlotOutlineDistance;
        [SerializeField] private Vector2 cityStyleEmphasizedButtonOutlineDistance;

        public string SourceManifestSha256 => sourceManifestSha256;
        public Vector2 PendingBuildGhostAnchorMin => pendingBuildGhostAnchorMin;
        public Vector2 PendingBuildGhostAnchorMax => pendingBuildGhostAnchorMax;
        public CardDragGhostLayout DragGhostLayout => dragGhostLayout;
        public SecondaryAnchorLayout ExternalCardAnchorLayout => externalCardAnchorLayout;
        public Vector2 FacilitySelectableOutlineDistance => facilitySelectableOutlineDistance;
        public Vector2 NormalOutlineDistance => normalOutlineDistance;
        public Vector2 LegalCityBoardSlotOutlineDistance => legalCityBoardSlotOutlineDistance;
        public Vector2 CityStyleBoardActiveOutlineDistance => cityStyleBoardActiveOutlineDistance;
        public Vector2 CityStyleBoardInactiveOutlineDistance => cityStyleBoardInactiveOutlineDistance;
        public Vector2 CityStyleSelectedSlotOutlineDistance => cityStyleSelectedSlotOutlineDistance;
        public Vector2 CityStyleSelectableSlotOutlineDistance => cityStyleSelectableSlotOutlineDistance;
        public Vector2 CityStyleEmphasizedButtonOutlineDistance => cityStyleEmphasizedButtonOutlineDistance;

        public bool TryValidateConfiguration(out string reason)
        {
            if (!SecondaryLayoutProfileValidation.IsSha256(sourceManifestSha256) ||
                !SecondaryLayoutProfileValidation.IsNormalized(pendingBuildGhostAnchorMin) ||
                !SecondaryLayoutProfileValidation.IsNormalized(pendingBuildGhostAnchorMax) ||
                pendingBuildGhostAnchorMin.x > pendingBuildGhostAnchorMax.x ||
                pendingBuildGhostAnchorMin.y > pendingBuildGhostAnchorMax.y ||
                !dragGhostLayout.TryValidate(out reason) ||
                !externalCardAnchorLayout.TryValidate(out reason) ||
                !ValidateOutlineDistances())
            {
                reason = "CardInteractionLayoutProfile 缺少有效 manifest 或布局数据。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string sourceSha256,
            Vector2 pendingAnchorMin,
            Vector2 pendingAnchorMax,
            CardDragGhostLayout sharedDragGhostLayout,
            SecondaryAnchorLayout externalAnchorLayout,
            Vector2 selectableOutlineDistance,
            Vector2 standardOutlineDistance,
            Vector2 legalSlotOutlineDistance,
            Vector2 boardActiveOutlineDistance,
            Vector2 boardInactiveOutlineDistance,
            Vector2 selectedSlotOutlineDistance,
            Vector2 selectableSlotOutlineDistance,
            Vector2 emphasizedButtonOutlineDistance)
        {
            sourceManifestSha256 = sourceSha256 ?? string.Empty;
            pendingBuildGhostAnchorMin = pendingAnchorMin;
            pendingBuildGhostAnchorMax = pendingAnchorMax;
            dragGhostLayout = sharedDragGhostLayout;
            externalCardAnchorLayout = externalAnchorLayout;
            facilitySelectableOutlineDistance = selectableOutlineDistance;
            normalOutlineDistance = standardOutlineDistance;
            legalCityBoardSlotOutlineDistance = legalSlotOutlineDistance;
            cityStyleBoardActiveOutlineDistance = boardActiveOutlineDistance;
            cityStyleBoardInactiveOutlineDistance = boardInactiveOutlineDistance;
            cityStyleSelectedSlotOutlineDistance = selectedSlotOutlineDistance;
            cityStyleSelectableSlotOutlineDistance = selectableSlotOutlineDistance;
            cityStyleEmphasizedButtonOutlineDistance = emphasizedButtonOutlineDistance;
        }
#endif

        private bool ValidateOutlineDistances()
        {
            return SecondaryLayoutProfileValidation.IsFinite(facilitySelectableOutlineDistance) &&
                   SecondaryLayoutProfileValidation.IsFinite(normalOutlineDistance) &&
                   SecondaryLayoutProfileValidation.IsFinite(legalCityBoardSlotOutlineDistance) &&
                   SecondaryLayoutProfileValidation.IsFinite(cityStyleBoardActiveOutlineDistance) &&
                   SecondaryLayoutProfileValidation.IsFinite(cityStyleBoardInactiveOutlineDistance) &&
                   SecondaryLayoutProfileValidation.IsFinite(cityStyleSelectedSlotOutlineDistance) &&
                   SecondaryLayoutProfileValidation.IsFinite(cityStyleSelectableSlotOutlineDistance) &&
                   SecondaryLayoutProfileValidation.IsFinite(cityStyleEmphasizedButtonOutlineDistance);
        }
    }

    public abstract class ZoomableViewerLayoutProfileBase : ScriptableObject
    {
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private SecondaryRectLayout collapsedToggleLayout;
        [SerializeField] private SecondaryRectLayout expandedToggleLayout;
        [SerializeField] private Vector2 fallbackViewportSize;

        public string SourceManifestSha256 => sourceManifestSha256;
        public SecondaryRectLayout CollapsedToggleLayout => collapsedToggleLayout;
        public SecondaryRectLayout ExpandedToggleLayout => expandedToggleLayout;
        public Vector2 FallbackViewportSize => fallbackViewportSize;

        public bool TryValidateConfiguration(out string reason)
        {
            if (!SecondaryLayoutProfileValidation.IsSha256(sourceManifestSha256) ||
                !collapsedToggleLayout.TryValidate(true, out reason) ||
                !expandedToggleLayout.TryValidate(true, out reason) ||
                !SecondaryLayoutProfileValidation.IsFinite(fallbackViewportSize) ||
                fallbackViewportSize.x <= 0f || fallbackViewportSize.y <= 0f)
            {
                reason = "ZoomableViewerLayoutProfile 缺少有效 manifest 或布局数据。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string sourceSha256,
            SecondaryRectLayout collapsedLayout,
            SecondaryRectLayout expandedLayout,
            Vector2 viewportFallbackSize)
        {
            sourceManifestSha256 = sourceSha256 ?? string.Empty;
            collapsedToggleLayout = collapsedLayout;
            expandedToggleLayout = expandedLayout;
            fallbackViewportSize = viewportFallbackSize;
        }
#endif
    }

    internal static class SecondaryLayoutProfileValidation
    {
        public static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        public static bool IsNormalized(Vector2 value)
        {
            return IsFinite(value) && value.x >= 0f && value.x <= 1f && value.y >= 0f && value.y <= 1f;
        }

        public static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'A' && character <= 'F') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
