using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class MapCameraGeometryTests
    {
        private static readonly Type GeometryType = Type.GetType(
            "YC.Presentation.MapCameraGeometry, Assembly-CSharp",
            false);

        [Test]
        public void PerspectiveFitDistance_ContainsEveryTabletopCorner()
        {
            EditModeTestCaseRunner.Run(
                new[] { 16f / 9f, 4f / 3f, 21f / 9f },
                aspect =>
                {
                    Assert.That(GeometryType, Is.Not.Null);
                    var corners = new List<Vector3>
                    {
                        new Vector3(-8f, -5f, 0f),
                        new Vector3(-8f, 5f, 0f),
                        new Vector3(12f, 5f, 0f),
                        new Vector3(12f, -5f, 0f)
                    };
                    var focus = new Vector3(2f, 0f, 0f);
                    var rotation = Quaternion.Euler(30f, 0f, 0f);
                    const float fov = 45f;
                    const float padding = 1.03f;
                    var distance = Invoke<float>(
                        "CalculatePerspectiveFitDistance",
                        corners,
                        focus,
                        rotation,
                        fov,
                        aspect,
                        padding,
                        0.3f);
                    var cameraPosition = Invoke<Vector3>("CalculateCameraPosition", focus, rotation, distance);
                    var inverseRotation = Quaternion.Inverse(rotation);
                    var verticalTangent = Mathf.Tan(fov * Mathf.Deg2Rad * 0.5f);

                    foreach (var corner in corners)
                    {
                        var local = inverseRotation * (corner - cameraPosition);
                        Assert.That(
                            local.z,
                            Is.GreaterThanOrEqualTo(0.3f - 0.0001f),
                            "aspect=" + aspect);
                        Assert.That(
                            Mathf.Abs(local.x) * padding,
                            Is.LessThanOrEqualTo(local.z * verticalTangent * aspect + 0.0001f),
                            "aspect=" + aspect);
                        Assert.That(
                            Mathf.Abs(local.y) * padding,
                            Is.LessThanOrEqualTo(local.z * verticalTangent + 0.0001f),
                            "aspect=" + aspect);
                    }
                },
                aspect => "aspect=" + aspect);
        }

        [Test]
        public void PlanarCenter_UsesUnionOfAllContributedCorners()
        {
            var corners = new List<Vector3>
            {
                new Vector3(-10f, -4f, 0f),
                new Vector3(6f, 8f, 0f),
                new Vector3(2f, 3f, 0f)
            };

            var center = Invoke<Vector3>(
                "CalculatePlanarCenter",
                corners,
                Vector3.zero,
                Vector3.right,
                Vector3.up);

            Assert.That(center.x, Is.EqualTo(-2f).Within(0.0001f));
            Assert.That(center.y, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(center.z, Is.Zero.Within(0.0001f));
        }

        [Test]
        public void ClampPlanarPosition_LimitsBothMapAxesToTwentyFivePercentRange()
        {
            var clamped = Invoke<Vector3>(
                "ClampPlanarPosition",
                new Vector3(100f, -100f, 9f),
                new Vector3(2f, 3f, 0f),
                Vector3.right,
                Vector3.up,
                Rect.MinMaxRect(-5f, -2.5f, 5f, 2.5f));

            Assert.That(clamped.x, Is.EqualTo(7f).Within(0.0001f));
            Assert.That(clamped.y, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(clamped.z, Is.Zero.Within(0.0001f));
        }

        [Test]
        public void ZoomedDistance_UsesOneHundredPercentAsBaseline()
        {
            EditModeTestCaseRunner.Run(
                new[]
                {
                    new ZoomCase { Zoom = 0.9f, ExpectedDistance = 22.222222f },
                    new ZoomCase { Zoom = 1f, ExpectedDistance = 20f },
                    new ZoomCase { Zoom = 2f, ExpectedDistance = 10f }
                },
                testCase =>
                {
                    var distance = Invoke<float>("CalculateZoomedDistance", 20f, testCase.Zoom);
                    Assert.That(
                        distance,
                        Is.EqualTo(testCase.ExpectedDistance).Within(0.0001f),
                        "zoom=" + testCase.Zoom);
                },
                testCase => "zoom=" + testCase.Zoom);
        }

        [Test]
        public void ScreenPointToMapPlane_UsesPerspectiveCameraRay()
        {
            var cameraObject = new GameObject("Map Camera Geometry Test", typeof(Camera));
            var mapObject = new GameObject("Map Camera Geometry Map", typeof(SpriteRenderer));
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = false;
                camera.fieldOfView = 45f;
                camera.aspect = 16f / 9f;
                var rotation = Quaternion.Euler(30f, 0f, 0f);
                camera.transform.SetPositionAndRotation(
                    Invoke<Vector3>("CalculateCameraPosition", Vector3.zero, rotation, 20f),
                    rotation);

                var arguments = new object[]
                {
                    camera,
                    new Vector2(camera.pixelWidth * 0.5f, camera.pixelHeight * 0.5f),
                    mapObject.GetComponent<SpriteRenderer>(),
                    null
                };
                var hit = (bool)GeometryType.GetMethod("TryScreenToMapPlane").Invoke(null, arguments);
                var worldPoint = (Vector3)arguments[3];

                Assert.That(hit, Is.True);
                Assert.That(worldPoint.x, Is.Zero.Within(0.0001f));
                Assert.That(worldPoint.y, Is.Zero.Within(0.0001f));
                Assert.That(worldPoint.z, Is.Zero.Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(mapObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void ResizingMapViewport_KeepsNavigationZoomAndRejectsOutsideScreenPoints()
        {
            var controllerType = Type.GetType("YC.Presentation.MapDisplayController, Assembly-CSharp", true);
            var boundsType = Type.GetType("YC.Presentation.TabletopViewportNavigationBounds, Assembly-CSharp", true);
            var mapObject = new GameObject("Viewport Map", typeof(SpriteRenderer));
            var cameraObject = new GameObject("Viewport Camera", typeof(Camera));
            Texture2D texture = null;
            Sprite sprite = null;
            mapObject.SetActive(false);
            try
            {
                texture = new Texture2D(100, 60);
                sprite = Sprite.Create(texture, new Rect(0f, 0f, 100f, 60f),
                    Vector2.one * 0.5f, 10f);
                var renderer = mapObject.GetComponent<SpriteRenderer>();
                renderer.sprite = sprite;
                var camera = cameraObject.GetComponent<Camera>();
                var bounds = mapObject.AddComponent(boundsType);
                var controller = mapObject.AddComponent(controllerType);
                controllerType.GetField("mapRenderer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, renderer);
                controllerType.GetField("targetCamera", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, camera);
                controllerType.GetField("navigationBoundsSource", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, bounds);
                controllerType.GetMethod("FitCameraToMap", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(controller, null);
                controllerType.GetMethod("SetScreenViewport", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(controller, new object[] { new Rect(0.1f, 0.1f, 0.8f, 0.8f) });
                AssertField(controllerType, controller, "currentZoom", 0.9f);
                AssertField(controllerType, controller, "targetZoom", 0.9f);
                var zoomField = controllerType.GetField("currentZoom", BindingFlags.Instance | BindingFlags.NonPublic);
                var targetField = controllerType.GetField("targetZoom", BindingFlags.Instance | BindingFlags.NonPublic);
                zoomField.SetValue(controller, 1.5f);
                targetField.SetValue(controller, 1.8f);
                controllerType.GetMethod("SetScreenViewport", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(controller, new object[] { new Rect(0.2f, 0.15f, 0.6f, 0.7f) });

                Assert.That((float)zoomField.GetValue(controller), Is.EqualTo(1.5f).Within(0.0001f));
                Assert.That((float)targetField.GetValue(controller), Is.EqualTo(1.8f).Within(0.0001f));
                Assert.That(camera.rect.xMin, Is.EqualTo(0.2f).Within(0.0001f));
                Assert.That(camera.rect.yMin, Is.EqualTo(0.15f).Within(0.0001f));
                Assert.That(camera.rect.width, Is.EqualTo(0.6f).Within(0.0001f));
                Assert.That(camera.rect.height, Is.EqualTo(0.7f).Within(0.0001f));
                var viewport = camera.pixelRect;
                var inside = new object[] { camera, viewport.center };
                var outside = new object[] { camera, new Vector2(viewport.xMin - 2f, viewport.center.y) };
                var contains = GeometryType.GetMethod("IsScreenPointInCameraViewport");
                Assert.That((bool)contains.Invoke(null, inside), Is.True);
                Assert.That((bool)contains.Invoke(null, outside), Is.False);
            }
            finally
            {
                if (sprite != null) Object.DestroyImmediate(sprite);
                if (texture != null) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(mapObject);
            }
        }

        [Test]
        public void Controller_DefaultsMatchTabletopNavigationContract()
        {
            var controllerType = Type.GetType("YC.Presentation.MapDisplayController, Assembly-CSharp", false);
            Assert.That(controllerType, Is.Not.Null);
            var owner = new GameObject("Map Camera Defaults Test");
            owner.SetActive(false);
            try
            {
                var controller = owner.AddComponent(controllerType);
                AssertField(controllerType, controller, "fieldOfView", 45f);
                AssertField(controllerType, controller, "minZoom", 0.9f);
                AssertField(controllerType, controller, "maxZoom", 2f);
                AssertField(controllerType, controller, "wheelZoomStep", 0.1f);
                AssertField(controllerType, controller, "zoomSmoothTime", 0.12f);
                var viewport = (Rect)controllerType
                    .GetField("tabletopViewport", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(controller);
                Assert.That(viewport.xMin, Is.EqualTo(0.02865f).Within(0.00001f));
                Assert.That(viewport.xMax, Is.EqualTo(0.8125f).Within(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [TestCase(16f / 9f)]
        [TestCase(32f / 9f)]
        [TestCase(9f / 16f)]
        public void Controller_FitCameraToMap_KeepsTopDownViewportInsideMap(float aspect)
        {
            var controllerType = Type.GetType("YC.Presentation.MapDisplayController, Assembly-CSharp", false);
            Assert.That(controllerType, Is.Not.Null);
            var mapObject = new GameObject("Perspective Map Camera Test", typeof(SpriteRenderer));
            var cameraObject = new GameObject("Perspective Map Camera", typeof(Camera));
            Texture2D texture = null;
            Sprite sprite = null;
            mapObject.SetActive(false);
            try
            {
                texture = new Texture2D(100, 60);
                sprite = Sprite.Create(texture, new Rect(0f, 0f, 100f, 60f), new Vector2(0.5f, 0.5f), 10f);
                var renderer = mapObject.GetComponent<SpriteRenderer>();
                renderer.sprite = sprite;
                var camera = cameraObject.GetComponent<Camera>();
                camera.aspect = aspect;
                camera.orthographic = true;
                var navigationBoundsType = Type.GetType(
                    "YC.Presentation.TabletopViewportNavigationBounds, Assembly-CSharp",
                    false);
                Assert.That(navigationBoundsType, Is.Not.Null);
                var navigationBounds = mapObject.AddComponent(navigationBoundsType);
                var controller = mapObject.AddComponent(controllerType);
                controllerType.GetField("mapRenderer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, renderer);
                controllerType.GetField("targetCamera", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, camera);
                controllerType.GetField("navigationBoundsSource", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, navigationBounds);

                controllerType.GetMethod("FitCameraToMap", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(controller, null);

                Assert.That(camera.orthographic, Is.False);
                Assert.That(camera.fieldOfView, Is.EqualTo(45f).Within(0.0001f));
                Assert.That(camera.rect, Is.EqualTo(new Rect(0f, 0f, 1f, 1f)));
                Assert.That(Mathf.DeltaAngle(camera.transform.eulerAngles.x, 0f),
                    Is.Zero.Within(0.0001f));
                var mapCenterViewport = camera.WorldToViewportPoint(Vector3.zero);
                Assert.That(mapCenterViewport.x, Is.EqualTo(0.5f).Within(0.001f));
                Assert.That(mapCenterViewport.y, Is.EqualTo(0.5f).Within(0.001f));
                var focusPoint = (Vector3)controllerType
                    .GetField("focusPoint", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(controller);
                var panOrigin = (Vector3)controllerType
                    .GetField("panOrigin", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(controller);
                Assert.That(focusPoint, Is.EqualTo(Vector3.zero));
                Assert.That(panOrigin, Is.EqualTo(Vector3.zero));
                AssertField(controllerType, controller, "currentZoom", 0.9f);
                AssertField(controllerType, controller, "targetZoom", 0.9f);

                controllerType.GetField("currentZoom", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, 0.9f);
                controllerType.GetMethod("ApplyCameraTransform", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, null);
                var minimumPanBounds = (Rect)controllerType
                    .GetMethod("GetCurrentPanBounds", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, null);
                Assert.That(Mathf.Min(minimumPanBounds.width, minimumPanBounds.height),
                    Is.Zero.Within(0.001f));

                controllerType.GetField("currentZoom", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, 2f);
                controllerType.GetMethod("ApplyCameraTransform", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, null);
                var zoomedPanBounds = (Rect)controllerType
                    .GetMethod("GetCurrentPanBounds", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, null);
                Assert.That(zoomedPanBounds.xMin, Is.LessThan(0f));
                Assert.That(zoomedPanBounds.xMax, Is.GreaterThan(0f));
                Assert.That(zoomedPanBounds.yMin, Is.LessThan(0f));
                Assert.That(zoomedPanBounds.yMax, Is.GreaterThan(0f));

                // 拖到四角，再连续缩小；每一步完整视口都不能越过地图边界。
                foreach (var zoom in new[] { 2f, 1.5f, 1f, 0.9f })
                {
                    controllerType.GetField("currentZoom", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(controller, zoom);
                    for (var edge = 0; edge < 4; edge++)
                    {
                        controllerType.GetField("focusPoint", BindingFlags.Instance | BindingFlags.NonPublic)
                            .SetValue(controller, new Vector3((edge & 1) == 0 ? -100f : 100f,
                                (edge & 2) == 0 ? -100f : 100f, 0f));
                        controllerType.GetMethod("ApplyCameraTransform", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(controller, null);
                        for (var corner = 0; corner < 4; corner++)
                        {
                            var ray = camera.ViewportPointToRay(new Vector3(corner & 1, (corner >> 1) & 1));
                            Assert.That(new Plane(Vector3.forward, Vector3.zero).Raycast(ray, out var distance), Is.True);
                            var point = ray.GetPoint(distance);
                            Assert.That(point.x, Is.InRange(-5.001f, 5.001f));
                            Assert.That(point.y, Is.InRange(-3.001f, 3.001f));
                        }
                    }
                }

            }
            finally
            {
                if (sprite != null)
                {
                    Object.DestroyImmediate(sprite);
                }

                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }

                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(mapObject);
            }
        }

        private static T Invoke<T>(string methodName, params object[] arguments)
        {
            Assert.That(GeometryType, Is.Not.Null);
            var method = GeometryType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "Missing MapCameraGeometry." + methodName + ".");
            return (T)method.Invoke(null, arguments);
        }

        private static void AssertField(Type type, object target, string fieldName, float expected)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            Assert.That((float)field.GetValue(target), Is.EqualTo(expected).Within(0.0001f));
        }

        private sealed class ZoomCase
        {
            public float Zoom;
            public float ExpectedDistance;
        }
    }
}
