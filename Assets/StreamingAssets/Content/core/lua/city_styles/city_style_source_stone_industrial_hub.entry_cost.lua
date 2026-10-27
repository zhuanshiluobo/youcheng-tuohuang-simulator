return function(ctx)
    return { Effect.PayResource({ player = ctx.playerId, cost = { GoldVoucher = 6 } }) }
end
