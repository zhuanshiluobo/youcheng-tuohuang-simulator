return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'iron', amount = 2 }) }
end
