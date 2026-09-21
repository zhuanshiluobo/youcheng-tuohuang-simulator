using YC.Domain.Effects;
using YC.Domain.Maps;

namespace YC.Infrastructure.Lua
{
    /// <summary>
    /// RoundStarted 的基础规则内容。脚本只从地图只读快照中挑选稳定地块 ID，
    /// 实际开放由 SetLocationsOpenEffectExecutor 在 C# 侧复验并提交。
    /// </summary>
    public static class RoundStartedRedZoneRule
    {
        public const string SubscriptionId = "rules.round_started.red_zone";
        public const string EventType = "RoundStarted";
        public const string ContentInstanceId = "rules.core";
        public const string AbilityId = "rule.round_started.red_zone";
        public const string DefinitionVersion = "1.0.0";
        public const string ContentId = "content.rules.round_started.red_zone";
        public const string SourcePath = "Assets/YC/Infrastructure/Lua/RoundStartedRedZoneRule.cs";

        public const string ScriptSource = @"
return function(ctx)
    local game = GameData.Get()
    local map = Global.Map.GetMap()
    local openRound = 4
    if game.playerCount <= 2 then
        openRound = 6
    elseif game.playerCount == 3 then
        openRound = 5
    end

    if game.round < openRound then
        return {}
    end

    local locations = {}
    for _, location in ipairs(map.locations) do
        if location.isRedZone then
            table.insert(locations, location.locationId)
        end
    end

    return {
        Effect.SetLocationsOpen({
            locations = locations,
            isOpen = true,
            reasonId = ""rule.round_started.red_zone""
        })
    }
end";

        public static LuaScriptDefinition CreateDefinition()
        {
            return new LuaScriptDefinition(
                ContentId,
                AbilityId,
                SubscriptionId,
                DefinitionVersion,
                ScriptSource,
                LuaContentHasher.ComputeSha256(ScriptSource));
        }

        public static void Register(EffectRegistry registry, IMapQueryService mapQueryService)
        {
            if (registry == null) throw new System.ArgumentNullException(nameof(registry));
            if (mapQueryService == null) throw new System.ArgumentNullException(nameof(mapQueryService));
            if (!registry.TryGet(SetLocationsOpenEffectExecutor.EffectTypeId, out _))
            {
                SetLocationsOpenEffectExecutor.Register(registry, mapQueryService);
            }

            new LuaEffectEventDispatcher(registry).Register(new LuaHandlerDefinition(
                SubscriptionId,
                EventType,
                string.Empty,
                CreateDefinition(),
                EffectHandlerRole.Primary,
                0,
                ContentInstanceId,
                AbilityId));
        }
    }
}
