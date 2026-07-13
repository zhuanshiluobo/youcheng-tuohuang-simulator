using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardFlowTests
    {
        [Test]
        public void CreateInitialState_GivesEachPlayerFiveOwnedCharacterCards()
        {
            var seats = new List<PlayerSeat>
            {
                new PlayerSeat { PlayerId = 1, PlayerName = "甲", Color = PlayerColor.Red },
                new PlayerSeat { PlayerId = 2, PlayerName = "乙", Color = PlayerColor.Blue }
            };

            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, "test", 17);

            Assert.That(state.FindPlayer(1).HandCardIds, Has.Count.EqualTo(5));
            Assert.That(state.FindPlayer(2).HandCardIds, Has.Count.EqualTo(5));
            Assert.That(state.FindPlayer(1).HandCardIds, Is.Unique);
            Assert.That(state.FindPlayer(1).HandCardIds, Is.Not.EquivalentTo(state.FindPlayer(2).HandCardIds));
            Assert.That(CharacterCardDatabase.Get(state.FindPlayer(1).HandCardIds[0]).Name, Is.EqualTo("雷蛇"));
            var standardTemplates = state.FindPlayer(1).HandCardIds.ConvertAll(id => CharacterCardDatabase.Get(id).TemplateId);
            Assert.That(standardTemplates, Is.EquivalentTo(new[]
            {
                CharacterCardDatabase.Liskarm,
                CharacterCardDatabase.Elysium,
                CharacterCardDatabase.Texas,
                CharacterCardDatabase.Cannot,
                CharacterCardDatabase.TinMan
            }));
            Assert.That(standardTemplates, Has.None.EqualTo("mlynar"));
            Assert.That(standardTemplates, Has.None.EqualTo("mountain"));
        }

        [Test]
        public void Cover_Succeeds_AndAllPlayersCoveredAdvancesToFirstActionRound()
        {
            var state = CreateCoverState();
            var handler = new CoverCharacterCardCommandHandler();
            var firstCard = state.FindPlayer(1).HandCardIds[0];
            var secondCard = state.FindPlayer(2).HandCardIds[0];

            var first = handler.Handle(state, CoverCommand(1, firstCard));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(2));
            var second = handler.Handle(state, CoverCommand(2, secondCard));

            Assert.That(first.Succeeded, Is.True);
            Assert.That(second.Succeeded, Is.True);
            Assert.That(first.Events[0].SubjectId, Is.Empty, "盖放事件不得泄露隐藏牌 ID。");
            Assert.That(second.Events[0].SubjectId, Is.Empty, "盖放事件不得泄露隐藏牌 ID。");
            Assert.That(state.FindPlayer(1).CoveredCharacterCardId, Is.EqualTo(firstCard));
            Assert.That(state.FindPlayer(1).HandCardIds, Does.Not.Contain(firstCard));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.ActionRound, Is.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void LocalSinglePlayerOpening_CoversOnceAndAdvancesToFirstActionRound()
        {
            var seats = new List<PlayerSeat>
            {
                new PlayerSeat { PlayerId = 1, PlayerName = "单机玩家", Color = PlayerColor.Blue }
            };
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, map.MapId, 17);
            var setup = new SetupCommandHandler(new MapQueryService(map));

            var entrance = setup.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.ChooseInitialLocation,
                PlayerId = 1,
                TargetId = "G-01"
            });
            Assert.That(entrance.Succeeded, Is.True);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));

            var cardId = state.FindPlayer(1).HandCardIds[0];
            var cover = new CoverCharacterCardCommandHandler().Handle(state, CoverCommand(1, cardId));

            Assert.That(cover.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(state.ActionRound, Is.EqualTo(1));
            Assert.That(state.CurrentPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void Cover_WithWrongPhaseOrMissingCardOrDuplicate_FailsWithoutPartialMutation()
        {
            var state = CreateCoverState();
            var handler = new CoverCharacterCardCommandHandler();
            var cardId = state.FindPlayer(1).HandCardIds[0];
            state.Phase = GamePhase.ActionRound1;

            var wrongPhase = handler.Handle(state, CoverCommand(1, cardId));
            state.Phase = GamePhase.CharacterCover;
            var missing = handler.Handle(state, CoverCommand(1, "character.red.p1.unknown"));
            var success = handler.Handle(state, CoverCommand(1, cardId));
            var duplicate = handler.Handle(state, CoverCommand(1, state.FindPlayer(1).HandCardIds[0]));

            Assert.That(wrongPhase.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(missing.Succeeded, Is.False);
            Assert.That(success.Succeeded, Is.True);
            Assert.That(duplicate.Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(state.FindPlayer(1).HandCardIds, Has.Count.EqualTo(4));
        }

        [Test]
        public void Cover_OutOfOrder_FailsWithoutMovingCard()
        {
            var state = CreateCoverState();
            var player = state.FindPlayer(2);
            var cardId = player.HandCardIds[0];

            var result = new CoverCharacterCardCommandHandler().Handle(state, CoverCommand(2, cardId));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.NotCurrentPlayer));
            Assert.That(player.HandCardIds, Does.Contain(cardId));
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
        }

        [Test]
        public void Cover_WithEmptyHand_RecyclesDiscardBeforeCovering()
        {
            var state = CreateCoverState();
            var player = state.FindPlayer(1);
            player.HandCardIds.Clear();
            player.DiscardCardIds.AddRange(CharacterCardDatabase.GetInitialCardIds(player));
            var cardId = player.DiscardCardIds[2];

            var result = new CoverCharacterCardCommandHandler().Handle(state, CoverCommand(1, cardId));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.HandCardIds, Has.Count.EqualTo(4));
            Assert.That(player.DiscardCardIds, Is.Empty);
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
        }

        [Test]
        public void Use_BothEffects_IsAllowedOnlyForStartPlayer_AndFailureIsAtomic()
        {
            var state = CreateActionStateWithCoveredCannot();
            state.CurrentPlayerId = 2;
            var player = state.FindPlayer(2);
            var cardId = player.CoveredCharacterCardId;
            var beforeResources = player.Resources.Clone();
            var command = UseCannotBothCommand(2, cardId);

            var result = new UseCharacterCardCommandHandler().Handle(state, command);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(player.DiscardCardIds, Is.Empty);
            Assert.That(player.UsedCharacterThisRound, Is.False);
            Assert.That(player.Resources.Originium, Is.EqualTo(beforeResources.Originium));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(beforeResources.GoldVoucher));
        }

        [Test]
        public void Use_StartPlayerBothEffectsInOrder_SettlesThenDiscardsCard()
        {
            var state = CreateActionStateWithCoveredCannot();
            var player = state.FindPlayer(1);
            player.Resources.Originium = 2;
            state.FindPlayer(2).Resources.Iron = 3;
            var cardId = player.CoveredCharacterCardId;
            var command = UseCannotBothCommand(1, cardId);

            var result = new UseCharacterCardCommandHandler().Handle(state, command);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.Resources.Originium, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(6));
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.DiscardCardIds, Does.Contain(cardId));
            Assert.That(player.UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void Use_StartPlayerSequentialFlow_AsksAfterFirstThenExecutesOnlyRemainingEffect()
        {
            var state = CreateActionStateWithCoveredCannot();
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            state.FindPlayer(2).Resources.Iron = 3;
            var first = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = cardId };
            first.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            first.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = CharacterEffectModes.Strategy;
            first.Parameters[CharacterEffectParameterKeys.OfferSecondEffect] = "true";

            var firstResult = new UseCharacterCardCommandHandler().Handle(state, first);

            Assert.That(firstResult.Succeeded, Is.True);
            Assert.That(state.PendingCharacterEffect.ChoiceType, Is.EqualTo(CharacterPendingChoiceTypes.SecondEffectDecision));
            Assert.That(state.PendingCharacterEffect.RemainingEffectMode, Is.EqualTo(CharacterEffectModes.Tactic));
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(player.UsedCharacterThisRound, Is.False);

            var continueCommand = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            continueCommand.Parameters[CharacterEffectParameterKeys.Choice] = CharacterEffectChoiceIds.ContinueSecondEffect;
            var continueResult = new UseCharacterCardCommandHandler().Handle(state, continueCommand);
            Assert.That(continueResult.Succeeded, Is.True);
            Assert.That(state.PendingCharacterEffect.ChoiceType, Is.EqualTo(CharacterPendingChoiceTypes.SecondEffectExecution));

            var second = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = cardId };
            second.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            second.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = CharacterEffectModes.Tactic;
            second.Parameters[CharacterEffectParameterKeys.ResourceType] = "iron";
            second.Parameters[CharacterEffectParameterKeys.OfferSecondEffect] = "true";
            var secondResult = new UseCharacterCardCommandHandler().Handle(state, second);

            Assert.That(secondResult.Succeeded, Is.True);
            Assert.That(state.PendingCharacterEffect, Is.Null);
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.DiscardCardIds, Does.Contain(cardId));
            Assert.That(player.UsedCharacterThisRound, Is.True);
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
        }

        [Test]
        public void Use_StartPlayerSequentialFlow_CanDeclineSecondEffect()
        {
            var state = CreateActionStateWithCoveredCannot();
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            var first = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = cardId };
            first.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            first.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = CharacterEffectModes.Strategy;
            first.Parameters[CharacterEffectParameterKeys.OfferSecondEffect] = "true";
            Assert.That(new UseCharacterCardCommandHandler().Handle(state, first).Succeeded, Is.True);

            var finish = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            finish.Parameters[CharacterEffectParameterKeys.Choice] = CharacterEffectChoiceIds.FinishCharacterUse;
            var result = new UseCharacterCardCommandHandler().Handle(state, finish);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.PendingCharacterEffect, Is.Null);
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void Use_CannotStrategyWithZeroSales_SucceedsWithoutChangingResources()
        {
            var state = CreateActionStateWithCoveredCannot();
            var player = state.FindPlayer(1);
            player.Resources.Originium = 2;
            player.Resources.GoldVoucher = 7;
            var cardId = player.CoveredCharacterCardId;
            var command = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = cardId };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = UseCharacterCardCommandHandler.Strategy;

            var result = new UseCharacterCardCommandHandler().Handle(state, command);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.Resources.Originium, Is.EqualTo(2));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(player.DiscardCardIds, Does.Contain(cardId));
            Assert.That(player.UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void Use_UnsupportedEffect_FailsWithoutMovingCard()
        {
            var state = CreateCoverState();
            var player = state.FindPlayer(1);
            var cardId = player.HandCardIds[0];
            player.HandCardIds.Remove(cardId);
            player.CoveredCharacterCardId = cardId;
            state.Phase = GamePhase.ActionRound1;
            state.CurrentPlayerId = 1;

            var command = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = 1, TargetId = cardId };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = UseCharacterCardCommandHandler.Strategy;
            var result = new UseCharacterCardCommandHandler().Handle(state, command);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(player.DiscardCardIds, Is.Empty);
            Assert.That(player.UsedCharacterThisRound, Is.False);
        }

        [Test]
        public void Use_AfterMainActionMarkedComplete_RemainsAvailableUntilEndAction()
        {
            var state = CreateActionStateWithCoveredCannot();
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            var roundAdvance = new RoundAdvanceService();
            roundAdvance.MarkMainActionComplete(state, player.PlayerId);

            var command = new GameCommand
            {
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = player.PlayerId,
                TargetId = cardId
            };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = UseCharacterCardCommandHandler.Strategy;

            var useResult = new UseCharacterCardCommandHandler().Handle(state, command);

            Assert.That(useResult.Succeeded, Is.True);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(player.PlayerId));
            Assert.That(player.ActedMainActionThisTurn, Is.True);
            Assert.That(player.UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void Cleanup_ReturnsUnusedCoveredCardAndDiscard_ThenEntersCharacterCover()
        {
            var state = CreateCoverState();
            state.Phase = GamePhase.Cleanup;
            state.Round = 1;
            state.MaxRounds = 8;
            var player = state.FindPlayer(1);
            var covered = player.HandCardIds[0];
            var discarded = player.HandCardIds[1];
            player.HandCardIds.Remove(covered);
            player.HandCardIds.Remove(discarded);
            player.CoveredCharacterCardId = covered;
            player.DiscardCardIds.Add(discarded);

            var result = new RoundAdvanceService().EndCompletedAction(state, state.StartPlayerId);

            Assert.That(result.IsValid, Is.True);
            Assert.That(player.HandCardIds, Does.Contain(covered));
            Assert.That(player.HandCardIds, Does.Contain(discarded));
            Assert.That(player.DiscardCardIds, Is.Empty);
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.UsedCharacterThisRound, Is.False);
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(state.ActionRound, Is.Zero);
        }

        private static GameState CreateCoverState()
        {
            var state = new GameState
            {
                Phase = GamePhase.CharacterCover,
                Round = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue }
                }
            };
            CharacterCardDatabase.InitializePlayerHand(state.FindPlayer(1));
            CharacterCardDatabase.InitializePlayerHand(state.FindPlayer(2));
            return state;
        }

        private static GameState CreateActionStateWithCoveredCannot()
        {
            var state = CreateCoverState();
            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                var cardId = player.HandCardIds.Find(id => CharacterCardDatabase.Get(id).TemplateId == CharacterCardDatabase.Cannot);
                player.HandCardIds.Remove(cardId);
                player.CoveredCharacterCardId = cardId;
            }

            state.Phase = GamePhase.ActionRound1;
            state.ActionRound = 1;
            return state;
        }

        private static GameCommand CoverCommand(int playerId, string cardId)
        {
            var command = new GameCommand { Kind = GameCommandKind.CoverCharacterCard, PlayerId = playerId, TargetId = cardId };
            command.Parameters[CoverCharacterCardCommandHandler.CardIdParameter] = cardId;
            return command;
        }

        private static GameCommand UseCannotBothCommand(int playerId, string cardId)
        {
            var command = new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = playerId, TargetId = cardId };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = UseCharacterCardCommandHandler.Both;
            command.Parameters[UseCharacterCardCommandHandler.EffectOrderParameter] = UseCharacterCardCommandHandler.StrategyFirst;
            command.Parameters[CharacterEffectParameterKeys.SaleOriginium] = "1";
            command.Parameters[CharacterEffectParameterKeys.ResourceType] = "iron";
            return command;
        }
    }
}
