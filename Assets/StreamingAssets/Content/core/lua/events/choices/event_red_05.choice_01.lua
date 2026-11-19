return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 1, reasonId = 'event_card.reward:event_red_05' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'PureOriginium', amount = 1, reasonId = 'event_card.reward:event_red_05' }))
    return effects
end
