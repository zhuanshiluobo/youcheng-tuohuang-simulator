using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class TabletopCanvasLayoutTests
    {
        private readonly List<Object> createdObjects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (var i = createdObjects.Count - 1; i >= 0; i--)
            {
                if (createdObjects[i] != null)
                {
                    Object.DestroyImmediate(createdObjects[i]);
                }
            }

            createdObjects.Clear();
        }

        [Test]
        public void Configure_AlignsWorldCanvasToMapAndKeepsRaycastingEnabled()
        {
            var layout = CreateConfiguredLayout(out var canvas, out var rectTransform,
                out var raycaster, out var scaler);
            var targetCamera = CreateComponent<Camera>("Tabletop Camera");
            var mapRenderer = CreateMapRenderer();
            mapRenderer.transform.SetPositionAndRotation(
                new Vector3(4f, -3f, 2f),
                Quaternion.Euler(0f, 0f, 17f));

            Assert.That(InvokeBoolWithReason(layout, "Configure", out var reason,
                targetCamera, mapRenderer), Is.True, reason);

            var expectedWorldUnitsPerPixel = mapRenderer.bounds.size.y / 1080f;
            Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
            Assert.That(canvas.worldCamera, Is.SameAs(targetCamera));
            Assert.That(rectTransform.sizeDelta, Is.EqualTo(new Vector2(1920f, 1080f)));
            Assert.That(rectTransform.position, Is.EqualTo(mapRenderer.bounds.center));
            Assert.That(Quaternion.Angle(rectTransform.rotation, mapRenderer.transform.rotation),
                Is.LessThan(0.001f));
            Assert.That(rectTransform.lossyScale.x, Is.EqualTo(expectedWorldUnitsPerPixel).Within(0.00001f));
            Assert.That(rectTransform.lossyScale.y, Is.EqualTo(expectedWorldUnitsPerPixel).Within(0.00001f));
            Assert.That(GetProperty<float>(layout, "WorldUnitsPerPixel"),
                Is.EqualTo(expectedWorldUnitsPerPixel).Within(0.00001f));
            Assert.That(raycaster.enabled, Is.True);
            Assert.That(GetProperty<GraphicRaycaster>(layout, "GraphicRaycaster"), Is.SameAs(raycaster));
            Assert.That(GetProperty<CanvasScaler>(layout, "CanvasScaler"), Is.SameAs(scaler));
            Assert.That(scaler.uiScaleMode, Is.EqualTo(CanvasScaler.ScaleMode.ScaleWithScreenSize));
            Assert.That(scaler.referenceResolution, Is.EqualTo(new Vector2(1920f, 1080f)));
        }

        [Test]
        public void Configure_CompensatesForParentScaleToKeepUniformWorldPixelSize()
        {
            var parent = CreateGameObject("Scaled Tabletop Parent");
            parent.transform.localScale = new Vector3(2f, 4f, 5f);
            var layout = CreateConfiguredLayout(out _, out var rectTransform, out _, out _);
            rectTransform.SetParent(parent.transform, false);
            var targetCamera = CreateComponent<Camera>("Tabletop Camera");
            var mapRenderer = CreateMapRenderer();

            Assert.That(InvokeBoolWithReason(layout, "Configure", out var reason,
                targetCamera, mapRenderer), Is.True, reason);

            Assert.That(rectTransform.lossyScale.x,
                Is.EqualTo(GetProperty<float>(layout, "WorldUnitsPerPixel")).Within(0.00001f));
            Assert.That(rectTransform.lossyScale.y,
                Is.EqualTo(GetProperty<float>(layout, "WorldUnitsPerPixel")).Within(0.00001f));
            Assert.That(rectTransform.lossyScale.z,
                Is.EqualTo(GetProperty<float>(layout, "WorldUnitsPerPixel")).Within(0.00001f));
        }

        [Test]
        public void TryValidateConfiguration_RequiresClickableCanvasComponents()
        {
            var canvasObject = CreateGameObject("Invalid Tabletop Canvas", typeof(RectTransform), typeof(Canvas));
            var layout = canvasObject.AddComponent(GetRuntimeType("YC.Presentation.TabletopCanvasLayout"));
            AssignReference(layout, "canvas", canvasObject.GetComponent<Canvas>());
            AssignReference(layout, "rectTransform", canvasObject.GetComponent<RectTransform>());

            Assert.That(InvokeBoolWithReason(layout, "TryValidateConfiguration", out var reason), Is.False);
            StringAssert.Contains("GraphicRaycaster", reason);
        }

        [Test]
        public void Configure_RejectsMissingCameraAndMapSpriteWithClearReasons()
        {
            var layout = CreateConfiguredLayout(out _, out _, out _, out _);
            var mapRenderer = CreateComponent<SpriteRenderer>("Map Without Sprite");

            Assert.That(InvokeBoolWithReason(layout, "Configure", out var cameraReason,
                null, mapRenderer), Is.False);
            StringAssert.Contains("目标相机", cameraReason);

            var targetCamera = CreateComponent<Camera>("Tabletop Camera");
            Assert.That(InvokeBoolWithReason(layout, "Configure", out var mapReason,
                targetCamera, mapRenderer), Is.False);
            StringAssert.Contains("Sprite", mapReason);
        }

        private Component CreateConfiguredLayout(
            out Canvas canvas,
            out RectTransform rectTransform,
            out GraphicRaycaster raycaster,
            out CanvasScaler scaler)
        {
            var canvasObject = CreateGameObject(
                "Tabletop Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster),
                typeof(CanvasScaler));
            canvas = canvasObject.GetComponent<Canvas>();
            rectTransform = canvasObject.GetComponent<RectTransform>();
            raycaster = canvasObject.GetComponent<GraphicRaycaster>();
            scaler = canvasObject.GetComponent<CanvasScaler>();
            var layout = canvasObject.AddComponent(GetRuntimeType("YC.Presentation.TabletopCanvasLayout"));
            AssignReference(layout, "canvas", canvas);
            AssignReference(layout, "rectTransform", rectTransform);
            return layout;
        }

        private SpriteRenderer CreateMapRenderer()
        {
            var texture = new Texture2D(192, 108);
            texture.name = "Tabletop Map Test Texture";
            createdObjects.Add(texture);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, 192f, 108f), Vector2.one * 0.5f, 10f);
            sprite.name = "Tabletop Map Test Sprite";
            createdObjects.Add(sprite);
            var mapRenderer = CreateComponent<SpriteRenderer>("Tabletop Map");
            mapRenderer.sprite = sprite;
            return mapRenderer;
        }

        private T CreateComponent<T>(string name) where T : Component
        {
            return CreateGameObject(name).AddComponent<T>();
        }

        private GameObject CreateGameObject(string name, params System.Type[] components)
        {
            var gameObject = new GameObject(name, components);
            createdObjects.Add(gameObject);
            return gameObject;
        }

        private static void AssignReference(Object target, string propertyName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(propertyName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool InvokeBoolWithReason(
            Component target,
            string methodName,
            out string reason,
            params object[] inputs)
        {
            var arguments = new object[inputs.Length + 1];
            for (var i = 0; i < inputs.Length; i++)
            {
                arguments[i] = inputs[i];
            }

            arguments[arguments.Length - 1] = string.Empty;
            MethodInfo method = null;
            var candidates = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (var i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Name == methodName &&
                    candidates[i].GetParameters().Length == arguments.Length)
                {
                    method = candidates[i];
                    break;
                }
            }

            Assert.That(method, Is.Not.Null, target.GetType().Name + "." + methodName);
            var result = (bool)method.Invoke(target, arguments);
            reason = arguments[arguments.Length - 1] as string ?? string.Empty;
            return result;
        }

        private static T GetProperty<T>(object target, string propertyName)
        {
            return (T)target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                .GetValue(target, null);
        }

        private static System.Type GetRuntimeType(string fullName)
        {
            var type = System.Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing runtime type " + fullName + ".");
            return type;
        }
    }
}
