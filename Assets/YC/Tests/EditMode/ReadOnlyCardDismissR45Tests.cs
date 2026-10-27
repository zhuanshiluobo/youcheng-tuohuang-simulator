using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    // 原Prefab/实际场景配置及指针消费的隔离检查，不计正常菜单或对局验收。
    public sealed class ReadOnlyCardDismissR45Tests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject canvasRoot, eventRoot;
        private Component registry;
        private readonly List<object> browsers = new List<object>();
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value)
            ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static object Call(object value, string name, params object[] args) =>
            value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);

        [SetUp] public void PrepareOriginalAssets()
        {
            canvasRoot = new GameObject("R45 Isolated Card Canvas", typeof(RectTransform), typeof(Canvas));
            canvasRoot.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvasRoot.transform).sizeDelta = new Vector2(1280, 720);
            if (EventSystem.current == null) eventRoot = new GameObject("R45 Isolated Pointer", typeof(EventSystem));
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            registry = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            Assert.That(registry, Is.Not.Null);
        }
        [TearDown] public void ReleaseOnlyOwnedObjects()
        {
            foreach (var browser in browsers) Call(browser, "Hide");
            browsers.Clear(); Object.DestroyImmediate(canvasRoot);
            if (eventRoot != null) Object.DestroyImmediate(eventRoot);
        }
        private object Show(Action close, object backgroundClose = null, object allowClose = null)
        {
            var cardType = T("EntrepreneurCardPresentation");
            var deck = ((IEnumerable)cardType.GetMethod("Deck", Flags).Invoke(null,
                new object[] { "entrepreneur.ability.A01", null })).Cast<object>().ToArray();
            Assert.That(deck.Length, Is.GreaterThanOrEqualTo(3));
            var cards = Array.CreateInstance(cardType, 2);
            cards.SetValue(deck[1], 0); cards.SetValue(deck[2], 1);
            var browser = Activator.CreateInstance(T("CardCollectionBrowser"), Flags, null,
                new object[] { registry, (RectTransform)canvasRoot.transform }, null);
            browsers.Add(browser);
            Call(browser, "Show", "", "", cards, (Func<bool>)(() => true), close,
                Type.Missing, backgroundClose ?? Type.Missing, allowClose ?? Type.Missing);
            return browser;
        }
        private static Component Page(object browser) => (Component)Read(Read(browser, "shell"), "view");
        private static GameObject Background(Component page) => ((Image)Read(page, "OverlayImage")).gameObject;
        private static Component[] Cards(Component page) => page.GetComponentsInChildren(T("FacilityEffectCardView"), false);
        private static PointerEventData Pointer(GameObject pressed, GameObject released = null) => new PointerEventData(EventSystem.current) {
            button = PointerEventData.InputButton.Left, pointerId = 45, position = Vector2.zero, pressPosition = Vector2.zero,
            pointerPressRaycast = new RaycastResult { gameObject = pressed },
            pointerCurrentRaycast = new RaycastResult { gameObject = released ?? pressed } };
        private static Button ActionButton(Component page, string name) => page.GetComponentsInChildren<Button>(false).SingleOrDefault(b => b.name == name);

        [Test] public void CollectionDefaultBlankDismissConsumesClickAndHasNoActionButtons()
        {
            int closes = 0, underlyingClicks = 0;
            var underlying = new GameObject("R45 Underlying Action", typeof(RectTransform), typeof(Button));
            underlying.transform.SetParent(canvasRoot.transform, false);
            underlying.GetComponent<Button>().onClick.AddListener(() => underlyingClicks++);
            var browser = Show(() => closes++); var page = Page(browser);
            Assert.That(Cards(page).Length, Is.EqualTo(2));
            Assert.That(ActionButton(page, "Confirm Selection"), Is.Null);
            Assert.That(ActionButton(page, "Cancel Selection"), Is.Null);
            var data = Pointer(Background(page));
            Assert.That(ExecuteEvents.ExecuteHierarchy(Background(page), data, ExecuteEvents.pointerClickHandler), Is.Not.Null);
            Assert.That(data.used, Is.True, "关闭前由查看层消费本次点击");
            Assert.That(Read(browser, "IsShowing"), Is.False);
            Assert.That(closes, Is.EqualTo(1)); Assert.That(underlyingClicks, Is.Zero);
            Call(page, "OnPointerClick", Pointer(Background(page)));
            Assert.That(closes, Is.EqualTo(1));
        }

        [Test] public void CardControlAndDragTargetsDoNotDismissCollection()
        {
            int closes = 0; var browser = Show(() => closes++); var page = Page(browser);
            var background = Background(page); var card = Cards(page)[0].gameObject;
            var control = new GameObject("R45 Protected Scroll Control", typeof(RectTransform), typeof(Button));
            control.transform.SetParent(page.transform, false);
            var samples = new List<PointerEventData> { Pointer(card), Pointer(background, card), Pointer(control) };
            var dragging = Pointer(background); dragging.dragging = true; samples.Add(dragging);
            var moved = Pointer(background); moved.position = new Vector2(1000, 0); samples.Add(moved);
            var right = Pointer(background); right.button = PointerEventData.InputButton.Right; samples.Add(right);
            foreach (var data in samples)
            {
                Call(page, "OnPointerClick", data);
                Assert.That(Read(browser, "IsShowing"), Is.True);
                Assert.That(data.used, Is.False);
                Assert.That(closes, Is.Zero);
            }
        }

        [Test] public void SingleCardInspectionReturnsToSameListAndScrollWithoutClosingCollection()
        {
            int closes = 0; var browser = Show(() => closes++); var page = Page(browser);
            Call(page, "RestoreScrollPosition", .37f);
            var before = (float)Read(page, "ScrollPosition");
            var card = Cards(page)[0]; var button = (Button)Read(card, "Button");
            var data = Pointer(button.gameObject);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerClickHandler);
            var viewer = Read(Read(browser, "shell"), "cardViewer");
            Assert.That(viewer, Is.Not.Null);
            Assert.That(Read(viewer, "IsShowing"), Is.True);
            Assert.That(((Button)Read(viewer, "CancelButton")).gameObject.activeInHierarchy, Is.False);
            Call(viewer, "Close");
            Assert.That(Read(browser, "IsShowing"), Is.True);
            Assert.That((float)Read(page, "ScrollPosition"), Is.EqualTo(before).Within(.0001f));
            Assert.That(Cards(page).Length, Is.EqualTo(2));
            Assert.That(closes, Is.Zero);
        }

        [Test] public void ExplicitButtonOrNonDismissableModesKeepTheirContractsAcrossReuse()
        {
            int closes = 0; var browser = Show(() => closes++, false, true); var page = Page(browser);
            Call(page, "OnPointerClick", Pointer(Background(page)));
            Assert.That(closes, Is.Zero); Assert.That(Read(browser, "IsShowing"), Is.True);
            Assert.That(ActionButton(page, "Cancel Selection"), Is.Not.Null);
            ActionButton(page, "Cancel Selection").onClick.Invoke();
            Assert.That(closes, Is.EqualTo(1));
            Call(browser, "Hide");
            var cardType = T("EntrepreneurCardPresentation");
            Call(browser, "Show", "", "", Array.CreateInstance(cardType, 0), (Func<bool>)(() => true),
                (Action)(() => closes++), Type.Missing, Type.Missing, false);
            page = Page(browser);
            Call(page, "OnPointerClick", Pointer(Background(page)));
            Assert.That(closes, Is.EqualTo(1), "禁止退出的只读模式不能继承上次回调");
            Assert.That(Read(browser, "IsShowing"), Is.True);
            Assert.That(ActionButton(page, "Cancel Selection"), Is.Null);
        }

        [TestCase("Assets/Scenes/SampleScene.unity")]
        [TestCase("Assets/Scenes/ThreePlayerScene.unity")]
        public void ActualSceneUsesAssignedEnterpriseBoardLabel(string path)
        {
            var scene = SceneManager.GetSceneByPath(path); bool opened = !scene.IsValid() || !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var expansions = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(T("GameplayExpansionUi"), true)).ToArray();
                Assert.That(expansions.Length, Is.EqualTo(1));
                var copy = Read(expansions[0], "entrepreneurUiCopy");
                Assert.That(Read(copy, "OwnEnterpriseLabel"), Is.EqualTo("查看企业合作板"));
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
