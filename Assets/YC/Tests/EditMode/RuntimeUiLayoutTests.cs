using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class RuntimeUiLayoutTests
    {
        private GameObject owner;

        [SetUp]
        public void SetUp()
        {
            ViewerPrefabTestUtility.RegisterZoomablePrefab();
        }

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                Object.DestroyImmediate(owner);
                owner = null;
            }

            DestroyNamedObject("Action Panel Test Canvas");
            DestroyNamedObject("Build Info Panel Canvas");
            DestroyNamedObject("Info Panel Canvas");
            DestroyNamedObject("Mobile City UI Canvas");
            DestroyNamedObject("Rulebook Viewer Canvas");
            DestroyNamedObject("Shared Rulebook Viewer Canvas");
            DestroyNamedObject("Hint Card Viewer Canvas");
            DestroyNamedObject("Hint Card Image Viewer");
            DestroyNamedObject("Settings Menu Canvas");
            DestroyNamedObject("Action Log Viewer Canvas");
            DestroyNamedObject("EventSystem");
        }

        [Test]
        public void ActionPanel_FlipsBetweenEqualSizedMainAndHintCardsAndOpensHintPreview()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            var controller = BuildActionPanel(canvas);
            var characterPreviewOpened = 0;
            InvokePublic(
                controller,
                "ConfigureCharacterCardViewerAction",
                new Action(() => characterPreviewOpened += 1));

            var actionPanel = FindTransform("Action Panel");
            var hintPanel = FindTransform("Hint Card Panel");
            var mainFace = FindTransform("Main Action Face");
            var cardFace = FindTransform("Action Card Face");
            var flipButton = FindTransform("Action Panel Flip Button");
            var influenceText = FindTransform("Remaining Influence Text");
            var removedSpecialActionEntry = FindTransform("特殊行动 Button");

            Assert.That(actionPanel, Is.Not.Null);
            Assert.That(actionPanel.anchorMin, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(actionPanel.anchorMax, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(actionPanel.pivot, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(actionPanel.sizeDelta, Is.EqualTo(new Vector2(360f, 502f)));
            Assert.That(actionPanel.anchoredPosition, Is.EqualTo(Vector2.zero));
            Assert.That(hintPanel, Is.Null);
            Assert.That(mainFace, Is.Not.Null);
            Assert.That(cardFace, Is.Not.Null);
            Assert.That(mainFace.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(mainFace.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(cardFace.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(cardFace.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(flipButton, Is.Not.Null);
            Assert.That(FindTransform("建设 Button"), Is.Null, "建设入口应改为直接拖动公开建设牌。");
            Assert.That(
                removedSpecialActionEntry,
                Is.Null,
                "特殊行动必须只从城市样式卡上的影响力标记拖拽发动。");
            Assert.That(
                controller.GetType().GetField("specialActionButton", BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Null,
                "行动面板不应继续保存不存在的特殊行动按钮状态。");
            Assert.That(
                typeof(ActionPanelViewModel).GetProperty("CanUseSpecialAction", BindingFlags.Instance | BindingFlags.Public),
                Is.Null,
                "行动面板 ViewModel 不应继续暴露无消费者的特殊行动按钮状态。");
            Assert.That(
                controller.GetType().GetMethod("Build", BindingFlags.Static | BindingFlags.Public),
                Is.Null,
                "行动面板不应保留运行时 UI 构建入口。");
            var bindParameters = controller.GetType()
                .GetMethod("Bind", BindingFlags.Static | BindingFlags.Public)
                .GetParameters();
            Assert.That(
                Array.Exists(bindParameters, parameter => parameter.Name == "onSpecialAction"),
                Is.False,
                "行动面板绑定 API 不应继续要求无消费者的特殊行动回调。");
            flipButton.GetComponent<Button>().onClick.Invoke();
            Assert.That(mainFace.gameObject.activeSelf, Is.False);
            Assert.That(cardFace.gameObject.activeSelf, Is.True);
            var hintImage = FindTransform("Action Card Image");
            Assert.That(hintImage.GetComponent<RawImage>().texture, Is.Not.Null);
            Assert.That(hintImage.GetComponent<Button>().interactable, Is.True);
            hintImage.GetComponent<Button>().onClick.Invoke();
            var hintViewerCanvas = FindTransform("Hint Card Viewer Canvas");
            Assert.That(hintViewerCanvas, Is.Not.Null);
            Assert.That(
                hintViewerCanvas.IsChildOf(actionPanel),
                Is.False,
                "提示卡大图画布必须独立于右下角行动面板。只要仍在面板层级内，就会被裁切并错位。");
            Assert.That(FindTransform("Hint Card Viewer"), Is.Not.Null);
            Assert.That(FindTransform("Hint Card Image").GetComponent<RawImage>().texture,
                Is.SameAs(hintImage.GetComponent<RawImage>().texture));
            Assert.That(characterPreviewOpened, Is.Zero, "提示卡点击不应误触角色牌预览。");
            Assert.That(influenceText, Is.Not.Null);
            Assert.That(influenceText.GetComponent<Text>().text, Is.EqualTo("× 0"));

            InvokePublic(controller, "SetRemainingInfluence", 17);
            Assert.That(influenceText.GetComponent<Text>().text, Is.EqualTo("× 17"));
        }

        [Test]
        public void ActionPanel_MainFaceReservesDedicatedTopRowForFlipButton()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            BuildActionPanel(canvas);

            var flip = FindTransform("Action Panel Flip Button");
            var player = FindTransform("当前玩家 Text");
            var phase = FindTransform("阶段 Text");

            Assert.That(flip.anchoredPosition.y, Is.EqualTo(-24f));
            Assert.That(player.anchoredPosition.y, Is.EqualTo(-68f));
            Assert.That(phase.anchoredPosition.y, Is.EqualTo(-98f));
            Assert.That(
                player.anchoredPosition.y + player.rect.height * 0.5f,
                Is.LessThan(flip.anchoredPosition.y - flip.rect.height * 0.5f));
        }

        [Test]
        public void ActionPanel_UsesInsetCoveredCardBackAndOpensFrontThroughConfiguredViewerAction()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            var controller = BuildActionPanel(canvas);
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CoveredCharacterCardId = "character.red.p1.liskarm"
                    }
                }
            };
            var opened = 0;
            InvokePublic(controller, "ConfigureCharacterCardViewerAction", new Action(() => opened += 1));
            InvokePublic(controller, "ShowCharacterCard", new CharacterCardPanelPresenter().BuildView(state, 1));

            var container = FindTransform("Action Card Image Container");
            var image = FindTransform("Action Card Image");
            Assert.That(container.sizeDelta, Is.EqualTo(new Vector2(222f, 310f)));
            Assert.That(image.GetComponent<RawImage>().texture.name, Is.EqualTo("back-red"));
            Assert.That(image.GetComponent<Button>().interactable, Is.True);
            Assert.That(FindTransform("Action Card Title").GetComponent<Text>().text,
                Is.EqualTo("已盖放角色牌（雷蛇）"));
            image.GetComponent<Button>().onClick.Invoke();
            Assert.That(opened, Is.EqualTo(1));
        }

        [Test]
        public void ActionPanel_ClickingCharacterEffectRevealsCardFrontAndKeepsItRevealedAfterRefresh()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            var controller = BuildActionPanel(canvas);
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CoveredCharacterCardId = "character.red.p1.liskarm"
                    }
                }
            };
            var invoked = 0;
            var view = new CharacterCardPanelPresenter().BuildView(state, 1);
            InvokePublic(controller, "ConfigureCharacterActions", new Action(() => invoked += 1), new Action(() => { }));
            InvokePublic(controller, "ShowCharacterCard", view);

            FindTransform("Character Strategy Button").GetComponent<Button>().onClick.Invoke();

            Assert.That(invoked, Is.EqualTo(1));
            Assert.That(FindTransform("Action Card Image").GetComponent<RawImage>().texture.name,
                Does.Contain("liskarm"));
            Assert.That(FindTransform("Action Card Hint").GetComponent<Text>().text,
                Does.Contain("已翻开"));

            InvokePublic(controller, "ShowCharacterCard", view);
            Assert.That(FindTransform("Action Card Image").GetComponent<RawImage>().texture.name,
                Does.Contain("liskarm"));
        }

        [Test]
        public void ActionPanel_CharacterCoverDropZoneHidesOverlayText()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            var controller = BuildActionPanel(canvas);

            InvokePublic(controller, "ShowCharacterCoverDropZone", string.Empty);

            Assert.That(FindTransform("Action Card Face").gameObject.activeSelf, Is.True);
            Assert.That(FindTransform("Action Card Title").GetComponent<Text>().text, Is.Empty);
            Assert.That(FindTransform("Action Card Hint").GetComponent<Text>().text, Is.Empty);
        }

        [Test]
        public void ActionPanel_SecondEffectDecisionUsesRemainingButtonAndFlipDeclines()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            var controller = BuildActionPanel(canvas);
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CoveredCharacterCardId = "character.red.p1.cannot"
                    }
                },
                PendingCharacterEffect = new PendingCharacterEffectState
                {
                    ChoiceType = YC.Domain.Cards.CharacterPendingChoiceTypes.SecondEffectDecision,
                    PlayerId = 1,
                    CardId = "character.red.p1.cannot",
                    RemainingEffectMode = YC.Domain.Cards.CharacterEffectModes.Tactic,
                    OptionIds =
                    {
                        YC.Domain.Cards.CharacterEffectChoiceIds.ContinueSecondEffect,
                        YC.Domain.Cards.CharacterEffectChoiceIds.FinishCharacterUse
                    }
                }
            };
            var declined = 0;
            InvokePublic(controller, "ConfigureCharacterFlipAction", new Func<bool>(() => { declined += 1; return true; }));
            InvokePublic(controller, "ShowCharacterCard", new CharacterCardPanelPresenter().BuildView(state, 1));

            Assert.That(FindTransform("Character Strategy Button").GetComponent<Button>().interactable, Is.False);
            Assert.That(FindTransform("Character Tactic Button").GetComponent<Button>().interactable, Is.True);
            Assert.That(FindTransform("Action Card Hint").GetComponent<Text>().text,
                Does.Contain("可继续使用第二个效果").And.Contain("点击翻转"));

            FindTransform("Action Panel Flip Button").GetComponent<Button>().onClick.Invoke();
            Assert.That(declined, Is.EqualTo(1));
            Assert.That(FindTransform("Main Action Face").gameObject.activeSelf, Is.True);
            Assert.That(FindTransform("Action Card Face").gameObject.activeSelf, Is.False);
        }

        [Test]
        public void PromptPresenter_StartsHiddenInTopRightPromptArea()
        {
            var type = Type.GetType("YC.Presentation.PromptPresenter, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.PromptPresenter.");

            owner = InstantiateGameplayHudPrefab();
            owner.name = "Prompt Presenter Layout Test";
            var viewType = Type.GetType("YC.Presentation.GameplayPromptView, Assembly-CSharp", false);
            var view = owner.GetComponentInChildren(viewType, true);
            var bind = type.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public);
            Assert.That(bind, Is.Not.Null, "Missing PromptPresenter.Bind.");
            bind.Invoke(null, new[] { view });

            var promptPanel = FindTransform("Prompt Panel");
            Assert.That(promptPanel, Is.Not.Null);
            Assert.That(promptPanel.anchorMin, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(promptPanel.anchorMax, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(promptPanel.pivot, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(promptPanel.sizeDelta.x, Is.EqualTo(520f).Within(0.01f));
            Assert.That(promptPanel.anchoredPosition.x, Is.EqualTo(544f).Within(0.01f));
            Assert.That(promptPanel.anchoredPosition.y, Is.EqualTo(-128f).Within(0.01f));

            var group = promptPanel.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null);
            Assert.That(group.alpha, Is.EqualTo(0f).Within(0.001f));
            Assert.That(group.blocksRaycasts, Is.False);
        }

        [Test]
        public void InfoPanel_DoesNotContainRemovedModules()
        {
            var type = Type.GetType("YC.Presentation.ExpandableInfoPanel, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.ExpandableInfoPanel.");

            owner = InstantiateExpandableInfoPanelPrefab();
            var controller = owner.GetComponent(type);
            var viewType = Type.GetType("YC.Presentation.ExpandableInfoPanelView, Assembly-CSharp", false);
            Assert.That(viewType, Is.Not.Null);
            var panelView = owner.GetComponent(viewType);
            Assert.That((bool)type.GetMethod("Bind").Invoke(controller, new[] { panelView }), Is.True);

            var modules = GetPublicProperty<System.Collections.IEnumerable>(controller, "Modules");
            Assert.That(HasModuleTitle(modules, "提示卡"), Is.False);
            Assert.That(HasModuleTitle(modules, "玩家概览"), Is.False);
            Assert.That(HasModuleTitle(modules, "城市与行动"), Is.False);
            Assert.That(HasModuleTitle(modules, "玩家宣告"), Is.True);

            var panel = FindTransform("Sidebar Panel");
            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.anchorMin, Is.EqualTo(new Vector2(0f, 0f)));
            Assert.That(panel.anchorMax, Is.EqualTo(new Vector2(0f, 1f)));
        }

        [Test]
        public void BuildInfoPanel_StretchesFromCityStyleAreaToMapBottomWithoutOuterOutline()
        {
            var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.BuildInfoPanel.");

            owner = InstantiateBuildInfoPanelPrefab();
            var controller = owner.GetComponent(type);
            var view = type.GetProperty("View", BindingFlags.Instance | BindingFlags.Public).GetValue(controller, null);
            Assert.That(
                (bool)type.GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(controller, new[] { view }),
                Is.True);

            var panel = FindTransform(owner, "Build Sidebar Panel");
            var content = FindTransform(owner, "Content Area");
            var header = FindTransform(owner, "Header");
            var toggle = FindTransform(owner, "Toggle Button");
            var scrollView = FindTransform(owner, "Scroll View");
            var viewport = FindTransform(owner, "Viewport");
            var contentRoot = FindTransform(owner, "Content");
            var facilityArea = FindTransform(owner, "External Facility Supply Area");
            var cityStyleArea = FindTransform(owner, "External City Style Area");

            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.anchorMin, Is.EqualTo(new Vector2(0f, 0f)));
            Assert.That(panel.anchorMax, Is.EqualTo(new Vector2(0f, 1f)));
            Assert.That(panel.pivot, Is.EqualTo(new Vector2(0f, 1f)));
            Assert.That(panel.sizeDelta, Is.EqualTo(new Vector2(365f, -697f)));
            Assert.That(panel.anchoredPosition, Is.EqualTo(new Vector2(72f, -697f)));
            Assert.That(panel.offsetMin.y, Is.EqualTo(0f).Within(0.01f));
            Assert.That(panel.offsetMax.y, Is.EqualTo(-697f).Within(0.01f));
            Assert.That(panel.GetComponent<Outline>(), Is.Null);
            Assert.That(panel.GetComponent<Image>(), Is.Null);
            Assert.That(content, Is.Not.Null);
            Assert.That(content.gameObject.activeSelf, Is.True);
            Assert.That(header, Is.Null);
            Assert.That(toggle, Is.Null);
            Assert.That(scrollView, Is.Null);
            Assert.That(viewport, Is.Null);
            Assert.That(contentRoot, Is.Not.Null);
            Assert.That(contentRoot.GetComponent<ScrollRect>(), Is.Null);
            Assert.That(contentRoot.GetComponent<Mask>(), Is.Null);
            Assert.That(contentRoot.GetComponent<VerticalLayoutGroup>(), Is.Null);
            Assert.That(AllTextRenderersAreMaskable(panel), Is.True);
            Assert.That(facilityArea, Is.Not.Null);
            Assert.That(cityStyleArea, Is.Not.Null);
            Assert.That(facilityArea.GetComponent<Outline>(), Is.Null);
            Assert.That(cityStyleArea.GetComponent<Outline>(), Is.Null);
            var expectedCardAreaBackground = (Color)new Color32(57, 47, 26, 255);
            AssertColor(facilityArea.GetComponent<Image>().color, expectedCardAreaBackground);
            AssertColor(cityStyleArea.GetComponent<Image>().color, expectedCardAreaBackground);
            Assert.That(facilityArea.anchorMin, Is.EqualTo(new Vector2(0f, 1f)));
            Assert.That(facilityArea.anchorMax, Is.EqualTo(new Vector2(0f, 1f)));
            Assert.That(facilityArea.anchoredPosition, Is.EqualTo(new Vector2(72f, -17f)));
            Assert.That(facilityArea.sizeDelta, Is.EqualTo(new Vector2(365f, 350f)));
            Assert.That(cityStyleArea.anchoredPosition, Is.EqualTo(new Vector2(72f, -367f)));

            var state = RightCardSmokeStateFactory.CreateInitialState(
                LaunchMode.Local,
                1,
                new List<PlayerSeat>
                {
                    new PlayerSeat { PlayerId = 1, PlayerName = "测试玩家" }
                },
                "test",
                12345);
            InvokePublic(controller, "Refresh", state, 1);
            Canvas.ForceUpdateCanvases();

            var cityBoard = FindTransform(owner, "City Board");
            Assert.That(cityBoard, Is.Not.Null);
            Assert.That(FindTransform(owner, "Section 城市面板"), Is.Null);
            Assert.That(cityBoard.GetComponent<Outline>(), Is.Null);
            Assert.That(cityBoard.GetComponent<Image>(), Is.Null);
            Assert.That(cityBoard.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(cityBoard.anchorMax, Is.EqualTo(Vector2.one));
            var boardImage = FindTransform(owner, "城市面板底图");
            Assert.That(boardImage, Is.Not.Null);
            Assert.That(boardImage.GetComponent<AspectRatioFitter>().aspectMode,
                Is.EqualTo(AspectRatioFitter.AspectMode.FitInParent));
        }

        [Test]
        public void RulebookViewer_UsesTopRightCrossCloseButton()
        {
            var type = Type.GetType("YC.Presentation.RulebookViewerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RulebookViewerController.");

            owner = ViewerPrefabTestUtility.Instantiate(ViewerPrefabTestUtility.RulebookPrefabPath);
            var controller = owner.GetComponent(type);
            InvokePublic(controller, "Open");

            var closeButton = FindTransform("Close Rulebook Button");
            Assert.That(closeButton, Is.Not.Null);
            Assert.That(closeButton.anchorMin, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeButton.anchorMax, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeButton.pivot, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(closeButton.sizeDelta, Is.EqualTo(new Vector2(42f, 42f)));

            var label = closeButton.GetComponentInChildren<Text>(true);
            Assert.That(label, Is.Not.Null);
            Assert.That(label.text, Is.EqualTo("×"));
        }

        [Test]
        public void SettingsMenu_InGamePlacesLogNextToGearWithoutHintButton()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);

            owner = InstantiateSettingsPrefab();
            var controller = owner.GetComponent(type);
            EnsureAwakeRan(controller, "initialized");

            var gear = owner.transform.Find("Game Settings Canvas/Settings Gear Button") as RectTransform;
            var configure = type.GetMethod("ConfigureActionLog", BindingFlags.Instance | BindingFlags.Public);
            configure.Invoke(controller, new object[] { new GameSession(new GameState()) });
            var log = owner.transform.Find("Game Settings Canvas/Action Log Button") as RectTransform;
            Assert.That(owner.transform.Find("Game Settings Canvas/Hint Card Button"), Is.Null);
            Assert.That(gear, Is.Not.Null);
            Assert.That(log, Is.Not.Null);
            Assert.That(log.gameObject.activeSelf, Is.True);
            Assert.That(log.sizeDelta, Is.EqualTo(gear.sizeDelta));
            Assert.That(log.anchoredPosition.y, Is.EqualTo(gear.anchoredPosition.y).Within(0.01f));
            Assert.That(gear.anchoredPosition.x - log.anchoredPosition.x - 64f, Is.EqualTo(12.8f).Within(0.01f));
        }

        [Test]
        public void SettingsMenu_OnStartPageKeepsAuthoredActionLogButtonInactive()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            owner = InstantiateSettingsPrefab();
            var controller = owner.GetComponent(type);
            EnsureAwakeRan(controller, "initialized");

            InvokePublic(controller, "SetReturnToStartButtonVisible", false);

            var actionLog = owner.transform.Find("Game Settings Canvas/Action Log Button");
            Assert.That(actionLog, Is.Not.Null);
            Assert.That(actionLog.gameObject.activeSelf, Is.False);
            Assert.That(owner.transform.Find("Game Settings Canvas/Hint Card Button"), Is.Null);
            Assert.That(owner.transform.Find("Game Settings Canvas/Settings Gear Button"), Is.Not.Null);
        }

        [Test]
        public void ZoomableImageViewer_ProvidesReusableZoomDragAndCloseSurface()
        {
            var type = Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);

            owner = ViewerPrefabTestUtility.Instantiate(ViewerPrefabTestUtility.ZoomablePrefabPath);
            owner.name = "Reusable Image Viewer Test";
            var controller = owner.GetComponent(type);
            var texture = new Texture2D(100, 200);
            var configure = type.GetMethod("Configure", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(configure, Is.Not.Null);
            configure.Invoke(controller, new object[] { "Test Image", "测试图片", 1, new Func<int, Texture2D>(_ => texture) });
            InvokePublic(controller, "Open", 0);

            var viewport = FindTransform("Test Image Viewport");
            var footer = FindTransform("Test Image Page Label");
            var close = FindTransform("Close Test Image Button");
            Assert.That(viewport, Is.Not.Null);
            Assert.That(footer, Is.Not.Null);
            Assert.That(close, Is.Not.Null);
            Assert.That(close.GetComponentInChildren<Text>().text, Is.EqualTo("×"));
            Assert.That(close.GetComponentInChildren<Text>().resizeTextForBestFit, Is.True);
            Assert.That(close.GetComponentInChildren<Text>().GetComponent<Outline>(), Is.Not.Null);
            Assert.That(footer.gameObject.activeSelf, Is.False);
            var scrollRect = viewport.GetComponent<ScrollRect>();
            Assert.That(scrollRect, Is.Not.Null);
            Assert.That(scrollRect.horizontal, Is.True);
            Assert.That(scrollRect.vertical, Is.True);
            Assert.That(
                viewport.rect.width / viewport.rect.height,
                Is.EqualTo((float)texture.width / texture.height).Within(0.001f));

            InvokePublic(controller, "SetZoom", 2f);
            var zoomProperty = type.GetProperty("Zoom", BindingFlags.Instance | BindingFlags.Public);
            Assert.That((float)zoomProperty.GetValue(controller, null), Is.EqualTo(2f).Within(0.001f));

            InvokePublic(controller, "ConfigureReferenceCollapse", "测试图片 · 参考中");
            InvokePublic(controller, "SetCollapsed", true);
            var collapsedProperty = type.GetProperty("IsCollapsed", BindingFlags.Instance | BindingFlags.Public);
            Assert.That((bool)collapsedProperty.GetValue(controller, null), Is.True);
            Assert.That(FindTransform("Test Image Expanded Content").gameObject.activeSelf, Is.False);
            Assert.That(FindTransform("Test Image Collapsed Summary").GetComponent<Text>().text, Does.Contain("参考中"));
            Assert.That(FindTransform("Test Image Viewer").GetComponent<Image>().raycastTarget, Is.False);
            Assert.That(FindTransform("Test Image Panel").GetComponent<RectTransform>().sizeDelta.y, Is.EqualTo(58f));

            InvokePublic(controller, "SetCollapsed", false);
            Assert.That((bool)collapsedProperty.GetValue(controller, null), Is.False);
            Assert.That(FindTransform("Test Image Expanded Content").gameObject.activeSelf, Is.True);

            InvokePublic(controller, "Close");
            var openProperty = type.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
            Assert.That((bool)openProperty.GetValue(controller, null), Is.False);
            Object.DestroyImmediate(texture);
        }

        private static Canvas CreateCanvas(string name)
        {
            var canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            return canvas;
        }

        private static GameObject InstantiateSettingsPrefab()
        {
            const string path = "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, "Missing editor-authored settings prefab.");
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Assert.That(instance, Is.Not.Null);
            return instance;
        }

        private static GameObject InstantiateGameplayHudPrefab()
        {
            const string path = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, "Missing editor-authored GameplayInteractionHud prefab.");
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Assert.That(instance, Is.Not.Null);
            return instance;
        }

        private static GameObject InstantiateExpandableInfoPanelPrefab()
        {
            const string path = "Assets/YC/Presentation/Prefabs/Gameplay/ExpandableInfoPanel.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Assert.That(instance, Is.Not.Null);
            return instance;
        }

        private static GameObject InstantiateBuildInfoPanelPrefab()
        {
            const string path = "Assets/YC/Presentation/Prefabs/Gameplay/BuildInfoPanel.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Assert.That(instance, Is.Not.Null);
            return instance;
        }

        private object BuildActionPanel(Canvas canvas)
        {
            var type = Type.GetType("YC.Presentation.ActionPanelController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.ActionPanelController.");

            owner = InstantiateGameplayHudPrefab();
            owner.name = "Action Panel Controller Test";
            var viewType = Type.GetType("YC.Presentation.ActionPanelView, Assembly-CSharp", false);
            var view = owner.GetComponentInChildren(viewType, true);
            var bind = type.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public);
            Assert.That(bind, Is.Not.Null, "Missing ActionPanelController.Bind.");
            const string catalogPath = "Assets/YC/Presentation/Content/CardVisualCatalog.asset";
            var catalogType = Type.GetType("YC.Presentation.CardVisualCatalog, Assembly-CSharp", true);
            var catalog = AssetDatabase.LoadAssetAtPath(catalogPath, catalogType);
            Assert.That(catalog, Is.Not.Null, catalogPath);

            Action noop = () => { };
            return bind.Invoke(
                null,
                new object[]
                {
                    view,
                    catalog,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop,
                    noop
                });
        }

        private static void EnsureAwakeRan(Component component, string readyFieldName)
        {
            var field = component.GetType().GetField(readyFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing " + component.GetType().Name + "." + readyFieldName + ".");
            var value = field.GetValue(component);
            if (value is bool boolValue ? boolValue : value != null)
            {
                return;
            }

            var awake = component.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null, "Missing " + component.GetType().Name + ".Awake.");
            awake.Invoke(component, null);
        }

        private static void InvokePublic(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing " + target.GetType().Name + "." + methodName + ".");
            method.Invoke(target, args);
        }

        private static T GetPublicProperty<T>(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, "Missing " + target.GetType().Name + "." + propertyName + ".");
            return (T)property.GetValue(target, null);
        }

        private static bool HasModuleTitle(System.Collections.IEnumerable modules, string title)
        {
            foreach (var module in modules)
            {
                var property = module.GetType().GetProperty("Title", BindingFlags.Instance | BindingFlags.Public);
                if (property != null && (string)property.GetValue(module, null) == title)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f));
        }

        private static bool AllTextRenderersAreMaskable(RectTransform root)
        {
            var texts = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null && !texts[i].maskable)
                {
                    return false;
                }
            }

            return true;
        }

        private static RectTransform FindTransform(string name)
        {
            var transforms = UnityEngine.Object.FindObjectsOfType<RectTransform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private static RectTransform FindTransform(GameObject root, string name)
        {
            var transforms = root.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private static void DestroyNamedObject(string objectName)
        {
            var go = GameObject.Find(objectName);
            if (go != null)
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
