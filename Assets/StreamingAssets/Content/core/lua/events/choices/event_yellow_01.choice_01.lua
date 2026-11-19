return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 5, reasonId = 'event_card.reward:event_yellow_01' }))
    return effects
end
