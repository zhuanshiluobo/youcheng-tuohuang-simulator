return function(ctx)
    return { Effect.LoadCardMarkers({ apiVersion = 1, player = ctx.playerId, cardInstanceId = ctx.payload.cardInstanceRef, localZoneId = 'markers', maxCount = 5 }) }
end
