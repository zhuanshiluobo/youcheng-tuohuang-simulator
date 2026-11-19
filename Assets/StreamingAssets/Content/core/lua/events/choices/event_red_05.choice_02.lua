return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 3, reasonId = 'event_card.reward:event_red_05' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 2, reasonId = 'event_card.reward:event_red_05' }))
    return effects
end
