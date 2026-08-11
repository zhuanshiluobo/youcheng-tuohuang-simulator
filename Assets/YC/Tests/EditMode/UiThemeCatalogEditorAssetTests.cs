using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class UiThemeCatalogEditorAssetTests
    {
        [Test]
        public void CatalogAndPrefab_AreCanonicalAndReady()
        {
            var catalog = LoadRequiredCatalog();
            AssertColor(catalog, "PanelBackground", .08f, .07f, .055f, .94f);
            AssertColor(catalog, "PanelBackgroundLighter", .14f, .1f, .06f, .96f);
            AssertColor(catalog, "SectionTitleBackground", .12f, .09f, .06f, .92f);
            AssertColor(catalog, "ScrollBackground", .06f, .05f, .04f, .5f);
            AssertColor(catalog, "ButtonBackground", .16f, .1f, .055f, .96f);
            AssertColor(catalog, "DisabledButtonBackground", .09f, .075f, .06f, .72f);
            AssertColor(catalog, "GoldText", .86f, .75f, .55f, 1f);
            AssertColor(catalog, "GoldOutline", .78f, .63f, .38f, .85f);
            AssertColor(catalog, "GoldOutlineThin", .78f, .63f, .38f, .65f);
            AssertColor(catalog, "GoldSeparator", .78f, .63f, .38f, .7f);
            AssertColor(catalog, "CyanAccent", .12f, .88f, 1f, 1f);
            AssertColor(catalog, "TacticalMapBackground", .08f, .1f, .12f, 1f);
            AssertColor(catalog, "DarkShadow", .06f, .04f, .025f, .9f);
            AssertColor(catalog, "DarkShadowLight", .06f, .04f, .025f, .95f);
            AssertColor(catalog, "LabelText", .7f, .65f, .55f, 1f);
            AssertColor(catalog, "ValueText", .95f, .9f, .82f, 1f);
            AssertColor(catalog, "TrackBackground", .1f, .1f, .09f, .88f);
            AssertColor(catalog, "DangerBand", .56f, .08f, .06f, .95f);
            AssertColor(catalog, "SafeBand", .82f, .78f, .67f, .95f);
            AssertColor(catalog, "GameOverOverlay", 0f, 0f, 0f, .62f);
            AssertColor(catalog, "GameOverDialog", .16f, .1f, .055f, .98f);
            Assert.That(Read<Vector2>(catalog, "CanvasReferenceResolution"), Is.EqualTo(new Vector2(1920f, 1080f)));
            Assert.That(Read<float>(catalog, "CanvasMatchWidthOrHeight"), Is.EqualTo(.5f));
            Assert.That(Read<Vector2>(catalog, "DialogActionButtonSize"), Is.EqualTo(new Vector2(220f, 48f)));
            Assert.That(Read<Vector2>(catalog, "CollapsibleMapPromptSize"), Is.EqualTo(new Vector2(650f, 260f)));
            Assert.That(Read<Vector2>(catalog, "ViewerCloseButtonSize"), Is.EqualTo(new Vector2(42f, 42f)));
            Assert.That(Read<Vector2>(catalog, "ViewerCloseButtonOffset"), Is.EqualTo(new Vector2(-18f, -12f)));
            AssertPlayerColor(catalog, "Red", .7019608f, 0f, .1137255f, 1f);
            AssertPlayerColor(catalog, "Blue", .003921569f, .2705882f, .6980392f, .4f);
            AssertPlayerColor(catalog, "Green", .3764706f, .8235294f, .003921569f, 1f);
            AssertPlayerColor(catalog, "Yellow", 1f, .7450981f, 0f, 1f);
            InvokeReadiness("ValidateReadyForBuild");
        }

        [Test]
        public void Theme_FailsFastAndAcceptsOnlyEqualData()
        {
            var theme = RequireType("YC.Presentation.UiTheme");
            InvokeStatic(theme, "ResetForTests");
            AssertInner<InvalidOperationException>(() => ReadStatic(theme, "PanelBackground"));
            var catalog = LoadRequiredCatalog();
            InvokeStatic(theme, "Initialize", catalog);
            InvokeStatic(theme, "Initialize", catalog);
            var different = ScriptableObject.CreateInstance(RequireType("YC.Presentation.UiThemeCatalog"));
            try
            {
                InvokeInstance(different.GetType(), different, "ConfigureCanonicalValuesForEditor");
                var data = new SerializedObject(different);
                data.FindProperty("goldText").colorValue = Color.white;
                data.ApplyModifiedPropertiesWithoutUndo();
                AssertInner<InvalidOperationException>(() => InvokeStatic(theme, "Initialize", different));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(different);
                InvokeStatic(theme, "ResetForTests");
                InvokeStatic(theme, "Initialize", catalog);
            }
        }

        [Test]
        public void SceneYamlGate_RejectsDangerousOverridesAndDuplicateBootstrap()
        {
            var readiness = Type.GetType("YC.Editor.UiThemeBuildReadiness, Assembly-CSharp-Editor", false);
            var validate = readiness.GetMethod("ValidateSavedSceneYaml", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);
            var baseYaml = "--- !u!1001 &1\nPrefabInstance:\n  m_SourcePrefab: {fileID: 100100000, guid: prefab, type: 3}\n";
            AssertYamlRejected(validate, baseYaml + Override(11, "m_IsActive", "0"));
            AssertYamlRejected(validate, baseYaml + Override(22, "m_Enabled", "0"));
            AssertYamlRejected(validate, baseYaml + CatalogOverride(22, "0", string.Empty));
            AssertYamlRejected(validate, baseYaml + CatalogOverride(22, "44", "0123456789abcdef0123456789abcdef"));
            AssertYamlRejected(validate, baseYaml + "  m_RemovedComponents:\n  - {fileID: 22, guid: prefab, type: 3}\n  m_RemovedGameObjects: []\n");
            AssertYamlRejected(validate, baseYaml + "--- !u!114 &2\nMonoBehaviour:\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_Script: {fileID: 11500000, guid: script, type: 3}\n");
        }

        private static object LoadRequiredCatalog() => InvokeReadiness("LoadRequiredCatalog");
        private static string Override(long id, string property, string value) => "    - target: {fileID: " + id + ", guid: prefab, type: 3}\n      propertyPath: " + property + "\n      value: " + value + "\n      objectReference: {fileID: 0}\n";
        private static string CatalogOverride(long id, string reference, string guid) => "    - target: {fileID: " + id + ", guid: prefab, type: 3}\n      propertyPath: themeCatalog\n      value: \n      objectReference: " + (reference == "0" ? "{fileID: 0}" : "{fileID: " + reference + ", guid: " + guid + ", type: 2}") + "\n";
        private static void AssertYamlRejected(MethodInfo method, string yaml) { var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { "test", yaml, "prefab", 11L, 22L, "catalog", 33L, "script" })); Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>()); }
        private static void AssertPlayerColor(object catalog, string player, float r, float g, float b, float a)
        {
            var enumType = Type.GetType("YC.Domain.Rules.PlayerColor, YC.Domain", false);
            Assert.That(enumType, Is.Not.Null, "YC.Domain.Rules.PlayerColor");
            var color = (Color)catalog.GetType().GetMethod("GetPlayerColor").Invoke(catalog, new[] { Enum.Parse(enumType, player), (object)a });
            Assert.That(color, Is.EqualTo(new Color(r, g, b, a)));
        }
        private static void AssertColor(object source, string property, float r, float g, float b, float a) => Assert.That(Read<Color>(source, property), Is.EqualTo(new Color(r, g, b, a)), property);
        private static T Read<T>(object source, string property) => (T)source.GetType().GetProperty(property).GetValue(source, null);
        private static object ReadStatic(Type type, string property) => type.GetProperty(property, BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        private static Type RequireType(string name) { var type = Type.GetType(name + ", Assembly-CSharp", false); Assert.That(type, Is.Not.Null, name); return type; }
        private static object InvokeReadiness(string method) { var type = Type.GetType("YC.Editor.UiThemeBuildReadiness, Assembly-CSharp-Editor", false); Assert.That(type, Is.Not.Null); return InvokeStatic(type, method); }
        private static object InvokeStatic(Type type, string methodName, params object[] arguments) { var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static); Assert.That(method, Is.Not.Null, methodName); return method.Invoke(null, arguments); }
        private static object InvokeInstance(Type type, object target, string methodName, params object[] arguments) { var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance); Assert.That(method, Is.Not.Null, methodName); return method.Invoke(target, arguments); }
        private static void AssertInner<T>(TestDelegate action) where T : Exception { var exception = Assert.Throws<TargetInvocationException>(action); Assert.That(exception.InnerException, Is.TypeOf<T>()); }
    }
}
