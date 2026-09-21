using YC.Domain.Effects;

namespace YC.Infrastructure.Lua
{
    public static class PlayerEntranceLuaCatalog
    {
        public const string Source = @"
return function(ctx)
    local payload = ctx.payload
    local effects = {}
    if payload.resourcePoint == 'B-01' and not payload.initialEntranceRewardApplied then
        for _, resource in ipairs({ 'originium', 'originium_shard', 'iron' }) do
            table.insert(effects, Effect.GainResource({ recipient = payload.player, resourceType = resource, amount = 2 }))
        end
    end
    local point = Global.Map.GetResourcePoint(payload.resourcePoint)
    if point ~= nil and not point.hasResourcePointIndicator then
        table.insert(effects, Effect.RevealEventCard({
            player = payload.player, eventDeck = 'main', eventColor = 'green',
            placeResourcePointIndicator = true, resourcePoint = payload.resourcePoint
        }))
    end
    return effects
end";
        public static void Register(EffectRegistry registry)
        {
            if (registry.HasEventHandler(PlayerEntranceEffectExecutor.RulesSubscriptionId)) return;
            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition(
                PlayerEntranceEffectExecutor.RulesSubscriptionId, PlayerEntranceEffectExecutor.EventType, "entrance",
                new LuaScriptDefinition("content.world.entrance", "world.entrance", "world.entrance",
                    PlayerEntranceEffectExecutor.Version, Source, LuaContentHasher.ComputeSha256(Source)),
                EffectHandlerRole.Primary, 0, "world", "world.entrance"));
        }
    }
}
