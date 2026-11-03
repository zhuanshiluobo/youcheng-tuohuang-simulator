return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium_shard', amount = 2 }) }, rightEffects = { Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'supply', paymentMode = 'auto', allowDecline = true , consumeMainAction=false }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.raythean.level_3.special') } }) }
end
