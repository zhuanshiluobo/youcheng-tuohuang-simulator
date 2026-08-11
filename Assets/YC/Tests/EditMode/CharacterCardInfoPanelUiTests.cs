using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardInfoPanelUiTests
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

            var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
            for (var i = 0; i < canvases.Length; i++)
            {
                if (canvases[i] != null && canvases[i].name == "Info Panel Canvas")
                {
                    Object.DestroyImmediate(canvases[i].gameObject);
                }
            }
        }

        [Test]
        public void CoverPhase_HandCardUsesDragAndClickOnlyOpensViewer()
        {
            var state = CreateState(GamePhase.CharacterCover);
            var player = state.FindPlayer(1);
            player.HandCardIds.Add("character.red.p1.liskarm");
            player.CoveredCharacterCardId = "secret-covered-card-id";
            var presenter = new CharacterCardPanelPresenter();
            var selectedCardId = string.Empty;
            var panel = CreatePanel();

            // Clear the covered card so the hand is interactable, then verify the refreshed covered snapshot separately.
            player.CoveredCharacterCardId = string.Empty;
            SetCharacterCards(panel, presenter.BuildView(state, 1), false, id => selectedCardId = id, null);
            Assert.That(
                FindOwnedTransform("Character Card Image Viewer"),
                Is.Null,
                "构建手牌列表不应提前创建查看器。");
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("盖放提示：拖动到主要行动卡上即可盖放"));
            var handButton = FindTransform("Character Hand Card Image: 0").GetComponent<Button>();
            Assert.That(handButton.interactable, Is.True);
            var interactionType = Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", false);
            Assert.That(interactionType, Is.Not.Null);
            var interaction = handButton.GetComponent(interactionType);
            Assert.That(interaction, Is.Not.Null);
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                position = new Vector2(300f, 240f)
            };
            ((IPointerDownHandler)interaction).OnPointerDown(pointer);
            ((IBeginDragHandler)interaction).OnBeginDrag(pointer);
            var dragGhost = FindTransform("Character Card Drag Ghost");
            Assert.That(dragGhost, Is.Not.Null);
            Assert.That(dragGhost.GetComponent<RawImage>().color.a, Is.LessThan(1f));
            pointer.position = new Vector2(360f, 280f);
            ((IDragHandler)interaction).OnDrag(pointer);
            ((IEndDragHandler)interaction).OnEndDrag(pointer);
            Assert.That(FindTransform("Character Card Drag Ghost"), Is.Null);
            Assert.That(
                FindOwnedTransform("Character Card Image Viewer"),
                Is.Null,
                "拖动结束本身不应打开查看器。");
            ((IPointerClickHandler)interaction).OnPointerClick(pointer);
            Assert.That(FindOwnedTransform("Character Card Image Viewer"), Is.Null, "拖动结束时产生的点击事件应被抑制。");
            ClickCard(handButton.gameObject);
            Assert.That(FindOwnedTransform("Character Card Image Viewer"), Is.Not.Null, "单击手牌应打开查看器。");
            Assert.That(selectedCardId, Is.Empty, "单击手牌只能查看，盖放必须通过拖动和二次确认完成。");

            player.CoveredCharacterCardId = "secret-covered-card-id";
            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("盖放区"));
            Assert.That(FindTransform("Covered Character Card Image"), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("secret-covered-card-id"));
        }

        [Test]
        public void CoverPhase_FiveCardImagesStayInsideDedicatedHandStrip()
        {
            var state = CreateState(GamePhase.CharacterCover);
            CharacterCardDatabase.InitializePlayerHand(state.FindPlayer(1));
            var panel = CreatePanel();

            SetCharacterCards(panel, new CharacterCardPanelPresenter().BuildView(state, 1), false, null, null);

            var strip = FindTransform("Character Hand Card Strip").GetComponent<RectTransform>();
            for (var i = 0; i < 5; i++)
            {
                var card = FindTransform("Character Hand Card Image: " + i).GetComponent<RectTransform>();
                Assert.That(card.parent, Is.EqualTo(strip));
                Assert.That(card.anchoredPosition.x, Is.GreaterThanOrEqualTo(0f));
                Assert.That(card.anchoredPosition.x + card.rect.width, Is.LessThanOrEqualTo(strip.rect.width + 0.5f));
            }
        }

        [Test]
        public void HandFrontCanBeViewedWhileCoveredCardIsHiddenFromInfoPanel()
        {
            var state = CreateState(GamePhase.CharacterCover);
            var player = state.FindPlayer(1);
            player.HandCardIds.Add("character.red.p1.liskarm");
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();

            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);
            Assert.That(FindTransform("Character Hand Card Image: 0"), Is.Not.Null);

            player.HandCardIds.Clear();
            player.CoveredCharacterCardId = "character.red.p1.liskarm";
            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);

            Assert.That(FindTransform("Covered Character Card Image"), Is.Null);
            Assert.That(FindTransform("Covered Character Card Empty Slot"), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("雷蛇"));
            Assert.That(FindTransform("Character Hand Card Image: 0"), Is.Null);

            InvokePublic(panel, "OpenCoveredCharacterCardViewer");
            Assert.That(FindTransform("Character Card Image Title").GetComponent<Text>().text,
                Is.EqualTo("已盖放角色牌（雷蛇）"));
            Assert.That(FindTransform("Character Card Image Image").GetComponent<RawImage>().texture.name,
                Does.Contain("liskarm"));
        }

        [Test]
        public void DiscardArea_ShowsFaceUpCardsAsReadOnlyViewerAndRefreshesFromState()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.DiscardCardIds.Add("character.red.p1.liskarm");
            player.DiscardCardIds.Add("character.red.p1.texas");
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();

            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);

            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("弃牌区：2 张"));
            var discardCard = FindTransform("Character Discard Card Image: 0");
            Assert.That(discardCard, Is.Not.Null);
            Assert.That(discardCard.GetComponent<Button>().interactable, Is.True);
            ClickCard(discardCard.gameObject);
            Assert.That(FindTransform("Primary Character Card Image Action Button").gameObject.activeSelf, Is.False);
            Assert.That(FindTransform("Secondary Character Card Image Action Button").gameObject.activeSelf, Is.False);

            player.DiscardCardIds.Clear();
            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);
            Assert.That(FindTransform("Character Discard Card Image: 0"), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("弃牌区：0 张"));
        }

        [Test]
        public void Refresh_ReplacesOldHandAndShowsUsedStateFromLatestSnapshot()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.HandCardIds.Add("character-red-texas");
            player.CoveredCharacterCardId = "character.red.p1.cannot";
            var presenter = new CharacterCardPanelPresenter();
            var panel = CreatePanel();

            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(FindTransform("Character Hand Card Image: 0"), Is.Not.Null);
            Assert.That(FindTransform("Use Character Strategy"), Is.Null);

            player.HandCardIds.Clear();
            player.CoveredCharacterCardId = string.Empty;
            player.UsedCharacterThisRound = true;
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Character Hand Card Image: 0"), Is.Null);
            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("盖放区"));
            Assert.That(FindTransform("Covered Character Card Empty Slot"), Is.Null);
        }

        [Test]
        public void CoveredCardClick_OpensFrontWithoutEffectActions()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";
            var presenter = new CharacterCardPanelPresenter();
            var panel = CreatePanel();

            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);
            InvokePublic(panel, "OpenCoveredCharacterCardViewer");

            var viewerObject = GameObject.Find("Character Card Image Viewer");
            var viewerType = viewerObject.GetComponent(Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false));
            Assert.That((bool)viewerType.GetType().GetProperty("IsOpen").GetValue(viewerType, null), Is.True);
            Assert.That(FindTransform("Character Card Image Title").GetComponent<Text>().text,
                Is.EqualTo("已盖放角色牌（坎诺特）"));
            Assert.That(FindTransform("Character Card Image Image").GetComponent<RawImage>().texture.name, Does.Contain("cannot"));
            Assert.That(FindTransform("Primary Character Card Image Action Button").gameObject.activeSelf, Is.False);
            Assert.That(FindTransform("Secondary Character Card Image Action Button").gameObject.activeSelf, Is.False);
        }

        [Test]
        public void UseOptions_AreNotBuiltInsideInfoPanel()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";
            var presenter = new CharacterCardPanelPresenter();
            var panel = CreatePanel();
            SetCharacterCards(
                panel,
                presenter.BuildView(state, 1),
                true,
                null,
                null);
            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Null);
            Assert.That(FindTransform("Use Character Both Tactic First"), Is.Null);
            Assert.That(FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleOriginium), Is.Null);
            Assert.That(FindTransform("Character Requisition Selector: " + ResourceType.Originium), Is.Null);

            state.StartPlayerId = 2;
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Null);
            Assert.That(FindTransform("Use Character Both Strategy First"), Is.Null);
            Assert.That(FindTransform("Use Character Both Tactic First"), Is.Null);
        }

        [Test]
        public void CannotOptions_DoNotCreateSettlementControlsInInfoPanel()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.CoveredCharacterCardId = "character.red.p1.cannot";
            player.Resources.Originium = 2;
            player.Resources.OriginiumShard = 1;
            player.Resources.Iron = 3;
            player.Resources.PureOriginium = 1;
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleOriginium), Is.Null);
            Assert.That(FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleIron), Is.Null);
            Assert.That(FindTransform("Character Requisition Selector: " + ResourceType.Iron), Is.Null);
            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Null);
        }

        [Test]
        public void RefreshingCannotSnapshot_NeverRestoresLegacySaleDraftControls()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.CoveredCharacterCardId = "character.red.p1.cannot";
            player.Resources.Originium = 3;
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            var selectorName = "Character Sale Selector: " + CharacterEffectParameterKeys.SaleOriginium;
            Assert.That(FindTransform(selectorName), Is.Null);

            player.Resources.Originium = 1;
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(FindTransform(selectorName), Is.Null);

            player.CoveredCharacterCardId = "character.blue.p1.cannot";
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(FindTransform(selectorName), Is.Null);
        }

        [Test]
        public void UnsupportedCard_ShowsNoSettlementMessageOrEffectButtons()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character-red-unknown";
            var panel = CreatePanel();

            SetCharacterCards(panel, new CharacterCardPanelPresenter().BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("效果尚未接入"));
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("操作提示"));
        }

        [Test]
        public void PendingCharacterEffect_ShowsNoSettlementPromptInInfoPanel()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.liskarm";
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.LiskarmCleanupRemoval,
                PlayerId = 1,
                CardId = "character.red.p1.liskarm",
                OptionIds = { "slot-a" }
            };
            var panel = CreatePanel();

            SetCharacterCards(panel, new CharacterCardPanelPresenter().BuildView(state, 1), true, null, null);

            var text = GetModuleText(panel, "角色牌");
            Assert.That(text, Does.Not.Contain("待选结算"));
            Assert.That(text, Does.Not.Contain("结算弹窗"));
            Assert.That(text, Does.Not.Contain("地图高亮"));
            Assert.That(text, Does.Not.Contain("请先处理待选择项"));
        }

        [Test]
        public void ImplementedLiskarm_ShowsNoSettlementControlsOrPromptsInInfoPanel()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.liskarm";
            var panel = CreatePanel();

            SetCharacterCards(panel, new CharacterCardPanelPresenter().BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.PlacementSlotId1), Is.Null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.TargetInfluenceSlotId), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("地图上依次选择两个影响力空格"));
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("地图上选择要替换的对手影响力"));
            Assert.That(FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleOriginium), Is.Null);
            Assert.That(FindTransform("Character Requisition Selector: " + ResourceType.Originium), Is.Null);
        }

        [Test]
        public void TinManStrategy_DoesNotCreatePurchaseControlsInInfoPanel()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.tin-man";
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Null);
            Assert.That(FindTransform("Character Boolean Option: " + CharacterEffectParameterKeys.TinManPurchasePureOriginium12), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("已选购买"));
        }

        [Test]
        public void TexasAndElysium_DoNotBuildAnyEffectControlsInInfoPanel()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();

            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.texas";
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId), Is.Null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.MoveSourceSlotId1), Is.Null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.MoveSourceSlotId2), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("地图上依次选择移除目标与两次移动"));
            Assert.That(FindTransform("Character Option Selector: placementSlotId"), Is.Null);

            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.elysium";
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("支付 3 源石碎片"));
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("地图上选择突袭资源点"));
        }

        private object CreatePanel()
        {
            var type = Type.GetType("YC.Presentation.ExpandableInfoPanel, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.ExpandableInfoPanel.");
            var viewType = Type.GetType("YC.Presentation.ExpandableInfoPanelView, Assembly-CSharp", false);
            Assert.That(viewType, Is.Not.Null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/ExpandableInfoPanel.prefab");
            Assert.That(prefab, Is.Not.Null);
            owner = new GameObject(
                "Character Card Info Panel Test",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            owner.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, owner.transform);
            var panel = instance.GetComponent(type);
            var panelView = instance.GetComponent(viewType);
            Assert.That(panel, Is.Not.Null);
            Assert.That(panelView, Is.Not.Null);
            const string catalogPath = "Assets/YC/Presentation/Content/CardVisualCatalog.asset";
            var catalogType = Type.GetType("YC.Presentation.CardVisualCatalog, Assembly-CSharp", true);
            var catalog = AssetDatabase.LoadAssetAtPath(catalogPath, catalogType);
            Assert.That(catalog, Is.Not.Null, catalogPath);
            type.GetMethod("ConfigureCardVisualCatalog", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(panel, new[] { catalog });
            Assert.That(
                (bool)type.GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(panel, new[] { panelView }),
                Is.True);
            return panel;
        }

        private static void SetCharacterCards(
            object panel,
            CharacterCardPanelViewModel view,
            bool showUseOptions,
            Action<string> cover,
            Action<string, string, IReadOnlyDictionary<string, string>> use)
        {
            panel.GetType().GetMethod("SetCharacterCards", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(panel, new object[] { view, showUseOptions, cover, use });
        }

        private static void InvokePublic(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing " + target.GetType().Name + "." + methodName + ".");
            method.Invoke(target, null);
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players = { new PlayerState { PlayerId = 1, Color = PlayerColor.Red } }
            };
        }

        private static Transform FindTransform(string name)
        {
            var transforms = Resources.FindObjectsOfTypeAll<Transform>();
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private Transform FindOwnedTransform(string name)
        {
            if (owner == null)
            {
                return null;
            }

            var transforms = owner.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private static void ClickCard(GameObject cardObject)
        {
            var interactionType = Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", false);
            Assert.That(interactionType, Is.Not.Null);
            var interaction = cardObject.GetComponent(interactionType);
            Assert.That(interaction, Is.Not.Null);
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                position = RectTransformUtility.WorldToScreenPoint(null, cardObject.transform.position)
            };
            ((IPointerDownHandler)interaction).OnPointerDown(pointer);
            ((IPointerClickHandler)interaction).OnPointerClick(pointer);
        }

        private static string GetModuleText(object panel, string moduleTitle)
        {
            var modules = (IEnumerable)panel.GetType()
                .GetProperty("Modules", BindingFlags.Instance | BindingFlags.Public)
                .GetValue(panel, null);
            foreach (var module in modules)
            {
                var moduleType = module.GetType();
                var title = (string)moduleType.GetProperty("Title", BindingFlags.Instance | BindingFlags.Public)
                    .GetValue(module, null);
                if (title != moduleTitle)
                {
                    continue;
                }

                var content = (RectTransform)moduleType.GetProperty("ContentRect", BindingFlags.Instance | BindingFlags.Public)
                    .GetValue(module, null);
                var texts = content.GetComponentsInChildren<Text>(true);
                var result = string.Empty;
                for (var j = 0; j < texts.Length; j++)
                {
                    result += texts[j].text + "\n";
                }
                return result;
            }

            return string.Empty;
        }
    }
}
