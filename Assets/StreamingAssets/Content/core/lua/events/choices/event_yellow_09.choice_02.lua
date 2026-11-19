return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 2, reasonId = 'event_card.reward:event_yellow_09' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 5, reasonId = 'event_card.reward:event_yellow_09' }))
    return effects
end
