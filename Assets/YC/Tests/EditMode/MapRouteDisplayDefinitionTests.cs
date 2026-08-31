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
            var layout = MapDisplayLayoutCatalog.Load(StaticMapDefinitions.FourPlayerMapId);
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.TryValidateScoreTrack(out var reason), Is.True, reason);

            var minusTwo = layout.GetScoreTrackNormalizedPosition(-2);
            var zero = layout.GetScoreTrackNormalizedPosition(0);
            var twentyThree = layout.GetScoreTrackNormalizedPosition(23);
            var twentyFour = layout.GetScoreTrackNormalizedPosition(24);
            var fifty = layout.GetScoreTrackNormalizedPosition(50);

            Assert.That(minusTwo.x, Is.EqualTo(0.1013f).Within(0.0001f));
            Assert.That(zero.x, Is.GreaterThan(minusTwo.x));
            Assert.That(twentyThree.y, Is.EqualTo(minusTwo.y).Within(0.0001f));
            Assert.That(twentyFour.x, Is.EqualTo(twentyThree.x).Within(0.0001f));
            Assert.That(twentyFour.y, Is.LessThan(twentyThree.y));
            Assert.That(fifty.y, Is.EqualTo(0.02535f).Within(0.0001f));
            Assert.That(layout.GetScoreTrackNormalizedPosition(-99), Is.EqualTo(minusTwo));
            Assert.That(layout.GetScoreTrackNormalizedPosition(99), Is.EqualTo(fifty));
        }

        [Test]
        public void FourPlayerRouteLayout_AlignsWithRuleRouteIds()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var layout = MapDisplayLayoutCatalog.Load(map.MapId);
            var definitions = layout.CreateRouteDefinitions();

            Assert.That(definitions.Select(definition => definition.RouteId),
                Is.EqualTo(map.Routes.Select(route => route.RouteId)));
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
                Is.EqualTo(map.Locations.Select(location => location.LocationId)));
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
        public void FourPlayerArtworkAlignment_LocksAllCityResourceAndInfluenceCenters()
        {
            var layout = MapDisplayLayoutCatalog.Load(StaticMapDefinitions.FourPlayerMapId);
            var locationDefinitions = layout.CreateResourcePointDefinitions();
            var routeDefinitions = layout.CreateRouteDefinitions();

            Assert.That(layout.Locations, Has.Count.EqualTo(22));
            Assert.That(locationDefinitions, Has.Count.EqualTo(22));
            Assert.That(routeDefinitions, Has.Count.EqualTo(22));
            Assert.That(locationDefinitions.Sum(definition => definition.InfluenceSlots.Count), Is.EqualTo(44));
            Assert.That(routeDefinitions.Sum(definition => definition.InfluenceSlots.Count), Is.EqualTo(33));

            AssertLocationAlignment(layout, "A-01", new Vector2(0.8413f, 0.2446f), new Vector2(0.7921f, 0.2139f), new[] { new Vector2(0.7995f, 0.2501f), new Vector2(0.7995f, 0.2705f) });
            AssertLocationAlignment(layout, "A-02", new Vector2(0.8351f, 0.4454f), new Vector2(0.786f, 0.4147f), new[] { new Vector2(0.7933f, 0.4509f), new Vector2(0.7933f, 0.4713f) });
            AssertLocationAlignment(layout, "A-03", new Vector2(0.6943f, 0.4748f), new Vector2(0.6452f, 0.4441f), new[] { new Vector2(0.6525f, 0.4803f), new Vector2(0.6525f, 0.5007f) });
            AssertLocationAlignment(layout, "B-01", new Vector2(0.9087f, 0.6442f), new Vector2(0.8596f, 0.6135f), new[] { new Vector2(0.8669f, 0.6497f), new Vector2(0.8669f, 0.6701f) });
            AssertLocationAlignment(layout, "B-02", new Vector2(0.8047f, 0.7432f), new Vector2(0.7556f, 0.7125f), new[] { new Vector2(0.7629f, 0.7487f), new Vector2(0.7629f, 0.7691f) });
            AssertLocationAlignment(layout, "B-03", new Vector2(0.6577f, 0.6776f), new Vector2(0.6086f, 0.6469f), new[] { new Vector2(0.6159f, 0.6831f), new Vector2(0.6159f, 0.7035f) });
            AssertLocationAlignment(layout, "C-01", new Vector2(0.8054f, 0.8974f), new Vector2(0.7562f, 0.8667f), new[] { new Vector2(0.7635f, 0.9029f), new Vector2(0.7635f, 0.9233f) });
            AssertLocationAlignment(layout, "C-02", new Vector2(0.4463f, 0.8252f), new Vector2(0.3972f, 0.7945f), new[] { new Vector2(0.4045f, 0.8307f), new Vector2(0.4045f, 0.8511f) });
            AssertLocationAlignment(layout, "C-03", new Vector2(0.1381f, 0.7841f), new Vector2(0.089f, 0.7534f), new[] { new Vector2(0.0963f, 0.7895f), new Vector2(0.0963f, 0.8099f) });
            AssertLocationAlignment(layout, "D-01", new Vector2(0.6943f, 0.3444f), new Vector2(0.6452f, 0.3137f), new[] { new Vector2(0.6525f, 0.3499f), new Vector2(0.6525f, 0.3703f) });
            AssertLocationAlignment(layout, "D-02", new Vector2(0.5525f, 0.297f), new Vector2(0.5034f, 0.2663f), new[] { new Vector2(0.5107f, 0.3025f), new Vector2(0.5107f, 0.3229f) });
            AssertLocationAlignment(layout, "D-03", new Vector2(0.5697f, 0.4888f), new Vector2(0.5206f, 0.4581f), new[] { new Vector2(0.5279f, 0.4943f), new Vector2(0.5279f, 0.5147f) });
            AssertLocationAlignment(layout, "E-01", new Vector2(0.5567f, 0.744f), new Vector2(0.5076f, 0.7132f), new[] { new Vector2(0.5149f, 0.7495f), new Vector2(0.5149f, 0.7699f) });
            AssertLocationAlignment(layout, "E-02", new Vector2(0.3389f, 0.683f), new Vector2(0.2898f, 0.6524f), new[] { new Vector2(0.2971f, 0.6885f), new Vector2(0.2971f, 0.7089f) });
            AssertLocationAlignment(layout, "E-03", new Vector2(0.1959f, 0.5922f), new Vector2(0.1468f, 0.5616f), new[] { new Vector2(0.1541f, 0.5977f), new Vector2(0.1541f, 0.6181f) });
            AssertLocationAlignment(layout, "F-01", new Vector2(0.3313f, 0.3422f), new Vector2(0.2822f, 0.3115f), new[] { new Vector2(0.2895f, 0.3477f), new Vector2(0.2895f, 0.3681f) });
            AssertLocationAlignment(layout, "F-02", new Vector2(0.3913f, 0.5248f), new Vector2(0.3422f, 0.4941f), new[] { new Vector2(0.3495f, 0.5303f), new Vector2(0.3495f, 0.5507f) });
            AssertLocationAlignment(layout, "F-03", new Vector2(0.1799f, 0.3992f), new Vector2(0.1308f, 0.3685f), new[] { new Vector2(0.1381f, 0.4047f), new Vector2(0.1381f, 0.4251f) });
            AssertLocationAlignment(layout, "G-01", new Vector2(0.7339f, 0.143f), new Vector2(0.6848f, 0.1123f), new[] { new Vector2(0.6921f, 0.1485f), new Vector2(0.6921f, 0.1689f) });
            AssertLocationAlignment(layout, "G-02", new Vector2(0.4859f, 0.1644f), new Vector2(0.4367f, 0.1337f), new[] { new Vector2(0.4441f, 0.1699f), new Vector2(0.4441f, 0.1903f) });
            AssertLocationAlignment(layout, "G-03", new Vector2(0.1457f, 0.119f), new Vector2(0.0966f, 0.0884f), new[] { new Vector2(0.1039f, 0.1245f), new Vector2(0.1039f, 0.1449f) });
            AssertLocationAlignment(layout, "G-04", new Vector2(0.3167f, 0.2292f), new Vector2(0.2676f, 0.1985f), new[] { new Vector2(0.2749f, 0.2347f), new Vector2(0.2749f, 0.2551f) });

            AssertRouteAlignment(layout, "A1", new[] { new Vector2(0.8173f, 0.3559f) });
            AssertRouteAlignment(layout, "A2", new[] { new Vector2(0.7007f, 0.5887f), new Vector2(0.7187f, 0.5887f) });
            AssertRouteAlignment(layout, "B1", new[] { new Vector2(0.7765f, 0.6293f) });
            AssertRouteAlignment(layout, "B2", new[] { new Vector2(0.8873f, 0.8353f), new Vector2(0.9053f, 0.8353f) });
            AssertRouteAlignment(layout, "C1", new[] { new Vector2(0.3035f, 0.8215f) });
            AssertRouteAlignment(layout, "C2", new[] { new Vector2(0.6633f, 0.8247f), new Vector2(0.6813f, 0.8247f) });
            AssertRouteAlignment(layout, "D1", new[] { new Vector2(0.5793f, 0.3889f), new Vector2(0.5973f, 0.3889f) });
            AssertRouteAlignment(layout, "D2", new[] { new Vector2(0.4557f, 0.4347f), new Vector2(0.4737f, 0.4347f) });
            AssertRouteAlignment(layout, "E1", new[] { new Vector2(0.4543f, 0.6679f), new Vector2(0.4723f, 0.6679f) });
            AssertRouteAlignment(layout, "E2", new[] { new Vector2(0.1963f, 0.6925f), new Vector2(0.2143f, 0.6925f) });
            AssertRouteAlignment(layout, "F1", new[] { new Vector2(0.4395f, 0.3031f) });
            AssertRouteAlignment(layout, "F2", new[] { new Vector2(0.3241f, 0.4405f) });
            AssertRouteAlignment(layout, "F3", new[] { new Vector2(0.1851f, 0.3129f), new Vector2(0.2031f, 0.3129f) });
            AssertRouteAlignment(layout, "G1", new[] { new Vector2(0.3209f, 0.1009f) });
            AssertRouteAlignment(layout, "G2", new[] { new Vector2(0.1815f, 0.2457f) });
            AssertRouteAlignment(layout, "R1", new[] { new Vector2(0.6849f, 0.2427f), new Vector2(0.7029f, 0.2427f) });
            AssertRouteAlignment(layout, "R2", new[] { new Vector2(0.9139f, 0.3555f), new Vector2(0.9319f, 0.3555f) });
            AssertRouteAlignment(layout, "R3", new[] { new Vector2(0.0909f, 0.4453f) });
            AssertRouteAlignment(layout, "R4", new[] { new Vector2(0.1807f, 0.4979f) });
            AssertRouteAlignment(layout, "R5", new[] { new Vector2(0.2869f, 0.5413f) });
            AssertRouteAlignment(layout, "R6", new[] { new Vector2(0.8725f, 0.5467f) });
            AssertRouteAlignment(layout, "R7", new[] { new Vector2(0.5675f, 0.5975f), new Vector2(0.5855f, 0.5975f) });
        }

        private static void AssertLocationAlignment(
            MapDisplayLayout layout,
            string locationId,
            Vector2 expectedCity,
            Vector2 expectedResource,
            IReadOnlyList<Vector2> expectedSlots)
        {
            var source = layout.Locations.Single(item => item.LocationId == locationId);
            var display = layout.CreateResourcePointDefinitions()
                .Single(item => item.LocationId == locationId);
            AssertArtworkPoint(source.NormalizedPosition, expectedCity, locationId + " city");
            AssertArtworkPoint(display.ResourceTokenPosition, expectedResource, locationId + " resource");
            Assert.That(display.InfluenceSlots, Has.Count.EqualTo(expectedSlots.Count), locationId);
            for (var i = 0; i < expectedSlots.Count; i++)
                AssertArtworkPoint(display.InfluenceSlots[i].NormalizedPosition, expectedSlots[i], locationId + " slot " + i);
        }

        private static void AssertRouteAlignment(
            MapDisplayLayout layout,
            string routeId,
            IReadOnlyList<Vector2> expectedSlots)
        {
            var display = layout.CreateRouteDefinitions().Single(item => item.RouteId == routeId);
            Assert.That(display.InfluenceSlots, Has.Count.EqualTo(expectedSlots.Count), routeId);
            for (var i = 0; i < expectedSlots.Count; i++)
                AssertArtworkPoint(display.InfluenceSlots[i].NormalizedPosition, expectedSlots[i], routeId + " slot " + i);
        }

        private static void AssertArtworkPoint(Vector2 actual, Vector2 expected, string label)
        {
            Assert.That(Vector2.Distance(actual, expected), Is.LessThanOrEqualTo(0.001f), label);
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
