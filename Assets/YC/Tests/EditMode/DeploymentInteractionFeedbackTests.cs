using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class DeploymentInteractionFeedbackTests
    {
        [Test]
        public void Theme_ExposesCyanAccentAndTacticalMapBackground()
        {
            var theme = RequireType("YC.Presentation.UiTheme");

            AssertColor(ReadColor(theme, "CyanAccent"), 0.12f, 0.88f, 1f, 1f);
            AssertColor(ReadColor(theme, "TacticalMapBg"), 0.08f, 0.10f, 0.12f, 1f);
        }

        [Test]
        public void MapDisplayController_AppliesTacticalBackgroundWithoutTintingMapSprite()
        {
            var mapObject = new GameObject("Map Display Test", typeof(SpriteRenderer));
            var cameraObject = new GameObject("Map Display Camera Test", typeof(Camera));
            try
            {
                var controllerType = RequireType("YC.Presentation.MapDisplayController");
                var controller = mapObject.AddComponent(controllerType);
                var camera = cameraObject.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.backgroundColor = Color.magenta;
                controllerType.GetField("targetCamera", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, camera);

                controllerType.GetMethod("FitCameraToMap", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(controller, null);

                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
                AssertColor(camera.backgroundColor, 0.08f, 0.10f, 0.12f, 1f);
                Assert.That(mapObject.GetComponent<SpriteRenderer>().color, Is.EqualTo(Color.white));
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(mapObject);
            }
        }

        [Test]
        public void ActionButtonFeedback_PressesToCardLikeScaleAndRestoresOnRelease()
        {
            var owner = new GameObject(
                "Action Button Feedback Test",
                typeof(RectTransform),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            try
            {
                var feedbackType = RequireType("YC.Presentation.ActionButtonPressFeedback");
                var feedback = owner.AddComponent(feedbackType);
                var button = owner.GetComponent<Button>();
                var outline = owner.GetComponent<Outline>();
                var restingColor = new Color(0.4f, 0.3f, 0.2f, 0.8f);
                outline.effectColor = restingColor;
                feedbackType.GetMethod("Configure").Invoke(feedback, new object[] { button, outline });

                Assert.That(ReadConstant(feedbackType, "PressDuration"), Is.EqualTo(0.08f));
                Assert.That(ReadConstant(feedbackType, "PressedScale"), Is.EqualTo(0.92f));

                var pointer = CreateLeftPointer();
                ((IPointerDownHandler)feedback).OnPointerDown(pointer);
                Assert.That(owner.transform.localScale.x, Is.EqualTo(0.92f).Within(0.001f));
                AssertColor(outline.effectColor, 0.12f, 0.88f, 1f, 1f);

                ((IPointerUpHandler)feedback).OnPointerUp(pointer);
                Assert.That(owner.transform.localScale, Is.EqualTo(Vector3.one));
                Assert.That(outline.effectColor, Is.EqualTo(restingColor));

                button.interactable = false;
                ((IPointerDownHandler)feedback).OnPointerDown(pointer);
                Assert.That(owner.transform.localScale, Is.EqualTo(Vector3.one));
                Assert.That(outline.effectColor, Is.EqualTo(restingColor));
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void MapHighlightPulse_UsesCyanBorderAndExactPointSixSecondCycle()
        {
            var owner = new GameObject("Map Highlight Pulse Test", typeof(SpriteRenderer));
            try
            {
                var pulseType = RequireType("YC.Presentation.MapHighlightPulse");
                var pulse = owner.AddComponent(pulseType);
                var renderer = owner.GetComponent<SpriteRenderer>();
                pulseType.GetMethod("Configure").Invoke(pulse, new object[] { renderer });
                pulseType.GetMethod("SetHighlighted").Invoke(pulse, new object[] { true });

                Assert.That(renderer.enabled, Is.True);
                AssertColorRgb(renderer.color, 0.12f, 0.88f, 1f);
                Assert.That(renderer.color.a, Is.InRange(0.4f, 1f));
                Assert.That(Evaluate(pulseType, "EvaluateAlpha", 0f), Is.EqualTo(0.7f).Within(0.001f));
                Assert.That(Evaluate(pulseType, "EvaluateAlpha", 0.15f), Is.EqualTo(1f).Within(0.001f));
                Assert.That(Evaluate(pulseType, "EvaluateAlpha", 0.45f), Is.EqualTo(0.4f).Within(0.001f));
                Assert.That(
                    Evaluate(pulseType, "EvaluateAlpha", 0.6f),
                    Is.EqualTo(Evaluate(pulseType, "EvaluateAlpha", 0f)).Within(0.001f));

                pulseType.GetMethod("SetHighlighted").Invoke(pulse, new object[] { false });
                Assert.That(renderer.enabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void PlacementFeedback_AnimatesOverlaysWithoutScalingRealTarget()
        {
            var owner = new GameObject("Map Placement Feedback Test", typeof(SpriteRenderer));
            try
            {
                owner.transform.localScale = new Vector3(0.65f, 0.65f, 1f);
                var originalScale = owner.transform.localScale;
                var feedbackType = RequireType("YC.Presentation.MapPlacementFeedback");
                var feedback = owner.AddComponent(feedbackType);
                feedbackType.GetMethod("Configure").Invoke(feedback, new object[] { 20 });
                feedbackType.GetMethod("Play").Invoke(feedback, null);

                Assert.That(ReadConstant(feedbackType, "Duration"), Is.EqualTo(0.15f));
                Assert.That(owner.transform.localScale, Is.EqualTo(originalScale));
                var flash = owner.transform.Find("Placement Flash").GetComponent<SpriteRenderer>();
                var ring = owner.transform.Find("Placement Expanding Ring").GetComponent<SpriteRenderer>();
                Assert.That(flash.enabled, Is.True);
                Assert.That(ring.enabled, Is.True);
                Assert.That(flash.transform.localScale.x, Is.EqualTo(1.2f).Within(0.001f));
                Assert.That(ring.transform.localScale.x, Is.EqualTo(1f).Within(0.001f));
                AssertColorRgb(flash.color, 0.12f, 0.88f, 1f);

                Assert.That(Evaluate(feedbackType, "EvaluateFlashScale", 1f), Is.EqualTo(1f));
                Assert.That(Evaluate(feedbackType, "EvaluateRingScale", 1f), Is.EqualTo(1.8f));
                Assert.That(Evaluate(feedbackType, "EvaluateRingAlpha", 1f), Is.Zero);

                feedbackType.GetMethod("ResetFeedback").Invoke(feedback, null);
                Assert.That(flash.enabled, Is.False);
                Assert.That(ring.enabled, Is.False);
                Assert.That(owner.transform.localScale, Is.EqualTo(originalScale));
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        private static Type RequireType(string name)
        {
            var type = Type.GetType(name + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing " + name + ".");
            return type;
        }

        private static Color ReadColor(Type type, string fieldName)
        {
            return (Color)type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static).GetValue(null);
        }

        private static float ReadConstant(Type type, string fieldName)
        {
            return (float)type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static).GetValue(null);
        }

        private static float Evaluate(Type type, string methodName, float value)
        {
            return (float)type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { value });
        }

        private static PointerEventData CreateLeftPointer()
        {
            return new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left
            };
        }

        private static void AssertColor(Color actual, float r, float g, float b, float a)
        {
            AssertColorRgb(actual, r, g, b);
            Assert.That(actual.a, Is.EqualTo(a).Within(0.001f));
        }

        private static void AssertColorRgb(Color actual, float r, float g, float b)
        {
            Assert.That(actual.r, Is.EqualTo(r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(b).Within(0.001f));
        }
    }
}
