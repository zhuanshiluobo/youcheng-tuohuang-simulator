using System;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        [TestCase("hand")]
        [TestCase("discard")]
        public void MuelsyseDestinationBindsOneOriginalCardToTwoRealMovesAcrossRecovery(string destination)
        {
            var state = State();
            var original = state.ItemZones.Items.Where(i => i.TypeId == "core.generated_character_card").Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var source = FindZone(state, "flowing_shapes").ZoneId;
            StartCard(state, "muelsyse", "tactic");
            for (int index = 0; index < original.Length; index++)
            {
                var request = Open(state);
                Assert.That(request.InteractionTypeId, Is.EqualTo("character.card.destination"));
                Assert.That(request.CandidateIds, Is.EqualTo(new[] { "hand", "discard" }));
                Assert.That(request.MinSelections, Is.EqualTo(1)); Assert.That(request.MaxSelections, Is.EqualTo(1));
                var subject = request.Presentation.Prompt.Arguments.Single(a => a.Name == "card" && a.Kind == "item");
                Assert.That(subject.Value.ReferenceId, Is.EqualTo(original[index]));
                var projected = InteractionRequestProjector.ProjectForPlayer(request, 1, View(state), state.GameId);
                Assert.That(projected.Presentation.Prompt.Arguments.Single(a => a.Name == "card").Value.ReferenceId,
                    Is.EqualTo(original[index]), "正式观察者投影必须可见当前待处理流形及其提示引用。");
                Assert.That(state.ItemZones.Items.Single(i => i.ItemId == original[index]).ZoneId, Is.EqualTo(source), "选择前不移动当前流形。");
                var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
                Assert.That(owner.NestedGroupSizes, Is.EqualTo(new[] { 1, 1 }));
                for (int branch = 0; branch < 2; branch++)
                {
                    var move = owner.NestedEffects[branch];
                    Assert.That(move.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.MoveCharacterCard));
                    Assert.That(Text(Value(move.NormalizedArguments, "itemRefs").Items.Single(), "itemId"), Is.EqualTo(original[index]));
                    Assert.That(Text(Value(move.NormalizedArguments, "expectedSource"), "zoneId"), Is.EqualTo(source));
                    Assert.That(Text(Value(move.NormalizedArguments, "destination"), "zoneId"), Is.EqualTo(FindZone(state, branch == 0 ? "hand" : "discard", 1).ZoneId));
                    Assert.That(Text(move.ContinuationContext, "stage"), Is.EqualTo("acquired"));
                }
                state = Restore(state);
                Answer(state, destination);
                Assert.That(state.ItemZones.Items.Single(i => i.ItemId == original[index]).ZoneId, Is.EqualTo(FindZone(state, destination, 1).ZoneId));
                var engine = new EffectTreeExecutor(state, registry);
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                    NormalizedValue.CreateString(destination), out _), Is.False, "原动作不能重复移动或作用到下一张卡。");
                state = Restore(state);
            }
            if (Open(state) != null)
            {
                Assert.That(Open(state).PromptKey, Is.EqualTo("core:interaction.muelsyse.cover"));
                Assert.That(Open(state).MinSelections, Is.Zero);
            }
            // 本用例止于三张真实获取后的原盖放边界；完整盖放／回收由既有生命周期用例验证。
            Assert.That(state.ItemZones.Items.Where(i => i.TypeId == "core.generated_character_card").Select(i => i.ItemId), Is.EquivalentTo(original));
            Assert.That(state.ItemZones.Items.Count(i => i.TypeId == "core.generated_character_card" && i.ZoneId == FindZone(state, destination, 1).ZoneId), Is.EqualTo(original.Length));
        }
    }
}
