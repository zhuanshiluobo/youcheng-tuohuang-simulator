using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using System;
using System.Reflection;

namespace YC.Tests.EditMode
{
    public sealed class GameplayDialogLayoutTests
    {
        private GameObject canvasRoot;
        private Component shell;
        [SetUp] public void SetUp()
        {
            canvasRoot = new GameObject("Layout Test", typeof(RectTransform), typeof(Canvas));
            canvasRoot.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvasRoot.transform).sizeDelta = new Vector2(1520, 900);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab");
            shell = UnityEngine.Object.Instantiate(prefab, canvasRoot.transform, false).GetComponent(Type.GetType("YC.Presentation.EffectDialogShellView, Assembly-CSharp", true));
            Call(shell, "PrepareForUse", "Overlay", "Panel", new Vector2(1320, 820), Vector2.zero, true);
            Call(shell, "ConfigureHeading", "测试选择", "同一请求只编辑草稿", 40, "Title", "Description", 28);
        }
        [TearDown] public void TearDown() { UnityEngine.Object.DestroyImmediate(canvasRoot); }
        [Test] public void PendingScroll_DoesNotRetainDestroyedHiddenPage()
        {
            shell.gameObject.SetActive(false);
            Call(shell, "RestoreScrollPosition", .3f);
            UnityEngine.Object.DestroyImmediate(shell.gameObject);
            Assert.DoesNotThrow(Canvas.ForceUpdateCanvases,
                "隐藏页的延后滚动恢复不得持有已销毁的窗口。");
        }
        [TestCase(0)] [TestCase(1)] [TestCase(7)] [TestCase(8)] [TestCase(40)]
        public void Cards_KeepAspectAndScrollAtNarrowWidths_AndRestore(int count)
        {
            Call(shell, "ConfigureCardScroll", new Vector2(240,358), 180f);
            for (int i = 0; i < count; i++) Call(shell, "CreateFacilityCard");
            Refresh();
            var before = Property<RectTransform>(shell, "OptionContent").rect.height;
            var grid = Property<RectTransform>(shell, "OptionContent").GetComponentInParent(Type.GetType("YC.Presentation.UiCardCollectionLayout, Assembly-CSharp", true));
            var columns = Property<int>(grid, "Columns");
            ((RectTransform)canvasRoot.transform).sizeDelta = new Vector2(560, 620);
            Refresh();
            Assert.That(Property<int>(grid, "Columns"), Is.LessThan(columns));
            AssertWithin(Property<RectTransform>(shell, "Panel"), (RectTransform)shell.transform);
            AssertWithin((RectTransform)Property<ScrollRect>(shell, "OptionScroll").transform, Property<RectTransform>(shell, "Panel"));
            foreach (var card in Property<RectTransform>(shell, "OptionContent").GetComponentsInChildren(Type.GetType("YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true)))
            {
                Assert.That(Property<RawImage>(card, "CardImage").rectTransform.rect.width / Property<RawImage>(card, "CardImage").rectTransform.rect.height,
                    Is.EqualTo(12f / 17f).Within(.001f));
                AssertWithin(Property<Button>(card, "DetailsButton").transform as RectTransform, Property<RectTransform>(card, "CardRect"));
            }
            if (count >= 7)
            {
                var scroll = Property<ScrollRect>(shell, "OptionScroll");
                var content = Property<RectTransform>(shell, "OptionContent");
                Assert.That(content.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
                scroll.verticalNormalizedPosition = 0;
                Canvas.ForceUpdateCanvases();
                var last = content.GetChild(content.childCount - 1) as RectTransform;
                Assert.That(Bounds(last).yMin, Is.GreaterThanOrEqualTo(Bounds(scroll.viewport).yMin - .01f),
                    "滚动到末尾后，最后一张卡牌底部及详情按钮必须可见。");
                Assert.That(Bounds(last).yMax, Is.LessThanOrEqualTo(Bounds(scroll.viewport).yMax + .01f));
            }
            ((RectTransform)canvasRoot.transform).sizeDelta = new Vector2(1520, 900);
            Refresh();
            Assert.That(Property<RectTransform>(shell, "OptionContent").rect.height, Is.EqualTo(before).Within(.01f));
        }
        [Test] public void ResourceRows_ReflowWithoutOverlappingControls_AndKeepFooterOutsideScroll()
        {
            for (int i = 0; i < 12; i++)
            {
                var row = (Component)Call(shell, "CreateResourceRow");
                Property<Text>(row, "Label").text = "库存和实际规则价格的较长说明文字，用于验证换行后数量按钮仍可访问 " + i;
                Property<Text>(row, "ValueText").text = i.ToString();
            }
            var button = (Component)Call(shell, "AcquireActionButton", Property<RectTransform>(shell, "ExpandedContent"), true);
            ((RectTransform)canvasRoot.transform).sizeDelta = new Vector2(480, 580);
            Refresh();
            AssertWithin(button.transform as RectTransform, Property<RectTransform>(shell, "Panel"));
            var scrollBounds = Bounds((RectTransform)Property<ScrollRect>(shell, "OptionScroll").transform);
            var buttonBounds = Bounds(button.transform as RectTransform);
            Assert.That(scrollBounds.yMin, Is.GreaterThanOrEqualTo(buttonBounds.yMax));
            foreach (var row in Property<RectTransform>(shell, "OptionContent").GetComponentsInChildren(Type.GetType("YC.Presentation.EffectDialogResourceRowView, Assembly-CSharp", true)))
            {
                AssertWithin(Property<Button>(row, "DecreaseButton").transform as RectTransform, row.transform as RectTransform);
                AssertWithin(Property<Button>(row, "IncreaseButton").transform as RectTransform, row.transform as RectTransform);
                var labelBounds = Bounds(Property<Text>(row, "Label").rectTransform);
                var decreaseBounds = Bounds(Property<Button>(row, "DecreaseButton").transform as RectTransform);
                Assert.That(labelBounds.xMax <= decreaseBounds.xMin + .01f ||
                    labelBounds.yMin >= decreaseBounds.yMax - .01f, Is.True,
                    "长文字与数量操作区不能重叠，允许原生行或列布局。");
            }
        }
        private static T Property<T>(object source, string name) => (T)source.GetType().GetProperty(name).GetValue(source);
        private static object Call(object source, string name, params object[] args) => source.GetType().GetMethod(name).Invoke(source, args);
        private void Refresh()
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)shell.transform);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)shell.transform);
        }
        private static Rect Bounds(RectTransform rect)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }
        private static void AssertWithin(RectTransform child, RectTransform parent)
        {
            var c = Bounds(child); var p = Bounds(parent);
            Assert.That(c.xMin, Is.GreaterThanOrEqualTo(p.xMin - .01f), child.name);
            Assert.That(c.yMin, Is.GreaterThanOrEqualTo(p.yMin - .01f), child.name);
            Assert.That(c.xMax, Is.LessThanOrEqualTo(p.xMax + .01f), child.name);
            Assert.That(c.yMax, Is.LessThanOrEqualTo(p.yMax + .01f), child.name);
        }
    }
}
