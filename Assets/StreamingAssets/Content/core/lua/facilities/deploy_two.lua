return function(ctx)
    local effects = {}
    for i = 1, 2 do
        table.insert(effects, Effect.PlaceInfluence({
            executingPlayer = ctx.playerId,
            influenceSource = 'facility.escort_dispatch_center.' .. tostring(i),
            ownerSubject = ctx.playerId,
            causeKind = 'other_intrinsic_flow'
        }))
    end
    return effects
end
