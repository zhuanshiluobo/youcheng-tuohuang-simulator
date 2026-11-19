using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class FacilityEnterpriseDeclinePolicyTests
    {
        [TestCase(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, "player_declined", true)]
        [TestCase(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, "real_entry_failure", false)]
        [TestCase("fixture.other_response", "player_declined", false)]
        public void FacilityEntryOnlyAcceptsAnEnterpriseSpecialDecline(string responseType, string reason, bool completes)
        {
            var state = new GameState { GameId = "facility.decline.fixture", Round = 1, ContentDefinitions = new GameContentDefinitions() };
            state.Players.Add(new PlayerState { PlayerId = 1 });
            var definition = new FacilityCardDefinition { FacilityId = "fixture.facility", EffectId = "fixture.entry",
                EffectType = FacilityBehaviorFamilies.Enterprise, HasEntryEffect = true };
            state.ContentDefinitions.Facilities.Add(definition);
            var placement = new FacilityPlacement { PlayerId = 1, CityBoardSlotIndex = 0, FacilityCardId = definition.FacilityId };
            state.Map.Facilities.Add(placement);
            FacilityInstanceStateService.EnsureIdentity(state, placement);
            var registry = new EffectRegistry();
            FacilityEntryEffectExecutor.Register(registry);
            registry.Register(responseType, context => EffectStepResult.Failed(reason));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("fixture.entry.subscription", FacilityEntryEventTypeIds.Activated,
                definition.EffectId, "fixture.entry.handler", context => new List<EffectSpec> { EffectSpec.Create(responseType, playerId: 1) }, EffectHandlerRole.Primary));
            var engine = new EffectTreeExecutor(state, registry);
            string rootId = engine.CreateRoot(FacilityEntryEffectSpecFactory.Activate(1, definition.FacilityId, placement.ContentInstanceId, 0));
            var run = engine.RunUntilQuiescent();
            Assert.That(run.Faulted, Is.False, engine.LastDiagnostic);
            var root = state.EffectRuntime.EffectNodes.Single(node => node.EffectId == rootId);
            var response = state.EffectRuntime.EffectNodes.Single(node => node.ParentEffectId == rootId && node.EffectTypeId == responseType);
            Assert.That(response.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(response.FailureReason, Is.EqualTo(reason));
            Assert.That(root.Status, Is.EqualTo(completes ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
            if (!completes) Assert.That(root.FailureReason, Is.EqualTo("facility_entry_response_failed"));
            Assert.That(state.Map.Facilities.Single().ContentInstanceId, Is.EqualTo(placement.ContentInstanceId));
        }
    }
}
