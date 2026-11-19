return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 3, reasonId = 'event_card.reward:event_yellow_07' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 1, reasonId = 'event_card.reward:event_yellow_07' }))
    return effects
end
