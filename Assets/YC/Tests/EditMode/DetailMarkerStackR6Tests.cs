using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    // 交根统一运行的原Prefab组件检查；只核相对排布/复用，不锁用户静态坐标、字号或行高值。
    public sealed class DetailMarkerStackR6Tests
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const string Path = "Assets/YC/Presentation/Effects/Prefabs/EffectRow.prefab";
        static object Read(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
        static Array Markers(Type rowType, int count)
        {
            var type = rowType.GetNestedType("Marker"); var result = Array.CreateInstance(type, count);
            for (int i = 0; i < count; i++)
            {
                var marker = Activator.CreateInstance(type);
                type.GetField("Mask").SetValue(marker, 1 << i); type.GetField("Count").SetValue(marker, i);
                result.SetValue(marker, i);
            }
            return result;
        }
        static void Bind(Component row, Array markers, Action<string> details = null)
        {
            var type = row.GetType(); var mode = type.GetNestedType("Mode"); var status = type.GetNestedType("Status");
            type.GetMethod("Bind", new[] { typeof(string), typeof(string), typeof(int), mode, status, typeof(bool), typeof(bool),
                typeof(string), typeof(string), markers.GetType(), typeof(Func<string, int, bool>), typeof(Action<string>), typeof(Action<string>) })
                .Invoke(row, new object[] { "r6.detail", "r6.request", 1, Enum.Parse(mode, "PersistentDetail"), Enum.Parse(status, "None"), false, false,
                    "标记布局组件样本", "仅为组件输入的长说明，用于确认文字区不会横向压缩标记。", markers,
                    new Func<string, int, bool>((id, revision) => id == "r6.request" && revision == 1), details, null });
        }
        static void Reflow(RectTransform row)
        { LayoutRebuilder.ForceRebuildLayoutImmediate(row); Canvas.ForceUpdateCanvases(); LayoutRebuilder.ForceRebuildLayoutImmediate(row); }
        [TestCase(1)] [TestCase(2)]
        public void OriginalDetailRowStacksMarkersInsideUnchangedRowHeight(int count)
        {
            var canvas = new GameObject("R6组件Canvas", typeof(RectTransform), typeof(Canvas));
            var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Path), canvas.transform, false);
            try
            {
                var type = Type.GetType("YC.Presentation.UiEffectRowView, Assembly-CSharp", true); var row = instance.GetComponent(type);
                var root = (RectTransform)instance.transform; var stack = (RectTransform)Read(row, "markerStack");
                var slots = (RectTransform[])Read(row, "markerSlots"); var originalHeight = root.rect.height; var originalWidth = slots[0].rect.width;
                Assert.That(stack, Is.Not.Null); Assert.That(slots.All(slot => slot.parent == stack), Is.True);
                int details = 0; Bind(row, Markers(type, count), _ => details++); Reflow(root);
                Assert.That(stack.gameObject.activeSelf, Is.True); Assert.That(slots.Count(slot => slot.gameObject.activeSelf), Is.EqualTo(count));
                Assert.That(root.rect.height, Is.EqualTo(originalHeight).Within(.1f));
                Assert.That(slots[0].rect.width, Is.EqualTo(originalWidth).Within(.1f));
                if (count == 2)
                {
                    Assert.That(slots[0].anchoredPosition.x, Is.EqualTo(slots[1].anchoredPosition.x).Within(.1f));
                    Assert.That(slots[0].anchoredPosition.y, Is.GreaterThan(slots[1].anchoredPosition.y));
                    Assert.That(slots[0].rect.height + slots[1].rect.height, Is.LessThanOrEqualTo(stack.rect.height));
                }
                root.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, root.rect.width * .6f); Reflow(root);
                Assert.That(slots[0].rect.width, Is.EqualTo(originalWidth).Within(.1f), "正文变窄不缩放标记");
                ((Button)Read(row, "bodyButton")).onClick.Invoke(); Assert.That(details, Is.EqualTo(1));
                instance.SetActive(false); instance.SetActive(true); Reflow(root);
                Assert.That(slots.Count(slot => slot.gameObject.activeSelf), Is.EqualTo(count));
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); UnityEngine.Object.DestroyImmediate(canvas); }
        }
        [Test] public void ClearAndRebindKeepExistingTwoSlotCapacityAndInputIdentity()
        {
            var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Path));
            try
            {
                var type = Type.GetType("YC.Presentation.UiEffectRowView, Assembly-CSharp", true); var row = instance.GetComponent(type);
                var stack = (RectTransform)Read(row, "markerStack"); var slots = (RectTransform[])Read(row, "markerSlots");
                var originalIds = slots.Select(slot => slot.GetInstanceID()).ToArray();
                Bind(row, Markers(type, 2)); type.GetMethod("ClearBinding").Invoke(row, null);
                Assert.That(stack.gameObject.activeSelf, Is.False); Assert.That(slots.All(slot => !slot.gameObject.activeSelf), Is.True);
                Bind(row, Markers(type, 0)); Assert.That(stack.gameObject.activeSelf, Is.False);
                // 生产合同现有两个槽，三项输入不被本次布局代码修改；不据此宣称第三项已显示或已获新容量合同。
                var input = Markers(type, 3); Bind(row, input);
                Assert.That(input.Length, Is.EqualTo(3)); Assert.That(slots.Select(slot => slot.GetInstanceID()), Is.EqualTo(originalIds));
                Assert.That(slots.All(slot => slot.gameObject.activeSelf), Is.True);
                Bind(row, Markers(type, 1)); Assert.That(slots[0].gameObject.activeSelf, Is.True); Assert.That(slots[1].gameObject.activeSelf, Is.False);
                Assert.That(((Text[])Read(row, "markerCounts"))[0].text, Is.EqualTo("0"), "零库存不隐藏既有标记");
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
}
