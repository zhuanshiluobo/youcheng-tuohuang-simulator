return function(ctx)
    return {Effect.ActivateEnterpriseSpecial({apiVersion=1,player=ctx.playerId,unlockedByPlayer=ctx.playerId,minLevel=1,maxLevel=2})}
end
