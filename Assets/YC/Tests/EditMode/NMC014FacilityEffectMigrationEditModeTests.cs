using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class NMC014FacilityEffectMigrationEditModeTests
    {
        [Test]
        public void FacilityCatalog_ContainsVersionedRegistrationForEveryDefinition()
        {
            IList<FacilityContentRegistration> registrations =
                FacilityContentRegistrationCatalog.CreateAll();

            Assert.That(registrations, Has.Count.EqualTo(FacilityCardDatabase.ExpectedDefinitionCount));
            for (int i = 0; i < registrations.Count; i++)
            {
                FacilityContentRegistration registration = registrations[i];
                Assert.That(registration.IsValid(), Is.True, registration.FacilityId);
                Assert.That(registration.DefinitionVersionValue, Is.EqualTo(FacilityContentRegistration.DefinitionVersion));
                Assert.That(registration.RouteKey, Is.EqualTo(registration.EffectId));
            }
        }

        [Test]
        public void LuaFacilityHandler_CompilesDocumentedBuildEffect()
        {
            const string source = @"return function(ctx)
    return { Effect.Build({
        player = ctx.playerId,
        facilityScope = 'facility.test',
        free = true
    }) }
end";
            var definition = new LuaScriptDefinition(
                "content.nmc014.test",
                "ability.nmc014.test",
                "handler.nmc014.test",
                FacilityContentRegistration.DefinitionVersion,
                source,
                LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost(
                new LuaExecutionBudget(100000, 5000, 8, 128, 16, 32768)).Invoke(
                    definition,
                    new LuaInvocationContext(
                        "event.nmc014.test",
                        FacilityEntryEventTypeIds.Activated,
                        "p1",
                        0,
                        FacilityContentRegistration.DefinitionVersion,
                        3,
                        0,
                        0));

            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(invocation.Effects, Has.Count.EqualTo(1));
            Assert.That(invocation.Effects[0].EffectTypeId, Is.EqualTo(FacilityEntryEffectTypeIds.Build));

            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            bool compiled = new LuaEffectSpecCompiler().TryCompile(
                invocation,
                new LuaEffectCompilationContext { PlayerId = 1 },
                out specs,
                out fault);
            Assert.That(compiled, Is.True, fault == null ? string.Empty : fault.Diagnostic);
            Assert.That(specs, Has.Count.EqualTo(1));
            Assert.That(specs[0].NormalizedArguments.ToDeterministicString(), Does.Contain("facility.test"));
        }

        [Test]
        public void LuaFacilityReward_CompilesToGenericResourceEffectWithPlayerReference()
        {
            const string source = @"return function(ctx)
    return { Effect.GainResource({
        recipient = ctx.playerId,
        resourceType = 'gold_voucher',
        amount = 2,
        reasonId = 'facility.entry.reward'
    }) }
end";
            var definition = new LuaScriptDefinition(
                "content.nmc014.reward.test",
                "ability.nmc014.reward.test",
                "handler.nmc014.reward.test",
                FacilityContentRegistration.DefinitionVersion,
                source,
                LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost(
                new LuaExecutionBudget(100000, 5000, 8, 128, 16, 32768)).Invoke(
                    definition,
                    new LuaInvocationContext(
                        "event.nmc014.reward.test",
                        FacilityEntryEventTypeIds.Activated,
                        "p3",
                        0,
                        FacilityContentRegistration.DefinitionVersion,
                        3,
                        0,
                        0));

            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            bool compiled = new LuaEffectSpecCompiler().TryCompile(
                invocation,
                new LuaEffectCompilationContext { PlayerId = 3 },
                out specs,
                out fault);

            Assert.That(compiled, Is.True, fault == null ? string.Empty : fault.Diagnostic);
            Assert.That(specs, Has.Count.EqualTo(1));
            Assert.That(specs[0].EffectTypeId, Is.EqualTo(ResourceEffectTypeIds.Gain));
            Assert.That(specs[0].NormalizedArguments.ToDeterministicString(), Does.Contain("executingPlayer"));
        }

        [Test]
        public void FacilityActivation_CommitsInstanceStateBeforeDispatchingEvent()
        {
            FacilityCardDefinition definition = FindFirstEntryDefinition();
            Assert.That(definition, Is.Not.Null);

            var state = new GameState
            {
                GameId = "nmc014-activation-test",
                Phase = GamePhase.ActionRound1,
                Round = 1,
                CurrentPlayerId = 1,
                MapId = StaticMapDefinitions.ThreePlayerMapId,
                Map = new MapRuntimeState(),
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, Resources = new ResourceSet() }
                }
            };
            var placement = new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = definition.FacilityId,
                CityBoardSlotIndex = 0
            };
            state.Map.Facilities.Add(placement);
            FacilityInstanceStateService.EnsureIdentity(state, placement);

            var registry = new EffectRegistry();
            FacilityEntryEffectExecutor.Register(registry);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                "test:nmc014:observer",
                FacilityEntryEventTypeIds.Activated,
                "*",
                "test.nmc014.observer",
                _ => new List<EffectSpec>(),
                EffectHandlerRole.Observer));

            var executor = new EffectTreeExecutor(state, registry);
            string rootId = executor.CreateRoot(FacilityEntryEffectSpecFactory.Activate(
                1,
                definition.FacilityId,
                placement.ContentInstanceId,
                placement.CityBoardSlotIndex,
                "test.nmc014"));
            EffectRunReport report = executor.RunUntilQuiescent();

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(executor.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            FacilityInstanceRuntimeState instance =
                FacilityInstanceStateService.Find(state, placement.ContentInstanceId);
            Assert.That(instance, Is.Not.Null);
            Assert.That(instance.ActivationCount, Is.EqualTo(1));
            Assert.That(CountEvents(state, FacilityEntryEventTypeIds.Activated), Is.EqualTo(1));
        }

        [Test]
        public void FacilityInstanceProjection_HidesPrivateCountersFromOtherPlayersAndSpectators()
        {
            var state = new GameState
            {
                GameId = "nmc014-visibility-test",
                Map = new MapRuntimeState(),
                Players = new List<PlayerState>()
            };
            var placement = new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = "building_001",
                CityBoardSlotIndex = 0
            };
            state.Map.Facilities.Add(placement);
            FacilityInstanceStateService.EnsureIdentity(state, placement);
            FacilityInstanceStateService.RecordActivation(state, placement.ContentInstanceId, 2);

            FacilityInstanceView owner = GameStateViewProjector.ProjectForPlayer(state, 1).Map.FacilityInstances[0];
            FacilityInstanceView other = GameStateViewProjector.ProjectForPlayer(state, 2).Map.FacilityInstances[0];
            FacilityInstanceView spectator = GameStateViewProjector.ProjectForSpectator(state).Map.FacilityInstances[0];

            Assert.That(owner.PrivateStateVisible, Is.True);
            Assert.That(owner.ActivationCount, Is.EqualTo(1));
            Assert.That(other.PrivateStateVisible, Is.False);
            Assert.That(other.ActivationCount, Is.EqualTo(0));
            Assert.That(spectator.PrivateStateVisible, Is.False);
            Assert.That(spectator.ActivationCount, Is.EqualTo(0));
        }

        private static FacilityCardDefinition FindFirstEntryDefinition()
        {
            for (int i = 0; i < FacilityCardDatabase.DefaultSupplyIds.Count; i++)
            {
                FacilityCardDefinition definition = FacilityCardDatabase.Get(FacilityCardDatabase.DefaultSupplyIds[i]);
                if (definition != null && definition.HasEntryEffect) return definition;
            }

            for (int i = 0; i < FacilityCardDatabase.ReserveIds.Count; i++)
            {
                FacilityCardDefinition definition = FacilityCardDatabase.Get(FacilityCardDatabase.ReserveIds[i]);
                if (definition != null && definition.HasEntryEffect) return definition;
            }

            return null;
        }

        private static int CountEvents(GameState state, string eventType)
        {
            int count = 0;
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.RuleEvents == null) return count;
            for (int i = 0; i < state.EffectRuntime.RuleEvents.Count; i++)
            {
                RuleEvent item = state.EffectRuntime.RuleEvents[i];
                if (item != null && item.EventType == eventType) count++;
            }

            return count;
        }
    }
}
