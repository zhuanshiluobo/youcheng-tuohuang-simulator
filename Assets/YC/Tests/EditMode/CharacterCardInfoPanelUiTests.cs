using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
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
        public void CoverPhase_HandButtonSubmitsSelectedCardWithoutExposingCoveredId()
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
            var handButton = FindTransform("Character Hand Card Image: 0").GetComponent<Button>();
            Assert.That(handButton.interactable, Is.True);
            handButton.onClick.Invoke();
            FindTransform("Primary Character Card Image Action Button").GetComponent<Button>().onClick.Invoke();
            Assert.That(selectedCardId, Is.EqualTo("character.red.p1.liskarm"));

            player.CoveredCharacterCardId = "secret-covered-card-id";
            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("已盖放（背面）"));
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
        public void HandFrontCanBeViewedWhileCoveredCardShowsOnlyColorBack()
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

            var backPreview = FindTransform("Covered Character Card Image");
            Assert.That(backPreview, Is.Not.Null);
            Assert.That(backPreview.GetComponent<RawImage>().texture.name, Is.EqualTo("back-red"));
            Assert.That(GetModuleText(panel, "角色牌"), Does.Not.Contain("雷蛇"));
            Assert.That(FindTransform("Character Hand Card Image: 0"), Is.Null);
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
            discardCard.GetComponent<Button>().onClick.Invoke();
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
            Assert.That(FindTransform("Use Character Strategy"), Is.Not.Null);

            player.HandCardIds.Clear();
            player.CoveredCharacterCardId = string.Empty;
            player.UsedCharacterThisRound = true;
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Character Hand Card Image: 0"), Is.Null);
            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("本回合角色牌已使用"));
        }

        [Test]
        public void FlipEffect_KeepsViewerOpenOnFrontAndSupportsReferenceCollapse()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";
            var presenter = new CharacterCardPanelPresenter();
            var panel = CreatePanel();

            SetCharacterCards(panel, presenter.BuildView(state, 1), false, null, null);
            FindTransform("Covered Character Card Image").GetComponent<Button>().onClick.Invoke();
            FindTransform("Primary Character Card Image Action Button").GetComponent<Button>().onClick.Invoke();

            var viewerObject = GameObject.Find("Character Card Image Viewer");
            var viewerType = viewerObject.GetComponent(Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false));
            Assert.That((bool)viewerType.GetType().GetProperty("IsOpen").GetValue(viewerType, null), Is.True);
            Assert.That(FindTransform("Character Card Image Title").GetComponent<Text>().text, Is.EqualTo("坎诺特 · 策略"));
            Assert.That(FindTransform("Character Card Image Image").GetComponent<RawImage>().texture.name, Does.Contain("cannot"));
            Assert.That(FindTransform("Use Character Strategy"), Is.Not.Null);
            Assert.That(FindTransform("Character Card Image Collapse Toggle Button").gameObject.activeSelf, Is.True);

            FindTransform("Character Card Image Collapse Toggle Button").GetComponent<Button>().onClick.Invoke();
            Assert.That((bool)viewerType.GetType().GetProperty("IsCollapsed").GetValue(viewerType, null), Is.True);
            Assert.That(FindTransform("Character Card Image Expanded Content").gameObject.activeSelf, Is.False);
            Assert.That(FindTransform("Use Character Strategy"), Is.Not.Null);
        }

        [Test]
        public void UseOptions_NeverShowsLegacyBothOrderButtons()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";
            var presenter = new CharacterCardPanelPresenter();
            var panel = CreatePanel();
            var selectedMode = string.Empty;
            var selectedOrder = string.Empty;

            SetCharacterCards(
                panel,
                presenter.BuildView(state, 1),
                true,
                null,
                (mode, order, parameters) =>
                {
                    selectedMode = mode;
                    selectedOrder = order;
                });
            Assert.That(FindTransform("Use Character Strategy"), Is.Not.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Not.Null);
            Assert.That(FindTransform("Use Character Both Tactic First"), Is.Null);

            state.StartPlayerId = 2;
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(FindTransform("Use Character Strategy"), Is.Not.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Not.Null);
            Assert.That(FindTransform("Use Character Both Strategy First"), Is.Null);
            Assert.That(FindTransform("Use Character Both Tactic First"), Is.Null);
        }

        [Test]
        public void CannotOptions_SubmitSelectedSaleCountsAndRequisitionResource()
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
            IReadOnlyDictionary<string, string> submitted = null;
            string submittedMode = null;
            string submittedOrder = null;

            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null,
                (mode, order, parameters) =>
                {
                    submittedMode = mode;
                    submittedOrder = order;
                    submitted = parameters;
                });

            FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleOriginium)
                .GetComponent<Button>().onClick.Invoke();
            FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleIron)
                .GetComponent<Button>().onClick.Invoke();
            FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleIron)
                .GetComponent<Button>().onClick.Invoke();
            FindTransform("Character Requisition Selector: " + ResourceType.Iron)
                .GetComponent<Button>().onClick.Invoke();

            FindTransform("Use Character Strategy").GetComponent<Button>().onClick.Invoke();
            Assert.That(submittedMode, Is.EqualTo(UseCharacterCardCommandHandler.Strategy));
            Assert.That(submittedOrder, Is.Empty);
            Assert.That(submitted[CharacterEffectParameterKeys.SaleOriginium], Is.EqualTo("1"));
            Assert.That(submitted[CharacterEffectParameterKeys.SaleIron], Is.EqualTo("2"));
            Assert.That(submitted.ContainsKey(CharacterEffectParameterKeys.ResourceType), Is.False);

            FindTransform("Use Character Tactic").GetComponent<Button>().onClick.Invoke();
            Assert.That(submittedMode, Is.EqualTo(UseCharacterCardCommandHandler.Tactic));
            Assert.That(submitted[CharacterEffectParameterKeys.ResourceType], Is.EqualTo("iron"));
            Assert.That(submitted.ContainsKey(CharacterEffectParameterKeys.SaleOriginium), Is.False);

            Assert.That(FindTransform("Use Character Both Strategy First"), Is.Null);
        }

        [Test]
        public void CannotSaleDraft_ClipsToLatestSnapshotAndResetsWhenCoveredCardChanges()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.CoveredCharacterCardId = "character.red.p1.cannot";
            player.Resources.Originium = 3;
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();
            IReadOnlyDictionary<string, string> submitted = null;

            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null,
                (mode, order, parameters) => submitted = parameters);
            var selectorName = "Character Sale Selector: " + CharacterEffectParameterKeys.SaleOriginium;
            FindTransform(selectorName).GetComponent<Button>().onClick.Invoke();
            FindTransform(selectorName).GetComponent<Button>().onClick.Invoke();

            player.Resources.Originium = 1;
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null,
                (mode, order, parameters) => submitted = parameters);
            FindTransform("Use Character Strategy").GetComponent<Button>().onClick.Invoke();
            Assert.That(submitted[CharacterEffectParameterKeys.SaleOriginium], Is.EqualTo("1"));

            player.CoveredCharacterCardId = "character.blue.p1.cannot";
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null,
                (mode, order, parameters) => submitted = parameters);
            FindTransform("Use Character Strategy").GetComponent<Button>().onClick.Invoke();
            Assert.That(submitted[CharacterEffectParameterKeys.SaleOriginium], Is.EqualTo("0"));
        }

        [Test]
        public void UnsupportedCard_ShowsUnavailableMessageAndNoEffectButtons()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character-red-liskarm";
            var panel = CreatePanel();

            SetCharacterCards(panel, new CharacterCardPanelPresenter().BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Use Character Strategy"), Is.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("效果尚未接入"));
        }

        [Test]
        public void ImplementedLiskarm_UsesSlotControlsInsteadOfCannotControls()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.liskarm";
            var panel = CreatePanel();

            SetCharacterCards(panel, new CharacterCardPanelPresenter().BuildView(state, 1), true, null, null);

            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.PlacementSlotId1), Is.Not.Null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.TargetInfluenceSlotId), Is.Not.Null);
            Assert.That(FindTransform("Character Sale Selector: " + CharacterEffectParameterKeys.SaleOriginium), Is.Null);
            Assert.That(FindTransform("Character Requisition Selector: " + ResourceType.Originium), Is.Null);
        }

        [Test]
        public void TinManStrategy_SubmitsExplicitPurchasesAndShowsDomainSummary()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.tin-man";
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();
            IReadOnlyDictionary<string, string> submitted = null;
            ConfigureCharacterInteraction(panel, presenter, state);

            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null,
                (mode, order, parameters) => submitted = parameters);

            Assert.That(FindTransform("Use Character Strategy"), Is.Not.Null);
            Assert.That(FindTransform("Use Character Tactic"), Is.Not.Null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("已选购买 0 个至纯源石，需支付 0 金券"));

            FindTransform("Character Boolean Option: " + CharacterEffectParameterKeys.TinManPurchasePureOriginium12)
                .GetComponent<Button>().onClick.Invoke();
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("已选购买 1 个至纯源石，需支付 12 金券"));
            FindTransform("Use Character Strategy").GetComponent<Button>().onClick.Invoke();

            Assert.That(submitted[CharacterEffectParameterKeys.TinManPurchasePureOriginium12], Is.EqualTo("true"));
            Assert.That(submitted[CharacterEffectParameterKeys.TinManPurchasePureOriginium15], Is.EqualTo("false"));
        }

        [Test]
        public void TexasAndElysiumControlsUseConfirmedRules()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var panel = CreatePanel();
            var presenter = new CharacterCardPanelPresenter();

            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.texas";
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId), Is.Not.Null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.MoveSourceSlotId1), Is.Not.Null);
            Assert.That(FindTransform("Character Option Selector: " + CharacterEffectParameterKeys.MoveSourceSlotId2), Is.Not.Null);
            Assert.That(FindTransform("Character Option Selector: placementSlotId"), Is.Null);

            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.elysium";
            SetCharacterCards(panel, presenter.BuildView(state, 1), true, null, null);
            Assert.That(GetModuleText(panel, "角色牌"), Does.Contain("支付 3 源石碎片"));
        }

        private static void ConfigureCharacterInteraction(object panel, CharacterCardPanelPresenter presenter, GameState state)
        {
            var queryOptions = new Func<CharacterCardEffectKind, IReadOnlyDictionary<string, string>, CharacterCardOptionQueryResult>(
                (effect, selected) => presenter.QueryOptions(state, 1, effect, selected));
            var queryPending = new Func<IReadOnlyDictionary<string, string>, CharacterCardOptionQueryResult>(
                selected => presenter.QueryPendingOptions(state, 1, selected));
            panel.GetType().GetMethod("ConfigureCharacterEffectInteraction", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(panel, new object[] { queryOptions, queryPending, null });
        }

        private object CreatePanel()
        {
            var type = Type.GetType("YC.Presentation.ExpandableInfoPanel, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.ExpandableInfoPanel.");
            owner = new GameObject("Character Card Info Panel Test");
            var panel = owner.AddComponent(type);
            type.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(panel, new object[] { owner.transform });
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
