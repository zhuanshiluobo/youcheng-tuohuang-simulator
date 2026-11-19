return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.LoseScore({ player = ctx.playerId, amount = 1 }) },
        rightEffects = { Flow.UpgradeThenActivateEnterpriseSpecial({ apiVersion = 1, player = ctx.playerId, levels = 1 }) } }),
        { interaction = { prompt = Text.Ref('core:condition.enterprise.event.collection_platform.resolve') } }) }
end
