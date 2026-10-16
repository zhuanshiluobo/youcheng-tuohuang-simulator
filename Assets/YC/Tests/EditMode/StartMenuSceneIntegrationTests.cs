using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    // 打开实际场景以包含实例覆盖；只读验证，不重建或保存界面资产。
    public sealed class StartMenuSceneIntegrationTests
    {
        [Test]
        public void StartScenePreservesRoomConfigurationAndDefersMatchSaves()
        {
            WithScene("Assets/Scenes/StartScene.unity", scene =>
            {
                var view = Find(scene, "StartMenuView");
                var arguments = new object[] { null };
                Assert.That((bool)view.GetType().GetMethod("TryValidateConfiguration").Invoke(view, arguments),
                    Is.True, arguments[0] as string);
                var load = (Button)Property(view, "LoadGameButton");
                Assert.That(load, Is.Not.Null);
                Assert.That(load.gameObject.activeSelf, Is.False, "存读档暂缓期间不开放入口");
                var room = Property(view, "RoomPanel");
                foreach (var group in new[] { "maps", "ruleModes", "expansions", "seats" })
                    Assert.That(((Array)Field(room, group)).Length, Is.GreaterThan(0), group);
                foreach (var property in new[] { "StartGameButton", "OnlineModeButton", "AchievementsButton" })
                    Assert.That((Button)Property(view, property), Is.Not.Null, property);
                var message = Property(view, "MessagePanel");
                foreach (var property in new[] { "OperationErrorActionLabel", "RoomDisbandedActionLabel" })
                    Assert.That((string)Property(message, property), Is.Not.Null.And.Not.Empty, property);
                foreach (var property in new[] { "CreatorSiteButton", "OfficialSiteButton", "WikiButton" })
                {
                    var button = (Button)Property(view, property);
                    Assert.That(button.GetComponent(PresentationType("BookmarkLinkHover")), Is.Not.Null, property);
                }
            });
        }

        [Test]
        public void CollectionSceneHasUsableCatalogAndDisplayReferences()
        {
            WithScene("Assets/Scenes/CollectionRoom.unity", scene =>
            {
                var controller = Find(scene, "CollectionRoomController");
                foreach (var name in new[] { "modelPivot", "previewCamera", "keyLight", "spotlightArea",
                    "dragController", "backButton", "resetViewButton", "catalog", "itemViewPrefab", "listContent" })
                    Assert.That((UnityEngine.Object)Field(controller, name), Is.Not.Null, name);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var visibleCount = 0;
                var catalog = Field(controller, "catalog");
                foreach (var item in (IEnumerable)Property(catalog, "Items"))
                {
                    Assert.That((UnityEngine.Object)item, Is.Not.Null);
                    var id = (string)Property(item, "ItemId");
                    Assert.That(id, Is.Not.Null.And.Not.Empty);
                    Assert.That(ids.Add(id), Is.True, "藏品标识重复：" + id);
                    if ((bool)Property(item, "DefaultDisplay")) visibleCount++;
                    // 资料先行的藏品允许尚无模型；已有模型必须完整可渲染。
                    var model = (GameObject)Property(item, "DisplayPrefab");
                    if (model == null) continue;
                    AssertNoMissingScripts(model);
                    foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                        foreach (var material in renderer.sharedMaterials)
                        {
                            Assert.That(material, Is.Not.Null, id + "/" + renderer.name);
                            Assert.That(material.shader, Is.Not.Null, material.name);
                        }
                }
                Assert.That(visibleCount, Is.GreaterThan(0));
            });
        }

        private static void WithScene(string path, Action<Scene> inspect)
        {
            var existing = SceneManager.GetSceneByPath(path);
            var scene = existing.isLoaded ? existing : EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                foreach (var root in scene.GetRootGameObjects()) AssertNoMissingScripts(root);
                inspect(scene);
            }
            finally
            {
                if (!existing.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void AssertNoMissingScripts(GameObject root) =>
            Assert.That(root.GetComponentsInChildren<Component>(true), Is.All.Not.Null, root.name);
        private static Type PresentationType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static Component Find(Scene scene, string name) => scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren(PresentationType(name), true)).Single();
        private static object Property(object target, string name) => target.GetType().GetProperty(name).GetValue(target);
        private static object Field(object target, string name) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
}
