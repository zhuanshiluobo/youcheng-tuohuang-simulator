using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.CityStyles;
using YC.Domain.SpecialActions;

namespace YC.Presentation
{
    [CreateAssetMenu(
        fileName = "CardBoardVisualLayout",
        menuName = "YC/Presentation/Card Board Visual Layout")]
    public sealed class CardBoardVisualLayout : ScriptableObject
    {
        public const int ExpectedCityBoardSlotCount = 12;
        public const int ExpectedMarkerAnchorCount = 7;

        [SerializeField] private string sourceManifestSha256 = string.Empty;
        [SerializeField] private Vector2[] cityBoardSlotCenters = Array.Empty<Vector2>();
        [SerializeField] private float cityBoardSlotWidthRatio;
        [SerializeField] private float cityBoardSlotHeightRatio;
        [SerializeField] private MarkerAreaAnchor[] markerAreaAnchors = Array.Empty<MarkerAreaAnchor>();
        [SerializeField] private int regularMarkerColumnCount;
        [SerializeField] private float regularMarkerColumnSpacingX;
        [SerializeField] private float regularMarkerRowSpacingY;
        [SerializeField] private int militaryUnusedPlayerLaneCount;
        [SerializeField] private int militaryUnusedMarkerRowCount;
        [SerializeField] private float militaryUnusedFirstLaneX;
        [SerializeField] private float militaryUnusedLaneSpacingX;
        [SerializeField] private float militaryUnusedFirstMarkerY;
        [SerializeField] private float militaryUnusedMarkerSpacingY;
        [SerializeField] private Rect levelOneUsedSpecialActionArea;
        [SerializeField] private Rect levelTwoUsedFromTwoSpecialActionArea;
        [SerializeField] private Rect levelTwoUsedFromOneSpecialActionArea;
        [SerializeField] private Vector2 fallbackSpecialActionAreaHalfExtents;
        [SerializeField] private float buildInfoMarkerSizePixels;
        [SerializeField] private float declarationPreviewMarkerSizePixels;

        public string SourceManifestSha256 => sourceManifestSha256;
        public int CityBoardSlotCount => cityBoardSlotCenters == null ? 0 : cityBoardSlotCenters.Length;
        public float CityBoardSlotWidthRatio => cityBoardSlotWidthRatio;
        public float CityBoardSlotHeightRatio => cityBoardSlotHeightRatio;
        public int MarkerAreaAnchorCount => markerAreaAnchors == null ? 0 : markerAreaAnchors.Length;
        public int RegularMarkerColumnCount => regularMarkerColumnCount;
        public float RegularMarkerColumnSpacingX => regularMarkerColumnSpacingX;
        public float RegularMarkerRowSpacingY => regularMarkerRowSpacingY;
        public int MilitaryUnusedPlayerLaneCount => militaryUnusedPlayerLaneCount;
        public int MilitaryUnusedMarkerRowCount => militaryUnusedMarkerRowCount;
        public float MilitaryUnusedFirstLaneX => militaryUnusedFirstLaneX;
        public float MilitaryUnusedLaneSpacingX => militaryUnusedLaneSpacingX;
        public float MilitaryUnusedFirstMarkerY => militaryUnusedFirstMarkerY;
        public float MilitaryUnusedMarkerSpacingY => militaryUnusedMarkerSpacingY;
        public Rect LevelOneUsedSpecialActionArea => levelOneUsedSpecialActionArea;
        public Rect LevelTwoUsedFromTwoSpecialActionArea => levelTwoUsedFromTwoSpecialActionArea;
        public Rect LevelTwoUsedFromOneSpecialActionArea => levelTwoUsedFromOneSpecialActionArea;
        public Vector2 FallbackSpecialActionAreaHalfExtents => fallbackSpecialActionAreaHalfExtents;
        public Vector2 BuildInfoMarkerSize => Vector2.one * buildInfoMarkerSizePixels;
        public Vector2 DeclarationPreviewMarkerSize => Vector2.one * declarationPreviewMarkerSizePixels;

        public Vector2 GetCityBoardSlotCenter(int slotIndex)
        {
            EnsureValid();
            return cityBoardSlotCenters[Mathf.Clamp(slotIndex, 0, cityBoardSlotCenters.Length - 1)];
        }

