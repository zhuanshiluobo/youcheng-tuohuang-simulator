return function(ctx)
    if ctx.payload.outcome ~= 'completed' then return {} end
    local target = ctx.payload.normalizedResult.value
    local effects = {}
    table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'event_card_pending:event_yellow_05', ownerSubject = ctx.playerId, targetSlotId = target, causeKind = 'other_intrinsic_flow' }))
    return effects
end
