using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    /// <summary>
    /// 覆盖真实盖牌投影到快速行动动态名称的边界。
    /// 本夹具不替代从房间开始的实际场景、四模式激活及使用后消费验收。
    /// </summary>
    public sealed class CharacterQuickActionPresentationTests
    {
        private ActiveContentCatalog catalog;
        private EffectRegistry actionRegistry;
        private GameObject hud;
        private object actionController;
        private Text quickLabel;

        [OneTimeSetUp]
        public void LoadFormalEnterpriseCatalog()
        {
            var installed = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.dataPath,
                "StreamingAssets/Content/core"));
            if (!CharacterCardDatabase.IsInitialized)
                CharacterCardDatabase.Initialize(installed.CreateCharacters());
            catalog = ActiveContentCatalogBuilder.Build(installed, Configuration());
            actionRegistry = new EffectRegistry();
            catalog.Register(actionRegistry);
        }

        [SetUp]
        public void BindExistingHudWithoutRebuildingAssets()
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(source, Is.Not.Null);
            hud = UnityEngine.Object.Instantiate(source);

            var hudType = Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true);
            var controllerType = Type.GetType("YC.Presentation.ActionPanelController, Assembly-CSharp", true);
            var rowType = Type.GetType("YC.Presentation.UiQuickActionRow, Assembly-CSharp", true);
            var hudView = hud.GetComponentInChildren(hudType, true);
            Assert.That(hudView, Is.Not.Null);
            var panel = Property(hudView, "ActionPanelView");
            var registry = Property(hudView, "DialogRegistry");
            var arguments = new object[11];
            arguments[0] = panel;
            arguments[1] = Property(registry, "CardVisualCatalog");
            actionController = controllerType.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, arguments);
            Assert.That(actionController, Is.Not.Null);
            var button = (Button)Property(panel, "UseCharacterButton");
            var row = button.GetComponent(rowType);
            Assert.That(row, Is.Not.Null);
            quickLabel = (Text)Field(row, "label");
        }

        [TearDown]
        public void DestroyRuntimeHudCopy()
        {
            if (hud != null) UnityEngine.Object.DestroyImmediate(hud);
            hud = null;
            actionController = null;
            quickLabel = null;
        }

        [TestCase("mlynar", false)]
        [TestCase("mlynar", true)]
        [TestCase("mountain", false)]
        [TestCase("mountain", true)]
        public void EnterpriseCoveredNameFlowsFromFrozenProjectionToQuickAction(string definitionId, bool client)
        {
            var state = CoverActualCard(definitionId);
            var visible = ActionProjection(state);
            var projectedState = state;
            if (client)
            {
                var session = new GameSession(GameStateCloneService.DeepClone(state));
                visible = ActionProjection(session.State);
                projectedState = SimulationUiTestShell.ReadState(session.State, visible, 1);
                Assert.That(session.State.EffectRuntime, Is.Not.Null);
                Assert.That(projectedState.EffectRuntime.EffectNodes, Is.Empty, "UI 读取壳不包含完整效果执行树。");
                Assert.That(projectedState.EffectRuntime.InteractionRequests.All(request => request.AnsweringPlayerId == 1), Is.True);
            }
            var model = new CharacterCardPanelPresenter().BuildProjectedView(projectedState, 1, visible);
            var covered = model.CoveredCards.Single(card => card.CardId == model.CoveredCardId);
            string expectedName = state.ContentDefinitions.Characters.Single(card => card.TemplateId == definitionId).Name;
            Assert.That(covered.DisplayName, Is.EqualTo(expectedName));
            Assert.That(model.ResolveDefinitionId(model.CoveredCardId), Is.EqualTo(definitionId));
            Assert.That(model.CanUseStrategy && model.CanUseTactic, Is.True);
            Assert.That(model.StrategyEffect, Is.EqualTo(CharacterCardEffectKind.External));
            Assert.That(model.TacticEffect, Is.EqualTo(CharacterCardEffectKind.External));
            Render(model);
            Assert.That(quickLabel.text, Does.Contain(expectedName));
        }

        [TestCase("mlynar")]
        [TestCase("mountain")]
        [TestCase("liskarm")]
        public void QuickActionUsesProjectedDisplayNameWithoutGuessingCardOrTemplateIds(string definitionId)
        {
            var state = CoverActualCard(definitionId);
            var model = new CharacterCardPanelPresenter().BuildProjectedView(state, 1, ActionProjection(state));
            var option = CharacterAction(model);
            option.DisplayName = "本局动态角色名称";
            Render(model);
            Assert.That(quickLabel.text, Does.StartWith(option.DisplayName));
        }

        [TestCase(1, false), TestCase(1, true), TestCase(2, false), TestCase(2, true)]
        public void LiskarmQuickActionCreatesSameModeChoiceInEitherActionRound(int actionRound, bool client)
        {
            // 仅隔离验证规则和投影，非从正常房间进入的联机画面验收。
            var state = BeginLiskarmChoice(actionRound);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" &&
                value.InteractionTypeId == CharacterLifecycleService.ModeInteractionTypeId);
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(request.CandidateIds, Does.Contain("strategy").And.Contain("tactic"));
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            var projectedState = state;
            if (client)
            {
                var session = new GameSession(GameStateCloneService.DeepClone(state));
                visible = session.RefreshLocalProjection(GameStateViewer.Player(1));
                projectedState = SimulationUiTestShell.ReadState(session.State, visible, 1);
            }
            var model = new CharacterCardPanelPresenter().BuildProjectedView(projectedState, 1, visible);
            Assert.That(model.CurrentUseSelection?.InteractionId, Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(model.CanUse && model.CanUseStrategy && model.CanUseTactic, Is.True);
            Assert.That(model.ResolveDefinitionId(model.CoveredCardId), Is.EqualTo("liskarm"));
            var hudView = hud.GetComponentInChildren(Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true), true);
            var visualCatalog = Property(Property(hudView, "DialogRegistry"), "CardVisualCatalog");
            Assert.That(visualCatalog.GetType().GetMethod("GetCharacterFront").Invoke(visualCatalog, new object[] { "liskarm" }), Is.Not.Null);
        }

        [TestCase(1), TestCase(2)]
        public void ActualCharacterPageWaitsForItsOwnUseReceiptAndPreservesFold(int actionRound)
        {
            // 直接组装真实控制器与预制体的隔离接缝，不替代正常菜单或联机对局验收。
            var state = BeginLiskarmChoice(actionRound);
            var session = new GameSession(state);
            var visible = session.RefreshLocalProjection(GameStateViewer.Player(1));
            var presenter = new CharacterCardPanelPresenter();
            var model = presenter.BuildProjectedView(state, 1, visible);
            var root = new GameObject("角色原卡面回执控制器夹具");
            root.SetActive(false);
            try
            {
                var type = Type.GetType("YC.Presentation.MobileCityInteractionController, Assembly-CSharp", true);
                var mobile = root.AddComponent(type);
                const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                void Set(string name, object value) => type.GetField(name, members).SetValue(mobile, value);
                object Call(string name, params object[] args) => type.GetMethod(name, members).Invoke(mobile, args);
                var hudView = hud.GetComponentInChildren(Type.GetType("YC.Presentation.GameplayInteractionHudView, Assembly-CSharp", true), true);
                Set("gameplayInteractionHud", hudView); Set("session", session); Set("localPlayerId", 1);
                Set("characterCardPresenter", presenter); Set("characterUseCommandId", "pending-use");
                var expansion = Property(hudView, "ExpansionUi");
                expansion.GetType().GetField("hud", members).SetValue(expansion, hudView);
                expansion.GetType().GetField("getView", members).SetValue(expansion, (Func<GameStateView>)(() => visible));
                expansion.GetType().GetField("getPlayer", members).SetValue(expansion, (Func<int>)(() => 1));
                var frame = Property(hudView, "Frame");
                var frameType = frame.GetType();
                if (!ReferenceEquals(frameType.GetProperty("Active").GetValue(null), frame))
                    frameType.GetMethod("Awake", members).Invoke(frame, null);
                frameType.GetMethod("ConfigureGameplayPresentation").Invoke(frame,
                    new object[] { catalog.TextCatalog, new Dictionary<string, string>() });
                frameType.GetMethod("SetRequest").Invoke(frame, new object[] { model.CurrentUseSelection.InteractionId, model.CurrentUseSelection.StateRevision });
                Assert.That(Call("ShowCharacterUsePage", model), Is.EqualTo(false));
                Assert.That(type.GetField("characterViewer", members).GetValue(mobile), Is.Null);
                Call("NotifyCharacterUseSettled", "another-command");
                Assert.That(Call("ShowCharacterUsePage", model), Is.EqualTo(false), "其他命令回执不能解除等待");
                Call("NotifyCharacterUseSettled", "pending-use");
                Assert.That(Call("ShowCharacterUsePage", model), Is.EqualTo(true));
                var viewer = (Component)type.GetField("characterViewer", members).GetValue(mobile);
                Assert.That(viewer, Is.Not.Null);
                Assert.That(viewer.GetType().GetProperty("CardId").GetValue(viewer), Is.EqualTo(model.CoveredCardId));
                Assert.That(viewer.gameObject.activeSelf, Is.True);
                ((Button)frameType.GetProperty("FoldButton").GetValue(frame)).onClick.Invoke();
                Assert.That(viewer.gameObject.activeSelf, Is.False);
                Assert.That(Call("ShowCharacterUsePage", model), Is.EqualTo(true));
                Assert.That(viewer.gameObject.activeSelf, Is.False, "刷新不能擅自展开用户收起的页面");
                ((Button)frameType.GetProperty("FoldButton").GetValue(frame)).onClick.Invoke();
                Assert.That(viewer.gameObject.activeSelf, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private GameState BeginLiskarmChoice(int actionRound) => BeginCharacterChoice("liskarm", actionRound);

        private GameState BeginCharacterChoice(string definitionId, int actionRound)
        {
            var state = CoverActualCard(definitionId);
            state.ActionRound = actionRound;
            state.Phase = actionRound == 1 ? GamePhase.ActionRound1 : GamePhase.ActionRound2;
            var card = state.CharacterCoverSlots.Single(slot => slot.PlayerId == 1).CardInstanceId;
            Assert.That(CharacterLifecycleService.GetAvailability(state, 1, card).CanUse, Is.True);
            var registry = new EffectRegistry();
            catalog.Register(registry);
            CharacterLifecycleService.Register(registry);
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(CharacterLifecycleService.CreateQuickActionSpec(1, card));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            return state;
        }

        [TestCase("mlynar")]
        [TestCase("mountain")]
        public void LockedCoveredCardKeepsItsProjectedNameWithoutInventingAnotherQuickAction(string definitionId)
        {
            var state = CoverActualCard(definitionId);
            state.FindPlayer(1).CharacterCardLockedThisTurn = true;
            var model = new CharacterCardPanelPresenter().BuildProjectedView(state, 1, ActionProjection(state));
            Assert.That(model.CanUse, Is.False);
            Render(model);
            string expectedName = model.CoveredCards.Single(card => card.CardId == model.CoveredCardId).DisplayName;
            Assert.That(quickLabel.text, Does.Contain(expectedName));
            Assert.That(CharacterAction(model).Enabled, Is.False);
            Assert.That(((Component)CharacterRows()[CharacterAction(model).OptionId]).GetComponent<Button>().interactable, Is.False);
        }

        [Test]
        public void MissingCoveredCardLeavesNoDynamicCharacterAction()
        {
            Render(CharacterCardPanelViewModel.Empty(string.Empty));
            Assert.That(CharacterRows().Count, Is.Zero);
            Assert.That(quickLabel, Is.Null);
        }

        [TestCase("mlynar")]
        [TestCase("mountain")]
        public void PendingCharacterSelectionKeepsItsCoveredCardIdentity(string definitionId)
        {
            var state = BeginCharacterChoice(definitionId, 1);
            var model = new CharacterCardPanelPresenter().BuildProjectedView(state, 1, ActionProjection(state));
            var use = state.CharacterUses.Single(value => !value.Finished && !value.SelectionCancelled);
            Assert.That(model.CurrentUseSelection, Is.Not.Null);
            Assert.That(model.CoveredCardId, Is.EqualTo(use.CardInstanceId));
            Assert.That(model.ResolveDefinitionId(model.CoveredCardId), Is.EqualTo(definitionId));
            Assert.That(model.CoveredCards.Single(card => card.CardId == model.CoveredCardId).DisplayName,
                Is.EqualTo(state.ContentDefinitions.Characters.Single(card => card.TemplateId == definitionId).Name));
        }

        private GameState CoverActualCard(string definitionId)
        {
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                new List<PlayerSeat> {
                    new PlayerSeat { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerSeat { PlayerId = 2, Color = PlayerColor.Blue },
                    new PlayerSeat { PlayerId = 3, Color = PlayerColor.Green }
                }, Configuration(), 71, catalog.SetupRecipe, catalog.ContentDefinitions);
            // 展示层定向夹具的阶段起点；正式开局、保留与四式消费另由实际入口验收。
            state.Round = 1;
            state.ActionRound = 1;
            state.Phase = GamePhase.CharacterCover;
            state.CurrentPlayerId = 1;
            state.StartPlayerId = 1;
            var registry = new EffectRegistry();
            catalog.Register(registry);
            CharacterCoverEffectExecutor.Register(registry);
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" &&
                value.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId);
            var target = state.ItemZones.Items.Single(item => item.DefinitionId == definitionId &&
                item.ControllerSubject.PlayerId == 1);
            Assert.That(request.CandidateIds, Does.Contain(target.ItemId));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(target.ItemId), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(state.CharacterCoverSlots.Any(slot => slot.PlayerId == 1 && slot.CardInstanceId == target.ItemId), Is.True);
            state.Phase = GamePhase.ActionRound1;
            return state;
        }

        private IDictionary CharacterRows() => (IDictionary)actionController.GetType()
            .GetField("characterRows", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(actionController);

        private GameStateView ActionProjection(GameState state) => GameStateViewProjector.Project(state,
            GameStateViewer.Player(1), null, actionRegistry.QueryActions(state, 1), actionRegistry);

        private static AbilityActionOption CharacterAction(CharacterCardPanelViewModel model) =>
            model.DeclaredQuickActions.Single(action => action.SourceRef.ItemId == model.CoveredCardId &&
                action.ActionId == model.ResolveDefinitionId(model.CoveredCardId) + ".use");

        private void Render(CharacterCardPanelViewModel model)
        {
            actionController.GetType().GetMethod("RenderCharacterQuickAction", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(actionController, new object[] { model });
            var rows = CharacterRows();
            Assert.That(rows.Count, Is.EqualTo(model.HasAbilityActionCatalog
                ? model.DeclaredQuickActions.Select(option => option.OptionId).Distinct().Count() : model.CoveredCards.Count));
            quickLabel = null;
            if (model.CoveredCards.Count == 0) return;
            var row = (Component)rows[model.HasAbilityActionCatalog ? CharacterAction(model).OptionId : model.CoveredCardId];
            Assert.That(row, Is.Not.Null, "必须检查对应角色实例的实际动态行动行。");
            Assert.That(row.gameObject.activeSelf, Is.True);
            quickLabel = (Text)Field(row, "label");
        }

        private static GameRuleConfiguration Configuration() => new GameRuleConfiguration {
            MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
            EnabledExpansionIds = new List<string> { "core", "enterprise" }
        };

        private static object Property(object value, string name) => value.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.Instance).GetValue(value);

        private static object Field(object value, string name) => value.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
    }
}
