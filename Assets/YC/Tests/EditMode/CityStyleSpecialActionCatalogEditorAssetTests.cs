using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Domain.CityStyles;
using YC.Domain.SpecialActions;

namespace YC.Tests.EditMode
{
    public sealed class CityStyleSpecialActionCatalogEditorAssetTests
    {
        private const string CatalogPath =
            "Assets/YC/Presentation/Content/CityStyleSpecialActionCatalog.asset";
        private const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";

        [Test]
        public void Catalog_ExistsIsValidAndExportsExactStableSets()
        {
            var catalog = LoadValidCatalog();
            Assert.That(GetProperty<int>(catalog, "CityStyleCount"), Is.EqualTo(6));
            Assert.That(GetProperty<int>(catalog, "SpecialActionCount"), Is.EqualTo(5));

            var exported = ExportDefinitions(catalog);
            Assert.That(exported.CityStyles.Select(item => item.CityStyleId), Is.EquivalentTo(new[]
            {
                CityStyleDatabase.MilitaryIndustrialArea,
                CityStyleDatabase.MobilizationSupportSystem,
                CityStyleDatabase.CompositePowerSystem,
                CityStyleDatabase.MaterialRelayStation,
                CityStyleDatabase.SourceStoneIndustrialHub,
                CityStyleDatabase.EfficientMobileManagementSystem
            }));
            Assert.That(exported.SpecialActions.Select(item => item.SpecialActionId), Is.EquivalentTo(new[]
            {
                SpecialActionDatabase.MilitaryIndustrialArea,
                SpecialActionDatabase.MobilizationSupportSystem,
                SpecialActionDatabase.CompositePowerSystem,
                SpecialActionDatabase.SourceStoneIndustrialHub,
                SpecialActionDatabase.EfficientMobileManagementSystem
            }));

            var actionsById = exported.SpecialActions.ToDictionary(
                item => item.SpecialActionId,
                StringComparer.Ordinal);
            foreach (var style in exported.CityStyles)
            {
                if (style.CityStyleId == CityStyleDatabase.MaterialRelayStation)
                {
                    Assert.That(style.SpecialActionId, Is.Empty);
                    continue;
                }

                Assert.That(actionsById.ContainsKey(style.SpecialActionId), Is.True, style.CityStyleId);
                Assert.That(actionsById[style.SpecialActionId].CityStyleId, Is.EqualTo(style.CityStyleId));
            }

            Assert.That(
                exported.SpecialActions.All(action => exported.CityStyles.Any(style =>
                    style.CityStyleId == action.CityStyleId &&
                    style.SpecialActionId == action.SpecialActionId)),
                Is.True);
        }

        [Test]
        public void Catalog_PreservesExistingPatternsRewardsCostsAndUsageFields()
        {
            var exported = ExportDefinitions(LoadValidCatalog());
            var styles = exported.CityStyles.ToDictionary(item => item.CityStyleId);
            var actions = exported.SpecialActions.ToDictionary(item => item.SpecialActionId);

            var military = styles[CityStyleDatabase.MilitaryIndustrialArea];
            Assert.That(military.Score, Is.EqualTo(2));
            Assert.That(military.DeclarationRequirement.RequiredPatternCells, Has.Count.EqualTo(2));
            Assert.That(
                military.DeclarationRequirement.RequiredPatternCells[0].AllowedFacilityColors,
                Is.EqualTo(new[] { "blue", "yellow" }));

            var relay = styles[CityStyleDatabase.MaterialRelayStation];
            Assert.That(relay.DeclarationReward.Originium, Is.EqualTo(1));
            Assert.That(relay.DeclarationReward.OriginiumShard, Is.EqualTo(1));
            Assert.That(relay.DeclarationReward.Iron, Is.EqualTo(1));

            var sourceHub = styles[CityStyleDatabase.SourceStoneIndustrialHub];
            Assert.That(sourceHub.Level, Is.EqualTo(2));
            Assert.That(sourceHub.Score, Is.EqualTo(6));
            Assert.That(sourceHub.MaxDeclarationsPerPlayer, Is.EqualTo(2));
            Assert.That(sourceHub.DeclarationRequirement.RequiredPatternCells, Has.Count.EqualTo(6));

            var composite = actions[SpecialActionDatabase.CompositePowerSystem];
            Assert.That(composite.FixedCost.OriginiumShard, Is.EqualTo(1));
            Assert.That(composite.FlexibleOriginiumAndIronCost, Is.EqualTo(3));
            Assert.That(composite.FreeMoveCount, Is.EqualTo(1));
            Assert.That(composite.MaximumTargetCount, Is.EqualTo(1));

            var sourceAction = actions[SpecialActionDatabase.SourceStoneIndustrialHub];
            Assert.That(sourceAction.FixedCost.GoldVoucher, Is.EqualTo(6));
            Assert.That(sourceAction.ExtraMainActionCount, Is.EqualTo(2));
            Assert.That(sourceAction.LocksCharacterCard, Is.True);
        }

