using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Rules;

namespace YC.Presentation
{
    [CreateAssetMenu(fileName = "CardVisualCatalog", menuName = "YC/Presentation/Card Visual Catalog")]
    public sealed class CardVisualCatalog : ScriptableObject
    {
        public const int ExpectedFacilityCount = 46;
        public const int ExpectedCityStyleCount = 6;
        public const int ExpectedCharacterFrontCount = 5;
        public const int ExpectedCharacterBackCount = 4;
        public const int ExpectedTextureCount = 62;

        [Serializable]
        public sealed class IdTextureEntry
        {
            [SerializeField] private string id = string.Empty;
            [SerializeField] private Texture2D texture;

            public IdTextureEntry(string configuredId, Texture2D configuredTexture)
            {
                id = configuredId ?? string.Empty;
                texture = configuredTexture;
            }

            public string Id => id;
            public Texture2D Texture => texture;
        }

        [Serializable]
        public sealed class CharacterBackTextureEntry
        {
            [SerializeField] private PlayerColor color;
            [SerializeField] private Texture2D texture;

            public CharacterBackTextureEntry(PlayerColor configuredColor, Texture2D configuredTexture)
            {
                color = configuredColor;
                texture = configuredTexture;
            }

            public PlayerColor Color => color;
            public Texture2D Texture => texture;
        }

        [SerializeField] private IdTextureEntry[] facilityTextures = new IdTextureEntry[0];
        [SerializeField] private IdTextureEntry[] cityStyleTextures = new IdTextureEntry[0];
        [SerializeField] private IdTextureEntry[] characterFrontTextures = new IdTextureEntry[0];
        [SerializeField] private CharacterBackTextureEntry[] characterBackTextures =
            new CharacterBackTextureEntry[0];
        [SerializeField] private Texture2D cityBoardTexture;

        private IReadOnlyDictionary<string, Texture2D> facilities =
            new ReadOnlyDictionary<string, Texture2D>(new Dictionary<string, Texture2D>());
        private IReadOnlyDictionary<string, Texture2D> cityStyles =
            new ReadOnlyDictionary<string, Texture2D>(new Dictionary<string, Texture2D>());
        private IReadOnlyDictionary<string, Texture2D> characterFronts =
            new ReadOnlyDictionary<string, Texture2D>(new Dictionary<string, Texture2D>());
        private IReadOnlyDictionary<PlayerColor, Texture2D> characterBacks =
            new ReadOnlyDictionary<PlayerColor, Texture2D>(new Dictionary<PlayerColor, Texture2D>());

        public int FacilityCount => facilityTextures == null ? 0 : facilityTextures.Length;
        public int CityStyleCount => cityStyleTextures == null ? 0 : cityStyleTextures.Length;
        public int CharacterFrontCount => characterFrontTextures == null ? 0 : characterFrontTextures.Length;
        public int CharacterBackCount => characterBackTextures == null ? 0 : characterBackTextures.Length;
        public int TextureCount => FacilityCount + CityStyleCount + CharacterFrontCount +
                                   CharacterBackCount + (cityBoardTexture == null ? 0 : 1);

        private void OnEnable()
        {
#if UNITY_EDITOR
            if (!UnityEditor.AssetDatabase.Contains(this))
            {
                RebuildDictionaries(false);
                return;
            }
#endif
            RebuildDictionaries(true);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            var strict = UnityEditor.AssetDatabase.Contains(this);
            try
            {
                RebuildDictionaries(strict);
            }
            catch (Exception exception)
            {
                Debug.LogError("[CardVisualCatalog] " + exception.Message, this);
            }
        }

