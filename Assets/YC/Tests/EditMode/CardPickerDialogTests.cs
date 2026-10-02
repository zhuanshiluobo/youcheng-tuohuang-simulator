using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Interactions;

namespace YC.Tests.EditMode
{
    /// <summary>使用正式 Registry 与 Prefab 的只读 UI 验证；不生成或保存界面资产。</summary>
    public sealed class CardPickerDialogTests
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject canvasRoot;
        private Component registry;
        private object previousViewerPrefab;
        private readonly List<object> dialogs = new List<object>();

        [SetUp]
        public void SetUp()
        {
            canvasRoot = new GameObject("卡牌选择验证", typeof(RectTransform), typeof(Canvas));
            canvasRoot.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvasRoot.transform).sizeDelta = new Vector2(1920, 1080);
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(hud, Is.Not.Null);
            registry = hud.GetComponentInChildren(RuntimeType("GameplayDialogRegistry"), true);
            Assert.That(registry, Is.Not.Null);
            var viewerType = RuntimeType("ZoomableImageViewerController");
            previousViewerPrefab = viewerType.GetField("registeredPrefab", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
            var viewer = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/InGame/InGameZoomableImageViewer.prefab");
            Assert.That(viewer, Is.Not.Null);
            viewerType.GetMethod("RegisterPrefab").Invoke(null,
                new object[] { viewer.GetComponent(viewerType) });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var dialog in dialogs) Call(dialog, "Hide");
            dialogs.Clear();
            UnityEngine.Object.DestroyImmediate(canvasRoot);
            RuntimeType("ZoomableImageViewerController")
                .GetField("registeredPrefab", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, previousViewerPrefab);
        }

        [TestCase(2, 1920)]
        [TestCase(6, 1920)]
        [TestCase(7, 1920)]
        [TestCase(9, 1920)]
        [TestCase(2, 1024)]
        [TestCase(6, 1024)]
        [TestCase(7, 1024)]
        [TestCase(9, 1024)]
        public void CardRow_KeepsArtworkAspectAndReachesLastCard(int count, int width)
        {
            ((RectTransform)canvasRoot.transform).sizeDelta = new Vector2(width, width * 9f / 16f);
            var dialog = NewDialog("CharacterCardEffectChoiceDialog");
            ShowSelection(dialog, NewSelection(FacilityIds(count)), false);
            var page = Page(dialog);
            Assert.That(Property<bool>(page, "IsCardPicker"), Is.True);
            Refresh(page);
            var scroll = Property<ScrollRect>(page, "OptionScroll");
            var cards = Cards(page);
            Assert.That(cards.Length, Is.EqualTo(count));
            Assert.That(scroll.horizontal, Is.True);
            Assert.That(scroll.vertical, Is.False);
            AssertWithin(Property<RectTransform>(page, "Panel"), (RectTransform)page.transform);
            foreach (var card in cards)
            {
                var artwork = Property<RawImage>(card, "CardImage");
                Assert.That(artwork.texture, Is.Not.Null);
                Assert.That(artwork.rectTransform.rect.height, Is.GreaterThan(0));
                Assert.That(artwork.rectTransform.rect.width / artwork.rectTransform.rect.height,
                    Is.EqualTo((artwork.uvRect.width * artwork.texture.width + 1f) / (artwork.uvRect.height * artwork.texture.height + 1f)).Within(.002f));
                var face = Property<Button>(card, "Button");
                Assert.That(face.transform, Is.Not.SameAs(card.transform),
                    "卡牌空槽不能整体充当选择按钮。");
                AssertWithin((RectTransform)face.transform, Property<RectTransform>(card, "CardRect"));
            }
            if (count > 7)
                Assert.That(scroll.content.rect.width, Is.GreaterThan(scroll.viewport.rect.width),
                    "超过七张的卡牌应可通过横向滚动访问。");
            Call(page, "RestoreScrollPosition", 0f);
            Refresh(page);
            var last = cards[cards.Length - 1];
            AssertWithin(Property<RawImage>(last, "CardImage").rectTransform, scroll.viewport);
            Call(page, "RestoreScrollPosition", 1f);
            Refresh(page);
            AssertWithin(Property<RawImage>(cards[0], "CardImage").rectTransform, scroll.viewport);
        }

        [TestCase("CharacterCardEffectChoiceDialog")]
        [TestCase("FacilityEffectChoiceDialog")]
        public void ArtworkSelectionUsesCardPicker_AndTextChoiceKeepsOriginalShell(string typeName)
        {
            var dialog = NewDialog(typeName);
            ShowSelection(dialog, NewSelection(FacilityIds(2)), false);
            Assert.That(Property<bool>(Page(dialog), "IsCardPicker"), Is.True);
            ShowSelection(dialog, NewSelection(new[] { "originium", "iron" }), false);
            var page = Page(dialog);
            Assert.That(Property<bool>(page, "IsCardPicker"), Is.False);
            Assert.That(Cards(page), Is.Empty);
            Assert.That(page.GetComponentsInChildren(RuntimeType("EffectDialogOptionRowView"), false).Length,
                Is.EqualTo(2));
        }

        [Test]
        public void ResourceSaleAndExtensionHubKeepTheirOriginalShell()
        {
            var character = NewDialog("CharacterCardEffectChoiceDialog");
            Call(character, "ShowResourceSale", new[] { "验证资源" }, new[] { 3 }, new[] { 2 },
                new Action<IReadOnlyList<int>>(_ => { }), new Action(() => { }));
            Assert.That(Property<bool>(Page(character), "IsCardPicker"), Is.False);
            Assert.That(Page(character).GetComponentsInChildren(RuntimeType("EffectDialogResourceRowView"), false).Length,
                Is.EqualTo(1));
            var facility = NewDialog("FacilityEffectChoiceDialog");
            var optionType = RuntimeType("FacilityEffectCardOption");
            var options = Array.CreateInstance(optionType, 1);
            options.SetValue(Activator.CreateInstance(optionType,
                new object[] { FacilityIds(1)[0], "验证建设候选", true }), 0);
            Call(facility, "ShowExtensionHubOptions", options, new Action(() => { }),
                new Action<string, int>((_, __) => { }), new Action(() => { }), new Action(() => { }));
            Assert.That(Property<bool>(Page(facility), "IsCardPicker"), Is.False);
        }

        [Test]
        public void DetailsReturnPreservesHorizontalScrollAndDraftWithoutSelectingOrSubmitting()
        {
            var ids = FacilityIds(9);
            var dialog = NewDialog("CharacterCardEffectChoiceDialog");
            var spec = NewSelection(ids);
            var selected = ids[1];
            var selectCalls = 0;
            var submissions = 0;
            Set(spec, "SelectedIds", new[] { selected });
            Set(spec, "Select", new Action<string>(_ => selectCalls++));
            Set(spec, "Confirm", new Action(() => submissions++));
            ShowSelection(dialog, spec, false);
            var page = Page(dialog);
            Call(page, "RestoreScrollPosition", .27f);
            Refresh(page);
            Assert.That(Property<ScrollRect>(page, "OptionScroll").horizontalNormalizedPosition,
                Is.EqualTo(.73f).Within(.002f));
            ShowSelection(dialog, spec, true);
            page = Page(dialog);
            Refresh(page);
            Assert.That(Property<float>(page, "ScrollPosition"), Is.EqualTo(.27f).Within(.002f));
            var cards = Cards(page);
            Assert.That(Property<Image>(cards[1], "SelectionImage").enabled, Is.True);
            OpenCardPreview(Property<Button>(cards[cards.Length - 1], "Button"));
            Assert.That(selectCalls, Is.Zero, "打开详情不得切换预选。");
            Assert.That(submissions, Is.Zero, "打开详情不得提交。");
            var shell = Field(dialog, "shell");
            var viewer = Field(shell, "cardViewer");
            Assert.That(viewer, Is.Not.Null);
            Assert.That(Property<bool>(viewer, "IsShowing"), Is.True);
            Property<ScrollRect>(page, "OptionScroll").horizontalNormalizedPosition = 0;
            Call(viewer, "OnPointerClick", new PointerEventData(EventSystem.current)
                { button = PointerEventData.InputButton.Left, clickCount = 1 });
            Refresh(page);
            Assert.That(Page(dialog), Is.SameAs(page));
            Assert.That(Property<float>(page, "ScrollPosition"), Is.EqualTo(.27f).Within(.002f));
            Assert.That(Property<Image>(cards[1], "SelectionImage").enabled, Is.True);
            Assert.That(selectCalls, Is.Zero);
            Assert.That(submissions, Is.Zero);
        }

        [Test]
        public void CardSelectionIsDraftOnly_AndReadOnlyCardsHaveNoConfirmation()
        {
            var ids = FacilityIds(2);
            var dialog = NewDialog("CharacterCardEffectChoiceDialog");
            var spec = NewSelection(ids);
            string selected = null;
            var submissions = 0;
            Set(spec, "Select", new Action<string>(id => selected = id));
            Set(spec, "Confirm", new Action(() => submissions++));
            ShowSelection(dialog, spec, false);
            var confirm = FindButton(Page(dialog), "Confirm Selection");
            Assert.That(confirm.interactable, Is.False);
            Property<Button>(Cards(Page(dialog))[1], "Button").onClick.Invoke();
            Assert.That(selected, Is.EqualTo(ids[1]));
            Assert.That(submissions, Is.Zero);
            Set(spec, "SelectedIds", new[] { selected });
            ShowSelection(dialog, spec, true);
            confirm = FindButton(Page(dialog), "Confirm Selection");
            Assert.That(confirm.interactable, Is.True);
            confirm.onClick.Invoke();
            confirm.onClick.Invoke();
            Assert.That(submissions, Is.EqualTo(1));
            Set(spec, "ReadOnly", true);
            ShowSelection(dialog, spec, false);
            Assert.That(FindButton(Page(dialog), "Confirm Selection"), Is.Null);
            foreach (var card in Cards(Page(dialog)))
            {
                Assert.That(Property<Button>(card, "Button").interactable, Is.False);
                OpenCardPreview(Property<Button>(card, "Button"));
                var viewer = Field(Field(dialog, "shell"), "cardViewer");
                Assert.That(Property<bool>(viewer, "IsShowing"), Is.True,
                    "只读卡牌仍可通过查看手势打开。");
                Call(viewer, "Close");
            }
        }

        [Test]
        public void ActionSelection_BackgroundClickDoesNotCancelBusinessSelection()
        {
            var dialog = NewDialog("CharacterCardEffectChoiceDialog");
            var spec = NewSelection(FacilityIds(2));
            var cancellations = 0;
            ((InteractionRequestProjection)Field(spec, "Request")).AllowDecline = true;
            Set(spec, "CloseOnBackgroundClick", true);
            Set(spec, "Cancel", new Action(() => cancellations++));
            ShowSelection(dialog, spec, false);
            var page = Page(dialog);
            var background = Property<Image>(page, "OverlayImage").gameObject;
            ExecuteEvents.ExecuteHierarchy(background, new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                pointerPressRaycast = new RaycastResult { gameObject = background }
            }, ExecuteEvents.pointerClickHandler);
            Assert.That(cancellations, Is.Zero, "空白关闭只允许显式启用的只读列表使用。");
            var cancel = FindButton(page, "Cancel Selection");
            Assert.That(cancel, Is.Not.Null);
            cancel.onClick.Invoke();
            Assert.That(cancellations, Is.EqualTo(1));
        }

        private static void OpenCardPreview(Button card)
        {
            ExecuteEvents.Execute(card.gameObject, new PointerEventData(EventSystem.current)
                { button = PointerEventData.InputButton.Right, clickCount = 1 }, ExecuteEvents.pointerClickHandler);
        }

        private object NewDialog(string name)
        {
            var dialog = Activator.CreateInstance(RuntimeType(name), InstanceFlags, null,
                new object[] { registry, (RectTransform)canvasRoot.transform }, null);
            dialogs.Add(dialog);
            return dialog;
        }

        private object NewSelection(IReadOnlyList<string> ids)
        {
            var spec = Activator.CreateInstance(RuntimeType("EffectDialogSelectionSpec"), true);
            Set(spec, "Request", new InteractionRequestProjection
            {
                InteractionId = "card-picker-ui-test", VisibleToViewer = true, Status = "open",
                CandidateIds = new List<string>(ids), MinSelections = 1, MaxSelections = 1
            });
            Set(spec, "SelectedIds", new string[0]);
            Set(spec, "IsEffectPage", false);
            Set(spec, "Label", new Func<string, string>(id => id));
            Set(spec, "IsCurrent", new Func<bool>(() => true));
            return spec;
        }

        private string[] FacilityIds(int count)
        {
            Assert.That(count, Is.LessThanOrEqualTo(41));
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = "building_" + (i + 1).ToString("000");
            return result;
        }

        private static void ShowSelection(object dialog, object spec, bool preserveScroll) =>
            Call(dialog, "ShowSelection", "卡牌选择验证", "验证显示与操作，不进行规则结算。", spec, preserveScroll);
        private static Component Page(object dialog) => (Component)Field(Field(dialog, "shell"), "view");
        private static Component[] Cards(Component page) =>
            page.GetComponentsInChildren(RuntimeType("FacilityEffectCardView"), false);
        private static Button FindButton(Component page, string name) =>
            Array.Find(page.GetComponentsInChildren<Button>(false), button => button.name == name);
        private static Type RuntimeType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Field(object source, string name) => source.GetType().GetField(name, InstanceFlags).GetValue(source);
        private static void Set(object source, string name, object value) => source.GetType().GetField(name, InstanceFlags).SetValue(source, value);
        private static T Property<T>(object source, string name) => (T)source.GetType().GetProperty(name, InstanceFlags).GetValue(source);
        private static object Call(object source, string name, params object[] args) =>
            source.GetType().GetMethod(name, InstanceFlags).Invoke(source, args);
        private static void Refresh(Component page)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.transform);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.transform);
            Canvas.ForceUpdateCanvases();
        }
        private static Rect Bounds(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }
        private static void AssertWithin(RectTransform child, RectTransform parent)
        {
            var c = Bounds(child);
            var p = Bounds(parent);
            const float tolerance = .25f;
            Assert.That(c.xMin, Is.GreaterThanOrEqualTo(p.xMin - tolerance), child.name);
            Assert.That(c.yMin, Is.GreaterThanOrEqualTo(p.yMin - tolerance), child.name);
            Assert.That(c.xMax, Is.LessThanOrEqualTo(p.xMax + tolerance), child.name);
            Assert.That(c.yMax, Is.LessThanOrEqualTo(p.yMax + tolerance), child.name);
        }
    }
}