        [Test]
        public void Catalog_AllDefinitionsMatchCanonicalMigrationGolden()
        {
            var exported = ExportDefinitions(LoadValidCatalog());
            var actualCityStyles = exported.CityStyles
                .Select(CanonicalCityStyle)
                .ToArray();
            var actualSpecialActions = exported.SpecialActions
                .Select(CanonicalSpecialAction)
                .ToArray();

            Assert.That(actualCityStyles, Is.EqualTo(new[]
            {
                "city_style_military_industrial_area|军工化区域|1|2|同一横排相邻布局：蓝/黄设施 + 红色设施。|2147483647|special_action.city_style.military_industrial_area|0,0,0,0,0|2||||0|0,0,blue,yellow;0,1,red",
                "city_style_mobilization_support_system|动员配套体系|1|3|同一横排相邻布局：黄色设施 + 黄色设施 + 红色设施。|2147483647|special_action.city_style.mobilization_support_system|0,0,0,0,0|3||||0|0,0,yellow;0,1,yellow;0,2,red",
                "city_style_composite_power_system|复合动力系统|1|3|2x2 局部布局：上方黄色；下方红色 + 黄色。|2147483647|special_action.city_style.composite_power_system|0,0,0,0,0|3||||0|0,0,yellow;1,0,red;1,1,yellow",
                "city_style_material_relay_station|物资中继站|1|2|同一横排相邻布局：蓝/红设施 + 黄色设施。|2147483647||1,1,1,0,0|2||||0|0,0,blue,red;0,1,yellow",
                "city_style_source_stone_industrial_hub|源石工业中枢|2|6|3 行阶梯布局：上方蓝色；中间红色 + 蓝色；下方黄色 + 黄色 + 红色。|2|special_action.city_style.source_stone_industrial_hub|0,0,0,0,0|6||||0|0,0,blue;1,0,red;1,1,blue;2,0,yellow;2,1,yellow;2,2,red",
                "city_style_efficient_mobile_management_system|高效移动管理体系|2|7|3 行阶梯布局：上方黄色；中间红色 + 黄色；下方蓝色 + 蓝色 + 红色。|2|special_action.city_style.efficient_mobile_management_system|0,0,0,0,0|6||||0|0,0,yellow;1,0,red;1,1,yellow;2,0,blue;2,1,blue;2,2,red"
            }));

            Assert.That(actualSpecialActions, Is.EqualTo(new[]
            {
                "special_action.city_style.military_industrial_area|city_style_military_industrial_area|军工化区域|放置尽可能多的影响力；数量等于本方在该样式上的标记数，最多 3 个。|1|0|0,0,0,0,0|0|3|0|0|0",
                "special_action.city_style.mobilization_support_system|city_style_mobilization_support_system|动员配套体系|移除 1 个对手影响力，再尽量在原槽位放置自己的影响力。|1|1|0,0,0,0,0|0|1|0|0|0",
                "special_action.city_style.composite_power_system|city_style_composite_power_system|复合动力系统|支付 1 源石碎片及合计 3 个源岩/异铁，免费移动一次，再在经过的航道放置影响力。|1|2|0,1,0,0,0|3|1|1|0|0",
                "special_action.city_style.source_stone_industrial_hub|city_style_source_stone_industrial_hub|源石工业中枢|支付 6 金券，本玩家行动轮获得至多 2 次额外主要行动。|2|3|0,0,0,0,6|0|0|0|2|1",
                "special_action.city_style.efficient_mobile_management_system|city_style_efficient_mobile_management_system|高效移动管理体系|支付 3 源石碎片，连续执行 2 次免费移动城市。|2|4|0,3,0,0,0|0|0|2|0|1"
            }));

            Assert.That(
                exported.CityStyles.Sum(style =>
                    style.DeclarationRequirement.RequiredPatternCells.Count),
                Is.EqualTo(22));
        }

