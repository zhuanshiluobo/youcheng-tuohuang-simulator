return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 1, reasonId = 'event_card.reward:event_red_06' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'PureOriginium', amount = 1, reasonId = 'event_card.reward:event_red_06' }))
    return effects
end
