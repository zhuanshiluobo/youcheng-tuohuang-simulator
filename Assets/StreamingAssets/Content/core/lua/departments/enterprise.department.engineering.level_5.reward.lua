return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'pure_originium', amount = 1 }) }, rightEffects = {
        Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'supply', includeStoredFacilities = false, paymentMode = 'free', maxGoldVoucherCost = 23 , consumeMainAction=false , allowDecline=false }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.engineering.level_5.reward') } }) }
end
