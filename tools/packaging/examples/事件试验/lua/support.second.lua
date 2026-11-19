return function(ctx)
    return {
        Effect.GainResource({recipient = ctx.playerId, resourceType = 'originium', amount = 3}),
        Effect.GainResource({recipient = ctx.playerId, resourceType = 'iron', amount = 1})
    }
end
