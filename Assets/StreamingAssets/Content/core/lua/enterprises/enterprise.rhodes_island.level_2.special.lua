return function(ctx)
    local map = Global.Map.GetMap()
    local slots, routes = {}, {}
    for _, route in ipairs(map.routes) do
        for _, slot in ipairs(route.influenceSlotIds) do slots[#slots + 1] = slot; routes[slot] = true end
    end
    if ctx.eventType == 'EffectCompleted' then
        local amount = 0
        for _, item in ipairs(map.influences) do
            if item.ownerPlayerId == ctx.playerId and routes[item.slotId] then amount = amount + 1 end
        end
        if amount == 0 then return {} end
        return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = amount }) }
    end
    return { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, candidateScope = slots, causeKind = 'other_intrinsic_flow' }) }
end
