using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class RoundTrackerControllerTests
    {
        private GameObject owner;
        private GameObject createdEventSystem;
        private Component controller;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
                owner = null;
            }

            if (createdEventSystem != null)
            {
                UnityEngine.Object.DestroyImmediate(createdEventSystem);
                createdEventSystem = null;
            }
        }

        [Test]
        public void BuildRoundUi_CreatesExpandedDynamicPanelByDefault()
        {
            controller = CreateController();

            var panel = GetPrivateField<RectTransform>("panelTransform");
            var content = GetPrivateField<RectTransform>("contentArea");
            var toggle = GetPrivateField<Button>("toggleButton");
            var expandedHeight = GetPrivateStaticFloat("ExpandedPanelHeight");
            var panelImage = panel.GetComponent<Image>();

            Assert.That(GetPublicProperty<bool>("IsExpanded"), Is.True);
            Assert.That(panel.GetComponent<RectMask2D>(), Is.Not.Null);
            Assert.That(panelImage, Is.Not.Null);
            Assert.That(panelImage.enabled, Is.True);
            Assert.That(panelImage.raycastTarget, Is.False);
            Assert.That(panel.anchorMin, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(panel.anchorMax, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(panel.pivot, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(panel.rect.height, Is.EqualTo(expandedHeight).Within(0.01f));
            Assert.That(content.gameObject.activeSelf, Is.True);
            Assert.That(toggle, Is.Not.Null);

            Assert.That(FindChild(content, "Round Track"), Is.Not.Null);
            Assert.That(FindChild(content, "End Round Button"), Is.Null);
        }

        [Test]
        public void Toggle_CollapsesAndExpandsRoundPanelVertically()
        {
            controller = CreateController();
            var panel = GetPrivateField<RectTransform>("panelTransform");
            var content = GetPrivateField<RectTransform>("contentArea");
            var toggleText = GetPrivateField<Text>("toggleButtonText");
            var collapsedHeight = GetPrivateStaticFloat("CollapsedPanelHeight");
            var expandedHeight = GetPrivateStaticFloat("ExpandedPanelHeight");
            var panelImage = panel.GetComponent<Image>();
            var panelOutline = panel.GetComponent<Outline>();

            InvokePublic("Toggle");

            Assert.That(GetPublicProperty<bool>("IsExpanded"), Is.False);
            Assert.That(content.gameObject.activeSelf, Is.False, "Content should hide immediately while collapse animates.");
            Assert.That(panelImage.enabled, Is.False);
            Assert.That(panelImage.raycastTarget, Is.False);
            Assert.That(panelOutline.enabled, Is.False);
            Assert.That(GetPrivateField<float>("targetPanelHeight"), Is.EqualTo(collapsedHeight));

            StepAnimationToTarget();

            Assert.That(panel.rect.height, Is.EqualTo(collapsedHeight).Within(0.01f));
            Assert.That(content.gameObject.activeSelf, Is.False);
            Assert.That(panelImage.enabled, Is.False);
            Assert.That(panelOutline.enabled, Is.False);
            Assert.That(toggleText.text, Is.EqualTo(GetPrivateStaticString("CollapsedArrow")));

            InvokePublic("Toggle");

            Assert.That(GetPublicProperty<bool>("IsExpanded"), Is.True);
            Assert.That(content.gameObject.activeSelf, Is.True);
            Assert.That(panelImage.enabled, Is.True);
            Assert.That(panelOutline.enabled, Is.True);

            StepAnimationToTarget();

            Assert.That(panel.rect.height, Is.EqualTo(expandedHeight).Within(0.01f));
            Assert.That(content.gameObject.activeSelf, Is.True);
            Assert.That(toggleText.text, Is.EqualTo(GetPrivateStaticString("ExpandedArrow")));
        }

        private Component CreateController()
        {
            var existingEventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
            var type = Type.GetType("YC.Presentation.RoundTrackerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RoundTrackerController.");

            owner = new GameObject("Round Tracker Controller Test");
            var component = owner.AddComponent(type);
            EnsureAwakeRan(component);

            if (existingEventSystem == null)
            {
                var eventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
                createdEventSystem = eventSystem == null ? null : eventSystem.gameObject;
            }

            return component;
        }

        private static void EnsureAwakeRan(Component component)
        {
            var panelField = component.GetType().GetField(
                "panelTransform",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(panelField, Is.Not.Null, "Missing RoundTrackerController.panelTransform.");
            if (panelField.GetValue(component) != null)
            {
                return;
            }

            var awake = component.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null, "Missing RoundTrackerController.Awake.");
            awake.Invoke(component, null);
        }

        private void StepAnimationToTarget()
        {
            InvokePrivate("StepPanelAnimation", 1f);
        }

        private void InvokePublic(string methodName, params object[] args)
        {
            var method = controller.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing RoundTrackerController." + methodName + ".");
            method.Invoke(controller, args);
        }

        private void InvokePrivate(string methodName, params object[] args)
        {
            var method = controller.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing RoundTrackerController." + methodName + ".");
            method.Invoke(controller, args);
        }

        private T GetPrivateField<T>(string fieldName)
        {
            var field = controller.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing RoundTrackerController." + fieldName + ".");
            return (T)field.GetValue(controller);
        }

        private T GetPublicProperty<T>(string propertyName)
        {
            var property = controller.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, "Missing RoundTrackerController." + propertyName + ".");
            return (T)property.GetValue(controller, null);
        }

        private Type GetControllerType()
        {
            var type = Type.GetType("YC.Presentation.RoundTrackerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RoundTrackerController.");
            return type;
        }

        private float GetPrivateStaticFloat(string fieldName)
        {
            var field = GetControllerType().GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing RoundTrackerController." + fieldName + ".");
            return (float)field.GetValue(null);
        }

        private string GetPrivateStaticString(string fieldName)
        {
            var field = GetControllerType().GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing RoundTrackerController." + fieldName + ".");
            return field.GetValue(null) as string;
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name)
                {
                    return child;
                }

                var match = FindChild(child, name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
