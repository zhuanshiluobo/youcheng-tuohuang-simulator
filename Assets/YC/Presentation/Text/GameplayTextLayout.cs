using System;
using System.Collections.Generic;
using System.Text;

namespace YC.Presentation
{
    /// <summary>无 Unity 依赖的行布局。输入可按字测量，输出按行和相同最终字体/样式合并。</summary>
    public static class GameplayTextLayout
    {
        public sealed class Element
        {
            public string Text;
            public int Group;
            public float Width, Ascent, Descent;
            public bool Atomic, HardBreak;
        }
        public sealed class Fragment
        {
            public string Text;
            public int Group;
            public float X, Y, Width, Height, LineWidth;
            public bool Atomic;
            internal float Ascent, Descent;
        }
        public sealed class Result
        {
            public readonly List<Fragment> Fragments = new List<Fragment>();
            public float Width, Height;
            public bool HasOversizedAtomic;
        }

        // 字形和生成网格都是像素坐标，二者相减才是实际字体基线；只转换一次到 UI 单位。
        public static float BaselineFromGeneratedGlyph(float glyphTop, float generatedTop, float pixelsPerUnit)
        {
            if (float.IsNaN(pixelsPerUnit) || float.IsInfinity(pixelsPerUnit) || pixelsPerUnit <= 0)
                throw new ArgumentOutOfRangeException(nameof(pixelsPerUnit));
            return (glyphTop - generatedTop) / pixelsPerUnit;
        }

        public static float TextTopForBaseline(float lineBaseline, float generatedBaselineFromTop)
            => lineBaseline - generatedBaselineFromTop;

        public static int FitFontSize(int preferredSize, float width, float height, Func<int, Element> measureInk)
        {
            if (preferredSize < 1) throw new ArgumentOutOfRangeException(nameof(preferredSize));
            if (measureInk == null) throw new ArgumentNullException(nameof(measureInk));
            for (int size = preferredSize; size > 1; size--)
            {
                var ink = measureInk(size);
                if (ink.Width <= width && ink.Ascent + ink.Descent <= height) return size;
            }
            return 1;
        }

        public static int FitSharedStep(int firstBaseSize, int secondBaseSize, Func<int, bool> firstFits, Func<int, bool> secondFits)
        {
            if (firstBaseSize < 1 || secondBaseSize < 1) throw new ArgumentOutOfRangeException(nameof(firstBaseSize));
            if (firstFits == null || secondFits == null) throw new ArgumentNullException(nameof(firstFits));
            int steps = Math.Max(firstBaseSize, secondBaseSize);
            for (int step = steps; step > 1; step--)
                if (firstFits(ScaledFontSize(firstBaseSize, step, steps)) && secondFits(ScaledFontSize(secondBaseSize, step, steps)))
                    return step;
            return 1;
        }

        public static int ScaledFontSize(int baseSize, int step, int steps)
            => Math.Max(1, (int)Math.Floor(baseSize * (double)step / steps));

        /// <summary>输入已把硬换行转成实测空格；宽度不受正文换行或可用框宽影响。</summary>
        public static float MeasureSingleLineWidth(IReadOnlyList<Element> elements)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            double width = 0;
            foreach (var element in elements)
            {
                ValidateSingleLineElement(element);
                width += element.Width;
            }
            if (width > float.MaxValue) throw new ArgumentOutOfRangeException(nameof(elements), "单行测量宽度超出范围。");
            return (float)width;
        }

        /// <summary>按实测元素保留最长前缀，图示/叠数元素整体留下或移除；不缩字、不换行。</summary>
        public static Result EllipsizeSingleLine(IReadOnlyList<Element> elements, Element ellipsis,
            float maximumWidth, float minimumLineHeight)
        {
            if (float.IsNaN(maximumWidth) || float.IsInfinity(maximumWidth) || maximumWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumWidth));
            ValidateSingleLineElement(ellipsis);
            var fullWidth = MeasureSingleLineWidth(elements);
            if (fullWidth <= maximumWidth) return Arrange(elements, Math.Max(1, maximumWidth), minimumLineHeight, 0, false);
            var visible = new List<Element>();
            if (ellipsis.Width <= maximumWidth)
            {
                double width = ellipsis.Width;
                foreach (var element in elements)
                {
                    if (width + element.Width > maximumWidth) break;
                    visible.Add(element); width += element.Width;
                }
                visible.Add(ellipsis);
            }
            return Arrange(visible, Math.Max(1, maximumWidth), minimumLineHeight, 0, false);
        }

        private static void ValidateSingleLineElement(Element element)
        {
            if (element == null || element.HardBreak || float.IsNaN(element.Width) || float.IsInfinity(element.Width) || element.Width < 0)
                throw new ArgumentException("单行输入必须是有效实测元素，硬换行须先转为空格。");
        }

        public static Result Arrange(IReadOnlyList<Element> elements, float availableWidth, float minimumLineHeight, float lineSpacing, bool wrap = true)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            availableWidth = Math.Max(1, availableWidth); minimumLineHeight = Math.Max(1, minimumLineHeight);
            var result = new Result(); var line = new List<Element>(); float width = 0, y = 0;
            bool endedWithBreak = false;
            Action flush = () =>
            {
                float ascent = minimumLineHeight * .8f, descent = minimumLineHeight * .2f;
                foreach (var element in line) { ascent = Math.Max(ascent, element.Ascent); descent = Math.Max(descent, element.Descent); }
                int start = result.Fragments.Count;
                float x = 0; Fragment previous = null; var text = new StringBuilder();
                Action finish = () => { if (previous != null && !previous.Atomic) previous.Text = text.ToString(); text.Clear(); };
                foreach (var element in line)
                {
                    if (previous == null || element.Atomic || previous.Atomic || element.Group != previous.Group)
                    {
                        finish(); previous = new Fragment { Group = element.Group, Atomic = element.Atomic, Text = element.Atomic ? element.Text : null, X = x, Ascent = element.Ascent, Descent = element.Descent };
                        result.Fragments.Add(previous);
                    }
                    previous.Width += element.Width;
                    previous.Ascent = Math.Max(previous.Ascent, element.Ascent); previous.Descent = Math.Max(previous.Descent, element.Descent);
                    previous.Y = y + ascent - previous.Ascent; previous.Height = previous.Ascent + previous.Descent;
                    if (!element.Atomic) text.Append(element.Text);
                    x += element.Width;
                }
                finish(); for (int i = start; i < result.Fragments.Count; i++) result.Fragments[i].LineWidth = x;
                result.Width = Math.Max(result.Width, x); y += ascent + descent + lineSpacing;
                line.Clear(); width = 0;
            };
            foreach (var element in elements)
            {
                if (element == null) throw new ArgumentException("布局不能含空片段。");
                if (element.HardBreak) { flush(); endedWithBreak = true; continue; }
                endedWithBreak = false;
                if (wrap && width > 0 && width + element.Width > availableWidth + .01f) flush();
                if (element.Atomic && element.Width > availableWidth + .01f) result.HasOversizedAtomic = true;
                line.Add(element); width += element.Width;
            }
            if (line.Count > 0 || endedWithBreak) flush();
            result.Height = y > 0 ? y - lineSpacing : 0;
            return result;
        }
    }
}
