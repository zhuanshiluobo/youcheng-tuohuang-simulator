using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;

namespace YC.Presentation
{
    internal static class CardTextureCatalog
    {
        internal const string CityBoardImageRelativePath =
            "Assets/YC/Presentation/Resources/CardImages/Boards/city_board.png";

        private static readonly Dictionary<string, Texture2D> FacilityTextures =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Texture2D> CityStyleTextures =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Texture2D> RelativePathTextures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public static Texture2D LoadFacility(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId))
            {
                return null;
            }

            Texture2D cached;
            if (FacilityTextures.TryGetValue(facilityId, out cached))
            {
                return cached;
            }

            string relativePath;
            var definition = FacilityCardDatabase.Get(facilityId);
            if (definition == null ||
                !CardImagePathCatalog.TryGetFacilityImageRelativePath(facilityId, out relativePath))
            {
                return null;
            }

            var texture = LoadRelative(relativePath, definition.Name);
            if (texture != null)
            {
                FacilityTextures[facilityId] = texture;
            }

            return texture;
        }

        public static Texture2D LoadCityStyle(string cityStyleId, string fallbackName = "")
        {
            if (string.IsNullOrEmpty(cityStyleId))
            {
                return null;
            }

            Texture2D cached;
            if (CityStyleTextures.TryGetValue(cityStyleId, out cached))
            {
                return cached;
            }

            string relativePath;
            var definition = CityStyleDatabase.Get(cityStyleId);
            if (!CardImagePathCatalog.TryGetCityStyleImageRelativePath(cityStyleId, out relativePath))
            {
                return null;
            }

            var texture = LoadRelative(
                relativePath,
                definition == null ? fallbackName : definition.Name);
            if (texture != null)
            {
                CityStyleTextures[cityStyleId] = texture;
            }

            return texture;
        }

        public static Texture2D LoadCityBoard()
        {
            return LoadRelative(CityBoardImageRelativePath, "城市面板");
        }

        public static Texture2D LoadRelative(string relativePath, string textureName)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            Texture2D cached;
            if (RelativePathTextures.TryGetValue(relativePath, out cached))
            {
                return cached;
            }

            var resourcePath = TryGetResourcesPath(relativePath);
            if (!string.IsNullOrEmpty(resourcePath))
            {
                var resourceTexture = Resources.Load<Texture2D>(resourcePath);
                if (resourceTexture != null)
                {
                    RelativePathTextures[relativePath] = resourceTexture;
                    return resourceTexture;
                }
            }

            var candidates = new[]
            {
                Path.Combine(UnityEngine.Application.dataPath, "..", relativePath),
                Path.Combine(UnityEngine.Application.dataPath, "..", "..", "..", relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), relativePath),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", relativePath)
            };
            for (var i = 0; i < candidates.Length; i++)
            {
                var fullPath = Path.GetFullPath(candidates[i]);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(File.ReadAllBytes(fullPath)))
                {
                    texture.name = textureName ?? string.Empty;
                    RelativePathTextures[relativePath] = texture;
                    return texture;
                }

                UnityEngine.Object.Destroy(texture);
            }

            return null;
        }

        private static string TryGetResourcesPath(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/');
            const string marker = "/Resources/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            var result = normalized.Substring(markerIndex + marker.Length);
            var extension = Path.GetExtension(result);
            return string.IsNullOrEmpty(extension)
                ? result
                : result.Substring(0, result.Length - extension.Length);
        }
    }

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
                var viewerObject = new GameObject(viewerObjectName);
                viewerObject.transform.SetParent(owner, false);
                viewer = viewerObject.AddComponent<ZoomableImageViewerController>();
            }

            viewer.Configure(viewerName, cardName, 1, _ => texture);
            viewer.Open();
        }
    }

    internal static class CityBoardSlotLayout
    {
        public const int SlotCount = 12;
        public const float WidthRatio = 0.292f;
        public const float HeightRatio = 0.224f;

        private static readonly Vector2[] Centers =
        {
            new Vector2(0.176f, 0.152f),
            new Vector2(0.502f, 0.152f),
            new Vector2(0.827f, 0.152f),
            new Vector2(0.176f, 0.383f),
            new Vector2(0.502f, 0.383f),
            new Vector2(0.827f, 0.383f),
            new Vector2(0.176f, 0.615f),
            new Vector2(0.502f, 0.615f),
            new Vector2(0.827f, 0.615f),
            new Vector2(0.176f, 0.846f),
            new Vector2(0.502f, 0.846f),
            new Vector2(0.827f, 0.846f)
        };

        public static Vector2 GetCenter(int slotIndex)
        {
            return Centers[Mathf.Clamp(slotIndex, 0, Centers.Length - 1)];
        }

        public static void Apply(RectTransform rect, int slotIndex)
        {
            var center = GetCenter(slotIndex);
            var centerY = 1f - center.y;
            rect.anchorMin = new Vector2(
                center.x - WidthRatio * 0.5f,
                centerY - HeightRatio * 0.5f);
            rect.anchorMax = new Vector2(
                center.x + WidthRatio * 0.5f,
                centerY + HeightRatio * 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
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
        }

        public string MarkerArea { get; private set; }

        public int AreaMarkerIndex { get; private set; }

        public int PlayerLaneIndex { get; private set; }

        public int PlayerMarkerIndex { get; private set; }
    }

    internal sealed class CityStyleMarkerLayoutTracker
    {
        private readonly Dictionary<string, int> areaCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, Dictionary<string, int>> playerAreaCounts =
            new Dictionary<int, Dictionary<string, int>>();

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
            return new CityStyleMarkerPlacement(
                displayArea,
                areaMarkerIndex,
                CityStyleMarkerRenderer.ResolvePlayerLaneIndex(playerId),
                playerMarkerIndex);
        }
    }

    internal static class CityStyleMarkerRenderer
    {
        public const int MilitaryUnusedPlayerLaneCount = 4;
        public const int MilitaryUnusedMarkerRowCount = 3;
        public const float MilitaryUnusedFirstLaneX = 0.62f;
        public const float MilitaryUnusedLaneSpacingX = 0.09f;
        public const float MilitaryUnusedFirstMarkerY = 0.84f;
        public const float MilitaryUnusedMarkerSpacingY = 0.14f;
        private const float SpecialActionAreaMinX = 0.54f;
        private const float SpecialActionAreaMaxX = 0.945f;
        private const float LevelOneUsedAreaMinY = 0.08f;
        private const float LevelOneUsedAreaMaxY = 0.485f;
        private const float LevelTwoUsedFromTwoAreaMinY = 0.60f;
        private const float LevelTwoUsedFromTwoAreaMaxY = 0.75f;
        private const float LevelTwoUsedFromOneAreaMinY = 0.23f;
        private const float LevelTwoUsedFromOneAreaMaxY = 0.38f;

        public static string ResolveDisplayArea(string cityStyleId, string markerArea)
        {
            return string.IsNullOrEmpty(markerArea)
                ? CityStyleMarkerAreas.Declared
                : markerArea;
        }

        public static int ResolvePlayerLaneIndex(int playerId)
        {
            return Mathf.Clamp(playerId - 1, 0, MilitaryUnusedPlayerLaneCount - 1);
        }

        public static Rect ResolveSpecialActionAreaBounds(
            string cityStyleId,
            string markerArea)
        {
            var definition = CityStyleDatabase.Get(cityStyleId);
            var isLevelTwo = definition != null && definition.Level >= 2;
            if (!isLevelTwo && markerArea == CityStyleMarkerAreas.Used)
            {
                return Rect.MinMaxRect(
                    SpecialActionAreaMinX,
                    LevelOneUsedAreaMinY,
                    SpecialActionAreaMaxX,
                    LevelOneUsedAreaMaxY);
            }

            if (isLevelTwo && markerArea == SpecialActionMarkerAreas.UsedFromTwo)
            {
                return Rect.MinMaxRect(
                    SpecialActionAreaMinX,
                    LevelTwoUsedFromTwoAreaMinY,
                    SpecialActionAreaMaxX,
                    LevelTwoUsedFromTwoAreaMaxY);
            }

            if (isLevelTwo && markerArea == SpecialActionMarkerAreas.UsedFromOne)
            {
                return Rect.MinMaxRect(
                    SpecialActionAreaMinX,
                    LevelTwoUsedFromOneAreaMinY,
                    SpecialActionAreaMaxX,
                    LevelTwoUsedFromOneAreaMaxY);
            }

            var anchor = ResolveAnchor(cityStyleId, markerArea, 0, 0, 0);
            return Rect.MinMaxRect(
                anchor.x - 0.09f,
                anchor.y - 0.07f,
                anchor.x + 0.09f,
                anchor.y + 0.07f);
        }

        public static Vector2 ResolveAnchor(
            string cityStyleId,
            string markerArea,
            int areaMarkerIndex,
            int playerLaneIndex,
            int playerMarkerIndex)
        {
            if (cityStyleId == CityStyleDatabase.MilitaryIndustrialArea &&
                markerArea == CityStyleMarkerAreas.Unused)
            {
                // Four player lanes and three marker rows fit the compact 143x91 card preview.
                var laneIndex = Mathf.Clamp(
                    playerLaneIndex,
                    0,
                    MilitaryUnusedPlayerLaneCount - 1);
                var rowIndex = Mathf.Clamp(
                    playerMarkerIndex,
                    0,
                    MilitaryUnusedMarkerRowCount - 1);
                return new Vector2(
                    MilitaryUnusedFirstLaneX + laneIndex * MilitaryUnusedLaneSpacingX,
                    MilitaryUnusedFirstMarkerY - rowIndex * MilitaryUnusedMarkerSpacingY);
            }

            Vector2 baseAnchor;
            switch (markerArea)
            {
                case CityStyleMarkerAreas.Unused:
                    baseAnchor = new Vector2(0.69f, 0.73f);
                    break;
                case CityStyleMarkerAreas.Used:
                    baseAnchor = new Vector2(0.69f, 0.28f);
                    break;
                case CityStyleMarkerAreas.UsesTwo:
                    baseAnchor = new Vector2(0.69f, 0.82f);
                    break;
                case SpecialActionMarkerAreas.UsedFromTwo:
                    baseAnchor = new Vector2(0.69f, 0.66f);
                    break;
                case CityStyleMarkerAreas.UsesOne:
                    baseAnchor = new Vector2(0.69f, 0.50f);
                    break;
                case SpecialActionMarkerAreas.UsedFromOne:
                    baseAnchor = new Vector2(0.69f, 0.34f);
                    break;
                case CityStyleMarkerAreas.UsesZero:
                    baseAnchor = new Vector2(0.69f, 0.18f);
                    break;
                default:
                    baseAnchor = new Vector2(0.69f, 0.50f);
                    break;
            }

            return new Vector2(
                baseAnchor.x + (areaMarkerIndex % 4) * 0.055f,
                baseAnchor.y - (areaMarkerIndex / 4) * 0.11f);
        }

        public static void Configure(
            Image marker,
            string objectName,
            Color color,
            Sprite sprite,
            Vector2 size,
            string cityStyleId,
            CityStyleMarkerPlacement placement)
        {
            var rect = marker.rectTransform;
            var anchor = ResolveAnchor(
                cityStyleId,
                placement.MarkerArea,
                placement.AreaMarkerIndex,
                placement.PlayerLaneIndex,
                placement.PlayerMarkerIndex);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            marker.gameObject.name = objectName;
            marker.sprite = sprite;
            marker.color = color;
            marker.raycastTarget = false;
            marker.gameObject.SetActive(true);
            var outline = marker.GetComponent<Outline>();
            if (outline != null)
            {
                outline.effectColor = Color.white;
                outline.effectDistance = new Vector2(1f, -1f);
            }
        }
    }
}
