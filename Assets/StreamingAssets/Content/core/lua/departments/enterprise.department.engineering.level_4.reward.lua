return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium', amount = 3 }) }, rightEffects = { Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'supply', paymentMode = 'auto', allowDecline = true , consumeMainAction=false }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.engineering.level_4.reward') } }) }
end
