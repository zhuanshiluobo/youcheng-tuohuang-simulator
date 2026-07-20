using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardPanelPresenterTests
    {
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
            Assert.That(view.InteractionStatus, Is.EqualTo("拖动到主要行动卡上即可盖放"));
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
            Assert.That(view.CoveredBackImageRelativePath, Does.EndWith("back-red.jpg"));
            Assert.That(view.CoveredBackImageRelativePath, Does.Not.Contain("texas"));
        }

        [Test]
        public void BuildView_StandardHandProvidesFiveFrontImagesWithoutExpansionCards()
        {
            var state = CreateState(GamePhase.CharacterCover);
            var player = state.FindPlayer(1);
            CharacterCardDatabase.InitializePlayerHand(player);

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.HandCards, Has.Count.EqualTo(5));
            Assert.That(view.HandCards, Has.All.Matches<CharacterCardHandItemViewModel>(item =>
                !string.IsNullOrEmpty(item.FrontImageRelativePath) &&
                item.FrontImageRelativePath.Contains("/CardImages/Characters/")));
            Assert.That(view.HandCards, Has.None.Matches<CharacterCardHandItemViewModel>(item =>
                item.CardId.Contains("mlynar") || item.CardId.Contains("mountain")));
        }

        [Test]
        public void BuildView_DiscardAreaShowsFrontImagesButNeverAllowsCover()
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.DiscardCardIds.Add("character.red.p1.liskarm");
            player.DiscardCardIds.Add("character.red.p1.texas");

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.DiscardCards, Has.Count.EqualTo(2));
            Assert.That(view.DiscardCards[0].DisplayName, Is.EqualTo("雷蛇"));
            Assert.That(view.DiscardCards, Has.All.Matches<CharacterCardHandItemViewModel>(item =>
                !item.CanCover && !string.IsNullOrEmpty(item.FrontImageRelativePath)));
        }

        [TestCase(PlayerColor.Red, "back-red.jpg")]
        [TestCase(PlayerColor.Yellow, "back-yellow.jpg")]
        [TestCase(PlayerColor.Green, "back-green.jpg")]
        [TestCase(PlayerColor.Blue, "back-blue.jpg")]
        public void BuildView_CoveredCardUsesPlayerColorBack(PlayerColor color, string expectedFile)
        {
            var state = CreateState(GamePhase.ActionRound1);
            var player = state.FindPlayer(1);
            player.Color = color;
            player.CoveredCharacterCardId = "character.red.p1.liskarm";

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.CoveredBackImageRelativePath, Does.EndWith(expectedFile));
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
            Assert.That(view.InteractionStatus, Does.Contain("可继续使用第二个效果").And.Contain("点击翻转"));
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

        [TestCase("character-red-liskarm")]
        [TestCase("unknown-character-card")]
        public void UnsupportedOrUnknownCoveredCard_HasNoUsableEffectEntry(string cardId)
        {
            var state = CreateState(GamePhase.ActionRound1);
            state.FindPlayer(1).CoveredCharacterCardId = cardId;

            var view = new CharacterCardPanelPresenter().BuildView(state, 1);

            Assert.That(view.CanUse, Is.False);
            Assert.That(view.CanUseStrategy, Is.False);
            Assert.That(view.CanUseTactic, Is.False);
            Assert.That(view.CanUseBoth, Is.False);
            Assert.That(view.InteractionStatus, Is.EqualTo("效果尚未接入"));
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
