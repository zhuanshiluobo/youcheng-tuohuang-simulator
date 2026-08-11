using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Presentation;

namespace YC.Editor
{
    public static class FacilityCardCatalogEditorAssetBuilder
    {
        public const string SourceJsonPath = "Assets/YC/Editor/Data/building_cards_manifest.json";
        public const string SourceJsonGuid = "2732b3f2be57b8742b1e2494431919bc";
        public const string CatalogAssetPath = "Assets/YC/Presentation/Content/FacilityCardCatalog.asset";
        public const string GameSettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";

        private sealed class ParsedSource
        {
            public readonly List<FacilityCardDefinition> Definitions =
                new List<FacilityCardDefinition>(FacilityCardCatalog.ExpectedDefinitionCount);
            public readonly HashSet<string> DefaultSupplyIds =
                new HashSet<string>(StringComparer.Ordinal);
            public string Sha256 = string.Empty;
        }

        [MenuItem("YC/Build/Facility Card Catalog/Rebuild Asset")]
        public static void RebuildCatalogAssetMenu()
        {
            RebuildCatalogAsset();
        }

        [MenuItem("YC/Build/Facility Card Catalog/Rebuild Asset And Configure Prefab")]
        public static void RebuildAssetAndConfigurePrefabMenu()
        {
            var catalog = RebuildCatalogAsset();
            ConfigureGameSettingsPrefab(catalog);
        }

        public static FacilityCardCatalog RebuildCatalogAsset()
        {
            var parsed = ParseSource();
            var catalog = AssetDatabase.LoadAssetAtPath<FacilityCardCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<FacilityCardCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogAssetPath);
            }

