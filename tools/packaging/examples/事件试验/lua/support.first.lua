return function(ctx)
    return {
        Effect.GainResource({recipient = ctx.playerId, resourceType = 'iron', amount = 3}),
        Effect.GainResource({recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 3})
    }
end
