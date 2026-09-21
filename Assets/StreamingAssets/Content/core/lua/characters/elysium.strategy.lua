return function(ctx)
    local resources = PlayerData.GetResources(ctx.playerId)
    local ids = { 'originium', 'originium-shard', 'iron' }
    local types = { originium = 'originium', ['originium-shard'] = 'originium_shard', iron = 'iron' }
    local values = { resources.originium, resources.originiumShard, resources.iron }
    local minimum = values[1]
    for i = 2, #values do if values[i] < minimum then minimum = values[i] end end
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        local selected = ctx.payload.normalizedResult
        for i = 1, #ids do
            if ids[i] == selected and values[i] == minimum then
                return { Effect.GainResource({ recipient = ctx.playerId, resourceType = types[selected], amount = 4,
                    reasonId = 'character.elysium.strategy' }) }
            end
        end
        error('elysium_strategy_resource_invalid')
    end
    local candidates = {}
    for i = 1, #ids do if values[i] == minimum then table.insert(candidates, ids[i]) end end
    return { Effect.Choice({ player = ctx.playerId, options = candidates,
        promptKey = 'character.elysium.strategy.choose_resource' }) }
end