            catalog.ConfigureForEditor(parsed.Sha256, parsed.Definitions, parsed.DefaultSupplyIds);
            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("生成的 FacilityCardCatalog 无效：" + reason);
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(CatalogAssetPath, ImportAssetOptions.ForceUpdate);
            return LoadRequiredCatalog();
        }

        public static void ConfigureGameSettingsPrefab(FacilityCardCatalog catalog)
        {
            if (catalog == null)
            {
                throw new InvalidOperationException("无法配置 GameSettings Prefab：目录引用为空。");
            }

            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("无法配置 GameSettings Prefab：" + reason);
            }

            var root = PrefabUtility.LoadPrefabContents(GameSettingsPrefabPath);
            if (root == null)
            {
                throw new InvalidOperationException("无法加载 GameSettings Prefab。" + GameSettingsPrefabPath);
            }

            try
            {
                var bootstraps = root.GetComponentsInChildren<FacilityCatalogBootstrap>(true);
                if (bootstraps.Length > 1)
                {
                    throw new InvalidOperationException("GameSettings Prefab 中存在多个 FacilityCatalogBootstrap。");
                }

                var bootstrap = bootstraps.Length == 1
                    ? bootstraps[0]
                    : root.AddComponent<FacilityCatalogBootstrap>();
                if (bootstrap.gameObject != root)
                {
                    throw new InvalidOperationException("FacilityCatalogBootstrap 必须位于 GameSettings Prefab 根对象。");
                }

                var serialized = new SerializedObject(bootstrap);
                serialized.FindProperty("facilityCardCatalog").objectReferenceValue = catalog;
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

        public static FacilityCardCatalog LoadRequiredCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FacilityCardCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                throw new InvalidOperationException("缺少 FacilityCardCatalog 资产。");
            }

            if (!catalog.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("缺少有效 FacilityCardCatalog：" + reason);
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(CatalogAssetPath);
            if (assets.Length != 1 || assets[0] != catalog ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(catalog, out var guid, out long localId) ||
                string.IsNullOrEmpty(guid) || localId == 0)
            {
                throw new InvalidOperationException("FacilityCardCatalog 必须是唯一持久化主资产。");
            }

            return catalog;
        }

        public static void ValidateReadyForBuild()
        {
            try
            {
                var parsed = ParseSource();
                var catalog = LoadRequiredCatalog();
                if (!string.Equals(catalog.SourceSha256, parsed.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("FacilityCardCatalog 的源 SHA-256 已过期。");
                }

                var catalogDefinitions = catalog.CreateDefinitions();
                if (!DefinitionSetsEqual(parsed.Definitions, catalogDefinitions))
                {
                    throw new InvalidOperationException("FacilityCardCatalog 字段与源 JSON 不一致。");
                }

                ValidateGameSettingsPrefab(catalog);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BuildFailedException("FacilityCardCatalog build readiness 失败：" + exception.Message);
            }
        }

        public static void ValidateGameSettingsPrefab(FacilityCardCatalog expectedCatalog)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameSettingsPrefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException("缺少 GameSettings Prefab。");
            }

            var bootstraps = prefab.GetComponentsInChildren<FacilityCatalogBootstrap>(true);
            if (bootstraps.Length != 1 || bootstraps[0].gameObject != prefab ||
                bootstraps[0].Catalog != expectedCatalog)
            {
                throw new InvalidOperationException("GameSettings Prefab 的 FacilityCatalogBootstrap 数量、位置或引用无效。");
            }

            if (!bootstraps[0].TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException("GameSettings Prefab 的 FacilityCatalogBootstrap 接线无效：" + reason);
            }
        }

        internal static IReadOnlyList<FacilityCardDefinition> ReadSourceDefinitionsForTests()
        {
            return ParseSource().Definitions.AsReadOnly();
        }

        internal static string ComputeCurrentSourceSha256()
        {
            return ComputeSha256(SourceJsonPath);
        }

        private static ParsedSource ParseSource()
        {
            if (!File.Exists(SourceJsonPath))
            {
                throw new InvalidOperationException("缺少 Editor-only 设施 manifest：" + SourceJsonPath);
            }

            if (AssetDatabase.AssetPathToGUID(SourceJsonPath) != SourceJsonGuid)
            {
                throw new InvalidOperationException("设施 manifest GUID 已改变。");
            }

            var root = JObject.Parse(File.ReadAllText(SourceJsonPath));
            var counts = RequireObject(root, "counts");
            RequireExactInt(counts, "buildingCards", 41);
            RequireExactInt(counts, "extensionHubs", 3);
            RequireExactInt(counts, "runtimeSupplementalCards", 1);
            RequireExactInt(counts, "skippedEnterpriseOffices", 3);

            var effectContracts = RequireObject(root, "effectContracts");
            var parsed = new ParsedSource { Sha256 = ComputeSha256(SourceJsonPath) };
            AddContractCards(
                parsed,
                RequireArray(root, "buildingCards"),
                effectContracts,
                false,
                true);
            AddContractCards(
                parsed,
                RequireArray(root, "reserveCards"),
                effectContracts,
                true,
                false);
            AddSupplementalCards(parsed, RequireArray(root, "runtimeSupplementalCards"));
            ValidateSkippedCards(RequireArray(root, "skippedCards"));

            if (parsed.Definitions.Count != FacilityCardCatalog.ExpectedDefinitionCount ||
                parsed.DefaultSupplyIds.Count != FacilityCardCatalog.ExpectedDefaultSupplyCount)
            {
                throw new InvalidOperationException("设施 manifest 输出数量不完整。");
            }

            return parsed;
        }

        private static void AddContractCards(
            ParsedSource parsed,
            JArray cards,
            JObject effectContracts,
            bool reserveOnly,
            bool defaultSupply)
        {
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i] as JObject ??
                           throw new InvalidOperationException("设施卡记录必须是对象。");
                var id = RequireString(card, "id", false);
                var name = RequireString(card, "name", false);
                var contract = effectContracts[name] as JObject ??
                               throw new InvalidOperationException("缺少 effect contract：" + name);
                var definition = CreateDefinition(
                    card,
                    id,
                    name,
                    RequireString(contract, "effectId", false),
                    RequireString(contract, "effectType", false),
                    RequireBoolean(contract, "hasEntryEffect"),
                    RequireKeywords(contract),
                    ReadResourceSet(contract["onBuiltReward"], false),
                    reserveOnly);
                AddDefinition(parsed, definition, defaultSupply);
            }
        }

        private static void AddSupplementalCards(ParsedSource parsed, JArray cards)
        {
            if (cards.Count != FacilityCardCatalog.ExpectedSupplementalCount)
            {
                throw new InvalidOperationException("runtimeSupplementalCards 必须精确包含 1 条记录。");
            }

            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i] as JObject ??
                           throw new InvalidOperationException("补充设施记录必须是对象。");
                var reserveOnly = RequireBoolean(card, "reserveOnly");
                var defaultSupply = RequireBoolean(card, "defaultSupply");
                if (reserveOnly || defaultSupply)
                {
                    throw new InvalidOperationException("运行时补充设施不得属于 reserve 或 default supply。");
                }

                var definition = CreateDefinition(
                    card,
                    RequireString(card, "id", false),
                    RequireString(card, "name", false),
                    RequireString(card, "effectId", false),
                    RequireString(card, "effectType", false),
                    RequireBoolean(card, "hasEntryEffect"),
                    RequireKeywords(card),
                    ReadResourceSet(card["onBuiltReward"], false),
                    false);
                AddDefinition(parsed, definition, false);
            }
        }

        private static FacilityCardDefinition CreateDefinition(
            JObject card,
            string id,
            string name,
            string effectId,
            string effectType,
            bool hasEntryEffect,
            List<string> keywords,
            ResourceSet onBuiltReward,
            bool reserveOnly)
        {
            var unique = keywords.Contains(FacilityCardKeywords.Unique);
            return new FacilityCardDefinition
            {
                FacilityId = id,
                ManifestId = id,
                Name = name,
                Color = RequireString(card, "color", false),
                Score = RequireInt(card, "score", true),
                ResourceCost = ReadResourceSet(card["resourceCost"], true),
                GoldVoucherCost = ReadNullableNonNegativeInt(card, "goldVoucherCost"),
                Unique = unique,
                UniqueGroupId = unique ? name : string.Empty,
                HasEntryEffect = hasEntryEffect,
                Keywords = new List<string>(keywords),
                EffectId = effectId,
                EffectType = effectType,
                Description = RequireString(card, "description", true),
                EffectText = RequireString(card, "effect", true),
                ReserveOnly = reserveOnly,
                OnBuiltReward = onBuiltReward
            };
        }

        private static void AddDefinition(
            ParsedSource parsed,
            FacilityCardDefinition definition,
            bool defaultSupply)
        {
            for (var i = 0; i < parsed.Definitions.Count; i++)
            {
                if (parsed.Definitions[i].FacilityId == definition.FacilityId)
                {
                    throw new InvalidOperationException("设施 ID 重复：" + definition.FacilityId);
                }
            }

            parsed.Definitions.Add(definition);
            if (defaultSupply && !parsed.DefaultSupplyIds.Add(definition.FacilityId))
            {
                throw new InvalidOperationException("默认供应 ID 重复：" + definition.FacilityId);
            }
        }

        private static List<string> RequireKeywords(JObject source)
        {
            var array = RequireArray(source, "keywords");
            var result = new List<string>(array.Count);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i].Type != JTokenType.String)
                {
                    throw new InvalidOperationException("keywords 必须是字符串数组。");
                }

                var value = array[i].Value<string>();
                if (string.IsNullOrEmpty(value) || !unique.Add(value))
                {
                    throw new InvalidOperationException("keywords 包含空值或重复值。");
                }

                result.Add(value);
            }

            return result;
        }

        private static ResourceSet ReadResourceSet(JToken token, bool required)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                if (required)
                {
                    throw new InvalidOperationException("缺少必需资源结构。");
                }

                return new ResourceSet();
            }

            var source = token as JObject ??
                         throw new InvalidOperationException("资源结构必须是对象。");
            return new ResourceSet
            {
                Originium = ReadOptionalNonNegativeInt(source, "源岩"),
                OriginiumShard = ReadOptionalNonNegativeInt(source, "源石碎片"),
                Iron = ReadOptionalNonNegativeInt(source, "异铁"),
                PureOriginium = ReadOptionalNonNegativeInt(source, "至纯源石"),
                GoldVoucher = ReadOptionalNonNegativeInt(source, "金券")
            };
        }

        private static void ValidateSkippedCards(JArray skippedCards)
        {
            if (skippedCards.Count != 3)
            {
                throw new InvalidOperationException("skippedCards 必须保留 3 条企业办事处记录。");
            }

            for (var i = 0; i < skippedCards.Count; i++)
            {
                var record = skippedCards[i] as JObject ??
                             throw new InvalidOperationException("skippedCards 记录必须是对象。");
                if (RequireString(record, "name", false) != "企业办事处" ||
                    string.IsNullOrEmpty(RequireString(record, "reason", false)))
                {
                    throw new InvalidOperationException("skippedCards 未保留企业办事处未录图语义。");
                }
            }
        }

        private static bool DefinitionSetsEqual(
            IReadOnlyList<FacilityCardDefinition> expected,
            IReadOnlyList<FacilityCardDefinition> actual)
        {
            if (expected.Count != actual.Count)
            {
                return false;
            }

            var byId = new Dictionary<string, FacilityCardDefinition>(StringComparer.Ordinal);
            for (var i = 0; i < actual.Count; i++)
            {
                byId[actual[i].FacilityId] = actual[i];
            }

            for (var i = 0; i < expected.Count; i++)
            {
                if (!byId.TryGetValue(expected[i].FacilityId, out var candidate) ||
                    !DefinitionEquals(expected[i], candidate))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool DefinitionEquals(FacilityCardDefinition left, FacilityCardDefinition right)
        {
            if (left.FacilityId != right.FacilityId || left.ManifestId != right.ManifestId ||
                left.Name != right.Name || left.Color != right.Color || left.Score != right.Score ||
                left.GoldVoucherCost != right.GoldVoucherCost || left.Unique != right.Unique ||
                left.UniqueGroupId != right.UniqueGroupId || left.HasEntryEffect != right.HasEntryEffect ||
                left.EffectId != right.EffectId || left.EffectType != right.EffectType ||
                left.Description != right.Description || left.EffectText != right.EffectText ||
                left.ReserveOnly != right.ReserveOnly ||
                !ResourceSetEquals(left.ResourceCost, right.ResourceCost) ||
                !ResourceSetEquals(left.OnBuiltReward, right.OnBuiltReward) ||
                left.Keywords.Count != right.Keywords.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Keywords.Count; i++)
            {
                if (left.Keywords[i] != right.Keywords[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ResourceSetEquals(ResourceSet left, ResourceSet right)
        {
            return left.Originium == right.Originium &&
                   left.OriginiumShard == right.OriginiumShard &&
                   left.Iron == right.Iron &&
                   left.PureOriginium == right.PureOriginium &&
                   left.GoldVoucher == right.GoldVoucher;
        }

        private static string ComputeSha256(string assetPath)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(File.ReadAllBytes(assetPath));
                return BitConverter.ToString(bytes).Replace("-", string.Empty);
            }
        }

        private static JObject RequireObject(JObject source, string propertyName)
        {
            return source[propertyName] as JObject ??
                   throw new InvalidOperationException("缺少对象字段：" + propertyName);
        }

        private static JArray RequireArray(JObject source, string propertyName)
        {
            return source[propertyName] as JArray ??
                   throw new InvalidOperationException("缺少数组字段：" + propertyName);
        }

        private static string RequireString(JObject source, string propertyName, bool allowEmpty)
        {
            var token = source[propertyName];
            if (token == null || token.Type != JTokenType.String)
            {
                throw new InvalidOperationException("缺少字符串字段：" + propertyName);
            }

            var value = token.Value<string>();
            if (!allowEmpty && string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException("字符串字段不能为空：" + propertyName);
            }

            return value ?? string.Empty;
        }

        private static bool RequireBoolean(JObject source, string propertyName)
        {
            var token = source[propertyName];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                throw new InvalidOperationException("缺少布尔字段：" + propertyName);
            }

            return token.Value<bool>();
        }

        private static int RequireInt(JObject source, string propertyName, bool allowNegative)
        {
            var token = source[propertyName];
            if (token == null || token.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException("缺少整数字段：" + propertyName);
            }

            var value = token.Value<int>();
            if (!allowNegative && value < 0)
            {
                throw new InvalidOperationException(propertyName + " 不能为负数。");
            }

            return value;
        }

        private static int ReadNullableNonNegativeInt(JObject source, string propertyName)
        {
            var token = source[propertyName];
            if (token == null)
            {
                throw new InvalidOperationException("缺少字段：" + propertyName);
            }

            if (token.Type == JTokenType.Null)
            {
                return 0;
            }

            return RequireInt(source, propertyName, false);
        }

        private static int ReadOptionalNonNegativeInt(JObject source, string propertyName)
        {
            var token = source[propertyName];
            if (token == null)
            {
                return 0;
            }

            return RequireInt(source, propertyName, false);
        }

        private static void RequireExactInt(JObject source, string propertyName, int expected)
        {
            var actual = RequireInt(source, propertyName, false);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    propertyName + " 必须为 " + expected + "，实际 " + actual + "。");
            }
        }
    }
}
