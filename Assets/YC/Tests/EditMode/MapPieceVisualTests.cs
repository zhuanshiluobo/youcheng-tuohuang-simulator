using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class MapPieceVisualTests
    {
        [Test]
        public void VisibilityAndPlayerColor_AreAppliedThroughSharedRendererPropertyBlock()
        {
            var visualType = Type.GetType("YC.Presentation.MapPieceVisual, Assembly-CSharp", true);
            var root = new GameObject("MapPieceVisual Test");
            var material = new Material(Shader.Find("Standard"));
            try
            {
                var meshObject = new GameObject("Mesh");
                meshObject.transform.SetParent(root.transform, false);
                var renderer = meshObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                var visual = root.AddComponent(visualType);
                var serialized = new SerializedObject(visual);
                var renderers = serialized.FindProperty("renderers");
                renderers.arraySize = 1;
                renderers.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var validationArguments = new object[] { string.Empty };
                Assert.That(
                    visualType.GetMethod("TryValidateConfiguration", BindingFlags.Instance | BindingFlags.Public)
                        .Invoke(visual, validationArguments),
                    Is.True,
                    validationArguments[0] as string);

                visualType.GetMethod("SetVisible", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(visual, new object[] { false });
                Assert.That(renderer.enabled, Is.False);
                visualType.GetMethod("SetVisible", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(visual, new object[] { true });
                Assert.That(renderer.enabled, Is.True);

                var playerColor = new Color(0.25f, 0.5f, 0.75f, 0.9f);
                visualType.GetMethod("SetPlayerColor", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(visual, new object[] { playerColor });
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                AssertColor(block.GetColor(Shader.PropertyToID("_Color")), playerColor);
                AssertColor(
                    block.GetColor(Shader.PropertyToID("_EmissionColor")),
                    new Color(0.02f, 0.04f, 0.06f, 0.9f));
                Assert.That(renderer.sharedMaterial, Is.SameAs(material));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.0001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.0001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.0001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.0001f));
        }
    }
}
