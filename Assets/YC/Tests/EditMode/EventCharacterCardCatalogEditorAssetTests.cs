using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Domain.Cards;

namespace YC.Tests.EditMode
{
    public sealed class EventCharacterCardCatalogEditorAssetTests
    {
        private const string CatalogPath =
            "Assets/YC/Presentation/Content/EventCharacterCardCatalog.asset";
        private const string ManifestPath =
            "Assets/YC/Editor/Data/event_character_cards_manifest.json";
        private const string ExpectedManifestSha256 =
            "881C7BF30F28D930C39D4664BDFABDB6ED9A020C2CD781C5D9876AB216DB0379";

        [Test]
        public void PersistentCatalog_IsUniqueValidAndExactlyMatchesLockedManifest()
        {
            var catalogType = CatalogType();
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogPath, catalogType);
            Assert.That(catalog, Is.Not.Null);
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(CatalogPath), Has.Length.EqualTo(1));
            Assert.That(AssetDatabase.AssetPathToGUID(CatalogPath),
                Is.EqualTo("3a15167db88346acada95f74436e8ade"));
            Assert.That(AssetDatabase.AssetPathToGUID(ManifestPath),
                Is.EqualTo("52c63b90bf124a6e87e73b2bb48052b5"));

            var arguments = new object[] { null };
            var isValid = (bool)catalogType.GetMethod("TryValidateConfiguration")
                .Invoke(catalog, arguments);
            Assert.That(isValid, Is.True, arguments[0] as string);
            Assert.That(catalogType.GetProperty("EventDefinitionCount").GetValue(catalog),
                Is.EqualTo(22));
            Assert.That(catalogType.GetProperty("CharacterDefinitionCount").GetValue(catalog),
                Is.EqualTo(5));
            Assert.That(catalogType.GetProperty("SourceSha256").GetValue(catalog),
                Is.EqualTo(ExpectedManifestSha256));

            var builderType = Type.GetType(
                "YC.Editor.EventCharacterCardCatalogEditorAssetBuilder, Assembly-CSharp-Editor",
                true);
            var computeHash = builderType.GetMethod(
                "ComputeCurrentSourceSha256",
                BindingFlags.NonPublic | BindingFlags.Static);
            var matchesSource = builderType.GetMethod(
                "MatchesSource",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(computeHash, Is.Not.Null);
            Assert.That(matchesSource, Is.Not.Null);
            Assert.That(computeHash.Invoke(null, null), Is.EqualTo(ExpectedManifestSha256));
            Assert.That(matchesSource.Invoke(null, new[] { catalog }), Is.True);
        }

