return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium', amount = 2 }) }, rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 15 }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.hyde_brothers.level_2.special') } }) }
end
