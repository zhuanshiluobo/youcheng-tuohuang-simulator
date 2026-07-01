using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Maps;
using YC.Domain.Rules;
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

        [Test]
        public void FourPlayerResourcePointDisplayDefinitions_CoverAllRuleLocationIds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var definitions = FourPlayerResourcePointDisplayDefinitions.Create();

            Assert.That(definitions.Select(definition => definition.LocationId),
                Is.EquivalentTo(map.Locations.Select(location => location.LocationId)));
            Assert.That(definitions.Select(definition => definition.LocationId).Distinct().Count(),
                Is.EqualTo(map.Locations.Count));
            Assert.That(definitions, Has.All.Matches<MapResourcePointDisplayDefinition>(
                definition => definition.InfluenceSlots != null &&
                              definition.InfluenceSlots.Count == 2));
        }

        [Test]
        public void FourPlayerResourcePointDisplayDefinitions_StayWithinNormalizedMapBounds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var definitions = FourPlayerResourcePointDisplayDefinitions.Create();
            var errors = MapResourcePointDisplayDefinitionValidator.Validate(map, definitions);

            Assert.That(errors, Is.Empty);

            foreach (var definition in definitions)
            {
                Assert.That(definition.MapId, Is.EqualTo(StaticMapDefinitions.FourPlayerMapId));
                Assert.That(definition.ResourceTokenPosition.x, Is.InRange(0f, 1f), definition.LocationId);
                Assert.That(definition.ResourceTokenPosition.y, Is.InRange(0f, 1f), definition.LocationId);

                for (var i = 0; i < definition.InfluenceSlots.Count; i++)
                {
                    var slot = definition.InfluenceSlots[i];
                    Assert.That(slot.NormalizedPosition.x, Is.InRange(0f, 1f), definition.LocationId + ":" + i);
                    Assert.That(slot.NormalizedPosition.y, Is.InRange(0f, 1f), definition.LocationId + ":" + i);
                    Assert.That(slot.Size, Is.GreaterThan(0f), definition.LocationId + ":" + i);
                    Assert.That(slot.ColliderRadius, Is.GreaterThan(0f), definition.LocationId + ":" + i);
                }
            }
        }

        [Test]
        public void ExplorationRepresentativeResourceIcons_CoverEventCardResources()
        {
            foreach (var cardId in EventCardDatabase.GreenCardIds
                         .Concat(EventCardDatabase.YellowCardIds)
                         .Concat(EventCardDatabase.RedCardIds))
            {
                var card = EventCardDatabase.Get(cardId);
                Assert.That(card, Is.Not.Null, cardId);
                Assert.That(ResourceTokenIconDefinitions.Supports(card.RepresentativeResourceType), Is.True, cardId);
            }
        }

        [Test]
        public void ExplorationResourceIconFileNames_MatchRuntimeMappings()
        {
            var expectedFileNames = new Dictionary<ResourceType, string>
            {
                { ResourceType.Originium, "源岩.png" },
                { ResourceType.OriginiumShard, "源石碎片.png" },
                { ResourceType.Iron, "异铁.png" },
                { ResourceType.PureOriginium, "至纯源石.png" }
            };

            var actualFileNames = ResourceTokenIconDefinitions.GetRequiredIcons()
                .ToDictionary(icon => icon.ResourceType, icon => icon.FileName);

            Assert.That(actualFileNames, Has.Count.EqualTo(expectedFileNames.Count));

            foreach (var pair in expectedFileNames)
            {
                Assert.That(actualFileNames.ContainsKey(pair.Key), Is.True, pair.Key.ToString());
                Assert.That(actualFileNames[pair.Key], Is.EqualTo(pair.Value), pair.Key.ToString());
                Assert.That(ResourceTokenIconDefinitions.GetFileName(pair.Key), Is.EqualTo(pair.Value));
                Assert.That(ResourceTokenIconDefinitions.GetFileName(pair.Key, 1), Is.EqualTo(pair.Value));
            }

            Assert.That(ResourceTokenIconDefinitions.GetFileName(ResourceType.GoldVoucher), Is.Empty);
            Assert.That(ResourceTokenIconDefinitions.GetFileName(ResourceType.Originium, 2), Is.EqualTo("源岩x2.png"));
        }

        [Test]
        public void ExplorationResourceIcons_ExistInImportedAssetsAndStreamingAssets()
        {
            var iconDirectory = Path.Combine(UnityEngine.Application.dataPath, "YC", "Data", ResourceTokenIconDefinitions.DirectoryName);
            var streamingIconDirectory = Path.Combine(
                UnityEngine.Application.streamingAssetsPath,
                ResourceTokenIconDefinitions.DirectoryName);

            foreach (var icon in ResourceTokenIconDefinitions.GetRequiredIcons())
            {
                Assert.That(File.Exists(Path.Combine(iconDirectory, icon.FileName)), Is.True, icon.FileName);
                Assert.That(File.Exists(Path.Combine(streamingIconDirectory, icon.FileName)), Is.True, icon.FileName);
            }

            foreach (var icon in ResourceTokenIconDefinitions.GetAmountSpecificIcons())
            {
                Assert.That(File.Exists(Path.Combine(iconDirectory, icon.FileName)), Is.True, icon.FileName);
                Assert.That(File.Exists(Path.Combine(streamingIconDirectory, icon.FileName)), Is.True, icon.FileName);
            }

            Assert.That(Directory.Exists(Path.Combine(UnityEngine.Application.dataPath, "Resources", "ResourceIcons")),
                Is.False);
        }
    }
}
