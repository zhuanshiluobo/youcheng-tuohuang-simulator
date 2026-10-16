return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 25 }) }, rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'pure_originium', amount = 3 }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.defense.level_5.reward') } }) }
end
