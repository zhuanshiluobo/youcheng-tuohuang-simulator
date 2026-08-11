using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;

namespace YC.Editor
{
    public static class EventCharacterCardCatalogEditorAssetBuilder
    {
        public const string SourceJsonPath =
            "Assets/YC/Editor/Data/event_character_cards_manifest.json";
        public const string SourceJsonGuid = "52c63b90bf124a6e87e73b2bb48052b5";
        public const string CatalogAssetPath =
            "Assets/YC/Presentation/Content/EventCharacterCardCatalog.asset";
        public const string CatalogAssetGuid = "3a15167db88346acada95f74436e8ade";
        public const string GameSettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";

        private sealed class ParsedSource
        {
            public readonly List<EventCardDefinition> Events =
                new List<EventCardDefinition>(EventCharacterCardCatalog.ExpectedEventDefinitionCount);
            public readonly List<CharacterCardDefinition> Characters =
                new List<CharacterCardDefinition>(EventCharacterCardCatalog.ExpectedCharacterDefinitionCount);
            public string Sha256 = string.Empty;
        }

        [MenuItem("YC/Build/Event & Character Card Catalog/Rebuild Asset")]
        public static void RebuildCatalogAssetMenu()
        {
            RebuildCatalogAsset();
        }

        [MenuItem("YC/Build/Event & Character Card Catalog/Rebuild Asset And Configure Prefab")]
        public static void RebuildAssetAndConfigurePrefabMenu()
        {
            var catalog = RebuildCatalogAsset();
            ConfigureGameSettingsPrefab(catalog);
        }

        public static EventCharacterCardCatalog RebuildCatalogAsset()
        {
            var parsed = ParseSource();
            var catalog = AssetDatabase.LoadAssetAtPath<EventCharacterCardCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<EventCharacterCardCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogAssetPath);
            }

