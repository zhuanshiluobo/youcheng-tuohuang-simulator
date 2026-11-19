return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 3, reasonId = 'event_card.reward:event_yellow_09' }))
    return effects
end
