return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.effectTypeId == 'effect.facility.choose_build' then
            return { Effect.ShuffleFacilityDeck({ player = ctx.playerId }) }
        end
        return {}
    end
    return { Effect.LoseScore({ player = ctx.playerId, amount = 1 }),
        Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'deck', allowDecline = true }) }
end