        public void ConfigureForEditor(
            IReadOnlyList<IdTextureEntry> configuredFacilities,
            IReadOnlyList<IdTextureEntry> configuredCityStyles,
            IReadOnlyList<IdTextureEntry> configuredCharacterFronts,
            IReadOnlyList<CharacterBackTextureEntry> configuredCharacterBacks,
            Texture2D configuredCityBoardTexture)
        {
            facilityTextures = Copy(configuredFacilities);
            cityStyleTextures = Copy(configuredCityStyles);
            characterFrontTextures = Copy(configuredCharacterFronts);
            characterBackTextures = Copy(configuredCharacterBacks);
            cityBoardTexture = configuredCityBoardTexture;
            RebuildDictionaries(true);
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        public bool TryValidateConfiguration(out string reason)
        {
            try
            {
                RebuildDictionaries(true);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        public Texture2D GetFacility(string facilityId)
        {
            return Get(facilities, facilityId);
        }

        public Texture2D GetCityStyle(string cityStyleId)
        {
            return Get(cityStyles, cityStyleId);
        }

        public Texture2D GetCharacterFront(string cardId)
        {
            var directTemplateTexture = Get(characterFronts, cardId);
            if (directTemplateTexture != null)
            {
                return directTemplateTexture;
            }

            var definition = CharacterCardDatabase.Get(cardId);
            return definition == null ? null : Get(characterFronts, definition.TemplateId);
        }

        public Texture2D GetCharacterBack(PlayerColor color)
        {
            Texture2D texture;
            return characterBacks.TryGetValue(color, out texture) ? texture : null;
        }

        public Texture2D GetCityBoard()
        {
            return cityBoardTexture;
        }

        private void RebuildDictionaries(bool strict)
        {
            var facilityDictionary = BuildIdDictionary(facilityTextures, "facility", strict);
            var cityStyleDictionary = BuildIdDictionary(cityStyleTextures, "city style", strict);
            var characterFrontDictionary = BuildIdDictionary(
                characterFrontTextures,
                "character front",
                strict);
            var characterBackDictionary = BuildCharacterBackDictionary(characterBackTextures, strict);

            if (strict)
            {
                RequireCount("facility", facilityDictionary.Count, ExpectedFacilityCount);
                RequireCount("city style", cityStyleDictionary.Count, ExpectedCityStyleCount);
                RequireCount("character front", characterFrontDictionary.Count, ExpectedCharacterFrontCount);
                RequireCount("character back", characterBackDictionary.Count, ExpectedCharacterBackCount);
                RequirePersistentTexture(cityBoardTexture, "city board");
            }

            facilities = new ReadOnlyDictionary<string, Texture2D>(facilityDictionary);
            cityStyles = new ReadOnlyDictionary<string, Texture2D>(cityStyleDictionary);
            characterFronts = new ReadOnlyDictionary<string, Texture2D>(characterFrontDictionary);
            characterBacks = new ReadOnlyDictionary<PlayerColor, Texture2D>(characterBackDictionary);
        }

        private static Dictionary<string, Texture2D> BuildIdDictionary(
            IEnumerable<IdTextureEntry> entries,
            string label,
            bool strict)
        {
            var result = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            if (entries == null)
            {
                if (strict) throw new InvalidOperationException(label + " entries are missing.");
                return result;
            }

            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Id))
                {
                    throw new InvalidOperationException(label + " entry has an empty id.");
                }

                RequirePersistentTexture(entry.Texture, label + " '" + entry.Id + "'");
                if (result.ContainsKey(entry.Id))
                {
                    throw new InvalidOperationException(label + " id is duplicated: " + entry.Id);
                }

                result.Add(entry.Id, entry.Texture);
            }

            return result;
        }

        private static Dictionary<PlayerColor, Texture2D> BuildCharacterBackDictionary(
            IEnumerable<CharacterBackTextureEntry> entries,
            bool strict)
        {
            var result = new Dictionary<PlayerColor, Texture2D>();
            if (entries == null)
            {
                if (strict) throw new InvalidOperationException("character back entries are missing.");
                return result;
            }

            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    throw new InvalidOperationException("character back entry is null.");
                }

                RequirePersistentTexture(entry.Texture, "character back '" + entry.Color + "'");
                if (result.ContainsKey(entry.Color))
                {
                    throw new InvalidOperationException("character back color is duplicated: " + entry.Color);
                }

                result.Add(entry.Color, entry.Texture);
            }

            return result;
        }

        private static void RequireCount(string label, int actual, int expected)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    label + " texture count must be " + expected + ", actual " + actual + ".");
            }
        }

        private static void RequirePersistentTexture(Texture2D texture, string label)
        {
            if (texture == null)
            {
                throw new InvalidOperationException(label + " texture is missing.");
            }

#if UNITY_EDITOR
            string guid;
            long localId;
            if (!UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out guid, out localId) ||
                string.IsNullOrEmpty(guid) || localId == 0)
            {
                throw new InvalidOperationException(label + " texture is not a persistent asset reference.");
            }
#endif
        }

        private static Texture2D Get(
            IReadOnlyDictionary<string, Texture2D> dictionary,
            string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Texture2D texture;
            return dictionary.TryGetValue(id, out texture) ? texture : null;
        }

#if UNITY_EDITOR
        private static T[] Copy<T>(IReadOnlyList<T> source)
        {
            if (source == null) return new T[0];
            var result = new T[source.Count];
            for (var i = 0; i < source.Count; i++) result[i] = source[i];
            return result;
        }
#endif
    }
}
