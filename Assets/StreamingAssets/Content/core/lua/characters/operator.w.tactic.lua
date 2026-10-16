return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 4 }) }, rightEffects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'replace', subjectFilter = 'opponent', count = 2, requiredCount = 0, distinctPolicy = 'subject', promptKey = 'effect.influence.choose' }) } }), { interaction = { prompt = Text.Ref('core:condition.operator.w.tactic') } }) }
end
