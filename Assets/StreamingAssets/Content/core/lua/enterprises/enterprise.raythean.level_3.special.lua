return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium_shard', amount = 2 }) }, rightEffects = { Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'supply', paymentMode = 'auto', allowDecline = true }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.raythean.level_3.special') } }) }
end
