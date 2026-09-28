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
        public void ActionPanel_MainActionsDoNotRequireRetiredContextFace()
        {
            var controller = BuildActionPanel(CreateCanvas("Action Panel Test Canvas"));
            Assert.That(FindTransform("Main Action Face"), Is.Not.Null);
            Assert.That(FindTransform("Action Panel Context Button"), Is.Null);
            Assert.That(FindTransform("Action Card Face"), Is.Null);
            InvokePublic(controller, "SetRemainingInfluence", 17);
            Assert.That(FindTransform("Remaining Influence Text").GetComponent<Text>().text,
                Does.Contain("17"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CharacterUse_SecondEffectOffersLegalChoiceOnce(bool finish)
        {
            var state = SecondEffectState();
            var dialog = CreateCharacterUseDialog();
            var calls = 0;
            InvokePublic(dialog, "ShowSecondEffectStep", new CharacterCardPanelPresenter().BuildView(state, 1),
                new Action(() => Assert.Fail("策略已完成，不得再次发动")),
                new Action(() => { if (!finish) calls++; }),
                new Action(() => { if (finish) calls++; }), new Func<bool>(() => true), null);
            var strategy = FindTransform("Character Effect Option 0").GetComponent<Button>();
            Assert.That(strategy.interactable, Is.False);
            var button = FindTransform("Character Effect Option " + (finish ? 2 : 1)).GetComponent<Button>();
            Assert.That(button.interactable, Is.True);
            var click = button.onClick;
            click.Invoke();
            click.Invoke();
            Assert.That(calls, Is.EqualTo(1), "连续点击不能重复提交角色效果决定。");
        }

        [Test]
        public void CharacterUse_StaleChoiceAndClosingDoNotSubmitDecision()
        {
            var dialog = CreateCharacterUseDialog();
            var calls = 0;
            Action callback = () => calls++;
            InvokePublic(dialog, "ShowSecondEffectStep", new CharacterCardPanelPresenter().BuildView(SecondEffectState(), 1),
                callback, callback, callback, new Func<bool>(() => false), callback);
            FindTransform("Character Effect Option 2").GetComponent<Button>().onClick.Invoke();
            InvokePublic(dialog, "Hide");
            Assert.That(calls, Is.Zero);
        }

        private object CreateCharacterUseDialog()
        {
            owner = InstantiateGameplayHudPrefab();
            var registryType = Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp", true);
            var registry = owner.GetComponentInChildren(registryType, true);
            var type = Type.GetType("YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp", true);
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { registry, owner.GetComponentInChildren<Canvas>(true).GetComponent<RectTransform>() }, null);
        }

        private static GameState SecondEffectState() => new GameState
        {
            Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1,
            Players = { new PlayerState { PlayerId = 1, Color = PlayerColor.Red,
                CoveredCharacterCardId = "character.red.p1.cannot" } },
            PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = YC.Domain.Cards.CharacterPendingChoiceTypes.SecondEffectDecision,
                PlayerId = 1, CardId = "character.red.p1.cannot",
                RemainingEffectMode = YC.Domain.Cards.CharacterEffectModes.Tactic,
                OptionIds = { YC.Domain.Cards.CharacterEffectChoiceIds.ContinueSecondEffect,
                    YC.Domain.Cards.CharacterEffectChoiceIds.FinishCharacterUse }
            }
        };



        [Test]
        public void BuildInfoPanel_BindsVisibleCityAndIndependentSupply()
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
            var boardImage = FindTransform(owner, "城市面板底图");
            Assert.That(boardImage, Is.Not.Null);
            Assert.That(boardImage.GetComponent<AspectRatioFitter>().aspectMode,
                Is.EqualTo(AspectRatioFitter.AspectMode.FitInParent));
        }

        [Test]
        public void RulebookViewer_HasWorkingCloseButton()
        {
            var type = Type.GetType("YC.Presentation.RulebookViewerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RulebookViewerController.");

            owner = ViewerPrefabTestUtility.Instantiate(ViewerPrefabTestUtility.RulebookPrefabPath);
            var controller = owner.GetComponent(type);
            InvokePublic(controller, "Open");

            var closeButton = FindTransform("Close Rulebook Button");
            Assert.That(closeButton, Is.Not.Null);

            var label = closeButton.GetComponentInChildren<Text>(true);
            Assert.That(label, Is.Not.Null);
            closeButton.GetComponent<Button>().onClick.Invoke();

        }

        [Test]
        public void SettingsMenu_InGameProvidesLogEntryWithoutHintButton()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);

            owner = InstantiateSettingsPrefab();
            var controller = owner.GetComponent(type);
            EnsureAwakeRan(controller, "initialized");

            var gear = FindTransform(owner, "Settings Gear Button");
            var configure = type.GetMethod("ConfigureActionLog", BindingFlags.Instance | BindingFlags.Public);
            configure.Invoke(controller, new object[] { new GameSession(new GameState()) });
            var log = FindTransform(owner, "Action Log Button");
            Assert.That(owner.transform.Find("Game Settings Canvas/Hint Card Button"), Is.Null);
            Assert.That(gear, Is.Not.Null);
            Assert.That(log, Is.Not.Null);
            Assert.That(log.gameObject.activeSelf, Is.True);
        }

        [Test]
        public void SettingsMenu_OnStartPageKeepsAuthoredActionLogButtonInactive()
        {
            var type = Type.GetType("YC.Presentation.GameSettingsMenuController, Assembly-CSharp", false);
            owner = InstantiateSettingsPrefab(ViewerPrefabTestUtility.SharedSettingsPrefabPath);
            var controller = owner.GetComponent(type);
            EnsureAwakeRan(controller, "initialized");

            InvokePublic(controller, "SetReturnToStartButtonVisible", false);

            var actionLog = FindTransform(owner, "Action Log Button");
            Assert.That(actionLog, Is.Not.Null);
            Assert.That(actionLog.gameObject.activeSelf, Is.False);
            Assert.That(owner.transform.Find("Game Settings Canvas/Hint Card Button"), Is.Null);
            Assert.That(FindTransform(owner, "Settings Gear Button"), Is.Not.Null);
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

        private static GameObject InstantiateSettingsPrefab(
            string path = ViewerPrefabTestUtility.InGameSettingsPrefabPath)
        {
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
