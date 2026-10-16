return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'originium', amount = 1 }),
    Effect.GainResource({ recipient = ctx.playerId, resourceType = 'originium_shard', amount = 1 }),
    Effect.GainResource({ recipient = ctx.playerId, resourceType = 'iron', amount = 1 }),
    Effect.ActivateEnterpriseSpecial({ apiVersion = 1, player = player, unlockedByPlayer = player }) }
end
