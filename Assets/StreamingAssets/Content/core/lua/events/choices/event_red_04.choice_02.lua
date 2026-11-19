return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 18, reasonId = 'event_card.reward:event_red_04' }))
    return effects
end