        [Test]
        public void SpecialActionContracts_EachStableEffectRejectsWrongRequiredAndForbiddenParameters()
        {
            var definitions = ExportDefinitions(LoadValidCatalog()).SpecialActions;
            foreach (var source in definitions)
            {
                var wrongEffect = source.Clone();
                wrongEffect.EffectKind = (SpecialActionEffectKind)
                    (((int)source.EffectKind + 1) % 5);
                AssertInvalidSpecialAction(wrongEffect, source.SpecialActionId + " wrong effect");

                var missingRequired = source.Clone();
                var forbiddenParameter = source.Clone();
                switch (source.EffectKind)
                {
                    case SpecialActionEffectKind.DeployInfluence:
                        missingRequired.MaximumTargetCount = 0;
                        forbiddenParameter.FreeMoveCount = 1;
                        break;
                    case SpecialActionEffectKind.ReplaceInfluence:
                        missingRequired.MaximumTargetCount = 0;
                        forbiddenParameter.ExtraMainActionCount = 1;
                        break;
                    case SpecialActionEffectKind.CompositePowerMove:
                        missingRequired.FlexibleOriginiumAndIronCost = 0;
                        forbiddenParameter.ExtraMainActionCount = 1;
                        break;
                    case SpecialActionEffectKind.GrantExtraMainActions:
                        missingRequired.ExtraMainActionCount = 0;
                        forbiddenParameter.MaximumTargetCount = 1;
                        break;
                    case SpecialActionEffectKind.ConsecutiveFreeMoves:
                        missingRequired.FreeMoveCount = 0;
                        forbiddenParameter.MaximumTargetCount = 1;
                        break;
                    default:
                        Assert.Fail("Unexpected effect kind: " + source.EffectKind);
                        break;
                }

                AssertInvalidSpecialAction(
                    missingRequired,
                    source.SpecialActionId + " missing required parameter");
                AssertInvalidSpecialAction(
                    forbiddenParameter,
                    source.SpecialActionId + " forbidden parameter");
            }
        }

        [Test]
        public void CityStyleContracts_RejectInvalidOrDuplicateRequirementValues()
        {
            var source = ExportDefinitions(LoadValidCatalog()).CityStyles[0];
            AssertInvalidCityStyle(source, style =>
                style.DeclarationRequirement.RequiredResourceTypes.Add(
                    (YC.Domain.Rules.ResourceType)999));
            AssertInvalidCityStyle(source, style =>
            {
                style.DeclarationRequirement.RequiredResourceTypes.Add(
                    YC.Domain.Rules.ResourceType.Originium);
                style.DeclarationRequirement.RequiredResourceTypes.Add(
                    YC.Domain.Rules.ResourceType.Originium);
            });
            AssertInvalidCityStyle(source, style =>
                style.DeclarationRequirement.RequiredEffectTypes.Add(" "));
            AssertInvalidCityStyle(source, style =>
            {
                style.DeclarationRequirement.RequiredEffectTypes.Add("entry");
                style.DeclarationRequirement.RequiredEffectTypes.Add("entry");
            });
            AssertInvalidCityStyle(source, style =>
                style.DeclarationRequirement.RequiredCityBoardSlotIndexes.Add(-1));
            AssertInvalidCityStyle(source, style =>
                style.DeclarationRequirement.RequiredCityBoardSlotIndexes.Add(
                    YC.Domain.Facilities.BuildFacilityService.CityBoardSlotCount));
            AssertInvalidCityStyle(source, style =>
            {
                style.DeclarationRequirement.RequiredCityBoardSlotIndexes.Add(0);
                style.DeclarationRequirement.RequiredCityBoardSlotIndexes.Add(0);
            });
        }

        [Test]
        public void Prefab_ExplicitlyReferencesCatalogFromSingleEarlyBootstrap()
        {
            var catalog = LoadValidCatalog();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null, PrefabPath);

            var bootstrapType = Type.GetType(
                "YC.Presentation.CityStyleSpecialActionCatalogBootstrap, Assembly-CSharp",
                true);
            var bootstraps = prefab.GetComponentsInChildren(bootstrapType, true);
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            Assert.That(bootstraps[0].gameObject, Is.SameAs(prefab));

            var serializedBootstrap = new SerializedObject(bootstraps[0]);
            Assert.That(
                serializedBootstrap.FindProperty("catalog").objectReferenceValue,
                Is.SameAs(catalog));

            var order = (DefaultExecutionOrder)Attribute.GetCustomAttribute(
                bootstrapType,
                typeof(DefaultExecutionOrder));
            Assert.That(order, Is.Not.Null);
            Assert.That(order.order, Is.LessThan(-20000));
        }

