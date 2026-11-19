return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 10, reasonId = 'event_card.reward:event_yellow_04' }))
    return effects
end