        [Test]
        public void CatalogPayload_PreservesAllStableIdsAndHighRiskEffects()
        {
            var events = LoadEvents();
            var characters = LoadCharacters();

            Assert.That(events, Has.Count.EqualTo(22));
            Assert.That(characters, Has.Count.EqualTo(5));
            CollectionAssert.AreEqual(
                new[]
                {
                    "event_green_01", "event_green_02", "event_green_03",
                    "event_green_04", "event_green_05", "event_green_06",
                    "event_red_01", "event_red_02", "event_red_03",
                    "event_red_04", "event_red_05", "event_red_06",
                    "event_yellow_01", "event_yellow_02", "event_yellow_03",
                    "event_yellow_04", "event_yellow_05", "event_yellow_06",
                    "event_yellow_07", "event_yellow_08", "event_yellow_09",
                    "event_yellow_10"
                },
                events.ConvertAll(card => card.CardId));
            CollectionAssert.AreEqual(
                new[] { "liskarm", "elysium", "texas", "cannot", "tin-man" },
                characters.ConvertAll(card => card.TemplateId));
            CollectionAssert.AreEqual(
                new[] { "雷蛇", "极境", "德克萨斯", "坎诺特", "锡人" },
                characters.ConvertAll(card => card.Name));
            CollectionAssert.AreEqual(
                new[]
                {
                    CharacterCardEffectKind.LiskarmSecurityProtocol,
                    CharacterCardEffectKind.ElysiumLogistics,
                    CharacterCardEffectKind.TexasSpecialDelivery,
                    CharacterCardEffectKind.CannotTradeChannel,
                    CharacterCardEffectKind.TinManEstablishPrestige
                },
                characters.ConvertAll(card => card.StrategyEffect));
            CollectionAssert.AreEqual(
                new[]
                {
                    CharacterCardEffectKind.LiskarmControlPosition,
                    CharacterCardEffectKind.ElysiumNavigation,
                    CharacterCardEffectKind.TexasRemoveAndDoubleMove,
                    CharacterCardEffectKind.CannotRequisition,
                    CharacterCardEffectKind.TinManDeepPlanning
                },
                characters.ConvertAll(card => card.TacticEffect));

            AssertEffect(
                events[6].ChoicePendingEffects[2][0],
                EventEffectKind.PlaceInfluence,
                "CurrentLocationOrAdjacentRoute",
                "Originium",
                1,
                "GoldVoucher",
                0);
            AssertEffect(
                events[10].ChoicePendingEffects[2][0],
                EventEffectKind.GrantResource,
                "Opponents",
                "GoldVoucher",
                3,
                "Originium",
                0);
            AssertEffect(
                events[12].ChoicePendingEffects[1][0],
                EventEffectKind.GainScore,
                "Self",
                "Originium",
                1,
                "Originium",
                0);
            AssertEffect(
                events[15].ChoicePendingEffects[0][0],
                EventEffectKind.PlaceInfluence,
                "None",
                "Originium",
                1,
                "GoldVoucher",
                4);
            AssertEffect(
                events[16].ChoicePendingEffects[0][0],
                EventEffectKind.PlaceInfluence,
                "AdjacentRoute",
                "Originium",
                1,
                "GoldVoucher",
                0);
            AssertEffect(
                events[18].ChoicePendingEffects[1][0],
                EventEffectKind.GrantResource,
                "Opponents",
                "Iron",
                1,
                "Originium",
                0);
            AssertEffect(
                events[19].ChoicePendingEffects[1][0],
                EventEffectKind.GainScore,
                "Self",
                "Originium",
                1,
                "Originium",
                0);

            var effectCount = 0;
            for (var cardIndex = 0; cardIndex < events.Count; cardIndex++)
            {
                for (var choiceIndex = 0;
                     choiceIndex < events[cardIndex].ChoicePendingEffects.Count;
                     choiceIndex++)
                {
                    effectCount += events[cardIndex].ChoicePendingEffects[choiceIndex].Count;
                }
            }

            Assert.That(effectCount, Is.EqualTo(7));
        }

        [Test]
        public void Databases_FailFastDeepCopyAndRejectDifferentReinitialization()
        {
            ResetDatabases();
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => EventCardDatabase.Get("event_green_01"));
                Assert.Throws<InvalidOperationException>(
                    () => CharacterCardDatabase.Get("character.red.p1.liskarm"));

                var events = LoadEvents();
                var characters = LoadCharacters();
                EventCardDatabase.Initialize(events);
                CharacterCardDatabase.Initialize(characters);

                events[0].Name = "污染源";
                events[0].ChoiceRewards[0].Originium = 999;
                characters[0].Name = "污染源";
                Assert.That(EventCardDatabase.Get("event_green_01").Name,
                    Is.EqualTo("中立采石场"));
                Assert.That(EventCardDatabase.Get("event_green_01").ChoiceRewards[0].Originium,
                    Is.EqualTo(0));
                Assert.That(
                    CharacterCardDatabase.Get("character.red.p1.liskarm").Name,
                    Is.EqualTo("雷蛇"));

                var returned = EventCardDatabase.Get("event_red_01");
                returned.ChoicePendingEffects[2][0].Amount = 999;
                Assert.That(
                    EventCardDatabase.Get("event_red_01").ChoicePendingEffects[2][0].Amount,
                    Is.EqualTo(1));

                Assert.DoesNotThrow(() => EventCardDatabase.Initialize(LoadEvents()));
                Assert.DoesNotThrow(() => CharacterCardDatabase.Initialize(LoadCharacters()));