        public Vector2 GetMarkerAreaAnchor(string markerArea)
        {
            EnsureValid();
            var requestedArea = string.IsNullOrEmpty(markerArea)
                ? CityStyleMarkerAreas.UsesOne
                : markerArea;
            for (var i = 0; i < markerAreaAnchors.Length; i++)
            {
                if (string.Equals(markerAreaAnchors[i].MarkerArea, requestedArea, StringComparison.Ordinal))
                {
                    return markerAreaAnchors[i].Anchor;
                }
            }

            for (var i = 0; i < markerAreaAnchors.Length; i++)
            {
                if (string.Equals(
                        markerAreaAnchors[i].MarkerArea,
                        CityStyleMarkerAreas.UsesOne,
                        StringComparison.Ordinal))
                {
                    return markerAreaAnchors[i].Anchor;
                }
            }

            throw new InvalidOperationException(
                "CardBoardVisualLayout 缺少稳定回退区域 " + CityStyleMarkerAreas.UsesOne + "。");
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (!IsSha256(sourceManifestSha256))
            {
                reason = "CardBoardVisualLayout 缺少有效 manifest SHA-256。";
                return false;
            }

            if (cityBoardSlotCenters == null ||
                cityBoardSlotCenters.Length != ExpectedCityBoardSlotCount)
            {
                reason = "CardBoardVisualLayout 必须包含 12 个有序城市面板槽位中心。";
                return false;
            }

            for (var i = 0; i < cityBoardSlotCenters.Length; i++)
            {
                if (!IsNormalizedPoint(cityBoardSlotCenters[i]))
                {
                    reason = "CardBoardVisualLayout 槽位中心越界或非有限值：" + i;
                    return false;
                }
            }

            if (!IsPositiveNormalized(cityBoardSlotWidthRatio) ||
                !IsPositiveNormalized(cityBoardSlotHeightRatio))
            {
                reason = "CardBoardVisualLayout 槽位宽高比必须位于 (0, 1]。";
                return false;
            }

            if (!ValidateMarkerAnchors(out reason))
            {
                return false;
            }

            if (regularMarkerColumnCount <= 0 ||
                !IsPositiveFinite(regularMarkerColumnSpacingX) ||
                !IsPositiveFinite(regularMarkerRowSpacingY) ||
                militaryUnusedPlayerLaneCount <= 0 || militaryUnusedMarkerRowCount <= 0 ||
                !IsFinite(militaryUnusedFirstLaneX) ||
                !IsPositiveFinite(militaryUnusedLaneSpacingX) ||
                !IsFinite(militaryUnusedFirstMarkerY) ||
                !IsPositiveFinite(militaryUnusedMarkerSpacingY))
            {
                reason = "CardBoardVisualLayout 标记行列或军工区布局参数无效。";
                return false;
            }

            if (!IsNormalizedRect(levelOneUsedSpecialActionArea) ||
                !IsNormalizedRect(levelTwoUsedFromTwoSpecialActionArea) ||
                !IsNormalizedRect(levelTwoUsedFromOneSpecialActionArea) ||
                !IsPositiveFinite(fallbackSpecialActionAreaHalfExtents.x) ||
                !IsPositiveFinite(fallbackSpecialActionAreaHalfExtents.y))
            {
                reason = "CardBoardVisualLayout 特殊行动区域无效。";
                return false;
            }

            if (!IsPositiveFinite(buildInfoMarkerSizePixels) ||
                !IsPositiveFinite(declarationPreviewMarkerSizePixels))
            {
                reason = "CardBoardVisualLayout 标记像素尺寸必须为正有限值。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            string sourceSha256,
            IReadOnlyList<Vector2> slotCenters,
            float slotWidthRatio,
            float slotHeightRatio,
            IReadOnlyList<MarkerAreaAnchor> anchors,
            int markerColumnCount,
            float markerColumnSpacingX,
            float markerRowSpacingY,
            int militaryLaneCount,
            int militaryRowCount,
            float militaryFirstLaneX,
            float militaryLaneSpacingX,
            float militaryFirstMarkerY,
            float militaryMarkerSpacingY,
            Rect levelOneArea,
            Rect levelTwoFromTwoArea,
            Rect levelTwoFromOneArea,
            Vector2 fallbackAreaHalfExtents,
            float compactMarkerSizePixels,
            float previewMarkerSizePixels)
        {
            sourceManifestSha256 = sourceSha256 ?? string.Empty;
            cityBoardSlotCenters = Copy(slotCenters);
            cityBoardSlotWidthRatio = slotWidthRatio;
            cityBoardSlotHeightRatio = slotHeightRatio;
            markerAreaAnchors = Copy(anchors);
            regularMarkerColumnCount = markerColumnCount;
            regularMarkerColumnSpacingX = markerColumnSpacingX;
            regularMarkerRowSpacingY = markerRowSpacingY;
            militaryUnusedPlayerLaneCount = militaryLaneCount;
            militaryUnusedMarkerRowCount = militaryRowCount;
            militaryUnusedFirstLaneX = militaryFirstLaneX;
            militaryUnusedLaneSpacingX = militaryLaneSpacingX;
            militaryUnusedFirstMarkerY = militaryFirstMarkerY;
            militaryUnusedMarkerSpacingY = militaryMarkerSpacingY;
            levelOneUsedSpecialActionArea = levelOneArea;
            levelTwoUsedFromTwoSpecialActionArea = levelTwoFromTwoArea;
            levelTwoUsedFromOneSpecialActionArea = levelTwoFromOneArea;
            fallbackSpecialActionAreaHalfExtents = fallbackAreaHalfExtents;
            buildInfoMarkerSizePixels = compactMarkerSizePixels;
            declarationPreviewMarkerSizePixels = previewMarkerSizePixels;
        }
#endif

        private void EnsureValid()
        {
            if (!TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(reason);
            }
        }

        private bool ValidateMarkerAnchors(out string reason)
        {
            if (markerAreaAnchors == null || markerAreaAnchors.Length != ExpectedMarkerAnchorCount)
            {
                reason = "CardBoardVisualLayout 必须包含 7 个有序且唯一的稳定标记区域锚点。";
                return false;
            }

            var required = new HashSet<string>(StringComparer.Ordinal)
            {
                CityStyleMarkerAreas.Unused,
                CityStyleMarkerAreas.Used,
                CityStyleMarkerAreas.UsesTwo,
                SpecialActionMarkerAreas.UsedFromTwo,
                CityStyleMarkerAreas.UsesOne,
                SpecialActionMarkerAreas.UsedFromOne,
                CityStyleMarkerAreas.UsesZero
            };
            var actual = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < markerAreaAnchors.Length; i++)
            {
                var entry = markerAreaAnchors[i];
                if (entry == null || string.IsNullOrEmpty(entry.MarkerArea) ||
                    !required.Contains(entry.MarkerArea) || !actual.Add(entry.MarkerArea) ||
                    !IsNormalizedPoint(entry.Anchor))
                {
                    reason = "CardBoardVisualLayout 标记区域锚点缺失、重复、未知或越界：" + i;
                    return false;
                }
            }

            if (!actual.SetEquals(required))
            {
                reason = "CardBoardVisualLayout 标记区域锚点未完整覆盖稳定 area id。";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsNormalizedRect(Rect value)
        {
            return IsFinite(value.xMin) && IsFinite(value.yMin) &&
                   IsFinite(value.xMax) && IsFinite(value.yMax) &&
                   value.xMin >= 0f && value.yMin >= 0f &&
                   value.xMax <= 1f && value.yMax <= 1f &&
                   value.width > 0f && value.height > 0f;
        }

        private static bool IsNormalizedPoint(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                   value.x >= 0f && value.x <= 1f && value.y >= 0f && value.y <= 1f;
        }

        private static bool IsPositiveNormalized(float value)
        {
            return IsPositiveFinite(value) && value <= 1f;
        }

        private static bool IsPositiveFinite(float value)
        {
            return IsFinite(value) && value > 0f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsSha256(string value)
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

#if UNITY_EDITOR
        private static Vector2[] Copy(IReadOnlyList<Vector2> source)
        {
            var result = new Vector2[source == null ? 0 : source.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = source[i];
            }

            return result;
        }

        private static MarkerAreaAnchor[] Copy(IReadOnlyList<MarkerAreaAnchor> source)
        {
            var result = new MarkerAreaAnchor[source == null ? 0 : source.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = source[i] == null
                    ? null
                    : new MarkerAreaAnchor
                    {
                        MarkerArea = source[i].MarkerArea,
                        Anchor = source[i].Anchor
                    };
            }

            return result;
        }
#endif
    }

    [Serializable]
    public sealed class MarkerAreaAnchor
    {
        public string MarkerArea = string.Empty;
        public Vector2 Anchor;
    }
}
