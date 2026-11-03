using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        [Test]
        public void CeobeUnexecutedDispatchDoesNotEnterSuccessfulRecipientContinuation()
        {
            var state = State(); diceFace = 5; StartCard(state, "ceobe", "tactic");
            Answer(state, "dispatch");
            var action = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction);
            Assert.That(YC.Domain.Effects.ItemTransferEffectExecutor.Text(action.NormalizedResult, "outcome"), Is.EqualTo("no_legal_target"));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.PromptKey == "core:interaction.ceobe.transfer.choose"), Is.False);
            Assert.That(Card(state, "ceobe").ControllerSubject.PlayerId, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Restore(state);
        }
        [TestCase(false)][TestCase(true)]
        public void CeobeStrategyKeepsRealPrepaymentAndNeverGrantsBudget(bool accept)
        {
            var state = State(); DockFixtureCity(state, 1); StartCard(state, "ceobe", "strategy");
            Assert.That(Open(state).CandidateIds, Does.Contain("continue"));
            Answer(state, accept ? "continue" : "decline");
            if (accept)
            {
                Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "deploy", "dispatch", "explore", "move_city", "build" }));
                Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(19));
                state = Restore(state); Answer(state, "deploy");
                var action = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction);
                Assert.That(action.Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
                state = Restore(state); Drain(state);
                Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == action.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            }
            else
            {
                Drain(state); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(20));
                Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction), Is.False);
            }
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions), Is.False);
            state = Restore(state); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(accept ? 19 : 20));
        }
        [TestCase(5)][TestCase(6)]
        public void CeobeDiceRecipientContinuationWaitsForActualImmediateActionAndRestoresOnce(int face)
        {
            diceFace = face; var state = State(); DockFixtureCity(state, 1); StartCard(state, "ceobe", "tactic");
            var action = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction);
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(new[] { "deploy", "dispatch", "explore", "move_city", "build", "special" }));
            state = Restore(state); Answer(state, "deploy");
            Assert.That(Open(state).PromptKey, Is.Not.EqualTo("core:interaction.ceobe.transfer.choose"));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == action.EffectId).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            state = Restore(state); Answer(state, Open(state).CandidateIds.First());
            Assert.That(Open(state).PromptKey, Is.EqualTo("core:interaction.ceobe.transfer.choose"));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == action.EffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            state = Restore(state);
            Assert.That(state.EffectRuntime.InteractionRequests.Count(q => q.Status == "open" && q.PromptKey == "core:interaction.ceobe.transfer.choose"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "EffectCompleted" && e.SourceEffectId == action.EffectId), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions), Is.False);
            // API-18 的边界是原接收者续接；转交语义和入口迁移在 API-19/20 实施。
        }
    }
}
