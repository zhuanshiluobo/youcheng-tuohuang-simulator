using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class InformationPanelsEditorAssetTests
    {
        private const string ExpandablePrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/ExpandableInfoPanel.prefab";
        private const string BuildPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab";

        [TestCase(ExpandablePrefabPath, "YC.Presentation.ExpandableInfoPanel")]
        [TestCase(BuildPrefabPath, "YC.Presentation.BuildInfoPanel")]
        public void StandalonePrefab_HasValidBidirectionalViewAndNoMissingScripts(
            string prefabPath,
            string controllerTypeName)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null, prefabPath);
            var controller = prefab.GetComponent(GetRuntimeType(controllerTypeName));
            Assert.That(controller, Is.Not.Null, controllerTypeName);
            var view = GetProperty<Component>(controller, "View");
            Assert.That(view, Is.Not.Null);
            Assert.That(
                view.GetType().GetMethod("IsBoundTo", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(view, new object[] { controller }),
                Is.True);
            AssertTryValidate(view);

            var transforms = prefab.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                Assert.That(
                    GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transforms[i].gameObject),
                    Is.EqualTo(0),
                    transforms[i].name);
            }
        }

        [Test]
        public void BuildPrefab_HasFixedSlotsTextureAndInactiveTemplates()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BuildPrefabPath);
            var controller = prefab.GetComponent(GetRuntimeType("YC.Presentation.BuildInfoPanel"));
            var view = GetProperty<Component>(controller, "View");
            Assert.That(GetProperty<Array>(view, "ExternalFacilitySlots").Length, Is.EqualTo(6));
            Assert.That(GetProperty<Array>(view, "CityBoardSlots").Length, Is.EqualTo(12));
            var boardImage = GetProperty<UnityEngine.UI.RawImage>(view, "CityBoardImage");
            Assert.That(boardImage.texture, Is.Not.Null);
            Assert.That(
                AssetDatabase.GetAssetPath(boardImage.texture),
                Is.EqualTo("Assets/YC/Presentation/Resources/CardImages/Boards/city_board.png"));

            var templateProperties = new[]
            {
                "CardContentTemplate",
                "CityStyleCardTemplate",
                "InfluenceMarkerTemplate",
                "DragGhostTemplate",
                "PendingBuildGhostTemplate"
            };
            for (var i = 0; i < templateProperties.Length; i++)
            {
                var template = GetProperty<Component>(view, templateProperties[i]);
                Assert.That(template, Is.Not.Null, templateProperties[i]);
                Assert.That(template.gameObject.activeSelf, Is.False, templateProperties[i]);
            }
        }

        [Test]
        public void ProductionControllers_DoNotRecreateFixedUiAtRuntime()
        {
            var projectRoot = Directory.GetCurrentDirectory();
            var expandable = File.ReadAllText(Path.Combine(
                projectRoot,
                "Assets/YC/Presentation/ExpandableInfoPanel.cs"));
            var build = File.ReadAllText(Path.Combine(
                projectRoot,
                "Assets/YC/Presentation/BuildInfoPanel.cs"));
            var mobile = File.ReadAllText(Path.Combine(
                projectRoot,
                "Assets/YC/Presentation/MobileCityInteractionController.cs"));

            StringAssert.DoesNotContain("new GameObject(", expandable);
            StringAssert.DoesNotContain(".AddComponent<", expandable);
            StringAssert.DoesNotContain("new GameObject(", build);
            StringAssert.DoesNotContain(".AddComponent<", build);
            StringAssert.DoesNotContain("Resources.Load", build);
            StringAssert.DoesNotContain("EnsureInfoPanel", mobile);
            StringAssert.DoesNotContain("EnsureBuildInfoPanel", mobile);
            StringAssert.DoesNotContain("FindObjectOfType<ExpandableInfoPanel>", mobile);
            StringAssert.DoesNotContain("FindObjectOfType<BuildInfoPanel>", mobile);
            StringAssert.DoesNotContain("new GameObject(", mobile);
            StringAssert.DoesNotContain(".AddComponent<", mobile);
            Assert.That(build.Split('\n').Length, Is.LessThan(1200));
            Assert.That(mobile.Split('\n').Length, Is.LessThan(1200));
        }

        private static Type GetRuntimeType(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, typeName);
            return type;
        }

        private static T GetProperty<T>(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, propertyName);
            return (T)property.GetValue(target, null);
        }

        private static void AssertTryValidate(object view)
        {
            var method = view.GetType().GetMethod(
                "TryValidateConfiguration",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { string.Empty };
            Assert.That(method.Invoke(view, arguments), Is.True, arguments[0] as string);
        }
    }
}
