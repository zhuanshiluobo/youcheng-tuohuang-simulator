return function(ctx)
    return { Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'supply', allowDecline = true , paymentMode='auto' , consumeMainAction=false }) }
end
