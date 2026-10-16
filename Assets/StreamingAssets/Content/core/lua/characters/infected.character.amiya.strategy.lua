return function(ctx)
    return {Flow.DispatchOperator({apiVersion=1,player=ctx.playerId,drawCount=3,excludedReturnItemIds={ctx.payload.cardInstanceRef}})}
end
