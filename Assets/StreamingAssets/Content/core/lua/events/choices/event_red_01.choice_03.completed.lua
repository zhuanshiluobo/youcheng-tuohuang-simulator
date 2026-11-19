return function(ctx)
    if ctx.payload.outcome ~= 'completed' then return {} end
    local target = ctx.payload.normalizedResult.value
    local effects = {}
    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = 'GoldVoucher', amount = 6, reasonId = 'event_card.reward:event_red_01' }))
    table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'event_card_pending:event_red_01', ownerSubject = ctx.playerId, targetSlotId = target, causeKind = 'other_intrinsic_flow' }))
    return effects
end
