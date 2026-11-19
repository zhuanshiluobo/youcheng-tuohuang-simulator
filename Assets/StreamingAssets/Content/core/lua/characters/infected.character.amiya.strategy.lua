return function(ctx)
    return {Flow.DispatchOperator({apiVersion=2,player=ctx.playerId,deckZoneId = Global.Content.FindZones({localZoneId='operator_deck'})[1].id,excludedReturnItemIds={ctx.payload.cardInstanceRef}})}
end
