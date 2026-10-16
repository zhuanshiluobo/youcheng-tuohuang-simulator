return function(ctx)
    return { Effect.GainScore({ recipient = ctx.playerId, amount = 3 }) }
end
