return function(ctx)
    return { Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'self', count = 2, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' }) }
end
