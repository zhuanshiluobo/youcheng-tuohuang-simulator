using System.Collections.Generic;
using NUnit.Framework;
using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.CityStyles;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class NMC013CityStyleSpecialActionEffectEditModeTests
    {
        [TestCase("Effect.ExecuteMainAction({player=ctx.playerId,executionMode='grant_budget',amount=2})")]
        [TestCase("Effect.ExecuteMainAction({player=ctx.playerId,executionMode='facility_entry'})")]
        [TestCase("Effect.ExecuteMainAction({player=ctx.playerId,executionMode='immediate',allowedActionTypes={'build'},amount=1})")]
        [TestCase("Effect.ExecuteMainAction({player=ctx.playerId,allowedActionTypes={'build'}})")]
        [TestCase("Effect.GrantMainActions({player=ctx.playerId,amount=1,executionMode='immediate'})")]
        [TestCase("Effect.RetainCharacter({apiVersion=1,player=ctx.playerId})")]
        [TestCase("{kind='effect',effectTypeId='effect.character.content.retain',apiVersion=1,player=ctx.playerId}")]
        public void RetiredAndMixedActionContractsCannotBeConstructedThroughLua(string expression)
        {
            string source = "return function(ctx) return {" + expression + "} end";
            var script = new LuaScriptDefinition("upgrade.isolated", "upgrade.isolated", "upgrade.isolated", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var result = new MoonSharpLuaRuntimeHost().Invoke(script, new LuaInvocationContext("upgrade.isolated", "test", "p1", 0, "1.0.0", 2, 0, 0));
            Assert.That(result.IsSuccess, Is.False, expression);
        }

        [TestCase("grant_budget")][TestCase("facility_entry")]
        public void RetiredActionModesAreRejectedByTheKernelAndRawNestedRecovery(string mode)
        {
            var state = CreateState(); var registry = new EffectRegistry(); GenericLuaEffectExecutor.Register(registry);
            var args = Object(Entry("player", NormalizedValue.CreateInteger(1)), Entry("executionMode", NormalizedValue.CreateString(mode)), Entry("amount", NormalizedValue.CreateInteger(2)));
            var spec = EffectSpec.Create(LuaDomainEffectTypeIds.ExecuteMainAction, args, 1);
            Assert.That(Assert.Throws<InvalidOperationException>(() => new EffectTreeExecutor(state, registry).CreateRoot(spec)).Message,
                Does.Contain("main_action_execution_mode_retired"));
            var raw = new JObject { ["EffectRuntime"] = new JObject { ["EffectNodes"] = new JArray(new JObject {
                ["EffectTypeId"] = "effect.flow.choice", ["NestedEffects"] = new JArray(JObject.FromObject(new EffectSpecRuntimeState {
                    EffectTypeId = spec.EffectTypeId, NormalizedArguments = args })) }) } };
            object[] values = { raw, null };
            var validate = typeof(HostRecoveryService).GetMethod("TryValidateRetiredCardMarkerRawState", BindingFlags.Static | BindingFlags.NonPublic);
            string before = raw.ToString();
            Assert.That((bool)validate.Invoke(null, values), Is.False);
            Assert.That(values[1], Is.EqualTo("main_action_execution_mode_retired"));
            Assert.That(raw.ToString(), Is.EqualTo(before));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
        }

        [Test] public void RetainCannotReturnThroughACustomRegistrationOrNestedSpec()
        {
            var state = CreateState(); var registry = new EffectRegistry();
            registry.Register("effect.character.content.retain", _ => EffectStepResult.Completed());
            var spec = EffectSpec.Create("effect.character.content.retain", ItemTransferEffectExecutor.Object(), 1);
            Assert.That(Assert.Throws<InvalidOperationException>(() => new EffectTreeExecutor(state, registry).CreateRoot(spec)).Message,
                Does.Contain("character_retain_api_retired"));
            var outer = EffectSpec.Create(EffectTypeIds.Condition, ItemTransferEffectExecutor.Object(), 1);
            outer.NestedEffectGroups.Add(new List<EffectSpec> { spec }); outer.NestedEffectGroups.Add(new List<EffectSpec>());
            Assert.That(Assert.Throws<InvalidOperationException>(() => new EffectTreeExecutor(state, registry).CreateRoot(outer)).Message,
                Does.Contain("character_retain_api_retired"));
        }

        [TestCase(false)][TestCase(true)]
        public void RetiredRetainIsRejectedInCompletedAndNestedRawSnapshots(bool nested)
        {
            var legacy = JObject.FromObject(new EffectNodeRuntimeState {
                EffectTypeId = "effect.character.content.retain", Status = EffectNodeStatus.Completed });
            var owner = nested ? JObject.FromObject(new EffectNodeRuntimeState { EffectTypeId = EffectTypeIds.Condition }) : legacy;
            if (nested) ((JArray)owner["NestedEffects"]).Add(legacy);
            var raw = new JObject { ["EffectRuntime"] = new JObject { ["EffectNodes"] = new JArray(owner) } };
            object[] values = { raw, null };
            var validate = typeof(HostRecoveryService).GetMethod("TryValidateRetiredCardMarkerRawState", BindingFlags.Static | BindingFlags.NonPublic);
            string before = raw.ToString();
            Assert.That((bool)validate.Invoke(null, values), Is.False);
            Assert.That(values[1], Is.EqualTo("character_retain_api_retired"));
            Assert.That(raw.ToString(), Is.EqualTo(before));
        }

        [TestCase(4294967297L)][TestCase(2147483648L)][TestCase(-4294967295L)]
        public void ActionPlayerIdsCannotWrapIntoAnotherPlayer(long player)
        {
            // 模拟未经工厂校验的原始规格；正常 CreateInteger 已先行拒绝这些数值。
            var rawPlayer = new NormalizedValue { Kind = NormalizedValueKind.Integer, IntegerValue = player };
            Assert.That(GenericLuaEffectExecutor.ValidateMainActionArguments(Object(
                Entry("player", rawPlayer), Entry("executionMode", NormalizedValue.CreateString("immediate")),
                Entry("allowedActionTypes", NormalizedValue.CreateArray(new List<NormalizedValue> { NormalizedValue.CreateString("build") })))), Is.Not.Empty);
            Assert.That(CityStyleSpecialActionEffectExecutor.ValidateGrantArguments(Object(
                Entry("player", rawPlayer), Entry("amount", NormalizedValue.CreateInteger(1)))), Is.Not.Empty);
        }
        [Test]
        public void CityStyleLuaCatalog_RegistersActionsWithoutPerPlayerRoundReset()
        {
            var registry = new EffectRegistry();
            CityStyleLuaCatalog.Register(registry);

            Assert.That(registry.HasEventHandler("city-styles.military"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.mobilization"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.composite"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.source-stone"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.efficient"), Is.True);
            Assert.That(registry.HasEventHandler(CityStyleLuaCatalog.CleanupSubscriptionId), Is.False);
        }

        [Test]
        public void LuaCityStyleHandler_CompilesDocumentedInfluenceEffect()
        {
            const string source = "return function(ctx) local p = ctx.payload return { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'city_style.test', ownerSubject = ctx.playerId, targetSlotId = p.candidateIds[1], causeKind = 'other_intrinsic_flow' }) } end";
            var payload = LuaSnapshotValue.Object(new[]
            {
                new LuaSnapshotEntry("candidateSetId", LuaSnapshotValue.String("candidate-set")),
                new LuaSnapshotEntry("candidateSetVersion", LuaSnapshotValue.Integer(1)),
                new LuaSnapshotEntry("candidateIds", LuaSnapshotValue.Array(new[] { LuaSnapshotValue.String("slot-a") }))
            });
            var facade = new LuaFacadeSnapshot { EventPayload = payload };
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(
                new LuaScriptDefinition("content.nmc013.test", "ability.nmc013.test", "handler.nmc013.test", "1.0.0", source, LuaContentHasher.ComputeSha256(source)),
                new LuaInvocationContext("event.nmc013.test", "CityStyleSpecialActionActivated", "p1", 0, "1.0.0", 3, 0, 0, facade));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);

            var compiler = new LuaEffectSpecCompiler();
            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            Assert.That(compiler.TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out specs, out fault), Is.True, fault == null ? string.Empty : fault.Diagnostic);
            Assert.That(specs, Has.Count.EqualTo(1));
            Assert.That(specs[0].EffectTypeId, Is.EqualTo(InfluenceEffectTypeIds.PlaceInfluence));
            Assert.That(specs[0].NormalizedArguments.ToDeterministicString(), Does.Contain("slot-a"));
        }

        [Test]
        public void GenericGrantMainActions_UpdatesBudgetAndLocksCharacter()
        {
            var state = CreateState();
            var registry = CreateRegistry();
            var executor = new EffectTreeExecutor(state, registry);
            var effect = new EffectSpec(
                CityStyleSpecialActionEffectTypeIds.GrantMainActions,
                Object(
                    Entry("amount", NormalizedValue.CreateInteger(2)),
                    Entry("lockCharacterCard", NormalizedValue.CreateBoolean(true))))
            {
                PlayerId = 1,
                DefinitionVersion = CityStyleSpecialActionEffectExecutor.DefinitionVersion
            };

            string effectId = executor.CreateRoot(effect);
            EffectRunReport report = executor.RunUntilQuiescent();

            Assert.That(report.Completed, Is.True, report.FaultCode);
            Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).CharacterCardLockedThisTurn, Is.True);
        }

        [Test]
        public void GenericCleanupEffect_IsSerializableAndIdempotent()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.UsedSpecialActionIdsThisRound.Add("action.nmc013");
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "marker.nmc013",
                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                MarkerArea = CityStyleMarkerAreas.Used,
                RemainingSpecialActionUses = 0
            });
            var registry = CreateRegistry();
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Cleanup(1));
            Assert.That(executor.RunUntilQuiescent().Completed, Is.True);
            Assert.That(state.FindPlayer(1).UsedSpecialActionIdsThisRound, Is.Empty);
            Assert.That(state.FindPlayer(1).DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Declared));

            var recovered = GameStateCloneService.DeepClone(state);
            var resumed = new EffectTreeExecutor(recovered, CreateRegistry());
            Assert.That(resumed.RunUntilQuiescent().Completed, Is.True);
            Assert.That(resumed.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(recovered.FindPlayer(1).DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Declared));

            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "marker.nmc013.level2",
                CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                UnlockedSpecialActionId = SpecialActionDatabase.SourceStoneIndustrialHub,
                MarkerArea = SpecialActionMarkerAreas.UsedFromTwo,
                RemainingSpecialActionUses = 2
            });
            var cleanupExecutor = new EffectTreeExecutor(state, registry);
            Assert.That(cleanupExecutor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Cleanup(1)), Is.Not.Empty);
            Assert.That(cleanupExecutor.RunUntilQuiescent().Completed, Is.True);
            Assert.That(player.DeclaredCityStyles[1].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesOne));
            Assert.That(player.DeclaredCityStyles[1].RemainingSpecialActionUses, Is.EqualTo(1));

            var secondCleanupExecutor = new EffectTreeExecutor(state, registry);
            Assert.That(secondCleanupExecutor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Cleanup(1)), Is.Not.Empty);
            Assert.That(secondCleanupExecutor.RunUntilQuiescent().Completed, Is.True);
            Assert.That(player.DeclaredCityStyles[1].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.UsesOne));
            Assert.That(player.DeclaredCityStyles[1].RemainingSpecialActionUses, Is.EqualTo(1));
        }

        [Test]
        public void PlayerCleanupEvent_WaitsForSystemRoundCleanup()
        {
            var state = CreateState();
            var player = state.FindPlayer(1);
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "marker.nmc013.event",
                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                UnlockedSpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                MarkerArea = CityStyleMarkerAreas.Used,
                RemainingSpecialActionUses = 0
            });

            var registry = CreateRegistry();
            CityStyleLuaCatalog.Register(registry);
            registry.Register("test.nmc013.cleanup-event", context =>
                EffectStepResult.Completed().AddEvent(new EffectEventRequest
                {
                    EventType = CityStyleSpecialActionEventTypeIds.CleanupStarted,
                    PlayerId = 1,
                    SemanticKey = "nmc013-cleanup"
                }));

            var executor = new EffectTreeExecutor(state, registry);
            var rootId = executor.CreateRoot(EffectSpec.Create("test.nmc013.cleanup-event"));
            var report = executor.RunUntilQuiescent();

            Assert.That(report.Completed, Is.True, report.FaultCode + ":" + state.EffectRuntime.LastFaultMessage + "\n" + Newtonsoft.Json.JsonConvert.SerializeObject(state.EffectRuntime.Diagnostics));
            Assert.That(executor.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            player = state.FindPlayer(1);
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Used));
            Assert.That(state.EffectRuntime.EffectNodes.Exists(n => n.EffectTypeId == CityStyleSpecialActionEffectTypeIds.OperatePlayerMarker), Is.False);
            Assert.That(executor.CreateRoot(CityStyleSpecialActionEffectSpecFactory.Cleanup(1)), Is.Not.Empty);
            Assert.That(executor.RunUntilQuiescent().Completed, Is.True);
            player = state.FindPlayer(1);
            Assert.That(player.UsedSpecialActionIdsThisRound, Is.Empty);
            Assert.That(player.DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Unused));
            Assert.That(player.DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(1));
        }

        [Test]
        public void DocumentedChoiceEffect_OpensAndResolvesCandidateInteraction()
        {
            var state = CreateState();
            var registry = new EffectRegistry();
            GenericLuaEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(EffectSpec.Create(
                LuaDomainEffectTypeIds.Choice,
                Object(
                    Entry("player", NormalizedValue.CreateInteger(1)),
                    Entry("options", NormalizedValue.CreateArray(new List<NormalizedValue>
                    {
                        NormalizedValue.CreateString("option-a"),
                        NormalizedValue.CreateString("option-b")
                    })),
                    Entry("minSelections", NormalizedValue.CreateInteger(1)),
                    Entry("maxSelections", NormalizedValue.CreateInteger(1)))));

            EffectRunReport waiting = executor.RunUntilQuiescent();
            Assert.That(waiting.WaitingForInput, Is.True, waiting.FaultCode);
            InteractionRequest request = state.EffectRuntime.InteractionRequests.Find(
                candidate => candidate != null && candidate.Status == "open" && candidate.OwnerEffectId == effectId);
            Assert.That(request, Is.Not.Null);
            Assert.That(request.CandidateIds, Is.EqualTo(new List<string> { "option-a", "option-b" }));

            string diagnostic;
            Assert.That(executor.TrySubmitInteraction(
                request.GetStableInteractionId(),
                1,
                NormalizedValue.CreateStableReference("candidate", "option-b"),
                out diagnostic), Is.True, diagnostic);
            Assert.That(executor.RunUntilQuiescent().Completed, Is.True);
            Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
        }

        [Test]
        public void GrantMainActions_UpdatesBudgetWithoutLockingWhenExplicitlyDisabled()
        {
            var state = CreateState();
            var registry = new EffectRegistry();
            GenericLuaEffectExecutor.Register(registry);
            CityStyleSpecialActionEffectExecutor.RegisterPrimitives(registry);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(EffectSpec.Create(
                CityStyleSpecialActionEffectTypeIds.GrantMainActions,
                Object(
                    Entry("player", NormalizedValue.CreateInteger(1)),
                    Entry("lockCharacterCard", NormalizedValue.CreateBoolean(false)),
                    Entry("amount", NormalizedValue.CreateInteger(2))), playerId: 1));

            Assert.That(executor.RunUntilQuiescent().Completed, Is.True);
            Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(3));
        }

        [Test]
        public void DocumentedLoseScore_ClampsToConfiguredMinimum()
        {
            var state = CreateState();
            state.FindPlayer(1).Score = 1;
            var registry = new EffectRegistry();
            GenericLuaEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(EffectSpec.Create(
                LuaDomainEffectTypeIds.LoseScore,
                Object(
                    Entry("player", NormalizedValue.CreateInteger(1)),
                    Entry("amount", NormalizedValue.CreateInteger(8)),
                    Entry("minimumScore", NormalizedValue.CreateInteger(-2)))));

            Assert.That(executor.RunUntilQuiescent().Completed, Is.True);
            Assert.That(executor.GetNode(effectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(-2));
        }

        private static EffectRegistry CreateRegistry()
        {
            var mapQuery = new MapQueryService(StaticMapDefinitions.CreateThreePlayerPlaceholder());
            var influence = new InfluenceService(mapQuery);
            var movement = new CityMovementService(
                mapQuery,
                influence,
                new TravelCostService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());
            var options = new SpecialActionOptionQueryService(
                mapQuery,
                influence,
                movement,
                new SpecialActionLifecycleService(),
                new MainActionBudgetService());
            var registry = new EffectRegistry();
            CityStyleSpecialActionEffectExecutor.Register(registry, options, new CandidatePolicyRegistry(), mapQuery, influence, movement);
            return registry;
        }

        private static GameState CreateState()
        {
            var map = StaticMapDefinitions.CreateThreePlayerPlaceholder();
            return new GameState
            {
                GameId = "nmc013-test",
                Round = 1, ActionRound = 1,
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                MapId = map.MapId,
                Map = new MapRuntimeState(),
                Players = new List<PlayerState>
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        RemainingMainActionsThisTurn = 1,
                        Resources = new ResourceSet(),
                        DeclaredCityStyles = new List<CityStyleDeclarationState>(),
                        UsedSpecialActionIdsThisRound = new List<string>()
                    }
                }
            };
        }

        private static NormalizedValue Object(params NormalizedValueEntry[] entries)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>(entries));
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }
}
