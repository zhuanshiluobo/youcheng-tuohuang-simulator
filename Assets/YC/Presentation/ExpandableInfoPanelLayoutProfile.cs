using System;
using UnityEngine;

namespace YC.Presentation
{
    [Serializable]
    public struct ExpandableInfoRectLayout
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

        internal bool TryValidate(out string reason)
        {
            if (!ExpandableInfoPanelLayoutProfile.IsNormalized(AnchorMin) ||
                !ExpandableInfoPanelLayoutProfile.IsNormalized(AnchorMax) ||
                !ExpandableInfoPanelLayoutProfile.IsNormalized(Pivot) ||
                AnchorMin.x > AnchorMax.x || AnchorMin.y > AnchorMax.y ||
                !ExpandableInfoPanelLayoutProfile.IsFinite(SizeDelta) ||
                !ExpandableInfoPanelLayoutProfile.IsFinite(AnchoredPosition))
            {
                reason = "信息面板 RectTransform 布局包含越界、逆序或非有限数值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public struct ExpandableInfoInsetLayout
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;
        public Vector2 OffsetMin;
        public Vector2 OffsetMax;

        public void ApplyTo(RectTransform target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.anchorMin = AnchorMin;
            target.anchorMax = AnchorMax;
            target.pivot = Pivot;
            target.offsetMin = OffsetMin;
            target.offsetMax = OffsetMax;
        }

        internal bool TryValidate(out string reason)
        {
            if (!ExpandableInfoPanelLayoutProfile.IsNormalized(AnchorMin) ||
                !ExpandableInfoPanelLayoutProfile.IsNormalized(AnchorMax) ||
                !ExpandableInfoPanelLayoutProfile.IsNormalized(Pivot) ||
                AnchorMin.x > AnchorMax.x || AnchorMin.y > AnchorMax.y ||
                !ExpandableInfoPanelLayoutProfile.IsFinite(OffsetMin) ||
                !ExpandableInfoPanelLayoutProfile.IsFinite(OffsetMax))
            {
                reason = "信息面板 Insets 布局包含越界、逆序或非有限数值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public sealed class ExpandableInfoPanelLayoutValues
    {
        public float ExpandedWidth;
        public float CollapsedWidth;
        public float RowHeight;
        public float ModuleContentWidth;
        public float ModuleContentLeftPadding;
        public float ModuleContentRightPadding;
        public float ModuleContentTopPadding;
        public float ModuleContentSpacing;
        public float PreviewMaxWidth;
        public float PreviewMaxHeight;
        public float PreviewFramePadding;
        public float PreviewRowExtraHeight;
        public float CardStripHeight;
        public float CardStripGap;
        public float CardStripMaxCardWidth;
        public float CardStripCardY;
        public float CardStripCardHeight;
        public float DragGhostScale;
        public Color DragGhostColor;
        public Vector2 TopLeftAnchor;
        public Vector2 CenterAnchor;
        public Vector2 TextRowOffset;
        public Vector2 CardStripOffset;
        public Vector2 EmptyModuleContentSize;

        public ExpandableInfoRectLayout PanelLayout;
        public Vector2 PanelOutlineDistance;
        public ExpandableInfoRectLayout ToggleLayout;
        public ExpandableInfoInsetLayout ContentAreaLayout;
        public ExpandableInfoRectLayout HeaderLayout;
        public Vector2 HeaderOutlineDistance;
        public ExpandableInfoRectLayout HeaderSeparatorLayout;
        public ExpandableInfoInsetLayout ScrollLayout;
        public ExpandableInfoRectLayout ScrollContentLayout;
        public ExpandableInfoRectLayout ModuleContentLayout;
        public ExpandableInfoRectLayout ModuleTitleLayout;
        public Vector2 ItemOutlineDistance;
        public Vector2 TextInsetMin;
        public Vector2 TextInsetMax;
        public Vector2 CardOutlineDistance;
    }

    [CreateAssetMenu(
        fileName = "ExpandableInfoPanelLayoutProfile",
        menuName = "YC/Presentation/Expandable Info Panel Layout Profile")]
    public sealed class ExpandableInfoPanelLayoutProfile : ScriptableObject
    {
        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private ExpandableInfoPanelLayoutValues values =
            new ExpandableInfoPanelLayoutValues();

        public string SourceManifestSha256 => sourceManifestSha256;
        public float ExpandedWidth => values.ExpandedWidth;
        public float CollapsedWidth => values.CollapsedWidth;
        public float RowHeight => values.RowHeight;
        public float ModuleContentWidth => values.ModuleContentWidth;
        public float ModuleContentLeftPadding => values.ModuleContentLeftPadding;
        public float ModuleContentRightPadding => values.ModuleContentRightPadding;
        public float ModuleContentTopPadding => values.ModuleContentTopPadding;
        public float ModuleContentSpacing => values.ModuleContentSpacing;
        public float PreviewMaxWidth => values.PreviewMaxWidth;
        public float PreviewMaxHeight => values.PreviewMaxHeight;
        public float PreviewFramePadding => values.PreviewFramePadding;
        public float PreviewRowExtraHeight => values.PreviewRowExtraHeight;
        public float CardStripHeight => values.CardStripHeight;
        public float CardStripGap => values.CardStripGap;
        public float CardStripMaxCardWidth => values.CardStripMaxCardWidth;
        public float CardStripCardY => values.CardStripCardY;
        public float CardStripCardHeight => values.CardStripCardHeight;
        public float DragGhostScale => values.DragGhostScale;
        public Color DragGhostColor => values.DragGhostColor;
        public Vector2 TopLeftAnchor => values.TopLeftAnchor;
        public Vector2 CenterAnchor => values.CenterAnchor;
        public Vector2 TextRowOffset => values.TextRowOffset;
        public Vector2 CardStripOffset => values.CardStripOffset;
        public Vector2 EmptyModuleContentSize => values.EmptyModuleContentSize;
        public ExpandableInfoRectLayout PanelLayout => values.PanelLayout;
        public Vector2 PanelOutlineDistance => values.PanelOutlineDistance;
        public ExpandableInfoRectLayout ToggleLayout => values.ToggleLayout;
        public ExpandableInfoInsetLayout ContentAreaLayout => values.ContentAreaLayout;
        public ExpandableInfoRectLayout HeaderLayout => values.HeaderLayout;
        public Vector2 HeaderOutlineDistance => values.HeaderOutlineDistance;
        public ExpandableInfoRectLayout HeaderSeparatorLayout => values.HeaderSeparatorLayout;
        public ExpandableInfoInsetLayout ScrollLayout => values.ScrollLayout;
        public ExpandableInfoRectLayout ScrollContentLayout => values.ScrollContentLayout;
        public ExpandableInfoRectLayout ModuleContentLayout => values.ModuleContentLayout;
        public ExpandableInfoRectLayout ModuleTitleLayout => values.ModuleTitleLayout;
        public Vector2 ItemOutlineDistance => values.ItemOutlineDistance;
        public Vector2 TextInsetMin => values.TextInsetMin;
        public Vector2 TextInsetMax => values.TextInsetMax;
        public Vector2 CardOutlineDistance => values.CardOutlineDistance;

        public bool TryValidateConfiguration(out string reason)
        {
            if (values == null || string.IsNullOrEmpty(sourceManifestSha256) ||
                sourceManifestSha256.Length != 64)
            {
                reason = "ExpandableInfoPanelLayoutProfile 缺少锁定 manifest 身份。";
                return false;
            }

            if (!PanelLayout.TryValidate(out reason) || !ToggleLayout.TryValidate(out reason) ||
                !ContentAreaLayout.TryValidate(out reason) || !HeaderLayout.TryValidate(out reason) ||
                !HeaderSeparatorLayout.TryValidate(out reason) || !ScrollLayout.TryValidate(out reason) ||
                !ScrollContentLayout.TryValidate(out reason) || !ModuleContentLayout.TryValidate(out reason) ||
                !ModuleTitleLayout.TryValidate(out reason))
            {
                return false;
            }

            if (ExpandedWidth <= CollapsedWidth || CollapsedWidth <= 0f || RowHeight <= 0f ||
                ModuleContentWidth <= 0f || ModuleContentLeftPadding < 0f ||
                ModuleContentRightPadding < 0f || ModuleContentTopPadding < 0f ||
                ModuleContentSpacing < 0f || PreviewMaxWidth <= 0f || PreviewMaxHeight <= 0f ||
                PreviewFramePadding < 0f || PreviewRowExtraHeight < 0f || CardStripHeight <= 0f ||
                CardStripGap < 0f || CardStripMaxCardWidth <= 0f || CardStripCardHeight <= 0f ||
                DragGhostScale <= 0f || !IsNormalized(TopLeftAnchor) || !IsNormalized(CenterAnchor) ||
                !IsFinite(TextRowOffset) || !IsFinite(CardStripOffset) ||
                !IsFinite(EmptyModuleContentSize) || !IsFinite(PanelOutlineDistance) ||
                !IsFinite(HeaderOutlineDistance) || !IsFinite(ItemOutlineDistance) ||
                !IsFinite(TextInsetMin) || !IsFinite(TextInsetMax) ||
                !IsFinite(CardOutlineDistance) || !IsFinite(DragGhostColor))
            {
                reason = "ExpandableInfoPanelLayoutProfile 包含越界或非有限固定布局值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string manifestSha256,
            ExpandableInfoPanelLayoutValues configuredValues)
        {
            sourceManifestSha256 = manifestSha256 ?? string.Empty;
            values = configuredValues ?? new ExpandableInfoPanelLayoutValues();
        }

        public bool MatchesValuesForEditor(ExpandableInfoPanelLayoutValues expected)
        {
            return expected != null && string.Equals(
                JsonUtility.ToJson(values),
                JsonUtility.ToJson(expected),
                StringComparison.Ordinal);
        }
#endif

        internal static bool IsNormalized(Vector2 value)
        {
            return IsFinite(value) && value.x >= 0f && value.x <= 1f &&
                   value.y >= 0f && value.y <= 1f;
        }

        internal static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        private static bool IsFinite(Color value)
        {
            return IsFinite(value.r) && IsFinite(value.g) &&
                   IsFinite(value.b) && IsFinite(value.a);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
