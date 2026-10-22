return function(ctx)
    return {Effect.Repeat({apiVersion=1,count=2,allowEarlyStop=false,effects={Effect.SelectInfluence({player=ctx.playerId, operation='move', subjectFilter='self', count=1})}})}
end
