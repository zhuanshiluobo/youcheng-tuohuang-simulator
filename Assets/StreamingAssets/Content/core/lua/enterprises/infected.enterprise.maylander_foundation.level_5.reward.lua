return function(ctx)
    return { Effect.GainScore({ recipient = ctx.playerId, amount = 2 }) }
end
