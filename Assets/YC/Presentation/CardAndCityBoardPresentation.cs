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
            ref CardViewer viewer,
            Transform owner,
            Sprite sprite)
        {
            if (owner == null || sprite == null)
            {
                return;
            }

            if (viewer == null)
            {
                viewer = CardViewer.InstantiateFor(owner);
                if (viewer == null)
                {
                    return;
                }
            }

            viewer.OpenInspect(sprite);
        }
    }

    internal static class CityBoardSlotLayout
    {
        public const int SlotCount = CardBoardVisualLayout.ExpectedCityBoardSlotCount;
        public const float SourceWidth = 2059f;
        public const float SourceHeight = 3801f;
        private const float SlotWidth = 600f;
        private const float SlotHeight = 850f;

        public static Vector2 GetCenter(CardBoardVisualLayout layout, int slotIndex)
        {
            RequireLayout(layout);
            var rect = GetSourceRect(slotIndex);
            return new Vector2(rect.center.x / SourceWidth, rect.center.y / SourceHeight);
        }

        // Source coordinates start at the artwork's upper left; UI anchors start at its lower left.
        public static Rect GetSourceRect(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SlotCount)
                throw new ArgumentOutOfRangeException(nameof(slotIndex));
            var column = slotIndex % 3;
            var row = slotIndex / 3;
            return new Rect(100f + column * 630f, 156f + row * 880f,
                SlotWidth, SlotHeight);
        }

        public static bool TryGetSlotIndex(
            RectTransform artwork, Vector2 screenPosition, Camera eventCamera, out int slotIndex)
        {
            slotIndex = -1;
            if (artwork == null || artwork.rect.width <= 0f || artwork.rect.height <= 0f ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    artwork, screenPosition, eventCamera, out var local))
                return false;

            var rect = artwork.rect;
            var source = new Vector2(
                (local.x - rect.xMin) * SourceWidth / rect.width,
                (rect.yMax - local.y) * SourceHeight / rect.height);
            if (source.x < 0f || source.y < 0f ||
                source.x >= SourceWidth || source.y >= SourceHeight) return false;
            for (var i = 0; i < SlotCount; i++)
            {
                if (!GetSourceRect(i).Contains(source)) continue;
                slotIndex = i;
                return true;
            }
            return false;
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
            var source = GetSourceRect(slotIndex);
            rect.anchorMin = new Vector2(
                source.xMin / SourceWidth,
                1f - source.yMax / SourceHeight);
            rect.anchorMax = new Vector2(
                source.xMax / SourceWidth,
                1f - source.yMin / SourceHeight);
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
            return layout.CityStyleVisuals.Bounds(cityStyleId, markerArea);
        }

        public static Vector2 ResolveAnchor(
            CardBoardVisualLayout layout, string cityStyleId, string markerArea,
            int areaMarkerIndex, int playerLaneIndex, int playerMarkerIndex)
        {
            RequireLayout(layout);
            var bounds = layout.CityStyleVisuals.Bounds(cityStyleId, markerArea);
            var lane = Mathf.Clamp(playerLaneIndex, 0, 3);
            return new Vector2(bounds.xMin + bounds.width * (.28f + lane * .173f), bounds.center.y);
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
            var bounds = layout.CityStyleVisuals == null ? Rect.zero : layout.CityStyleVisuals.Bounds(cityStyleId, placement.MarkerArea);
            var lane = layout.CityStyleVisuals.VisualLane(color);
            var width = Mathf.Min(bounds.width * .1484f, bounds.height * 600f / 930f * .8f);
            var center = ResolveAnchor(layout, cityStyleId, placement.MarkerArea, 0, lane, 0);
            rect.anchorMin = center - new Vector2(width, width * 930f / 600f) * .5f;
            rect.anchorMax = center + new Vector2(width, width * 930f / 600f) * .5f;
            var artwork = marker.GetComponentInParent<RawImage>(true);
            var overlay = artwork == null ? null : artwork.GetComponentInChildren<CityStyleTrackOverlay>(true);
            var slot = overlay == null ? null : overlay.FindSlot(cityStyleId, placement.MarkerArea,
                layout.CityStyleVisuals.PlayerIndex(color), layout.CityStyleVisuals);
            if (slot != null)
            {
                rect.SetParent(slot, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
            }
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var position = rect.anchoredPosition3D; position.z = 0; rect.anchoredPosition3D = position;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            marker.gameObject.name = objectName;
            var square = marker.GetComponent<InfluenceMarker2DView>();
            if (square != null)
                square.RenderPlayer((YC.Domain.Rules.PlayerColor)layout.CityStyleVisuals.PlayerIndex(color), placement.PlayerMarkerCount);
            else
                marker.sprite = layout.CityStyleVisuals.PlayerMarkerSprite(color);
            marker.type = Image.Type.Simple;
            marker.preserveAspect = true;
            marker.color = Color.white;
            marker.raycastTarget = false;
            marker.gameObject.SetActive(placement.PlayerMarkerIndex == 0 && bounds.width > 0);
            var label = marker.GetComponentInChildren<Text>(true);
            if (square == null && label != null) label.text = placement.PlayerMarkerCount.ToString();
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
