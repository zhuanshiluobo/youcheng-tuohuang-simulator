using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.SpecialActions;

namespace YC.Presentation
{
    /// <summary>建设卡在不同界面共用的单图预览入口。</summary>
    internal static class CardImagePreviewUtility
    {
        public static void Open(
            ref ZoomableImageViewerController viewer,
            Transform owner,
            string viewerObjectName,
            string viewerName,
            string cardName,
            Texture2D texture)
        {
            if (owner == null || texture == null)
            {
                return;
            }

            if (viewer == null)
            {
                viewer = ZoomableImageViewerController.InstantiateRegistered(owner, viewerObjectName);
                if (viewer == null)
                {
                    return;
                }
            }

            viewer.Configure(viewerName, cardName, 1, _ => texture);
            viewer.Open();
        }
    }

    internal static class CityBoardSlotLayout
    {
        public const int SlotCount = CardBoardVisualLayout.ExpectedCityBoardSlotCount;

        public static Vector2 GetCenter(CardBoardVisualLayout layout, int slotIndex)
        {
            RequireLayout(layout);
            return layout.GetCityBoardSlotCenter(slotIndex);
        }

        public static void Apply(
            RectTransform rect,
            CardBoardVisualLayout layout,
            int slotIndex)
        {
            if (rect == null)
            {
                throw new ArgumentNullException(nameof(rect));
            }

            RequireLayout(layout);
            var center = GetCenter(layout, slotIndex);
            var centerY = 1f - center.y;
            rect.anchorMin = new Vector2(
                center.x - layout.CityBoardSlotWidthRatio * 0.5f,
                centerY - layout.CityBoardSlotHeightRatio * 0.5f);
            rect.anchorMax = new Vector2(
                center.x + layout.CityBoardSlotWidthRatio * 0.5f,
                centerY + layout.CityBoardSlotHeightRatio * 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void RequireLayout(CardBoardVisualLayout layout)
        {
            string reason = null;
            if (layout == null || !layout.TryValidateConfiguration(out reason))
            {
                throw new InvalidOperationException(
                    "缺少有效 CardBoardVisualLayout：" +
                    (layout == null ? "引用为空。" : reason));
            }
        }
    }

    internal struct CityStyleMarkerPlacement
    {
        public CityStyleMarkerPlacement(
            string markerArea,
            int areaMarkerIndex,
            int playerLaneIndex,
            int playerMarkerIndex)
        {
            MarkerArea = markerArea ?? string.Empty;
            AreaMarkerIndex = areaMarkerIndex;
            PlayerLaneIndex = playerLaneIndex;
            PlayerMarkerIndex = playerMarkerIndex;
            PlayerMarkerCount = 3;
        }

        public string MarkerArea { get; private set; }

        public int AreaMarkerIndex { get; private set; }

        public int PlayerLaneIndex { get; private set; }

        public int PlayerMarkerIndex { get; private set; }

        public int PlayerMarkerCount { get; set; }
    }

    internal sealed class CityStyleMarkerLayoutTracker
    {
        private readonly CardBoardVisualLayout layout;
        private readonly Dictionary<string, int> areaCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, Dictionary<string, int>> playerAreaCounts =
            new Dictionary<int, Dictionary<string, int>>();

        public CityStyleMarkerLayoutTracker(CardBoardVisualLayout layout)
        {
            string reason = null;
            if (layout == null || !layout.TryValidateConfiguration(out reason))
            {
                throw new ArgumentException(
                    "CityStyleMarkerLayoutTracker 需要有效 CardBoardVisualLayout：" +
                    (layout == null ? "引用为空。" : reason),
                    nameof(layout));
            }

            this.layout = layout;
        }

        private readonly Dictionary<string, int> totals = new Dictionary<string, int>();

        public void Register(string cityStyleId, string markerArea, int playerId)
        {
            var key = cityStyleId + ":" + CityStyleMarkerRenderer.ResolveDisplayArea(cityStyleId, markerArea) + ":" + playerId;
            totals.TryGetValue(key, out var count);
            totals[key] = count + 1;
        }

        public CityStyleMarkerPlacement Next(string cityStyleId, string markerArea, int playerId)
        {
            var displayArea = CityStyleMarkerRenderer.ResolveDisplayArea(cityStyleId, markerArea);

            int areaMarkerIndex;
            if (!areaCounts.TryGetValue(displayArea, out areaMarkerIndex))
            {
                areaMarkerIndex = 0;
            }

            areaCounts[displayArea] = areaMarkerIndex + 1;

            Dictionary<string, int> countsForPlayer;
            if (!playerAreaCounts.TryGetValue(playerId, out countsForPlayer))
            {
                countsForPlayer = new Dictionary<string, int>(StringComparer.Ordinal);
                playerAreaCounts[playerId] = countsForPlayer;
            }

            int playerMarkerIndex;
            if (!countsForPlayer.TryGetValue(displayArea, out playerMarkerIndex))
            {
                playerMarkerIndex = 0;
            }

            countsForPlayer[displayArea] = playerMarkerIndex + 1;
            totals.TryGetValue(cityStyleId + ":" + displayArea + ":" + playerId, out var total);
            return new CityStyleMarkerPlacement(
                displayArea,
                areaMarkerIndex,
                CityStyleMarkerRenderer.ResolvePlayerLaneIndex(layout, playerId),
                playerMarkerIndex) { PlayerMarkerCount = Mathf.Max(1, total) };
        }
    }

    internal static class CityStyleMarkerRenderer
    {
        public static string ResolveDisplayArea(string cityStyleId, string markerArea)
        {
            if (cityStyleId == CityStyleDatabase.MilitaryIndustrialArea &&
                (string.IsNullOrEmpty(markerArea) || markerArea == CityStyleMarkerAreas.Declared))
                return CityStyleMarkerAreas.Unused;
            return string.IsNullOrEmpty(markerArea)
                ? CityStyleMarkerAreas.Declared
                : markerArea;
        }

        public static int ResolvePlayerLaneIndex(
            CardBoardVisualLayout layout,
            int playerId)
        {
            RequireLayout(layout);
            return Mathf.Clamp(playerId - 1, 0, layout.MilitaryUnusedPlayerLaneCount - 1);
        }

        public static Rect ResolveSpecialActionAreaBounds(
            CardBoardVisualLayout layout,
            string cityStyleId,
            string markerArea)
        {
            RequireLayout(layout);
            var definition = CityStyleDatabase.Get(cityStyleId);
            var isLevelTwo = definition != null && definition.Level >= 2;
            if (!isLevelTwo && markerArea == CityStyleMarkerAreas.Used)
            {
                return layout.LevelOneUsedSpecialActionArea;
            }

            if (isLevelTwo && markerArea == SpecialActionMarkerAreas.UsedFromTwo)
            {
                return layout.LevelTwoUsedFromTwoSpecialActionArea;
            }

            if (isLevelTwo && markerArea == SpecialActionMarkerAreas.UsedFromOne)
            {
                return layout.LevelTwoUsedFromOneSpecialActionArea;
            }

            var anchor = ResolveAnchor(layout, cityStyleId, markerArea, 0, 0, 0);
            var halfExtents = layout.FallbackSpecialActionAreaHalfExtents;
            return Rect.MinMaxRect(
                anchor.x - halfExtents.x,
                anchor.y - halfExtents.y,
                anchor.x + halfExtents.x,
                anchor.y + halfExtents.y);
        }

        public static Vector2 ResolveAnchor(
            CardBoardVisualLayout layout, string cityStyleId, string markerArea,
            int areaMarkerIndex, int playerLaneIndex, int playerMarkerIndex)
        {
            return ResolveGroupAnchor(layout, cityStyleId, markerArea,
                areaMarkerIndex, playerLaneIndex, playerMarkerIndex, 3);
        }

        private static Vector2 ResolveGroupAnchor(
            CardBoardVisualLayout layout, string cityStyleId, string markerArea,
            int areaMarkerIndex, int playerLaneIndex, int playerMarkerIndex,
            int playerMarkerCount)
        {
            RequireLayout(layout);
            var definition = CityStyleDatabase.Get(cityStyleId);
            var isLevelOne = definition != null && definition.Level < 2;
            // 同一玩家固定在同一列；移入已使用区不会横向跳位。
            var lane = Mathf.Clamp(playerLaneIndex, 0, layout.MilitaryUnusedPlayerLaneCount - 1);
            var x = layout.MilitaryUnusedFirstLaneX + lane * layout.MilitaryUnusedLaneSpacingX;
            if (isLevelOne)
            {
                var top = markerArea == CityStyleMarkerAreas.Used ? 0.40f : 0.84f;
                var spacing = 0.28f / Mathf.Max(2, playerMarkerCount - 1);
                return new Vector2(x, top - Mathf.Clamp(playerMarkerIndex, 0, Mathf.Max(2, playerMarkerCount - 1)) * spacing);
            }

            var baseAnchor = layout.GetMarkerAreaAnchor(markerArea);
            return new Vector2(x,
                baseAnchor.y + 0.025f - playerMarkerIndex * 0.05f / Mathf.Max(1, playerMarkerCount - 1));
        }

        public static void Configure(
            CardBoardVisualLayout layout,
            Image marker,
            string objectName,
            Color color,
            Sprite sprite,
            Vector2 size,
            string cityStyleId,
            CityStyleMarkerPlacement placement)
        {
            RequireLayout(layout);
            var rect = marker.rectTransform;
            var anchor = ResolveGroupAnchor(
                layout,
                cityStyleId,
                placement.MarkerArea,
                placement.AreaMarkerIndex,
                placement.PlayerLaneIndex,
                placement.PlayerMarkerIndex,
                placement.PlayerMarkerCount);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            var definition = CityStyleDatabase.Get(cityStyleId);
            var parentRect = rect.parent as RectTransform;
            var isLevelOne = definition != null && definition.Level < 2;
            var rowSpacing = isLevelOne
                ? 0.28f / Mathf.Max(2, placement.PlayerMarkerCount - 1)
                : 0.05f / Mathf.Max(1, placement.PlayerMarkerCount - 1);
            if (parentRect != null && parentRect.rect.height > 0f)
                size *= Mathf.Min(1f, parentRect.rect.height * rowSpacing * 0.85f / size.y);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            rect.anchoredPosition3D = Vector3.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            marker.gameObject.name = objectName;
            marker.sprite = null;
            marker.preserveAspect = true;
            marker.color = color;
            marker.raycastTarget = false;
            marker.gameObject.SetActive(true);
            InfluenceModelUiAnchor.Configure(marker, layout.InfluencePiecePrefab, color);
            var outline = marker.GetComponent<Outline>();
            if (outline != null)
            {
                outline.effectColor = Color.white;
                outline.effectDistance = new Vector2(1f, -1f);
            }
        }

        private static void RequireLayout(CardBoardVisualLayout layout)
        {
            string reason = null;
            if (layout == null || !layout.TryValidateConfiguration(out reason))
            {
                throw new InvalidOperationException(
                    "缺少有效 CardBoardVisualLayout：" +
                    (layout == null ? "引用为空。" : reason));
            }
        }
    }
}
