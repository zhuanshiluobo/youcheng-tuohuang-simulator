return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'pure_originium', amount = 1 }) }, rightEffects = {
        Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'supply', includeStoredFacilities = false, paymentMode = 'free', maxGoldVoucherCost = 23 }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.engineering.level_5.reward') } }) }
end
