using System;
using System.Collections.Generic;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using UnityEngine;

namespace YC.Presentation.Maps
{
    public sealed class MapRouteDisplayController : MonoBehaviour
    {
        private static readonly Color EmptySlotColor = new Color(0.25f, 0.95f, 0.45f, 0.65f);

        private readonly Dictionary<string, List<SpriteRenderer>> routeSlotRenderers = new Dictionary<string, List<SpriteRenderer>>(StringComparer.Ordinal);

        [SerializeField] private SpriteRenderer mapRenderer;
        [SerializeField] private float slotZ = -0.24f;
        [SerializeField] private int slotSortingOrder = 14;

        private GameMapDefinition map;
        private GameState state;
        private Transform routeRoot;
        private Sprite slotSprite;

        private void Awake()
        {
            if (mapRenderer == null)
            {
                mapRenderer = GetComponent<SpriteRenderer>();
            }
        }

        public void Initialize(SpriteRenderer renderer, GameMapDefinition gameMap, GameState gameState)
        {
            mapRenderer = renderer != null ? renderer : mapRenderer;
            map = gameMap;
            state = gameState;

            if (map == null)
            {
                return;
            }

            Build();
            Refresh(state);
        }

        public void Refresh(GameState gameState)
        {
            state = gameState;

            foreach (var pair in routeSlotRenderers)
            {
                var routeId = pair.Key;
                var renderers = pair.Value;
                for (var i = 0; i < renderers.Count; i++)
                {
                    var slotId = InfluenceService.GetRouteSlotId(routeId, i);
                    var placement = FindInfluence(slotId);
                    renderers[i].color = placement == null ? EmptySlotColor : GetPlayerColor(placement.PlayerId);
                }
            }
        }

        private void Build()
        {
            if (mapRenderer == null || mapRenderer.sprite == null)
            {
                Debug.LogWarning("MapRouteDisplayController requires a SpriteRenderer with a map sprite.", this);
                return;
            }

            var definitions = GetDefinitions(map.MapId);
            var validationErrors = MapRouteDisplayDefinitionValidator.Validate(map, definitions);
            if (validationErrors.Count > 0)
            {
                Debug.LogError("Map route display configuration is invalid:\n" + string.Join("\n", validationErrors), this);
                return;
            }

            ClearRuntimeObjects();
            EnsureSlotSprite();

            routeRoot = new GameObject("Route Displays").transform;
            routeRoot.SetParent(transform, false);

            routeSlotRenderers.Clear();

            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                BuildRouteSlots(definition);
            }
        }

        private static IReadOnlyList<MapRouteDisplayDefinition> GetDefinitions(string mapId)
        {
            if (string.Equals(mapId, StaticMapDefinitions.FourPlayerMapId, StringComparison.Ordinal))
            {
                return FourPlayerRouteDisplayDefinitions.Create();
            }

            return new List<MapRouteDisplayDefinition>();
        }

        private void BuildRouteSlots(MapRouteDisplayDefinition definition)
        {
            var slotRenderers = new List<SpriteRenderer>(definition.InfluenceSlots.Count);
            for (var i = 0; i < definition.InfluenceSlots.Count; i++)
            {
                var slotObject = new GameObject("RouteInfluenceSlot " + definition.RouteId + ":" + i, typeof(SpriteRenderer));
                slotObject.transform.SetParent(routeRoot, false);
                slotObject.transform.position = ToWorldPosition(definition.InfluenceSlots[i].NormalizedPosition, slotZ);
                slotObject.transform.localScale = Vector3.one * definition.InfluenceSlots[i].Size;

                var renderer = slotObject.GetComponent<SpriteRenderer>();
                renderer.sprite = slotSprite;
                renderer.color = EmptySlotColor;
                renderer.sortingOrder = slotSortingOrder;
                slotRenderers.Add(renderer);
            }

            routeSlotRenderers.Add(definition.RouteId, slotRenderers);
        }

        private void ClearRuntimeObjects()
        {
            if (routeRoot == null)
            {
                var existing = transform.Find("Route Displays");
                routeRoot = existing;
            }

            if (routeRoot == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(routeRoot.gameObject);
            }
            else
            {
                DestroyImmediate(routeRoot.gameObject);
            }

            routeRoot = null;
        }

        private void EnsureSlotSprite()
        {
            if (slotSprite != null)
            {
                return;
            }

            slotSprite = CreateCircleSprite(24, 8f, 2f);
        }

        private Vector3 ToWorldPosition(Vector2 normalizedPosition, float z)
        {
            var bounds = mapRenderer.bounds;
            return new Vector3(
                bounds.min.x + bounds.size.x * normalizedPosition.x,
                bounds.max.y - bounds.size.y * normalizedPosition.y,
                z);
        }

        private InfluencePlacement FindInfluence(string slotId)
        {
            if (state == null || state.Map == null)
            {
                return null;
            }

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var placement = state.Map.Influences[i];
                if (string.Equals(placement.SlotId, slotId, StringComparison.Ordinal))
                {
                    return placement;
                }
            }

            return null;
        }

        private Color GetPlayerColor(int playerId)
        {
            if (state != null)
            {
                var player = state.FindPlayer(playerId);
                if (player != null)
                {
                    return ToUnityColor(player.Color);
                }
            }

            return new Color(0.9f, 0.3f, 0.3f, 0.85f);
        }

        private static Color ToUnityColor(PlayerColor playerColor)
        {
            switch (playerColor)
            {
                case PlayerColor.Red: return new Color(0.9f, 0.2f, 0.2f, 0.9f);
                case PlayerColor.Blue: return new Color(0.2f, 0.55f, 1f, 0.9f);
                case PlayerColor.Green: return new Color(0.2f, 0.85f, 0.35f, 0.9f);
                case PlayerColor.Yellow: return new Color(1f, 0.82f, 0.2f, 0.9f);
                default: return new Color(0.9f, 0.3f, 0.3f, 0.85f);
            }
        }

        private static Sprite CreateCircleSprite(int size, float radius, float thickness)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), center);
                    var alpha = distance <= radius && distance >= radius - thickness ? 1f : 0f;
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }
    }
}
