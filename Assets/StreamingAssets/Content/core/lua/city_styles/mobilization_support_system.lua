return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.outcome ~= 'completed' then return {} end
        return { Effect.ReplaceInfluence({ executingPlayer = ctx.playerId,
            targetInfluence = ctx.payload.normalizedResult,
            replacementSource = 'city_style.mobilization', replacementOwner = ctx.playerId,
            placementFailurePolicy = 'keep_removal' }) }
    end
    if #ctx.payload.candidateIds == 0 then return {} end
    return { Effect.Choice({ player = ctx.playerId, options = ctx.payload.candidateIds,
        promptKey = 'effect.influence.replace.target' }) }
end
