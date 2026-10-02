using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class InfluenceMarker2DPrefabTests
    {
        private const string Folder = "Assets/YC/Presentation/Prefabs/Gameplay/Pieces/";
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Property(object target, string name) => target.GetType().GetProperty(name).GetValue(target);
        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static void Validate(object target)
        {
            var args = new object[] { null };
            Assert.That((bool)target.GetType().GetMethod("TryValidateConfiguration").Invoke(target, args), Is.True, args[0] as string);
        }

        [TestCase("InfluenceMarker2D.prefab", false)]
        [TestCase("InfluenceMarker2DNumbered.prefab", true)]
        public void SharedPrefabs_SelectEveryColorNumberModeAndRegionAppearance(string asset, bool numbered)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + asset);
            Assert.That(prefab, Is.Not.Null);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                var square = instance.GetComponent(T("InfluenceMarker2DView"));
                Validate(square);
                Assert.That((bool)Property(square, "ShowNumber"), Is.EqualTo(numbered));
                var catalog = Property(square, "Catalog");
                var entries = (Array)Property(catalog, "Entries");
                Assert.That(entries.Length, Is.EqualTo(Enum.GetValues(T("InfluenceMarker2DColor")).Length));
                var text = (Text)Property(square, "NumberText");
                var image = (Image)Property(square, "Face");
                var rect = (RectTransform)instance.transform;
                var size = rect.sizeDelta;
                var anchor = rect.anchorMin;
                foreach (var entry in entries)
                {
                    var color = entry.GetType().GetField("color").GetValue(entry);
                    foreach (var show in new[] { false, true })
                    {
                        square.GetType().GetMethod("SetAppearance").Invoke(square, new object[] { color, show, 27, false });
                        Assert.That(image.sprite, Is.SameAs(entry.GetType().GetField(show ? "numbered" : "plain").GetValue(entry)));
                        Assert.That(text.gameObject.activeSelf, Is.EqualTo(show));
                        if (show) Assert.That(text.text, Is.EqualTo("27"));
                        var flat = (Sprite)entry.GetType().GetField("flat").GetValue(entry);
                        if (flat == null) continue;
                        square.GetType().GetMethod("SetAppearance").Invoke(square, new object[] { color, show, 0, true });
                        Assert.That(image.sprite, Is.SameAs(flat));
                        Assert.That(text.text, Is.EqualTo("0"), "零数量必须能显示，不应被当作无数字版");
                    }
                }
                Assert.That(rect.sizeDelta, Is.EqualTo(size), "投影不能改写预制体尺寸");
                Assert.That(rect.anchorMin, Is.EqualTo(anchor), "投影不能改写预制体布局");
                foreach (var state in Enum.GetValues(T("InfluenceMarker2DState")))
                {
                    square.GetType().GetMethod("SetState").Invoke(square, new[] { state });
                    var overlay = (Image)Field(square, "stateOverlay");
                    var expected = catalog.GetType().GetMethod("Overlay").Invoke(catalog, new[] { state });
                    if (expected == null) Assert.That(overlay.sprite == null, Is.True);
                    else Assert.That(overlay.sprite, Is.SameAs(expected));
                    Assert.That(overlay.gameObject.activeSelf, Is.EqualTo(expected != null));
                    Assert.That(overlay.raycastTarget, Is.False);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        [Test]
        public void HudSupplyMarkers_UseNumberedPrefabAndNativeResourceSlots()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var modules = prefab.GetComponentInChildren(T("GameplayMainModules"), true);
            Validate(modules);
            var opponents = (Array)Field(modules, "opponentSupplyMarkers");
            Assert.That(opponents.Length, Is.EqualTo(3));
            var markers = new Component[4];
            markers[0] = (Component)Field(modules, "localSupplyMarker");
            opponents.CopyTo(markers, 1);
            foreach (var marker in markers)
            {
                Validate(marker);
                Assert.That((bool)Property(marker, "ShowNumber"), Is.True);
                Assert.That(AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(marker)),
                    Is.EqualTo(Folder + "InfluenceMarker2DNumbered.prefab"));
                Assert.That(marker.transform.parent.GetComponent<UnityEngine.UI.LayoutElement>(), Is.Not.Null);
                Assert.That(marker.transform.parent.parent.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>(), Is.Not.Null);
            }
            var hands = (Text[])Field(modules,"opponentHandCount");
            var styles = (Text[])Field(modules,"opponentStyleCount");
            var names = (Text[])Field(modules,"opponentName");
            var data = (GameObject[])Field(modules,"opponentData");
            var firstNumber = (Text)Property(markers[1],"NumberText");
            for (var i=0;i<3;i++)
            {
                var marker=markers[i+1];var status=marker.transform.parent.parent;
                Assert.That(status,Is.SameAs(hands[i].transform.parent.parent));
                Assert.That(status,Is.SameAs(styles[i].transform.parent.parent));
                Assert.That(marker.transform.parent.GetSiblingIndex(),Is.GreaterThan(styles[i].transform.parent.GetSiblingIndex()),
                    "玩家标记属于手牌、样式这一排的末尾。");
                Assert.That(data[i].GetComponent<VerticalLayoutGroup>(),Is.Not.Null);
                Assert.That(names[i].transform.parent.GetComponent<HorizontalLayoutGroup>(),Is.Not.Null);
                Assert.That(status.parent.GetComponent<VerticalLayoutGroup>(),Is.Not.Null);
                var body=status.parent.parent;
                Assert.That(body.GetComponent<HorizontalLayoutGroup>(),Is.Not.Null);
                Assert.That(body.Find("Portrait Slot"),Is.Not.Null);
                var number=(Text)Property(marker,"NumberText");
                Assert.That(number.resizeTextForBestFit,Is.False,"三个席位的玩家标记不随位数自动缩字号。");
                Assert.That(number.font,Is.SameAs(firstNumber.font));
                Assert.That(number.fontSize,Is.EqualTo(firstNumber.fontSize));
            }
        }

        [Test]
        public void ActualUiAssets_ReferenceNestedSharedMarkersAndOneCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/YC/Presentation/Content/InfluenceMarker2DCatalog.asset");
            Validate(catalog);
            foreach (var path in new[] {
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab",
                "Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.That(prefab, Is.Not.Null, path);
                var markers = prefab.GetComponentsInChildren(T("InfluenceMarker2DView"), true);
                Assert.That(markers, Is.Not.Empty, path);
                foreach (var square in markers)
                {
                    Validate(square);
                    Assert.That(Property(square, "Catalog"), Is.SameAs(catalog));
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(square.gameObject);
                    Assert.That(source, Is.Not.Null, path);
                    Assert.That(AssetDatabase.GetAssetPath(source), Does.StartWith(Folder), "实际方块必须引用独立预制体");
                }
            }
        }
    }
}
