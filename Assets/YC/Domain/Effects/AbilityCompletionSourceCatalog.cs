using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 固定完成事件目录只读取真实来源，构造独立Spec。不接收外部处理器或工厂委托。
    public static class AbilityCompletionSourceCatalog
    {
        [ThreadStatic] private static bool originalRequested;
        public static IDisposable UseOriginalCatalogForDiagnostics() => new OriginalScope();
        private sealed class OriginalScope : IDisposable
        {
            private readonly bool previous=originalRequested;private bool closed;
            internal OriginalScope(){originalRequested=true;}
            public void Dispose(){if(closed)return;closed=true;originalRequested=previous;}
        }
        public static bool TryRegister(EffectRegistry registry,string contentHash)
        {
            if(registry==null)throw new ArgumentNullException(nameof(registry));
            // 未冻结或带可用性注入的目录仍由原Infrastructure路径登记和隔离。
            if(originalRequested || !registry.HasAbilityCatalog || registry.HasAvailabilityControlledEventSource("EffectCompleted"))return false;
            const string subscription="ability-source.event.EffectCompleted";
            var registration=new EffectEventHandlerRegistration(subscription,"EffectCompleted","",subscription,context=>
            {
                var effects=new List<EffectSpec>();
                foreach(var source in registry.QuerySources(context.State,-1,"event","EffectCompleted"))
                {
                    var declaration=registry.GetAbilitySourceDeclaration(source.AbilityId);
                    if(!MatchesCompletion(context.State,context.Event,declaration))continue;
                    if(declaration.Kind=="event" && declaration.AllowedOperations.Any(p=>p.StartsWith("response.",StringComparison.Ordinal)))continue;
                    if(AbilitySourceEffectExecutor.IsConsumed(context.State,source,"event:"+context.Event.EventId))continue;
                    effects.Add(AbilitySourceEffectExecutor.Create(source,context.Event,declaration.HandlerRef,false,context.State.Round));
                }
                return effects;
            },EffectHandlerRole.Observer,definitionVersion:"1.0.0",contentHash:contentHash,contentId:"ability-source.catalog")
                .WithNonRetainedStateContract(()=>true);
            registration.HandlerPreservesUniqueReferences=true;
            registration.HandlerPreservesJournalPrefix=true;
            registry.RegisterEventHandler(registration);return true;
        }
        public static bool MatchesCompletion(GameState state,RuleEvent ruleEvent,AbilitySourceDeclaration declaration)
        {
            if(ruleEvent.EventType!="EffectCompleted")return true;
            var node=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==ruleEvent.SourceEffectId);
            return node!=null && !FacilityBuildContract.IsSelection(node.EffectTypeId,node.NormalizedArguments) &&
                !(node.EffectTypeId==ExplorationEffectTypeIds.Explore && ExplorationContract.IsV3(node.NormalizedArguments)) &&
                node.CompletedEventId==ruleEvent.EventId && declaration.CompletedEffectTypes.Contains(node.EffectTypeId) && node.EffectTypeId!=AbilitySourceEffectExecutor.TypeId &&
                (declaration.CompletionPolicy=="terminal" || node.Status==EffectNodeStatus.Completed || node.PendingOutcome==EffectPendingOutcome.Completed);
        }
    }
}
