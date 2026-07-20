using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardRemainingEffectsTests
    {
        [Test]
        public void ElysiumStrategy_SelectsTiedMinimumAndGainsFour()
        {
            var state = CreateActionState(CharacterCardDatabase.Elysium);
            var player = state.FindPlayer(1);
            player.Resources.Originium = 1;
            player.Resources.OriginiumShard = 1;
            player.Resources.Iron = 3;

            var result = Use(state, CharacterEffectModes.Strategy, command =>
                command.Parameters[CharacterEffectParameterKeys.ResourceType] = "originium-shard");

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(5));
            Assert.That(player.Resources.Originium, Is.EqualTo(1));
            Assert.That(player.DiscardCardIds, Does.Contain(CardId(state, CharacterCardDatabase.Elysium)));
        }

        [Test]
        public void ElysiumStrategy_SelectingNonMinimumFailsAtomically()
        {
            var state = CreateActionState(CharacterCardDatabase.Elysium);
            var player = state.FindPlayer(1);
            player.Resources.Originium = 1;
            player.Resources.OriginiumShard = 2;
            player.Resources.Iron = 3;
            var cardId = player.CoveredCharacterCardId;

            var result = Use(state, CharacterEffectModes.Strategy, command =>
                command.Parameters[CharacterEffectParameterKeys.ResourceType] = "iron");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.Resources.Iron, Is.EqualTo(3));
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(player.DiscardCardIds, Is.Empty);
        }

        [Test]
        public void TexasStrategy_GainsGoldMovesSupplyCardToDeckBottomAndRefills()
        {
            var state = CreateActionState(CharacterCardDatabase.Texas);
            state.Decks.FacilitySupply.AddRange(new[] { "f1", "f2", "f3", "f4", "f5", "f6" });
            state.Decks.FacilityDeck.AddRange(new[] { "deck-top", "deck-bottom" });

            var result = Use(state, CharacterEffectModes.Strategy, command =>
                command.Parameters[CharacterEffectParameterKeys.FacilityCardId] = "f3");

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(state.Decks.FacilitySupply, Has.Count.EqualTo(6));
            Assert.That(state.Decks.FacilitySupply, Does.Contain("deck-top"));
            Assert.That(state.Decks.FacilitySupply, Does.Not.Contain("f3"));
            Assert.That(state.Decks.FacilityDeck, Is.EqualTo(new[] { "deck-bottom", "f3" }));
        }

        [Test]
        public void TexasStrategy_WhenDeckEmptyMovedCardImmediatelyRefillsFaceUp()
        {
            var state = CreateActionState(CharacterCardDatabase.Texas);
            state.Decks.FacilitySupply.AddRange(new[] { "f1", "f2", "f3", "f4", "f5", "f6" });

            var result = Use(state, CharacterEffectModes.Strategy, command =>
                command.Parameters[CharacterEffectParameterKeys.FacilityCardId] = "f4");

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Decks.FacilitySupply, Has.Count.EqualTo(6));
            Assert.That(state.Decks.FacilitySupply, Does.Contain("f4"));
            Assert.That(state.Decks.FacilityDeck, Is.Empty);
        }

        [Test]
        public void TexasStrategy_InvalidSupplyTargetDoesNotGrantGoldOrMoveCard()
        {
            var state = CreateActionState(CharacterCardDatabase.Texas);
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            state.Decks.FacilitySupply.Add("f1");

            var result = Use(state, CharacterEffectModes.Strategy, command =>
                command.Parameters[CharacterEffectParameterKeys.FacilityCardId] = "missing");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(new[] { "f1" }));
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
        }

        [Test]
        public void TinManTactic_ResolvesEachDiscardChoiceThenRecallsAllCards()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.DiscardCardIds.Add("discard-a");
            player.DiscardCardIds.Add("discard-b");
            var tinManCardId = player.CoveredCharacterCardId;

            var use = Use(state, CharacterEffectModes.Tactic, null);

            Assert.That(use.Succeeded, Is.True);
            Assert.That(state.HasPendingChoice(), Is.True);
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(tinManCardId));
            Assert.That(player.DiscardCardIds, Is.EqualTo(new[] { "discard-a", "discard-b" }));

            var first = Resolve(state, CharacterEffectChoiceIds.GainGold);
            Assert.That(first.Succeeded, Is.True);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(player.HandCardIds, Is.Empty);
            Assert.That(state.HasPendingChoice(), Is.True);

            var second = Resolve(state, CharacterEffectChoiceIds.GainGold);
            Assert.That(second.Succeeded, Is.True);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(10));
            Assert.That(player.HandCardIds, Is.EquivalentTo(new[] { "discard-a", "discard-b" }));
            Assert.That(player.DiscardCardIds, Is.EqualTo(new[] { tinManCardId }));
            Assert.That(player.UsedCharacterThisRound, Is.True);
            Assert.That(state.HasPendingChoice(), Is.False);
        }

        [Test]
        public void TinManTactic_InvalidChoiceKeepsPendingStateAndResourcesUntouched()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.DiscardCardIds.Add("discard-a");
            Use(state, CharacterEffectModes.Tactic, null);

            var result = Resolve(state, "unknown");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(player.DiscardCardIds, Is.EqualTo(new[] { "discard-a" }));
            Assert.That(state.PendingCharacterEffect.RemainingCardIds, Is.EqualTo(new[] { "discard-a" }));
        }

        [Test]
        public void TinManStrategy_AwardsFixedScoreThenWaitsForFirstPurchaseDecision()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            player.Resources.GoldVoucher = 40;

            var result = Use(state, CharacterEffectModes.Strategy, null);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(40));
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManFirstPurchase));
            Assert.That(state.PendingCharacterEffect.OptionIds,
                Does.Contain(CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium));
        }

        [Test]
        public void TinManStrategy_PurchasesProgressivelyAndSettlesAfterSecondDecision()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            player.Resources.GoldVoucher = 40;

            Assert.That(Use(state, CharacterEffectModes.Strategy, null).Succeeded, Is.True);
            var first = Resolve(state, CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);

            Assert.That(first.Succeeded, Is.True);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(28));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(player.CoveredCharacterCardId, Is.EqualTo(cardId));
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManSecondPurchase));
            Assert.That(state.PendingCharacterEffect.OptionIds,
                Does.Contain(CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium));

            var repeatedFirst = Resolve(
                state,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);
            Assert.That(repeatedFirst.Succeeded, Is.False);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(28));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(1));

            var second = Resolve(state, CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium);

            Assert.That(second.Succeeded, Is.True);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(13));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(2));
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.DiscardCardIds, Does.Contain(cardId));
            Assert.That(state.PendingCharacterEffect, Is.Null);
        }

        [Test]
        public void TinManStrategy_CancelAfterFirstPurchase_SettlesWithOnePureOriginium()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            player.Resources.GoldVoucher = 20;

            Assert.That(Use(state, CharacterEffectModes.Strategy, null).Succeeded, Is.True);
            Assert.That(
                Resolve(state, CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium).Succeeded,
                Is.True);
            Assert.That(state.PendingCharacterEffect.OptionIds,
                Does.Not.Contain(CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium));

            var cancel = Resolve(state, CharacterEffectChoiceIds.TinManFinishPurchasing);

            Assert.That(cancel.Succeeded, Is.True);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(player.CoveredCharacterCardId, Is.Empty);
            Assert.That(player.DiscardCardIds, Does.Contain(cardId));
            Assert.That(state.PendingCharacterEffect, Is.Null);
        }

        [Test]
        public void TinManStrategy_StaleOrOutOfOrderPurchaseIsRejectedWithoutMutation()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 12;
            Assert.That(Use(state, CharacterEffectModes.Strategy, null).Succeeded, Is.True);

            var outOfOrder = Resolve(
                state,
                CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium);
            Assert.That(outOfOrder.Succeeded, Is.False);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManFirstPurchase));

            player.Resources.GoldVoucher = 11;
            var staleBalance = Resolve(
                state,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);
            Assert.That(staleBalance.Succeeded, Is.False);
            Assert.That(staleBalance.Validation.Reason, Does.Contain("金券不足"));
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(11));
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManFirstPurchase));
        }

        [Test]
        public void TinManStrategy_FinalCancelLogsOnceAndReplayCannotAwardAgain()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            var session = new GameSession(state);
            session.RegisterHandler(new UseCharacterCardCommandHandler());
            var command = new GameCommand
            {
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1,
                TargetId = cardId
            };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = CharacterEffectModes.Strategy;

            var start = session.Submit(command);
            Assert.That(start.Succeeded, Is.True);
            Assert.That(state.Logs, Is.Empty);

            var finish = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            };
            finish.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] =
                CharacterEffectChoiceIds.TinManFinishPurchasing;
            finish.Parameters[CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId] =
                state.PendingCharacterEffect.SourceCommandId;
            var firstFinish = session.Submit(finish);
            var replay = session.Submit(finish);

            Assert.That(firstFinish.Succeeded, Is.True);
            Assert.That(replay.Succeeded, Is.False);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(state.Logs, Has.Count.EqualTo(1));
            Assert.That(state.Logs[0].Message, Does.Contain("锡人").And.Contain("完成全部结算"));
        }

        [Test]
        public void TinManStrategy_PurchaseCommandFromCompletedActivationCannotReplayInNextActivation()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            var cardId = player.CoveredCharacterCardId;
            player.Resources.GoldVoucher = 24;
            var handler = new UseCharacterCardCommandHandler();
            var firstUse = new GameCommand
            {
                CommandId = "tin-man-use-first",
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1,
                TargetId = cardId
            };
            firstUse.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            firstUse.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] =
                CharacterEffectModes.Strategy;
            Assert.That(handler.Handle(state, firstUse).Succeeded, Is.True);

            var oldPurchase = new GameCommand
            {
                CommandId = "tin-man-purchase-first",
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            };
            oldPurchase.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] =
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium;
            oldPurchase.Parameters[CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId] =
                firstUse.CommandId;
            Assert.That(handler.Handle(state, oldPurchase).Succeeded, Is.True);

            var finishFirstActivation = new GameCommand
            {
                Kind = GameCommandKind.ResolvePendingChoice,
                PlayerId = 1
            };
            finishFirstActivation.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] =
                CharacterEffectChoiceIds.TinManFinishPurchasing;
            finishFirstActivation.Parameters[CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId] =
                firstUse.CommandId;
            Assert.That(handler.Handle(state, finishFirstActivation).Succeeded, Is.True);
            Assert.That(state.PendingCharacterEffect, Is.Null);

            player.UsedCharacterThisRound = false;
            player.DiscardCardIds.Remove(cardId);
            player.CoveredCharacterCardId = cardId;
            player.CoveredCharacterCardIds.Add(cardId);
            var secondUse = new GameCommand
            {
                CommandId = "tin-man-use-second",
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1,
                TargetId = cardId
            };
            secondUse.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            secondUse.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] =
                CharacterEffectModes.Strategy;
            Assert.That(handler.Handle(state, secondUse).Succeeded, Is.True);
            Assert.That(state.PendingCharacterEffect.SourceCommandId, Is.EqualTo(secondUse.CommandId));

            var goldBeforeReplay = player.Resources.GoldVoucher;
            var pureOriginiumBeforeReplay = player.Resources.PureOriginium;
            var replay = handler.Handle(state, oldPurchase);

            Assert.That(replay.Succeeded, Is.False);
            Assert.That(replay.Validation.Reason, Does.Contain("已经结束"));
            Assert.That(player.Score, Is.EqualTo(2));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(goldBeforeReplay));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(pureOriginiumBeforeReplay));
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManFirstPurchase));
            Assert.That(state.PendingCharacterEffect.SourceCommandId, Is.EqualTo(secondUse.CommandId));
        }

        [Test]
        public void LiskarmStrategy_PlacesTwoThenCleanupWaitsForRemovalAndAdvancesAfterResolve()
        {
            var state = CreateActionState(CharacterCardDatabase.Liskarm);
            var player = state.FindPlayer(1);
            var firstSlot = InfluenceService.GetRouteSlotId("A1", 0);
            var secondSlot = InfluenceService.GetRouteSlotId("B1", 0);

            var use = Use(state, CharacterEffectModes.Strategy, command =>
            {
                command.Parameters[CharacterEffectParameterKeys.PlacementSlotId1] = firstSlot;
                command.Parameters[CharacterEffectParameterKeys.PlacementSlotId2] = secondSlot;
            });

            Assert.That(use.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(2));
            Assert.That(player.InfluenceSupply, Is.EqualTo(28));
            Assert.That(state.DelayedCharacterEffects, Has.Count.EqualTo(1));

            state.Phase = GamePhase.Cleanup;
            state.MaxRounds = 8;
            var endActionHandler = new EndActionCommandHandler();
            var firstCleanup = endActionHandler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });
            Assert.That(firstCleanup.Succeeded, Is.True, "创建收尾待选属于成功写入状态，必须能够被权威命令广播。");
            Assert.That(state.Phase, Is.EqualTo(GamePhase.Cleanup));
            Assert.That(state.PendingCharacterEffect.ChoiceType, Is.EqualTo(CharacterPendingChoiceTypes.LiskarmCleanupRemoval));

            var resolve = ResolveRemoval(state, secondSlot);
            Assert.That(resolve.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(player.InfluenceSupply, Is.EqualTo(29));
            Assert.That(state.DelayedCharacterEffects, Is.Empty);

            var secondCleanup = endActionHandler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.EndAction,
                PlayerId = 1
            });
            Assert.That(secondCleanup.Succeeded, Is.True);
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
        }

        [Test]
        public void LiskarmTactic_OpponentCityRemovesTargetWithoutPlacingReplacement()
        {
            var state = CreateActionState(CharacterCardDatabase.Liskarm);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 3;
            var opponent = new PlayerState { PlayerId = 2, Color = PlayerColor.Blue, CityLocationId = "A-01", InfluenceSupply = 29 };
            state.Players.Add(opponent);
            state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "A-01", ResourceType = ResourceType.Iron });
            var targetSlot = InfluenceService.GetLocationSlotId("A-01", 0);
            state.Map.Influences.Add(InfluenceAt(2, targetSlot, "A-01", string.Empty));

            var result = Use(state, CharacterEffectModes.Tactic, command =>
                command.Parameters[CharacterEffectParameterKeys.TargetInfluenceSlotId] = targetSlot);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(player.InfluenceSupply, Is.EqualTo(30));
            Assert.That(opponent.InfluenceSupply, Is.EqualTo(30));
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
        }

        [Test]
        public void ElysiumTactic_PaysOriginiumShardAndPerformsFreeRaidToAdjacentOwnedInfluence()
        {
            var state = CreateActionState(CharacterCardDatabase.Elysium);
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            player.Resources.GoldVoucher = 7;
            player.Resources.OriginiumShard = 3;
            state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "A-02", ResourceType = ResourceType.Iron });
            var ownedSlot = InfluenceService.GetLocationSlotId("A-02", 0);
            state.Map.Influences.Add(InfluenceAt(1, ownedSlot, "A-02", string.Empty));

            var result = Use(state, CharacterEffectModes.Tactic, command =>
                command.Parameters[CharacterEffectParameterKeys.TargetLocationId] = "A-02");

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.CityLocationId, Is.EqualTo("A-02"));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
        }

        [Test]
        public void ElysiumTactic_OccupiedByOpponentCityFailsWithoutPayingOrMoving()
        {
            var state = CreateActionState(CharacterCardDatabase.Elysium);
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            player.Resources.GoldVoucher = 7;
            player.Resources.OriginiumShard = 3;
            state.Players.Add(new PlayerState { PlayerId = 2, Color = PlayerColor.Blue, CityLocationId = "A-02" });
            state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "A-02", ResourceType = ResourceType.Iron });
            state.Map.Influences.Add(InfluenceAt(1, InfluenceService.GetLocationSlotId("A-02", 0), "A-02", string.Empty));

            var result = Use(state, CharacterEffectModes.Tactic, command =>
                command.Parameters[CharacterEffectParameterKeys.TargetLocationId] = "A-02");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.CityLocationId, Is.EqualTo("A-01"));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(3));
            Assert.That(player.CoveredCharacterCardId, Is.Not.Empty);
        }

        [Test]
        public void ElysiumTactic_InsufficientOriginiumShardFailsWithoutPayingGoldOrMoving()
        {
            var state = CreateActionState(CharacterCardDatabase.Elysium);
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            player.Resources.GoldVoucher = 9;
            player.Resources.OriginiumShard = 2;
            state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "A-02", ResourceType = ResourceType.Iron });
            state.Map.Influences.Add(InfluenceAt(1, InfluenceService.GetLocationSlotId("A-02", 0), "A-02", string.Empty));

            var result = Use(state, CharacterEffectModes.Tactic, command =>
                command.Parameters[CharacterEffectParameterKeys.TargetLocationId] = "A-02");

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.CityLocationId, Is.EqualTo("A-01"));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(9));
            Assert.That(player.CoveredCharacterCardId, Is.Not.Empty);
        }

        [Test]
        public void Elysium_StartPlayerCanResolveBothEffectsInDeclaredOrder()
        {
            var state = CreateActionState(CharacterCardDatabase.Elysium);
            var player = state.FindPlayer(1);
            player.CityLocationId = "A-01";
            player.Resources.GoldVoucher = 7;
            player.Resources.Originium = 0;
            player.Resources.OriginiumShard = 3;
            player.Resources.Iron = 2;
            state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = "A-02", ResourceType = ResourceType.Iron });
            state.Map.Influences.Add(InfluenceAt(1, InfluenceService.GetLocationSlotId("A-02", 0), "A-02", string.Empty));

            var result = Use(state, CharacterEffectModes.Both, command =>
            {
                command.Parameters[UseCharacterCardCommandHandler.EffectOrderParameter] = CharacterEffectOrders.StrategyFirst;
                command.Parameters[CharacterEffectParameterKeys.ResourceType] = "originium";
                command.Parameters[CharacterEffectParameterKeys.TargetLocationId] = "A-02";
            });

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.Resources.Originium, Is.EqualTo(4));
            Assert.That(player.Resources.OriginiumShard, Is.Zero);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(player.CityLocationId, Is.EqualTo("A-02"));
            Assert.That(player.UsedCharacterThisRound, Is.True);
        }

        [Test]
        public void TexasTactic_RemovesInfluenceThenMovesTwoExistingInfluences()
        {
            var state = CreateActionState(CharacterCardDatabase.Texas);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 3;
            player.InfluenceSupply = 28;
            var opponent = new PlayerState { PlayerId = 2, Color = PlayerColor.Blue, InfluenceSupply = 29 };
            state.Players.Add(opponent);
            var removalSlot = InfluenceService.GetRouteSlotId("A1", 0);
            var sources = new[]
            {
                InfluenceService.GetRouteSlotId("B1", 0),
                InfluenceService.GetRouteSlotId("C1", 0)
            };
            var targets = new[]
            {
                InfluenceService.GetRouteSlotId("A2", 0),
                InfluenceService.GetRouteSlotId("B2", 0)
            };
            state.Map.Influences.Add(InfluenceAt(2, removalSlot, string.Empty, "A1"));
            state.Map.Influences.Add(InfluenceAt(1, sources[0], string.Empty, "B1"));
            state.Map.Influences.Add(InfluenceAt(1, sources[1], string.Empty, "C1"));

            var result = Use(state, CharacterEffectModes.Tactic, command => ConfigureTexasTactic(command, removalSlot, sources, targets));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(state.Map.Influences.ConvertAll(item => item.SlotId), Is.EquivalentTo(targets));
            Assert.That(player.InfluenceSupply, Is.EqualTo(28));
            Assert.That(opponent.InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void TexasTactic_InvalidSecondMoveRollsBackRemovalFirstMoveAndPayment()
        {
            var state = CreateActionState(CharacterCardDatabase.Texas);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 3;
            player.Resources.Originium = 4;
            player.InfluenceSupply = 28;
            state.Decks.FacilitySupply.Add("f1");
            var removalSlot = InfluenceService.GetRouteSlotId("A1", 0);
            var sources = new[]
            {
                InfluenceService.GetRouteSlotId("B1", 0),
                InfluenceService.GetRouteSlotId("C1", 0)
            };
            var targets = new[]
            {
                InfluenceService.GetRouteSlotId("A2", 0),
                "route:missing:0"
            };
            state.Map.Influences.Add(InfluenceAt(2, removalSlot, string.Empty, "A1"));
            state.Map.Influences.Add(InfluenceAt(1, sources[0], string.Empty, "B1"));
            state.Map.Influences.Add(InfluenceAt(1, sources[1], string.Empty, "C1"));

            var result = Use(state, CharacterEffectModes.Tactic, command => ConfigureTexasTactic(command, removalSlot, sources, targets));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(player.Resources.Originium, Is.EqualTo(4));
            Assert.That(player.InfluenceSupply, Is.EqualTo(28));
            Assert.That(state.Map.Influences.ConvertAll(item => item.SlotId), Is.EqualTo(new[] { removalSlot, sources[0], sources[1] }));
            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(new[] { "f1" }));
            Assert.That(player.CoveredCharacterCardId, Is.Not.Empty);
        }

        [Test]
        public void TexasTactic_InvalidRemovalTargetAndLegacyPlacementParameterDoNotMutateState()
        {
            var state = CreateActionState(CharacterCardDatabase.Texas);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 3;
            player.InfluenceSupply = 28;
            var sources = new[]
            {
                InfluenceService.GetRouteSlotId("B1", 0),
                InfluenceService.GetRouteSlotId("C1", 0)
            };
            var targets = new[]
            {
                InfluenceService.GetRouteSlotId("B2", 0),
                InfluenceService.GetRouteSlotId("C2", 0)
            };
            state.Map.Influences.Add(InfluenceAt(1, sources[0], string.Empty, "B1"));
            state.Map.Influences.Add(InfluenceAt(1, sources[1], string.Empty, "C1"));

            var result = Use(state, CharacterEffectModes.Tactic, command =>
            {
                command.Parameters["placementSlotId"] = InfluenceService.GetRouteSlotId("A1", 0);
                command.Parameters[CharacterEffectParameterKeys.MoveSourceSlotId1] = sources[0];
                command.Parameters[CharacterEffectParameterKeys.MoveTargetSlotId1] = targets[0];
                command.Parameters[CharacterEffectParameterKeys.MoveSourceSlotId2] = sources[1];
                command.Parameters[CharacterEffectParameterKeys.MoveTargetSlotId2] = targets[1];
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(3));
            Assert.That(player.InfluenceSupply, Is.EqualTo(28));
            Assert.That(state.Map.Influences.ConvertAll(item => item.SlotId), Is.EqualTo(sources));
            Assert.That(player.CoveredCharacterCardId, Is.Not.Empty);
        }

        [Test]
        public void TinManBoth_StrategyFirstUsesPurchaseStepsThenResolvesRecall()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 12;
            player.DiscardCardIds.Add("discard-a");

            var use = Use(state, CharacterEffectModes.Both, command =>
            {
                command.Parameters[UseCharacterCardCommandHandler.EffectOrderParameter] = CharacterEffectOrders.StrategyFirst;
                ConfigureLegacyTinManPurchases(command);
            });

            Assert.That(use.Succeeded, Is.True);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManFirstPurchase));
            Assert.That(state.PendingCharacterEffect.ResolveTinManTacticAfterStrategy, Is.True);

            var firstPurchase = Resolve(
                state,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);

            Assert.That(firstPurchase.Succeeded, Is.True);
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManSecondPurchase));
            Assert.That(state.PendingCharacterEffect.ResolveTinManTacticAfterStrategy, Is.True);

            var finishPurchasing = Resolve(
                state,
                CharacterEffectChoiceIds.TinManFinishPurchasing);

            Assert.That(finishPurchasing.Succeeded, Is.True);
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManDiscard));

            var recall = Resolve(state, CharacterEffectChoiceIds.GainGold);

            Assert.That(recall.Succeeded, Is.True);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(player.HandCardIds, Does.Contain("discard-a"));
            Assert.That(player.UsedCharacterThisRound, Is.True);
            Assert.That(state.HasPendingChoice(), Is.False);
        }

        [Test]
        public void TinManBoth_TacticFirstRecallGoldCanFundFollowingPurchase()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 7;
            player.DiscardCardIds.Add("discard-a");

            var use = Use(state, CharacterEffectModes.Both, command =>
            {
                command.Parameters[UseCharacterCardCommandHandler.EffectOrderParameter] = CharacterEffectOrders.TacticFirst;
                ConfigureLegacyTinManPurchases(command);
            });

            Assert.That(use.Succeeded, Is.True);
            Assert.That(player.Score, Is.Zero);
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManDiscard));

            var recall = Resolve(state, CharacterEffectChoiceIds.GainGold);

            Assert.That(recall.Succeeded, Is.True);
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(12));
            Assert.That(player.HandCardIds, Does.Contain("discard-a"));
            Assert.That(player.UsedCharacterThisRound, Is.False);
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManFirstPurchase));

            var firstPurchase = Resolve(
                state,
                CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);

            Assert.That(firstPurchase.Succeeded, Is.True);
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(1));
            Assert.That(player.Resources.GoldVoucher, Is.Zero);
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManSecondPurchase));

            var finishPurchasing = Resolve(
                state,
                CharacterEffectChoiceIds.TinManFinishPurchasing);

            Assert.That(finishPurchasing.Succeeded, Is.True);
            Assert.That(player.UsedCharacterThisRound, Is.True);
            Assert.That(state.HasPendingChoice(), Is.False);
        }

        [Test]
        public void TinManBoth_TacticFirstMoveRemainsLegalAndFollowingPurchaseCanBeCanceled()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.Resources.GoldVoucher = 7;
            player.DiscardCardIds.Add("discard-a");
            var source = InfluenceService.GetRouteSlotId("A1", 0);
            var target = InfluenceService.GetRouteSlotId("A2", 0);
            state.Map.Influences.Add(InfluenceAt(1, source, string.Empty, "A1"));
            var use = Use(state, CharacterEffectModes.Both, command =>
            {
                command.Parameters[UseCharacterCardCommandHandler.EffectOrderParameter] = CharacterEffectOrders.TacticFirst;
                ConfigureLegacyTinManPurchases(command);
            });
            Assert.That(use.Succeeded, Is.True);

            var resolveCommand = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            resolveCommand.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] = CharacterEffectChoiceIds.MoveInfluence;
            resolveCommand.Parameters[UseCharacterCardCommandHandler.SourceInfluenceSlotIdParameter] = source;
            resolveCommand.Parameters[UseCharacterCardCommandHandler.TargetInfluenceSlotIdParameter] = target;
            var resolve = new UseCharacterCardCommandHandler().Handle(state, resolveCommand);

            Assert.That(resolve.Succeeded, Is.True);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(player.Score, Is.EqualTo(1));
            Assert.That(player.Resources.PureOriginium, Is.Zero);
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(target));
            Assert.That(player.HandCardIds, Does.Contain("discard-a"));
            Assert.That(state.PendingCharacterEffect.ChoiceType,
                Is.EqualTo(CharacterPendingChoiceTypes.TinManFirstPurchase));
            Assert.That(state.PendingCharacterEffect.OptionIds,
                Does.Not.Contain(CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium));

            var finishPurchasing = Resolve(
                state,
                CharacterEffectChoiceIds.TinManFinishPurchasing);

            Assert.That(finishPurchasing.Succeeded, Is.True);
            Assert.That(player.UsedCharacterThisRound, Is.True);
            Assert.That(state.HasPendingChoice(), Is.False);
        }

        [Test]
        public void TinManMoveChoice_UsesInfluenceRulesAndKeepsSessionLockedUntilResolved()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.DiscardCardIds.Add("discard-a");
            var source = InfluenceService.GetRouteSlotId("A1", 0);
            var target = InfluenceService.GetRouteSlotId("A2", 0);
            state.Map.Influences.Add(InfluenceAt(1, source, string.Empty, "A1"));
            Use(state, CharacterEffectModes.Tactic, null);

            var command = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            command.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] = CharacterEffectChoiceIds.MoveInfluence;
            command.Parameters[UseCharacterCardCommandHandler.SourceInfluenceSlotIdParameter] = source;
            command.Parameters[UseCharacterCardCommandHandler.TargetInfluenceSlotIdParameter] = target;
            var result = new UseCharacterCardCommandHandler().Handle(state, command);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(target));
            Assert.That(player.HandCardIds, Does.Contain("discard-a"));
            Assert.That(state.HasPendingChoice(), Is.False);
        }

        [Test]
        public void PendingCharacterSettlement_AppendsOnlyOneFinalBroadcastLog()
        {
            var state = CreateActionState(CharacterCardDatabase.TinMan);
            var player = state.FindPlayer(1);
            player.DiscardCardIds.Add("discard-a");
            var cardId = player.CoveredCharacterCardId;
            var session = new GameSession(state);
            session.RegisterHandler(new UseCharacterCardCommandHandler());
            var useCommand = new GameCommand
            {
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1,
                TargetId = cardId
            };
            useCommand.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            useCommand.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = CharacterEffectModes.Tactic;

            var use = session.Submit(useCommand);

            Assert.That(use.Succeeded, Is.True);
            Assert.That(use.LogMessage, Is.Empty);
            Assert.That(state.Logs, Is.Empty, "待选尚未完成时不应提前广播结算。");

            var resolveCommand = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            resolveCommand.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] = CharacterEffectChoiceIds.GainGold;
            var resolve = session.Submit(resolveCommand);

            Assert.That(resolve.Succeeded, Is.True);
            Assert.That(state.Logs, Has.Count.EqualTo(1));
            Assert.That(state.Logs[0].Message, Does.Contain("锡人").And.Contain("完成全部结算"));
        }

        private static GameState CreateActionState(string templateId)
        {
            var player = new PlayerState { PlayerId = 1, Color = PlayerColor.Red };
            var cardId = "character.red.p1." + templateId;
            player.CoveredCharacterCardId = cardId;
            player.CoveredCharacterCardIds.Add(cardId);
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players = { player }
            };
        }

        private static CommandResult Use(GameState state, string mode, System.Action<GameCommand> configure)
        {
            var cardId = state.FindPlayer(1).CoveredCharacterCardId;
            var command = new GameCommand
            {
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = 1,
                TargetId = cardId
            };
            command.Parameters[UseCharacterCardCommandHandler.CardIdParameter] = cardId;
            command.Parameters[UseCharacterCardCommandHandler.EffectModeParameter] = mode;
            configure?.Invoke(command);
            return new UseCharacterCardCommandHandler().Handle(state, command);
        }

        private static CommandResult Resolve(GameState state, string choice)
        {
            var command = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            command.Parameters[UseCharacterCardCommandHandler.ChoiceParameter] = choice;
            if (state.PendingCharacterEffect != null &&
                !string.IsNullOrEmpty(state.PendingCharacterEffect.SourceCommandId))
            {
                command.Parameters[CharacterEffectParameterKeys.PendingCharacterEffectSourceCommandId] =
                    state.PendingCharacterEffect.SourceCommandId;
            }

            return new UseCharacterCardCommandHandler().Handle(state, command);
        }

        private static CommandResult ResolveRemoval(GameState state, string targetSlotId)
        {
            var command = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = 1 };
            command.Parameters[UseCharacterCardCommandHandler.TargetInfluenceSlotIdParameter] = targetSlotId;
            return new UseCharacterCardCommandHandler().Handle(state, command);
        }

        private static InfluencePlacement InfluenceAt(int playerId, string slotId, string locationId, string routeId)
        {
            return new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = slotId,
                LocationId = locationId,
                RouteId = routeId
            };
        }

        private static void ConfigureTexasTactic(GameCommand command, string removalSlot, string[] sources, string[] targets)
        {
            command.Parameters[CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId] = removalSlot;
            command.Parameters[CharacterEffectParameterKeys.MoveSourceSlotId1] = sources[0];
            command.Parameters[CharacterEffectParameterKeys.MoveTargetSlotId1] = targets[0];
            command.Parameters[CharacterEffectParameterKeys.MoveSourceSlotId2] = sources[1];
            command.Parameters[CharacterEffectParameterKeys.MoveTargetSlotId2] = targets[1];
        }

        private static void ConfigureLegacyTinManPurchases(GameCommand command)
        {
            command.Parameters[CharacterEffectParameterKeys.TinManPurchasePureOriginium12] = "true";
            command.Parameters[CharacterEffectParameterKeys.TinManPurchasePureOriginium15] = "true";
        }

        private static string CardId(GameState state, string templateId)
        {
            return "character." + state.FindPlayer(1).Color.ToString().ToLowerInvariant() + ".p1." + templateId;
        }
    }
}
