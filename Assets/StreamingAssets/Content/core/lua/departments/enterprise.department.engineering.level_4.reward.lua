return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium', amount = 3 }) }, rightEffects = { Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'supply', paymentMode = 'auto', allowDecline = true }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.engineering.level_4.reward') } }) }
end
