#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace YC.Tests.PlayMode
{
    public sealed class EffectRowPlayModeTests
    {
        [UnityTest]
        public IEnumerator ExecutionRow_SeparatesSourceAndKeepsNumbersInDescription()
        {
            var shellPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab");
            var shellType = Type.GetType("YC.Presentation.EffectDialogShellView, Assembly-CSharp", true);
            var template = (Component)Field(shellPrefab.GetComponent(shellType), "executionRowTemplate");
            Assert.That(template, Is.Not.Null);
            var host = new GameObject("Execution Row Layout Fixture", typeof(RectTransform), typeof(Canvas));
            var instance = UnityEngine.Object.Instantiate(template.gameObject, host.transform, false);
            try
            {
                var rect = (RectTransform)instance.transform;
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 600);
                instance.SetActive(true);
                var row = instance.GetComponent(Type.GetType("YC.Presentation.EffectDialogOptionRowView, Assembly-CSharp", true));
                row.GetType().GetMethod("SetSourceAndDescription").Invoke(row,
                    new object[] { "军工化区域", "放置 3 个影响力。\n最多 12 个。" });
                // 字体片段由运行时 MonoBehaviour 生成，先经过真实生命周期。
                yield return null;
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Canvas.ForceUpdateCanvases();
                var source = (Text)Field(row, "sourceLabel");
                var description = (Text)Field(row, "label");
                Assert.That(source.text, Is.EqualTo("军工化区域"));
                Assert.That(description.text, Does.Not.Contain(source.text));
                var semantic = description.GetComponent(Type.GetType("YC.Presentation.UiSemanticLabel, Assembly-CSharp", true));
                var fonts = Field(semantic, "fonts");
                Assert.That(source.font, Is.SameAs(fonts.GetType().GetProperty("Emphasis").GetValue(fonts)));
                var texts = description.GetComponentsInChildren<Text>();
                Text first = null, number = null, nextLineNumber = null;
                foreach (var text in texts)
                {
                    if (text.text == "放") first = text;
                    if (text.text == "3") number = text;
                    if (text.text == "12") nextLineNumber = text;
                }
                Assert.That(first, Is.Not.Null);
                Assert.That(number, Is.Not.Null);
                Assert.That(nextLineNumber, Is.Not.Null);
                Assert.That(number.font, Is.SameAs(fonts.GetType().GetProperty("EffectNumber").GetValue(fonts)));
                Assert.That(number.rectTransform.anchoredPosition.y,
                    Is.EqualTo(first.rectTransform.anchoredPosition.y).Within(.1f), "数字应跟随正文首行。");
                Assert.That(nextLineNumber.rectTransform.anchoredPosition.y,
                    Is.LessThan(number.rectTransform.anchoredPosition.y), "描述中的显式换行应保留。");
                var sourceCorners = new Vector3[4]; var bodyCorners = new Vector3[4];
                source.rectTransform.GetWorldCorners(sourceCorners);
                description.rectTransform.GetWorldCorners(bodyCorners);
                Assert.That(sourceCorners[1].y, Is.EqualTo(bodyCorners[1].y).Within(.1f));
                Assert.That(sourceCorners[2].x, Is.EqualTo(bodyCorners[1].x).Within(.1f), "左右文本框应相邻。");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        private static object Field(object owner, string name) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    }
}
#endif
