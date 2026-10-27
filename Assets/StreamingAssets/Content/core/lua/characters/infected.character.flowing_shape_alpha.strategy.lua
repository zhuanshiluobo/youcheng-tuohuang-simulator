return function(ctx)
    return {Effect.SelectInfluence({player=ctx.playerId, operation='move', subjectFilter='self', count=1}),
        Effect.SelectInfluence({player=ctx.playerId, operation='move', subjectFilter='self', count=1})}
end
