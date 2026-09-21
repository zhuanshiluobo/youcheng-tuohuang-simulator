return function(ctx)
    if ctx.eventType ~= 'EffectCompleted' then
        return { Effect.Choice({ player = ctx.playerId, options = { 'originium', 'originium-shard', 'iron' },
            promptKey = 'character.cannot.tactic.choose_resource' }) }
    end
    if ctx.payload.outcome ~= 'completed' then return {} end
    local selected = ctx.payload.normalizedResult
    local fields = { originium = 'originium', ['originium-shard'] = 'originiumShard', iron = 'iron' }
    local types = { originium = 'originium', ['originium-shard'] = 'originium_shard', iron = 'iron' }
    if fields[selected] == nil then error('cannot_requisition_resource_invalid') end
    local effects = {}
    for _, player in ipairs(GameData.GetPlayers()) do
        local amount = PlayerData.GetResources(player)[fields[selected]]
        while amount > 0 do
            local batch = amount
            if batch > 50 then batch = 50 end
            table.insert(effects, Effect.PayResource({ payer = player, resourceType = types[selected], amount = batch,
                reasonId = 'character.cannot.tactic' }))
            table.insert(effects, Effect.GainResource({ recipient = player, resourceType = 'gold_voucher', amount = batch * 2,
                reasonId = 'character.cannot.tactic' }))
            amount = amount - batch
        end
    end
    table.insert(effects, Effect.GainScore({ recipient = ctx.playerId, amount = 1, reasonId = 'character.cannot.tactic' }))
    return effects
end
