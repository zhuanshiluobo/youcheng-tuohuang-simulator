using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class FacilityCardDatabaseInitializationContractTests
    {
        private const string CatalogAssetPath =
            "Assets/YC/Presentation/Content/FacilityCardCatalog.asset";

        [Test]
        public void Queries_FailFastBeforeInitializationEvenForOtherwiseEmptyInputs()
        {
            var definitions = ExportDefinitions();
            ResetDatabaseForTest();

            try
            {
                AssertUninitialized(() => FacilityCardDatabase.Get(null));
                AssertUninitialized(() => FacilityCardDatabase.TryGet(string.Empty, out _));
                AssertUninitialized(() =>
                    FacilityCardDatabase.PlayerHasBuiltUniqueFacility(null, null));
                AssertUninitialized(() => ReadDefaultSupplyCount());
                AssertUninitialized(() => ReadReserveCount());
            }
            finally
            {
                FacilityCardDatabase.Initialize(definitions);
            }
        }

        [Test]
        public void Initialize_AcceptsExactlyTheNineteenKnownEffectContracts()
        {
            var definitions = ExportDefinitions();
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                FacilityCardEffectIds.UniqueOnly,
                FacilityCardEffectIds.CopyAdjacentEntryEffect,
                FacilityCardEffectIds.ClaimStartMarkerAtCleanup,
                FacilityCardEffectIds.BuildAdditionalFacility,
                FacilityCardEffectIds.BuildExtensionHub,
                FacilityCardEffectIds.GainOriginiumShardSix,
                FacilityCardEffectIds.GainGoldPerCoreAdjacentFacility,
                FacilityCardEffectIds.GainIronFour,
                FacilityCardEffectIds.SellResources,
                FacilityCardEffectIds.DiscountOriginiumByFacilityColor,
                FacilityCardEffectIds.FreeCityMoveAndDeployRouteInfluence,
                FacilityCardEffectIds.GainOriginiumSeven,
                FacilityCardEffectIds.ChooseFiveBasicResources,
                FacilityCardEffectIds.ReplaceOneInfluence,
                FacilityCardEffectIds.DeployTwoInfluences,
                FacilityCardEffectIds.RemoveThenDispatchOrExplore,
                FacilityCardEffectIds.SetupCoreCommandTower,
                FacilityCardEffectIds.ReserveExtensionHub,
                FacilityCardEffectIds.EnterpriseOffice
            };

            Assert.That(
                new HashSet<string>(definitions.Select(item => item.EffectId), StringComparer.Ordinal),
                Is.EqualTo(expected));
            Assert.DoesNotThrow(() => FacilityCardDatabase.Initialize(definitions));
        }

        [Test]
        public void Initialize_RejectsUnknownEffectIdAndEntryKeywordMismatch()
        {
            var unknown = ExportDefinitions();
            unknown[0].EffectId = "unknown_effect";
            AssertInvalid(unknown, "未知 effectId");

            var mismatchedEntry = ExportDefinitions();
            mismatchedEntry[0].HasEntryEffect = !mismatchedEntry[0].HasEntryEffect;
            AssertInvalid(mismatchedEntry, "HasEntryEffect");
        }

        [TestCase(FacilityCardEffectIds.GainOriginiumShardSix)]
        [TestCase(FacilityCardEffectIds.GainIronFour)]
        [TestCase(FacilityCardEffectIds.GainOriginiumSeven)]
        public void Initialize_RejectsIncorrectRequiredReward(string effectId)
        {
            var definitions = ExportDefinitions();
            var definition = definitions.First(item => item.EffectId == effectId);
            definition.OnBuiltReward = new ResourceSet();

            AssertInvalid(definitions, "OnBuiltReward");
        }

        [Test]
        public void Initialize_RejectsRewardAttachedToOrdinaryEffect()
        {
            var definitions = ExportDefinitions();
            var definition = definitions.First(item =>
                item.EffectId == FacilityCardEffectIds.UniqueOnly);
            definition.OnBuiltReward.Originium = 1;

            AssertInvalid(definitions, "OnBuiltReward");
        }

        [Test]
        public void SupplyLists_AreDerivedExactAndReadOnlyAfterInitialization()
        {
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Has.Count.EqualTo(41));
            Assert.That(FacilityCardDatabase.ReserveIds, Has.Count.EqualTo(4));
            for (var index = 1; index <= 41; index++)
            {
                Assert.That(
                    FacilityCardDatabase.DefaultSupplyIds[index - 1],
                    Is.EqualTo("building_" + index.ToString("000")));
            }

            for (var index = 1; index <= 4; index++)
            {
                Assert.That(
                    FacilityCardDatabase.ReserveIds[index - 1],
                    Is.EqualTo("reserve_" + index.ToString("000")));
            }

            Assert.That(FacilityCardDatabase.DefaultSupplyIds,
                Does.Not.Contain(FacilityCardDatabase.EnterpriseOffice));
            Assert.That(FacilityCardDatabase.ReserveIds,
                Does.Not.Contain(FacilityCardDatabase.EnterpriseOffice));
            Assert.Throws<NotSupportedException>(() =>
                ((ICollection<string>)FacilityCardDatabase.DefaultSupplyIds).Add("mutated"));
            Assert.Throws<NotSupportedException>(() =>
                ((ICollection<string>)FacilityCardDatabase.ReserveIds).Add("mutated"));
        }

        private static int ReadDefaultSupplyCount()
        {
            return FacilityCardDatabase.DefaultSupplyIds.Count;
        }

        private static int ReadReserveCount()
        {
            return FacilityCardDatabase.ReserveIds.Count;
        }

        private static void AssertUninitialized(TestDelegate action)
        {
            var exception = Assert.Throws<InvalidOperationException>(action);
            Assert.That(exception.Message, Does.Contain("FacilityCardDatabase 未初始化"));
        }

        private static void AssertInvalid(
            IEnumerable<FacilityCardDefinition> definitions,
            string expectedMessage)
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                FacilityCardDatabase.Initialize(definitions));
            Assert.That(exception.Message, Does.Contain(expectedMessage));
        }

        private static List<FacilityCardDefinition> ExportDefinitions()
        {
            var catalogType = Type.GetType(
                "YC.Presentation.FacilityCardCatalog, Assembly-CSharp",
                true);
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogAssetPath, catalogType);
            Assert.That(catalog, Is.Not.Null, CatalogAssetPath);
            var definitions = catalogType.GetMethod("CreateDefinitions")
                .Invoke(catalog, null) as IEnumerable<FacilityCardDefinition>;
            Assert.That(definitions, Is.Not.Null);
            return definitions.ToList();
        }

        private static void ResetDatabaseForTest()
        {
            var type = typeof(FacilityCardDatabase);
            var definitions = type.GetField(
                "Definitions",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) as IDictionary;
            Assert.That(definitions, Is.Not.Null);
            definitions.Clear();
            type.GetField("injectedDefinitions", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, false);
            type.GetField("defaultSupplyIds", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, Array.Empty<string>());
            type.GetField("reserveIds", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, Array.Empty<string>());
            Assert.That(FacilityCardDatabase.IsInitialized, Is.False);
        }
    }
}
