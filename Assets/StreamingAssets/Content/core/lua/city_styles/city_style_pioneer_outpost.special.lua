return function(ctx)
    local locationId = PlayerData.GetCityLocation(ctx.playerId)
    local point = Global.Map.GetLocation(locationId)
    local effects = {}
    if point ~= nil then
        for _ = 1, 2 do
            table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId,
                influenceSource = 'city_style.pioneer_outpost', targetLocationId = locationId }))
        end
    end
    for _ = 1, ctx.payload.declarationMarkerCount do
        table.insert(effects, Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', subjectFilter = 'self', count = 1 }))
    end
    return effects
end
