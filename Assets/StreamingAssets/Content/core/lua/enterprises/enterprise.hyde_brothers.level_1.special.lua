return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'originium', amount = 4 }) }
end