        [Test]
        public void BuildReadiness_CurrentAssetsPrefabAndBuildSettingsPass()
        {
            var readinessType = Type.GetType(
                "YC.Editor.CityStyleSpecialActionBuildReadiness, Assembly-CSharp-Editor",
                true);
            var validate = readinessType.GetMethod(
                "ValidateReadyForBuild",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);
            Assert.DoesNotThrow(() => validate.Invoke(null, null));

            var preprocessorType = Type.GetType(
                "YC.Editor.CityStyleSpecialActionBuildReadinessPreprocessor, Assembly-CSharp-Editor",
                true);
            var preprocessor = Activator.CreateInstance(preprocessorType);
            Assert.That(
                (int)preprocessorType.GetProperty("callbackOrder").GetValue(preprocessor, null),
                Is.EqualTo(-260));
        }

        [Test]
        public void ProductionScenes_KeepConnectedPrefabBootstrapWithoutRemovedComponentOverride()
        {
            var readinessType = Type.GetType(
                "YC.Editor.CityStyleSpecialActionBuildReadiness, Assembly-CSharp-Editor",
                true);
            var validateScenes = readinessType.GetMethod(
                "ValidateSavedProductionSceneConnections",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validateScenes, Is.Not.Null);
            Assert.DoesNotThrow(() => validateScenes.Invoke(null, null));
        }