                var differentEvents = LoadEvents();
                differentEvents[0].Name = "不同目录";
                Assert.Throws<InvalidOperationException>(
                    () => EventCardDatabase.Initialize(differentEvents));
                var differentCharacters = LoadCharacters();
                differentCharacters[0].Name = "不同目录";
                Assert.Throws<InvalidOperationException>(
                    () => CharacterCardDatabase.Initialize(differentCharacters));
            }
            finally
            {
                ResetDatabases();
                FacilityCardDatabaseSetUpFixture.InitializeEventAndCharacterCardDatabases();
            }
        }

        [Test]
        public void GameSettingsPrefab_HasExactlyOneEnabledRootBootstrapAndExpectedCatalog()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab");
            var bootstrapType = Type.GetType(
                "YC.Presentation.EventCharacterCatalogBootstrap, Assembly-CSharp",
                true);
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogPath, CatalogType());
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.activeSelf, Is.True);

            var bootstraps = prefab.GetComponentsInChildren(bootstrapType, true);
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            Assert.That(bootstraps[0].gameObject, Is.SameAs(prefab));
            Assert.That(((Behaviour)bootstraps[0]).enabled, Is.True);
            Assert.That(bootstrapType.GetProperty("Catalog").GetValue(bootstraps[0]),
                Is.SameAs(catalog));

            var gateType = Type.GetType(
                "YC.Editor.EventCharacterBuildReadiness, Assembly-CSharp-Editor",
                true);
            Assert.DoesNotThrow(() => gateType.GetMethod("ValidateGameSettingsPrefab")
                .Invoke(null, new[] { catalog }));
        }

        [Test]
        public void GameSettingsBuilderHelper_AttachesEventCharacterBootstrapToTemporaryRoot()
        {
            var facility = LoadAsset(
                "Assets/YC/Presentation/Content/FacilityCardCatalog.asset",
                "YC.Presentation.FacilityCardCatalog, Assembly-CSharp");
            var content = LoadAsset(
                "Assets/YC/Presentation/Content/CityStyleSpecialActionCatalog.asset",
                "YC.Presentation.CityStyleSpecialActionCatalog, Assembly-CSharp");
            var eventCharacter = AssetDatabase.LoadAssetAtPath(CatalogPath, CatalogType());
            var theme = LoadAsset(
                "Assets/YC/Presentation/Content/UiThemeCatalog.asset",
                "YC.Presentation.UiThemeCatalog, Assembly-CSharp");
            var builderType = Type.GetType(
                "YC.EditorTools.GameSettingsMenuEditorAssetBuilder, Assembly-CSharp-Editor",
                true);
            var configure = builderType.GetMethod(
                "ConfigureContentBootstraps",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(configure, Is.Not.Null);

            var root = new GameObject("Temporary GameSettings Root");
            try
            {
                Assert.DoesNotThrow(() => configure.Invoke(
                    null,
                    new[] { root, facility, content, eventCharacter, theme }));
                var bootstrapType = Type.GetType(
                    "YC.Presentation.EventCharacterCatalogBootstrap, Assembly-CSharp",
                    true);
                var bootstraps = root.GetComponentsInChildren(bootstrapType, true);
                Assert.That(bootstraps, Has.Length.EqualTo(1));
                Assert.That(bootstraps[0].gameObject, Is.SameAs(root));
                Assert.That(new SerializedObject(bootstraps[0]).FindProperty("catalog")
                    .objectReferenceValue, Is.SameAs(eventCharacter));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SceneYamlGate_AcceptsBaseAndRejectsDangerousOverrides()
        {
            const string prefabGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string catalogGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string scriptGuid = "cccccccccccccccccccccccccccccccc";
            const long rootId = 11;
            const long bootstrapId = 22;
            const long catalogId = 11400000;
            var baseYaml =
                "--- !u!1001 &1\nPrefabInstance:\n  m_Modification:\n" +
                "    m_Modifications: []\n    m_RemovedComponents: []\n" +
                "    m_RemovedGameObjects: []\n" +
                "  m_SourcePrefab: {fileID: 100100000, guid: " + prefabGuid +
                ", type: 3}\n";
            var gateType = Type.GetType(
                "YC.Editor.EventCharacterBuildReadiness, Assembly-CSharp-Editor",
                true);
            var validate = gateType.GetMethod(
                "ValidateSavedSceneYaml",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);
            object[] Arguments(string yaml) => new object[]
            {
                "Synthetic.unity", yaml, prefabGuid, rootId, bootstrapId,
                catalogGuid, catalogId, scriptGuid
            };

            Assert.DoesNotThrow(() => validate.Invoke(null, Arguments(baseYaml)));
            AssertRejected(validate, Arguments(baseYaml + baseYaml));
            AssertRejected(
                validate,
                Arguments(baseYaml + "--- !u!114 &2\nMonoBehaviour:\n  m_Script: " +
                    "{fileID: 11500000, guid: " + scriptGuid + ", type: 3}\n"));
            AssertRejected(
                validate,
                Arguments(baseYaml.Replace(
                    "m_RemovedComponents: []",
                    "m_RemovedComponents:\n    - {fileID: " + bootstrapId +
                    ", guid: " + prefabGuid + ", type: 3}")));
            AssertRejected(
                validate,
                Arguments(WithOverride(
                    baseYaml,
                    rootId,
                    prefabGuid,
                    "m_IsActive",
                    "0",
                    "{fileID: 0}")));
            AssertRejected(
                validate,
                Arguments(WithOverride(
                    baseYaml,
                    bootstrapId,
                    prefabGuid,
                    "m_Enabled",
                    "0",
                    "{fileID: 0}")));
            AssertRejected(
                validate,
                Arguments(WithOverride(
                    baseYaml,
                    bootstrapId,
                    prefabGuid,
                    "catalog",
                    string.Empty,
                    "{fileID: 999, guid: dddddddddddddddddddddddddddddddd, type: 2}")));
            AssertRejected(
                validate,
                Arguments(WithOverride(
                    baseYaml,
                    bootstrapId,
                    prefabGuid,
                    "catalog",
                    string.Empty,
                    "{fileID: 0}")));
        }

        [Test]
        public void FullBuildReadinessGate_PassesCurrentPersistentState()
        {
            var gateType = Type.GetType(
                "YC.Editor.EventCharacterBuildReadiness, Assembly-CSharp-Editor",
                true);
            var validate = gateType.GetMethod("ValidateReadyForBuild");
            Assert.That(validate, Is.Not.Null);
            Assert.DoesNotThrow(() => validate.Invoke(null, null));
        }

        private static Type CatalogType()
        {
            return Type.GetType(
                "YC.Presentation.EventCharacterCardCatalog, Assembly-CSharp",
                true);
        }

        private static List<EventCardDefinition> LoadEvents()
        {
            var type = CatalogType();
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogPath, type);
            var values = type.GetMethod("CreateEventDefinitions").Invoke(catalog, null) as
                IEnumerable<EventCardDefinition>;
            Assert.That(values, Is.Not.Null);
            return new List<EventCardDefinition>(values);
        }

        private static List<CharacterCardDefinition> LoadCharacters()
        {
            var type = CatalogType();
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogPath, type);
            var values = type.GetMethod("CreateCharacterDefinitions").Invoke(catalog, null) as
                IEnumerable<CharacterCardDefinition>;
            Assert.That(values, Is.Not.Null);
            return new List<CharacterCardDefinition>(values);
        }

        private static UnityEngine.Object LoadAsset(string path, string assemblyTypeName)
        {
            var result = AssetDatabase.LoadAssetAtPath(path, Type.GetType(assemblyTypeName, true));
            Assert.That(result, Is.Not.Null, path);
            return result;
        }

        private static void ResetDatabases()
        {
            typeof(EventCardDatabase).GetMethod(
                    "ResetForTests",
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
            typeof(CharacterCardDatabase).GetMethod(
                    "ResetForTests",
                    BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
        }

        private static void AssertEffect(
            EventEffect effect,
            EventEffectKind kind,
            string targetScope,
            string resourceType,
            int amount,
            string costResourceType,
            int costAmount)
        {
            Assert.That(effect.Kind, Is.EqualTo(kind));
            Assert.That(effect.TargetScope.ToString(), Is.EqualTo(targetScope));
            Assert.That(effect.ResourceType.ToString(), Is.EqualTo(resourceType));
            Assert.That(effect.Amount, Is.EqualTo(amount));
            Assert.That(effect.CostResourceType.ToString(), Is.EqualTo(costResourceType));
            Assert.That(effect.CostAmount, Is.EqualTo(costAmount));
        }

        private static string WithOverride(
            string yaml,
            long targetId,
            string prefabGuid,
            string property,
            string value,
            string reference)
        {
            var modification =
                "    - target: {fileID: " + targetId + ", guid: " + prefabGuid +
                ", type: 3}\n" +
                "      propertyPath: " + property + "\n" +
                "      value: " + value + "\n" +
                "      objectReference: " + reference + "\n";
            return yaml.Replace("    m_Modifications: []", "    m_Modifications:\n" + modification);
        }

        private static void AssertRejected(MethodInfo validate, object[] arguments)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => validate.Invoke(null, arguments));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }
    }
}
