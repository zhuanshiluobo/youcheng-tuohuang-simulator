return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.effectTypeId == 'effect.city.move' and ctx.payload.outcome == 'completed' and (ctx.payload.normalizedResult.removedOpponentCount or 0) > 0 then
            return {Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='player_supply'})}
        end
        return {}
    end
    return {Effect.MoveCity({player=ctx.playerId,targetPolicy='explored',waiveBaseCost=true,clearInfluencePolicy='remove',consumeMainAction=false})}
end
