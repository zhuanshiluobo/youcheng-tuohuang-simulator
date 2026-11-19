return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 2, reasonId = 'event_card.reward:event_yellow_08' }))
    table.insert(effects, Effect.GainScore({ recipient = ctx.playerId, amount = 1, reasonId = 'event_card.score:event_yellow_08' }))
    return effects
end
