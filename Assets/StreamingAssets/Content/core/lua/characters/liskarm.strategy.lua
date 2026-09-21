return function(ctx)
    local p = ctx.payload
    local effects = {}
    local count = 2
    for i = 1, count do
        table.insert(effects, Effect.PlaceInfluence({
            executingPlayer = ctx.playerId,
            influenceSource = 'character.liskarm.strategy.' .. tostring(i),
            ownerSubject = ctx.playerId,
            causeKind = 'other_intrinsic_flow'
        }))
    end
    return effects
end
