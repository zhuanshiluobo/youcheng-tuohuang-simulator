using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class SteamAvatarViewTests
    {
        private static Type ViewType => Type.GetType("YC.Presentation.SteamAvatarView, Assembly-CSharp", true);
        private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void RebindingAndReopeningClearPreviousAvatarAndRestorePlaceholder()
        {
            var root = new GameObject("头像绑定测试"); root.SetActive(false);
            var imageObject = new GameObject("头像", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(root.transform, false);
            var placeholder = new GameObject("现有占位"); placeholder.transform.SetParent(root.transform, false);
            var image = imageObject.GetComponent<RawImage>();
            var texture = new Texture2D(2, 2);
            try
            {
                var view = root.AddComponent(ViewType);
                Field(view, "avatar").SetValue(view, image);
                Field(view, "emptyAvatar").SetValue(view, placeholder);
                root.SetActive(true);
                void Bind(ulong id, Texture supplied) => ViewType.GetMethod("Bind").Invoke(view, new object[] { id, supplied });
                Bind(10, texture);
                Assert.That(image.texture, Is.SameAs(texture));
                Assert.That(imageObject.activeSelf, Is.True);
                Assert.That(placeholder.activeSelf, Is.False);
                root.SetActive(false);
                Bind(0, null);
                root.SetActive(true);
                Assert.That(image.texture, Is.Null);
                Assert.That(imageObject.activeSelf, Is.False);
                Assert.That(placeholder.activeSelf, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(texture); }
        }

        [TestCase("Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab", 4)]
        [TestCase("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab", 3)]
        [TestCase("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/PlayerSelectionPage.prefab", 1)]
        public void AvatarPrefabsHaveDisplayBindingsWithoutInterceptingClicks(string path, int expected)
        {
            Check(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentsInChildren(ViewType, true), expected);
        }

        [TestCase("Assets/Scenes/StartScene.unity", 4)]
        [TestCase("Assets/Scenes/SampleScene.unity", 3)]
        [TestCase("Assets/Scenes/ThreePlayerScene.unity", 3)]
        public void ActualSceneInstancesRetainAvatarBindings(string path, int expected)
        {
            // 只读检查实际场景实例及其覆盖，不保存场景，不运行任何生成器。
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try { Check(scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(ViewType, true)).ToArray(), expected); }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        private static void Check(Component[] views, int expected)
        {
            Assert.That(views.Length, Is.EqualTo(expected));
            foreach (var view in views)
            {
                Assert.That((bool)ViewType.GetProperty("IsConfigured").GetValue(view), Is.True);
                var image = (RawImage)Field(view, "avatar").GetValue(view);
                Assert.That(image.raycastTarget, Is.False, "头像不应抢占换座、查看城市或选择玩家的点击。");
                Assert.That(image.transform.IsChildOf(view.transform), Is.True);
            }
        }
    }
}
