using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace YC.Tests.EditMode
{
    public sealed class CardViewerTests
    {
        private GameObject root;
        private Component viewer;
        private Texture2D texture;
        private static Type Runtime(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private object Call(string name, params object[] args) => viewer.GetType().GetMethod(name).Invoke(viewer, args);
        private T Get<T>(string name) => (T)viewer.GetType().GetProperty(name).GetValue(viewer);

        [SetUp] public void SetUp()
        {
            root = new GameObject("卡牌查看器验证", typeof(RectTransform), typeof(Canvas));
            root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)root.transform).sizeDelta = new Vector2(1920, 912);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CardViewer.prefab");
            viewer = UnityEngine.Object.Instantiate(prefab, root.transform).GetComponent(Runtime("CardViewer"));
            viewer.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(viewer, null);
            texture = new Texture2D(600, 850);
        }
        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(texture);
        }
        private void Inspect(Action close = null, Func<bool> valid = null) => Call("OpenInspect", texture, close, valid);
        private void Use(bool plot, bool strategy, Func<bool> valid, Action callback, Action close = null)
        {
            var callbackType = typeof(Action<>).MakeGenericType(Runtime("CardViewerEffect"));
            var bridge = new CallbackBridge { callback = callback };
            var method = typeof(CallbackBridge).GetMethod("Run").MakeGenericMethod(Runtime("CardViewerEffect"));
            var handler = Delegate.CreateDelegate(callbackType, bridge, method);
            Call("OpenCharacter", "visible-card", texture, plot, strategy, "规则不允许", "规则不允许", valid, handler, close);
        }
        public sealed class CallbackBridge { public Action callback; public void Run<T>(T value) => callback(); }
        private void Layout()
        {
            for (var i = 0; i < 3; i++)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)viewer.transform);
                viewer.GetComponentInChildren(Runtime("CardViewerLayoutInputs"), true).SendMessage("LateUpdate");
            }
        }

        [Test] public void Inspect_HasNoExecutionOrCardIdentity_AndCancelOnlyReturns()
        {
            var commands = 0; var closes = 0;
            Use(true, true, () => true, () => commands++);
            Inspect(() => closes++);
            Get<Button>("PlotButton").onClick.Invoke(); Get<Button>("StrategyButton").onClick.Invoke();
            Assert.That(commands, Is.Zero);
            Assert.That(Get<string>("CardId"), Is.Empty);
            Assert.That(Get<Button>("PlotButton").gameObject.activeInHierarchy, Is.False);
            Assert.That(Get<Button>("CancelButton").gameObject.activeInHierarchy, Is.False);
            Call("Close"); Call("Close");
            Assert.That(closes, Is.EqualTo(1));
        }

        [Test] public void Use_RequiresEnabledCurrentChoice_AndOnlyDispatchesOnce()
        {
            var commands = 0; var closes = 0;
            Use(false, true, () => true, () => commands++, () => closes++);
            Get<Button>("PlotButton").onClick.Invoke();
            Assert.That(commands, Is.Zero);
            var click = Get<Button>("StrategyButton").onClick;
            click.Invoke(); click.Invoke();
            Assert.That(commands, Is.EqualTo(1)); Assert.That(closes, Is.Zero);
            Assert.That(Get<bool>("IsShowing"), Is.False);
        }

        [Test] public void StaleSource_CannotSubmitOrRestore_AndDismissDoesNotReturn()
        {
            var commands = 0; var closes = 0; var current = true;
            Use(true, true, () => current, () => commands++, () => closes++);
            current = false;
            Get<Button>("PlotButton").onClick.Invoke();
            Assert.That(commands, Is.Zero); Assert.That(closes, Is.Zero);
            Inspect(() => closes++); Call("Dismiss");
            Assert.That(closes, Is.Zero);
        }

        [Test] public void DestroyedViewer_LateCloseAndDismissAreSafeWithoutCallbacks()
        {
            var commands = 0; var closes = 0;
            Use(true, true, () => true, () => commands++, () => closes++);
            var staleViewer = viewer;
            UnityEngine.Object.DestroyImmediate(viewer.gameObject);
            Assert.That(Get<bool>("IsShowing"), Is.False);
            Assert.DoesNotThrow(() => Call("Dismiss"));
            Assert.DoesNotThrow(() => Call("Close"));
            Assert.That(commands, Is.Zero);
            Assert.That(closes, Is.Zero);
            var owner = new GameObject("销毁顺序回归");
            owner.SetActive(false);
            try
            {
                var controller = owner.AddComponent(Runtime("MobileCityInteractionController"));
                controller.GetType().GetField("characterViewer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, staleViewer);
                Assert.DoesNotThrow(() => controller.GetType().GetMethod("OnDestroy",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null));
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        [Test] public void UseButtons_SwitchTexturesAndDisabledStateOverridesPointer()
        {
            Use(true, true, () => true, () => {});
            foreach (var name in new[] { "PlotButton", "StrategyButton", "CancelButton" })
            {
                var button = Get<Button>(name);
                var state = button.GetComponent(Runtime("UiTextureButtonState"));
                Assert.That(state, Is.Not.Null);
                var type = state.GetType();
                type.GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(state, null);
                Texture Face(string field) => (Texture)type.GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(state);
                var image = button.GetComponent<RawImage>();
                var pointer = new PointerEventData(null);
                Assert.That(image.texture, Is.SameAs(Face("normal")));
                type.GetMethod("OnPointerEnter").Invoke(state, new object[] { pointer });
                Assert.That(image.texture, Is.SameAs(Face("hover")));
                type.GetMethod("OnPointerDown").Invoke(state, new object[] { pointer });
                Assert.That(image.texture, Is.SameAs(Face("pressed")));
                button.interactable = false;
                type.GetMethod("Refresh").Invoke(state, null);
                Assert.That(image.texture, Is.SameAs(Face("disabled")));
                type.GetMethod("OnPointerUp").Invoke(state, new object[] { pointer });
                Assert.That(image.texture, Is.SameAs(Face("disabled")));
                button.interactable = true;
                type.GetMethod("OnPointerExit").Invoke(state, new object[] { pointer });
                Assert.That(image.texture, Is.SameAs(Face("normal")));
            }
        }

        [TestCase(1920, 912)] [TestCase(1920, 1032)] [TestCase(2560, 912)] [TestCase(900, 432)]
        public void ModesKeepSameCardRect_AndContainDifferentImageAspects(int width, int height)
        {
            ((RectTransform)root.transform).sizeDelta = new Vector2(width, height);
            Inspect(); Layout();
            var card = Get<RawImage>("CardImage").rectTransform;
            var before = new Vector3[4]; card.GetWorldCorners(before);
            Use(true, true, () => true, () => {}); Layout();
            var after = new Vector3[4]; card.GetWorldCorners(after);
            for (var i = 0; i < 4; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(.1f));
            Assert.That(card.rect.width / card.rect.height, Is.EqualTo(600f / 850).Within(.001f));
            var landscape = new Texture2D(850, 600);
            try
            {
                Call("OpenInspect", landscape, null, null); Layout();
                Assert.That(card.rect.width / card.rect.height, Is.EqualTo(850f / 600).Within(.001f));
                var parent = (RectTransform)card.parent;
                Assert.That(card.rect.width, Is.LessThanOrEqualTo(parent.rect.width + .1f));
                Assert.That(card.rect.height, Is.LessThanOrEqualTo(parent.rect.height + .1f));
            }
            finally { UnityEngine.Object.DestroyImmediate(landscape); }
        }
    }
}
