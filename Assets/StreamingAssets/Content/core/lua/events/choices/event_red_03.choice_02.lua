return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 4, reasonId = 'event_card.reward:event_red_03' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 5, reasonId = 'event_card.reward:event_red_03' }))
    return effects
end
