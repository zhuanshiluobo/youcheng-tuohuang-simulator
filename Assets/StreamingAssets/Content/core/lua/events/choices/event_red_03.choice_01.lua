return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 4, reasonId = 'event_card.reward:event_red_03' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 3, reasonId = 'event_card.reward:event_red_03' }))
    return effects
end
