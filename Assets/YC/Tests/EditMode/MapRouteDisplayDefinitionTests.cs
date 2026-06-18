using System.Linq;
using NUnit.Framework;
using YC.Domain.Maps;
using YC.Presentation.Maps;

namespace YC.Tests.EditMode
{
    public sealed class MapRouteDisplayDefinitionTests
    {
        [Test]
        public void FourPlayerRouteDisplayDefinitions_AlignWithRuleRouteIds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var definitions = FourPlayerRouteDisplayDefinitions.Create();

            Assert.That(definitions.Select(definition => definition.RouteId),
                Is.EquivalentTo(map.Routes.Select(route => route.RouteId)));
        }

        [Test]
        public void FourPlayerRouteDisplayDefinitions_KeepInfluenceSlotCountsConsistent()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var definitions = FourPlayerRouteDisplayDefinitions.Create();

            var errors = MapRouteDisplayDefinitionValidator.Validate(map, definitions);

            Assert.That(errors, Is.Empty);
        }
    }
}
