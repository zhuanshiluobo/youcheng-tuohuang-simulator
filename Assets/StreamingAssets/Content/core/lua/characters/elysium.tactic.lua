return function(ctx)
    return { Effect.Condition({
        leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium_shard', amount = 3 }) },
        rightEffects = { Effect.MoveCity({ player = ctx.playerId, waiveBaseCost = true,
            consumeMainAction = false, targetPolicy = 'explored_own_influence' }) }
    }) }
end