        [Test]
        public void SavedSceneYamlGate_RejectsDisabledNullCatalogAndDuplicateBootstrapOverrides()
        {
            const string scenePath = "Assets/Scenes/StartScene.unity";
            const string prefabPath =
                "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                prefab, out var rootGuid, out long rootLocalId), Is.True);
            Assert.That(rootGuid, Is.EqualTo(prefabGuid));

            var bootstrapType = Type.GetType(
                "YC.Presentation.CityStyleSpecialActionCatalogBootstrap, Assembly-CSharp",
                true);
            var bootstrap = (MonoBehaviour)prefab.GetComponentsInChildren(bootstrapType, true)[0];
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                bootstrap, out var bootstrapGuid, out long bootstrapLocalId), Is.True);
            Assert.That(bootstrapGuid, Is.EqualTo(prefabGuid));

            var catalog = LoadValidCatalog();
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                catalog, out var catalogGuid, out long catalogLocalId), Is.True);
            var script = MonoScript.FromMonoBehaviour(bootstrap);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                script, out var scriptGuid, out long scriptLocalId), Is.True);

            var readinessType = Type.GetType(
                "YC.Editor.CityStyleSpecialActionBuildReadiness, Assembly-CSharp-Editor",
                true);
            var validateYaml = readinessType.GetMethod(
                "ValidateSavedSceneYaml",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validateYaml, Is.Not.Null);
            var arguments = new object[]
            {
                scenePath,
                File.ReadAllText(scenePath),
                prefabGuid,
                rootLocalId,
                bootstrapLocalId,
                catalogGuid,
                catalogLocalId,
                scriptGuid
            };

            AssertYamlRejected(validateYaml, arguments, AddPrefabOverride(
                (string)arguments[1], prefabGuid, rootLocalId, "m_IsActive", "0", "{fileID: 0}"));
            AssertYamlRejected(validateYaml, arguments, AddPrefabOverride(
                (string)arguments[1], prefabGuid, bootstrapLocalId, "m_Enabled", "0", "{fileID: 0}"));
            AssertYamlRejected(validateYaml, arguments, AddPrefabOverride(
                (string)arguments[1], prefabGuid, bootstrapLocalId, "catalog", string.Empty, "{fileID: 0}"));

            var facilityCatalogType = Type.GetType(
                "YC.Presentation.FacilityCardCatalog, Assembly-CSharp",
                true);
            var differentCatalog = AssetDatabase.LoadAssetAtPath(
                "Assets/YC/Presentation/Content/FacilityCardCatalog.asset",
                facilityCatalogType);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                differentCatalog, out var differentGuid, out long differentLocalId), Is.True);
            AssertYamlRejected(validateYaml, arguments, AddPrefabOverride(
                (string)arguments[1],
                prefabGuid,
                bootstrapLocalId,
                "catalog",
                string.Empty,
                "{fileID: " + differentLocalId + ", guid: " + differentGuid + ", type: 2}"));

            var duplicateYaml = (string)arguments[1] +
                "\n--- !u!114 &919191919191919191\nMonoBehaviour:\n" +
                "  m_Script: {fileID: " + scriptLocalId + ", guid: " + scriptGuid + ", type: 3}\n";
            AssertYamlRejected(validateYaml, arguments, duplicateYaml);
        }

        [Test]
        public void GameSettingsBuilderHelper_AttachesBothPersistentCatalogBootstrapsToTemporaryRoot()
        {
            var facilityCatalogType = Type.GetType(
                "YC.Presentation.FacilityCardCatalog, Assembly-CSharp",
                true);
            var facilityCatalog = AssetDatabase.LoadAssetAtPath(
                "Assets/YC/Presentation/Content/FacilityCardCatalog.asset",
                facilityCatalogType);
            var contentCatalog = LoadValidCatalog();
            Assert.That(facilityCatalog, Is.Not.Null);
            var eventCatalogType = Type.GetType(
                "YC.Presentation.EventCharacterCardCatalog, Assembly-CSharp",
                true);
            var eventCatalog = AssetDatabase.LoadAssetAtPath(
                "Assets/YC/Presentation/Content/EventCharacterCardCatalog.asset",
                eventCatalogType);
            var themeCatalogType = Type.GetType(
                "YC.Presentation.UiThemeCatalog, Assembly-CSharp",
                true);
            var themeCatalog = AssetDatabase.LoadAssetAtPath(
                "Assets/YC/Presentation/Content/UiThemeCatalog.asset",
                themeCatalogType);
            Assert.That(eventCatalog, Is.Not.Null);
            Assert.That(themeCatalog, Is.Not.Null);

            var builderType = Type.GetType(
                "YC.EditorTools.GameSettingsMenuEditorAssetBuilder, Assembly-CSharp-Editor",
                true);
            var configure = builderType.GetMethod(
                "ConfigureContentBootstraps",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(configure, Is.Not.Null);

            var temporaryRoot = new GameObject("Temporary GameSettings Root");
            try
            {
                Assert.DoesNotThrow(() => configure.Invoke(
                    null,
                    new object[]
                    {
                        temporaryRoot,
                        facilityCatalog,
                        contentCatalog,
                        eventCatalog,
                        themeCatalog
                    }));

                var facilityBootstrapType = Type.GetType(
                    "YC.Presentation.FacilityCatalogBootstrap, Assembly-CSharp",
                    true);
                var contentBootstrapType = Type.GetType(
                    "YC.Presentation.CityStyleSpecialActionCatalogBootstrap, Assembly-CSharp",
                    true);
                var facilityBootstraps = temporaryRoot.GetComponentsInChildren(
                    facilityBootstrapType,
                    true);
                var contentBootstraps = temporaryRoot.GetComponentsInChildren(
                    contentBootstrapType,
                    true);
                Assert.That(facilityBootstraps, Has.Length.EqualTo(1));
                Assert.That(contentBootstraps, Has.Length.EqualTo(1));
                Assert.That(facilityBootstraps[0].gameObject, Is.SameAs(temporaryRoot));
                Assert.That(contentBootstraps[0].gameObject, Is.SameAs(temporaryRoot));
                Assert.That(
                    new SerializedObject(facilityBootstraps[0])
                        .FindProperty("facilityCardCatalog").objectReferenceValue,
                    Is.SameAs(facilityCatalog));
                Assert.That(
                    new SerializedObject(contentBootstraps[0])
                        .FindProperty("catalog").objectReferenceValue,
                    Is.SameAs(contentCatalog));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporaryRoot);
            }
        }

        [Test]
        public void Databases_UninitializedQueriesFailFastAndStateIsAlwaysRestored()
        {
            var original = ExportDefinitions(LoadValidCatalog());
            try
            {
                ResetDatabases();
                Assert.That(CityStyleDatabase.IsInitialized, Is.False);
                Assert.That(SpecialActionDatabase.IsInitialized, Is.False);

                Assert.Throws<InvalidOperationException>(() =>
                {
                    var unused = CityStyleDatabase.All;
                });
                Assert.Throws<InvalidOperationException>(() =>
                    CityStyleDatabase.Get(CityStyleDatabase.MilitaryIndustrialArea));
                Assert.Throws<InvalidOperationException>(() =>
                {
                    var unused = CityStyleDatabase.DefaultSupplyIds;
                });
                Assert.Throws<InvalidOperationException>(() =>
                {
                    var unused = SpecialActionDatabase.All;
                });
                Assert.Throws<InvalidOperationException>(() =>
                    SpecialActionDatabase.Get(SpecialActionDatabase.MilitaryIndustrialArea));
            }
            finally
            {
                RestoreDatabases(original);
            }

            Assert.That(CityStyleDatabase.IsInitialized, Is.True);
            Assert.That(SpecialActionDatabase.IsInitialized, Is.True);
        }

        [Test]
        public void Databases_RejectDuplicateMissingAndBrokenBidirectionalMappings()
        {
            var original = ExportDefinitions(LoadValidCatalog());
            try
            {
                ResetCityStyles();
                var duplicateStyles = new List<CityStyleDefinition>(original.CityStyles)
                {
                    original.CityStyles[0]
                };
                Assert.Throws<InvalidOperationException>(() =>
                    CityStyleDatabase.Initialize(duplicateStyles));

                ResetSpecialActions();
                Assert.Throws<InvalidOperationException>(() =>
                    SpecialActionDatabase.Initialize(original.SpecialActions.Take(4)));

                ResetCityStyles();
                var brokenStyles = CloneCityStyles(original.CityStyles);
                brokenStyles[0].SpecialActionId = SpecialActionDatabase.CompositePowerSystem;
                Assert.Throws<InvalidOperationException>(() =>
                    CityStyleDatabase.Initialize(brokenStyles));

                ResetSpecialActions();
                var brokenActions = CloneSpecialActions(original.SpecialActions);
                brokenActions[0].CityStyleId = CityStyleDatabase.CompositePowerSystem;
                Assert.Throws<InvalidOperationException>(() =>
                    SpecialActionDatabase.Initialize(brokenActions));
            }
            finally
            {
                RestoreDatabases(original);
            }
        }

        [Test]
        public void Databases_DeepCopyInjectionAndExposeReadOnlyCopies()
        {
            var original = ExportDefinitions(LoadValidCatalog());
            try
            {
                ResetDatabases();
                var injectedStyles = CloneCityStyles(original.CityStyles);
                var injectedActions = CloneSpecialActions(original.SpecialActions);
                CityStyleDatabase.Initialize(injectedStyles);
                SpecialActionDatabase.Initialize(injectedActions);

                injectedStyles[0].Name = "mutated source";
                injectedStyles[0].DeclarationRequirement.RequiredPatternCells[0]
                    .AllowedFacilityColors[0] = "red";
                injectedActions[0].FixedCost.GoldVoucher = 99;

                var firstStyle = CityStyleDatabase.Get(original.CityStyles[0].CityStyleId);
                var firstAction = SpecialActionDatabase.Get(original.SpecialActions[0].SpecialActionId);
                Assert.That(firstStyle.Name, Is.Not.EqualTo("mutated source"));
                Assert.That(
                    firstStyle.DeclarationRequirement.RequiredPatternCells[0].AllowedFacilityColors[0],
                    Is.Not.EqualTo("red"));
                Assert.That(firstAction.FixedCost.GoldVoucher, Is.Not.EqualTo(99));

                firstStyle.Name = "mutated query";
                firstAction.FixedCost.GoldVoucher = 88;
                Assert.That(
                    CityStyleDatabase.Get(firstStyle.CityStyleId).Name,
                    Is.Not.EqualTo("mutated query"));
                Assert.That(
                    SpecialActionDatabase.Get(firstAction.SpecialActionId).FixedCost.GoldVoucher,
                    Is.Not.EqualTo(88));
                Assert.That(CityStyleDatabase.All, Is.InstanceOf<IReadOnlyList<CityStyleDefinition>>());
                Assert.That(SpecialActionDatabase.All, Is.InstanceOf<IReadOnlyList<SpecialActionDefinition>>());
            }
            finally
            {
                RestoreDatabases(original);
            }
        }

        [Test]
        public void Databases_SameCompleteSnapshotIsIdempotentAndDifferentSnapshotIsRejected()
        {
            var original = ExportDefinitions(LoadValidCatalog());
            try
            {
                Assert.DoesNotThrow(() => CityStyleDatabase.Initialize(original.CityStyles));
                Assert.DoesNotThrow(() => SpecialActionDatabase.Initialize(original.SpecialActions));

                var differentCityStyles = CloneCityStyles(original.CityStyles);
                differentCityStyles[0].Score++;
                var cityException = Assert.Throws<InvalidOperationException>(() =>
                    CityStyleDatabase.Initialize(differentCityStyles));
                Assert.That(cityException.Message, Does.Contain("不同"));

                var differentSpecialActions = CloneSpecialActions(original.SpecialActions);
                differentSpecialActions[0].Name += "（不同快照）";
                var actionException = Assert.Throws<InvalidOperationException>(() =>
                    SpecialActionDatabase.Initialize(differentSpecialActions));
                Assert.That(actionException.Message, Does.Contain("不同"));

                Assert.That(
                    CityStyleDatabase.Get(original.CityStyles[0].CityStyleId).Score,
                    Is.EqualTo(original.CityStyles[0].Score));
                Assert.That(
                    SpecialActionDatabase.Get(original.SpecialActions[0].SpecialActionId).Name,
                    Is.EqualTo(original.SpecialActions[0].Name));
            }
            finally
            {
                RestoreDatabases(original);
            }
        }

        private static UnityEngine.Object LoadValidCatalog()
        {
            var type = Type.GetType(
                "YC.Presentation.CityStyleSpecialActionCatalog, Assembly-CSharp",
                true);
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogPath, type);
            Assert.That(catalog, Is.Not.Null, CatalogPath);
            var arguments = new object[] { null };
            Assert.That(
                (bool)type.GetMethod("TryValidateConfiguration").Invoke(catalog, arguments),
                Is.True,
                arguments[0] as string);
            return catalog;
        }

        private static ExportedDefinitions ExportDefinitions(UnityEngine.Object catalog)
        {
            var definitionSet = catalog.GetType().GetMethod("CreateDefinitions").Invoke(catalog, null);
            Assert.That(definitionSet, Is.Not.Null);
            var type = definitionSet.GetType();
            var cityStyles = (IEnumerable<CityStyleDefinition>)type.GetProperty("CityStyles")
                .GetValue(definitionSet, null);
            var specialActions = (IEnumerable<SpecialActionDefinition>)type.GetProperty("SpecialActions")
                .GetValue(definitionSet, null);
            return new ExportedDefinitions(cityStyles.ToList(), specialActions.ToList());
        }

        private static T GetProperty<T>(UnityEngine.Object target, string name)
        {
            return (T)target.GetType().GetProperty(name).GetValue(target, null);
        }

        private static string AddPrefabOverride(
            string yaml,
            string prefabGuid,
            long targetLocalId,
            string propertyPath,
            string value,
            string objectReference)
        {
            var sourceToken =
                "m_SourcePrefab: {fileID: 100100000, guid: " + prefabGuid + ", type: 3}";
            var sourceIndex = yaml.IndexOf(sourceToken, StringComparison.Ordinal);
            Assert.That(sourceIndex, Is.GreaterThanOrEqualTo(0));
            var insertionIndex = yaml.LastIndexOf(
                "    m_RemovedComponents:",
                sourceIndex,
                StringComparison.Ordinal);
            Assert.That(insertionIndex, Is.GreaterThanOrEqualTo(0));
            var entry =
                "    - target: {fileID: " + targetLocalId + ", guid: " + prefabGuid + ", type: 3}\n" +
                "      propertyPath: " + propertyPath + "\n" +
                "      value: " + value + "\n" +
                "      objectReference: " + objectReference + "\n";
            return yaml.Insert(insertionIndex, entry);
        }

        private static void AssertYamlRejected(
            MethodInfo validateYaml,
            object[] baseArguments,
            string yaml)
        {
            var arguments = (object[])baseArguments.Clone();
            arguments[1] = yaml;
            var exception = Assert.Throws<TargetInvocationException>(() =>
                validateYaml.Invoke(null, arguments));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static string CanonicalCityStyle(CityStyleDefinition definition)
        {
            var requirement = definition.DeclarationRequirement;
            var cells = requirement.RequiredPatternCells.Select(cell =>
                cell.RowOffset + "," + cell.ColumnOffset + "," +
                string.Join(",", cell.AllowedFacilityColors.ToArray()));
            return string.Join("|", new[]
            {
                definition.CityStyleId,
                definition.Name,
                definition.Level.ToString(),
                definition.Score.ToString(),
                definition.Description,
                definition.MaxDeclarationsPerPlayer.ToString(),
                definition.SpecialActionId,
                CanonicalResourceSet(definition.DeclarationReward),
                requirement.RequiredFacilityCount.ToString(),
                string.Join(",", requirement.RequiredEffectTypes.ToArray()),
                string.Join(",", requirement.RequiredResourceTypes
                    .Select(value => ((int)value).ToString()).ToArray()),
                string.Join(",", requirement.RequiredCityBoardSlotIndexes
                    .Select(value => value.ToString()).ToArray()),
                requirement.RequireSameCityBoardRow ? "1" : "0",
                string.Join(";", cells.ToArray())
            });
        }

        private static string CanonicalSpecialAction(SpecialActionDefinition definition)
        {
            return string.Join("|", new[]
            {
                definition.SpecialActionId,
                definition.CityStyleId,
                definition.Name,
                definition.Description,
                definition.Level.ToString(),
                ((int)definition.EffectKind).ToString(),
                CanonicalResourceSet(definition.FixedCost),
                definition.FlexibleOriginiumAndIronCost.ToString(),
                definition.MaximumTargetCount.ToString(),
                definition.FreeMoveCount.ToString(),
                definition.ExtraMainActionCount.ToString(),
                definition.LocksCharacterCard ? "1" : "0"
            });
        }

        private static string CanonicalResourceSet(YC.Domain.State.ResourceSet value)
        {
            return string.Join(",", new[]
            {
                value.Originium.ToString(),
                value.OriginiumShard.ToString(),
                value.Iron.ToString(),
                value.PureOriginium.ToString(),
                value.GoldVoucher.ToString()
            });
        }

        private static void AssertInvalidSpecialAction(
            SpecialActionDefinition definition,
            string label)
        {
            Assert.That(
                SpecialActionDatabase.TryValidateDefinition(definition, out var reason),
                Is.False,
                label);
            Assert.That(reason, Is.Not.Empty, label);
        }

        private static void AssertInvalidCityStyle(
            CityStyleDefinition source,
            Action<CityStyleDefinition> mutate)
        {
            var definition = CloneCityStyles(new[] { source })[0];
            mutate(definition);
            Assert.That(
                CityStyleDatabase.TryValidateDefinition(definition, out var reason),
                Is.False);
            Assert.That(reason, Is.Not.Empty);
        }

        private static List<CityStyleDefinition> CloneCityStyles(
            IReadOnlyList<CityStyleDefinition> definitions)
        {
            var result = new List<CityStyleDefinition>(definitions.Count);
            for (var i = 0; i < definitions.Count; i++)
            {
                var source = definitions[i];
                var requirement = source.DeclarationRequirement;
                var requirementCopy = new CityStyleRequirement
                {
                    RequiredFacilityCount = requirement.RequiredFacilityCount,
                    RequiredEffectTypes = new List<string>(requirement.RequiredEffectTypes),
                    RequiredResourceTypes = new List<YC.Domain.Rules.ResourceType>(
                        requirement.RequiredResourceTypes),
                    RequiredCityBoardSlotIndexes = new List<int>(
                        requirement.RequiredCityBoardSlotIndexes),
                    RequireSameCityBoardRow = requirement.RequireSameCityBoardRow
                };
                foreach (var cell in requirement.RequiredPatternCells)
                {
                    requirementCopy.RequiredPatternCells.Add(new CityStylePatternCell
                    {
                        RowOffset = cell.RowOffset,
                        ColumnOffset = cell.ColumnOffset,
                        AllowedFacilityColors = new List<string>(cell.AllowedFacilityColors)
                    });
                }

                result.Add(new CityStyleDefinition
                {
                    CityStyleId = source.CityStyleId,
                    Name = source.Name,
                    Level = source.Level,
                    Score = source.Score,
                    Description = source.Description,
                    MaxDeclarationsPerPlayer = source.MaxDeclarationsPerPlayer,
                    SpecialActionId = source.SpecialActionId,
                    DeclarationReward = source.DeclarationReward.Clone(),
                    DeclarationRequirement = requirementCopy
                });
            }

            return result;
        }

        private static List<SpecialActionDefinition> CloneSpecialActions(
            IReadOnlyList<SpecialActionDefinition> definitions)
        {
            return definitions.Select(item => item.Clone()).ToList();
        }

        private static void ResetDatabases()
        {
            ResetCityStyles();
            ResetSpecialActions();
        }

        private static void ResetCityStyles()
        {
            InvokeReset(typeof(CityStyleDatabase));
        }

        private static void ResetSpecialActions()
        {
            InvokeReset(typeof(SpecialActionDatabase));
        }

        private static void InvokeReset(Type databaseType)
        {
            var method = databaseType.GetMethod(
                "ResetForTests",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, databaseType.FullName);
            method.Invoke(null, null);
        }

        private static void RestoreDatabases(ExportedDefinitions original)
        {
            ResetDatabases();
            CityStyleDatabase.Initialize(original.CityStyles);
            SpecialActionDatabase.Initialize(original.SpecialActions);
        }

        private sealed class ExportedDefinitions
        {
            public ExportedDefinitions(
                List<CityStyleDefinition> cityStyles,
                List<SpecialActionDefinition> specialActions)
            {
                CityStyles = cityStyles;
                SpecialActions = specialActions;
            }

            public List<CityStyleDefinition> CityStyles { get; }
            public List<SpecialActionDefinition> SpecialActions { get; }
        }
    }
}
