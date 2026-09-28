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
        public void ActionPanel_FlipsBetweenMainAndHintCardsAndOpensHintPreview()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            var controller = BuildActionPanel(canvas);
            var characterPreviewOpened = 0;
            InvokePublic(
                controller,
                "ConfigureCharacterCardViewerAction",
                new Action(() => characterPreviewOpened += 1));

            var actionPanel = ViewRect("ActionPanelView", "PanelObject");
            var mainFace = ViewRect("ActionPanelView", "MainFaceObject");
            var cardFace = ViewRect("ActionPanelView", "CardFaceObject");
            var flipButton = ViewRect("ActionPanelView", "FlipButton");
            var influenceText = ViewRect("ActionPanelView", "RemainingInfluenceText");

            Assert.That(actionPanel, Is.Not.Null);
            Assert.That(mainFace, Is.Not.Null);
            Assert.That(cardFace, Is.Not.Null);
            Assert.That(flipButton, Is.Not.Null);
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
            var hintImage = ViewRect("ActionPanelView", "CardImage");
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

            var image = ViewRect("ActionPanelView", "CharacterCoverPreview");
            Assert.That(image.GetComponent<RawImage>().texture.name, Is.EqualTo("artwork/character_back_" + ((int)PlayerColor.Red) + ".jpg"));
            Assert.That(ViewRect("ActionPanelView", "CharacterPreviewButton").GetComponent<Button>().interactable, Is.True);
            ViewRect("ActionPanelView", "CharacterPreviewButton").GetComponent<Button>().onClick.Invoke();
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

            ViewRect("ActionPanelView", "CharacterStrategyButton").GetComponent<Button>().onClick.Invoke();

            Assert.That(invoked, Is.EqualTo(1));
            Assert.That(ViewRect("ActionPanelView", "CharacterCoverPreview").GetComponent<RawImage>().texture.name,
                Does.Contain("liskarm"));

            InvokePublic(controller, "ShowCharacterCard", view);
            Assert.That(ViewRect("ActionPanelView", "CharacterCoverPreview").GetComponent<RawImage>().texture.name,
                Does.Contain("liskarm"));
        }

        [Test]
        public void ActionPanel_EmptyCharacterCoverRegionShowsEmptyState()
        {
            var canvas = CreateCanvas("Action Panel Test Canvas");
            var controller = BuildActionPanel(canvas);

            InvokePublic(controller, "ShowCharacterCoverDropZone", string.Empty);

            Assert.That(ViewRect("ActionPanelView", "CharacterCoverPreview").gameObject.activeSelf, Is.False);
            Assert.That(ViewRect("ActionPanelView", "CharacterEmptyState").gameObject.activeSelf, Is.True);
        }

        [Test]
        public void ActionPanel_SecondEffectDecisionUsesRemainingButtonAndFinishDeclines()
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

            Assert.That(ViewRect("ActionPanelView", "CharacterStrategyButton").GetComponent<Button>().interactable, Is.False);
            Assert.That(ViewRect("ActionPanelView", "CharacterTacticButton").GetComponent<Button>().interactable, Is.True);
            Assert.That(ViewRect("ActionPanelView", "CharacterFinishButton").gameObject.activeSelf, Is.True);

            ViewRect("ActionPanelView", "CharacterFinishButton").GetComponent<Button>().onClick.Invoke();
            Assert.That(declined, Is.EqualTo(1));
            Assert.That(ViewRect("ActionPanelView", "MainFaceObject").gameObject.activeSelf, Is.True);
            Assert.That(ViewRect("ActionPanelView", "CardFaceObject").gameObject.activeSelf, Is.False);
        }

        [Test]
        public void PromptPresenter_StartsHidden()
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

            var promptPanel = ViewRect("GameplayPromptView", "PanelTransform");
            Assert.That(promptPanel, Is.Not.Null);

            var group = promptPanel.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null);
            Assert.That(group.alpha, Is.EqualTo(0f).Within(0.001f));
            Assert.That(group.blocksRaycasts, Is.False);
        }

        [Test]
        public void BuildInfoPanel_BindsAuthoredReferencesAndRefreshesCityBoard()
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

            Assert.That(GetPublicProperty<RectTransform>(view, "PanelTransform"), Is.Not.Null);
            Assert.That(GetPublicProperty<RectTransform>(view, "ContentRoot"), Is.Not.Null);
            Assert.That(GetPublicProperty<RectTransform>(view, "ExternalFacilityArea"), Is.Not.Null);
            Assert.That(GetPublicProperty<RectTransform>(view, "ExternalCityStyleArea"), Is.Not.Null);
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

            Assert.That(GetPublicProperty<RectTransform>(view, "CityBoardRoot"), Is.Not.Null);
            Assert.That(GetPublicProperty<RawImage>(view, "CityBoardImage"), Is.Not.Null);
        }

        [Test]
        public void RulebookViewer_HasCloseButton()
        {
            var type = Type.GetType("YC.Presentation.RulebookViewerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RulebookViewerController.");

            owner = ViewerPrefabTestUtility.Instantiate(ViewerPrefabTestUtility.RulebookPrefabPath);
            var controller = owner.GetComponent(type);
            InvokePublic(controller, "Open");

            var closeButton = ViewRect("ZoomableImageViewerView", "CloseButton");
            Assert.That(closeButton, Is.Not.Null);

            var label = closeButton.GetComponentInChildren<Text>(true);
            Assert.That(label, Is.Not.Null);
        }

        [Test]
        public void SettingsMenu_InGamePlacesLogNextToGearWithoutHintButton()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);

            owner = InstantiateSettingsPrefab();
            var controller = owner.GetComponent(type);
            EnsureAwakeRan(controller, "initialized");

            var gear = ViewRect("GameSettingsMenuView", "GearButton") as RectTransform;
            var configure = type.GetMethod("ConfigureActionLog", BindingFlags.Instance | BindingFlags.Public);
            configure.Invoke(controller, new object[] { new GameSession(new GameState()) });
            var log = ViewRect("GameSettingsMenuView", "ActionLogButton") as RectTransform;
            Assert.That(owner.transform.Find("Game Settings Canvas/Hint Card Button"), Is.Null);
            Assert.That(gear, Is.Not.Null);
            Assert.That(log, Is.Not.Null);
            Assert.That(log.gameObject.activeSelf, Is.True);
        }

        [Test]
        public void SettingsMenu_OnStartPageKeepsAuthoredActionLogButtonInactive()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            owner = InstantiateSettingsPrefab();
            var controller = owner.GetComponent(type);
            EnsureAwakeRan(controller, "initialized");

            InvokePublic(controller, "SetReturnToStartButtonVisible", false);

            var actionLog = ViewRect("GameSettingsMenuView", "ActionLogButton");
            Assert.That(actionLog, Is.Not.Null);
            Assert.That(actionLog.gameObject.activeSelf, Is.False);
            Assert.That(owner.transform.Find("Game Settings Canvas/Hint Card Button"), Is.Null);
            Assert.That(ViewRect("GameSettingsMenuView", "GearButton"), Is.Not.Null);
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

            var viewport = ViewRect("ZoomableImageViewerView", "ViewportTransform");
            var footer = ViewRect("ZoomableImageViewerView", "PageLabel");
            var close = ViewRect("ZoomableImageViewerView", "CloseButton");
            Assert.That(viewport, Is.Not.Null);
            Assert.That(footer, Is.Not.Null);
            Assert.That(close, Is.Not.Null);
            Assert.That(footer.gameObject.activeSelf, Is.False);
            var scrollRect = viewport.GetComponent<ScrollRect>();
            Assert.That(scrollRect, Is.Not.Null);
            Assert.That(scrollRect.horizontal, Is.True);
            Assert.That(scrollRect.vertical, Is.True);

            InvokePublic(controller, "SetZoom", 2f);
            var zoomProperty = type.GetProperty("Zoom", BindingFlags.Instance | BindingFlags.Public);
            Assert.That((float)zoomProperty.GetValue(controller, null), Is.EqualTo(2f).Within(0.001f));

            InvokePublic(controller, "ConfigureReferenceCollapse", "测试图片 · 参考中");
            InvokePublic(controller, "SetCollapsed", true);
            var collapsedProperty = type.GetProperty("IsCollapsed", BindingFlags.Instance | BindingFlags.Public);
            Assert.That((bool)collapsedProperty.GetValue(controller, null), Is.True);
            Assert.That(ViewRect("ZoomableImageViewerView", "ExpandedContentObject").gameObject.activeSelf, Is.False);
            Assert.That(ViewRect("ZoomableImageViewerView", "CollapsedSummaryText").GetComponent<Text>().text, Does.Contain("参考中"));
            Assert.That(ViewRect("ZoomableImageViewerView", "RootObject").GetComponent<Image>().raycastTarget, Is.False);

            InvokePublic(controller, "SetCollapsed", false);
            Assert.That((bool)collapsedProperty.GetValue(controller, null), Is.False);
            Assert.That(ViewRect("ZoomableImageViewerView", "ExpandedContentObject").gameObject.activeSelf, Is.True);

            InvokePublic(controller, "Close");
            var openProperty = type.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
            Assert.That((bool)openProperty.GetValue(controller, null), Is.False);
            Object.DestroyImmediate(texture);
        }

        private RectTransform ViewRect(string viewName, string propertyName)
        {
            var type = Type.GetType("YC.Presentation." + viewName + ", Assembly-CSharp", true);
            var view = owner.GetComponentInChildren(type, true);
            Assert.That(view, Is.Not.Null);
            var value = GetPublicProperty<UnityEngine.Object>(view, propertyName);
            return value is GameObject go ? go.GetComponent<RectTransform>() : ((Component)value).GetComponent<RectTransform>();
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
                    noop,
                    null
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
