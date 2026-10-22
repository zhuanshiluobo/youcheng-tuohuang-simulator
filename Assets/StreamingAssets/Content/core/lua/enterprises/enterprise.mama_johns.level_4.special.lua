return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 25 }) }, rightEffects = { Effect.GainScore({ recipient = ctx.playerId, amount = 3 }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.mama_johns.level_4.special') } }) }
end
