using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class CardPointerInteractionTests
    {
        [Test]
        public void SharedInteraction_ClassifiesClicksAndOwnsDragLifecycle()
        {
            var owner = new GameObject("Shared Card Pointer Interaction Test", typeof(RectTransform), typeof(Image), typeof(Button));
            try
            {
                var interactionType = Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", false);
                Assert.That(interactionType, Is.Not.Null);
                var interaction = owner.AddComponent(interactionType);
                var button = owner.GetComponent<Button>();
                var singleClicks = 0;
                var doubleClicks = 0;
                var dragBegins = 0;
                var dragUpdates = 0;
                var dragEnds = 0;
                var dragCancels = 0;

                interactionType.GetMethod("ConfigureDrag", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(interaction, new object[]
                    {
                        new Func<bool>(() => true),
                        new Action<PointerEventData>(_ => dragBegins += 1),
                        new Action<PointerEventData>(_ => dragUpdates += 1),
                        new Action<PointerEventData>(_ => dragEnds += 1),
                        new Action(() => dragCancels += 1)
                    });
                interactionType.GetMethod("ConfigureClick", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(interaction, new object[]
                    {
                        button,
                        new Action(() => singleClicks += 1),
                        new Action(() => doubleClicks += 1)
                    });

                var pointer = CreatePointer(PointerEventData.InputButton.Left, 1);
                ((IPointerDownHandler)interaction).OnPointerDown(pointer);
                ((IPointerClickHandler)interaction).OnPointerClick(pointer);
                Assert.That(singleClicks, Is.EqualTo(1));

                pointer.clickCount = 2;
                ((IPointerDownHandler)interaction).OnPointerDown(pointer);
                ((IPointerClickHandler)interaction).OnPointerClick(pointer);
                Assert.That(doubleClicks, Is.EqualTo(1));

                pointer.clickCount = 1;
                ((IPointerDownHandler)interaction).OnPointerDown(pointer);
                ((IBeginDragHandler)interaction).OnBeginDrag(pointer);
                ((IDragHandler)interaction).OnDrag(pointer);
                ((IEndDragHandler)interaction).OnEndDrag(pointer);
                ((IPointerClickHandler)interaction).OnPointerClick(pointer);
                Assert.That(dragBegins, Is.EqualTo(1));
                Assert.That(dragUpdates, Is.EqualTo(1));
                Assert.That(dragEnds, Is.EqualTo(1));
                Assert.That(singleClicks, Is.EqualTo(1), "拖动后的点击不应再次触发单击。");

                var rightPointer = CreatePointer(PointerEventData.InputButton.Right, 1);
                ((IBeginDragHandler)interaction).OnBeginDrag(rightPointer);
                ((IEndDragHandler)interaction).OnEndDrag(rightPointer);
                Assert.That(dragBegins, Is.EqualTo(1), "右键不应开始卡牌拖动。");

                ((IBeginDragHandler)interaction).OnBeginDrag(pointer);
                interactionType.GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(interaction, null);
                Assert.That(dragCancels, Is.EqualTo(1), "卡牌中途禁用时应结束通用拖动状态。");
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }

        private static PointerEventData CreatePointer(PointerEventData.InputButton button, int clickCount)
        {
            return new PointerEventData(EventSystem.current)
            {
                button = button,
                clickCount = clickCount,
                position = new Vector2(100f, 100f)
            };
        }
    }
}
