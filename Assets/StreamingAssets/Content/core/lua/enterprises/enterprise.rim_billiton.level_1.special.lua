return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'originium', amount = 1 }),
    Effect.GainResource({ recipient = ctx.playerId, resourceType = 'originium_shard', amount = 1 }),
    Effect.GainResource({ recipient = ctx.playerId, resourceType = 'iron', amount = 1 }) }
end
