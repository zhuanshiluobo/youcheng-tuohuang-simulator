using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class InformationPagePrefabTests
    {
        [Test]
        public void HudReferencesBothPageAssetsAndCompleteViewBindings()
        {
            var hud=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var type=Type.GetType("YC.Presentation.GameplayInformationPages, Assembly-CSharp",true);
            var component=hud.GetComponent(type);Assert.That(component,Is.Not.Null);
            var args=new object[]{null};
            Assert.That((bool)type.GetMethod("TryValidateConfiguration").Invoke(component,args),Is.True,args[0]?.ToString());
            foreach(var field in new[]{"regionPrefab","gameBoxPrefab"})
            {
                var page=(Component)type.GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(component);
                Assert.That(AssetDatabase.GetAssetPath(page),Does.StartWith("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Information/"));
                Assert.That(page.gameObject.activeSelf,Is.False,"进入主界面不会自动打开信息页。");
                Assert.That(page.GetComponent<Canvas>().renderMode,Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Assert.That(page.GetComponent<Canvas>().overrideSorting,Is.True);
                Assert.That(page.GetComponent<Canvas>().sortingOrder,Is.LessThan(30000),"信息页在常驻栏下方。");
                Assert.That(page.GetComponent<CanvasScaler>().enabled,Is.False,"复用 PageHost 的缩放。");
                Assert.That(page.GetComponent<VerticalLayoutGroup>(),Is.Not.Null);
                var window = (RectTransform)page.GetType().GetProperty("Window").GetValue(page);
                Assert.That(window.GetComponent<VerticalLayoutGroup>(),Is.Not.Null);
                Assert.That(window.parent.GetComponent<HorizontalLayoutGroup>(),Is.Not.Null);
            }
        }
    }
}
