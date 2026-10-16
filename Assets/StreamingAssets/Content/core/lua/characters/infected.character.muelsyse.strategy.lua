return function(ctx)
    return {Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='player_supply'}),Effect.SelectInfluence({player=ctx.playerId, operation='move', subjectFilter='self', count=1})}
end
