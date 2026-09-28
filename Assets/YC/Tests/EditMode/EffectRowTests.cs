using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class EffectRowTests
    {
        [Test]
        public void SharedRow_GuardsExecutionAndSeparatesBodyFromStatus()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab");
            Assert.That(prefab, Is.Not.Null);
            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                var rowType = Type.GetType("YC.Presentation.UiEffectRowView, Assembly-CSharp", true);
                var row = instance.GetComponent(rowType);
                Assert.That(row, Is.Not.Null);
                var validation = new object[] { null };
                Assert.That((bool)rowType.GetMethod("TryValidateConfiguration")
                    .Invoke(row, validation), Is.True, validation[0] as string);
                var body = (Button)Field(row, "bodyButton");
                var status = (Button)Field(row, "statusButton");
                var markerType = rowType.GetNestedType("Marker");
                var markers = Array.CreateInstance(markerType, 1);
                var marker = Activator.CreateInstance(markerType);
                markerType.GetField("Mask").SetValue(marker, 1);
                markerType.GetField("Count").SetValue(marker, 0);
                markers.SetValue(marker, 0);
                var mode = Enum.Parse(rowType.GetNestedType("Mode"), "PersistentDetail");
                var use = Enum.Parse(rowType.GetNestedType("Status"), "Use");
                var details = 0;
                var commands = 0;
                var current = true;
                var bind = rowType.GetMethod("Bind", new[] {
                    typeof(string), typeof(string), typeof(int), rowType.GetNestedType("Mode"),
                    rowType.GetNestedType("Status"), typeof(bool), typeof(bool),
                    typeof(string), typeof(string), markers.GetType(),
                    typeof(Func<string, int, bool>), typeof(Action<string>),
                    typeof(Action<string>) });
                object[] args = { "item-a", "request-a", 4, mode, use, false, false,
                    "标题", "说明", markers,
                    new Func<string, int, bool>((id, revision) => current &&
                        id == "request-a" && revision == 4),
                    new Action<string>(id => details++),
                    new Action<string>(id => commands++) };
                bind.Invoke(row, args);
                status.onClick.Invoke();
                Assert.That(commands, Is.Zero, "显式禁用的 use 不得执行");
                body.onClick.Invoke();
                Assert.That(details, Is.EqualTo(1));
                Assert.That(commands, Is.Zero, "正文查看不得执行");
                Assert.That(((Text[])Field(row, "markerCounts"))[0].text, Is.EqualTo("0"));

                args[5] = true;
                bind.Invoke(row, args);
                status.onClick.Invoke();
                Assert.That(commands, Is.EqualTo(1));
                current = false;
                status.onClick.Invoke();
                Assert.That(commands, Is.EqualTo(1), "过期请求不得执行");
                current = true;
                args[6] = true;
                bind.Invoke(row, args);
                status.onClick.Invoke();
                Assert.That(commands, Is.EqualTo(1), "pending 不得重复执行");
                rowType.GetMethod("ClearBinding").Invoke(row, null);
                status.onClick.Invoke();
                Assert.That(commands, Is.EqualTo(1), "复用后旧监听必须清除");

                var bindingType = rowType.GetNestedType("Binding");
                var quoteType = rowType.GetNestedType("QuoteState");
                var data = Activator.CreateInstance(bindingType);
                bindingType.GetField("StableItemId").SetValue(data, "item-b");
                bindingType.GetField("RequestId").SetValue(data, "request-a");
                bindingType.GetField("Revision").SetValue(data, 4);
                bindingType.GetField("RowMode").SetValue(data, mode);
                bindingType.GetField("RightStatus").SetValue(data, use);
                bindingType.GetField("Enabled").SetValue(data, true);
                bindingType.GetField("Quote").SetValue(data, Enum.Parse(quoteType, "Waiting"));
                bindingType.GetField("Source").SetValue(data, "公开效果");
                rowType.GetMethod("Bind", new[] { bindingType,
                    typeof(Func<string, int, bool>), typeof(Action<string>),
                    typeof(Action<string>) }).Invoke(row,
                    new object[] { data, new Func<string, int, bool>((id, revision) => true),
                        new Action<string>(id => details++), new Action<string>(id => commands++) });
                var detailText = (Text)Field(row, "description");
                Assert.That(detailText.text, Does.Contain("公开效果"));
                Assert.That(detailText.text, Does.Contain("等待正式报价"));
                Assert.That(detailText.text, Does.Not.Contain("费用：0"));
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        [Test]
        public void ExecutionRow_SeparatesSourceAndKeepsNumbersInDescription()
        {
            var shellPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab");
            var shellType = Type.GetType("YC.Presentation.EffectDialogShellView, Assembly-CSharp", true);
            var template = (Component)Field(shellPrefab.GetComponent(shellType), "executionRowTemplate");
            Assert.That(template, Is.Not.Null);
            var host = new GameObject("特殊行动排版夹具", typeof(RectTransform));
            var instance = UnityEngine.Object.Instantiate(template.gameObject, host.transform, false);
            try
            {
                var rect = (RectTransform)instance.transform;
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 600);
                instance.SetActive(true);
                var row = instance.GetComponent(Type.GetType("YC.Presentation.EffectDialogOptionRowView, Assembly-CSharp", true));
                row.GetType().GetMethod("SetSourceAndDescription").Invoke(row,
                    new object[] { "军工化区域", "放置 3 个影响力。\n最多 12 个。" });
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
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
