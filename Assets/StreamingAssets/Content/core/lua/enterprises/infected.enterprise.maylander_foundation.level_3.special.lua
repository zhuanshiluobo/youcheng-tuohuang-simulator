return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'iron', amount = 4 }) }, rightEffects = { Effect.GainScore({ recipient = ctx.playerId, amount = 2 }) } }), { interaction = { prompt = Text.Ref('core:condition.infected.enterprise.maylander_foundation.level_3.special') } }) }
end
