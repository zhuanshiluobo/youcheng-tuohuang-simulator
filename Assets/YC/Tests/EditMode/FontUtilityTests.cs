using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YC.Tests.EditMode
{
    public sealed class FontUtilityTests
    {
        private const string CjkAssetPath = "Assets/YC/Resources/Fonts/CJK/NotoSansCJKsc-Regular.otf";
        private const string LatinAssetPath = "Assets/YC/Resources/Fonts/Latin/NotoSans-Regular.ttf";
        private GameObject configurationOwner;

        [SetUp]
        public void SetUp()
        {
            ResetFontUtilityRuntimeState();
            configurationOwner = new GameObject("FontUtility Test Owner");
            ConfigureFontUtility(
                AssetDatabase.LoadAssetAtPath<Font>(CjkAssetPath),
                AssetDatabase.LoadAssetAtPath<Font>(LatinAssetPath),
                configurationOwner);
            DestroyIfExists("YC Font Health Probe");
        }

        [TearDown]
        public void TearDown()
        {
            DestroyIfExists("YC Font Health Probe");
            ResetFontUtilityRuntimeState();
            UnityEngine.Object.DestroyImmediate(configurationOwner);
        }

        [Test]
        public void GetCjkFont_ReturnsConfiguredProjectFont()
        {
            var expectedFont = AssetDatabase.LoadAssetAtPath<Font>(CjkAssetPath);
            Assert.That(expectedFont, Is.Not.Null, "Missing bundled CJK font asset.");

            var font = InvokeGetFont("GetCjkFont", 18);

            Assert.That(font, Is.SameAs(expectedFont));
            Assert.That(GetManagedFontOrigin(font), Is.EqualTo("Serialized:CJK"));
        }

        [Test]
        public void GetLatinFont_ReturnsConfiguredProjectFont()
        {
            var expectedFont = AssetDatabase.LoadAssetAtPath<Font>(LatinAssetPath);
            Assert.That(expectedFont, Is.Not.Null, "Missing bundled Latin font asset.");

            var font = InvokeGetFont("GetLatinFont", 18);

            Assert.That(font, Is.SameAs(expectedFont));
            Assert.That(GetManagedFontOrigin(font), Is.EqualTo("Serialized:Latin"));
        }

        [Test]
        public void GetCjkFont_ThrowsClearlyWhenNoDriverConfigured()
        {
            ResetFontUtilityRuntimeState();

            var error = Assert.Throws<TargetInvocationException>(() => InvokeGetFont("GetCjkFont", 18));

            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain("FontRefreshDriver"));
        }

        [Test]
        public void ReleaseFromOldOwner_DoesNotClearNewDriverConfiguration()
        {
            var newerOwner = new GameObject("New Font Driver Owner");
            try
            {
                var cjk = AssetDatabase.LoadAssetAtPath<Font>(CjkAssetPath);
                var latin = AssetDatabase.LoadAssetAtPath<Font>(LatinAssetPath);
                ConfigureFontUtility(cjk, latin, newerOwner);
                InvokePrivateStaticMethod("Release", configurationOwner);

                Assert.That(InvokeGetFont("GetCjkFont", 18), Is.SameAs(cjk));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(newerOwner);
            }
        }

        [Test]
        public void GetCjkFont_ReusesOneFontAcrossTextSizes()
        {
            var smallFont = InvokeGetFont("GetCjkFont", 12);
            var largeFont = InvokeGetFont("GetCjkFont", 31);

            Assert.That(smallFont, Is.Not.Null);
            Assert.That(largeFont, Is.SameAs(smallFont));
        }

        [Test]
        public void GetLatinFont_ReusesOneFontAcrossTextSizes()
        {
            var smallFont = InvokeGetFont("GetLatinFont", 12);
            var largeFont = InvokeGetFont("GetLatinFont", 31);

            Assert.That(smallFont, Is.Not.Null);
            Assert.That(largeFont, Is.SameAs(smallFont));
        }

        [Test]
        public void RunFontHealthCheck_CapturesManagedFontSnapshot()
        {
            var result = InvokeFontHealthCheckRunner("CheckOnly");
            var snapshot = GetProperty<string>(result, "Snapshot");

            Assert.That(GetProperty<bool>(result, "ProbeObjectsCreated"), Is.True);
            Assert.That(snapshot, Does.Contain("长挂机字体自检快照"));
            Assert.That(snapshot, Does.Contain("YC Font Health Probe"));
            Assert.That(snapshot, Does.Contain("Prompt Text"));
            Assert.That(snapshot, Does.Contain("Serialized:CJK"));
        }

        [Test]
        public void RunFontHealthCheck_SimulatedModeRecreatesManagedFonts()
        {
            var beforeFont = InvokeGetFont("GetCjkFont", 18);
            Assert.That(beforeFont, Is.Not.Null);

            var result = InvokeFontHealthCheckRunner("SimulateRecreate");
            var afterFont = InvokeGetFont("GetCjkFont", 18);
            var snapshot = GetProperty<string>(result, "Snapshot");

            Assert.That(GetProperty<bool>(result, "RecreatedManagedFonts"), Is.True);
            Assert.That(snapshot, Does.Contain("SimulatedInvalidTexture: True"));
            Assert.That(afterFont, Is.Not.Null);
            Assert.That(afterFont, Is.SameAs(beforeFont));
            Assert.That(GetManagedFontOrigin(afterFont), Is.EqualTo("Serialized:CJK"));
        }

        [Test]
        public void OnFontTextureRebuilt_DoesNotTriggerGlobalRefreshOnNormalAtlasUpdates()
        {
            var font = InvokeGetFont("GetCjkFont", 18);
            Assert.That(font, Is.Not.Null);

            InvokePrivateStaticMethod("OnFontTextureRebuilt", font);

            Assert.That(GetPrivateStaticField<bool>("pendingManagedTextRefresh"), Is.False);
            Assert.That(GetPrivateStaticField<bool>("pendingManagedFontRecreate"), Is.False);
        }

        private static Font InvokeGetFont(string methodName, int fontSize)
        {
            var type = GetFontUtilityType();
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing FontUtility." + methodName + ".");

            return method.Invoke(null, new object[] { fontSize }) as Font;
        }

        private static string GetManagedFontOrigin(Font font)
        {
            var type = GetFontUtilityType();
            var method = type.GetMethod("GetManagedFontOrigin", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing FontUtility.GetManagedFontOrigin.");

            return method.Invoke(null, new object[] { font }) as string;
        }

        private static T GetPrivateStaticField<T>(string fieldName)
        {
            var type = GetFontUtilityType();
            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing FontUtility." + fieldName + ".");
            return (T)field.GetValue(null);
        }

        private static object InvokePrivateStaticMethod(string methodName, params object[] args)
        {
            var type = GetFontUtilityType();
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing FontUtility." + methodName + ".");
            return method.Invoke(null, args);
        }

        private static void SetPrivateStaticField(string fieldName, object value)
        {
            var type = GetFontUtilityType();
            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing FontUtility." + fieldName + ".");
            field.SetValue(null, value);
        }

        private static Type GetFontUtilityType()
        {
            var type = Type.GetType("YC.Presentation.FontUtility, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.FontUtility.");
            return type;
        }

        private static object InvokeFontHealthCheckRunner(string modeName)
        {
            var runnerType = Type.GetType("YC.Presentation.FontHealthCheckRunner, Assembly-CSharp", false);
            Assert.That(runnerType, Is.Not.Null, "Missing YC.Presentation.FontHealthCheckRunner.");

            var modeType = Type.GetType("YC.Presentation.FontHealthCheckMode, Assembly-CSharp", false);
            Assert.That(modeType, Is.Not.Null, "Missing YC.Presentation.FontHealthCheckMode.");

            var method = runnerType.GetMethod("Run", BindingFlags.Static | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing FontHealthCheckRunner.Run.");

            var mode = Enum.Parse(modeType, modeName);
            return method.Invoke(null, new[] { mode });
        }

        private static T GetProperty<T>(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, "Missing property " + propertyName + ".");
            return (T)property.GetValue(target, null);
        }

        private static void ResetFontUtilityRuntimeState()
        {
            var type = GetFontUtilityType();
            var method = type.GetMethod("ResetRuntimeState", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing FontUtility.ResetRuntimeState.");
            method.Invoke(null, null);
        }

        private static void ConfigureFontUtility(Font cjk, Font latin, UnityEngine.Object owner)
        {
            Assert.That(cjk, Is.Not.Null, "Missing bundled CJK font asset.");
            Assert.That(latin, Is.Not.Null, "Missing bundled Latin font asset.");
            var type = GetFontUtilityType();
            var method = type.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing FontUtility.Configure.");
            method.Invoke(null, new object[] { cjk, latin, owner });
        }

        private static void DestroyIfExists(string objectName)
        {
            var target = GameObject.Find(objectName);
            if (target == null)
            {
                return;
            }

            UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
