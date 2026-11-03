return function(ctx)
    return { Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'player', count = 16, requiredCount = 0, distinctPolicy = 'subject', promptKey = 'effect.influence.choose' }), Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }),
        Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) }
end
