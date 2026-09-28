using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class InGameAssetIsolationTests
    {
        private static readonly string[] SharedPaths =
        {
            ViewerPrefabTestUtility.SharedSettingsPrefabPath,
            "Assets/YC/Presentation/Prefabs/Viewers/ActionLogViewer.prefab",
            "Assets/YC/Presentation/Prefabs/Viewers/ZoomableImageViewer.prefab",
            "Assets/YC/Presentation/Prefabs/Viewers/RulebookViewer.prefab"
        };

        private static readonly string[] InGamePaths =
        {
            ViewerPrefabTestUtility.InGameSettingsPrefabPath,
            ViewerPrefabTestUtility.ActionLogPrefabPath,
            ViewerPrefabTestUtility.ZoomablePrefabPath,
            ViewerPrefabTestUtility.RulebookPrefabPath
        };

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void SettingsAndViewerSources_HaveIndependentCompleteDependencies(int index)
        {
            Assert.That(AssetDatabase.AssetPathToGUID(SharedPaths[index]),
                Is.Not.EqualTo(AssetDatabase.AssetPathToGUID(InGamePaths[index])));
            AssertPrefabDependencies(SharedPaths[index], InGamePaths);
            AssertPrefabDependencies(InGamePaths[index], SharedPaths);
        }

        [TestCase("Assets/Scenes/StartScene.unity", false)]
        [TestCase("Assets/Scenes/SampleScene.unity", true)]
        [TestCase("Assets/Scenes/ThreePlayerScene.unity", true)]
        public void Scenes_ResolveSettingsFromTheirOwnScope(string scenePath, bool inGame)
        {
            var dependencies = AssetDatabase.GetDependencies(scenePath, true);
            var expected = inGame ? InGamePaths : SharedPaths;
            var excluded = inGame ? SharedPaths : InGamePaths;
            foreach (var path in expected)
                Assert.That(dependencies, Does.Contain(path), scenePath);
            foreach (var path in excluded)
                Assert.That(dependencies, Does.Not.Contain(path), scenePath);
        }

        [TestCase("YC.Editor.UiThemeBuildReadiness")]
        [TestCase("YC.Editor.EventCharacterBuildReadiness")]
        [TestCase("YC.Editor.CityStyleSpecialActionBuildReadiness")]
        [TestCase("YC.Editor.FacilityCardCatalogEditorAssetBuilder")]
        public void BuildReadiness_AcceptsSeparatedSceneSourcesWithoutSavingAssets(string typeName)
        {
            var paths = SharedPaths.Concat(InGamePaths).Concat(new[]
            {
                "Assets/Scenes/StartScene.unity",
                "Assets/Scenes/SampleScene.unity",
                "Assets/Scenes/ThreePlayerScene.unity"
            }).ToArray();
            var before = paths.ToDictionary(path => path, File.ReadAllBytes);
            var type = Type.GetType(typeName + ", Assembly-CSharp-Editor", true);
            var validate = type.GetMethod("ValidateReadyForBuild");
            Assert.That(validate, Is.Not.Null, typeName);
            try
            {
                Assert.DoesNotThrow(() => validate.Invoke(null, null), typeName);
            }
            finally
            {
                foreach (var path in paths)
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before[path]),
                        "构建前检查不能覆盖资产：" + path);
            }
        }

        private static void AssertPrefabDependencies(string path, string[] excluded)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            Assert.That(prefab.GetComponentsInChildren<Component>(true).Any(value => value == null),
                Is.False, path + " 含缺失脚本。");
            var dependencies = AssetDatabase.GetDependencies(path, true);
            foreach (var other in excluded)
                Assert.That(dependencies, Does.Not.Contain(other), path);
            foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            {
                var validate = component.GetType().GetMethod("TryValidateConfiguration",
                    new[] { typeof(string).MakeByRefType() });
                if (validate == null || validate.ReturnType != typeof(bool)) continue;
                var arguments = new object[] { null };
                Assert.That((bool)validate.Invoke(component, arguments), Is.True,
                    path + " / " + component.GetType().Name + "：" + arguments[0]);
            }
        }
    }
}
