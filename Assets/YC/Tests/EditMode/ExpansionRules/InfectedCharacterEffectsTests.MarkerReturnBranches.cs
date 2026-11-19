using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        [TestCase("lin", false)] [TestCase("lin", true)]
        [TestCase("swire", false)] [TestCase("swire", true)]
        public void MarkerReturnBindsActualMoveBeforeSelectionAndPreservesCancellationAndContinuation(string key, bool decline)
        {
            var state = State(3, formalAbilities: true);
            LoadInitialMarkers(state, key, 2);
            var stored = StoredMarkers(state, key).OrderBy(i => i.ItemId).ToArray();
            var selected = stored.Last(); int originalOwner = selected.OwnerSubject.PlayerId;
            string selectedId = selected.ItemId, sourceZone = selected.ZoneId;
            int gold = state.FindPlayer(1).Resources.GoldVoucher;
            Cleanup(state); InvokeCleanup(state);
            var request = Open(state);
            Assert.That(request.InteractionTypeId, Is.EqualTo("effect.choice.execute"));
            Assert.That(request.MinSelections, Is.Zero); Assert.That(request.MaxSelections, Is.EqualTo(1));
            Assert.That(request.AllowDecline, Is.True);
            Assert.That(request.CandidateIds, Is.EquivalentTo(key == "swire"
                ? stored.Select(i => "p" + i.OwnerSubject.PlayerId) : stored.Select(i => i.ItemId)));
            var choice = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId);
            Assert.That(choice.ChildEffectIds, Is.Empty);
            Assert.That(choice.NestedGroupSizes, Is.All.EqualTo(1));
            Assert.That(choice.NestedEffects.All(e => e.EffectTypeId == ItemTransferEffectExecutor.TypeId), Is.True);
            Assert.That(StoredMarkers(state, key).Count, Is.EqualTo(2));
            string choiceId = choice.EffectId, requestId = request.GetStableInteractionId();
            state = Restore(state);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(requestId));
            if (decline)
            {
                AnswerBoolean(state, false);
                Assert.That(StoredMarkers(state, key).Count, Is.EqualTo(2));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
                Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == choiceId).ChildEffectIds, Is.Empty);
                Assert.That(Open(state), Is.Null);
            }
            else
            {
                Answer(state, key == "swire" ? "p" + originalOwner : selectedId);
                Assert.That(state.ItemZones.Items.Single(i => i.ItemId == selectedId).ZoneId,
                    Is.EqualTo(FindZone(state, "supply", originalOwner).ZoneId));
                Assert.That(state.ItemZones.Items.Single(i => i.ItemId == stored.First().ItemId).ZoneId, Is.EqualTo(sourceZone));
                Assert.That(Open(state).InteractionTypeId, Is.EqualTo(key == "lin" ? "lua.choice" : "enterprise.upgrade"));
                Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == choiceId).ChildEffectIds.Count, Is.EqualTo(1));
                state = Restore(state); Drain(state);
                Assert.That(StoredMarkers(state, key).Count, Is.EqualTo(1));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + (key == "lin" ? 10 : 6)));
                int nodes = state.EffectRuntime.EffectNodes.Count;
                state = Restore(state);
                Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
                Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodes));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + (key == "lin" ? 10 : 6)));
            }
        }
    }
}
