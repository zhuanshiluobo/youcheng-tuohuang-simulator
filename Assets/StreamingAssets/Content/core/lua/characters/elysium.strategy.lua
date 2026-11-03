return function(ctx)
    local resources = PlayerData.GetResources(ctx.playerId)
    local ids = { 'originium', 'originium-shard', 'iron' }
    local types = { originium = 'originium', ['originium-shard'] = 'originium_shard', iron = 'iron' }
    local values = { resources.originium, resources.originiumShard, resources.iron }
    local minimum = values[1]
    for i = 2, #values do if values[i] < minimum then minimum = values[i] end end
    local branches = {}
    for i = 1, #ids do
        if values[i] == minimum then
            table.insert(branches, { id = ids[i], effects = {
                Effect.GainResource({ apiVersion = 2, recipient = ctx.playerId, resourceType = types[ids[i]], amount = 4,
                    qualification = { kind = 'minimum_balance', resourceTypes = { 'originium', 'originium_shard', 'iron' } },
                    reasonId = 'character.elysium.strategy' })
            } })
        end
    end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches,
        promptKey = 'character.elysium.strategy.choose_resource' }), { interaction = { prompt = Text.Ref('core:interaction.elysium.least_resource'), options = { { candidateId = 'originium', label = Text.Ref('core:interaction.resource.originium') }, { candidateId = 'originium-shard', label = Text.Ref('core:interaction.resource.originium_shard') }, { candidateId = 'iron', label = Text.Ref('core:interaction.resource.iron') } } } }) }
end
