return function(ctx)
    return { Effect.SellResource({ player = ctx.playerId, interactionType = 'lua.choice', promptKey = 'effect.resource.sell' }) }
end
