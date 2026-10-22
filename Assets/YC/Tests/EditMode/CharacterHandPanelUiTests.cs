using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class CharacterHandPanelUiTests
    {
        private const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/CharacterHandPanel.prefab";
        private const string CatalogPath =
            "Assets/YC/Presentation/Content/CardVisualCatalog.asset";

        private static readonly string[] CardIds =
        {
            "character.red.p1.liskarm",
            "character.red.p1.texas",
            "character.red.p1.cannot"
        };

        private GameObject owner;
        private PanelFacade panel;

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

            var viewers = Resources.FindObjectsOfTypeAll(GetRuntimeType(
                "YC.Presentation.CardViewer"));
            for (var i = 0; i < viewers.Length; i++)
            {
                var component = viewers[i] as Component;
                if (component != null && component.gameObject.scene.IsValid())
                {
                    Object.DestroyImmediate(component.gameObject);
                }
            }
        }

        [Test]
        public void Prefab_HasConfiguredPileAndSharedSlot()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var target = prefab.GetComponent(GetRuntimeType("YC.Presentation.CharacterHandPanel"));
            var args = new object[] { null };
            Assert.That((bool)target.GetType().GetMethod("TryValidateConfiguration").Invoke(target, args),
                Is.True, args[0] as string);
            var view = GetProperty<object>(target, "View");
            var pile = GetProperty<object>(view, "HandPile");
            Assert.That(GetProperty<bool>(pile, "IsConfigured"), Is.True);
            Assert.That(AssetDatabase.Contains(GetProperty<Object>(pile, "SlotPrefab")), Is.True);
        }

        [Test]
        public void DiscardList_ContainsOnlyVisibleDiscardsAndCannotSubmitSelection()
        {
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            owner = (GameObject)PrefabUtility.InstantiatePrefab(hud);
            panel = new PanelFacade(owner.GetComponentInChildren(GetRuntimeType("YC.Presentation.CharacterHandPanel"), true));
            Assert.That(panel.Configure(AssetDatabase.LoadAssetAtPath(CatalogPath,
                GetRuntimeType("YC.Presentation.CardVisualCatalog"))), Is.True);
            panel.Render(1, BuildModel(new[] { CardIds[0], CardIds[1] }, new[] { CardIds[2] }, false));
            panel.OpenDiscardPreview();
            Assert.That(panel.IsDiscardPreviewOpen, Is.True);
            var list = owner.GetComponentInChildren(GetRuntimeType("YC.Presentation.EffectDialogShellView"), false);
            Assert.That(list, Is.Not.Null);
            Assert.That(GetProperty<bool>(list, "IsCardPicker"), Is.True,
                "已有图像的只读弃牌列表使用卡牌选择页，但不能获得提交权限。");
            var cards = list.GetComponentsInChildren(GetRuntimeType("YC.Presentation.FacilityEffectCardView"), false);
            Assert.That(cards.Length, Is.EqualTo(1), "只读弃牌页不得混入手牌或其他玩家的牌。");
            Assert.That(GetProperty<Button>(cards[0], "Button").interactable, Is.False);
            Assert.That(Array.Exists(list.GetComponentsInChildren<Button>(), button => button.name == "Confirm Selection"), Is.False);
            Assert.That(Array.Exists(list.GetComponentsInChildren<Button>(), button => button.name == "Cancel Selection"), Is.False);
            Assert.That(GetProperty<Button>(cards[0], "Button").GetType().Name, Is.EqualTo("CardPickerCardButton"));
            var cardImage = GetProperty<RawImage>(cards[0], "CardImage");
            ClickHierarchy(cardImage.gameObject);
            Assert.That(panel.IsDiscardPreviewOpen, Is.True, "点击角色牌不得触发空白处关闭。");
            ClickHierarchy(GetProperty<Image>(list, "OverlayImage").gameObject);
            Assert.That(panel.IsDiscardPreviewOpen, Is.False);
            Assert.That(panel.OrderedHandCount, Is.EqualTo(2));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void DiscardList_BlankClickClosesPopulatedAndEmptyLists_WithoutClosingOnDragOrRightClick(bool empty,
            bool viewportBlank)
        {
            CreatePanel();
            panel.Render(1, BuildModel(new[] { CardIds[0] }, empty ? new string[0] : new[] { CardIds[1] }, false));
            panel.OpenDiscardPreview();
            var list = owner.GetComponentInChildren(GetRuntimeType("YC.Presentation.EffectDialogShellView"), false);
            Assert.That(list, Is.Not.Null);
            Assert.That(Array.Exists(list.GetComponentsInChildren<Text>(false),
                label => label.text == "点击空白处关闭"), Is.True, "弃牌页应显示用户指定的关闭提示。");
            var background = viewportBlank ? GetProperty<ScrollRect>(list, "OptionScroll").viewport.gameObject :
                GetProperty<Image>(list, "OverlayImage").gameObject;
            Assert.That(ExecuteEvents.GetEventHandler<IPointerClickHandler>(background), Is.SameAs(list.gameObject),
                "视口及外围空白点击都必须路由到弃牌页，而非被滚动组件截断。");
            ClickHierarchy(background, PointerEventData.InputButton.Right);
            Assert.That(panel.IsDiscardPreviewOpen, Is.True);
            ClickHierarchy(background, PointerEventData.InputButton.Left, true);
            Assert.That(panel.IsDiscardPreviewOpen, Is.True, "滚动拖动结束不得关闭弃牌页。");
            ClickHierarchy(background);
            Assert.That(panel.IsDiscardPreviewOpen, Is.False);
            Assert.That(panel.OrderedHandCount, Is.EqualTo(1));
        }

        private static void ClickHierarchy(GameObject target,
            PointerEventData.InputButton button = PointerEventData.InputButton.Left, bool dragging = false)
        {
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            Assert.That(handler, Is.Not.Null);
            // 与输入模块抬起时的派发一致；避免 EditMode 立即销毁窗口后 ExecuteHierarchy 再取其 Transform。
            ExecuteEvents.Execute(handler, new PointerEventData(EventSystem.current)
            {
                button = button, dragging = dragging,
                pointerPressRaycast = new RaycastResult { gameObject = target },
                pointerCurrentRaycast = new RaycastResult { gameObject = target }
            }, ExecuteEvents.pointerClickHandler);
        }

        [Test]
        public void EmptyHandCover_ProjectsDiscardWithoutChangingGameState()
        {
            var state = new GameState
            {
                Phase = GamePhase.CharacterCover,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                Players = { new PlayerState { PlayerId = 1, Color = PlayerColor.Red } }
            };
            state.FindPlayer(1).DiscardCardIds.Add(CardIds[0]);
            state.FindPlayer(1).DiscardCardIds.Add(CardIds[1]);
            var model = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(model.CanCover, Is.True);
            Assert.That(model.HandCards.Count, Is.EqualTo(2));
            Assert.That(model.DiscardCards.Count, Is.Zero);
            Assert.That(state.FindPlayer(1).HandCardIds, Is.Empty);
            CollectionAssert.AreEqual(
                new[] { CardIds[0], CardIds[1] },
                state.FindPlayer(1).DiscardCardIds);

            CreatePanel();
            panel.Render(1, model);
            Assert.That(panel.OrderedHandCount, Is.EqualTo(2));
        }

        private void CreatePanel()
        {
            owner = new GameObject(
                "Character Hand Panel Test Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            owner.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var registryType = GetRuntimeType("YC.Presentation.GameplayDialogRegistry");
            var hudSource = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            EditorUtility.CopySerialized(hudSource.GetComponentInChildren(registryType, true),
                owner.AddComponent(registryType));
            var scaler = owner.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, owner.transform);
            panel = new PanelFacade(instance.GetComponent(GetRuntimeType(
                "YC.Presentation.CharacterHandPanel")));
            var catalog = AssetDatabase.LoadAssetAtPath(
                CatalogPath,
                GetRuntimeType("YC.Presentation.CardVisualCatalog"));
            Assert.That(catalog, Is.Not.Null);
            Assert.That(panel.Configure(catalog), Is.True);
            Canvas.ForceUpdateCanvases();
        }

        private static CharacterCardPanelViewModel BuildModel(
            IEnumerable<string> handIds,
            IEnumerable<string> discardIds,
            bool canCover)
        {
            var hand = new List<CharacterCardHandItemViewModel>();
            foreach (var id in handIds)
                hand.Add(new CharacterCardHandItemViewModel(id, id, canCover));
            var discard = new List<CharacterCardHandItemViewModel>();
            foreach (var id in discardIds)
                discard.Add(new CharacterCardHandItemViewModel(id, id, false));
            return new CharacterCardPanelViewModel(
                hand.AsReadOnly(), discard.AsReadOnly(), string.Empty, string.Empty,
                canCover, false, false, false, false, false, string.Empty,
                0, 0, 0, 0,
                CharacterCardEffectKind.Unsupported,
                CharacterCardEffectKind.Unsupported,
                string.Empty, string.Empty, default(PlayerColor), string.Empty);
        }

        private static Type GetRuntimeType(string fullName)
        {
            var type = Type.GetType(fullName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, fullName);
            return type;
        }

        private static T GetProperty<T>(object target, string name)
        {
            var property = target.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public);
            Assert.That(property, Is.Not.Null, target.GetType().Name + "." + name);
            return (T)property.GetValue(target, null);
        }

        private sealed class PanelFacade
        {
            public void CloseDiscardPreview() => target.GetType().GetMethod("CloseDiscardPreview").Invoke(target, null);
            private readonly Component target;

            public PanelFacade(Component target)
            {
                this.target = target;
                Assert.That(target, Is.Not.Null);
            }

            public bool IsDiscardPreviewOpen => GetProperty<bool>(target, "IsDiscardPreviewOpen");
            public IReadOnlyList<CharacterCardHandItemViewModel> OrderedHand =>
                GetProperty<IReadOnlyList<CharacterCardHandItemViewModel>>(target, "OrderedHand");
            public int OrderedHandCount => OrderedHand.Count;

            public bool TryValidateConfiguration(out string reason)
            {
                var args = new object[] { null };
                var result = (bool)target.GetType().GetMethod("TryValidateConfiguration")
                    .Invoke(target, args);
                reason = args[0] as string ?? string.Empty;
                return result;
            }

            public bool Configure(
                Object catalog)
            {
                return (bool)target.GetType().GetMethod("Configure")
                    .Invoke(target, new object[] { catalog });
            }

            public void Render(int playerId, CharacterCardPanelViewModel model)
            {
                target.GetType().GetMethod("Render").Invoke(target, new object[] { playerId, model });
            }

            public void OpenDiscardPreview()
            {
                target.GetType().GetMethod("OpenDiscardPreview").Invoke(target, null);
            }

            public void CloseCharacterCardViewer()
            {
                target.GetType().GetMethod("CloseCharacterCardViewer").Invoke(target, null);
            }

            public bool TryHandleEscape()
            {
                return (bool)target.GetType().GetMethod("TryHandleEscape").Invoke(target, null);
            }
        }
    }
}
