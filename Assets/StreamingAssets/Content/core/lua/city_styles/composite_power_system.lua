return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        local slots = ctx.payload.normalizedResult.traversedRouteSlots
        if #slots == 0 then return {} end
        return { Effect.PlaceInfluence({ executingPlayer = ctx.playerId,
            influenceSource = 'city_style.composite.route', ownerSubject = ctx.playerId,
            causeKind = 'place_road', candidateScope = slots }) }
    end
    return { Effect.MoveCity({ player = ctx.playerId, waiveBaseCost = true, consumeMainAction = false }) }
end
