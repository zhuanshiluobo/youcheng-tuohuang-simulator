return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 7, reasonId = 'event_card.reward:event_red_01' }))
    return effects
end
