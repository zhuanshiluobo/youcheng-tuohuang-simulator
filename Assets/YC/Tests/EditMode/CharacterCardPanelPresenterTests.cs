using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Infrastructure.Lua;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardPanelPresenterTests
    {
        [Test, Category("角色详情投影")]
        public void MemberActiveUseDetailsReadAuthorizedModelAndRefreshWithoutRuntime()
        {
            var host = CreateActiveUseDetailState();
            var visible = GameStateViewProjector.ProjectForPlayer(host, 1);
            var member = GameStateViewProjector.ToClientState(visible);
            Assert.That(member.EffectRuntime, Is.Null);
            Assert.That(member.CharacterUses.Any(use => !use.Finished && use.ExecutingPlayerId == 1), Is.True);
            var presenter = new CharacterCardPanelPresenter();
            var model = presenter.BuildProjectedView(member, 1, visible);
            Assert.That(model.CanUse, Is.True);
            Assert.That(model.CurrentUseSelection, Is.Not.Null);
            Assert.That(model.CurrentUseSelection.InteractionTypeId, Is.EqualTo(CharacterLifecycleService.ModeInteractionTypeId));
            Assert.That(model.CanUseStrategy, Is.True);
            Assert.That(model.CanUseTactic, Is.True);
            var beforeMember = JsonConvert.SerializeObject(member);
            var beforeVisible = JsonConvert.SerializeObject(visible);
            for (var refresh = 0; refresh < 3; refresh++)
            {
                Assert.That(presenter.GetEffectUnavailableReason(model, false), Is.Empty);
                Assert.That(presenter.GetEffectUnavailableReason(model, true), Is.Empty);
                model = presenter.BuildProjectedView(member, 1, visible);
                Assert.That(model.CurrentUseSelection.InteractionTypeId, Is.EqualTo(CharacterLifecycleService.ModeInteractionTypeId));
            }
            Assert.That(JsonConvert.SerializeObject(member), Is.EqualTo(beforeMember));
            Assert.That(JsonConvert.SerializeObject(visible), Is.EqualTo(beforeVisible));
            Assert.That(member.EffectRuntime, Is.Null);
        }

        [Test, Category("角色详情投影")]
        public void MemberUnavailableModeAndOtherViewerDetailsKeepAuthorizedBoundaries()
        {
            var host = CreateActiveUseDetailState(out var registry);
            var request = host.EffectRuntime.InteractionRequests.Single(value => value.Status == "open" &&
                value.AnsweringPlayerId == 1 && value.InteractionTypeId == CharacterLifecycleService.ModeInteractionTypeId);
            Assert.That(request.CandidateIds, Does.Contain("strategy"));
            var answered = new AnswerInteractionCommandHandler(registry).Handle(host,
                EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { "strategy" }));
            Assert.That(answered.Succeeded, Is.True, answered.Validation?.Reason);
            var visible = GameStateViewProjector.ProjectForPlayer(host, 1);
            var member = GameStateViewProjector.ToClientState(visible);
            Assert.That(member.EffectRuntime, Is.Null);
            var presenter = new CharacterCardPanelPresenter();
            var model = presenter.BuildProjectedView(member, 1, visible);
            Assert.That(model.CanUseStrategy, Is.False);
            var beforeMember = JsonConvert.SerializeObject(member);
            var before = JsonConvert.SerializeObject(visible);
            for (var refresh = 0; refresh < 3; refresh++)
            {
                Assert.That(presenter.GetEffectUnavailableReason(model, true), Is.Not.Empty);
                Assert.That(presenter.GetEffectUnavailableReason(model, false).Length == 0, Is.EqualTo(model.CanUseTactic));
                model = presenter.BuildProjectedView(member, 1, visible);
            }
            Assert.That(JsonConvert.SerializeObject(visible), Is.EqualTo(before));
            Assert.That(JsonConvert.SerializeObject(member), Is.EqualTo(beforeMember));
            Assert.That(member.EffectRuntime, Is.Null);
            foreach (var restricted in new[] { GameStateViewProjector.ProjectForPlayer(host, 2),
                GameStateViewProjector.ProjectForSpectator(host) })
            {
                var client = GameStateViewProjector.ToClientState(restricted);
                Assert.That(client.EffectRuntime, Is.Null);
                var hidden = presenter.BuildProjectedView(client, 1, restricted);
                Assert.That(hidden.CanUse, Is.False);
                Assert.That(hidden.CurrentUseSelection, Is.Null);
                Assert.That(hidden.CoveredCards, Is.Empty);
                Assert.That(hidden.CardDefinitionIds, Is.Empty);
                Assert.That(presenter.GetEffectUnavailableReason(hidden, false), Is.Empty);
                Assert.That(presenter.GetEffectUnavailableReason(hidden, true), Is.Empty);
            }
        }

        private static GameState CreateActiveUseDetailState() => CreateActiveUseDetailState(out _);
        private static GameState CreateActiveUseDetailState(out EffectRegistry registry)
        {
            var seats = new List<PlayerSeat> {
                new PlayerSeat { PlayerId = 1, Color = PlayerColor.Red },
                new PlayerSeat { PlayerId = 2, Color = PlayerColor.Blue },
                new PlayerSeat { PlayerId = 3, Color = PlayerColor.Green } };
            var configuration = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.ThreePlayerMapId };
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Current, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, configuration, 71, catalog.SetupRecipe, catalog.ContentDefinitions);
            registry = new EffectRegistry();
            LuaContentCatalog.Register(registry, catalog);
            var started = new RoundExecutionService(registry).CreateRound(state, 1, 1);
            Assert.That(started.IsValid, Is.True, started.Reason);
            var cover = new CoverCharacterCardCommandHandler(new CharacterCardService(), registry);
            string usedCard = null;
            foreach (var seat in seats)
            {
                var card = state.ItemZones.Items.First(item => item.ControllerSubject.PlayerId == seat.PlayerId &&
                    item.DefinitionId == (seat.PlayerId == 1 ? CharacterCardDatabase.Elysium : CharacterCardDatabase.Liskarm) &&
                    state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.character_hand"));
                var result = cover.Handle(state, new GameCommand { Kind = GameCommandKind.CoverCharacterCard, PlayerId = seat.PlayerId, TargetId = card.ItemId });
                Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
                if (seat.PlayerId == 1) usedCard = card.ItemId;
            }
            var used = new UseCharacterCardCommandHandler(new CharacterCardService(), registry).Handle(state,
                new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = usedCard });
            Assert.That(used.Succeeded, Is.True, used.Validation?.Reason);
            return state;
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void EntityDisplayOrderIsStableAcrossExpansionsAndClientViewsWithoutChangingInstances(int expansions)
        {
            var configuration = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.ThreePlayerMapId };
            if (expansions >= 1) configuration.EnabledExpansionIds.Add("enterprise");
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Current, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var seats = new List<PlayerSeat> {
                new PlayerSeat { PlayerId = 1, Color = PlayerColor.Red },
                new PlayerSeat { PlayerId = 2, Color = PlayerColor.Blue },
                new PlayerSeat { PlayerId = 3, Color = PlayerColor.Green } };
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, configuration, 71,
                catalog.SetupRecipe, catalog.ContentDefinitions);
            state.Phase = GamePhase.CharacterCover;
            state.CurrentPlayerId = 1;
            var handZone = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.character_hand" && zone.OwnerPlayerId == 1);
            var hand = state.ItemZones.Items.Where(item => item.ZoneId == handZone.ZoneId).ToList();
            if (expansions == 2)
            {
                // 正式感染者包尚未登记：只用真实定义的受控可见实例验证显示排序，不冒充开局集成。
                var json = JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(Environment.CurrentDirectory,
                    "Assets/StreamingAssets/Content/core/character/infected.character.amiya.json")));
                var definition = json["data"].ToObject<CharacterCardDefinition>();
                definition.TemplateId = definition.CardId = (string)json["definitionId"];
                definition.Name = (string)json["displayName"];
                state.ContentDefinitions.Characters.Add(definition);
                var infected = GameStateCloneService.DeepClone(hand[0]);
                infected.ItemId = "visible-infected-character-instance";
                infected.DefinitionId = definition.TemplateId;
                state.ItemZones.Items.Add(infected);
                hand.Add(infected);
            }
            var duplicate = GameStateCloneService.DeepClone(hand.Single(item => item.DefinitionId == CharacterCardDatabase.Liskarm));
            duplicate.ItemId = "second-visible-liskarm-instance";
            state.ItemZones.Items.Add(duplicate);
            hand.Add(duplicate);
            var expected = hand.Where(item => item.DefinitionId == CharacterCardDatabase.Liskarm)
                .OrderBy(item => item.ItemId, StringComparer.Ordinal).Select(item => item.ItemId)
                .Concat(hand.Where(item => item.DefinitionId != CharacterCardDatabase.Liskarm).Select(item => item.ItemId)).ToArray();
            state.ItemZones.Items.Reverse(); // 模拟不同合法序列化枚举顺序，不把此顺序当显示合同。
            var before = JsonConvert.SerializeObject(state.ItemZones);
            var presenter = new CharacterCardPanelPresenter();
            var host = presenter.BuildView(state, 1);
            Assert.That(host.HandCards.Select(card => card.CardId), Is.EqualTo(expected));
            Assert.That(host.HandCards.Count, Is.EqualTo(expansions == 0 ? 6 : expansions == 1 ? 8 : 9));
            Assert.That(host.HandCards.All(card => card.CanCover), Is.True);
            var client = new GameSession(GameStateCloneService.DeepClone(state));
            var clientState = SimulationUiTestShell.ReadState(client.State, client.RefreshLocalProjection(GameStateViewer.Player(1)), 1);
            Assert.That(presenter.BuildView(clientState, 1).HandCards.Select(card => card.CardId), Is.EqualTo(expected));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            foreach (var template in new[] { "core.zone.character_discard", "core.zone.character_covered" })
            {
                var target = state.ItemZones.Zones.Single(zone => zone.TemplateId == template && zone.OwnerPlayerId == 1);
                foreach (var card in hand) card.ZoneId = target.ZoneId;
                state.Phase = GamePhase.ActionRound1;
                before = JsonConvert.SerializeObject(state.ItemZones);
                var model = presenter.BuildView(state, 1);
                var displayed = template.EndsWith("discard", StringComparison.Ordinal) ? model.DiscardCards : model.CoveredCards;
                Assert.That(displayed.Select(card => card.CardId), Is.EqualTo(expected));
                Assert.That(JsonConvert.SerializeObject(state.ItemZones), Is.EqualTo(before));
            }
        }

        [Test]
        public void DisplayOrderUsesFrozenExtensionsAndKeepsUnknownAndDuplicateInstances()
        {
            var definitions = new GameContentDefinitions { Characters = new List<CharacterCardDefinition> {
                new CharacterCardDefinition { TemplateId = "extension-z" },
                new CharacterCardDefinition { TemplateId = "extension-a" } } };
            var cards = new[] { ("unknown", "u"), ("extension-a", "a"), ("liskarm", "2"),
                ("extension-z", "z"), ("liskarm", "1"), ("tin-man", "t") };
            var ordered = new CharacterCardDisplayOrder(definitions).Sort(cards, card => card.Item1, card => card.Item2);
            Assert.That(ordered.Select(card => card.Item2), Is.EqualTo(new[] { "1", "2", "t", "z", "a", "u" }));
            Assert.That(cards[0].Item2, Is.EqualTo("u"));
        }

        [Test]
        public void EntityOpeningUsesFrozenDefinitionsAndRealCoverOrderInHostAndClientViews()
        {
            var seats = new System.Collections.Generic.List<PlayerSeat>
            {
                new PlayerSeat { PlayerId = 1, Color = PlayerColor.Red },
                new PlayerSeat { PlayerId = 2, Color = PlayerColor.Blue },
                new PlayerSeat { PlayerId = 3, Color = PlayerColor.Green }
            };
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, StaticMapDefinitions.ThreePlayerMapId, 71);
            state.ContentDefinitions.Characters.Find(card => card.TemplateId == CharacterCardDatabase.Liskarm).Name = "本局角色标题";
            var registry = new EffectRegistry();
            var started = new RoundExecutionService(registry).CreateRound(state, 1, 1);
            Assert.That(started.IsValid, Is.True, started.Reason);
            var presenter = new CharacterCardPanelPresenter();
            var firstView = presenter.BuildView(state, 1);
            Assert.That(firstView.CanCover, Is.True, "其他玩家的独立盖牌请求不能阻塞本人。");
            Assert.That(firstView.HandCards[0].DisplayName, Is.EqualTo("本局角色标题"));
            Assert.That(presenter.BuildView(state, 2).CanCover, Is.False);
            var handler = new CoverCharacterCardCommandHandler(new CharacterCardService(), registry);
            string firstCard = state.FindPlayer(1).HandCardIds[0];
            Assert.That(handler.Handle(state, new GameCommand { Kind = GameCommandKind.CoverCharacterCard, PlayerId = 1, TargetId = firstCard }).Succeeded, Is.True);
            var client = new GameSession(GameStateCloneService.DeepClone(state));
            var clientState = SimulationUiTestShell.ReadState(client.State, client.RefreshLocalProjection(GameStateViewer.Player(2)), 2);
            Assert.That(client.State.EffectRuntime, Is.Not.Null);
            Assert.That(clientState.EffectRuntime.EffectNodes, Is.Empty, "UI 读取壳不包含完整效果执行树。");
            Assert.That(clientState.EffectRuntime.InteractionRequests.All(request => request.AnsweringPlayerId == 2), Is.True);
            var clientView = presenter.BuildView(clientState, 2);
            Assert.That(clientView.CanCover, Is.True);
            Assert.That(clientView.HandCards[0].DisplayName, Is.EqualTo("本局角色标题"));
            foreach (var seat in seats)
            {
                if (seat.PlayerId == 1) continue;
                string cardId = state.FindPlayer(seat.PlayerId).HandCardIds[0];
                var covered = handler.Handle(state, new GameCommand { Kind = GameCommandKind.CoverCharacterCard, PlayerId = seat.PlayerId, TargetId = cardId });
                Assert.That(covered.Succeeded, Is.True, covered.Validation?.Reason);
            }
            var actionView = presenter.BuildView(state, 1);
            Assert.That(actionView.CanUseStrategy, Is.True);
            Assert.That(actionView.StrategyEffect, Is.EqualTo(state.ContentDefinitions.Characters.Find(card => card.TemplateId == CharacterCardDatabase.Liskarm).StrategyEffect));
        }

        [Test]
        public void BuildView_ShowsKnownHandNamesAndSafelyFallsBackForUnknownCard()
        {
            var state = CreateState(GamePhase.CharacterCover);
            var player = state.FindPlayer(1);
            player.HandCardIds.Add("character-red-liskarm");
            player.HandCardIds.Add("expansion-card-7");

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.HandCards.Count, Is.EqualTo(2));
            Assert.That(view.HandCards[0].DisplayName, Is.EqualTo("雷蛇"));
            Assert.That(view.HandCards[1].DisplayName, Is.EqualTo("expansion-card-7"));
            Assert.That(view.HandCards[0].CanCover, Is.True);
            Assert.That(view.InteractionStatus, Does.Contain("单选后确认"));
            Assert.That(view.InteractionStatus, Does.Not.Contain("拖动"));
        }

        [Test]
        public void BuildView_CoveredCardOnlyExposesBackSideStatus()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "secret-texas-card-id";

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.CoveredStatus, Is.EqualTo("已盖放（背面）"));
            Assert.That(view.CoveredStatus, Does.Not.Contain("texas"));
            Assert.That(view.InteractionStatus, Does.Not.Contain("secret"));
            Assert.That(view.CoveredCardBackColor, Is.EqualTo(PlayerColor.Red));
        }

        [Test]
        public void BuildView_StandardHandProvidesFiveStableIdsWithoutExpansionCards()
        {
            var state = CreateState(GamePhase.CharacterCover);
            var player = state.FindPlayer(1);
            CharacterCardDatabase.InitializePlayerHand(player);

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.HandCards, Has.Count.EqualTo(5));
            Assert.That(view.HandCards, Has.All.Matches<CharacterCardHandItemViewModel>(item =>
                !string.IsNullOrEmpty(item.CardId)));
            Assert.That(view.HandCards, Has.None.Matches<CharacterCardHandItemViewModel>(item =>
                item.CardId.Contains("mlynar") || item.CardId.Contains("mountain")));
        }

        [Test]
        public void BuildView_DiscardAreaKeepsStableIdsButNeverAllowsCover()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.DiscardCardIds.Add("character.red.p1.liskarm");
            player.DiscardCardIds.Add("character.red.p1.texas");

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.DiscardCards, Has.Count.EqualTo(2));
            Assert.That(view.DiscardCards[0].DisplayName, Is.EqualTo("雷蛇"));
            Assert.That(view.DiscardCards, Has.All.Matches<CharacterCardHandItemViewModel>(item =>
                !item.CanCover && !string.IsNullOrEmpty(item.CardId)));
        }

        [Test]
        public void BuildView_CoveredCardCarriesPlayerColor()
        {
            EditModeTestCaseRunner.Run(
                new[] { PlayerColor.Red, PlayerColor.Yellow, PlayerColor.Green, PlayerColor.Blue },
                color =>
                {
                    var state = CreateState(GamePhase.ActionRound1);
                    var player = state.FindPlayer(1);
                    player.Color = color;
                    player.CoveredCharacterCardId = "character.red.p1.liskarm";

                    var view = new CharacterCardPanelPresenter().BuildView(state, 1);

                    Assert.That(view.CoveredCardBackColor, Is.EqualTo(color), "color=" + color);
                },
                color => "color=" + color);
        }

        [Test]
        public void CreateCoverCommand_WritesCardIdParameter()
        {
            var command = new CharacterCardPanelPresenter().CreateCoverCommand(2, "character-blue-elysium");

            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.CoverCharacterCard));
            Assert.That(command.PlayerId, Is.EqualTo(2));
            Assert.That(command.TargetId, Is.EqualTo("character-blue-elysium"));
            Assert.That(command.Parameters[CoverCharacterCardCommandHandler.CardIdParameter], Is.EqualTo("character-blue-elysium"));
        }

        [Test]
        public void BuildView_OutsideCoverPhaseMakesHandReadOnly()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).HandCardIds.Add("character-red-texas");

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.HandCards[0].CanCover, Is.False);
        }

        [Test]
        public void StartPlayer_FirstSelectionShowsTwoSingleEffectsWithoutLegacyBothEntry()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.StartPlayerId = 1;
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";
            var presenter = new CharacterCardPanelPresenter();

            var view = presenter.BuildView(state, 1);
            Assert.That(view.CanUseStrategy, Is.True);
            Assert.That(view.CanUseTactic, Is.True);
            Assert.That(view.CanUseBoth, Is.False);
        }

        [Test]
        public void NonStartPlayer_DoesNotReceiveBothEffectsEntry()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.StartPlayerId = 2;
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.CanUse, Is.True);
            Assert.That(view.CanUseBoth, Is.False);
        }

        [Test]
        public void PendingSecondEffectExecution_OnlyExposesConfirmedRemainingSide()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.SecondEffectExecution,
                PlayerId = 1,
                CardId = "character.red.p1.cannot",
                RemainingEffectMode = CharacterEffectModes.Tactic,
                OptionIds = { CharacterEffectModes.Tactic }
            };

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.CanUse, Is.True);
            Assert.That(view.CanUseStrategy, Is.False);
            Assert.That(view.CanUseTactic, Is.True);
            Assert.That(view.IsSecondEffectExecution, Is.True);
        }

        [Test]
        public void PendingSecondEffectDecision_ExposesRemainingSideWithoutBlockingCardPanel()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.cannot";
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.SecondEffectDecision,
                PlayerId = 1,
                CardId = "character.red.p1.cannot",
                RemainingEffectMode = CharacterEffectModes.Tactic,
                OptionIds =
                {
                    CharacterEffectChoiceIds.ContinueSecondEffect,
                    CharacterEffectChoiceIds.FinishCharacterUse
                }
            };

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.CanUse, Is.True);
            Assert.That(view.CanUseStrategy, Is.False);
            Assert.That(view.CanUseTactic, Is.True);
            Assert.That(view.IsSecondEffectDecision, Is.True);
            Assert.That(view.InteractionStatus, Does.Contain("可继续使用第二个效果").And.Contain("结束角色牌使用"));
        }

        [Test]
        public void PendingSecondEffectDecision_WithoutContinueOption_OnlyAllowsEndAction()
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = "character.red.p1.liskarm";
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.SecondEffectDecision,
                PlayerId = 1,
                CardId = "character.red.p1.liskarm",
                RemainingEffectMode = CharacterEffectModes.Tactic,
                OptionIds = { CharacterEffectChoiceIds.FinishCharacterUse }
            };

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.IsSecondEffectDecision, Is.True);
            Assert.That(view.CanUse, Is.True, "角色牌结算未完成时仍应允许从主界面重新打开角色牌。");
            Assert.That(view.CanUseStrategy, Is.False);
            Assert.That(view.CanUseTactic, Is.False);
            Assert.That(view.InteractionStatus, Does.Contain("结束操作完成结算").And.Not.Contain("翻转"));
        }

        [Test]
        public void CannotUseCommand_CopiesStrategyTacticAndBothEffectParameters()
        {
            var presenter = new CharacterCardPanelPresenter();
            var parameters = new System.Collections.Generic.Dictionary<string, string>
            {
                [CharacterEffectParameterKeys.SaleOriginium] = "1",
                [CharacterEffectParameterKeys.SaleOriginiumShard] = "2",
                [CharacterEffectParameterKeys.SaleIron] = "3",
                [CharacterEffectParameterKeys.SalePureOriginium] = "4",
                [CharacterEffectParameterKeys.ResourceType] = "iron"
            };

            var command = presenter.CreateUseCommand(
                1,
                "character.red.p1.cannot",
                UseCharacterCardCommandHandler.Both,
                UseCharacterCardCommandHandler.StrategyFirst,
                parameters);

            Assert.That(command.Parameters[CharacterEffectParameterKeys.SaleOriginium], Is.EqualTo("1"));
            Assert.That(command.Parameters[CharacterEffectParameterKeys.SaleOriginiumShard], Is.EqualTo("2"));
            Assert.That(command.Parameters[CharacterEffectParameterKeys.SaleIron], Is.EqualTo("3"));
            Assert.That(command.Parameters[CharacterEffectParameterKeys.SalePureOriginium], Is.EqualTo("4"));
            Assert.That(command.Parameters[CharacterEffectParameterKeys.ResourceType], Is.EqualTo("iron"));
        }

        [Test]
        public void UnsupportedOrUnknownCoveredCard_HasNoUsableEffectEntry()
        {
            EditModeTestCaseRunner.Run(
                new[] { "character-red-liskarm", "unknown-character-card" },
                cardId =>
                {
                    var state = CreateState(GamePhase.ActionRound1);
                    state.FindPlayer(1).CoveredCharacterCardId = cardId;

                    var view = new CharacterCardPanelPresenter().BuildView(state, 1);

                    Assert.That(view.CanUse, Is.False, cardId);
                    Assert.That(view.CanUseStrategy, Is.False, cardId);
                    Assert.That(view.CanUseTactic, Is.False, cardId);
                    Assert.That(view.CanUseBoth, Is.False, cardId);
                    Assert.That(view.InteractionStatus, Is.EqualTo("效果尚未接入"), cardId);
                },
                cardId => "cardId=" + cardId);
        }

        [Test]
        public void UsedCharacterAndPendingChoice_DisableFurtherInteraction()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.CoveredCharacterCardId = "character-red-texas";
            player.UsedCharacterThisRound = true;

            var used = new CharacterCardPanelPresenter().BuildView(state, 1);
            Assert.That(used.CanUse, Is.False);
            Assert.That(used.CoveredStatus, Is.EqualTo("本回合角色牌已使用"));

            player.UsedCharacterThisRound = false;
            state.PendingChoice = new PendingChoiceState
            {
                ChoiceId = "choice",
                PlayerId = 1,
                ChoiceType = "test",
                CardId = "event",
                OptionIds = { "one" }
            };
            var pending = new CharacterCardPanelPresenter().BuildView(state, 1);
            Assert.That(pending.CanUse, Is.False);
            Assert.That(pending.InteractionStatus, Is.EqualTo("请先处理待选择项"));
        }

        [Test]
        public void CharacterCardLockedThisTurn_DisablesEveryUseEntryAndShowsReason()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.CoveredCharacterCardId = "character.red.p1.cannot";
            player.CharacterCardLockedThisTurn = true;

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.CanUse, Is.False);
            Assert.That(view.CanUseStrategy, Is.False);
            Assert.That(view.CanUseTactic, Is.False);
            Assert.That(view.CoveredStatus, Does.Contain("锁定"));
        }

        private static GameState CreateState(GamePhase phase)
        {
            return new GameState
            {
                Phase = phase,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Name = "玩家一", Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Name = "玩家二", Color = PlayerColor.Blue }
                }
            };
        }
    }
}
