return function(ctx)
    local candidates = {}
    for _, influence in ipairs(Global.Map.FindInfluences()) do
        if influence.ownerPlayerId == ctx.playerId then
            table.insert(candidates, influence.influenceId)
        end
    end
    if #candidates == 0 then return {} end
    return { Effect.RemoveInfluence({
        executingPlayer = ctx.playerId,
        candidateScope = candidates,
        reasonId = 'character.liskarm.strategy.cleanup',
        causeKind = 'other_intrinsic_flow'
    }) }
end
