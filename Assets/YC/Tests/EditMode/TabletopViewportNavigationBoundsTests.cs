using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class TabletopViewportNavigationBoundsTests
    {
        private static readonly Type BoundsType = Type.GetType(
            "YC.Presentation.TabletopViewportNavigationBounds, Assembly-CSharp",
            false);
        private static readonly Type GeometryType = Type.GetType(
            "YC.Presentation.MapCameraGeometry, Assembly-CSharp",
            false);

        [TestCase(1f)]
        [TestCase(0.9f)]
        [TestCase(2f)]
        public void FocusBounds_KeepViewportEdgesOnTheSameFixedWorldBoundary(float zoom)
        {
            Assert.That(BoundsType, Is.Not.Null);
            Assert.That(GeometryType, Is.Not.Null);
            var mapObject = new GameObject("Navigation Bounds Map", typeof(SpriteRenderer), BoundsType);
            var cameraObject = new GameObject("Navigation Bounds Camera", typeof(Camera));
            Texture2D texture = null;
            Sprite sprite = null;
            try
            {
                texture = new Texture2D(100, 60);
                sprite = Sprite.Create(texture, new Rect(0f, 0f, 100f, 60f), Vector2.one * 0.5f, 10f);
                var renderer = mapObject.GetComponent<SpriteRenderer>();
                renderer.sprite = sprite;
                var provider = mapObject.GetComponent(BoundsType);
                var camera = cameraObject.GetComponent<Camera>();
                camera.orthographic = false;
                camera.fieldOfView = 45f;
                camera.aspect = 16f / 9f;
                var rotation = Quaternion.Euler(-30f, 0f, 0f);
                var plane = new Plane(Vector3.forward, Vector3.zero);
                var viewport = new Rect(0.02865f, 0f, 0.78385f, 1f);
                const float baseDistance = 20f;
                camera.transform.SetPositionAndRotation(
                    InvokeGeometry<Vector3>("CalculateCameraPosition", Vector3.zero, rotation, baseDistance),
                    rotation);

                var initializeArguments = new object[]
                {
                    camera,
                    viewport,
                    plane,
                    Vector3.zero,
                    Vector3.right,
                    Vector3.up,
                    renderer,
                    null
                };
                Assert.That(
                    BoundsType.GetMethod("Initialize").Invoke(provider, initializeArguments),
                    Is.True,
                    initializeArguments[7] as string);
                var fixedWorldBounds = (Rect)BoundsType
                    .GetField("fixedWorldBounds", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(provider);

                var currentDistance = InvokeGeometry<float>("CalculateZoomedDistance", baseDistance, zoom);
                camera.transform.SetPositionAndRotation(
                    InvokeGeometry<Vector3>("CalculateCameraPosition", Vector3.zero, rotation, currentDistance),
                    rotation);
                var boundsArguments = new object[]
                {
                    camera,
                    viewport,
                    plane,
                    Vector3.zero,
                    Vector3.right,
                    Vector3.up,
                    null
                };
                Assert.That(
                    BoundsType.GetMethod("TryGetFocusBounds").Invoke(provider, boundsArguments),
                    Is.True);
                var focusBounds = (Rect)boundsArguments[6];

                AssertAxisBoundariesReachable(
                    camera,
                    viewport,
                    plane,
                    currentDistance,
                    focusBounds.xMin,
                    focusBounds.xMax,
                    fixedWorldBounds.xMin,
                    fixedWorldBounds.xMax,
                    true);
                AssertAxisBoundariesReachable(
                    camera,
                    viewport,
                    plane,
                    currentDistance,
                    focusBounds.yMin,
                    focusBounds.yMax,
                    fixedWorldBounds.yMin,
                    fixedWorldBounds.yMax,
                    false);
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

        private static void AssertAxisBoundariesReachable(
            Camera camera,
            Rect viewport,
            Plane plane,
            float cameraDistance,
            float firstFocus,
            float secondFocus,
            float expectedMinimum,
            float expectedMaximum,
            bool horizontal)
        {
            GetVisibleAxisRange(
                camera,
                viewport,
                plane,
                cameraDistance,
                firstFocus,
                horizontal,
                out var firstMinimum,
                out var firstMaximum);
            GetVisibleAxisRange(
                camera,
                viewport,
                plane,
                cameraDistance,
                secondFocus,
                horizontal,
                out var secondMinimum,
                out var secondMaximum);

            Assert.That(
                Mathf.Min(Mathf.Abs(firstMinimum - expectedMinimum), Mathf.Abs(secondMinimum - expectedMinimum)),
                Is.LessThanOrEqualTo(0.001f));
            Assert.That(
                Mathf.Min(Mathf.Abs(firstMaximum - expectedMaximum), Mathf.Abs(secondMaximum - expectedMaximum)),
                Is.LessThanOrEqualTo(0.001f));
        }

        private static void GetVisibleAxisRange(
            Camera camera,
            Rect viewport,
            Plane plane,
            float cameraDistance,
            float focusValue,
            bool horizontal,
            out float minimum,
            out float maximum)
        {
            var focus = horizontal
                ? new Vector3(focusValue, 0f, 0f)
                : new Vector3(0f, focusValue, 0f);
            camera.transform.position = InvokeGeometry<Vector3>(
                "CalculateCameraPosition",
                focus,
                camera.transform.rotation,
                cameraDistance);
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;
            for (var i = 0; i < 4; i++)
            {
                var viewportPoint = new Vector3(
                    (i & 1) == 0 ? viewport.xMin : viewport.xMax,
                    (i & 2) == 0 ? viewport.yMin : viewport.yMax);
                var ray = camera.ViewportPointToRay(viewportPoint);
                Assert.That(plane.Raycast(ray, out var rayDistance), Is.True);
                var point = ray.GetPoint(rayDistance);
                var value = horizontal ? point.x : point.y;
                minimum = Mathf.Min(minimum, value);
                maximum = Mathf.Max(maximum, value);
            }
        }

        private static T InvokeGeometry<T>(string methodName, params object[] arguments)
        {
            Assert.That(GeometryType, Is.Not.Null);
            var method = GeometryType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (T)method.Invoke(null, arguments);
        }
    }
}