            catalog.ConfigureForEditor(parsed.Sha256, parsed.Events, parsed.Characters);
            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "生成的 EventCharacterCardCatalog 无效：" + reason);
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(CatalogAssetPath, ImportAssetOptions.ForceUpdate);
            return LoadRequiredCatalog();
        }

        public static EventCharacterCardCatalog LoadRequiredCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<EventCharacterCardCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                throw new InvalidOperationException("缺少 EventCharacterCardCatalog 资产。");
            }

            if (AssetDatabase.AssetPathToGUID(CatalogAssetPath) != CatalogAssetGuid)
            {
                throw new InvalidOperationException("EventCharacterCardCatalog 资产 GUID 已改变。");
            }

            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "缺少有效 EventCharacterCardCatalog：" + reason);
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(CatalogAssetPath);
            if (assets.Length != 1 || assets[0] != catalog || !AssetDatabase.Contains(catalog) ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    catalog,
                    out var guid,
                    out long localId) ||
                guid != CatalogAssetGuid || localId == 0)
            {
                throw new InvalidOperationException(
                    "EventCharacterCardCatalog 必须是唯一持久化主资产。");
            }

            return catalog;
        }

        public static void ConfigureGameSettingsPrefab(EventCharacterCardCatalog catalog)
        {
            string reason = null;
            if (catalog == null || !catalog.TryValidateConfiguration(out reason))
            {
                throw new InvalidOperationException(
                    "无法配置 EventCharacterCatalogBootstrap：" +
                    (reason ?? "目录引用为空。"));
            }

            var root = PrefabUtility.LoadPrefabContents(GameSettingsPrefabPath);
            if (root == null)
            {
                throw new InvalidOperationException("无法加载 GameSettings Prefab。");
            }

            try
            {
                var bootstraps = root.GetComponentsInChildren<EventCharacterCatalogBootstrap>(true);
                if (bootstraps.Length > 1)
                {
                    throw new InvalidOperationException(
                        "GameSettings Prefab 中存在多个 EventCharacterCatalogBootstrap。");
                }

                var bootstrap = bootstraps.Length == 1
                    ? bootstraps[0]
                    : root.AddComponent<EventCharacterCatalogBootstrap>();
                if (bootstrap.gameObject != root)
                {
                    throw new InvalidOperationException(
                        "EventCharacterCatalogBootstrap 必须位于 GameSettings Prefab 根对象。");
                }

                bootstrap.enabled = true;
                var serialized = new SerializedObject(bootstrap);
                serialized.FindProperty("catalog").objectReferenceValue = catalog;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(bootstrap);
                PrefabUtility.SaveAsPrefabAsset(root, GameSettingsPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
        }

        internal static IReadOnlyList<EventCardDefinition> ReadSourceEventsForTests()
        {
            return ParseSource().Events.AsReadOnly();
        }

        internal static IReadOnlyList<CharacterCardDefinition> ReadSourceCharactersForTests()
        {
            return ParseSource().Characters.AsReadOnly();
        }

        internal static string ComputeCurrentSourceSha256()
        {
            return ComputeSha256(SourceJsonPath);
        }

        internal static bool MatchesSource(EventCharacterCardCatalog catalog)
        {
            if (catalog == null)
            {
                return false;
            }

            var parsed = ParseSource();
            return string.Equals(catalog.SourceSha256, parsed.Sha256, StringComparison.OrdinalIgnoreCase) &&
                   EventDefinitionSetsEqual(parsed.Events, catalog.CreateEventDefinitions()) &&
                   CharacterDefinitionSetsEqual(parsed.Characters, catalog.CreateCharacterDefinitions());
        }

        private static ParsedSource ParseSource()
        {
            if (!File.Exists(SourceJsonPath))
            {
                throw new InvalidOperationException(
                    "缺少 Editor-only Event/Character manifest：" + SourceJsonPath);
            }

            if (AssetDatabase.AssetPathToGUID(SourceJsonPath) != SourceJsonGuid)
            {
                throw new InvalidOperationException("Event/Character manifest GUID 已改变。");
            }

            var root = JObject.Parse(File.ReadAllText(SourceJsonPath));
            RequireExactInt(root, "schemaVersion", 1);
            var counts = RequireObject(root, "counts");
            RequireExactInt(
                counts,
                "eventCards",
                EventCharacterCardCatalog.ExpectedEventDefinitionCount);
            RequireExactInt(
                counts,
                "characterCards",
                EventCharacterCardCatalog.ExpectedCharacterDefinitionCount);

            var parsed = new ParsedSource { Sha256 = ComputeSha256(SourceJsonPath) };
            var eventCards = RequireArray(root, "eventCards");
            for (var i = 0; i < eventCards.Count; i++)
            {
                parsed.Events.Add(ParseEvent(RequireObject(eventCards[i], "eventCards[" + i + "]")));
            }

            var characterCards = RequireArray(root, "characterCards");
            for (var i = 0; i < characterCards.Count; i++)
            {
                parsed.Characters.Add(ParseCharacter(
                    RequireObject(characterCards[i], "characterCards[" + i + "]")));
            }

            if (parsed.Events.Count != EventCharacterCardCatalog.ExpectedEventDefinitionCount ||
                parsed.Characters.Count != EventCharacterCardCatalog.ExpectedCharacterDefinitionCount)
            {
                throw new InvalidOperationException("Event/Character manifest 数量不完整。");
            }

            var probe = ScriptableObject.CreateInstance<EventCharacterCardCatalog>();
            try
            {
                probe.ConfigureForEditor(parsed.Sha256, parsed.Events, parsed.Characters);
                if (!probe.TryValidateConfiguration(out var reason))
                {
                    throw new InvalidOperationException("Event/Character manifest 无效：" + reason);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }

            return parsed;
        }

        private static EventCardDefinition ParseEvent(JObject source)
        {
            var result = new EventCardDefinition
            {
                CardId = RequireString(source, "cardId"),
                Name = RequireString(source, "name"),
                Description = RequireString(source, "description"),
                Color = RequireEnum<EventColor>(source, "color"),
                ResourceType = RequireEnum<ResourceType>(source, "resourceType"),
                ResourceAmount = RequirePositiveInt(source, "resourceAmount"),
                RepresentativeResourceType =
                    RequireEnum<ResourceType>(source, "representativeResourceType"),
                RepresentativeResourceAmount =
                    RequirePositiveInt(source, "representativeResourceAmount")
            };

            var choices = RequireArray(source, "choices");
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = RequireObject(choices[i], result.CardId + ".choices[" + i + "]");
                result.ChoiceDescriptions.Add(RequireString(choice, "description"));
                result.ChoiceRewards.Add(ParseResourceSet(RequireObject(choice, "reward")));

                var effects = new List<EventEffect>();
                var effectTokens = RequireArray(choice, "pendingEffects");
                for (var effectIndex = 0; effectIndex < effectTokens.Count; effectIndex++)
                {
                    var effect = RequireObject(
                        effectTokens[effectIndex],
                        result.CardId + ".choices[" + i + "].pendingEffects[" + effectIndex + "]");
                    effects.Add(new EventEffect
                    {
                        Kind = RequireEnum<EventEffectKind>(effect, "kind"),
                        ResourceType = RequireEnum<ResourceType>(effect, "resourceType"),
                        Amount = RequirePositiveInt(effect, "amount"),
                        TargetScope = RequireEnum<EventEffectTargetScope>(effect, "targetScope"),
                        CostResourceType = RequireEnum<ResourceType>(effect, "costResourceType"),
                        CostAmount = RequireNonNegativeInt(effect, "costAmount")
                    });
                }

                result.ChoicePendingEffects.Add(effects);
            }

            return result;
        }

        private static CharacterCardDefinition ParseCharacter(JObject source)
        {
            return new CharacterCardDefinition
            {
                TemplateId = RequireString(source, "templateId"),
                CardId = RequireString(source, "cardId"),
                Name = RequireString(source, "name"),
                StrategyEffect = RequireEnum<CharacterCardEffectKind>(source, "strategyEffect"),
                TacticEffect = RequireEnum<CharacterCardEffectKind>(source, "tacticEffect")
            };
        }

        private static ResourceSet ParseResourceSet(JObject source)
        {
            return new ResourceSet
            {
                Originium = RequireNonNegativeInt(source, "originium"),
                OriginiumShard = RequireNonNegativeInt(source, "originiumShard"),
                Iron = RequireNonNegativeInt(source, "iron"),
                PureOriginium = RequireNonNegativeInt(source, "pureOriginium"),
                GoldVoucher = RequireNonNegativeInt(source, "goldVoucher")
            };
        }

        private static bool EventDefinitionSetsEqual(
            IReadOnlyList<EventCardDefinition> left,
            IReadOnlyList<EventCardDefinition> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                var a = left[i];
                var b = right[i];
                if (a.CardId != b.CardId || a.Name != b.Name || a.Description != b.Description ||
                    a.Color != b.Color || a.ResourceType != b.ResourceType ||
                    a.ResourceAmount != b.ResourceAmount ||
                    a.RepresentativeResourceType != b.RepresentativeResourceType ||
                    a.RepresentativeResourceAmount != b.RepresentativeResourceAmount ||
                    a.ChoiceDescriptions.Count != b.ChoiceDescriptions.Count)
                {
                    return false;
                }

                for (var choice = 0; choice < a.ChoiceDescriptions.Count; choice++)
                {
                    if (a.ChoiceDescriptions[choice] != b.ChoiceDescriptions[choice] ||
                        !ResourceSetsEqual(a.ChoiceRewards[choice], b.ChoiceRewards[choice]) ||
                        a.ChoicePendingEffects[choice].Count !=
                        b.ChoicePendingEffects[choice].Count)
                    {
                        return false;
                    }

                    for (var effect = 0; effect < a.ChoicePendingEffects[choice].Count; effect++)
                    {
                        if (!EffectsEqual(
                                a.ChoicePendingEffects[choice][effect],
                                b.ChoicePendingEffects[choice][effect]))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private static bool CharacterDefinitionSetsEqual(
            IReadOnlyList<CharacterCardDefinition> left,
            IReadOnlyList<CharacterCardDefinition> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                var a = left[i];
                var b = right[i];
                if (a.TemplateId != b.TemplateId || a.CardId != b.CardId || a.Name != b.Name ||
                    a.StrategyEffect != b.StrategyEffect || a.TacticEffect != b.TacticEffect)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ResourceSetsEqual(ResourceSet left, ResourceSet right)
        {
            return left.Originium == right.Originium &&
                   left.OriginiumShard == right.OriginiumShard &&
                   left.Iron == right.Iron && left.PureOriginium == right.PureOriginium &&
                   left.GoldVoucher == right.GoldVoucher;
        }

        private static bool EffectsEqual(EventEffect left, EventEffect right)
        {
            return left.Kind == right.Kind && left.ResourceType == right.ResourceType &&
                   left.Amount == right.Amount && left.TargetScope == right.TargetScope &&
                   left.CostResourceType == right.CostResourceType &&
                   left.CostAmount == right.CostAmount;
        }

        private static JObject RequireObject(JObject source, string property)
        {
            return RequireObject(source[property], property);
        }

        private static JObject RequireObject(JToken token, string label)
        {
            if (!(token is JObject result))
            {
                throw new InvalidOperationException(label + " 必须是 JSON object。");
            }

            return result;
        }

        private static JArray RequireArray(JObject source, string property)
        {
            if (!(source[property] is JArray result))
            {
                throw new InvalidOperationException(property + " 必须是 JSON array。");
            }

            return result;
        }

        private static string RequireString(JObject source, string property)
        {
            var value = (string)source[property];
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(property + " 必须是非空字符串。");
            }

            return value;
        }

        private static int RequirePositiveInt(JObject source, string property)
        {
            var value = RequireInt(source, property);
            if (value <= 0)
            {
                throw new InvalidOperationException(property + " 必须为正整数。");
            }

            return value;
        }

        private static int RequireNonNegativeInt(JObject source, string property)
        {
            var value = RequireInt(source, property);
            if (value < 0)
            {
                throw new InvalidOperationException(property + " 不能为负数。");
            }

            return value;
        }

        private static int RequireInt(JObject source, string property)
        {
            var token = source[property];
            if (token == null || token.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException(property + " 必须是整数。");
            }

            return (int)token;
        }

        private static void RequireExactInt(JObject source, string property, int expected)
        {
            var actual = RequireInt(source, property);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    property + " 必须为 " + expected + "，实际 " + actual + "。");
            }
        }

        private static T RequireEnum<T>(JObject source, string property) where T : struct
        {
            var text = RequireString(source, property);
            if (!Enum.TryParse(text, false, out T result) || !Enum.IsDefined(typeof(T), result))
            {
                throw new InvalidOperationException(property + " 枚举值无效：" + text);
            }

            return result;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(File.ReadAllBytes(path));
                return BitConverter.ToString(bytes).Replace("-", string.Empty);
            }
        }
    }
}
