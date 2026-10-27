using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    /// <summary>新版离场来源各自登记真实处理器，沿内核CompareHandlers排序，不藏在一个聚合订阅内。</summary>
    internal static class DepartureSourceLuaCatalog
    {
        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            foreach (var declaration in registry.AbilityDeclarations.Where(d => d.Kind == "event" && d.AllowedOperations.Any(p => p.StartsWith("response.", StringComparison.Ordinal))))
            foreach (string eventType in declaration.ObservedEvents)
            {
                if (eventType != InfluenceEventTypeIds.InfluenceRemoved && eventType != InfluenceEventTypeIds.InfluenceReplaced) throw new InvalidOperationException("departure_response_event_invalid");
                string subscription = "departure-source." + declaration.AbilityId + "." + eventType;
                var content = pack.ActiveDefinitions.Single(d => d.DefinitionId == declaration.DefinitionId);
                var scriptPath = content.AbilityHandlers.SingleOrDefault(h => h.HandlerId == declaration.HandlerRef)?.Script ?? content.Abilities.SingleOrDefault(a => a.SubscriptionId == declaration.HandlerRef)?.Script;
                if (string.IsNullOrEmpty(scriptPath)) throw new InvalidOperationException("departure_source_lua_handler_missing");
                string handlerHash = LuaContentHasher.ComputeSha256(pack.GetScript(scriptPath));
                registry.RegisterEventHandler(new EffectEventHandlerRegistration(subscription, eventType, "", declaration.HandlerRef, context =>
                {
                    var effects = new List<EffectSpec>();
                    if (Number(context.Event.Payload, "eventProtocolVersion") == 2 && eventType == InfluenceEventTypeIds.InfluenceReplaced) return effects;
                    foreach (var source in registry.QuerySources(context.State, -1, "event", eventType).Where(s => s.AbilityId == declaration.AbilityId))
                    {
                        if (AbilitySourceEffectExecutor.IsConsumed(context.State, source, "event:" + context.Event.EventId)) continue;
                        var root = AbilitySourceEffectExecutor.Create(source, context.Event, declaration.HandlerRef, false, context.State.Round);
                        // 真正的离场可选收益先登记来源事实；库存相关正文等同批去向结清后再预检/构造。
                        if ((declaration.AllowedOperations.Contains("response.optional_effect") && InfluenceDepartureProtocol.ForEvent(context.State, context.Event) != null) ||
                            registry.HasEventResponseBody(context, root, source)) effects.Add(root);
                    }
                    return effects;
                }, EffectHandlerRole.Observer, contentInstanceId: declaration.DefinitionId, abilityId: declaration.AbilityId,
                    definitionVersion: declaration.DeclarationVersion, contentHash: handlerHash, contentId: declaration.DefinitionId));
            }
        }
    }
}
