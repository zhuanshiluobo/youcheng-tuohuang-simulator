using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class NMC006LuaEffectDispatchEditModeTests
    {
        [Test]
        public void UnimplementedLuaEffect_FailsStopInsteadOfReportingSuccessfulSettlement()
        {
            var registry = new EffectRegistry();
            GenericLuaEffectExecutor.Register(registry);
            var state = new GameState();
            var executor = new EffectTreeExecutor(state, registry);
            var id = executor.CreateRoot(new EffectSpec(LuaDomainEffectTypeIds.UpgradeEnterprise));
            var report = executor.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.True);
            Assert.That(executor.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Faulted));
            Assert.That(state.EffectRuntime.LastFaultMessage, Does.Contain("尚未实现宿主结算"));
        }

        [Test]
        public void FacadeContract_ReadsGlobalGamePayloadAndPlayerSnapshots()
        {
            const string source = @"
return function(ctx)
    local game = GameData.Get()
    local player = PlayerData.Get(ctx.playerId)
    local map = Global.Map.GetMap()
    return {
        Effect.GainScore({
            recipient = player.playerId,
            amount = game.playerCount + map.missing + 1,
            reasonId = ctx.payload.reason
        })
    }
end";

            LuaFacadeSnapshot snapshot = LuaFacadeSnapshot.FromInvocation("p1", 10, 4, 2, "1.0.0");
            snapshot.GlobalMap = LuaSnapshotValue.Object(new[]
            {
                new LuaSnapshotEntry("missing", LuaSnapshotValue.Integer(0))
            });
            snapshot.EventPayload = LuaSnapshotValue.Object(new[]
            {
                new LuaSnapshotEntry("reason", LuaSnapshotValue.String("payload.reason"))
            });
            LuaScriptDefinition definition = Definition(source, "facade.handler");
            var context = new LuaInvocationContext(
                "event.facade",
                "FacadeEvent",
                "p1",
                4,
                "1.0.0",
                2,
                10,
                4,
                snapshot);

            LuaInvocationResult result = new MoonSharpLuaRuntimeHost().Invoke(definition, context);

            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects, Has.Count.EqualTo(1));
            Assert.That(result.Effects[0].Amount, Is.EqualTo(3));
            Assert.That(result.Effects[0].ReasonId, Is.EqualTo("payload.reason"));
        }

        [Test]
        public void Compiler_ProducesNormalizedSpecsAndKeepsContinuationBindingMetadata()
        {
            const string source = @"return function(ctx) return {
    Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 2 })
} end";
            LuaScriptDefinition definition = Definition(source, "ability.compile");
            LuaInvocationResult invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, Context("event.compile"));
            var compiler = new LuaEffectSpecCompiler();
            var compilationContext = new LuaEffectCompilationContext
            {
                ContentInstanceId = "instance.1",
                AbilityId = "ability.compile",
                CompletionHandlerId = "continuation.compile",
                StableKeyPrefix = "subscription.compile",
                PlayerId = 1
            };

            List<EffectSpec> specs;
            LuaEffectCompilationFault fault;
            Assert.That(compiler.TryCompile(invocation, compilationContext, out specs, out fault), Is.True, fault == null ? string.Empty : fault.Diagnostic);
            Assert.That(specs, Has.Count.EqualTo(1));
            Assert.That(specs[0].EffectTypeId, Is.EqualTo("effect.resource.gain"));
            Assert.That(specs[0].CompletionHandlerId, Is.EqualTo("continuation.compile"));
            Assert.That(specs[0].ContinuationContentId, Is.EqualTo(definition.ContentId));
            Assert.That(specs[0].ContinuationDefinitionVersion, Is.EqualTo(definition.DefinitionVersion));
            Assert.That(specs[0].ContinuationContentHash, Is.EqualTo(definition.ContentHash));
            Assert.That(specs[0].NormalizedArguments.IsValid(), Is.True);
        }

        [Test]
        public void LuaHandlers_AreIndexedSortedAndEmptyResponseOnlyWritesReceipt()
        {
            const string emptySource = @"return function(ctx) return {} end";
            LuaScriptDefinition observerScript = Definition(emptySource, "ability.observer");
            LuaScriptDefinition primaryScript = Definition(emptySource, "ability.primary");
            var registry = NewRegistry();
            var dispatcher = new LuaEffectEventDispatcher(registry);
            dispatcher.Register(new LuaHandlerDefinition(
                "observer",
                "LuaEvent",
                "*",
                observerScript,
                EffectHandlerRole.Observer,
                0,
                "content.z",
                "ability.z"));
            dispatcher.Register(new LuaHandlerDefinition(
                "primary",
                "LuaEvent",
                "route.target",
                primaryScript,
                EffectHandlerRole.Primary,
                9,
                "content.primary",
                "ability.primary"));

            GameState state = NewState("lua-dispatch");
            registry.Register("test.lua.root", context => EffectStepResult.Completed().AddEvent(new EffectEventRequest
            {
                EventType = "LuaEvent",
                RouteKey = "route.target",
                PlayerId = 1,
                SemanticKey = "one"
            }));
            var executor = new EffectTreeExecutor(state, registry);
            string rootId = executor.CreateRoot(EffectSpec.Create("test.lua.root"));

            EffectRunReport report = executor.RunUntilQuiescent();

            Assert.That(report.Faulted, Is.False, report.FaultCode);
            Assert.That(executor.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.DispatchReceipts, Has.Count.EqualTo(2));
            Assert.That(state.EffectRuntime.DispatchReceipts[0].SubscriptionId, Is.EqualTo("primary"));
            Assert.That(state.EffectRuntime.DispatchReceipts[1].SubscriptionId, Is.EqualTo("observer"));
            Assert.That(state.EffectRuntime.EffectNodes, Has.Count.EqualTo(1));
        }

        [Test]
        public void LuaSchemaFault_PausesEffectRuntimeAndRollsBackReceiptAndChildren()
        {
            const string invalidSource = @"return function(ctx) return {
    { kind = 'effect_spec', effectTypeId = 'effect.not.registered', recipient = ctx.playerId, amount = 1 }
} end";
            LuaScriptDefinition definition = Definition(invalidSource, "ability.invalid");
            var registry = NewRegistry();
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition(
                "invalid",
                "LuaEvent",
                "route.invalid",
                definition,
                EffectHandlerRole.Primary,
                0,
                "content.invalid",
                "ability.invalid"));
            registry.Register("test.lua.root", context => EffectStepResult.Completed().AddEvent(new EffectEventRequest
            {
                EventType = "LuaEvent",
                RouteKey = "route.invalid",
                PlayerId = 1,
                SemanticKey = "one"
            }));

            GameState state = NewState("lua-invalid");
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(EffectSpec.Create("test.lua.root"));

            EffectRunReport report = executor.RunUntilQuiescent();

            Assert.That(report.Faulted, Is.True);
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.PausedFault));
            Assert.That(state.EffectRuntime.DispatchReceipts, Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes, Has.Count.EqualTo(1));
        }

        [Test]
        public void LuaEffectConstructor_RejectsUnexpectedSensitiveField()
        {
            const string source = @"return function(ctx) return {
    Effect.GainScore({ recipient = ctx.playerId, amount = 1, stateMutation = true })
} end";

            LuaInvocationResult result = new MoonSharpLuaRuntimeHost().Invoke(
                Definition(source, "ability.sensitive-field"),
                Context("event.sensitive-field"));

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.UnexpectedField));
            Assert.That(result.Effects, Is.Empty);
        }

        [Test]
        public void ContinuationBinding_IsPersistedAndHashMismatchFaultsAfterRecovery()
        {
            const string continuationSource = @"return function(ctx) return {
    Effect.GainScore({ recipient = ctx.playerId, amount = 1 })
} end";
            LuaScriptDefinition definition = Definition(continuationSource, "ability.continuation");
            var registry = NewRegistry();
            new LuaEffectEventDispatcher(registry).RegisterContinuation(new LuaHandlerDefinition(
                "continuation",
                "EffectCompleted",
                string.Empty,
                definition,
                EffectHandlerRole.Continuation,
                0,
                "content.instance",
                "ability.continuation"));
            registry.Register("test.lua.root", context => EffectStepResult.Completed(), definitionVersion: "1.0.0");

            GameState state = NewState("lua-continuation");
            var root = EffectSpec.Create("test.lua.root");
            root.DefinitionVersion = "1.0.0";
            root.CompletionHandlerId = definition.HandlerId;
            root.ContinuationContentId = definition.ContentId;
            root.ContinuationContentInstanceId = "content.instance";
            root.ContinuationAbilityId = "ability.continuation";
            root.ContinuationContentHash = definition.ContentHash;
            var executor = new EffectTreeExecutor(state, registry);
            executor.CreateRoot(root);
            EffectRunReport first = executor.RunUntilQuiescent();

            Assert.That(first.Faulted, Is.False, first.FaultCode);
            Assert.That(state.EffectRuntime.ContinuationBindings, Has.Count.EqualTo(1));
            Assert.That(state.EffectRuntime.ContinuationBindings[0].Status, Is.EqualTo(ContinuationBindingStatus.Completed));
            Assert.That(state.EffectRuntime.ContinuationBindings[0].CompletionEventId, Is.Not.Empty);
            Assert.That(state.EffectRuntime.DispatchReceipts, Has.Count.EqualTo(1));
            Assert.That(state.EffectRuntime.DispatchReceipts[0].ContentId, Is.EqualTo(definition.ContentId));

            GameState recovered = GameStateCloneService.DeepClone(state);
            var recoveredExecutor = new EffectTreeExecutor(recovered, registry);
            EffectRunReport second = recoveredExecutor.RunUntilQuiescent();
            Assert.That(second.Faulted, Is.False, second.FaultCode);
            Assert.That(recovered.EffectRuntime.DispatchReceipts, Has.Count.EqualTo(1));

            const string changedSource = @"return function(ctx) return {} end";
            LuaScriptDefinition changedDefinition = Definition(changedSource, "ability.continuation");
            var changedRegistry = NewRegistry();
            new LuaEffectEventDispatcher(changedRegistry).RegisterContinuation(new LuaHandlerDefinition(
                "continuation",
                "EffectCompleted",
                string.Empty,
                changedDefinition,
                EffectHandlerRole.Continuation,
                0,
                "content.instance",
                "ability.continuation"));
            var mismatchedExecutor = new EffectTreeExecutor(
                GameStateCloneService.DeepClone(state),
                changedRegistry);
            EffectRunReport mismatch = mismatchedExecutor.RunUntilQuiescent();
            Assert.That(mismatch.Faulted, Is.True);
            Assert.That(mismatchedExecutor.Runtime.LastFaultCode, Is.EqualTo(EffectFaultCodes.ContinuationContentHashMismatch));
        }

        private static EffectRegistry NewRegistry()
        {
            var registry = new EffectRegistry();
            registry.Register("effect.resource.gain", context => EffectStepResult.Completed(), definitionVersion: "1.0.0");
            registry.Register("effect.score.gain", context => EffectStepResult.Completed(), definitionVersion: "1.0.0");
            return registry;
        }

        private static GameState NewState(string gameId)
        {
            var state = new GameState { GameId = gameId, CurrentPlayerId = 1, StartPlayerId = 1 };
            state.Players.Add(new PlayerState { PlayerId = 1, Score = 7 });
            state.Players.Add(new PlayerState { PlayerId = 2, Score = 3 });
            return state;
        }

        private static LuaScriptDefinition Definition(string source, string abilityId)
        {
            return new LuaScriptDefinition(
                "content." + abilityId,
                abilityId,
                abilityId + ".handler",
                "1.0.0",
                source,
                LuaContentHasher.ComputeSha256(source));
        }

        private static LuaInvocationContext Context(string eventId)
        {
            return new LuaInvocationContext(eventId, "LuaEvent", "p1", 1, "1.0.0", 2, 7, 2);
        }
    }
}
