using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class NMC011InfluenceEffectEditModeTests
    {
        [Test]
        public void PlaceInfluence_CommitsIdentityAndEmitsPlacedAfterCommit()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            AddResourceToken(state, "city-a");
            var service = new InfluenceService(mapQuery);
            var registry = CreateRegistry(service);
            var executor = new EffectTreeExecutor(state, registry);
            var slotId = InfluenceService.GetLocationSlotId("city-a", 0);

            var effectId = executor.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(1, slotId, "nmc011.test"));
            var report = executor.RunUntilQuiescent();
            var node = executor.GetNode(effectId);

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences[0].InfluenceId, Is.Not.Empty);
            Assert.That(state.Map.Influences[0].Source.Kind, Is.EqualTo(InfluenceIdentity.PlayerSupplySourceKind));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void MoveInfluence_EmitsOnlyMovedAndKeepsInstanceIdentity()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            AddResourceToken(state, "city-a");
            AddResourceToken(state, "harbor-c");
            var service = new InfluenceService(mapQuery);
            var source = InfluenceService.GetLocationSlotId("city-a", 0);
            var target = InfluenceService.GetLocationSlotId("harbor-c", 0);
            service.Place(state, 1, source);
            var influenceId = state.Map.Influences[0].InfluenceId;
            var executor = new EffectTreeExecutor(state, CreateRegistry(service));

            var effectId = executor.CreateRoot(InfluenceEffectSpecFactory.MoveInfluence(1, influenceId, target));
            var report = executor.RunUntilQuiescent();

            Assert.That(report.Completed, Is.True);
            Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.Influences[0].InfluenceId, Is.EqualTo(influenceId));
            Assert.That(state.Map.Influences[0].SlotId, Is.EqualTo(target));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluenceMoved), Is.EqualTo(1));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(0));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(0));
        }

        [Test]
        public void RemoveInfluence_WhenTargetMissing_FailsWithoutStateOrSuccessEvent()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            var service = new InfluenceService(mapQuery);
            var executor = new EffectTreeExecutor(state, CreateRegistry(service));

            var effectId = executor.CreateRoot(InfluenceEffectSpecFactory.RemoveInfluence(1, "missing_influence"));
            var report = executor.RunUntilQuiescent();
            var node = executor.GetNode(effectId);

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(0));
        }

        [Test]
        public void ReplaceInfluence_WhenTargetMissing_DoesNotNormalizeLegacyState()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            var legacySlotId = InfluenceService.GetLocationSlotId("city-a", 0);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = legacySlotId,
                LocationId = "city-a"
            });
            var service = new InfluenceService(mapQuery);
            var executor = new EffectTreeExecutor(state, CreateRegistry(service));

            var effectId = executor.CreateRoot(InfluenceEffectSpecFactory.ReplaceInfluence(2, "missing_influence"));
            var report = executor.RunUntilQuiescent();
            var node = executor.GetNode(effectId);

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.Map.Influences[0].InfluenceId, Is.Empty);
            Assert.That(state.Map.Influences[0].OwnerSubject.SubjectType, Is.Empty);
            Assert.That(state.Map.Influences[0].Source.Kind, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
        }

        [Test]
        public void ReplaceInfluence_SucceedsInRemovePlaceReplaceOrder()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            AddResourceToken(state, "city-a");
            var service = new InfluenceService(mapQuery);
            var slotId = InfluenceService.GetLocationSlotId("city-a", 0);
            service.Place(state, 1, slotId);
            var targetId = state.Map.Influences[0].InfluenceId;
            var executor = new EffectTreeExecutor(state, CreateRegistry(service));

            var effectId = executor.CreateRoot(InfluenceEffectSpecFactory.ReplaceInfluence(2, targetId));
            var report = executor.RunUntilQuiescent();
            var node = executor.GetNode(effectId);
            var domainEvents = GetDomainEvents(state);

            Assert.That(report.Completed, Is.True);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(GetString(node.NormalizedResult, "outcome"), Is.EqualTo("replaced"));
            Assert.That(domainEvents, Is.EqualTo(new[]
            {
                InfluenceEventTypeIds.InfluenceRemoved,
                InfluenceEventTypeIds.InfluencePlaced,
                InfluenceEventTypeIds.InfluenceReplaced
            }));
            Assert.That(state.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(state.Map.Influences[0].PlayerId, Is.EqualTo(2));
            Assert.That(state.Map.Influences[0].InfluenceId, Is.Not.EqualTo(targetId));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(29));
        }

        [Test]
        public void ReplaceInfluence_WhenPlacementFails_CompletesRemovedOnlyWithoutRollback()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            AddResourceToken(state, "city-a");
            var service = new InfluenceService(mapQuery);
            var slotId = InfluenceService.GetLocationSlotId("city-a", 0);
            service.Place(state, 1, slotId);
            state.FindPlayer(2).InfluenceSupply = 0;
            var targetId = state.Map.Influences[0].InfluenceId;
            var executor = new EffectTreeExecutor(state, CreateRegistry(service));

            var effectId = executor.CreateRoot(InfluenceEffectSpecFactory.ReplaceInfluence(2, targetId));
            var report = executor.RunUntilQuiescent();
            var node = executor.GetNode(effectId);

            Assert.That(report.Completed, Is.True);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(GetString(node.NormalizedResult, "outcome"), Is.EqualTo("removed_only"));
            Assert.That(state.Map.Influences, Is.Empty);
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(30));
            Assert.That(state.FindPlayer(2).InfluenceSupply, Is.EqualTo(0));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluenceRemoved), Is.EqualTo(1));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluencePlaced), Is.EqualTo(0));
            Assert.That(CountEvents(state, InfluenceEventTypeIds.InfluenceReplaced), Is.EqualTo(0));
        }

        [Test]
        public void InfluenceIdentity_ClonesAndRecoversWithStableInstanceId()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            AddResourceToken(state, "city-a");
            var service = new InfluenceService(mapQuery);
            service.Place(state, 1, InfluenceService.GetLocationSlotId("city-a", 0));
            var influenceId = state.Map.Influences[0].InfluenceId;

            var recovered = GameStateCloneService.DeepClone(state);

            Assert.That(recovered.Map.Influences[0].InfluenceId, Is.EqualTo(influenceId));
            Assert.That(recovered.Map.Influences[0].OwnerSubject.SubjectType, Is.EqualTo("player"));
            Assert.That(recovered.Map.Influences[0].Source.Kind, Is.EqualTo(InfluenceIdentity.PlayerSupplySourceKind));
        }

        [Test]
        public void ReplaceInfluence_ResumesFromRemoveCommitCheckpoint()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var state = CreateState(mapQuery);
            AddResourceToken(state, "city-a");
            var service = new InfluenceService(mapQuery);
            var slotId = InfluenceService.GetLocationSlotId("city-a", 0);
            service.Place(state, 1, slotId);
            var targetId = state.Map.Influences[0].InfluenceId;
            var executor = new EffectTreeExecutor(state, CreateRegistry(service));
            var effectId = executor.CreateRoot(InfluenceEffectSpecFactory.ReplaceInfluence(2, targetId));

            var checkpointReached = false;
            for (var step = 0; step < 20 && !checkpointReached; step++)
            {
                Assert.That(executor.Advance(), Is.True);
                var root = executor.GetNode(effectId);
                if (root.FlowStage != "remove_started" || root.ChildEffectIds.Count != 1)
                {
                    continue;
                }

                var removal = executor.GetNode(root.ChildEffectIds[0]);
                checkpointReached = removal.Status == EffectNodeStatus.Completed && state.Map.Influences.Count == 0;
            }

            Assert.That(checkpointReached, Is.True, executor.LastDiagnostic);
            var recovered = GameStateCloneService.DeepClone(state);
            var resumed = new EffectTreeExecutor(
                recovered,
                CreateRegistry(new InfluenceService(mapQuery)));
            var report = resumed.RunUntilQuiescent();
            var recoveredRoot = resumed.GetNode(effectId);

            Assert.That(report.Completed, Is.True, report.FaultCode);
            Assert.That(recoveredRoot.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(GetString(recoveredRoot.NormalizedResult, "outcome"), Is.EqualTo("replaced"));
            Assert.That(recovered.Map.Influences, Has.Count.EqualTo(1));
            Assert.That(recovered.Map.Influences[0].PlayerId, Is.EqualTo(2));
            Assert.That(recovered.Map.Influences[0].InfluenceId, Is.Not.EqualTo(targetId));
            Assert.That(GetDomainEvents(recovered), Is.EqualTo(new[]
            {
                InfluenceEventTypeIds.InfluenceRemoved,
                InfluenceEventTypeIds.InfluencePlaced,
                InfluenceEventTypeIds.InfluenceReplaced
            }));
        }

        [Test]
        public void LuaPlaceInfluence_CompilesToStableNormalizedArguments()
        {
            const string source = "return function(ctx) return { Effect.PlaceInfluence({ executingPlayer = 'p1', influenceSource = { kind = 'player_supply', sourceId = 'lua.test' }, ownerSubject = { subjectType = 'player', instanceId = 'p1' }, targetSlotId = 'location:city-a:0', causeKind = 'direct' }) } end";
            var definition = new LuaScriptDefinition(
                "content.nmc011.lua",
                "ability.nmc011.lua",
                "handler.nmc011.lua",
                "1.0.0",
                source,
                LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(
                definition,
                new LuaInvocationContext("event.nmc011", "InfluencePlaced", "p1", 0, "1.0.0", 3, 0, 0));
            var compiler = new LuaEffectSpecCompiler();
            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;

            var compiled = compiler.TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out specs, out fault);

            Assert.That(compiled, Is.True, fault == null ? string.Empty : fault.Diagnostic);
            Assert.That(specs, Has.Count.EqualTo(1));
            Assert.That(specs[0].EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
            Assert.That(specs[0].NormalizedArguments.ToDeterministicString(), Does.Contain("city-a"));
            Assert.That(specs[0].NormalizedArguments.ToDeterministicString(), Does.Contain("lua.test"));
        }

        private static EffectRegistry CreateRegistry(InfluenceService service)
        {
            var registry = new EffectRegistry();
            InfluenceEffectExecutor.Register(registry, service);
            return registry;
        }

        private static GameState CreateState(MapQueryService mapQuery)
        {
            return new GameState
            {
                GameId = "nmc011-test",
                MapId = mapQuery.Map.MapId,
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 3, Color = PlayerColor.Green }
                }
            };
        }

        private static void AddResourceToken(GameState state, string locationId)
        {
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
        }

        private static int CountEvents(GameState state, string eventType)
        {
            var count = 0;
            for (var i = 0; i < state.EffectRuntime.RuleEvents.Count; i++)
            {
                if (state.EffectRuntime.RuleEvents[i].EventType == eventType) count++;
            }

            return count;
        }

        private static List<string> GetDomainEvents(GameState state)
        {
            var result = new List<string>();
            for (var i = 0; i < state.EffectRuntime.RuleEvents.Count; i++)
            {
                var eventType = state.EffectRuntime.RuleEvents[i].EventType;
                if (eventType == InfluenceEventTypeIds.InfluenceRemoved ||
                    eventType == InfluenceEventTypeIds.InfluencePlaced ||
                    eventType == InfluenceEventTypeIds.InfluenceReplaced)
                {
                    result.Add(eventType);
                }
            }

            return result;
        }

        private static string GetString(NormalizedValue value, string name)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (var i = 0; i < value.Properties.Count; i++)
                {
                    var entry = value.Properties[i];
                    if (entry != null && entry.Name == name && entry.Value != null && entry.Value.Kind == NormalizedValueKind.String)
                    {
                        return entry.Value.StringValue;
                    }
                }
            }

            return string.Empty;
        }
    }
}
