return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.LoseScore({ player = ctx.playerId, amount = 1 }) }, rightEffects = { Effect.Collect({ apiVersion = 1, player = ctx.playerId, scope = 'extra' }) } }), { interaction = { prompt = Text.Ref('core:condition.enterprise.rim_billiton.level_4.special') } }) }
end
