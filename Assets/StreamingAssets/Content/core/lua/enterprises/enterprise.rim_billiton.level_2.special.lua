return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 4 }) }, rightEffects = {
        Effect.SelectInfluence({ player = ctx.playerId, operation = 'replace', subjectFilter = 'any', count = 1, requiredCount = 0, candidateScope = 'resource_points' }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.rim_billiton.level_2.special') } }) }
end
