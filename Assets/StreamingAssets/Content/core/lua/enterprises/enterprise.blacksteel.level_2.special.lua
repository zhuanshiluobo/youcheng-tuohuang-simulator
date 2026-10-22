return function(ctx)
    return { Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', subjectFilter = 'self', count = 2, requiredCount = 0, distinctPolicy = 'instance', promptKey = 'effect.influence.choose' }) }
end
