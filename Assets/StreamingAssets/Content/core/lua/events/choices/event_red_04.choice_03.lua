return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'PureOriginium', amount = 1, reasonId = 'event_card.reward:event_red_04' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 4, reasonId = 'event_card.reward:event_red_04' }))
    return effects
end
