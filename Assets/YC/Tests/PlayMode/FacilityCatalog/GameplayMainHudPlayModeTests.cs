using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Application.Sessions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class GameplayMainHudPlayModeTests
    {
        [UnityTest]
        public IEnumerator BothScenes_InformationEntriesOpenAndCloseConfiguredPages()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ConfigureLocalContext(scene);
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var pages = Find("YC.Presentation.GameplayInformationPages");
                Assert.That(pages, Is.Not.Null);
                foreach (var entry in new[] { ("RegionButton", "RegionPage"), ("GameBoxButton", "GameBoxPage") })
                {
                    var button = (Button)Property(pages, entry.Item1);
                    Assert.That(button, Is.Not.Null);
                    Assert.That(button.interactable, Is.True);
                    button.onClick.Invoke();
                    yield return null;
                    var page = (Component)Property(pages, entry.Item2);
                    Assert.That(page, Is.Not.Null);
                    Assert.That(page.gameObject.activeInHierarchy, Is.True);
                    Assert.That((bool)pages.GetType().GetMethod("TryHandleEscape").Invoke(pages, null), Is.True);
                    yield return null;
                    Assert.That(page == null || !page.gameObject.activeInHierarchy, Is.True);
                }
            }
        }

        [UnityTest]
        public IEnumerator BothScenes_UseOneBottomActionAndTabbedFormalEntries()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ConfigureLocalContext(scene);
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                Assert.That(hud, Is.Not.Null, scene);
                var args = new object[] { null };
                Assert.That((bool)hud.GetType().GetMethod("TryValidateConfiguration")
                    .Invoke(hud, args), Is.True, args[0] as string);
                var action = Property(hud, "ActionPanelView");
                var frame = Property(hud, "Frame");
                var end = (Button)Property(action, "EndRoundButton");
                Assert.That(end, Is.SameAs(Property(frame, "EndActionButton")), scene);
                Assert.That(FirstHit(end.transform as RectTransform), Is.SameAs(end.gameObject), scene);
                var build = (Button)Property(action, "BuildButton");
                var special = (Button)Property(action, "SpecialButton");
                Assert.That(build, Is.Not.Null, scene);
                Assert.That(special, Is.Not.Null, scene);
                Assert.That(special.interactable, Is.False, "尚无独立的特殊行动入口能力时应禁用");

                var tabs = Find("YC.Presentation.UiMainActionTabs");
                Assert.That(tabs, Is.Not.Null, scene);
                var buttons = (Button[])Field(tabs, "tabs");
                var sections = (GameObject[])Field(tabs, "sections");
                Assert.That(buttons, Has.Length.EqualTo(3));
                for (var i = 0; i < 3; i++)
                {
                    Assert.That(FirstHit(buttons[i].transform as RectTransform),
                        Is.SameAs(buttons[i].gameObject), scene + " 页签被装饰层遮挡");
                    buttons[i].onClick.Invoke();
                    Assert.That((int)Property(tabs, "SelectedIndex"), Is.EqualTo(i));
                    for (var j = 0; j < 3; j++)
                        Assert.That(sections[j].activeSelf, Is.EqualTo(j == i),
                            scene + " 页签显示了错误行动组");
                }
                buttons[0].onClick.Invoke();
                Assert.That(build.gameObject.activeInHierarchy, Is.True);
                Assert.That(special.gameObject.activeInHierarchy, Is.True);
            }
        }

        [UnityTest]
        public IEnumerator TabState_DisabledOverridesSelection_AndRetainsSelectionOnExit()
        {
            yield return ConfigureLocalContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var tabs = Find("YC.Presentation.UiMainActionTabs");
            var buttons = (Button[])Field(tabs, "tabs");
            var state = buttons[0].GetComponent(Type.GetType(
                "YC.Presentation.UiMainButtonState, Assembly-CSharp", true));
            var image = buttons[0].GetComponent<Image>();
            var selected = (Sprite)Field(state, "selectedFace");
            var selectedHover = (Sprite)Field(state, "selectedHover");
            var disabled = (Sprite)Field(state, "disabled");
            Assert.That(image.sprite, Is.SameAs(selected));
            var pointer = new PointerEventData(EventSystem.current);
            state.GetType().GetMethod("OnPointerEnter").Invoke(state, new object[] { pointer });
            Assert.That(image.sprite, Is.SameAs(selectedHover));
            state.GetType().GetMethod("OnPointerExit").Invoke(state, new object[] { pointer });
            Assert.That(image.sprite, Is.SameAs(selected));
            state.GetType().GetMethod("SetAvailable").Invoke(state, new object[] { false });
            Assert.That(image.sprite, Is.SameAs(disabled));
            state.GetType().GetMethod("SetAvailable").Invoke(state, new object[] { true });
            Assert.That(image.sprite, Is.SameAs(selected));
        }

        [UnityTest]
        public IEnumerator ResourceProjection_UsesTopbarTextBindingsAndDiscardEntryOpensPreview()
        {
            yield return ConfigureLocalContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var modules = Property(hud, "MainModules");
            var state = new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        Resources = new ResourceSet
                        {
                            Originium = 3,
                            OriginiumShard = 5,
                            Iron = 7,
                            PureOriginium = 11,
                            GoldVoucher = 13
                        }
                    }
                }
            };
            var registry = Property(hud, "DialogRegistry");
            var catalog = Property(registry, "CardVisualCatalog");
            modules.GetType().GetMethod("Render").Invoke(modules,
                new[] { state, null, (object)1, catalog });
            var values = (Text[])Field(modules, "localResourceValues");
            CollectionAssert.AreEqual(new[] { "3", "5", "7", "11" },
                Array.ConvertAll(values, value => value.text));
            Assert.That(((Text)Field(modules, "localVoucherValue")).text, Is.EqualTo("13"));

            modules.GetType().GetMethod("RenderDiscardCount").Invoke(modules, new object[] { 4 });
            Assert.That(((Text)Property(modules, "DiscardCountText")).text, Is.EqualTo("4"));
            var button = (Button)Field(modules, "discardPreviewButton");
            Assert.That(button.interactable, Is.True);
            button.onClick.Invoke();
            var panel = Find("YC.Presentation.CharacterHandPanel");
            Assert.That((bool)Property(panel, "IsDiscardPreviewOpen"), Is.True);
            var discardPage = Field(panel, "discardPage");
            var discardView = (Component)Field(discardPage, "view");
            ClickDiscardBackground(discardView);
            Assert.That((bool)Property(panel, "IsDiscardPreviewOpen"), Is.False);
        }

        [UnityTest]
        public IEnumerator BuildButton_OpensAndCancelsTheFormalBuildDialog()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("此用例使用正式右卡行动阶段启动状态。");
            yield return ConfigureLocalContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var controller = Find("YC.Presentation.MobileCityInteractionController");
            var presenter = Field(controller, "turnActionPresenter");
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var action = Property(hud, "ActionPanelView");
            var build = (Button)Property(action, "BuildButton");
            var frame = Property(hud, "Frame");
            var undo = (Button)Property(frame, "UndoButton");
            Assert.That(build.interactable, Is.True, "建设入口应使用当前主要行动能力");
            build.onClick.Invoke();
            yield return null;
            var interaction = Property(presenter, "BuildInteraction");
            var page = (Component)Field(controller, "facilityBuildPage");
            Assert.That((bool)Property(interaction, "IsActive"), Is.True);
            Assert.That(page.gameObject.activeInHierarchy, Is.True, "应打开当前正式建设页");
            Assert.That(undo.interactable, Is.True, "未提交建设选择应允许底栏撤销");
            undo.onClick.Invoke();
            yield return null;
            Assert.That((bool)Property(interaction, "IsActive"), Is.False);
            Assert.That(page.gameObject.activeInHierarchy, Is.False);
            Assert.That(undo.interactable, Is.False);
        }

        [UnityTest]
        public IEnumerator CityStyleTab_OpensAndClosesTheFormalQuickActionPreview()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),
                    "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("此用例使用正式右卡行动阶段启动状态。");
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ConfigureLocalContext(scene);
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var currentHud = Find("YC.Presentation.GameplayInteractionHudView");
                var tabs = Find("YC.Presentation.UiMainActionTabs");
                ((Button[])Field(tabs, "tabs"))[2].onClick.Invoke();
                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                var action = Property(hud, "ActionPanelView");
                var declare = (Button)Property(action, "DeclareCityStyleButton");
                Assert.That(declare.gameObject.activeInHierarchy, Is.True, scene);
                Assert.That(declare.interactable, Is.True, scene);
                declare.onClick.Invoke();
                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var workflow = Field(controller, "workflowView");
                var preview = Field(workflow, "cityStyleDeclarationDialog");
                Assert.That((bool)Property(preview, "IsShowing"), Is.True, scene);
                var previewView = Field(preview, "view");
                ((Button)Property(previewView, "CloseButton")).onClick.Invoke();
                Assert.That((bool)Property(preview, "IsShowing"), Is.False, scene);
                Assert.That((int)Property(tabs, "SelectedIndex"), Is.EqualTo(2), scene);
            }
        }

        [UnityTest]
        public IEnumerator CardPicker_HorizontalWheelAndScrollbarReachLastCard()
        {
            yield return ConfigureLocalContext();
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            yield return null;
            var hud = Find("YC.Presentation.GameplayInteractionHudView");
            var frame = Property(hud, "Frame");
            var registry = Property(hud, "DialogRegistry");
            var controller = Find("YC.Presentation.MobileCityInteractionController");
            var state = (GameState)Property(Field(controller, "session"), "State");
            var initialPhase = state.Phase;
            var initialPlayer = state.CurrentPlayerId;
            var catalog = Property(registry, "CardVisualCatalog");
            var fronts = CharacterCardDatabase.GetInitialCardIds(state.FindPlayer(initialPlayer));
            Assert.That(fronts.Count, Is.GreaterThan(0));
            var page = registry.GetType().GetMethod("InstantiateEffectDialogShell")
                .Invoke(registry, new object[] { Property(frame, "ContentRect"), false, false, true }) as Component;
            Assert.That(page, Is.Not.Null);
            try
            {
                Assert.That((bool)Property(page, "IsCardPicker"), Is.True);
                page.GetType().GetMethod("PrepareForUse").Invoke(page, new object[]
                    { "卡牌滚动输入验证", "卡牌选择窗口", new Vector2(1774, 887), Vector2.zero, true });
                page.GetType().GetMethod("ConfigureHeading").Invoke(page, new object[]
                    { "卡牌滚动输入验证", "真实素材组件诊断，不提交游戏命令。", 34f,
                        "Diagnostic Title", "Diagnostic Description", 40 });
                page.GetType().GetMethod("ConfigureCardScroll").Invoke(page,
                    new object[] { new Vector2(180, 255), 180f });
                var cards = new List<Component>();
                // 仅增加运行中的显示项，用正式素材验证九张卡的溢出，不构造规则候选或结算命令。
                for (var i = 0; i < 9; i++)
                {
                    var card = (Component)page.GetType().GetMethod("CreateFacilityCard").Invoke(page, null);
                    card.name = "滚动诊断卡牌 " + i;
                    var image = (RawImage)Property(card, "CardImage");
                    var sprite = (Sprite)catalog.GetType().GetMethod("GetCharacterFront").Invoke(catalog, new object[] { fronts[i % fronts.Count] });
                    Type.GetType("YC.Presentation.CardArtworkView, Assembly-CSharp", true).GetMethod("Set").Invoke(null, new object[] { image, sprite });
                    Assert.That(image.texture, Is.Not.Null);
                    image.color = Color.white;
                    ((Text)Property(card, "FallbackLabel")).gameObject.SetActive(false);
                    ((Button)Property(card, "Button")).onClick.RemoveAllListeners();
                    cards.Add(card);
                }
                yield return null;
                yield return null;
                RefreshPageLayout(page);
                var scroll = (ScrollRect)Property(page, "OptionScroll");
                Assert.That(scroll.horizontal, Is.True);
                Assert.That(scroll.vertical, Is.False);
                Assert.That(scroll.content.rect.width, Is.GreaterThan(scroll.viewport.rect.width));
                var firstCardButton = (Button)Property(cards[0], "Button");
                var wheelHit = FirstRaycast((RectTransform)firstCardButton.transform);
                Assert.That(wheelHit.gameObject, Is.SameAs(firstCardButton.gameObject));
                var beforeWheel = scroll.horizontalNormalizedPosition;
                var wheel = new PointerEventData(EventSystem.current)
                {
                    position = wheelHit.screenPosition,
                    pointerCurrentRaycast = wheelHit,
                    scrollDelta = Vector2.up
                };
                var wheelReceiver = ExecuteEvents.ExecuteHierarchy(wheelHit.gameObject, wheel, ExecuteEvents.scrollHandler);
                Assert.That(wheelReceiver, Is.SameAs(scroll.gameObject),
                    "卡面上的滚轮事件必须沿真实层级到达本页 ScrollRect。");
                yield return null;
                Assert.That(scroll.horizontalNormalizedPosition, Is.GreaterThan(beforeWheel),
                    "垂直滚轮输入必须推动横向列表。");

                var bar = scroll.horizontalScrollbar;
                Assert.That(bar, Is.Not.Null);
                Assert.That(bar.gameObject.activeInHierarchy, Is.True);
                var dragHit = FirstRaycast(bar.handleRect);
                Assert.That(dragHit.gameObject, Is.Not.Null);
                Assert.That(ExecuteEvents.GetEventHandler<IDragHandler>(dragHit.gameObject),
                    Is.SameAs(bar.gameObject), "滚动条滑块的实际首命中必须可以拖动本页滚动条。");
                var drag = new PointerEventData(EventSystem.current)
                {
                    button = PointerEventData.InputButton.Left,
                    position = dragHit.screenPosition,
                    pressPosition = dragHit.screenPosition,
                    pointerPressRaycast = dragHit,
                    pointerCurrentRaycast = dragHit,
                    pointerDrag = bar.gameObject,
                    useDragThreshold = false
                };
                ExecuteEvents.ExecuteHierarchy(dragHit.gameObject, drag, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.beginDragHandler);
                drag.dragging = true;
                var slidingArea = (RectTransform)bar.handleRect.parent;
                var right = RectTransformUtility.WorldToScreenPoint(drag.pressEventCamera,
                    slidingArea.TransformPoint(new Vector2(slidingArea.rect.xMax - .5f, slidingArea.rect.center.y)));
                var start = drag.position;
                for (var step = 1; step <= 3; step++)
                {
                    var next = Vector2.Lerp(start, right, step / 3f);
                    drag.delta = next - drag.position;
                    drag.position = next;
                    ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.dragHandler);
                    yield return null;
                }
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.endDragHandler);
                ExecuteEvents.Execute(bar.gameObject, drag, ExecuteEvents.pointerUpHandler);
                drag.dragging = false;
                RefreshPageLayout(page);
                Assert.That(scroll.horizontalNormalizedPosition, Is.GreaterThan(.99f),
                    "滚动条拖至右端应访问列表末尾，不能直接赋值代替输入。");
                var lastImage = ((RawImage)Property(cards[8], "CardImage")).rectTransform;
                AssertHorizontalInside(lastImage, scroll.viewport, "末张卡面");
                AssertVerticalInside(lastImage, scroll.viewport, "末张卡面");
                Assert.That(state.Phase, Is.EqualTo(initialPhase));
                Assert.That(state.CurrentPlayerId, Is.EqualTo(initialPlayer));
            }
            finally
            {
                frame.GetType().GetMethod("ReleasePage").Invoke(frame, new object[] { page.gameObject });
                UnityEngine.Object.Destroy(page.gameObject);
            }
        }

        [UnityTest]
        public IEnumerator RightColumn_OverflowScrollsOnlyInsideTheActionBody()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ConfigureLocalContext(scene);
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                var column = Child(hud.transform, "Actions Column") as RectTransform;
                Assert.That(column.GetComponent<ScrollRect>(), Is.Null, "整列不得滚动。");
                var tabs = Find("YC.Presentation.UiMainActionTabs");
                var buttons = (Button[])Field(tabs, "tabs");
                buttons[1].onClick.Invoke();
                var group = Child(hud.transform, "Quick Actions") as RectTransform;
                var original = group.GetChild(0);
                Transform last = original;
                // 只扩充运行中的 UI 内容验证溢出，不伪造业务候选或写回资产。
                for (var i = 0; i < 12; i++)
                { last = UnityEngine.Object.Instantiate(original, group, false); last.gameObject.SetActive(true); }
                yield return null;
                yield return null;
                Canvas.ForceUpdateCanvases();
                var face = (GameObject)Property(Property(hud, "ActionPanelView"), "MainFaceObject");
                var scroll = face.GetComponent<ScrollRect>();
                Assert.That(scroll, Is.Not.Null);
                Assert.That(scroll.content.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
                foreach (var name in new[] { "Cooperation Region", "Deck Row", "Action Tabs", "Action Region" })
                    AssertVerticalInside(Child(column, name) as RectTransform, column, name);
                var outerBefore = column.anchoredPosition;
                scroll.StopMovement();
                scroll.verticalNormalizedPosition = 0;
                yield return null;
                Canvas.ForceUpdateCanvases();
                AssertVerticalInside(last as RectTransform, scroll.viewport, "快速行动末项必须可达");
                Assert.That(column.anchoredPosition, Is.EqualTo(outerBefore), "正文滚动不能移动右列。");
                foreach (var name in new[] { "Cooperation Region", "Deck Row", "Action Tabs", "Action Region" })
                    AssertVerticalInside(Child(column, name) as RectTransform, column, name);
                buttons[2].onClick.Invoke();
                yield return null;
                Assert.That(group.gameObject.activeSelf, Is.False, "隐藏长列表不应占据城市样式页。");
                AssertVerticalInside(Child(column, "City Style Actions") as RectTransform,
                    scroll.viewport, "城市样式页");
                buttons[0].onClick.Invoke();
                yield return null;
                AssertVerticalInside(Child(column, "Main Actions") as RectTransform,
                    scroll.viewport, "主要行动页");
            }
        }

        [UnityTest]
        public IEnumerator CardViewer_HandAndFacilityDetailsStayReadOnlyAndReturn()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-dev-right-card-smoke-no-dialog") < 0)
                Assert.Ignore("沿用项目行动阶段测试起点；不代表从大厅开始的联机验收。");
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ConfigureLocalContext(scene);
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var session = Field(controller, "session");
                var state = (GameState)Property(session, "State");
                var player = state.FindPlayer((int)Field(controller, "localPlayerId"));
                var hand = Find("YC.Presentation.CharacterHandPanel");
                var id = player.HandCardIds[0];
                var card = Child(hand.transform, "Card Slot: " + id);
                var pointerType = Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", true);
                var revision = state.EffectRuntime.StateRevision;
                Assert.That(card, Is.Not.Null, "正式手牌实例必须存在。");
                yield return ClickVisibleCard((RectTransform)card);
                AssertInspectAndClose(state, revision);
                Assert.That(player.HandCardIds, Does.Contain(id));

                var hud = Find("YC.Presentation.GameplayInteractionHudView");
                var build = Property(hud, "BuildInfoPanel");
                var slots = (Array)Property(Property(build, "View"), "CityBoardSlots");
                Component facilityPointer = null;
                foreach (var slot in slots)
                {
                    var root = (RectTransform)Property(slot, "Root");
                    var pointer = root.GetComponent(pointerType);
                    var image = root.GetComponentInChildren<RawImage>();
                    if (pointer != null && image != null && image.texture != null) { facilityPointer = pointer; break; }
                }
                Assert.That(facilityPointer, Is.Not.Null, "必须使用当前城市的真实设施卡入口。");
                yield return ClickVisibleCard((RectTransform)facilityPointer.transform);
                AssertInspectAndClose(state, revision);
            }
        }

        [UnityTest]
        public IEnumerator InformationPages_FormalSettingsRulebookPagingImagePanAndLongLog()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                yield return ConfigureLocalContext(scene);
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var settings = Find("YC.Presentation.GameSettingsMenuController");
                var settingsView = Field(settings, "view");
                settings.GetType().GetMethod("Open").Invoke(settings, null);
                ((Button)Property(settingsView, "RulebookButton")).onClick.Invoke();
                yield return null;
                var rulebook = Field(settings, "rulebookViewer");
                var ruleView = Field(rulebook, "view");
                var imageController = Property(ruleView, "ImageViewer");
                var imageView = Field(imageController, "view");
                var next = (Button)Property(imageView, "NextButton");
                var previous = (Button)Property(imageView, "PreviousButton");
                var pageCount = (int)Property(ruleView, "PageCount");
                for (var page = 1; page < pageCount; page++)
                {
                    next.onClick.Invoke();
                    Assert.That((int)Property(imageController, "PageIndex"), Is.EqualTo(page));
                    Assert.That(((RawImage)Property(imageView, "Image")).texture, Is.Not.Null);
                }
                Assert.That(next.interactable, Is.False, "规则书末页不能越界。");
                previous.onClick.Invoke();
                Assert.That((int)Property(imageController, "PageIndex"), Is.EqualTo(pageCount - 2));
                imageController.GetType().GetMethod("SetZoom").Invoke(imageController, new object[] { 2f });
                yield return null;
                Canvas.ForceUpdateCanvases();
                var panel = (RectTransform)Property(imageView, "PanelTransform");
                var imageRect = (RectTransform)Property(imageView, "ImageTransform");
                var before = panel.anchoredPosition;
                var imageBefore = imageRect.anchoredPosition;
                var scroll = panel.GetComponentInChildren<ScrollRect>();
                var position = RectTransformUtility.WorldToScreenPoint(null, scroll.viewport.TransformPoint(scroll.viewport.rect.center));
                var drag = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = position };
                scroll.OnBeginDrag(drag);
                drag.position += new Vector2(25, 30);
                scroll.OnDrag(drag); scroll.OnEndDrag(drag); scroll.StopMovement();
                Assert.That(imageRect.anchoredPosition, Is.Not.EqualTo(imageBefore), "缩放后的图片应可内部平移。");
                Assert.That(panel.anchoredPosition, Is.EqualTo(before), "图片平移不能拖动整个窗口。");
                ((Button)Property(imageView, "CloseButton")).onClick.Invoke();

                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var state = (GameState)Property(Field(controller, "session"), "State");
                var revision = state.EffectRuntime.StateRevision;
                for (var i = 0; i < 80; i++) state.Logs.Add(new GameLogEntry { Sequence = 10000 + i,
                    PlayerId = state.Players[0].PlayerId, Message = "查看器滚动诊断记录 " + i });
                settings.GetType().GetMethod("Open").Invoke(settings, null);
                ((Button)Property(settingsView, "ActionLogButton")).onClick.Invoke();
                yield return null;
                Canvas.ForceUpdateCanvases();
                var log = Field(settings, "actionLogViewer");
                var logView = Field(log, "view");
                var content = (RectTransform)Property(logView, "ContentTransform");
                var logScroll = content.GetComponentInParent<ScrollRect>();
                Assert.That(content.rect.height, Is.GreaterThan(logScroll.viewport.rect.height));
                logScroll.verticalNormalizedPosition = 0f;
                Canvas.ForceUpdateCanvases();
                var last = (RectTransform)content.GetChild(content.childCount - 1);
                AssertVerticalInside(last, logScroll.viewport, "日志末项");
                ((Button)Property(logView, "CloseButton")).onClick.Invoke();
                Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision), "资料操作不得发出游戏命令。");
            }
        }

        private static void ClickDiscardBackground(Component page)
        {
            var background = ((Image)Property(page, "OverlayImage")).gameObject;
            ExecuteEvents.ExecuteHierarchy(background, new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                pointerPressRaycast = new RaycastResult { gameObject = background },
                pointerCurrentRaycast = new RaycastResult { gameObject = background }
            }, ExecuteEvents.pointerClickHandler);
            Assert.That(page == null || !page.gameObject.activeInHierarchy, Is.True,
                "点击弃牌页空白处应关闭列表。");
        }

        private static IEnumerator ClickVisibleCard(RectTransform card)
        {
            // 牌堆允许重叠；从牌面实际露出的区域点击，不要求牌面中心始终在最上层。
            yield return null;
            Canvas.ForceUpdateCanvases();
            var canvas = card.GetComponentInParent<Canvas>().rootCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var expected = ExecuteEvents.GetEventHandler<IPointerClickHandler>(card.gameObject);
            Assert.That(expected, Is.Not.Null, card.name);
            var hits = new List<RaycastResult>();
            foreach (var x in new[] { .5f, .2f, .8f })
            foreach (var y in new[] { .5f, .2f, .8f })
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, card.TransformPoint(new Vector2(
                    Mathf.Lerp(card.rect.xMin, card.rect.xMax, x), Mathf.Lerp(card.rect.yMin, card.rect.yMax, y))));
                if (point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height) continue;
                var input = new PointerEventData(EventSystem.current)
                {
                    position = point, pressPosition = point, button = PointerEventData.InputButton.Left,
                    eligibleForClick = true, clickCount = 1
                };
                hits.Clear();
                EventSystem.current.RaycastAll(input, hits);
                if (hits.Count == 0 || ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject) != expected) continue;
                input.pointerCurrentRaycast = input.pointerPressRaycast = hits[0];
                var pressed = ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, input, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(pressed, input, ExecuteEvents.pointerUpHandler);
                Assert.That(ExecuteEvents.Execute(expected, input, ExecuteEvents.pointerClickHandler), Is.True);
                yield return null;
                yield break;
            }
            Assert.Fail("卡牌没有可点击的可见区域：" + card.name);
        }

        private static void AssertInspectAndClose(GameState state, int revision)
        {
            var viewer = Find("YC.Presentation.CardViewer");
            Assert.That(viewer, Is.Not.Null);
            Assert.That(Property(viewer, "Mode").ToString(), Is.EqualTo("Inspect"));
            Assert.That((string)Property(viewer, "CardId"), Is.Empty);
            Assert.That(((Button)Property(viewer, "PlotButton")).gameObject.activeInHierarchy, Is.False);
            viewer.GetType().GetMethod("Close").Invoke(viewer, null);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision), "资料查看不能提交命令。");
        }

        private static void AssertVerticalInside(RectTransform child, RectTransform parent, string context)
        {
            var a = new Vector3[4]; var b = new Vector3[4];
            child.GetWorldCorners(a); parent.GetWorldCorners(b);
            Assert.That(a[0].y, Is.GreaterThanOrEqualTo(b[0].y - .5f), context + " 底端越界");
            Assert.That(a[2].y, Is.LessThanOrEqualTo(b[2].y + .5f), context + " 顶端越界");
        }

        private static void AssertHorizontalInside(RectTransform child, RectTransform parent, string context)
        {
            var a = new Vector3[4]; var b = new Vector3[4];
            child.GetWorldCorners(a); parent.GetWorldCorners(b);
            Assert.That(a[0].x, Is.GreaterThanOrEqualTo(b[0].x - .5f), context + " 左端越界");
            Assert.That(a[2].x, Is.LessThanOrEqualTo(b[2].x + .5f), context + " 右端越界");
        }

        private static void RefreshPageLayout(Component page)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)page.transform);
            Canvas.ForceUpdateCanvases();
        }

        private static Component Find(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp", true);
            return UnityEngine.Object.FindObjectOfType(type) as Component;
        }

        // 隔离场景交互测试使用正式开局配置；正常菜单流程由独立 Player 验收。
        private static IEnumerator ConfigureLocalContext(string scene = "SampleScene")
        {
            var previous = Find("YC.Presentation.GameLaunchContext");
            if (previous != null)
            {
                UnityEngine.Object.Destroy(previous.gameObject);
                yield return null;
            }
            var type = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var context = type.GetMethod("Ensure").Invoke(null, null);
            var count = scene == "ThreePlayerScene" ? 3 : 4;
            var seats = new List<PlayerSeat>();
            for (var id = 1; id <= count; id++) seats.Add(new PlayerSeat { PlayerId = id });
            type.GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(string) })
                .Invoke(context, new object[] { LaunchMode.Local, 1, "scene-interaction-test", seats,
                    count == 3 ? StaticMapDefinitions.ThreePlayerMapId : StaticMapDefinitions.FourPlayerMapId });
        }

        private static object Property(object target, string name)
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                .GetValue(target);
        }

        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(target);
        }

        private static GameObject FirstHit(RectTransform target)
        {
            return FirstRaycast(target).gameObject;
        }

        private static RaycastResult FirstRaycast(RectTransform target)
        {
            Canvas.ForceUpdateCanvases();
            var point = RectTransformUtility.WorldToScreenPoint(null,
                target.TransformPoint(target.rect.center));
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, results);
            return results.Count == 0 ? default(RaycastResult) : results[0];
        }

        private static Transform Child(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = Child(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
