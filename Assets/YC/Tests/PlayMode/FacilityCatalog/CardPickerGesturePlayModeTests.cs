using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace YC.Tests.PlayMode
{
    public sealed class CardPickerGesturePlayModeTests
    {
        private GameObject owner;
        private Button button;
        private int selections, previews;

        [SetUp]
        public void SetUp()
        {
            selections = previews = 0;
            owner = new GameObject("卡牌手势验证", typeof(RectTransform), typeof(Canvas));
            var card = new GameObject("卡面", typeof(RectTransform), typeof(Image));
            card.transform.SetParent(owner.transform, false);
            button = (Button)card.AddComponent(Type.GetType("YC.Presentation.CardPickerCardButton, Assembly-CSharp", true));
            button.onClick.AddListener(() => selections++);
            button.GetType().GetMethod("ConfigurePreview").Invoke(button,
                new object[] { new Action(() => previews++), new Func<bool>(() => true) });
        }

        [TearDown]
        public void TearDown() { UnityEngine.Object.DestroyImmediate(owner); }

        [UnityTest]
        public IEnumerator SingleClickSelects_DoubleClickAndRightClickOnlyPreview()
        {
            Click(1);
            Assert.That(selections, Is.Zero);
            yield return new WaitForSecondsRealtime(.35f);
            Assert.That(selections, Is.EqualTo(1));
            Click(1);
            Click(2);
            yield return new WaitForSecondsRealtime(.35f);
            Assert.That(previews, Is.EqualTo(1));
            Assert.That(selections, Is.EqualTo(1));
            Click(1, PointerEventData.InputButton.Right);
            Assert.That(previews, Is.EqualTo(2));
            Assert.That(selections, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator LongPressPreviewsWithoutSelecting_AndDragCancelsGesture()
        {
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            button.OnPointerDown(data);
            yield return new WaitForSecondsRealtime(.55f);
            button.OnPointerUp(data);
            button.OnPointerClick(data);
            yield return new WaitForSecondsRealtime(.35f);
            Assert.That(previews, Is.EqualTo(1));
            Assert.That(selections, Is.Zero);
            button.OnPointerDown(data);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.beginDragHandler);
            yield return new WaitForSecondsRealtime(.55f);
            button.OnPointerUp(data);
            button.OnPointerClick(data);
            yield return new WaitForSecondsRealtime(.35f);
            Assert.That(previews, Is.EqualTo(1));
            Assert.That(selections, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ReadOnlyCardCanPreview_ButCannotSelect()
        {
            button.interactable = false;
            Click(1);
            yield return new WaitForSecondsRealtime(.35f);
            Click(1, PointerEventData.InputButton.Right);
            Assert.That(previews, Is.EqualTo(1));
            Assert.That(selections, Is.Zero);
        }

        private void Click(int count, PointerEventData.InputButton kind = PointerEventData.InputButton.Left)
        {
            var data = new PointerEventData(EventSystem.current) { button = kind, clickCount = count };
            button.OnPointerDown(data);
            button.OnPointerUp(data);
            button.OnPointerClick(data);
        }
    }
}
