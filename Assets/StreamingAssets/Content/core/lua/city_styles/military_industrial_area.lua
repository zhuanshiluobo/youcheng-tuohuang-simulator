return function(ctx)
    local p = ctx.payload
    local effects = {}
    local count = p.declarationMarkerCount
    if count > 3 then count = 3 end
    if count > p.influenceSupply then count = p.influenceSupply end
    for i = 1, count do
        table.insert(effects, Effect.PlaceInfluence({
            executingPlayer = ctx.playerId,
            influenceSource = "city_style.military." .. tostring(i),
            ownerSubject = ctx.playerId,
            causeKind = "other_intrinsic_flow"
        }))
    end
    return effects
end
