return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 1, reasonId = 'event_card.reward:event_red_02' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'PureOriginium', amount = 1, reasonId = 'event_card.reward:event_red_02' }))
    return effects
end
