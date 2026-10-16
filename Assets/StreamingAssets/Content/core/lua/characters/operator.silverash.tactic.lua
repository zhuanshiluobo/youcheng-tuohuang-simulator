return function(ctx)
    return { Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'player', count = 16, requiredCount = 0, distinctPolicy = 'subject', promptKey = 'effect.influence.choose' }), Effect.Repeat({ apiVersion = 1, count = 2, effects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = 'player_supply', ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) } }) }
end
