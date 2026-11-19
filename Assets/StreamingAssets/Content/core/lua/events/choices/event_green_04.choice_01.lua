return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 4, reasonId = 'event_card.reward:event_green_04' }))
    return effects
end
