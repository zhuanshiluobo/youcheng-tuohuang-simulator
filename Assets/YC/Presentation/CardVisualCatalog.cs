using System;
using System.Collections.Generic;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Rules;

namespace YC.Presentation
{
    [CreateAssetMenu(fileName = "CardVisualCatalog", menuName = "YC/Presentation/Card Visual Catalog")]
    public sealed class CardVisualCatalog : ScriptableObject
    {
        public const int ExpectedFacilityCount = 45;
        public const int ExpectedCityStyleCount = 6;
        public const int ExpectedCharacterFrontCount = 5;
        public const int ExpectedCharacterBackCount = 4;
        public const int ExpectedEventCount = 22;
        public const int ExpectedTextureCount = 83;

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

        [SerializeField] private CharacterBackTextureEntry[] characterBackTextures =
            new CharacterBackTextureEntry[0];
        [SerializeField] private Texture2D cityBoardTexture;

        public int FacilityCount => CountContent("facility");
        public int CityStyleCount => CountContent("city_style");
        public int CharacterFrontCount => CountContent("character");
        public int CharacterBackCount => characterBackTextures == null ? 0 : characterBackTextures.Length;
        public int EventCount => CountContent("event");
        public int TextureCount => FacilityCount + CityStyleCount + CharacterFrontCount +
                                   CharacterBackCount + EventCount + (cityBoardTexture == null ? 0 : 1);

        private void OnEnable()
        {
#if UNITY_EDITOR
            if (!UnityEditor.AssetDatabase.Contains(this)) return;
#endif
            ValidateConfiguration();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!UnityEditor.AssetDatabase.Contains(this)) return;
            try { ValidateConfiguration(); }
            catch (Exception exception) { Debug.LogError("[CardVisualCatalog] " + exception.Message, this); }
        }

        public void ConfigureForEditor(
            IReadOnlyList<CharacterBackTextureEntry> configuredCharacterBacks,
            Texture2D configuredCityBoardTexture)
        {
            characterBackTextures = Copy(configuredCharacterBacks);
            cityBoardTexture = configuredCityBoardTexture;
            ValidateConfiguration();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        public bool TryValidateConfiguration(out string reason)
        {
            try
            {
                ValidateConfiguration();
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        public Sprite GetFacility(string facilityId) => ExternalContentRuntime.GetArtworkSprite("facility", facilityId);

        public Sprite GetCityStyle(string cityStyleId) => ExternalContentRuntime.GetArtworkSprite("city_style", cityStyleId);

        public Sprite GetCharacterFront(string cardId)
        {
            var direct = ExternalContentRuntime.GetArtworkSprite("character", cardId);
            if (direct != null) return direct;
            var definition = CharacterCardDatabase.Get(cardId);
            return definition == null ? null : ExternalContentRuntime.GetArtworkSprite("character", definition.TemplateId);
        }

        public Texture2D GetCharacterBack(PlayerColor color) =>
            ExternalContentRuntime.GetSharedArtwork("character_back_" + ((int)color).ToString());

        public Sprite GetEvent(string cardId) => ExternalContentRuntime.GetArtworkSprite("event", cardId);

        public Texture2D GetCityBoard() => ExternalContentRuntime.GetSharedArtwork("city_board");

        private void ValidateConfiguration()
        {
            if (characterBackTextures == null || characterBackTextures.Length != ExpectedCharacterBackCount)
                throw new InvalidOperationException("character back entries must contain exactly four persistent references.");
            var colors = new HashSet<PlayerColor>();
            foreach (var entry in characterBackTextures)
            {
                if (entry == null || !colors.Add(entry.Color))
                    throw new InvalidOperationException("character back color is missing or duplicated.");
                RequirePersistentTexture(entry.Texture, "character back '" + entry.Color + "'");
            }
            RequirePersistentTexture(cityBoardTexture, "city board");
        }

        private static int CountContent(string contentType)
        {
            var definitions = ExternalContentRuntime.Pack.ActiveDefinitions;
            var count = 0;
            foreach (var definition in definitions)
                if (definition.ContentType == contentType) count++;
            return count;
        }

        private static void RequirePersistentTexture(Texture2D texture, string label)
        {
            if (texture == null) throw new InvalidOperationException(label + " texture is missing.");
#if UNITY_EDITOR
            if (!UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out var guid, out long localId) ||
                string.IsNullOrEmpty(guid) || localId == 0)
                throw new InvalidOperationException(label + " texture is not a persistent asset reference.");
#endif
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
