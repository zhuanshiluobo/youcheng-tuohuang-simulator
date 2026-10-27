return function(ctx)
    return { Effect.PayResource({ player = ctx.playerId, cost = { OriginiumShard = 3 } }) }
end
