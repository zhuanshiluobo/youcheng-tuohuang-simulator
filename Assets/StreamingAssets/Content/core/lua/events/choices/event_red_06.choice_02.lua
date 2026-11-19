return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 3, reasonId = 'event_card.reward:event_red_06' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 3, reasonId = 'event_card.reward:event_red_06' }))
    return effects
end
