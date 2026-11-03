return function(ctx)
    return { Effect.Explore({apiVersion=3,targetMode='select',player=ctx.playerId,consumeMainAction=false}), Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'self', count = 1, requiredCount = 0, promptKey = 'effect.influence.choose' }) }
end
