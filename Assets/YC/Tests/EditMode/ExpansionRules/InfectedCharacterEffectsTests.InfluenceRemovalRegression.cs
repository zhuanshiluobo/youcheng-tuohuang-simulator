using System.Linq;
using NUnit.Framework;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅隔离装配真实卡牌和响应，不能作为正常对局或画面验收。
    public sealed partial class InfectedCharacterEffectsTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void HellagurRemovalResolvesOtherPlayersInfluenceWithDorothyActive(bool store)
        {
            var state = State(3, formalAbilities: true);
            var dorothy = Item(state, "operator.dorothy");
            state.CurrentPlayerId = 3;
            Run(state, MoveSpec(3, new[] { dorothy.ItemId }, dorothy.ZoneId,
                FindZone(state, "hand", 3).ZoneId, "unordered", 1, "core.character_card_transfer"));
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 3));
            Answer(state, dorothy.ItemId);
            Run(state, CharacterLifecycleService.CreateUseSpec(3, dorothy.ItemId, "tactic"));
            Answer(state, "1");
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, dorothy.ItemId, "operator.dorothy.tactic"), Is.True);

            string targetSlot = InfluenceService.GetLocationSlotId("E-03", 0);
            string otherSlot = InfluenceService.GetLocationSlotId("E-03", 1);
            Assert.That(Slots(state), Does.Contain(targetSlot));
            var target = PlaceFixtureInfluence(state, 3, targetSlot);
            PlaceFixtureInfluence(state, 1, otherSlot);
            string originalSupply = target.OriginalSupplyZoneId;

            state.CurrentPlayerId = 2;
            var hellagur = Item(state, "infected.character.hellagur");
            Run(state, MoveSpec(2, new[] { hellagur.ItemId }, hellagur.ZoneId,
                FindZone(state, "hand", 2).ZoneId, "unordered", 1, "core.character_card_transfer"));
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 2));
            Answer(state, hellagur.ItemId);
            Run(state, CharacterLifecycleService.CreateUseSpec(2, hellagur.ItemId, "strategy"));
            Answer(state, "place_place_remove");
            Answer(state, Open(state).CandidateIds.First());
            Answer(state, Open(state).CandidateIds.First());
            Assert.That(Open(state).CandidateIds, Does.Contain(target.ItemId));
            artifacts.WriteLabeledText("赫拉格移除确认前状态", Newtonsoft.Json.JsonConvert.SerializeObject(state));
            Answer(state, target.ItemId);

            var response = Open(state);
            Assert.That(response, Is.Not.Null, "多萝西本人应有接收标记的选择机会。");
            Assert.That(response.AnsweringPlayerId, Is.EqualTo(3));
            if (store) Answer(state, "store");
            else
            {
                var engine = new EffectTreeExecutor(state, registry);
                Assert.That(engine.TrySubmitInteraction(response.GetStableInteractionId(), response.AnsweringPlayerId,
                    response.StateRevision, NormalizedValue.CreateBoolean(false), out string reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            }
            Drain(state);
            var actual = state.ItemZones.Items.Single(i => i.ItemId == target.ItemId);
            Assert.That(actual.SlotId, Is.Not.EqualTo(targetSlot));
            if (store)
                Assert.That(state.ItemZones.Zones.Single(z => z.ZoneId == actual.ZoneId).ParentId, Is.EqualTo(dorothy.ItemId));
            else
                Assert.That(actual.ZoneId, Is.EqualTo(originalSupply));
            Assert.That(state.EffectRuntime.Status, Is.Not.EqualTo(EffectRuntimeStatus.PausedFault));
        }
    }
}
