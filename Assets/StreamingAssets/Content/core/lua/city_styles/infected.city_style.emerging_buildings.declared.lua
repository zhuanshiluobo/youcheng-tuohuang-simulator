return function(ctx)
    return { Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'supply', allowDecline = true, includeStoredFacilities = false, excludeRainbow = true , paymentMode='auto' , consumeMainAction=false }) }
end
