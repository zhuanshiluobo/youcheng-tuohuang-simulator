return function(ctx)
    return {Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='player_supply'}),Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer=ctx.playerId, subjectFilter='self', count=1 })}
end
