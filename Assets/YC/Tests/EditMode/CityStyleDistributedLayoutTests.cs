using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class CityStyleDistributedLayoutTests
    {
        private static Type LayoutType(string name) =>
            Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);

        [Test]
        public void VerticalRowsFillHeightWithoutStretchingOrAccumulatingSpacing()
        {
            var root = new GameObject("状态行分布测试", typeof(RectTransform));
            try
            {
                var rect = (RectTransform)root.transform;
                var layout = (VerticalLayoutGroup)root.AddComponent(LayoutType("UiDistributedVerticalLayoutGroup"));
                layout.childControlWidth = layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
                layout.spacing = 4;
                var heights = new[] { 30f, 30f, 30f, 30f, 86f, 86f };
                var rows = heights.Select(height =>
                {
                    var row = new GameObject("状态行", typeof(RectTransform), typeof(LayoutElement));
                    row.transform.SetParent(rect, false);
                    ((RectTransform)row.transform).pivot = new Vector2(0, 1);
                    row.GetComponent<LayoutElement>().minHeight = height;
                    row.GetComponent<LayoutElement>().preferredHeight = height;
                    return (RectTransform)row.transform;
                }).ToArray();
                foreach (var height in new[] { 380f, 308f, 292f, 520f, 340f, 380f })
                {
                    rect.sizeDelta = new Vector2(540, height);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                    var gap = (height - heights.Sum()) / (rows.Length - 1);
                    Assert.That(-rows[0].anchoredPosition.y, Is.EqualTo(0).Within(.01));
                    for (var i = 0; i < rows.Length; i++)
                    {
                        Assert.That(rows[i].rect.height, Is.EqualTo(heights[i]).Within(.01));
                        if (i > 0)
                            Assert.That(rows[i - 1].anchoredPosition.y - rows[i].anchoredPosition.y - heights[i - 1],
                                Is.EqualTo(gap).Within(.01));
                    }
                    Assert.That(-rows.Last().anchoredPosition.y + rows.Last().rect.height,
                        Is.EqualTo(height).Within(.01));
                    Assert.That(layout.spacing, Is.EqualTo(4), "自动分配不应改写继承的序列化间距字段。");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void ScrollContentReservesOnlyRowHeightsBeforeDistributingGaps()
        {
            var viewport = new GameObject("列表视口测试", typeof(RectTransform));
            try
            {
                var viewportRect = (RectTransform)viewport.transform;
                var content = new GameObject("列表内容", typeof(RectTransform));
                var rect = (RectTransform)content.transform;
                rect.SetParent(viewportRect, false);
                var fitter = content.AddComponent(LayoutType("UiFlexibleCanvasContent"));
                fitter.GetType().GetMethod("ConfigureForEditor").Invoke(fitter, new object[] { viewportRect });
                var layout = (VerticalLayoutGroup)content.AddComponent(LayoutType("UiDistributedVerticalLayoutGroup"));
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;
                layout.spacing = 4;
                var heights = new[] { 30f, 30f, 30f, 30f, 86f, 86f };
                foreach (var height in heights)
                {
                    var row = new GameObject("状态行", typeof(RectTransform), typeof(LayoutElement));
                    row.transform.SetParent(rect, false);
                    ((RectTransform)row.transform).pivot = new Vector2(0, 1);
                    var element = row.GetComponent<LayoutElement>();
                    element.minHeight = element.preferredHeight = height;
                }
                foreach (var available in new[] { 380f, 308f, 280f, 308f })
                {
                    viewportRect.sizeDelta = new Vector2(540, available);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                    var expectedHeight = Mathf.Max(available, heights.Sum());
                    Assert.That(LayoutUtility.GetPreferredHeight(rect), Is.EqualTo(heights.Sum()).Within(.01));
                    Assert.That(rect.rect.height, Is.EqualTo(expectedHeight).Within(.01));
                    var first = (RectTransform)rect.GetChild(0);
                    var last = (RectTransform)rect.GetChild(heights.Length - 1);
                    Assert.That(first.anchoredPosition.y, Is.EqualTo(0).Within(.01));
                    Assert.That(-last.anchoredPosition.y + last.rect.height, Is.EqualTo(expectedHeight).Within(.01));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(viewport); }
        }

        [TestCase(2)]
        [TestCase(1)]
        public void ActionGridDistributesSpaceAtBothEndsAndBetweenRows(int columns)
        {
            var root = new GameObject("行动网格分布测试", typeof(RectTransform));
            try
            {
                var rect = (RectTransform)root.transform;
                var layout = (GridLayoutGroup)root.AddComponent(LayoutType("UiDistributedGridLayoutGroup"));
                layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                layout.constraintCount = columns;
                layout.cellSize = new Vector2(100, 30);
                layout.spacing = new Vector2(12, 10);
                layout.padding = new RectOffset(0, 0, 10, 10);
                for (var i = 0; i < 6; i++)
                {
                    var button = (RectTransform)new GameObject("行动按钮", typeof(RectTransform)).transform;
                    button.SetParent(rect, false);
                    button.pivot = new Vector2(0, 1);
                }
                var rows = 6 / columns;
                foreach (var height in new[] { 320f, 500f, 300f, 320f })
                {
                    rect.sizeDelta = new Vector2(300, height);
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                    var gap = (height - rows * 30) / (rows + 1);
                    for (var row = 0; row < rows; row++)
                    {
                        var button = (RectTransform)rect.GetChild(row * columns);
                        Assert.That(button.rect.height, Is.EqualTo(30).Within(.01));
                        Assert.That(-button.anchoredPosition.y, Is.EqualTo(gap + row * (30 + gap)).Within(.01));
                    }
                    Assert.That(layout.spacing.y, Is.EqualTo(10));
                    Assert.That(layout.padding.top, Is.EqualTo(10));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
