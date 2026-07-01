using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class RuntimeUiLayoutTests
    {
        private GameObject owner;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                Object.DestroyImmediate(owner);
                owner = null;
            }

            DestroyNamedObject("Action Panel Test Canvas");
            DestroyNamedObject("Build Info Panel Canvas");
            DestroyNamedObject("Info Panel Canvas");
            DestroyNamedObject("Mobile City UI Canvas");
            DestroyNamedObject("Rulebook Viewer Canvas");
            DestroyNamedObject("EventSystem");
        }

        [Test]
        public void ActionPanel_IsDockedToBottomRightWithoutHintCardRail()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            BuildActionPanel(canvas);

            var actionPanel = FindTransform("Action Panel");
            var hintPanel = FindTransform("Hint Card Panel");

            Assert.That(actionPanel, Is.Not.Null);
            Assert.That(actionPanel.anchorMin, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(actionPanel.anchorMax, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(actionPanel.pivot, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(actionPanel.sizeDelta, Is.EqualTo(new Vector2(360f, 488f)));
            Assert.That(actionPanel.anchoredPosition, Is.EqualTo(Vector2.zero));
            Assert.That(hintPanel, Is.Null);
        }

        [Test]
        public void PromptPresenter_StartsHiddenInTopRightPromptArea()
        {
            var type = Type.GetType("YC.Presentation.PromptPresenter, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.PromptPresenter.");

            owner = new GameObject("Prompt Presenter Layout Test");
            var build = type.GetMethod("Build", BindingFlags.Static | BindingFlags.Public);
            Assert.That(build, Is.Not.Null, "Missing PromptPresenter.Build.");
            build.Invoke(null, new object[] { owner.transform });

            var promptPanel = FindTransform("Prompt Panel");
            Assert.That(promptPanel, Is.Not.Null);
            Assert.That(promptPanel.anchorMin, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(promptPanel.anchorMax, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(promptPanel.pivot, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(promptPanel.sizeDelta.x, Is.EqualTo(520f).Within(0.01f));
            Assert.That(promptPanel.anchoredPosition.x, Is.EqualTo(544f).Within(0.01f));
            Assert.That(promptPanel.anchoredPosition.y, Is.EqualTo(-128f).Within(0.01f));

            var group = promptPanel.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null);
            Assert.That(group.alpha, Is.EqualTo(0f).Within(0.001f));
            Assert.That(group.blocksRaycasts, Is.False);
        }

        [Test]
        public void InfoPanel_KeepsHintCardPreviewModule()
        {
            var type = Type.GetType("YC.Presentation.ExpandableInfoPanel, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.ExpandableInfoPanel.");

            owner = new GameObject("Info Panel Layout Test");
            var controller = owner.AddComponent(type);
            InvokePublic(controller, "Initialize", owner.transform);

            var modules = GetPublicProperty<System.Collections.IEnumerable>(controller, "Modules");
            Assert.That(HasModuleTitle(modules, "提示卡"), Is.True);

            var panel = FindTransform("Sidebar Panel");
            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.anchorMin, Is.EqualTo(new Vector2(0f, 0f)));
            Assert.That(panel.anchorMax, Is.EqualTo(new Vector2(0f, 1f)));
        }

        [Test]
        public void BuildInfoPanel_StartsAsSmallButtonAndExpandsToSquarePanel()
        {
            var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.BuildInfoPanel.");

            owner = new GameObject("Build Info Panel Layout Test");
            var controller = owner.AddComponent(type);
            InvokePublic(controller, "Initialize", owner.transform);

            var panel = FindTransform("Build Sidebar Panel");
            var content = FindTransform("Content Area");
            var toggle = FindTransform("Toggle Button");

            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.anchorMin, Is.EqualTo(new Vector2(1f, 0.5f)));
            Assert.That(panel.anchorMax, Is.EqualTo(new Vector2(1f, 0.5f)));
            Assert.That(panel.pivot, Is.EqualTo(new Vector2(1f, 0.5f)));
            Assert.That(panel.sizeDelta.x, Is.EqualTo(47f).Within(0.01f));
            Assert.That(panel.sizeDelta.y, Is.EqualTo(90f).Within(0.01f));
            Assert.That(panel.GetComponent<Outline>(), Is.Null);
            Assert.That(content, Is.Not.Null);
            Assert.That(content.gameObject.activeSelf, Is.False);
            Assert.That(toggle, Is.Not.Null);
            Assert.That(toggle.sizeDelta.x, Is.EqualTo(47f).Within(0.01f));
            Assert.That(toggle.anchoredPosition, Is.EqualTo(Vector2.zero));
            AssertColor(toggle.GetComponent<Image>().color, new Color(0.14f, 0.1f, 0.06f, 0.96f));
            Assert.That(toggle.GetComponent<Outline>(), Is.Null);
            Assert.That(AllTextRenderersAreMaskable(panel), Is.True);

            var collapsedToggleX = toggle.position.x;
            toggle.GetComponent<Button>().onClick.Invoke();

            Assert.That(panel.sizeDelta.x, Is.EqualTo(520f).Within(0.01f));
            Assert.That(panel.sizeDelta.y, Is.EqualTo(520f).Within(0.01f));
            Assert.That(content.gameObject.activeSelf, Is.True);
            Assert.That(toggle.position.x, Is.LessThan(collapsedToggleX));
        }

        [Test]
        public void RulebookViewer_UsesTopRightCrossCloseButton()
        {
            var type = Type.GetType("YC.Presentation.RulebookViewerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RulebookViewerController.");

            owner = new GameObject("Rulebook Viewer Test");
            var controller = owner.AddComponent(type);
            EnsureAwakeRan(controller, "rootObject");

            var closeButton = FindTransform("Close Rulebook Button");
            Assert.That(closeButton, Is.Not.Null);
            Assert.That(closeButton.anchorMin, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeButton.anchorMax, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeButton.pivot, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeButton.sizeDelta, Is.EqualTo(new Vector2(42f, 42f)));

            var label = closeButton.GetComponentInChildren<Text>(true);
            Assert.That(label, Is.Not.Null);
            Assert.That(label.text, Is.EqualTo("×"));
        }

        private static Canvas CreateCanvas(string name)
        {
            var canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        private static object BuildActionPanel(Canvas canvas)
        {
            var type = Type.GetType("YC.Presentation.ActionPanelController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.ActionPanelController.");

            var build = type.GetMethod("Build", BindingFlags.Static | BindingFlags.Public);
            Assert.That(build, Is.Not.Null, "Missing ActionPanelController.Build.");

            Action noop = () => { };
            return build.Invoke(
                null,
                new object[]
                {
                    canvas,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop
                });
        }

        private static void EnsureAwakeRan(Component component, string readyFieldName)
        {
            var field = component.GetType().GetField(readyFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing " + component.GetType().Name + "." + readyFieldName + ".");
            if (field.GetValue(component) != null)
            {
                return;
            }

            var awake = component.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null, "Missing " + component.GetType().Name + ".Awake.");
            awake.Invoke(component, null);
        }

        private static void InvokePublic(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing " + target.GetType().Name + "." + methodName + ".");
            method.Invoke(target, args);
        }

        private static T GetPublicProperty<T>(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, "Missing " + target.GetType().Name + "." + propertyName + ".");
            return (T)property.GetValue(target, null);
        }

        private static bool HasModuleTitle(System.Collections.IEnumerable modules, string title)
        {
            foreach (var module in modules)
            {
                var property = module.GetType().GetProperty("Title", BindingFlags.Instance | BindingFlags.Public);
                if (property != null && (string)property.GetValue(module, null) == title)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f));
        }

        private static bool AllTextRenderersAreMaskable(RectTransform root)
        {
            var texts = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null && !texts[i].maskable)
                {
                    return false;
                }
            }

            return true;
        }

        private static RectTransform FindTransform(string name)
        {
            var transforms = Resources.FindObjectsOfTypeAll<RectTransform>();
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private static void DestroyNamedObject(string objectName)
        {
            var go = GameObject.Find(objectName);
            if (go != null)
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
