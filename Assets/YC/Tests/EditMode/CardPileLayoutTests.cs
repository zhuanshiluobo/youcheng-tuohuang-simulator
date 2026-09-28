using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class CardPileLayoutTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject owner;

        [TearDown]
        public void TearDown() { if (owner != null) Object.DestroyImmediate(owner); }

        private Component CreatePile(int kind)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            owner = Object.Instantiate(prefab);
            foreach (var canvas in owner.GetComponentsInChildren<Canvas>(true))
                if (canvas.isRootCanvas) canvas.renderMode = RenderMode.WorldSpace;
            var type = Type.GetType("YC.Presentation.CardPileView, Assembly-CSharp", true);
            return owner.GetComponentsInChildren(type, true).Cast<Component>()
                .Single(p => Convert.ToInt32(Field(p, "kind")) == kind);
        }

        [TestCase(0, 3)]
        [TestCase(0, 7)]
        [TestCase(2, 1)]
        [TestCase(2, 3)]
        [TestCase(2, 7)]
        public void Scatter_KeepsConfiguredScaleWithoutScrolling_AndKeepsCentersInside(int kind, int count)
        {
            var pile = CreatePile(kind);
            var viewport = (RectTransform)Field(pile, "viewport");
            var content = (RectTransform)Field(pile, "content");
            // 隔离当前实例的尺寸输入；不修改或保存源资产。
            viewport.SetParent(owner.transform, false);
            viewport.anchorMin = viewport.anchorMax = new Vector2(.5f, .5f);
            var header = (RectTransform)Field(pile, "protectedHeader");
            Assert.That(header, Is.Not.Null, "正式手牌和盖牌都应绑定标题避让区域。");
            header.SetParent(viewport, false);
            header.anchorMin = Vector2.zero;
            header.anchorMax = new Vector2(1, 0);
            header.pivot = Vector2.zero;
            header.sizeDelta = new Vector2(0, 30);
            header.anchorMin = header.anchorMax = new Vector2(0, 1);
            header.sizeDelta = new Vector2(900, 30);
            header.anchoredPosition = Vector2.zero;
            Assert.That(content.GetComponentInParent<ScrollRect>(), Is.Null);
            Assert.That(viewport.GetComponent<RectMask2D>(), Is.Null);
            Assert.That(content.GetComponent<Canvas>().sortingOrder,
                Is.LessThan(header.GetComponent<Canvas>().sortingOrder));
            var ids = Enumerable.Range(0, count).Select(i => "character.red.p1." + i).ToArray();
            Render(pile, ids);
            var angles = new float[count];
            var wideSpan = 0f;
            foreach (var width in new[] { 900f, 120f, 900f })
            {
                viewport.sizeDelta = new Vector2(width, kind == 0 ? 122 : 160);
                Call(pile, "RefreshLayout");
                var positions = content.Cast<RectTransform>().Select(r => r.anchoredPosition).ToArray();
                var span = positions.Max(p => p.x) - positions.Min(p => p.x);
                if (kind == 0 && width == 900)
                    Assert.That(positions.Max(p => p.y) - positions.Min(p => p.y), Is.GreaterThan(10f),
                        "允许使用标题栏空白后，卡牌中心应有明显的纵向落差。");
                if (wideSpan == 0f && count > 1) wideSpan = span;
                if (width == 120 && count > 1)
                {
                    Assert.That(span, Is.LessThan(wideSpan), "区域变窄后必须恢复重叠。");
                    Assert.That(span / (count - 1), Is.LessThan(78f));
                }
                for (var i = 0; i < content.childCount; i++)
                {
                    var rect = (RectTransform)content.GetChild(i);
                    Assert.That(rect.rect.size, Is.EqualTo(new Vector2(78, 112)));
                    Assert.That(rect.localScale, Is.EqualTo(Vector3.one * (float)pile.GetType().GetProperty("SlotScale").GetValue(pile)));
                    var center = viewport.InverseTransformPoint(rect.TransformPoint(rect.rect.center));
                    Assert.That(center.x, Is.InRange(viewport.rect.xMin, viewport.rect.xMax));
                    Assert.That(center.y, Is.InRange(viewport.rect.yMin, viewport.rect.yMax));
                    var corners = new Vector3[4];
                    rect.GetWorldCorners(corners);
                    var labelRect = header.rect;
                    if ((bool)Field(pile, "protectHeaderTextOnly"))
                    {
                        var label = header.GetComponent<Text>();
                        var textWidth = Mathf.Min(labelRect.width, label.preferredWidth);
                        labelRect.x += (labelRect.width - textWidth) * ((int)label.alignment % 3) * .5f;
                        labelRect.width = textWidth;
                    }
                    if (corners.Max(c => header.InverseTransformPoint(c).x) >= labelRect.xMin - 4f &&
                        corners.Min(c => header.InverseTransformPoint(c).x) <= labelRect.xMax + 4f)
                        Assert.That(corners.Max(c => header.InverseTransformPoint(c).y), Is.LessThanOrEqualTo(-3.9f),
                            "卡牌可以进入标题栏空白，但不能遮挡标题文字。");
                    Assert.That(corners.Min(c => viewport.InverseTransformPoint(c).x),
                        Is.GreaterThanOrEqualTo(viewport.rect.xMin - 12.1f));
                    Assert.That(corners.Max(c => viewport.InverseTransformPoint(c).x),
                        Is.LessThanOrEqualTo(viewport.rect.xMax + 12.1f));
                    if (width == 120)
                    {
                        angles[i] = rect.localEulerAngles.z;
                    }
                    else if (angles.Any(a => a != 0))
                        Assert.That(rect.localEulerAngles.z, Is.EqualTo(angles[i]).Within(.001f), "缩放不能重新抽取角度。");
                }
            }
            if (count > 1) Assert.That(angles.Distinct().Count(), Is.GreaterThan(1), "散落角度不能全部相同。");
            var before = content.Cast<RectTransform>().Select(r => r.anchoredPosition).ToArray();
            Render(pile, ids);
            Call(pile, "RefreshLayout");
            CollectionAssert.AreEqual(before, content.Cast<RectTransform>().Select(r => r.anchoredPosition).ToArray());
            Render(pile, new string[0]);
            Assert.That(content.childCount, Is.Zero);
        }

        [Test]
        public void Discard_KeepsLatestFiveInActualOrder_WithSharedSlotSize()
        {
            var pile = CreatePile(1);
            var ids = new[] { "z", "b", "g", "a", "h", "c", "f" };
            Render(pile, ids);
            Call(pile, "RefreshLayout");
            var content = (RectTransform)Field(pile, "content");
            CollectionAssert.AreEqual(ids.Skip(2).Select(id => "Card Slot: " + id),
                content.Cast<Transform>().Select(t => t.name));
            foreach (RectTransform slot in content)
            {
                Assert.That(slot.rect.size, Is.EqualTo(new Vector2(78, 112)));
                Assert.That(slot.localScale, Is.EqualTo(Vector3.one * (float)pile.GetType().GetProperty("SlotScale").GetValue(pile)));
            }
        }

        private static object Field(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void WholeRegionIds_ResampleRetainedCards_WithoutChangingGlobalRandom(int kind)
        {
            var pile = CreatePile(kind);
            PrepareSamplingArea(pile);
            var ids = new[] { "card-a", "card-b", "card-c" };
            var randomState = UnityEngine.Random.state;
            var initial = RenderSnapshot(pile, ids);
            CollectionAssert.AreEqual(initial, RenderSnapshot(pile, ids.ToArray()), "普通刷新不应重新抽样。");

            var added = RenderSnapshot(pile, ids.Concat(new[] { "card-d" }).ToArray());
            AssertResampled(initial, added, ids);
            var replaced = RenderSnapshot(pile, ids.Concat(new[] { "card-e" }).ToArray());
            AssertResampled(added, replaced, ids);
            CollectionAssert.AreEqual(initial, RenderSnapshot(pile, ids), "恢复同一 ID 列表应恢复对应布局。");
            var reordered = RenderSnapshot(pile, ids.Reverse().ToArray());
            AssertResampled(initial, reordered, ids);
            Assert.That(UnityEngine.Random.state, Is.EqualTo(randomState), "本地散落不能消耗全局随机状态。");
        }

        [Test]
        public void DiscardSeed_IncludesCardsBelowVisibleFive_AndKeepsTopCard()
        {
            var pile = CreatePile(1);
            PrepareSamplingArea(pile);
            var ids = new[] { "hidden-a", "hidden-b", "a", "b", "c", "d", "top" };
            var initial = RenderSnapshot(pile, ids);
            ids[0] = "hidden-changed";
            var changed = RenderSnapshot(pile, ids);
            AssertResampled(initial, changed, ids.Skip(2).ToArray());
            var content = (RectTransform)Field(pile, "content");
            CollectionAssert.AreEqual(ids.Skip(2).Select(id => "Card Slot: " + id),
                content.Cast<Transform>().Select(t => t.name));
        }

        [Test]
        public void RegionChanges_DoNotResampleOtherRegions()
        {
            var hand = CreatePile(0);
            var covered = owner.GetComponentsInChildren(hand.GetType(), true).Cast<Component>()
                .Single(p => Convert.ToInt32(Field(p, "kind")) == 2);
            PrepareSamplingArea(hand);
            PrepareSamplingArea(covered);
            RenderSnapshot(hand, new[] { "a", "b" });
            var before = RenderSnapshot(covered, new[] { "covered" });
            RenderSnapshot(hand, new[] { "a", "b", "c" });
            CollectionAssert.AreEqual(before, RenderSnapshot(covered, new[] { "covered" }));
        }

        private void PrepareSamplingArea(Component pile)
        {
            var viewport = (RectTransform)Field(pile, "viewport");
            viewport.SetParent(owner.transform, false);
            viewport.anchorMin = viewport.anchorMax = Vector2.one * .5f;
            viewport.sizeDelta = new Vector2(900, 300);
            pile.GetType().GetField("protectedHeader", Flags).SetValue(pile, null);
        }

        private static System.Collections.Generic.Dictionary<string, Vector3> RenderSnapshot(Component pile, string[] ids)
        {
            Render(pile, ids);
            Call(pile, "RefreshLayout");
            return ((RectTransform)Field(pile, "content")).Cast<RectTransform>().ToDictionary(
                rect => rect.name.Substring("Card Slot: ".Length),
                rect => new Vector3(rect.anchoredPosition.x, rect.anchoredPosition.y, rect.localEulerAngles.z));
        }

        private static void AssertResampled(System.Collections.Generic.Dictionary<string, Vector3> before,
            System.Collections.Generic.Dictionary<string, Vector3> after, string[] retained)
        {
            foreach (var id in retained)
            {
                Assert.That((Vector2)after[id], Is.Not.EqualTo((Vector2)before[id]), id + " 的位置应重新生成。");
                Assert.That(after[id].z, Is.Not.EqualTo(before[id].z), id + " 的角度应重新生成。");
            }
        }

        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Flags).Invoke(target, args);
        private static void Render(Component pile, string[] ids) =>
            Call(pile, "Render", ids, new Func<string, Texture>(_ => Texture2D.whiteTexture), null);
    }
}
