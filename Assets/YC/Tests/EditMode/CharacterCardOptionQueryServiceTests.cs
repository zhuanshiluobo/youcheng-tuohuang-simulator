using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class CharacterCardOptionQueryServiceTests
    {
        [Test]
        public void LiskarmPlacement_ReturnsLegalDistinctSlotsWithoutChangingState()
        {
            var state = CreateState();
            var service = new CharacterCardOptionQueryService();
            var first = service.Query(state, 1, CharacterCardEffectKind.LiskarmSecurityProtocol);
            var firstSlot = first.Get(CharacterEffectParameterKeys.PlacementSlotId1)[0].Id;

            var selected = new Dictionary<string, string> { [CharacterEffectParameterKeys.PlacementSlotId1] = firstSlot };
            var second = service.Query(state, 1, CharacterCardEffectKind.LiskarmSecurityProtocol, selected);

            Assert.That(second.Get(CharacterEffectParameterKeys.PlacementSlotId2), Has.None.Matches<CharacterCardOption>(item => item.Id == firstSlot));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void ElysiumLogistics_ReturnsOnlyTiedMinimumBaseResources()
        {
            var state = CreateState();
            state.FindPlayer(1).Resources.Originium = 1;
            state.FindPlayer(1).Resources.OriginiumShard = 1;
            state.FindPlayer(1).Resources.Iron = 3;

            var options = new CharacterCardOptionQueryService()
                .Query(state, 1, CharacterCardEffectKind.ElysiumLogistics)
                .Get(CharacterEffectParameterKeys.ResourceType);

            Assert.That(options, Has.Count.EqualTo(2));
            Assert.That(options, Has.Some.Matches<CharacterCardOption>(item => item.Id == "originium"));
            Assert.That(options, Has.Some.Matches<CharacterCardOption>(item => item.Id == "originium-shard"));
            Assert.That(options, Has.None.Matches<CharacterCardOption>(item => item.Id == "iron"));
        }

        [TestCase(false, false, 0, 0)]
        [TestCase(true, false, 12, 1)]
        [TestCase(true, true, 27, 2)]
        public void TinManStrategySummary_ReflectsSelectedOptionalPurchases(
            bool purchase12,
            bool purchase15,
            int expectedCost,
            int expectedPureOriginium)
        {
            var selected = new Dictionary<string, string>
            {
                [CharacterEffectParameterKeys.TinManPurchasePureOriginium12] = purchase12 ? "true" : "false",
                [CharacterEffectParameterKeys.TinManPurchasePureOriginium15] = purchase15 ? "true" : "false"
            };

            var result = new CharacterCardOptionQueryService()
                .Query(CreateState(), 1, CharacterCardEffectKind.TinManEstablishPrestige, selected);

            Assert.That(result.GoldVoucherCost, Is.EqualTo(expectedCost));
            Assert.That(result.ScoreGain, Is.EqualTo(1));
            Assert.That(result.PureOriginiumGain, Is.EqualTo(expectedPureOriginium));
            Assert.That(result.SummaryText, Does.Contain(expectedCost + " 金券"));
            Assert.That(result.SummaryText, Does.Contain(expectedPureOriginium + " 个至纯源石"));
        }

        [Test]
        public void TexasTactic_CandidatesProjectRemovalAndFirstMove()
        {
            var state = CreateState();
            var removalSlot = InfluenceService.GetRouteSlotId("A1", 0);
            var source1 = InfluenceService.GetRouteSlotId("B1", 0);
            var source2 = InfluenceService.GetRouteSlotId("C1", 0);
            var target1 = InfluenceService.GetRouteSlotId("B2", 0);
            state.Map.Influences.Add(InfluenceAt(removalSlot, "A1"));
            state.Map.Influences.Add(InfluenceAt(source1, "B1"));
            state.Map.Influences.Add(InfluenceAt(source2, "C1"));
            var service = new CharacterCardOptionQueryService();
            var afterRemoval = service.Query(
                state,
                1,
                CharacterCardEffectKind.TexasRemoveAndDoubleMove,
                new Dictionary<string, string> { [CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId] = removalSlot });
            Assert.That(afterRemoval.Get(CharacterEffectParameterKeys.MoveSourceSlotId1),
                Has.None.Matches<CharacterCardOption>(item => item.Id == removalSlot));

            var selected = new Dictionary<string, string>
            {
                [CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId] = removalSlot,
                [CharacterEffectParameterKeys.MoveSourceSlotId1] = source1,
                [CharacterEffectParameterKeys.MoveTargetSlotId1] = target1
            };

            var options = service
                .Query(state, 1, CharacterCardEffectKind.TexasRemoveAndDoubleMove, selected)
                .Get(CharacterEffectParameterKeys.MoveSourceSlotId2);

            Assert.That(options, Has.Some.Matches<CharacterCardOption>(item => item.Id == source2));
            Assert.That(options, Has.None.Matches<CharacterCardOption>(item => item.Id == target1));
            Assert.That(state.Map.Influences, Has.Count.EqualTo(3));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(removalSlot));
        }

        [Test]
        public void PendingQueries_ExposeTinManChoicesAndLiskarmAuthoritativeOptions()
        {
            var state = CreateState();
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                PlayerId = 1,
                CardId = "character.red.p1.tin-man",
                ChoiceType = CharacterPendingChoiceTypes.TinManDiscard,
                RemainingCardIds = new List<string> { "discard-a" },
                OptionIds = new List<string> { CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence }
            };
            var service = new CharacterCardOptionQueryService();
            Assert.That(service.QueryPending(state, 1).Get(CharacterEffectParameterKeys.Choice), Has.Count.EqualTo(2));

            var slot = InfluenceService.GetRouteSlotId("A1", 0);
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                PlayerId = 1,
                CardId = "character.red.p1.liskarm",
                ChoiceType = CharacterPendingChoiceTypes.LiskarmCleanupRemoval,
                OptionIds = new List<string> { slot }
            };
            var removal = service.QueryPending(state, 1).Get(CharacterEffectParameterKeys.TargetInfluenceSlotId);
            Assert.That(removal, Has.Count.EqualTo(1));
            Assert.That(removal[0].Id, Is.EqualTo(slot));
        }

        [Test]
        public void TinManPendingQuery_HidesMoveWhenCommittedPurchaseRequiresThisCardsGold()
        {
            var state = CreateState();
            state.FindPlayer(1).Resources.GoldVoucher = 7;
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                PlayerId = 1,
                CardId = "character.red.p1.tin-man",
                ChoiceType = CharacterPendingChoiceTypes.TinManDiscard,
                RemainingCardIds = new List<string> { "discard-a" },
                OptionIds = new List<string> { CharacterEffectChoiceIds.GainGold, CharacterEffectChoiceIds.MoveInfluence },
                ResolveTinManStrategyAfterRecall = true,
                TinManPurchasePureOriginium12 = true
            };

            var options = new CharacterCardOptionQueryService()
                .QueryPending(state, 1)
                .Get(CharacterEffectParameterKeys.Choice);

            Assert.That(options, Has.Count.EqualTo(1));
            Assert.That(options[0].Id, Is.EqualTo(CharacterEffectChoiceIds.GainGold));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(7));
            Assert.That(state.PendingCharacterEffect.RemainingCardIds, Is.EqualTo(new[] { "discard-a" }));
        }

        private static GameState CreateState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                StartPlayerId = 1,
                Players = { new PlayerState { PlayerId = 1, InfluenceSupply = 30 } }
            };
        }

        private static InfluencePlacement InfluenceAt(string slotId, string routeId)
        {
            return new InfluencePlacement { PlayerId = 1, SlotId = slotId, RouteId = routeId };
        }
    }
}
