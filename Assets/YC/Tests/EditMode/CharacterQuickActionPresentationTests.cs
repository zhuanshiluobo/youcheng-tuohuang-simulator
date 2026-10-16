using System;
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
        private GameObject hud;
        private object actionController;
        private Text quickLabel;
        private string characterFormat;
        private string emptyCharacterName;

        [OneTimeSetUp]
        public void LoadFormalEnterpriseCatalog()
        {
            var installed = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.dataPath,
                "StreamingAssets/Content/core"));
            if (!CharacterCardDatabase.IsInitialized)
                CharacterCardDatabase.Initialize(installed.CreateCharacters());
            catalog = ActiveContentCatalogBuilder.Build(installed, Configuration());
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
            characterFormat = (string)Field(row, "characterFormat");
            emptyCharacterName = (string)Field(row, "emptyCharacterName");
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
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            var projectedState = state;
            if (client)
            {
                var session = new GameSession(GameStateCloneService.DeepClone(state));
                session.ReplaceView(visible);
                projectedState = session.State;
                Assert.That(projectedState.EffectRuntime, Is.Null);
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
            Assert.That(quickLabel.text, Is.EqualTo(string.Format(characterFormat, expectedName)));
        }

        [TestCase("mlynar")]
        [TestCase("mountain")]
        [TestCase("liskarm")]
        public void QuickActionUsesProjectedDisplayNameWithoutGuessingCardOrTemplateIds(string definitionId)
        {
            var state = CoverActualCard(definitionId);
            var definition = state.ContentDefinitions.Characters.Single(card => card.TemplateId == definitionId);
            definition.Name = "本局动态角色名称";
            var model = new CharacterCardPanelPresenter().BuildView(state, 1);
            Render(model);
            Assert.That(quickLabel.text, Is.EqualTo(string.Format(characterFormat, definition.Name)));
        }

        [TestCase("mlynar")]
        [TestCase("mountain")]
        public void LockedCoveredCardKeepsItsProjectedNameWithoutInventingAnotherQuickAction(string definitionId)
        {
            var state = CoverActualCard(definitionId);
            state.FindPlayer(1).CharacterCardLockedThisTurn = true;
            var model = new CharacterCardPanelPresenter().BuildView(state, 1);
            Assert.That(model.CanUse, Is.False);
            Render(model);
            string expectedName = model.CoveredCards.Single(card => card.CardId == model.CoveredCardId).DisplayName;
            Assert.That(quickLabel.text, Is.EqualTo(string.Format(characterFormat, expectedName)));
        }

        [Test]
        public void MissingCoveredCardUsesTheExistingConfiguredEmptyName()
        {
            Render(CharacterCardPanelViewModel.Empty(string.Empty));
            Assert.That(quickLabel.text, Is.EqualTo(string.Format(characterFormat, emptyCharacterName)));
        }

        [TestCase("mlynar")]
        [TestCase("mountain")]
        public void PendingCharacterNameStillOverridesTheCoveredCardName(string definitionId)
        {
            var state = CoverActualCard(definitionId);
            var model = new CharacterCardPanelPresenter().BuildView(state, 1);
            var pending = new CharacterCardPanelViewModel(model.HandCards, model.DiscardCards,
                model.CoveredStatus, model.InteractionStatus, model.CanCover, model.CanUse,
                model.CanUseStrategy, model.CanUseTactic, model.CanUseBoth, model.UsedCharacterThisRound,
                model.CoveredCardId, model.Originium, model.OriginiumShard, model.Iron, model.PureOriginium,
                model.StrategyEffect, model.TacticEffect, CharacterPendingChoiceTypes.SecondEffectDecision,
                "待完成的角色", model.CoveredCardBackColor, CharacterEffectModes.Tactic);
            Render(pending);
            Assert.That(quickLabel.text, Is.EqualTo(string.Format(characterFormat, pending.PendingCardDisplayName)));
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

        private void Render(CharacterCardPanelViewModel model) => actionController.GetType()
            .GetMethod("RenderCharacterQuickAction", BindingFlags.Instance | BindingFlags.Public)
            .Invoke(actionController, new object[] { model });

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
