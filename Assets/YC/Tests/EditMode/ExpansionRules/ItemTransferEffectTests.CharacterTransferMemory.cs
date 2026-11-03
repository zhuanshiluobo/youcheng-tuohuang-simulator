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
        public void RetiredCharacterTransferParentCannotContinueItsRealRequiredResponse(bool accepted)
        {
            var state = State(); var registry = Registry(); string card = Ids(state, "hand", 1)[0];
            registry.Register("api20.required_response", c => c.Node.FlowStage == ""
                ? EffectStepResult.Continue("answered").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = "api20.required_response", AnsweringPlayerId = 1, Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "confirm" } })
                : EffectStepResult.Completed().AddChild(ResourceEffectSpecFactory.GainScore(1, 5)), EffectExecutorKind.IntrinsicFlow);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api20.required_response", "character.transferred", "*", "api20.required_response",
                c => new[] { EffectSpec.Create("api20.required_response", playerId: 1) }, role: EffectHandlerRole.Observer));
            string root = Run(state, CharacterMove(state, card, Zone(state, "hand", 2).ZoneId, false), registry).EffectId;
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            if (accepted) Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("confirm"), out string beforeReason), Is.True, beforeReason);
            // 只损坏隔离副本，定位旧包装已经挂接合法响应时的内存拒绝；不改真实存档。
            engine.GetNode(root).EffectTypeId = ContentPrimitiveEffectExecutor.RetiredCharacterTransferTypeId;
            string before = JsonConvert.SerializeObject(state);
            if (!accepted)
            {
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                    NormalizedValue.CreateString("confirm"), out string reason), Is.False);
                Assert.That(reason, Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredCharacterTransferReason));
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            }
            else
            {
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
                Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain(ContentPrimitiveEffectExecutor.RetiredCharacterTransferReason));
                Assert.That(state.FindPlayer(1).Score, Is.Zero);
            }
            Assert.That(state.FindPlayer(1).Score, Is.Zero); Assert.That(Ids(state, "hand", 2), Does.Contain(card));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "character.transferred"), Is.EqualTo(1));
        }
    }
}
