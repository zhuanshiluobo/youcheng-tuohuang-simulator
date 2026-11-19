return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 2, reasonId = 'event_card.reward:event_green_06' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 2, reasonId = 'event_card.reward:event_green_06' }))
    return effects
end
