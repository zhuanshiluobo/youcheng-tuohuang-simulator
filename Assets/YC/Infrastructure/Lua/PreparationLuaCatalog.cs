using System;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    /// <summary>将模式冻结的步骤交给已有 Lua 宿主、效果编译器和续接处理器。</summary>
    public static class PreparationLuaCatalog
    {
        public static void Register(EffectRegistry registry, ExternalContentPack pack, PreparationFlowPlan plan)
        {
            if (registry == null || pack == null || plan == null) throw new ArgumentNullException();
            plan.Validate();
            PreparationWindowFlow.Register(registry);
            foreach (var declaration in plan.BeforeDistribution.Concat(plan.BetweenDistributionAndConfirmation)
                .Concat(plan.AfterConfirmation).Where(step => step.Kind == "lua"))
            {
                if (registry.TryGetPreparationStep(declaration, out _)) continue;
                if (declaration.SourcePackId == "core")
                {
                    if ((string)pack.Manifest["version"] != declaration.SourcePackVersion)
                        throw new InvalidOperationException("preparation_source_version_mismatch:" + declaration.SourcePackId);
                }
                else if (pack.DiyComposition == null ||
                    pack.DiyComposition.GetPack(declaration.SourcePackId).Manifest.Version != declaration.SourcePackVersion)
                    throw new InvalidOperationException("preparation_source_version_mismatch:" + declaration.SourcePackId);

                string path = SetupRecipeComposer.PreparationScriptPath(declaration.SourcePackId, declaration.SourcePackVersion, declaration.ScriptPath);
                string source = pack.GetScript(path);
                if (LuaContentHasher.ComputeSha256(source) != declaration.ScriptHash)
                    throw new InvalidOperationException("preparation_script_hash_mismatch:" + declaration.StepId);
                string identity = StableIdFactory.Create("preparation-handler", declaration.SourcePackId,
                    declaration.SourcePackVersion, declaration.HandlerId, declaration.HandlerVersion);
                var body = new LuaScriptDefinition(identity, identity, declaration.HandlerId, declaration.HandlerVersion,
                    source, declaration.ScriptHash, pack.GetScriptEntryId(path));
                var compiler = new LuaEffectSpecCompiler(presentationPack: pack);
                var dispatcher = new LuaEffectEventDispatcher(registry, compiler: compiler);
                LuaScriptDefinition completion = null;
                if (declaration.Completion != null)
                {
                    string endPath = SetupRecipeComposer.PreparationScriptPath(declaration.SourcePackId,
                        declaration.SourcePackVersion, declaration.Completion.ScriptPath);
                    string endSource = pack.GetScript(endPath);
                    if (LuaContentHasher.ComputeSha256(endSource) != declaration.Completion.ScriptHash)
                        throw new InvalidOperationException("preparation_completion_hash_mismatch:" + declaration.StepId);
                    completion = new LuaScriptDefinition(identity, identity, declaration.Completion.HandlerId,
                        declaration.Completion.HandlerVersion, endSource, declaration.Completion.ScriptHash, pack.GetScriptEntryId(endPath));
                    compiler.RegisterPreparationCompletion(identity, completion);
                    dispatcher.RegisterContinuation(new LuaHandlerDefinition(
                        StableIdFactory.Create("preparation-continuation", identity, completion.HandlerId), "EffectCompleted", "", completion,
                        EffectHandlerRole.Continuation, contentInstanceId: identity, abilityId: identity));
                }
                var handler = new LuaHandlerDefinition(identity, "AbilityInvoked", "", body, EffectHandlerRole.Primary,
                    contentInstanceId: identity, abilityId: identity, completionHandlerId: completion?.HandlerId ?? "", continuationTarget: completion);
                var frozen = GameStateCloneService.DeepClone(declaration);
                registry.RegisterPreparationStep(new PreparationStepRegistration(frozen, (context, arguments) =>
                {
                    if (context.State.PreparationRuntime?.ModeId != plan.ModeId ||
                        !string.IsNullOrEmpty(registry.ContentPackHash) && registry.ContentPackHash != context.State.ContentPackHash)
                        throw new InvalidOperationException("preparation_lua_context_invalid");
                    var ruleEvent = new RuleEvent { EventId = StableIdFactory.Create("preparation-invocation", context.Node.EffectId, identity),
                        EventType = "AbilityInvoked", PlayerId = context.Node.PlayerId, OwnerNodeId = context.Node.EffectId,
                        SourceEffectId = context.Node.EffectId, RouteKey = identity,
                        Payload = Object("arguments", arguments.Clone()) };
                    return dispatcher.Invoke(handler, new EffectEventHandlerContext(context.State, ruleEvent, context.Node, null));
                }));
            }
        }
    }
}
