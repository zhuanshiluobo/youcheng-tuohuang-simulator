using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation
{
    public sealed partial class GameplayExpansionUi
    {
        [SerializeField] private ScrollRect cooperationScroll;
        [SerializeField] private RectTransform cooperationContent;
        [SerializeField] private LayoutElement cooperationContentLayout;

        private sealed class CooperationRow
        {
            internal GameObject Root;
            internal Text Name;
            internal Image Logo;
            internal Image[] Grades;
            internal GameObject[] EmptySlots;
        }

        private readonly List<CooperationRow> cooperationRows = new List<CooperationRow>();
        private float cooperationBaseHeight = -1f;

        private IReadOnlyList<CooperationRow> PrepareCooperationRows(int count)
        {
            if (cooperationRows.Count == 0)
            {
                cooperationBaseHeight = cooperationContentLayout.preferredHeight;
                for (int row = 0; row < cooperationNames.Length; row++)
                {
                    var grades = new Image[6]; var empty = new GameObject[6];
                    Array.Copy(cooperationGrades, row * 6, grades, 0, 6);
                    Array.Copy(cooperationEmptySlots, row * 6, empty, 0, 6);
                    cooperationRows.Add(new CooperationRow { Root = cooperationNames[row].transform.parent.gameObject,
                        Name = cooperationNames[row], Logo = cooperationLogos[row], Grades = grades, EmptySlots = empty });
                }
            }
            var first = (RectTransform)cooperationRows[0].Root.transform;
            var second = (RectTransform)cooperationRows[1].Root.transform;
            Vector2 interval = second.anchoredPosition - first.anchoredPosition;
            if (interval.y >= 0f || Math.Abs(interval.x) > .01f)
                throw new InvalidOperationException("企业合作行的原预制体顺序无效。");
            while (cooperationRows.Count < count)
            {
                var prototype = cooperationRows[0];
                var clone = Instantiate(prototype.Root, cooperationContent, false);
                clone.name = prototype.Root.name + " " + (cooperationRows.Count + 1);
                var rect = (RectTransform)clone.transform;
                rect.anchoredPosition = first.anchoredPosition + interval * cooperationRows.Count;
                var grades = new Image[6]; var empty = new GameObject[6];
                for (int level = 0; level < 6; level++)
                {
                    grades[level] = Corresponding(prototype.Root.transform, clone.transform, prototype.Grades[level].transform).GetComponent<Image>();
                    empty[level] = Corresponding(prototype.Root.transform, clone.transform, prototype.EmptySlots[level].transform).gameObject;
                }
                cooperationRows.Add(new CooperationRow { Root = clone,
                    Name = Corresponding(prototype.Root.transform, clone.transform, prototype.Name.transform).GetComponent<Text>(),
                    Logo = Corresponding(prototype.Root.transform, clone.transform, prototype.Logo.transform).GetComponent<Image>(),
                    Grades = grades, EmptySlots = empty });
            }
            float height = cooperationBaseHeight + Mathf.Max(0, count - cooperationNames.Length) * -interval.y;
            if (!Mathf.Approximately(cooperationContentLayout.preferredHeight, height))
            {
                cooperationContentLayout.preferredHeight = height;
                LayoutRebuilder.MarkLayoutForRebuild(cooperationContent);
            }
            return cooperationRows;
        }

        private static Transform Corresponding(Transform originalRoot, Transform cloneRoot, Transform original)
        {
            var path = new Stack<int>();
            for (var node = original; node != originalRoot; node = node.parent)
            {
                if (node == null) throw new InvalidOperationException("企业合作行引用不属于原预制体行。");
                path.Push(node.GetSiblingIndex());
            }
            var target = cloneRoot;
            while (path.Count > 0) target = target.GetChild(path.Pop());
            return target;
        }

        private void ClearCooperationRows()
        {
            for (int row = cooperationNames?.Length ?? 0; row < cooperationRows.Count; row++)
                if (cooperationRows[row].Root != null) cooperationRows[row].Root.SetActive(false);
            if (cooperationContentLayout != null && cooperationBaseHeight >= 0f)
            {
                cooperationContentLayout.preferredHeight = cooperationBaseHeight;
                if (cooperationContent != null) LayoutRebuilder.MarkLayoutForRebuild(cooperationContent);
            }
            if (cooperationScroll != null) cooperationScroll.StopMovement();
        }
    }
}
