return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 2, reasonId = 'event_card.reward:event_red_04' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 2, reasonId = 'event_card.reward:event_red_04' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 2, reasonId = 'event_card.reward:event_red_04' }))
    return effects
end
