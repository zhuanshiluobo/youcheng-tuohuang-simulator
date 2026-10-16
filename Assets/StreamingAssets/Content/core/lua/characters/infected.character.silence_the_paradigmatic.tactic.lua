return function(ctx)
    return {Effect.GainScore({player=ctx.playerId,amount=1}),Effect.CreateCityInfluenceLock({apiVersion=1,player=ctx.playerId,definitionId='infected.component.influence_lock_token'})}
end
