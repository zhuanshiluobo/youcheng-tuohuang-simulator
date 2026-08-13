using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class SecondaryLayoutEditorAssetTests
    {
        private const string ManifestPath =
            "Assets/YC/Editor/Data/secondary_layout_manifest.json";
        private const string ManifestGuid = "84181727001c7bc48b3b310efcce7325";
        private const string ManifestSha256 =
            "EFCBE8FFF20617DC67A9671AEFE123A83553D739BBA7B09476B3964869213544";
        private const string ActionProfilePath =
            "Assets/YC/Presentation/Content/ActionPanelLayoutProfile.asset";
        private const string ActionProfileGuid = "fdfb2e039f0c842439596ccfcea7686b";
        private const string CardProfilePath =
            "Assets/YC/Presentation/Content/CardInteractionLayoutProfile.asset";
        private const string CardProfileGuid = "84c89edc1445da24cb6d911a4839e1f3";
        private const string ViewerProfilePath =
            "Assets/YC/Presentation/Content/ZoomableViewerLayoutProfile.asset";
        private const string ViewerProfileGuid = "6897c32684bc9b7448d53ba522fcdbc8";

        [Test]
        public void ManifestProfilesAndPrefabs_AreLockedAndReady()
        {
            Assert.That(ComputeSha256(ManifestPath), Is.EqualTo(ManifestSha256).IgnoreCase);
            AssertControlledAsset(ManifestPath, ManifestGuid);
            AssertControlledAsset(ActionProfilePath, ActionProfileGuid);
            AssertControlledAsset(CardProfilePath, CardProfileGuid);
            AssertControlledAsset(ViewerProfilePath, ViewerProfileGuid);

            var action = AssetDatabase.LoadAssetAtPath<Object>(ActionProfilePath);
            var card = AssetDatabase.LoadAssetAtPath<Object>(CardProfilePath);
            var viewer = AssetDatabase.LoadAssetAtPath<Object>(ViewerProfilePath);
            AssertProfileValid(action);
            AssertProfileValid(card);
            AssertProfileValid(viewer);
            AssertSourceHash(action);
            AssertSourceHash(card);
            AssertSourceHash(viewer);

            AssertPrefabReference(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab",
                "YC.Presentation.ActionPanelView",
                "layoutProfile",
                action);
            AssertPrefabReference(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab",
                "YC.Presentation.GameplayDialogRegistry",
                "cardInteractionLayoutProfile",
                card);
            AssertPrefabReference(
                "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab",
                "YC.Presentation.BuildInfoPanelView",
                "cardInteractionLayoutProfile",
                card);
            AssertPrefabReference(
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab",
                "YC.Presentation.CityStyleDeclarationPreviewView",
                "cardInteractionLayoutProfile",
                card);
            AssertPrefabReference(
                "Assets/YC/Presentation/Prefabs/Viewers/ZoomableImageViewer.prefab",
                "YC.Presentation.ZoomableImageViewerView",
                "layoutProfile",
                viewer);

            Assert.DoesNotThrow(InvokeReadiness);
        }

        [Test]
        public void MissingOrInvalidProfile_FailsFastWithoutCodeDefault()
        {
            AssertInvalidTransientProfile("YC.Presentation.ActionPanelLayoutProfile");
            AssertInvalidTransientProfile("YC.Presentation.CardInteractionLayoutProfile");
            AssertInvalidTransientProfile("YC.Presentation.ZoomableViewerLayoutProfile");
            AssertMissingViewProfile("YC.Presentation.ActionPanelView");
            AssertMissingViewProfile("YC.Presentation.BuildInfoPanelView");
            AssertMissingViewProfile("YC.Presentation.ZoomableImageViewerView");
        }

        [Test]
        public void FixedCandidatesAreGone_AndDynamicConstructorsRemain()
        {
            var expected = new Dictionary<string, int>
            {
                { "Assets/YC/Presentation/ActionPanelController.cs", 0 },
                { "Assets/YC/Presentation/PromptPresenter.cs", 2 },
                { "Assets/YC/Presentation/BuildInfoPanel.cs", 2 },
                { "Assets/YC/Presentation/CityStyleDeclarationPreviewDialog.cs", 2 },
                { "Assets/YC/Presentation/CardPointerInteraction.cs", 0 },
                { "Assets/YC/Presentation/ZoomableImageViewerController.cs", 6 }
            };
            foreach (var pair in expected)
                Assert.That(CountConstructors(pair.Key), Is.EqualTo(pair.Value), pair.Key);

            var total = Directory.GetFiles(
                    Path.GetFullPath("Assets/YC/Presentation"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Sum(path => Regex.Matches(
                    File.ReadAllText(path),
                    @"\bnew\s+Vector2\s*\(").Count);
            Assert.That(total, Is.EqualTo(87));

            var combinedSource = string.Join(
                "\n",
                Directory.GetFiles(
                        Path.GetFullPath("Assets/YC/Presentation"),
                        "*.cs",
                        SearchOption.AllDirectories)
                    .Select(File.ReadAllText));
            StringAssert.DoesNotContain("GameplayInteractionLayoutProfile", combinedSource);
            StringAssert.Contains(
                "CardInteractionLayoutProfile.DragGhostLayout.RootLayout.ApplyTo",
                File.ReadAllText("Assets/YC/Presentation/BuildInfoPanel.cs"));
            StringAssert.Contains(
                "CardInteractionLayoutProfile.DragGhostLayout",
                File.ReadAllText("Assets/YC/Presentation/CityStyleDeclarationPreviewDialog.cs"));
            StringAssert.Contains(
                "cardInteractionLayoutProfile.DragGhostLayout",
                File.ReadAllText("Assets/YC/Presentation/FacilityEffectChoiceDialog.cs"));
        }

        private static void AssertInvalidTransientProfile(string fullName)
        {
            var profile = ScriptableObject.CreateInstance(GetRuntimeType(fullName));
            try
            {
                Assert.That(InvokeValidation(profile, out var reason), Is.False);
                Assert.That(reason, Is.Not.Empty);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static void AssertMissingViewProfile(string fullName)
        {
            var owner = new GameObject(fullName + " Missing Profile Test");
            try
            {
                var component = owner.AddComponent(GetRuntimeType(fullName));
                Assert.That(InvokeValidation(component, out var reason), Is.False);
                StringAssert.Contains("Profile", reason);
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        private static void AssertPrefabReference(
            string prefabPath,
            string componentTypeName,
            string propertyName,
            Object expected)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null, prefabPath);
            var components = prefab.GetComponentsInChildren(GetRuntimeType(componentTypeName), true);
            Assert.That(components.Length, Is.EqualTo(1), componentTypeName);
            var property = new SerializedObject(components[0]).FindProperty(propertyName);
            Assert.That(property, Is.Not.Null, propertyName);
            Assert.That(property.objectReferenceValue, Is.SameAs(expected));
        }

        private static void AssertProfileValid(Object profile)
        {
            Assert.That(profile, Is.Not.Null);
            Assert.That(InvokeValidation(profile, out var reason), Is.True, reason);
        }

        private static bool InvokeValidation(Object target, out string reason)
        {
            var method = target.GetType().GetMethod(
                "TryValidateConfiguration",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, target.GetType().FullName);
            var arguments = new object[] { null };
            var result = (bool)method.Invoke(target, arguments);
            reason = arguments[0] as string ?? string.Empty;
            return result;
        }

        private static void AssertSourceHash(Object profile)
        {
            var serialized = new SerializedObject(profile);
            Assert.That(
                serialized.FindProperty("sourceManifestSha256").stringValue,
                Is.EqualTo(ManifestSha256).IgnoreCase);
        }

        private static void InvokeReadiness()
        {
            var type = Type.GetType(
                "YC.Editor.SecondaryLayoutBuildReadiness, Assembly-CSharp-Editor",
                true);
            type.GetMethod("ValidateReadyForBuild", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, null);
        }

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, fullName);
            return type;
        }

        private static int CountConstructors(string path)
        {
            return Regex.Matches(File.ReadAllText(path), @"\bnew\s+Vector2\s*\(").Count;
        }

        private static void AssertControlledAsset(string path, string guid)
        {
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(path));
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)))
                    .Replace("-", string.Empty);
            }
        }
    }
}
