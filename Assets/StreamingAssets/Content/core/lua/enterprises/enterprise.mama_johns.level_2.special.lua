return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.LoseScore({ player = ctx.playerId, amount = 1 }) }, rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 20 }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.mama_johns.level_2.special') } }) }
end
