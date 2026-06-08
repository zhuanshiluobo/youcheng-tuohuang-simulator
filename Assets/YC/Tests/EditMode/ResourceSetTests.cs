using NUnit.Framework;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class ResourceSetTests
    {
        [Test]
        public void TryPay_WhenResourcesAreEnough_SubtractsCost()
        {
            var resources = new ResourceSet
            {
                PureOriginium = 3,
                OriginiumShard = 2,
                Iron = 1,
                GoldVoucher = 4
            };

            var paid = resources.TryPay(new ResourceSet
            {
                PureOriginium = 1,
                OriginiumShard = 2,
                Iron = 0,
                GoldVoucher = 3
            });

            Assert.That(paid, Is.True);
            Assert.That(resources.Get(ResourceType.PureOriginium), Is.EqualTo(2));
            Assert.That(resources.Get(ResourceType.OriginiumShard), Is.EqualTo(0));
            Assert.That(resources.Get(ResourceType.Iron), Is.EqualTo(1));
            Assert.That(resources.Get(ResourceType.GoldVoucher), Is.EqualTo(1));
        }

        [Test]
        public void TryPay_WhenResourcesAreInsufficient_DoesNotMutate()
        {
            var resources = new ResourceSet
            {
                PureOriginium = 0,
                OriginiumShard = 1,
                Iron = 1,
                GoldVoucher = 1
            };

            var paid = resources.TryPay(new ResourceSet
            {
                PureOriginium = 1
            });

            Assert.That(paid, Is.False);
            Assert.That(resources.Get(ResourceType.PureOriginium), Is.EqualTo(0));
            Assert.That(resources.Get(ResourceType.OriginiumShard), Is.EqualTo(1));
            Assert.That(resources.Get(ResourceType.Iron), Is.EqualTo(1));
            Assert.That(resources.Get(ResourceType.GoldVoucher), Is.EqualTo(1));
        }
    }
}
