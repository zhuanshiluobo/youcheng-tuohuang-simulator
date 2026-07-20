using NUnit.Framework;
using YC.Domain.Economy;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ResourceSaleServiceTests
    {
        [Test]
        public void Sell_AllResourceTypes_UsesSharedUnitPrices()
        {
            var resources = new ResourceSet
            {
                Originium = 1,
                OriginiumShard = 1,
                Iron = 1,
                PureOriginium = 1,
                GoldVoucher = 7
            };

            var result = new ResourceSaleService().Sell(
                resources,
                new ResourceSaleRequest(1, 1, 1, 1));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Revenue, Is.EqualTo(25));
            Assert.That(resources.Originium, Is.Zero);
            Assert.That(resources.OriginiumShard, Is.Zero);
            Assert.That(resources.Iron, Is.Zero);
            Assert.That(resources.PureOriginium, Is.Zero);
            Assert.That(resources.GoldVoucher, Is.EqualTo(32));
        }

        [TestCase(-1, 0, ResourceSaleFailureKind.InvalidAmount)]
        [TestCase(1, 2, ResourceSaleFailureKind.InsufficientResource)]
        public void Sell_InvalidRequest_FailsAtomically(
            int originium,
            int pureOriginium,
            ResourceSaleFailureKind expectedFailure)
        {
            var resources = new ResourceSet
            {
                Originium = 1,
                OriginiumShard = 2,
                Iron = 3,
                PureOriginium = 1,
                GoldVoucher = 7
            };
            var before = resources.Clone();

            var result = new ResourceSaleService().Sell(
                resources,
                new ResourceSaleRequest(originium, 1, 1, pureOriginium));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailureKind, Is.EqualTo(expectedFailure));
            Assert.That(resources.Originium, Is.EqualTo(before.Originium));
            Assert.That(resources.OriginiumShard, Is.EqualTo(before.OriginiumShard));
            Assert.That(resources.Iron, Is.EqualTo(before.Iron));
            Assert.That(resources.PureOriginium, Is.EqualTo(before.PureOriginium));
            Assert.That(resources.GoldVoucher, Is.EqualTo(before.GoldVoucher));
        }
    }
}
