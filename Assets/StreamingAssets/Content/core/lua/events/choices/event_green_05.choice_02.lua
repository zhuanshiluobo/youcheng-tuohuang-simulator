return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 7, reasonId = 'event_card.reward:event_green_05' }))
    return effects
end
