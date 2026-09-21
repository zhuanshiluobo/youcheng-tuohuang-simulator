using System.Collections.Generic;
using NUnit.Framework;
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
        [Test]
        public void CityStyleLuaCatalog_RegistersEverySpecialActionAndCleanupRoute()
        {
            var registry = new EffectRegistry();
            CityStyleLuaCatalog.Register(registry);

            Assert.That(registry.HasEventHandler("city-styles.military"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.mobilization"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.composite"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.source-stone"), Is.True);
            Assert.That(registry.HasEventHandler("city-styles.efficient"), Is.True);
            Assert.That(registry.HasEventHandler(CityStyleLuaCatalog.CleanupSubscriptionId), Is.True);
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
                    Entry("operation", NormalizedValue.CreateString("grant_main_actions")),
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
        public void LuaCleanupEvent_DispatchesTheDocumentedMarkerCleanupEffect()
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

            Assert.That(report.Completed, Is.True, report.FaultCode);
            Assert.That(executor.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
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
        public void DocumentedExecuteMainActionGrantBudget_UpdatesPlayerBudget()
        {
            var state = CreateState();
            var registry = new EffectRegistry();
            GenericLuaEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            string effectId = executor.CreateRoot(EffectSpec.Create(
                LuaDomainEffectTypeIds.ExecuteMainAction,
                Object(
                    Entry("player", NormalizedValue.CreateInteger(1)),
                    Entry("executionMode", NormalizedValue.CreateString("grant_budget")),
                    Entry("amount", NormalizedValue.CreateInteger(2)))));

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
