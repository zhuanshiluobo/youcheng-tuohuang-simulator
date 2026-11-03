return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'self', count = 2, requiredCount = 2, requireCompleteSelection = true }) },
        rightEffects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }),
        Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }),
        Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } }), { interaction = { prompt = Text.Ref('core:condition.operator.dorothy.strategy') } }) }
end
