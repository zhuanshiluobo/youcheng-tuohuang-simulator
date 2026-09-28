using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YC.Tests.EditMode
{
    internal static class ViewerPrefabTestUtility
    {
        public const string ZoomablePrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameZoomableImageViewer.prefab";
        public const string RulebookPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameRulebookViewer.prefab";
        public const string ActionLogPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameActionLogViewer.prefab";
        public const string InGameSettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameSettingsMenu.prefab";
        public const string SharedSettingsPrefabPath =
            "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";

        public static GameObject Instantiate(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, "Missing editor-authored viewer prefab: " + path);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Assert.That(instance, Is.Not.Null, path);
            return instance;
        }

        public static void RegisterZoomablePrefab()
        {
            var type = GetControllerType("YC.Presentation.ZoomableImageViewerController");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ZoomablePrefabPath);
            Assert.That(prefab, Is.Not.Null);
            var controller = prefab.GetComponent(type);
            Assert.That(controller, Is.Not.Null);
            var register = type.GetMethod("RegisterPrefab", BindingFlags.Static | BindingFlags.Public);
            Assert.That(register, Is.Not.Null);
            register.Invoke(null, new object[] { controller });
        }

        public static Type GetControllerType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing " + fullName + ".");
            return type;
        }
    }
}
