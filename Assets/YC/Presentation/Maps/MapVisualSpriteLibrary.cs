using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Rules;

namespace YC.Presentation.Maps
{
    [CreateAssetMenu(fileName = "MapVisualSpriteLibrary", menuName = "YC/Maps/Map Visual Sprite Library")]
    public sealed class MapVisualSpriteLibrary : ScriptableObject
    {
        [SerializeField] private Sprite hotspot;
        [SerializeField] private Sprite emptyInfluenceSlot;
        [SerializeField] private Sprite occupiedInfluenceSlot;
        [SerializeField] private Sprite movableInfluenceBorder;
        [SerializeField] private Sprite placementFeedbackRing;
        [SerializeField] private Sprite mobileCity;
        [SerializeField] private Sprite scoreMarker;
        [SerializeField] private Sprite scoreMarkerBorder;
        [SerializeField] private List<MapResourceTokenSpriteBinding> resourceTokens =
            new List<MapResourceTokenSpriteBinding>();

        // Generated textures are retained explicitly so every generated sub-sprite has a
        // persistent owner and survives player builds without runtime texture creation.
        [SerializeField, HideInInspector] private List<Texture2D> retainedTextures = new List<Texture2D>();
        [SerializeField, HideInInspector] private List<Sprite> retainedSprites = new List<Sprite>();

        public Sprite Hotspot => hotspot;
        public Sprite EmptyInfluenceSlot => emptyInfluenceSlot;
        public Sprite OccupiedInfluenceSlot => occupiedInfluenceSlot;
        public Sprite MovableInfluenceBorder => movableInfluenceBorder;
        public Sprite PlacementFeedbackRing => placementFeedbackRing;
        public Sprite MobileCity => mobileCity;
        public Sprite ScoreMarker => scoreMarker;
        public Sprite ScoreMarkerBorder => scoreMarkerBorder;
        public IReadOnlyList<MapResourceTokenSpriteBinding> ResourceTokens => resourceTokens;

        public bool TryGetResourceTokenSprite(ResourceType resourceType, int amount, out Sprite sprite)
        {
            Sprite fallback = null;
            for (var i = 0; i < resourceTokens.Count; i++)
            {
                var binding = resourceTokens[i];
                if (binding == null || binding.ResourceType != resourceType)
                {
                    continue;
                }

                if (binding.Amount == amount)
                {
                    sprite = binding.Sprite;
                    return sprite != null;
                }

                if (binding.Amount == 0)
                {
                    fallback = binding.Sprite;
                }
            }

            sprite = fallback;
            return sprite != null;
        }

        public bool TryValidateConfiguration(out string reason)
        {
            if (hotspot == null || emptyInfluenceSlot == null || occupiedInfluenceSlot == null ||
                movableInfluenceBorder == null || placementFeedbackRing == null || mobileCity == null ||
                scoreMarker == null || scoreMarkerBorder == null)
            {
                reason = "Map visual sprite library has missing generated sprites.";
                return false;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < resourceTokens.Count; i++)
            {
                var binding = resourceTokens[i];
                if (binding == null || binding.Sprite == null || binding.Amount < 0)
                {
                    reason = "Map visual sprite library has an invalid resource token entry at index " + i + ".";
                    return false;
                }

                var key = binding.ResourceType + ":" + binding.Amount;
                if (!keys.Add(key))
                {
                    reason = "Map visual sprite library has a duplicate resource token entry " + key + ".";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
    }

    [Serializable]
    public sealed class MapResourceTokenSpriteBinding
    {
        [SerializeField] private ResourceType resourceType;
        [SerializeField] private int amount;
        [SerializeField] private Sprite sprite;

        public ResourceType ResourceType => resourceType;
        public int Amount => amount;
        public Sprite Sprite => sprite;
    }
}
