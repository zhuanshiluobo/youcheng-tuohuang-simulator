using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class Ui002FontRolesTests
    {
        [Test]
        public void RecreateManagedTextPreservesFiveSemanticFonts()
        {
            var roleType = Type.GetType("YC.Presentation.UiFontRoles, Assembly-CSharp");
            var roleEnum = Type.GetType("YC.Presentation.UiFontRole, Assembly-CSharp");
            var utility = Type.GetType("YC.Presentation.FontUtility, Assembly-CSharp");
            Assert.That(roleType, Is.Not.Null);
            Assert.That(roleEnum, Is.Not.Null);
            Assert.That(utility, Is.Not.Null);
            var roles = AssetDatabase.LoadAssetAtPath(
                "Assets/YC/Presentation/Ui002/Content/UiFontRoles.asset", roleType);
            Assert.That(roles, Is.Not.Null);
            var arguments = new object[] { null };
            Assert.That((bool)roleType.GetMethod("TryValidate").Invoke(roles, arguments), Is.True, arguments[0] as string);
            var owner = new GameObject("UI002 font owner");
            var labels = new GameObject[5];
            var configure = utility.GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(Font), typeof(Font), roleType, typeof(UnityEngine.Object) }, null);
            var refresh = utility.GetMethod("RefreshManagedTextRenderers", BindingFlags.NonPublic | BindingFlags.Static);
            var release = utility.GetMethod("Release", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(configure, Is.Not.Null);
            Assert.That(refresh, Is.Not.Null);
            Assert.That(release, Is.Not.Null);
            try
            {
                var cjk = AssetDatabase.LoadAssetAtPath<Font>("Assets/YC/Resources/Fonts/CJK/NotoSansCJKsc-Regular.otf");
                var latin = AssetDatabase.LoadAssetAtPath<Font>("Assets/YC/Resources/Fonts/Latin/NotoSans-Regular.ttf");
                configure.Invoke(null, new object[] { cjk, latin, roles, owner });
                var sample = new[] { "选择，", "确认", "策略计谋", "2", "3" };
                for (var i = 0; i < labels.Length; i++)
                {
                    labels[i] = new GameObject("UI002 role " + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                    var text = labels[i].GetComponent<Text>();
                    text.text = sample[i];
                    text.font = (Font)roleType.GetMethod("Get").Invoke(roles, new[] { Enum.ToObject(roleEnum, i) });
                    text.fontStyle = FontStyle.Normal;
                }
                refresh.Invoke(null, new object[] { true });
                for (var i = 0; i < labels.Length; i++)
                {
                    var text = labels[i].GetComponent<Text>();
                    var expected = roleType.GetMethod("Get").Invoke(roles, new[] { Enum.ToObject(roleEnum, i) });
                    Assert.That(text.font, Is.SameAs(expected), "Role " + i + " collapsed");
                    Assert.That(text.fontStyle, Is.EqualTo(FontStyle.Normal));
                }
            }
            finally
            {
                release.Invoke(null, new object[] { owner });
                foreach (var label in labels) if (label != null) UnityEngine.Object.DestroyImmediate(label);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }
    }
}
