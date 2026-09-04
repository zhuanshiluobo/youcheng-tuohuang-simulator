using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class FacilityCardCatalogEditorAssetTests
    {
        private const string SourcePath = "Assets/YC/Editor/Data/building_cards_manifest.json";
        private const string SourceGuid = "2732b3f2be57b8742b1e2494431919bc";
        private const string OldSourcePath =
            "Assets/StreamingAssets/YC/Data/building_cards_manifest.json";
        private const string CatalogPath =
            "Assets/YC/Presentation/Content/FacilityCardCatalog.asset";
        private const string GameSettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
        private const string GameSettingsPrefabGuid = "b8c82ab37df14894f9d7ca148f923a1b";

        [Test]
        public void Source_IsEditorOnlyRetainsGuidAndMatchesCatalogHash()
        {
            Assert.That(AssetDatabase.AssetPathToGUID(SourcePath), Is.EqualTo(SourceGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(OldSourcePath), Is.Empty);
            Assert.That(File.Exists(OldSourcePath), Is.False);

            var root = LoadSource();
            Assert.That(((JArray)root["buildingCards"]).Count, Is.EqualTo(41));
            Assert.That(((JArray)root["reserveCards"]).Count, Is.EqualTo(4));
            Assert.That(((JArray)root["runtimeSupplementalCards"]).Count, Is.EqualTo(1));
            Assert.That(((JArray)root["skippedCards"]).Count, Is.EqualTo(3));

            var effectContracts = (JObject)root["effectContracts"];
            foreach (var property in effectContracts.Properties())
            {
                var contract = (JObject)property.Value;
                Assert.That(contract["effectId"], Is.Not.Null, property.Name);
                Assert.That(contract["effectType"], Is.Not.Null, property.Name);
                Assert.That(contract["hasEntryEffect"], Is.Not.Null, property.Name);
                Assert.That(contract["keywords"], Is.TypeOf<JArray>(), property.Name);
            }

            var catalog = LoadCatalog();
            var sourceHash = ComputeSha256(SourcePath);
            Assert.That(GetCatalogProperty<string>(catalog, "SourceSha256"), Is.EqualTo(sourceHash));
            Assert.That(GetCatalogProperty<int>(catalog, "DefinitionCount"), Is.EqualTo(46));
        }

        [Test]
        public void Catalog_ExportsExactSourceFieldsAndStableIds()
        {
            var root = LoadSource();
            var contracts = (JObject)root["effectContracts"];
            var definitions = ExportDefinitions();
            var byId = definitions.ToDictionary(item => item.FacilityId, StringComparer.Ordinal);

            Assert.That(definitions, Has.Count.EqualTo(46));
            Assert.That(byId.Keys.Count(id => id.StartsWith("building_", StringComparison.Ordinal)), Is.EqualTo(41));
            Assert.That(byId.Keys.Count(id => id.StartsWith("reserve_", StringComparison.Ordinal)), Is.EqualTo(4));
            for (var index = 1; index <= 41; index++)
            {
                Assert.That(byId.ContainsKey("building_" + index.ToString("000")), Is.True);
            }

            for (var index = 1; index <= 4; index++)
            {
                Assert.That(byId.ContainsKey("reserve_" + index.ToString("000")), Is.True);
            }

            AssertContractCardsMatch((JArray)root["buildingCards"], contracts, byId, false);
            AssertContractCardsMatch((JArray)root["reserveCards"], contracts, byId, true);
            AssertSupplementalCardMatches((JObject)((JArray)root["runtimeSupplementalCards"])[0], byId);

            var refinery = byId[FacilityCardDatabase.SourceStoneRefinery];
            var ironRefinery = byId[FacilityCardDatabase.IronRefinery];
            var purification = byId[FacilityCardDatabase.OriginiumPurificationPlant];
            Assert.That(refinery.OnBuiltReward.OriginiumShard, Is.EqualTo(6));
            Assert.That(ironRefinery.OnBuiltReward.Iron, Is.EqualTo(4));
            Assert.That(purification.OnBuiltReward.Originium, Is.EqualTo(7));

            var enterprise = byId[FacilityCardDatabase.EnterpriseOffice];
            Assert.That(enterprise.Name, Is.EqualTo("企业办事处"));
            Assert.That(enterprise.Color, Is.EqualTo("rainbow"));
            Assert.That(enterprise.Score, Is.EqualTo(1));
            Assert.That(enterprise.ResourceCost.Originium, Is.EqualTo(3));
            Assert.That(enterprise.ResourceCost.OriginiumShard, Is.EqualTo(3));
            Assert.That(enterprise.ResourceCost.Iron, Is.EqualTo(1));
            Assert.That(enterprise.GoldVoucherCost, Is.EqualTo(23));
            Assert.That(enterprise.EffectId, Is.EqualTo(FacilityCardEffectIds.EnterpriseOffice));
            Assert.That(enterprise.EffectType, Is.EqualTo("entry"));
            Assert.That(enterprise.HasEntryEffect, Is.False);
            Assert.That(enterprise.Keywords, Is.Empty);
            Assert.That(enterprise.Description, Is.Empty);
            Assert.That(enterprise.EffectText, Is.Empty);
            Assert.That(enterprise.ReserveOnly, Is.False);
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Has.Count.EqualTo(41));
            Assert.That(FacilityCardDatabase.ReserveIds, Has.Count.EqualTo(4));
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Does.Not.Contain(enterprise.FacilityId));
            Assert.That(FacilityCardDatabase.ReserveIds, Does.Not.Contain(enterprise.FacilityId));
        }

        [Test]
        public void Catalog_CreateDefinitionsReturnsDeepCopies()
        {
            var first = ExportDefinitions();
            var second = ExportDefinitions();
            Assert.That(first[0], Is.Not.SameAs(second[0]));
            Assert.That(first[0].ResourceCost, Is.Not.SameAs(second[0].ResourceCost));
            Assert.That(first[0].OnBuiltReward, Is.Not.SameAs(second[0].OnBuiltReward));
            Assert.That(first[0].Keywords, Is.Not.SameAs(second[0].Keywords));

            var originalCost = second[0].ResourceCost.Originium;
            var originalKeywordCount = second[0].Keywords.Count;
            first[0].ResourceCost.Originium = 999;
            first[0].OnBuiltReward.Iron = 999;
            first[0].Keywords.Add("mutated");

            var third = ExportDefinitions();
            Assert.That(second[0].ResourceCost.Originium, Is.EqualTo(originalCost));
            Assert.That(second[0].Keywords, Has.Count.EqualTo(originalKeywordCount));
            Assert.That(third[0].ResourceCost.Originium, Is.EqualTo(originalCost));
            Assert.That(third[0].OnBuiltReward.Iron, Is.Not.EqualTo(999));
            Assert.That(third[0].Keywords, Has.Count.EqualTo(originalKeywordCount));
        }

        [Test]
        public void DatabaseInitialize_IsIdempotentForSameDataAndRejectsConflict()
        {
            var definitions = ExportDefinitions();
            Assert.DoesNotThrow(() => FacilityCardDatabase.Initialize(definitions));
            Assert.DoesNotThrow(() => FacilityCardDatabase.Initialize(ExportDefinitions()));

            var original = FacilityCardDatabase.Get(definitions[0].FacilityId);
            definitions[0].Score++;
            var exception = Assert.Throws<InvalidOperationException>(
                () => FacilityCardDatabase.Initialize(definitions));
            Assert.That(exception.Message, Does.Contain("不同"));
            Assert.That(
                FacilityCardDatabase.Get(original.FacilityId).Score,
                Is.EqualTo(original.Score));
        }

        [Test]
        public void DatabaseInitialize_RejectsNullDuplicateAndIncompleteInput()
        {
            var complete = ExportDefinitions();
            var withNull = new List<FacilityCardDefinition>(complete) { null };
            var duplicate = new List<FacilityCardDefinition>(complete) { complete[0] };
            var incomplete = complete.Take(complete.Count - 1).ToList();

            Assert.Throws<InvalidOperationException>(() => FacilityCardDatabase.Initialize(withNull));
            Assert.Throws<InvalidOperationException>(() => FacilityCardDatabase.Initialize(duplicate));
            Assert.Throws<InvalidOperationException>(() => FacilityCardDatabase.Initialize(incomplete));
        }

        [Test]
        public void BuildReadiness_ValidatesCurrentSourceCatalogAndPrefab()
        {
            var builderType = Type.GetType(
                "YC.Editor.FacilityCardCatalogEditorAssetBuilder, Assembly-CSharp-Editor",
                true);
            var validate = builderType.GetMethod(
                "ValidateReadyForBuild",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);
            Assert.DoesNotThrow(() => validate.Invoke(null, null));

            var preprocessorType = Type.GetType(
                "YC.Editor.FacilityCatalogBuildReadinessPreprocessor, Assembly-CSharp-Editor",
                true);
            var preprocessor = Activator.CreateInstance(preprocessorType);
            Assert.That(
                (int)preprocessorType.GetProperty("callbackOrder").GetValue(preprocessor, null),
                Is.EqualTo(-250));
        }

        [Test]
        public void CatalogAndBootstrap_ArePersistentSingleMainAssetsWithStablePrefabWiring()
        {
            var catalog = LoadCatalog();
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(CatalogPath), Has.Length.EqualTo(1));
            Assert.That(AssetDatabase.Contains(catalog), Is.True);
            Assert.That(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(catalog, out var catalogGuid, out long localId),
                Is.True);
            Assert.That(catalogGuid, Is.Not.Empty);
            Assert.That(localId, Is.Not.Zero);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameSettingsPrefabPath);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(GameSettingsPrefabPath), Is.EqualTo(GameSettingsPrefabGuid));
            Assert.That(
                prefab.GetComponentsInChildren<Component>(true),
                Has.None.Null,
                "GameSettingsMenu Prefab 包含丢失脚本。");

            var bootstrapType = Type.GetType("YC.Presentation.FacilityCatalogBootstrap, Assembly-CSharp", true);
            var bootstraps = prefab.GetComponentsInChildren(bootstrapType, true);
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            Assert.That(bootstraps[0].gameObject, Is.SameAs(prefab));
            var serialized = new SerializedObject(bootstraps[0]);
            Assert.That(
                serialized.FindProperty("facilityCardCatalog").objectReferenceValue,
                Is.SameAs(catalog));
            var orderAttribute = (DefaultExecutionOrder)Attribute.GetCustomAttribute(
                bootstrapType,
                typeof(DefaultExecutionOrder));
            Assert.That(orderAttribute, Is.Not.Null);
            Assert.That(orderAttribute.order, Is.EqualTo(-20000));

            var uiThemeBootstrapType = Type.GetType(
                "YC.Presentation.UiThemeBootstrap, Assembly-CSharp",
                true);
            var uiThemeBootstraps = prefab.GetComponentsInChildren(
                uiThemeBootstrapType,
                true);
            Assert.That(uiThemeBootstraps, Has.Length.EqualTo(1));
            Assert.That(uiThemeBootstraps[0].gameObject, Is.SameAs(prefab));
            Assert.That(((Behaviour)uiThemeBootstraps[0]).enabled, Is.True);
            Assert.That(
                new SerializedObject(uiThemeBootstraps[0])
                    .FindProperty("themeCatalog").objectReferenceValue,
                Is.SameAs(AssetDatabase.LoadMainAssetAtPath(
                    "Assets/YC/Presentation/Content/UiThemeCatalog.asset")));

            var eventCharacterBootstrapType = Type.GetType(
                "YC.Presentation.EventCharacterCatalogBootstrap, Assembly-CSharp",
                true);
            var eventCharacterBootstraps = prefab.GetComponentsInChildren(
                eventCharacterBootstrapType,
                true);
            Assert.That(eventCharacterBootstraps, Has.Length.EqualTo(1));
            Assert.That(eventCharacterBootstraps[0].gameObject, Is.SameAs(prefab));
            Assert.That(((Behaviour)eventCharacterBootstraps[0]).enabled, Is.True);
            Assert.That(
                new SerializedObject(eventCharacterBootstraps[0])
                    .FindProperty("catalog").objectReferenceValue,
                Is.SameAs(AssetDatabase.LoadMainAssetAtPath(
                    "Assets/YC/Presentation/Content/EventCharacterCardCatalog.asset")));
        }

        [TestCase("Assets/Scenes/StartScene.unity", 4)]
        [TestCase("Assets/Scenes/SampleScene.unity", 6)]
        public void Scenes_InheritConnectedBootstrapWithoutFileMutation(string scenePath, int expectedRoots)
        {
            var beforeHash = ComputeSha256(scenePath);
            var scene = SceneManager.GetSceneByPath(scenePath);
            var openedForTest = !scene.isLoaded;
            if (openedForTest)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }

            try
            {
                Assert.That(scene.isDirty, Is.False);
                var roots = scene.GetRootGameObjects();
                Assert.That(roots, Has.Length.EqualTo(expectedRoots));
                Assert.That(
                    roots.Sum(root => root.GetComponentsInChildren<EventSystem>(true).Length),
                    Is.EqualTo(1));

                var bootstrapType = Type.GetType("YC.Presentation.FacilityCatalogBootstrap, Assembly-CSharp", true);
                Assert.That(
                    roots.Sum(root => root.GetComponentsInChildren(bootstrapType, true).Length),
                    Is.EqualTo(1));
                Assert.That(
                    roots.Sum(root => root.GetComponentsInChildren<Component>(true).Count(item => item == null)),
                    Is.Zero);

                var gameSettingsRoots = roots.Where(root =>
                    AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(root)) ==
                    GameSettingsPrefabPath).ToArray();
                Assert.That(gameSettingsRoots, Has.Length.EqualTo(1));
                Assert.That(
                    PrefabUtility.GetPrefabInstanceStatus(gameSettingsRoots[0]),
                    Is.EqualTo(PrefabInstanceStatus.Connected));
                Assert.That(PrefabUtility.GetPropertyModifications(gameSettingsRoots[0]), Has.Length.EqualTo(12));
                Assert.That(scene.isDirty, Is.False);
            }
            finally
            {
                if (openedForTest)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            Assert.That(ComputeSha256(scenePath), Is.EqualTo(beforeHash));
        }

        private static UnityEngine.Object LoadCatalog()
        {
            var catalogType = Type.GetType("YC.Presentation.FacilityCardCatalog, Assembly-CSharp", true);
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogPath, catalogType);
            Assert.That(catalog, Is.Not.Null, CatalogPath);
            var arguments = new object[] { null };
            Assert.That(
                (bool)catalogType.GetMethod("TryValidateConfiguration").Invoke(catalog, arguments),
                Is.True,
                arguments[0] as string);
            return catalog;
        }

        private static List<FacilityCardDefinition> ExportDefinitions()
        {
            var catalog = LoadCatalog();
            var result = catalog.GetType().GetMethod("CreateDefinitions").Invoke(catalog, null) as
                IEnumerable<FacilityCardDefinition>;
            Assert.That(result, Is.Not.Null);
            return result.ToList();
        }

        private static T GetCatalogProperty<T>(UnityEngine.Object catalog, string propertyName)
        {
            return (T)catalog.GetType().GetProperty(propertyName).GetValue(catalog, null);
        }

        private static JObject LoadSource()
        {
            return JObject.Parse(File.ReadAllText(SourcePath));
        }

        private static void AssertContractCardsMatch(
            JArray cards,
            JObject contracts,
            IReadOnlyDictionary<string, FacilityCardDefinition> byId,
            bool reserveOnly)
        {
            foreach (var token in cards)
            {
                var card = (JObject)token;
                var id = card.Value<string>("id");
                var name = card.Value<string>("name");
                var contract = (JObject)contracts[name];
                Assert.That(byId.ContainsKey(id), Is.True, id);
                AssertDefinitionMatches(card, contract, byId[id], reserveOnly);
            }
        }

        private static void AssertSupplementalCardMatches(
            JObject card,
            IReadOnlyDictionary<string, FacilityCardDefinition> byId)
        {
            var id = card.Value<string>("id");
            Assert.That(byId.ContainsKey(id), Is.True);
            AssertDefinitionMatches(card, card, byId[id], false);
            Assert.That(card.Value<bool>("defaultSupply"), Is.False);
            Assert.That(card.Value<bool>("reserveOnly"), Is.False);
        }

        private static void AssertDefinitionMatches(
            JObject card,
            JObject contract,
            FacilityCardDefinition definition,
            bool reserveOnly)
        {
            var id = card.Value<string>("id");
            var name = card.Value<string>("name");
            Assert.That(definition.FacilityId, Is.EqualTo(id));
            Assert.That(definition.ManifestId, Is.EqualTo(id));
            Assert.That(definition.Name, Is.EqualTo(name));
            Assert.That(definition.Color, Is.EqualTo(card.Value<string>("color")));
            Assert.That(definition.Score, Is.EqualTo(card.Value<int>("score")));
            Assert.That(
                definition.GoldVoucherCost,
                Is.EqualTo(card["goldVoucherCost"].Type == JTokenType.Null
                    ? 0
                    : card.Value<int>("goldVoucherCost")));
            AssertResourceSetMatches((JObject)card["resourceCost"], definition.ResourceCost);
            Assert.That(definition.EffectId, Is.EqualTo(contract.Value<string>("effectId")));
            Assert.That(definition.EffectType, Is.EqualTo(contract.Value<string>("effectType")));
            Assert.That(definition.HasEntryEffect, Is.EqualTo(contract.Value<bool>("hasEntryEffect")));
            var expectedKeywords = ((JArray)contract["keywords"]).Values<string>().ToArray();
            Assert.That(definition.Keywords, Is.EqualTo(expectedKeywords));
            Assert.That(definition.Unique, Is.EqualTo(expectedKeywords.Contains(FacilityCardKeywords.Unique)));
            Assert.That(definition.UniqueGroupId, Is.EqualTo(definition.Unique ? name : string.Empty));
            Assert.That(definition.Description, Is.EqualTo(card.Value<string>("description") ?? string.Empty));
            Assert.That(definition.EffectText, Is.EqualTo(card.Value<string>("effect") ?? string.Empty));
            Assert.That(definition.ReserveOnly, Is.EqualTo(reserveOnly));
            AssertResourceSetMatches(contract["onBuiltReward"] as JObject, definition.OnBuiltReward);
        }

        private static void AssertResourceSetMatches(JObject source, ResourceSet actual)
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual.Originium, Is.EqualTo(ReadResource(source, "源岩")));
            Assert.That(actual.OriginiumShard, Is.EqualTo(ReadResource(source, "源石碎片")));
            Assert.That(actual.Iron, Is.EqualTo(ReadResource(source, "异铁")));
            Assert.That(actual.PureOriginium, Is.EqualTo(ReadResource(source, "至纯源石")));
            Assert.That(actual.GoldVoucher, Is.EqualTo(ReadResource(source, "金券")));
        }

        private static int ReadResource(JObject source, string key)
        {
            return source == null || source[key] == null ? 0 : source.Value<int>(key);
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)))
                    .Replace("-", string.Empty);
            }
        }
    }
}
