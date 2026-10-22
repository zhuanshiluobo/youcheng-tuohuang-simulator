return function(ctx)
    return { Effect.MoveCity({ player = ctx.playerId, waiveBaseCost = true, consumeMainAction = false, clearInfluencePolicy = 'move' }) }
end
