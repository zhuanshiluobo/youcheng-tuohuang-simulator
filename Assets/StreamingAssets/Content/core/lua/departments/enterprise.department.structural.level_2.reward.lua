return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'originium_shard', amount = 3 }) }
end
