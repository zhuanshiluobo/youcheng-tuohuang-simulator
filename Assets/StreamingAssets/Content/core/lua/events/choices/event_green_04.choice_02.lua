return function(ctx)
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'OriginiumShard', amount = 3, reasonId = 'event_card.reward:event_green_04' }))
    return effects
end
