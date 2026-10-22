using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;

namespace YC.Tests.EditMode
{
    // Assembly-CSharp 的布局模块通过真实类型反射；不改变测试程序集引用边界。
    public sealed class GameplayTextLayoutTests
    {
        [TestCase(1f)] [TestCase(2f)] [TestCase(.75f)]
        public void GeneratedPixelMetricsPlaceDifferentFontsOnOneUiBaseline(float pixelsPerUnit)
        {
            float Baseline(float glyphTop, float generatedTop) => (float)TypeOf("").GetMethod("BaselineFromGeneratedGlyph")
                .Invoke(null, new object[] { glyphTop * pixelsPerUnit, generatedTop * pixelsPerUnit, pixelsPerUnit });
            var firstBaseline = Baseline(18, -5);
            var secondBaseline = Baseline(14, 2);
            var items = Items(); Add(items, "甲", 0, 20, false, false); Add(items, "乙", 1, 18, false, false); Add(items, null, 2, 24, true, false);
            Set(items[0], "Ascent", 23f); Set(items[0], "Descent", 6f);
            Set(items[1], "Ascent", 14f); Set(items[1], "Descent", 4f);
            Set(items[2], "Ascent", 19f); Set(items[2], "Descent", 5f);
            var fragments = Fragments(Arrange(items, 100));
            float lineBaseline = Get<float>(fragments[2], "Y") + 19;
            foreach (var pair in new[] { new { Fragment = fragments[0], FontBaseline = firstBaseline }, new { Fragment = fragments[1], FontBaseline = secondBaseline } })
            {
                float top = (float)TypeOf("").GetMethod("TextTopForBaseline").Invoke(null,
                    new object[] { Get<float>(pair.Fragment, "Y") + GetInternalAscent(pair.Fragment), pair.FontBaseline });
                Assert.That(top + pair.FontBaseline, Is.EqualTo(lineBaseline).Within(.0001f));
            }
        }

