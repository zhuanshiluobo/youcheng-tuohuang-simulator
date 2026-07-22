using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Facilities;

namespace YC.Tests.EditMode
{
    public sealed class FacilityCardMetadataTests
    {
        private static readonly Dictionary<string, ExpectedCard> ExpectedCards =
            new Dictionary<string, ExpectedCard>
            {
                { "building_001", new ExpectedCard(3, "rainbow") },
                { "building_002", new ExpectedCard(3, "rainbow") },
                { "building_003", new ExpectedCard(3, "rainbow") },
                { "building_004", new ExpectedCard(1, "rainbow") },
                { "building_005", new ExpectedCard(1, "rainbow") },
                { "building_006", new ExpectedCard(1, "rainbow") },
                { "building_007", new ExpectedCard(0, "rainbow") },
                { "building_008", new ExpectedCard(0, "rainbow") },
                { "building_009", new ExpectedCard(0, "rainbow") },
                { "building_010", new ExpectedCard(-1, "rainbow") },
                { "building_011", new ExpectedCard(-1, "rainbow") },
                { "building_012", new ExpectedCard(2, "blue") },
                { "building_013", new ExpectedCard(2, "blue") },
                { "building_014", new ExpectedCard(0, "blue") },
                { "building_015", new ExpectedCard(1, "blue") },
                { "building_016", new ExpectedCard(1, "blue") },
                { "building_017", new ExpectedCard(1, "blue") },
                { "building_018", new ExpectedCard(1, "blue") },
                { "building_019", new ExpectedCard(0, "blue") },
                { "building_020", new ExpectedCard(0, "blue") },
                { "building_021", new ExpectedCard(0, "blue") },
                { "building_022", new ExpectedCard(2, "yellow") },
                { "building_023", new ExpectedCard(2, "yellow") },
                { "building_024", new ExpectedCard(2, "yellow") },
                { "building_025", new ExpectedCard(0, "yellow") },
                { "building_026", new ExpectedCard(0, "yellow") },
                { "building_027", new ExpectedCard(0, "yellow") },
                { "building_028", new ExpectedCard(0, "yellow") },
                { "building_029", new ExpectedCard(0, "yellow") },
                { "building_030", new ExpectedCard(0, "yellow") },
                { "building_031", new ExpectedCard(0, "yellow") },
                { "building_032", new ExpectedCard(1, "red") },
                { "building_033", new ExpectedCard(0, "red") },
                { "building_034", new ExpectedCard(1, "red") },
                { "building_035", new ExpectedCard(1, "red") },
                { "building_036", new ExpectedCard(1, "red") },
                { "building_037", new ExpectedCard(0, "red") },
                { "building_038", new ExpectedCard(0, "red") },
                { "building_039", new ExpectedCard(0, "red") },
                { "building_040", new ExpectedCard(0, "red") },
                { "building_041", new ExpectedCard(0, "red") }
            };

        [Test]
        public void FormalCards_KeepPerCardScoreColorAndStructuredEffectMetadata()
        {
            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Has.Count.EqualTo(41));
            Assert.That(ExpectedCards, Has.Count.EqualTo(41));
            foreach (var facilityId in FacilityCardDatabase.DefaultSupplyIds)
            {
                var facility = FacilityCardDatabase.Get(facilityId);
                var expected = ExpectedCards[facilityId];

                Assert.That(facility, Is.Not.Null, facilityId);
                Assert.That(facility.ManifestId, Is.EqualTo(facilityId), facilityId);
                Assert.That(facility.Score, Is.EqualTo(expected.Score), facilityId);
                Assert.That(facility.Color, Is.EqualTo(expected.Color), facilityId);
                Assert.That(facility.EffectId, Is.Not.Empty, facilityId);
                Assert.That(facility.Keywords, Is.Not.Null, facilityId);
                Assert.That(
                    facility.Keywords.Distinct().Count(),
                    Is.EqualTo(facility.Keywords.Count),
                    facilityId);
                Assert.That(
                    facility.Keywords.Contains(FacilityCardKeywords.Entry),
                    Is.EqualTo(facility.HasEntryEffect),
                    facilityId);
                Assert.That(
                    facility.Keywords.Contains(FacilityCardKeywords.Unique),
                    Is.EqualTo(facility.Unique),
                    facilityId);
            }
        }

        [Test]
        public void FederalOffice_IsEntryUniqueAndUsesStableCleanupEffectId()
        {
            AssertCopies(
                new[] { "building_007", "building_008", "building_009" },
                facility =>
                {
                    Assert.That(facility.HasEntryEffect, Is.True);
                    Assert.That(facility.Unique, Is.True);
                    Assert.That(facility.Keywords, Does.Contain(FacilityCardKeywords.Entry));
                    Assert.That(facility.Keywords, Does.Contain(FacilityCardKeywords.Unique));
                    Assert.That(
                        facility.EffectId,
                        Is.EqualTo(FacilityCardEffectIds.ClaimStartMarkerAtCleanup));
                });
        }

        [Test]
        public void MercenaryCommand_OffersReplaceOrDeployInfluenceAndIsNotEnterpriseMetadata()
        {
            AssertCopies(
                new[] { "building_034", "building_035", "building_036" },
                facility =>
                {
                    Assert.That(facility.HasEntryEffect, Is.True);
                    Assert.That(facility.EffectId, Is.EqualTo(FacilityCardEffectIds.ReplaceOneInfluence));
                    Assert.That(facility.EffectType, Is.Not.EqualTo("enterprise"));
                    Assert.That(facility.EffectText,
                        Does.Contain("替换 1 个影响力或放置 1 个影响力"));
                    Assert.That(facility.EffectText, Does.Not.Contain("企业"));
                });
        }

        [Test]
        public void SimpleEngineeringCamp_UsesReviewedAdditionalBuildEffectText()
        {
            AssertCopies(
                new[] { "building_010", "building_011" },
                facility =>
                {
                    Assert.That(facility.HasEntryEffect, Is.True);
                    Assert.That(facility.EffectId, Is.EqualTo(FacilityCardEffectIds.BuildAdditionalFacility));
                    Assert.That(facility.EffectText, Does.Contain("执行 1 次建设"));
                    Assert.That(facility.EffectText, Does.Not.Contain("待按正式规则术语校准"));
                });
        }

        [Test]
        public void RedInfluenceFacilities_DoNotUseEnterpriseEffectMetadata()
        {
            AssertCopies(
                new[]
                {
                    "building_034", "building_035", "building_036",
                    "building_037", "building_038",
                    "building_039", "building_040", "building_041"
                },
                facility =>
                {
                    Assert.That(facility.HasEntryEffect, Is.True);
                    Assert.That(facility.EffectType, Is.Not.EqualTo("enterprise"));
                    Assert.That(facility.EffectText, Does.Not.Contain("企业"));
                });
        }

        private static void AssertCopies(
            string[] facilityIds,
            System.Action<FacilityCardDefinition> assertion)
        {
            foreach (var facilityId in facilityIds)
            {
                var facility = FacilityCardDatabase.Get(facilityId);
                Assert.That(facility, Is.Not.Null, facilityId);
                assertion(facility);
            }
        }

        private sealed class ExpectedCard
        {
            public ExpectedCard(int score, string color)
            {
                Score = score;
                Color = color;
            }

            public int Score { get; private set; }
            public string Color { get; private set; }
        }
    }
}
