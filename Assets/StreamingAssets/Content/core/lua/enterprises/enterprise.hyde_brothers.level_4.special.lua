return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium', amount = 6 }) }, rightEffects = {
        Effect.RefreshFacilitySupply({ apiVersion = 1, player = ctx.playerId }),
        Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'supply', includeStoredFacilities = false, paymentMode = 'free', allowDecline = true }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.hyde_brothers.level_4.special') } }) }
end
