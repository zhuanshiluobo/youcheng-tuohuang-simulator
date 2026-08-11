using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Presentation.Maps;

namespace YC.Tests.EditMode
{
    public sealed class MapRouteDisplayDefinitionTests
    {
        [Test]
        public void ScoreTrackPositions_FollowBottomThenRightEdgeAndClampToPrintedTrack()
        {
            var minusTwo = FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(-2);
            var zero = FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(0);
            var twentyThree = FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(23);
            var twentyFour = FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(24);
            var fifty = FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(50);

            Assert.That(minusTwo.x, Is.EqualTo(0.1013f).Within(0.0001f));
            Assert.That(zero.x, Is.GreaterThan(minusTwo.x));
            Assert.That(twentyThree.y, Is.EqualTo(minusTwo.y).Within(0.0001f));
            Assert.That(twentyFour.x, Is.EqualTo(twentyThree.x).Within(0.0001f));
            Assert.That(twentyFour.y, Is.LessThan(twentyThree.y));
            Assert.That(fifty.y, Is.EqualTo(0.02535f).Within(0.0001f));
            Assert.That(FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(-99), Is.EqualTo(minusTwo));
            Assert.That(FourPlayerScoreTrackDisplayDefinition.GetNormalizedPosition(99), Is.EqualTo(fifty));
        }

        [Test]
        public void FourPlayerRouteLayout_AlignsWithRuleRouteIds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var definitions = layout.CreateRouteDefinitions();

            Assert.That(definitions.Select(definition => definition.RouteId),
                Is.EquivalentTo(map.Routes.Select(route => route.RouteId)));
        }

        [Test]
        public void FourPlayerRouteLayout_KeepsInfluenceSlotCountsConsistent()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var definitions = layout.CreateRouteDefinitions();

            var errors = MapRouteDisplayDefinitionValidator.Validate(map, definitions);

            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void FourPlayerMapDisplayLayout_IsBoundToMapSpriteAndValid()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);

            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.MapSprite, Is.Not.Null);
            Assert.That(layout.MapId, Is.EqualTo(map.MapId));
            Assert.That(MapDisplayLayoutValidator.Validate(map, layout), Is.Empty);
        }

        [Test]
        public void FourPlayerLocationChildren_StayRelativeToTheirLocationAnchor()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var location = layout.Locations.Single(definition => definition.LocationId == "A-01");
            var display = layout.CreateResourcePointDefinitions()
                .Single(definition => definition.LocationId == location.LocationId);

            Assert.That(Vector2.Distance(
                    display.ResourceTokenPosition - location.NormalizedPosition,
                    location.ResourceTokenOffset),
                Is.LessThan(0.00001f));
            Assert.That(Vector2.Distance(
                    display.InfluenceSlots[0].NormalizedPosition - location.NormalizedPosition,
                    location.InfluenceSlots[0].Offset),
                Is.LessThan(0.00001f));
            Assert.That(Vector2.Distance(
                    display.InfluenceSlots[1].NormalizedPosition - location.NormalizedPosition,
                    location.InfluenceSlots[1].Offset),
                Is.LessThan(0.00001f));
        }

        [Test]
        public void MapCoordinateSpace_RoundTripsAfterMapTransformChanges()
        {
            var mapObject = new GameObject("Map coordinate test", typeof(SpriteRenderer));
            var texture = new Texture2D(200, 100);
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            var layout = ScriptableObject.CreateInstance<MapDisplayLayout>();
            layout.MapId = "test-map";
            layout.MapSprite = sprite;

            try
            {
                mapObject.transform.position = new Vector3(13f, -7f, 2f);
                mapObject.transform.rotation = Quaternion.Euler(0f, 0f, 31f);
                mapObject.transform.localScale = new Vector3(1.75f, 0.8f, 1f);

                var renderer = mapObject.GetComponent<SpriteRenderer>();
                renderer.sprite = sprite;
                var coordinateSpace = mapObject.AddComponent<MapCoordinateSpace>();
                var contentRoot = new GameObject("Map Content").transform;
                contentRoot.SetParent(renderer.transform, false);
                coordinateSpace.Configure(renderer, layout, contentRoot);

                var normalized = new Vector2(0.23f, 0.71f);
                var world = coordinateSpace.ToWorldPosition(normalized, -0.2f);

                Assert.That(coordinateSpace.ToNormalizedPosition(world).x,
                    Is.EqualTo(normalized.x).Within(0.0001f));
                Assert.That(coordinateSpace.ToNormalizedPosition(world).y,
                    Is.EqualTo(normalized.y).Within(0.0001f));
                Assert.That(coordinateSpace.ContentRoot.parent, Is.EqualTo(renderer.transform));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mapObject);
                UnityEngine.Object.DestroyImmediate(layout);
                UnityEngine.Object.DestroyImmediate(sprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FourPlayerResourcePointLayout_CoversAllRuleLocationIds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var definitions = layout.CreateResourcePointDefinitions();

            Assert.That(definitions.Select(definition => definition.LocationId),
                Is.EquivalentTo(map.Locations.Select(location => location.LocationId)));
            Assert.That(definitions.Select(definition => definition.LocationId).Distinct().Count(),
                Is.EqualTo(map.Locations.Count));
            Assert.That(definitions, Has.All.Matches<MapResourcePointDisplayDefinition>(
                definition => definition.InfluenceSlots != null &&
                              definition.InfluenceSlots.Count == 2));
        }

        [Test]
        public void FourPlayerResourcePointLayout_StaysWithinNormalizedMapBounds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var definitions = layout.CreateResourcePointDefinitions();
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
