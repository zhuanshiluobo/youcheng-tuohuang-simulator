return function(ctx)
    local point = Global.Map.GetResourcePoint(PlayerData.GetCityLocation(ctx.playerId))
    local effects = {}
    if point ~= nil then
        table.insert(effects, Effect.Repeat({ apiVersion = 1, count = 2, effects = {
            Effect.PlaceInfluence({ executingPlayer = ctx.playerId, ownerSubject = ctx.playerId,
                influenceSource = 'city_style.pioneer_outpost', candidateScope = point.influenceSlotIds }) } }))
    end
    table.insert(effects, Effect.Repeat({ apiVersion = 1, count = ctx.payload.declarationMarkerCount, effects = {
        Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', subjectFilter = 'self', count = 1 }) } }))
    return effects
end