        [Test] public void OverlayFontFittingUsesAvailableInkAreaAndRetainsLargestConfiguredSize()
        {
            var method = TypeOf("").GetMethod("FitFontSize");
            // 使用真实生产方法和不同宽度的测量输入；最小字号仍为1，小区域确实不能容纳时才到1。
            var helper = GetType().GetMethod(nameof(Fit), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .MakeGenericMethod(TypeOf("+Element"));
            Assert.That((int)helper.Invoke(null, new object[] { method, 24, 32f, 18f, 1.1f }), Is.EqualTo(24));
            Assert.That((int)helper.Invoke(null, new object[] { method, 24, 32f, 18f, 4f }), Is.EqualTo(8));
            Assert.That((int)helper.Invoke(null, new object[] { method, 24, 0f, 0f, 1.1f }), Is.EqualTo(1));
        }

        private static int Fit<T>(System.Reflection.MethodInfo method, int size, float width, float height, float widthFactor)
        {
            Func<int, T> measure = candidate => { var element = Activator.CreateInstance(typeof(T)); Set(element, "Width", candidate * widthFactor); Set(element, "Ascent", candidate * .7f); return (T)element; };
            return (int)method.Invoke(null, new object[] { size, width, height, measure });
        }
        private static float GetInternalAscent(object fragment) => (float)fragment.GetType().GetField("Ascent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(fragment);

        [Test] public void LongTextProducesLineFragmentsInsteadOfCharacterObjects()
        {
            var items = Items(); for (int i = 0; i < 1000; i++) Add(items, "文", 0, 10, false, false);
            var result = Arrange(items, 100); var fragments = Fragments(result);
            Assert.That(fragments.Count, Is.EqualTo(100)); Assert.That(fragments.Cast<object>().All(f => Get<string>(f, "Text").Length == 10), Is.True);
        }
        [Test] public void AtomicScoreFragmentMovesWholeToNextLineAndReportsOversizedWidth()
        {
            var items = Items(); Add(items, "前", 0, 60, false, false); Add(items, null, 1, 50, true, false);
            var result = Arrange(items, 100); var fragments = Fragments(result);
            Assert.That(fragments.Count, Is.EqualTo(2)); Assert.That(Get<float>(fragments[1], "Y"), Is.GreaterThan(Get<float>(fragments[0], "Y")));
            Assert.That(Get<bool>(fragments[1], "Atomic"), Is.True); Assert.That(Get<float>(fragments[1], "Width"), Is.EqualTo(50));
            Assert.That(Get<bool>(Arrange(items, 30), "HasOversizedAtomic"), Is.True);
        }
        [Test] public void HardBreakAndFontGroupsArePreservedWhileSameLineTextMerges()
        {
            var items = Items(); Add(items, "甲", 0, 10, false, false); Add(items, "乙", 0, 10, false, false); Add(items, "策略", 1, 20, false, false); Add(items, null, 0, 0, false, true); Add(items, "丙", 0, 10, false, false);
            var fragments = Fragments(Arrange(items, 100)); Assert.That(fragments.Count, Is.EqualTo(3)); Assert.That(Get<string>(fragments[0], "Text"), Is.EqualTo("甲乙"));
            Assert.That(Get<float>(fragments[2], "Y"), Is.GreaterThan(Get<float>(fragments[1], "Y")));
        }
        [Test] public void MergedLineFragmentUsesMaximumGlyphAscentAndDescent()
        {
            var items = Items(); Add(items, "甲", 0, 10, false, false); Add(items, "乙", 0, 10, false, false);
            Set(items[1], "Ascent", 15f); Set(items[0], "Descent", 4f);
            var fragment = Fragments(Arrange(items, 100))[0];
            Assert.That(Get<float>(fragment, "Height"), Is.EqualTo(19f)); Assert.That(Get<float>(fragment, "Y"), Is.EqualTo(0f));
        }
        [Test] public void CompleteSingleLineMeasurementUsesMeasuredWidthsRatherThanWrappedWidthOrCharacterCount()
        {
            var items = Items(); Add(items, "宽", 0, 17, false, false); Add(items, "i", 0, 3, false, false); Add(items, null, 1, 42, true, false);
            Assert.That(Get<float>(Arrange(items, 20), "Width"), Is.EqualTo(42));
            Assert.That(MeasureSingleLine(items), Is.EqualTo(62));
        }
        [Test] public void SingleLineEllipsisKeepsLongestMeasuredPrefixWithoutWrappingOrChangingInput()
        {
            var items = Items(); Add(items, "甲", 0, 23, false, false); Add(items, "i", 0, 4, false, false); Add(items, "乙", 1, 8, false, false); Add(items, "丙", 1, 9, false, false);
            var result = Ellipsize(items, 34); var fragments = Fragments(result);
            Assert.That(string.Concat(fragments.Cast<object>().Select(f => Get<string>(f, "Text"))), Is.EqualTo("甲i…"));
            Assert.That(Get<float>(result, "Width"), Is.EqualTo(33)); Assert.That(Get<float>(result, "Height"), Is.EqualTo(10));
            Assert.That(items.Count, Is.EqualTo(4)); Assert.That(MeasureSingleLine(items), Is.EqualTo(44));
        }
        [Test] public void SingleLineKeepsOrDropsTheWholeScoreIncludingItsNumberAndDoesNotSkipAhead()
        {
            var items = Items(); Add(items, "前", 0, 12, false, false); Add(items, "-123 分", 1, 40, true, false); Add(items, "后文", 2, 50, false, false);
            var kept = Fragments(Ellipsize(items, 60));
            Assert.That(Get<string>(kept.Cast<object>().Single(f => Get<bool>(f, "Atomic")), "Text"), Is.EqualTo("-123 分"));
            Assert.That(Get<float>(kept[1], "Width"), Is.EqualTo(40));
            var dropped = Fragments(Ellipsize(items, 57));
            Assert.That(dropped.Cast<object>().All(f => !Get<bool>(f, "Atomic")), Is.True);
            Assert.That(string.Concat(dropped.Cast<object>().Select(f => Get<string>(f, "Text"))), Is.EqualTo("前…"));
        }
        [Test] public void SingleLineRetainsWholeGraphemesAndKeepsAnExactlyFittingLabelUnchanged()
        {
            var items = Items(); Add(items, "e\u0301", 0, 10, false, false); Add(items, "\U0001F642", 0, 12, false, false);
            Assert.That(string.Concat(Fragments(Ellipsize(items, 18)).Cast<object>().Select(f => Get<string>(f, "Text"))), Is.EqualTo("e\u0301…"));
            Assert.That(string.Concat(Fragments(Ellipsize(items, 22)).Cast<object>().Select(f => Get<string>(f, "Text"))), Is.EqualTo("e\u0301\U0001F642"));
        }
        [Test] public void SingleLineNeverDrawsPartOfAnEllipsisWhenThereIsNoRoom()
        {
            var items = Items(); Add(items, "长文", 0, 30, false, false);
            Assert.That(Fragments(Ellipsize(items, 0)), Is.Empty); Assert.That(Fragments(Ellipsize(items, 5)), Is.Empty);
            Assert.That(Get<string>(Fragments(Ellipsize(items, 6))[0], "Text"), Is.EqualTo("…"));
        }
        [Test] public void SingleLineRejectsInvalidWidthsAndUnnormalizedHardBreaks()
        {
            var items = Items(); Add(items, "甲", 0, 10, false, false);
            foreach (var width in new[] { -1f, float.NaN, float.PositiveInfinity })
                Assert.That(() => Ellipsize(items, width), Throws.TypeOf<System.Reflection.TargetInvocationException>().With.InnerException.TypeOf<ArgumentOutOfRangeException>());
            Add(items, null, 0, 0, false, true);
            Assert.That(() => MeasureSingleLine(items), Throws.TypeOf<System.Reflection.TargetInvocationException>().With.InnerException.TypeOf<ArgumentException>());
        }
        private static float MeasureSingleLine(IList items) => (float)TypeOf("").GetMethod("MeasureSingleLineWidth").Invoke(null, new object[] { items });
        private static object Ellipsize(IList items, float width)
        {
            var marker = Items(); Add(marker, "…", 99, 6, false, false);
            return TypeOf("").GetMethod("EllipsizeSingleLine").Invoke(null, new object[] { items, marker[0], width, 10f });
        }
        private static Type TypeOf(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YC.Presentation.GameplayTextLayout" + name)).FirstOrDefault(t => t != null) ?? throw new InvalidOperationException("玩法文字布局类型未加载。");
        private static IList Items() => (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(TypeOf("+Element")));
        private static void Add(IList items, string text, int group, float width, bool atomic, bool hardBreak)
        { var item = Activator.CreateInstance(TypeOf("+Element")); Set(item, "Text", text); Set(item, "Group", group); Set(item, "Width", width); Set(item, "Ascent", 8f); Set(item, "Descent", 2f); Set(item, "Atomic", atomic); Set(item, "HardBreak", hardBreak); items.Add(item); }
        private static object Arrange(IList items, float width) => TypeOf("").GetMethod("Arrange").Invoke(null, new object[] { items, width, 10f, 2f, true });
        private static IList Fragments(object result) => Get<IList>(result, "Fragments");
        private static T Get<T>(object value, string name) => (T)value.GetType().GetField(name).GetValue(value);
        private static void Set(object value, string name, object field) => value.GetType().GetField(name).SetValue(value, field);
    }
}
