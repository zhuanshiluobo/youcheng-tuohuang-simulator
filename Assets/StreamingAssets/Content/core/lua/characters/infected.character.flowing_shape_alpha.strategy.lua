return function(ctx)
    return {Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer=ctx.playerId, subjectFilter='self', count=1 }),
        Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer=ctx.playerId, subjectFilter='self', count=1 })}
end
