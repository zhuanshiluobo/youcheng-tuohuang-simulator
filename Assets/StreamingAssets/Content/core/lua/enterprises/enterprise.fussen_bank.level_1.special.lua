return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 8 }) }
end
