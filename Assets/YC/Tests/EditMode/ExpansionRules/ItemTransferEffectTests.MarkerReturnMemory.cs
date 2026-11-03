using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        [TestCase(false)][TestCase(true)]
        public void RetiredMarkerReturnParentCannotContinueItsRealRequiredResponse(bool accepted)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            registry.Register("api26.required_response", c => c.Node.FlowStage == ""
                ? EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "api26.required_response", AnsweringPlayerId = 1, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "confirm" } })
                : EffectStepResult.Completed().AddChild(ResourceEffectSpecFactory.GainScore(1, 5)), EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api26.required_response", "character.cards.revealed", "*", "api26.required_response",
                c => new[] { EffectSpec.Create("api26.required_response", playerId: 1) }, role: EffectHandlerRole.Observer));
            string root = Run(state, RevealCharacterMove(state, card, Zone(state, "discard", 1).ZoneId, false), registry).EffectId;
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            if (accepted) Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("confirm"), out string beforeReason), Is.True, beforeReason);
            // 只损坏隔离副本，定位旧包装已经挂接合法响应时的内存拒绝；不改真实存档。
            engine.GetNode(root).EffectTypeId = ContentPrimitiveEffectExecutor.RetiredMarkerReturnTypeId;
            string before = JsonConvert.SerializeObject(state);
            if (!accepted)
            {
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                    NormalizedValue.CreateString("confirm"), out string reason), Is.False);
                Assert.That(reason, Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredMarkerReturnReason));
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            }
            else
            {
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
                Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain(ContentPrimitiveEffectExecutor.RetiredMarkerReturnReason));
                Assert.That(state.FindPlayer(1).Score, Is.Zero);
            }
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(Ids(state, "discard", 1), Does.Contain(card));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.cards.revealed"), Is.EqualTo(1));
        }
    }
}
