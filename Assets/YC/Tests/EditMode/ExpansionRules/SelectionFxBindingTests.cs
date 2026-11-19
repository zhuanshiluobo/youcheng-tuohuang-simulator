using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 只读取和实例化正式源预制体；不保存或重建界面。
    public sealed class SelectionFxBindingTests
    {
        private const BindingFlags Public = BindingFlags.Instance | BindingFlags.Public;
        private static Type Presentation(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static Type Graphic => Type.GetType("NoMadCity.SelectionFX.SelectionEffectGraphic, Assembly-CSharp", true);
        private static GameObject Load(string path)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(source, Is.Not.Null, path);
            return UnityEngine.Object.Instantiate(source);
        }
        private static string State(Component graphic) => Graphic.GetProperty("State").GetValue(graphic).ToString();

        [Test]
        public void SelectionShaderImportsWithoutCompilerErrorsAndHasUiStencilProperties()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/NoMadCitySelectionFX/Resources/NoMadCitySelection.shader");
            Assert.That(shader, Is.Not.Null);
            var messages = ShaderUtil.GetShaderMessages(shader).Where(message => message.severity.ToString() == "Error").ToArray();
            Assert.That(messages, Is.Empty, string.Join("\n", messages.Select(message => message.message)));
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            try
            {
                foreach (string property in new[] { "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask" })
                    Assert.That(material.HasProperty(property), Is.True, property);
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [TestCase(true, false, "Selection FX Company")]
        [TestCase(false, false, "Selection FX Effect Row")]
        [TestCase(true, true, "Selection FX Department")]
        public void EnterpriseTargetShowsOnlyTheMatchingCurrentSelection(bool company, bool department, string expected)
        {
            var root = Load("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Enterprise/EnterprisePickerTarget.prefab");
            try
            {
                root.SetActive(true);
                var view = root.GetComponent(Presentation("EnterpriseTargetView"));
                var bind = view.GetType().GetMethod("Bind", Public);
                void Bind(bool available, bool selected, bool readOnly) => bind.Invoke(view, new object[] {
                    new Rect(0, 0, 1, 1), available, selected, false, (Action)(() => { }),
                    readOnly, root.transform as RectTransform, company, null, null, department });
                Bind(true, true, false);
                var effects = root.GetComponentsInChildren(Graphic, true).Cast<Component>().ToArray();
                Assert.That(effects, Has.Length.EqualTo(3));
                Assert.That(effects.Count(effect => effect.gameObject.activeInHierarchy), Is.EqualTo(1));
                var current = effects.Single(effect => effect.gameObject.activeInHierarchy);
                Assert.That(current.name, Is.EqualTo(expected));
                Assert.That(State(current), Is.EqualTo("Selected"));
                var frame = (GameObject)view.GetType().GetField("stateFrame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                Assert.That(frame.activeInHierarchy, Is.True, "状态框承载特效，不能隐藏整个对象。");
                Assert.That(frame.GetComponent<Image>().enabled, Is.False, "选中时不叠加旧状态贴图。");
                Bind(false, false, false);
                Assert.That(State(current), Is.EqualTo("Unavailable"));
                Assert.That(frame.GetComponent<Image>().enabled, Is.True, "不可选状态仍使用原状态框。");
                Bind(true, false, true);
                Assert.That(effects.Any(effect => effect.gameObject.activeInHierarchy), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [TestCase("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CardSelectionSlot.prefab")]
        [TestCase("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab")]
        public void CardEffectTracksSelectionAndHidesForInspection(string path)
        {
            var root = Load(path);
            try
            {
                var view = root.GetComponentInChildren(Presentation("FacilityEffectCardView"), true);
                Assert.That(view, Is.Not.Null);
                var effect = view.GetComponentsInChildren(Graphic, true).Cast<Component>().Single();
                var set = view.GetType().GetMethod("SetSelectionEffect", Public);
                set.Invoke(view, new object[] { true, true, true });
                Assert.That(effect.gameObject.activeSelf, Is.True);
                Assert.That(State(effect), Is.EqualTo("Selected"));
                set.Invoke(view, new object[] { true, false, false });
                Assert.That(State(effect), Is.EqualTo("Unavailable"));
                set.Invoke(view, new object[] { false, false, false });
                Assert.That(effect.gameObject.activeSelf, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void CityStyleEffectUsesDeclarationAvailabilityAndSelection()
        {
            var root = Load("Assets/YC/Presentation/Prefabs/Gameplay/CityStyles/CityStyleCardItem.prefab");
            try
            {
                var view = root.GetComponent(Presentation("CityStyleCardStateView"));
                var effect = root.GetComponentsInChildren(Graphic, true).Cast<Component>().Single();
                view.GetType().GetMethod("Bind", Public).Invoke(view, new object[] { false });
                Assert.That(State(effect), Is.EqualTo("Unavailable"));
                view.GetType().GetMethod("Bind", Public).Invoke(view, new object[] { true });
                view.GetType().GetMethod("SetSelected", Public).Invoke(view, new object[] { true });
                Assert.That(State(effect), Is.EqualTo("Selected"));
                view.GetType().GetMethod("SetSelectionEffectEnabled", Public).Invoke(view, new object[] { false });
                Assert.That(effect.gameObject.activeSelf, Is.False, "只读查看不能显示宣告候选效果。");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void V3DrawsOnlySelectedAndClearsMeshWhenSelectionChanges()
        {
            var root = Load("Assets/YC/Presentation/Prefabs/Gameplay/CityStyles/CityStyleCardItem.prefab");
            try
            {
                var view = root.GetComponent(Presentation("CityStyleCardStateView"));
                var effect = root.GetComponentsInChildren(Graphic, true).Cast<Component>().Single();
                var meshMethod = Graphic.GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(VertexHelper) }, null);
                int Vertices()
                {
                    using var vertices = new VertexHelper();
                    meshMethod.Invoke(effect, new object[] { vertices });
                    return vertices.currentVertCount;
                }
                view.GetType().GetMethod("Bind", Public).Invoke(view, new object[] { true });
                Assert.That(Vertices(), Is.Zero, "未选择时不增加任何框。");
                view.GetType().GetMethod("SetSelected", Public).Invoke(view, new object[] { true });
                Assert.That(Vertices(), Is.EqualTo(4));
                view.GetType().GetMethod("SetSelected", Public).Invoke(view, new object[] { false });
                Assert.That(Vertices(), Is.Zero, "取消选择后必须清除网格。");
                view.GetType().GetMethod("SetSelected", Public).Invoke(view, new object[] { true });
                Assert.That(Vertices(), Is.EqualTo(4));
                view.GetType().GetMethod("Bind", Public).Invoke(view, new object[] { false });
                Assert.That(Vertices(), Is.Zero, "不可选时不保留额外框。");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
