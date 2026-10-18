return function(ctx)
    return { Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'supply', allowDecline = true, includeStoredFacilities = false, excludeRainbow = true }) }
end
