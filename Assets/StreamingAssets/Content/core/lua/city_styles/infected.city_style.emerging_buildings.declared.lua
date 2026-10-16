return function(ctx)
    return { Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'supply', includeStoredFacilities = false, excludeRainbow = true }) }
end
