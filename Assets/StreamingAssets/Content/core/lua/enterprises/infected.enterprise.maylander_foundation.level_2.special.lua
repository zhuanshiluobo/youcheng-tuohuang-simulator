return function(ctx)
    return { Effect.ChooseExplore({ player = ctx.playerId }), Effect.SelectInfluence({ player = ctx.playerId, operation = 'remove', subjectFilter = 'self', count = 1, requiredCount = 0, promptKey = 'effect.influence.choose' }) }
end
