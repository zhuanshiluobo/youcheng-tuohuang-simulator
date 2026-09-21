return function(ctx)
    local p = ctx.payload
    local effects = {}
    for i = 1, p.requiredTargetCount do
        table.insert(effects, Effect.PlaceInfluence({
            executingPlayer = ctx.playerId,
            influenceSource = "city_style.military." .. tostring(i),
            ownerSubject = ctx.playerId,
            causeKind = "other_intrinsic_flow"
        }))
    end
    return effects
end
