return function(ctx)
    return {Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='player_supply'})}
end
