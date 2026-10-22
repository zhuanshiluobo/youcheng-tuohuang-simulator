return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'originium_shard', amount = 3 }) }, rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'pure_originium', amount = 1 }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.department.structural.level_4.reward') } }) }
end
