using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using YC.Domain.CityStyles;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class PresentationReuseArchitectureTests
    {
        [Test]
        public void EffectDialogs_ShareShellAndResourceAllocationModel()
        {
            var characterDialog = Type.GetType(
                "YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp",
                false);
            var facilityDialog = Type.GetType(
                "YC.Presentation.FacilityEffectChoiceDialog, Assembly-CSharp",
                false);
            var shell = Type.GetType("YC.Presentation.EffectDialogShell, Assembly-CSharp", false);
            var allocation = Type.GetType(
                "YC.Presentation.ResourceAllocationSpec, Assembly-CSharp",
                false);

            Assert.That(characterDialog, Is.Not.Null);
            Assert.That(facilityDialog, Is.Not.Null);
            Assert.That(shell, Is.Not.Null);
            Assert.That(allocation, Is.Not.Null);
            Assert.That(
                characterDialog.GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType,
                Is.EqualTo(shell));
            Assert.That(
                facilityDialog.GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType,
                Is.EqualTo(shell));
            Assert.That(allocation.GetField("UnitPrices"), Is.Not.Null);
            Assert.That(allocation.GetField("FormatSummary"), Is.Not.Null);
        }

        [Test]
        public void CityBoardSlotLayout_ProvidesSharedTwelveSlotGeometry()
        {
            var type = Type.GetType("YC.Presentation.CityBoardSlotLayout, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);
            Assert.That(
                (int)type.GetField("SlotCount", BindingFlags.Static | BindingFlags.Public)
                    .GetRawConstantValue(),
                Is.EqualTo(12));

            var owner = new GameObject("Shared City Board Slot Layout Test", typeof(RectTransform));
            try
            {
                var rect = owner.GetComponent<RectTransform>();
                type.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public)
                    .Invoke(null, new object[] { rect, 0 });
                Assert.That((rect.anchorMin.x + rect.anchorMax.x) * 0.5f, Is.EqualTo(0.176f).Within(0.0001f));
                Assert.That((rect.anchorMin.y + rect.anchorMax.y) * 0.5f, Is.EqualTo(0.848f).Within(0.0001f));
                Assert.That(rect.anchorMax.x - rect.anchorMin.x, Is.EqualTo(0.292f).Within(0.0001f));
                Assert.That(rect.anchorMax.y - rect.anchorMin.y, Is.EqualTo(0.224f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void CityStyleMarkerRenderer_UsesSharedMarkerAreaAnchors()
        {
            var type = Type.GetType(
                "YC.Presentation.CityStyleMarkerRenderer, Assembly-CSharp",
                false);
            Assert.That(type, Is.Not.Null);
            var resolve = type.GetMethod("ResolveAnchor", BindingFlags.Static | BindingFlags.Public);
            Assert.That(resolve, Is.Not.Null);

            var unused = (Vector2)resolve.Invoke(
                null,
                new object[] { "style.test", CityStyleMarkerAreas.Unused, 0, 0, 0 });
            var used = (Vector2)resolve.Invoke(
                null,
                new object[] { "style.test", CityStyleMarkerAreas.Used, 0, 0, 0 });
            var secondInArea = (Vector2)resolve.Invoke(
                null,
                new object[] { "style.test", CityStyleMarkerAreas.Used, 1, 0, 1 });
            Assert.That(unused.y, Is.GreaterThan(used.y));
            Assert.That(secondInArea.x - used.x, Is.EqualTo(0.055f).Within(0.0001f));
        }

        [Test]
        public void MilitaryIndustrialMarkers_UseFourVerticalPlayerLanesInsideUnusedArea()
        {
            var type = Type.GetType(
                "YC.Presentation.CityStyleMarkerRenderer, Assembly-CSharp",
                false);
            Assert.That(type, Is.Not.Null);
            var resolve = type.GetMethod("ResolveAnchor", BindingFlags.Static | BindingFlags.Public);
            var resolveLane = type.GetMethod(
                "ResolvePlayerLaneIndex",
                BindingFlags.Static | BindingFlags.Public);
            Assert.That(resolve, Is.Not.Null);
            Assert.That(resolveLane, Is.Not.Null);

            const float unusedMinimumX = 0.557f;
            const float unusedMaximumX = 0.941f;
            const float unusedMinimumY = 0.49f;
            const float unusedMaximumY = 0.94f;
            const float compactMarkerHalfWidth = 6f / 143f;
            const float compactMarkerHalfHeight = 6f / 91f;

            var firstPlayerAnchors = new Vector2[3];
            for (var playerId = 1; playerId <= 4; playerId++)
            {
                var laneIndex = (int)resolveLane.Invoke(null, new object[] { playerId });
                Assert.That(laneIndex, Is.EqualTo(playerId - 1));
                for (var playerMarkerIndex = 0; playerMarkerIndex < 3; playerMarkerIndex++)
                {
                    var anchor = (Vector2)resolve.Invoke(
                        null,
                        new object[]
                        {
                            CityStyleDatabase.MilitaryIndustrialArea,
                            CityStyleMarkerAreas.Unused,
                            (playerId - 1) * 3 + playerMarkerIndex,
                            laneIndex,
                            playerMarkerIndex
                        });
                    Assert.That(anchor.x - compactMarkerHalfWidth, Is.GreaterThanOrEqualTo(unusedMinimumX));
                    Assert.That(anchor.x + compactMarkerHalfWidth, Is.LessThanOrEqualTo(unusedMaximumX));
                    Assert.That(anchor.y - compactMarkerHalfHeight, Is.GreaterThanOrEqualTo(unusedMinimumY));
                    Assert.That(anchor.y + compactMarkerHalfHeight, Is.LessThanOrEqualTo(unusedMaximumY));

                    if (playerId == 1)
                    {
                        firstPlayerAnchors[playerMarkerIndex] = anchor;
                    }
                    else
                    {
                        Assert.That(
                            anchor.x - firstPlayerAnchors[playerMarkerIndex].x,
                            Is.EqualTo(0.09f * (playerId - 1)).Within(0.0001f));
                        Assert.That(anchor.y, Is.EqualTo(firstPlayerAnchors[playerMarkerIndex].y).Within(0.0001f));
                    }
                }
            }

            Assert.That(firstPlayerAnchors[1].x, Is.EqualTo(firstPlayerAnchors[0].x).Within(0.0001f));
            Assert.That(firstPlayerAnchors[2].x, Is.EqualTo(firstPlayerAnchors[0].x).Within(0.0001f));
            Assert.That(
                firstPlayerAnchors[0].y - firstPlayerAnchors[1].y,
                Is.EqualTo(0.14f).Within(0.0001f));
            Assert.That(
                firstPlayerAnchors[1].y - firstPlayerAnchors[2].y,
                Is.EqualTo(0.14f).Within(0.0001f));

            var overflowAnchor = (Vector2)resolve.Invoke(
                null,
                new object[]
                {
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Unused,
                    99,
                    99,
                    99
                });
            Assert.That(overflowAnchor.x + compactMarkerHalfWidth, Is.LessThanOrEqualTo(unusedMaximumX));
            Assert.That(overflowAnchor.y - compactMarkerHalfHeight, Is.GreaterThanOrEqualTo(unusedMinimumY));
        }

        [Test]
        public void CityStyleMarkerEntries_SharePlayerAwareLayoutTracker()
        {
            var trackerType = Type.GetType(
                "YC.Presentation.CityStyleMarkerLayoutTracker, Assembly-CSharp",
                false);
            var buildPanelType = Type.GetType(
                "YC.Presentation.BuildInfoPanel, Assembly-CSharp",
                false);
            var previewDialogType = Type.GetType(
                "YC.Presentation.CityStyleDeclarationPreviewDialog, Assembly-CSharp",
                false);
            Assert.That(trackerType, Is.Not.Null);
            Assert.That(buildPanelType, Is.Not.Null);
            Assert.That(previewDialogType, Is.Not.Null);
            Assert.That(
                HasPrivateMethodParameter(
                    buildPanelType,
                    "AddCityStyleInfluenceMarker",
                    trackerType),
                Is.True);
            Assert.That(
                HasPrivateMethodParameter(
                    previewDialogType,
                    "AddCityStyleInfluenceMarker",
                    trackerType),
                Is.True);

            var tracker = Activator.CreateInstance(trackerType, true);
            var next = trackerType.GetMethod("Next", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(next, Is.Not.Null);
            var playerOneFirst = next.Invoke(
                tracker,
                new object[]
                {
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Declared,
                    1
                });
            var playerOneSecond = next.Invoke(
                tracker,
                new object[]
                {
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Unused,
                    1
                });
            var playerTwoFirst = next.Invoke(
                tracker,
                new object[]
                {
                    CityStyleDatabase.MilitaryIndustrialArea,
                    CityStyleMarkerAreas.Declared,
                    2
                });

            Assert.That(
                GetPublicProperty<string>(playerOneFirst, "MarkerArea"),
                Is.EqualTo(CityStyleMarkerAreas.Unused));
            Assert.That(GetPublicProperty<int>(playerOneFirst, "PlayerLaneIndex"), Is.Zero);
            Assert.That(GetPublicProperty<int>(playerOneFirst, "PlayerMarkerIndex"), Is.Zero);
            Assert.That(GetPublicProperty<int>(playerOneSecond, "PlayerLaneIndex"), Is.Zero);
            Assert.That(GetPublicProperty<int>(playerOneSecond, "PlayerMarkerIndex"), Is.EqualTo(1));
            Assert.That(GetPublicProperty<int>(playerTwoFirst, "PlayerLaneIndex"), Is.EqualTo(1));
            Assert.That(GetPublicProperty<int>(playerTwoFirst, "PlayerMarkerIndex"), Is.Zero);

            var otherStyleTracker = Activator.CreateInstance(trackerType, true);
            var otherStyleMarker = next.Invoke(
                otherStyleTracker,
                new object[] { "style.test", CityStyleMarkerAreas.Declared, 1 });
            Assert.That(
                GetPublicProperty<string>(otherStyleMarker, "MarkerArea"),
                Is.EqualTo(CityStyleMarkerAreas.Declared));
        }

        [Test]
        public void CityStyleOptionsViewModel_ContainsOnlyDisplaySnapshotsAndCallbacks()
        {
            Assert.That(typeof(CityStyleOptionsViewModel).GetProperty("State"), Is.Null);
            Assert.That(typeof(CityStyleOptionsViewModel).GetProperty("PlayerId"), Is.Null);
            foreach (var property in typeof(CityStyleOptionsViewModel).GetProperties())
            {
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(GameState)), property.Name);
            }

            var slot = new CityBoardSlotViewModel(3, "facility.test", true);
            var marker = new CityStyleMarkerViewModel(
                "style.test",
                2,
                PlayerColor.Blue,
                CityStyleMarkerAreas.Used);
            Assert.That(slot.SlotIndex, Is.EqualTo(3));
            Assert.That(slot.FacilityId, Is.EqualTo("facility.test"));
            Assert.That(slot.Used, Is.True);
            Assert.That(marker.PlayerId, Is.EqualTo(2));
            Assert.That(marker.PlayerColor, Is.EqualTo(PlayerColor.Blue));
        }

        private static bool HasPrivateMethodParameter(Type ownerType, string methodName, Type parameterType)
        {
            var methods = ownerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
            for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
            {
                var method = methods[methodIndex];
                if (method.Name != methodName)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (parameters[parameterIndex].ParameterType == parameterType)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static T GetPublicProperty<T>(object target, string propertyName)
        {
            return (T)target.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                .GetValue(target, null);
        }
    }
}
