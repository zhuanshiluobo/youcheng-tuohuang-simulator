return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 7 }) }
end
