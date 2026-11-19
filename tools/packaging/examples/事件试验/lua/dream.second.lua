return function(ctx)
    return {
        Effect.GainResource({recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 4}),
        Effect.GainScore({recipient = ctx.playerId, amount = 1})
    }
end
