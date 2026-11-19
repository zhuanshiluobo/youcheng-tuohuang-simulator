return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'Originium', amount = 1, reasonId = 'event_card.reward:event_yellow_05' }))
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 3, reasonId = 'event_card.reward:event_yellow_05' }))
    return effects
end
