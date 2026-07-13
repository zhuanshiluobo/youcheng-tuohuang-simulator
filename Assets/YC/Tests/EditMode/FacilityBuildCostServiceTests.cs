using NUnit.Framework;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class FacilityBuildCostServiceTests
    {
        [Test]
        public void CityIndustrialDistrict_DiscountsOncePerBuiltBaseColor()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.BuiltFacilityIds.Add("building_014"); // blue
            player.BuiltFacilityIds.Add("building_018"); // blue again
            player.BuiltFacilityIds.Add("building_028"); // yellow
            player.BuiltFacilityIds.Add("building_032"); // red

            var cost = new FacilityBuildCostService().GetEffectiveResourceCost(
                state,
                player,
                FacilityCardDatabase.Get("building_022"));

            Assert.That(cost.Originium, Is.EqualTo(1));
            Assert.That(cost.OriginiumShard, Is.EqualTo(3));
            Assert.That(cost.GoldVoucher, Is.EqualTo(2));
        }

        [Test]
        public void CityIndustrialDistrict_RainbowFacilitiesDoNotDiscount()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.BuiltFacilityIds.Add(FacilityCardDatabase.CoreCommandTower);
            player.BuiltFacilityIds.Add("building_001");

            var cost = new FacilityBuildCostService().GetEffectiveResourceCost(
                state,
                player,
                FacilityCardDatabase.Get("building_022"));

            Assert.That(cost.Originium, Is.EqualTo(4));
        }

        [Test]
        public void Build_WithResourcePayment_UsesDiscountedCost()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.BuiltFacilityIds.Add("building_014");
            player.BuiltFacilityIds.Add("building_028");
            player.BuiltFacilityIds.Add("building_032");
            player.Resources = new ResourceSet
            {
                Originium = 1,
                OriginiumShard = 3,
                GoldVoucher = 2
            };
            state.Decks.FacilitySupply.Add("building_022");

            var result = new BuildFacilityService().Build(
                state,
                1,
                "building_022",
                0,
                BuildFacilityService.PaymentModeResources);

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.Resources.Originium, Is.Zero);
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
        }

        [Test]
        public void Build_WithGoldPayment_DoesNotDiscountGoldCost()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.BuiltFacilityIds.Add("building_014");
            player.BuiltFacilityIds.Add("building_028");
            player.BuiltFacilityIds.Add("building_032");
            player.Resources.GoldVoucher = 24;
            state.Decks.FacilitySupply.Add("building_022");

            var result = new BuildFacilityService().Build(
                state,
                1,
                "building_022",
                0,
                BuildFacilityService.PaymentModeGold);

            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
        }

        private static GameState CreateState()
        {
            var state = new GameState();
            state.Players.Add(new PlayerState { PlayerId = 1, Name = "P1" });
            return state;
        }
    }
}
