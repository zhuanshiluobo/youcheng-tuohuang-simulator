return function(ctx)
    return { Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'reserve', effectId = 'reserve_extension_hub' , paymentMode='free' , consumeMainAction=false , allowDecline=true }) }
end
