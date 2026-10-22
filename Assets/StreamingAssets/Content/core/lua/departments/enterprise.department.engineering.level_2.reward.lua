return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'originium', amount = 3 }) }
end
